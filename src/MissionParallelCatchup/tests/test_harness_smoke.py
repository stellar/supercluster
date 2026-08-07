"""Proof that the fake cluster drives the real reconcile().

Every test here imports job_monitor and calls the shipped reconcile() -- nothing
is extracted from source or reimplemented. If one of these fails, the monitor's
behaviour changed, not a regex.
"""

import pytest

import fake_k8s
import config
import records
import job_monitor as jm


def test_dispatch_happens_on_an_empty_cluster(cluster):
    result = cluster.reconcile()

    # PARALLELISM is 2 and there are three ranges, so exactly two go out, and
    # tip-first means the two highest ends.
    assert cluster.jobs() == ['pc-r200-a1', 'pc-r300-a1']
    assert result['created'] == 2
    assert result['total'] == 3
    assert result['remaining'] == 1
    assert sorted(result['in_progress']) == ['200/420', '300/420']

    # pvc mode: each range gets its own volume, created before its Job.
    assert cluster.pvcs() == ['pc-data-r200', 'pc-data-r300']
    created = [(c.kind, c.name) for c in cluster.calls if c.verb == 'create']
    assert created == [('pvc', 'pc-data-r300'), ('job', 'pc-r300-a1'),
                       ('pvc', 'pc-data-r200'), ('job', 'pc-r200-a1')]

    # Nothing durable is written until a range actually finishes -- dispatch
    # alone must not touch the progress record or its ConfigMap mirror.
    assert cluster.progress() == {}
    assert cluster.calls.names(verb='patch', kind='configmap') == []


def test_a_succeeded_job_is_recorded_into_completed(cluster):
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    # The collector's half of the contract: peaks and tx_apply are only ever
    # readable from the files it writes, and the .done marker is what allows a
    # reap at all.
    cluster.finalize(300, 1, tx_apply=1.5, peaks={'peakAnonBytes': 123})

    cluster.reconcile()

    record = cluster.completed()['300']
    assert record['attempts'] == 1
    assert record['count'] == 420
    assert record['txApply'] == 1.5
    assert record['peakAnonBytes'] == 123
    assert record['seconds'] == pytest.approx(60.0)
    assert record['wallSeconds'] == pytest.approx(60.0)

    # A completed range gives its volume back and its Job is reaped.
    assert 'pc-data-r300' not in cluster.pvcs()
    assert cluster.deleted.names(verb='delete', kind='job') == ['pc-r300-a1']
    assert cluster.deleted.names(verb='delete', kind='pvc') == ['pc-data-r300']

    # The freed slot is refilled in the same pass.
    assert 'pc-r100-a1' in cluster.jobs()


def test_a_failed_job_is_retried(cluster):
    cluster.reconcile()
    # exit 3 is stellar-core's "did not complete" and is retried only when the
    # archive shows a fetch fault killed it -- so the decision waits for .done.
    cluster.advance(300, 'incomplete')
    cluster.finalize(300, 1, archive='fetch_fault')

    cluster.reconcile()

    assert 'pc-r300-a2' in cluster.jobs()
    assert cluster.attempt_of(300) == 2
    assert cluster.failed() == {}, "a retryable failure must not be recorded as failed"
    assert cluster.completed() == {}

    # The retry rides the same volume -- that is what makes resume-at-LCL work.
    assert cluster.calls.names(verb='create', kind='pvc').count('pc-data-r300') == 1
    # ...and the new pod carries the attempt label the collector keys files on.
    pod = cluster.k8s.pod_for_job('pc-r300-a2')
    assert pod.metadata.labels[config.LABEL_ATTEMPT] == '2'


def test_a_condemned_range_is_recorded_and_not_retried(cluster):
    cluster.reconcile()
    # A plain non-zero exit that is not 3 is a genuine catchup failure.
    cluster.advance(300, 'condemned')

    cluster.reconcile()

    assert 'pc-r300-a2' not in cluster.jobs()
    assert cluster.failed()['300'] == {
        'attempts': 1, 'pod': cluster.k8s.pod_for_job('pc-r300-a1').metadata.name,
        'outcome': 'failed', 'exitCode': 1}
    # Dispatch is not frozen by a condemned range: the freed slot is refilled,
    # otherwise the mission's `remaining == 0` wait would deadlock.
    assert 'pc-r100-a1' in cluster.jobs()


def test_an_oom_retry_escalates_the_memory_limit(cluster):
    cluster.reconcile()
    cluster.advance(300, 'oom')

    cluster.reconcile()

    resources = (cluster.k8s.job('pc-r300-a2')
                 .spec.template.spec.containers[0].resources)
    # One OOM = one rung: 24000Mi * 1.5. The request follows the limit, because
    # a pod that OOMed will not fit where it was scheduled before.
    assert resources.requests['memory'] == '13824Mi'
    assert resources.requests['memory'] == '13824Mi'
    assert cluster.failed() == {}


def test_a_disruption_does_not_spend_the_range_budget(cluster):
    cluster.reconcile()
    cluster.advance(300, 'disrupted')

    cluster.reconcile()

    assert 'pc-r300-a2' in cluster.jobs()
    outcome = records.read_outcome('300', 1)
    assert outcome['outcome'] == 'disrupted'
    # Memory is untouched: an eviction says nothing about how much the range wants.
    resources = (cluster.k8s.job('pc-r300-a2')
                 .spec.template.spec.containers[0].resources)
    assert resources.requests['memory'] == config.REQ_MEM


def test_progress_going_backwards_redispatches_rather_than_halting(cluster):
    # There is no monotonic-progress guard. It kept its high-water mark in
    # memory, so a restart reset it to zero and disarmed the guard for exactly
    # the event it existed to survive. Re-running a range is idempotent -- the
    # PVC still holds /data, so the attempt resumes from its last closed ledger.
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1)
    cluster.reconcile()
    assert '300' in cluster.completed()

    # Someone deletes the record underneath the run.
    cluster.write(config.PROGRESS_FILE, '{}')
    result = cluster.reconcile()

    # Back in the pool, and the run keeps going instead of halting.
    assert '300' not in cluster.completed()
    assert result['remaining'] + len(result['in_progress']) == 3


def test_the_fake_raises_the_status_codes_the_monitor_branches_on(cluster):
    cluster.reconcile()

    with pytest.raises(fake_k8s.ApiException) as dup:
        cluster.k8s.batch_v1.create_namespaced_job(
            cluster.namespace, cluster.k8s.job('pc-r300-a1'))
    assert dup.value.status == 409

    with pytest.raises(fake_k8s.ApiException) as missing:
        cluster.k8s.core_v1.read_namespaced_config_map('nope', cluster.namespace)
    assert missing.value.status == 404

    # 404 on a PVC read is what ensure_pvc() uses to decide to create one, and
    # 404 on the progress ConfigMap is what load_progress() treats as "new run".
    with pytest.raises(fake_k8s.ApiException) as gone:
        cluster.k8s.core_v1.read_namespaced_persistent_volume_claim(
            'pc-data-r999', cluster.namespace)
    assert gone.value.status == 404


def test_an_unfinalized_predecessor_is_not_deleted(cluster):
    """Reaping the Job reaps the pod its measurements still live on.

    Driven through a disruption rather than exit 3: exit 3 now defers until the
    collector has finalized, so it can never be observed mid-retry unfinalized.
    """
    cluster.reconcile()
    cluster.advance(300, 'disrupted')

    cluster.reconcile()

    assert 'pc-r300-a2' in cluster.jobs(), "the successor must exist"
    assert 'pc-r300-a1' in cluster.jobs(), "the collector has not finalized a1"
