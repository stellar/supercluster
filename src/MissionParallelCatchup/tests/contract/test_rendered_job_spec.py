"""The worker Job the monitor renders, against the controllers that read it.

Three readers on the other side of this boundary, none of them ours:

  the Job controller   evaluates podFailurePolicy rules first-match-wins and
                       reports the winner as "matching FailJob rule at index N".
                       That INDEX is the whole verdict -- see
                       test_k8s_failure_formats.py -- so the order the rules are
                       rendered in is a contract with the message we later decode.
  the kubelet          honours restartPolicy and terminationGracePeriodSeconds.
  the log collector    reads the attempt off the POD's labels, not the Job's.

Everything here is asserted against a real build_job() object rather than the
source text, so a rewrite that keeps the rendered Job identical is free to
happen.
"""

from types import SimpleNamespace as NS

import pytest

import job_monitor as jm
import log_collector as lc

import _artifacts as art


@pytest.fixture
def job(monkeypatch):
    """One rendered worker Job, in the mode that needs no cluster."""
    monkeypatch.setattr(jm, 'STORAGE_MODE', 'ephemeral')
    monkeypatch.setattr(jm, 'RUN_NAME', 'pc')
    monkeypatch.setattr(jm, 'CORE_IMAGE', 'stellar/stellar-core:test')
    monkeypatch.setattr(jm, 'PROFILE', None)
    return jm.build_job(31005951, 16320, 2, None)


# --- the Job controller must not own retries ---------------------------------

def test_the_job_controller_never_replaces_a_failed_pod(job):
    """backoffLimit 0 is load-bearing.

    Above 0 the controller replaces the pod on its own schedule: we could not
    tell a disruption from a catchup failure, could not count evictions against
    their own budget, and could not guarantee the log was archived before the
    next attempt started. Escalating a memory limit also needs a NEW Job --
    spec.template is immutable -- so a controller-driven retry would silently
    re-run at the limit that just killed the range.
    """
    assert job.spec.backoff_limit == 0


def test_a_finished_job_still_has_a_ttl_backstop(job):
    """reconcile() reaps finished Jobs, but only while it is running.

    A monitor that is down, wedged, or has lost its RBAC leaves every finished
    Job listed on every later pass. The TTL is what bounds that, and it must be
    the value the chart configured -- not a second, independent default.
    """
    assert job.spec.ttl_seconds_after_finished == jm.JOB_TTL_SECONDS
    assert jm.JOB_TTL_SECONDS > 0, "a TTL of 0 deletes a Job before it can be classified"


def test_a_worker_pod_is_never_restarted_in_place(job):
    """restartPolicy OnFailure restarts the container inside the same pod.

    Same pod name, same resource limits -- so an OOM would loop forever at the
    limit that killed it, the attempt counter would never advance, and the
    terminated container state the classifier reads would be overwritten.
    """
    assert job.spec.template.spec.restart_policy == 'Never'


def test_the_deadline_is_on_the_pod_not_on_the_job(job, monkeypatch):
    """JobSpec.activeDeadlineSeconds runs from the Job's startTime.

    Every second spent Pending -- waiting for Karpenter, pulling the image -- is
    then charged against a budget meant to bound how long the range RUNS. During
    a node-class outage this run sat ~15 minutes Pending and ranges died as
    "timeouts" having barely executed; a timeout gets only MAX_TIMEOUT_ATTEMPTS,
    so two stalls condemn a range and fail the mission.
    """
    monkeypatch.setattr(jm, 'ATTEMPT_DEADLINE_SECONDS', 43200)
    j = jm.build_job(300, 420, 1, None)
    assert j.spec.active_deadline_seconds is None, \
        "the deadline is on the JobSpec, so Pending time is charged to the range"
    assert j.spec.template.spec.active_deadline_seconds == 43200


def test_no_deadline_means_no_field_at_all(job, monkeypatch):
    """0 is "off". Rendering it literally would kill every pod instantly."""
    monkeypatch.setattr(jm, 'ATTEMPT_DEADLINE_SECONDS', 0)
    j = jm.build_job(300, 420, 1, None)
    assert j.spec.template.spec.active_deadline_seconds is None


def test_the_grace_period_outlasts_a_stellar_core_drain(job):
    """stellar-core catches SIGTERM, drains, and exits 3 in ~7s (ssc-test).

    A grace period shorter than the drain turns every eviction into a SIGKILL
    and exit 137 -- which the podFailurePolicy classifies as an OOM, spends the
    OOM budget instead of the disruption budget, and escalates memory for a
    range that never needed any.
    """
    grace = job.spec.template.spec.termination_grace_period_seconds
    assert grace == jm.WORKER_GRACE_SECONDS
    assert grace > 7, f"{grace}s does not cover the measured ~7s drain"


# --- podFailurePolicy: order IS the protocol ---------------------------------

def test_every_rule_index_decodes_back_to_the_rule_that_matched(job):
    """The round trip: rules[i] -> "rule at index i" -> classify_from_job.

    The controller reports only the index, so the rendered ORDER and the table
    the decoder uses are one contract. Rather than assert they are the same
    list, this drives a real condition message through the real classifier for
    every index that exists -- which is what actually has to hold.
    """
    rules = job.spec.pod_failure_policy.rules
    assert len(rules) == len(jm.RULE_ORDER), (
        f"{len(rules)} rules rendered but {len(jm.RULE_ORDER)} decodable indices")
    for index, expected in enumerate(jm.RULE_ORDER):
        msg = (f"Container stellar-core for pod ns/p failed with exit code 1 "
               f"matching FailJob rule at index {index}")
        verdict = jm.classify_from_job(_failed_job(msg))
        assert verdict['outcome'] == expected, (
            f"rule {index} renders as {expected!r} but decodes as {verdict['outcome']!r}")


def test_disruption_is_evaluated_before_any_exit_code(job):
    """First match wins, and exit 3 is ambiguous on its own.

    stellar-core exits 3 both for a SIGTERM drain and for a corrupt bucket, so
    the DisruptionTarget condition is the only thing that separates a spot
    eviction from a broken range. If an exit-code rule were evaluated first, an
    eviction would match it, be condemned as a catchup failure, and abort a
    whole run -- on spot, routinely.
    """
    rules = job.spec.pod_failure_policy.rules
    assert rules[0].on_pod_conditions, "index 0 is not the pod-condition rule"
    assert [(c.type, c.status) for c in rules[0].on_pod_conditions] \
        == [('DisruptionTarget', 'True')]
    assert jm.RULE_ORDER[0] == 'disrupted'
    for rule in rules[1:]:
        assert rule.on_exit_codes is not None


def test_the_oom_rule_is_narrower_than_the_catch_all_and_precedes_it(job):
    """137 has to be matched before "any non-zero", or it never matches at all.

    Reaching the 137 rule also proves DisruptionTarget did not match, which is
    the only way to tell an OOM kill from a grace-period SIGKILL once the pod
    is gone.
    """
    rules = job.spec.pod_failure_policy.rules
    oom = rules[jm.RULE_ORDER.index('oom')].on_exit_codes
    catch_all = rules[jm.RULE_ORDER.index('failed')].on_exit_codes
    assert (oom.operator, oom.values) == ('In', [137])
    assert (catch_all.operator, catch_all.values) == ('NotIn', [0])
    assert jm.RULE_ORDER.index('oom') < jm.RULE_ORDER.index('failed')


def test_every_rule_fails_the_job_rather_than_counting_it(job):
    """A Count action surfaces as BackoffLimitExceeded and loses the index.

    classify_from_job only reads a condition whose reason is PodFailurePolicy;
    anything else carries no per-rule detail and returns no verdict at all.
    """
    for rule in job.spec.pod_failure_policy.rules:
        assert rule.action == 'FailJob'


def test_the_exit_code_rules_name_the_container_that_actually_runs(job):
    """A containerName that matches nothing makes the rule silently inert.

    The Job would then fall through to the catch-all -- or to no rule -- and an
    OOM would arrive with no index at all.
    """
    names = {c.name for c in job.spec.template.spec.containers}
    for rule in job.spec.pod_failure_policy.rules:
        if rule.on_exit_codes is not None:
            assert rule.on_exit_codes.container_name in names, (
                f"rule targets container {rule.on_exit_codes.container_name!r}, "
                f"pod has {sorted(names)}")


def test_the_collector_watches_the_container_the_job_creates(job):
    """The collector streams one container by name and samples its memory.

    A rename here leaves it streaming nothing -- and the peak sampler skipping
    every container, since it filters on the same name.
    """
    names = {c.name for c in job.spec.template.spec.containers}
    default = _clean_default('log_collector', 'CONTAINER')
    assert default in names, (
        f"the collector follows {default!r}; the Job creates {sorted(names)}")


# --- labels: the pod is the collector's only source of the attempt -----------

def test_the_pod_carries_its_own_attempt_number(job):
    """The collector reads the attempt off the POD, and defaults it to "1".

    With the label only on the Job, every attempt claimed the same
    range-<end>-a1.* files: measured on ssc-test 2026-07-30, 2246 metrics files
    all a1 while 475 a2 pods were running -- so each retry OVERWROTE the first
    attempt's peak instead of being maxed against it, destroying exactly the
    OOM evidence the resumed chain exists to keep.
    """
    labels = job.spec.template.metadata.labels
    assert labels[jm.LABEL_ATTEMPT] == '2'
    assert labels[jm.LABEL_RANGE] == '31005951'
    assert labels[jm.LABEL_RUN] == 'pc'


def test_both_processes_agree_on_the_label_keys():
    """Two readers, one key. A mismatch reproduces the same silent collision."""
    assert jm.LABEL_ATTEMPT == lc.LABEL_ATTEMPT
    assert jm.LABEL_RUN == lc.LABEL_RUN


def test_the_job_is_findable_by_the_same_labels_as_its_pod(job):
    """reconcile lists Jobs by run label and reads the range and attempt off it.

    The pod list and the Job list have to describe the same universe, or a Job
    is reaped while its pod is still streaming.
    """
    for key in (jm.LABEL_RUN, jm.LABEL_RANGE, jm.LABEL_ATTEMPT):
        assert job.metadata.labels[key] == job.spec.template.metadata.labels[key]


def test_the_job_name_encodes_the_range_and_the_attempt(job):
    """Name uniqueness IS the dispatch mutex.

    reconcile treats a 409 AlreadyExists as "someone else already dispatched
    this attempt" and spends a slot rather than raising. A name that did not
    vary with the attempt would make a retry collide with its predecessor
    forever; one that did not vary with the range would let two ranges share it.
    """
    assert job.metadata.name == jm.job_name(31005951, 2)
    assert jm.job_name(1, 1) != jm.job_name(1, 2) != jm.job_name(2, 2)


# --- the worker's own inputs -------------------------------------------------

def test_the_worker_runs_the_resume_script_for_its_own_range(job):
    """The key the script marks /data with is the range identity.

    RESUME only skips new-db when the DB on /data belongs to THIS range; the
    mark file is how it knows. A key that did not match the catchup argument
    would resume one range's replay into another's database.
    """
    command = job.spec.template.spec.containers[0].command
    assert command[:2] == ['/bin/sh', '-c']
    script = command[2]
    key = jm.job_key(31005951, 16320)
    assert f'KEY="{key}"' in script
    assert f'catchup "$KEY"' in script


def test_synthetic_worker_is_absent_from_the_default_job(job):
    container = job.spec.template.spec.containers[0]
    assert container.image_pull_policy is None
    assert 'synthetic-worker' not in {v.name for v in job.spec.template.spec.volumes}
    assert not any(e.name.startswith('SYNTHETIC_') for e in container.env)


def test_opt_in_synthetic_worker_uses_only_the_fixed_chart_script(job, monkeypatch):
    monkeypatch.setattr(jm, 'SYNTHETIC_WORKER_CONFIG_MAP', 'pc-synthetic-worker')
    monkeypatch.setattr(jm, 'SYNTHETIC_WORKER_IMAGE_PULL_POLICY', 'IfNotPresent')

    synthetic = jm.build_job(31005951, 16320, 2, None)
    container = synthetic.spec.template.spec.containers[0]
    env = {e.name: e.value for e in container.env}
    volumes = {v.name: v for v in synthetic.spec.template.spec.volumes}
    mounts = {m.name: m.mount_path for m in container.volume_mounts}

    assert container.command == ['python3', '/synthetic/worker.py']
    assert container.image == 'stellar/stellar-core:test'
    assert container.image_pull_policy == 'IfNotPresent'
    assert env['SYNTHETIC_ATTEMPT'] == '2'
    assert env['SYNTHETIC_TARGET'] == '31005951'
    assert env['SYNTHETIC_COUNT'] == '16320'
    assert env['SYNTHETIC_KEY'] == jm.job_key(31005951, 16320)
    assert volumes['synthetic-worker'].config_map.name == 'pc-synthetic-worker'
    assert mounts['synthetic-worker'] == '/synthetic'


def test_the_worker_mounts_the_config_the_chart_renders(job):
    """The stellar-core.cfg ConfigMap is the chart's, named off the release.

    It is also the object every Job, PVC and the progress ConfigMap are
    owner-referenced to, so the name has to be the one owner_ref() reads.
    """
    volumes = {v.name: v for v in job.spec.template.spec.volumes}
    assert volumes['config'].config_map.name == f"{jm.RUN_NAME}-stellar-core-config"
    mounts = {m.name: m.mount_path for m in job.spec.template.spec.containers[0].volume_mounts}
    assert mounts['config'] == '/config'
    assert '/config/stellar-core.cfg' in job.spec.template.spec.containers[0].command[2]


def test_data_is_the_path_the_resume_script_probes(job):
    """RESUME reads /data/.job-key and the previous incarnation's core log."""
    mounts = {m.name: m.mount_path for m in job.spec.template.spec.containers[0].volume_mounts}
    assert mounts['data'] == '/data'
    assert 'MARK=/data/.job-key' in job.spec.template.spec.containers[0].command[2]


# --- helpers -----------------------------------------------------------------

def _failed_job(message, reason='PodFailurePolicy'):
    """The shape classify_from_job reads: a Job with one Failed condition."""
    return NS(status=NS(conditions=[
        NS(type='Failed', status='True', reason=reason, message=message)]))


def _clean_default(module_name, constant):
    """The module's own fallback, read with no ambient env set."""
    return art.defaults(module_name)[constant]
