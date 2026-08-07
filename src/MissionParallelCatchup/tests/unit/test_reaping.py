"""Deleting finished Jobs and released volumes.

reconcile() LISTs every Job and Pod each pass, so a finished Job is not free:
it inflates two LIST calls for as long as it lingers. At 2048-4096 parallelism
with a real OOM or spot-eviction rate that is hundreds of dead objects per hour
of run, and the apiserver pressure shows up as truncated list responses long
before anything else complains.

Everything here is best-effort by design: a cleanup failure costs disk or etcd,
never correctness, and raising would abort a reconcile pass mid-run and strand
every other range in the same iteration.
"""

import pytest
from kubernetes import client

import fake_k8s
import config
import kube
import records
import job_monitor as jm


NAMESPACE = 'catchup-test'
RUN = 'pc'


@pytest.fixture
def k8s(logdir, monkeypatch):
    """A fake cluster wired into the monitor, with no reconcile in the way."""
    fake = fake_k8s.FakeCluster(namespace=NAMESPACE)
    monkeypatch.setattr(kube, 'core_v1', fake.core_v1)
    monkeypatch.setattr(kube, 'batch_v1', fake.batch_v1)
    monkeypatch.setattr(config, 'NAMESPACE', NAMESPACE)
    monkeypatch.setattr(config, 'RUN_NAME', RUN)
    monkeypatch.setattr(config, 'STORAGE_MODE', 'pvc')

    def add_job(end, attempt):
        name = jm.job_name(end, attempt)
        labels = {config.LABEL_RUN: RUN, config.LABEL_RANGE: str(end),
                  config.LABEL_ATTEMPT: str(attempt)}
        fake.batch_v1.create_namespaced_job(NAMESPACE, client.V1Job(
            metadata=client.V1ObjectMeta(name=name, labels=labels),
            spec=client.V1JobSpec(
                template=client.V1PodTemplateSpec(
                    metadata=client.V1ObjectMeta(labels=labels),
                    spec=client.V1PodSpec(containers=[], restart_policy='Never')))))
        return name

    fake.add_job = add_job
    return fake


class Boom:
    """A batch API that fails every delete with one status."""

    def __init__(self, status):
        self.status = status
        self.calls = 0

    def delete_namespaced_job(self, name, namespace, **_):
        self.calls += 1
        raise fake_k8s.api_exception(self.status, 'boom')

    def list_namespaced_job(self, namespace, **_):
        raise fake_k8s.api_exception(self.status, 'boom')


# --- deleting one attempt's Job ----------------------------------------------

def test_delete_job_reaps_the_pod_too(k8s):
    # Background propagation is what actually removes the pod. Orphan would
    # leave the pod behind, and the pod is what reconcile lists.
    name = k8s.add_job(30957951, 2)
    assert k8s.pod_for_job(name) is not None
    jm.delete_job(30957951, 2)
    assert k8s.job_names() == []
    assert k8s.pod_for_job(name) is None, "the pod outlived its Job"


@pytest.mark.parametrize('status', [404, 403, 500])
def test_delete_job_is_best_effort(monkeypatch, k8s, status):
    # A 404 is the normal race with the TTL controller, not an error. Any other
    # status must be swallowed too: losing a Job to a leaked object is a
    # disk/etcd cost, but raising here would abort the whole reconcile pass.
    boom = Boom(status)
    monkeypatch.setattr(kube, 'batch_v1', boom)
    jm.delete_job(1, 1)          # must not raise
    assert boom.calls == 1


# --- deleting every Job a completed range has --------------------------------

def test_a_completed_range_reaps_every_attempt_not_just_the_winner(k8s):
    # Completion is terminal for the RANGE. An attempt-scoped reap leaves an
    # older Failed Job standing -- typically one lost to node disruption whose
    # collector died with the node, so it was never finalized and was
    # deliberately not deleted. Once the winner's Job is gone that leftover is
    # the range's highest live attempt, and the next pass feeds it into the
    # retry decision and re-runs an already-recorded range.
    k8s.add_job(300, 1)
    k8s.add_job(300, 2)
    other = k8s.add_job(400, 1)
    jm.reap_range_jobs(300)
    assert k8s.job_names() == [other], "the reap is not scoped to the range"


def test_a_list_failure_leaves_the_jobs_to_the_ttl_rather_than_raising(monkeypatch, k8s):
    monkeypatch.setattr(kube, 'batch_v1', Boom(500))
    jm.reap_range_jobs(300)      # must not raise


# --- the gate in front of both -----------------------------------------------

def test_the_reap_waits_for_the_collectors_done_marker(k8s):
    # Not inferred from peaks or tx_apply: tx_apply falls back to the archive so
    # it lands long before the collector finishes, and an attempt can finalize
    # with no peaks at all. Only the collector knows it is done, and deleting
    # the Job reaps the pod -- the last place peaks could still be read from.
    k8s.add_job(300, 1)
    assert jm._attempt_finalized(300, 1) is False, \
        "no marker yet, so reconcile must not reap"
    open(records.done_path(300, 1), 'w').close()
    assert jm._attempt_finalized(300, 1) is True
    jm.reap_range_jobs(300)
    assert k8s.job_names() == []


def test_the_done_marker_is_the_only_thing_that_counts_as_finalized(logdir):
    assert jm._attempt_finalized(300, 1) is False
    open(records.metrics_path(300, 1), 'w').close()
    assert jm._attempt_finalized(300, 1) is False, "metrics are not a promise"
    open(records.done_path(300, 1), 'w').close()
    assert jm._attempt_finalized(300, 1) is True


# --- releasing the volume ----------------------------------------------------

def test_a_completed_range_releases_its_volume(k8s):
    # PVCs are owner-referenced to the release, so nothing reclaimed them until
    # helm uninstall. Measured on ssc-test: 2032 bound PVCs / 79 TiB a third of
    # the way through a 3982-range run, heading for ~156 TiB and 3982 volumes
    # against the account's volume ceiling.
    name = jm.ensure_pvc(300, owner=None)
    assert k8s.pvc_names() == [name]
    jm.release_pvc(300)
    assert k8s.pvc_names() == []


def test_ephemeral_mode_has_no_volume_to_release(monkeypatch, k8s):
    jm.ensure_pvc(300, owner=None)
    monkeypatch.setattr(config, 'STORAGE_MODE', 'ephemeral')
    jm.release_pvc(300)
    assert k8s.pvc_names() != [], "ephemeral mode deleted a volume it does not own"


def test_releasing_a_volume_never_fails_a_completed_range(k8s):
    # Already-gone is the common case (a restart re-running the same tail), and
    # a disk cleanup failure must not condemn a finished range either way.
    jm.release_pvc(300)          # nothing there: 404, must not raise
    name = jm.ensure_pvc(300, owner=None)
    k8s.fail_next['delete pvc'] = fake_k8s.api_exception(403, 'Forbidden')
    jm.release_pvc(300)          # must not raise
    assert k8s.pvc_names() == [name], "the 403 was never actually injected"
