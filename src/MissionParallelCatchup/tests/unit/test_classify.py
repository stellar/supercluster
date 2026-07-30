"""How a failed attempt is classified, from the Job and from the pod.

Two classifiers, two different objects. classify_from_job() reads the Job
controller's podFailurePolicy condition message -- a format this mission does
not control, pinned here from real captures so an EKS change fails here rather
than silently degrading a run. classify() reads the pod, which carries detail
the Job never has.
"""

import pytest
from kubernetes import client

import job_monitor as jm
import log_collector as lc


# --- captures ----------------------------------------------------------------

# EKS 1.34 Job condition messages. Only the wording is pinned; pod and
# container names are renamed for readability.
DISRUPTED = ("Pod sandbox/jterm-catchup-snfr2 has condition DisruptionTarget "
             "matching FailJob rule at index 0")
OOMKILLED = ("Container oom-container for pod sandbox/oom-test-job-qvq8b failed with "
             "exit code 137 matching FailJob rule at index 1")
NONZERO_EXIT = ("Container exit-1-container for pod sandbox/exit-1-job-wbhkq failed with "
                "exit code 1 matching FailJob rule at index 2")

# RECONSTRUCTED 2026-07-30 after an over-broad test deletion removed the
# originals -- twice. Shaped to what the code parses (RULE_ORDER[2] is 'failed';
# classify() keys on the substring 'ephemeral' in status.message) but no longer
# a verbatim capture. Re-pin from a real eviction on the next run.
EPH_EVICT_JOB_CONDITION = (
    "Container stellar-core for pod stellar-supercluster/"
    "parallel-catchup-r31005951-a1-x7k2p failed with exit code 3 "
    "matching FailJob rule at index 2")
EPH_EVICT_MESSAGE = (
    "Pod ephemeral local storage usage exceeds the total limit of containers 40Gi")


def failed_job(message='', reason='PodFailurePolicy'):
    return client.V1Job(status=client.V1JobStatus(conditions=[
        client.V1JobCondition(type='Failed', status='True',
                              reason=reason, message=message)]))


def verdict(message, reason='PodFailurePolicy'):
    """(outcome, exitCode, pod) as classify_from_job reports them."""
    got = jm.classify_from_job(failed_job(message, reason))
    if got is None:
        return (None, None, None)
    return (got['outcome'], got['exitCode'], got['pod'] or None)


def pod_with(reason=None, message=None, conditions=None, terminated=None,
             container='stellar-core'):
    """A pod carrying exactly the status fields classify() branches on."""
    statuses = None
    if terminated is not None:
        statuses = [client.V1ContainerStatus(
            name=container, image='core', image_id='', ready=False, restart_count=0,
            state=client.V1ContainerState(
                terminated=client.V1ContainerStateTerminated(**terminated)))]
    return client.V1Pod(
        metadata=client.V1ObjectMeta(name='p'),
        status=client.V1PodStatus(reason=reason, message=message,
                                  conditions=conditions, container_statuses=statuses))


def as_dict(reason=None, message=None, conditions=None, terminated=None):
    """The same pod, in the shape the collector reads off the raw API."""
    status = {'reason': reason, 'message': message,
              'conditions': conditions or [],
              'containerStatuses': ([{'state': {'terminated': terminated}}]
                                    if terminated is not None else [])}
    return {'metadata': {'name': 'p'}, 'status': status}


# --- classify_from_job: the Job controller's message --------------------------

@pytest.mark.parametrize("msg,outcome,code,pod", [
    (DISRUPTED, 'disrupted', None, None),
    (OOMKILLED, 'oom', 137, 'oom-test-job-qvq8b'),
    (NONZERO_EXIT, 'failed', 1, 'exit-1-job-wbhkq'),
])
def test_job_condition_message(msg, outcome, code, pod):
    assert verdict(msg) == (outcome, code, pod)


def test_rule_order_matches_the_rendered_policy():
    # "rule at index N" is only meaningful against the order the rules are
    # rendered in, so the lookup table and the policy must be the same list.
    assert [name for name, _ in jm._failure_rules()] == jm.RULE_ORDER


def test_eviction_is_told_apart_from_a_broken_range_by_the_condition():
    # stellar-core exits 3 both for a drain and for a corrupt bucket, so only
    # DisruptionTarget separates them -- hence rule 0 must be evaluated first.
    assert verdict(DISRUPTED)[0] == 'disrupted'
    assert verdict("Container c for pod ns/p failed with exit code 3")[0] == 'failed'


def test_a_bare_exit_code_is_read_when_no_rule_index_is_offered():
    # Measured on ssc-test 2026-07-28: a drained stellar-core catches SIGTERM
    # and exits 3 well inside the 100s grace, so evictions do NOT produce 137 --
    # which makes a bare 137 an OOM with high confidence.
    assert verdict("Container c for pod ns/p failed with exit code 137")[:2] == ('oom', 137)
    assert verdict("Container c for pod ns/p failed with exit code 1")[:2] == ('failed', 1)


def test_an_unclassifiable_job_failure_stays_unclassified():
    # BackoffLimitExceeded carries no rule index and no exit code, so classify
    # honestly returns nothing rather than guessing. A monitor restart while a
    # node was reaped produces exactly this, and condemning on it would fail a
    # 10-hour job on no evidence -- reconcile gives it the environmental budget.
    assert jm.classify_from_job(failed_job(
        "Job has reached the specified backoff limit",
        reason='BackoffLimitExceeded')) is None
    assert 'unknown' in jm.ENVIRONMENTAL_OUTCOMES
    assert {'disrupted', 'rejected'} <= set(jm.ENVIRONMENTAL_OUTCOMES)


def test_a_deadline_exceeded_job_is_a_timeout_not_a_catchup_failure():
    # activeDeadlineSeconds fired: the attempt hung rather than failing. Only
    # the Job knows this -- the deadline SIGTERMs the pod, which drains to
    # exit 3 and reads as a plain catchup failure from the pod side.
    assert verdict('', reason='DeadlineExceeded')[0] == 'timeout'


def test_a_job_with_no_failed_condition_yields_nothing():
    assert jm.classify_from_job(client.V1Job(status=client.V1JobStatus())) is None


# --- classify: what only the pod can say --------------------------------------

def test_a_disruption_target_condition_outranks_everything_on_the_pod():
    got = jm.classify(pod_with(
        conditions=[client.V1PodCondition(type='DisruptionTarget', status='True')],
        terminated={'exit_code': 3, 'reason': 'Error'}))
    assert got['outcome'] == 'disrupted'


def test_an_ephemeral_eviction_is_not_read_as_an_oom_or_a_disruption():
    # Measured end-to-end on ssc-test: the kubelet sets no DisruptionTarget,
    # and stellar-core drains and exits 3, so the Job condition is a plain
    # non-zero failure that would get no retry. status.message is the only
    # discriminator and only the pod carries it, so both classifiers must test
    # it before anything keyed on Evicted.
    assert verdict(EPH_EVICT_JOB_CONDITION)[0] == 'failed', \
        "the Job matches the generic non-zero rule"
    assert 'ephemeral' in EPH_EVICT_MESSAGE, "both classifiers key on this substring"
    evicted = dict(reason='Evicted', message=EPH_EVICT_MESSAGE,
                   terminated={'exit_code': 3, 'reason': 'Error'})
    assert jm.classify(pod_with(**evicted))['outcome'] == 'ephemeral'
    assert lc.classify(as_dict(reason='Evicted', message=EPH_EVICT_MESSAGE,
                               terminated={'exitCode': 3}))['outcome'] == 'ephemeral'


def test_a_plain_eviction_with_no_disk_message_is_only_a_rejection():
    # The generic Evicted branch sits right behind the ephemeral one; an
    # eviction for anything other than the range's own disk use must still
    # reach it, or a node-pressure eviction would be read as a disk overrun and
    # grow the range's storage for no reason.
    got = jm.classify(pod_with(reason='Evicted', message='node was low on memory'))
    assert got['outcome'] == 'rejected'


@pytest.mark.parametrize('reason', [
    'VolumeAttachmentLimitExceeded', 'OutOfcpu', 'OutOfmemory', 'OutOfpods',
    'UnexpectedAdmissionError', 'NodeAffinity', 'Shutdown', 'Evicted',
])
def test_an_admission_rejection_is_not_a_catchup_failure(reason):
    # Observed on ssc-test: reason=VolumeAttachmentLimitExceeded, "Node has
    # reached its volume attachment limit, rejecting pod". No exit code, no
    # DisruptionTarget -- without this branch it falls through to 'failed' and
    # a transient admission rejection kills the whole run.
    for got in (jm.classify(pod_with(reason=reason)),
                lc.classify(as_dict(reason=reason))):
        assert got['outcome'] == 'rejected', reason
        assert got['exitCode'] is None


def test_a_deadline_kill_is_visible_on_the_pod_too():
    # The deadline lives on the PodSpec, so the kubelet fires it and the pod
    # carries the reason; the Job only sees a non-zero exit.
    assert jm.classify(pod_with(reason='DeadlineExceeded'))['outcome'] == 'timeout'


def test_a_pod_where_nothing_ever_ran_says_nothing_about_the_range():
    for got in (jm.classify(pod_with()), lc.classify(as_dict())):
        assert got['outcome'] == 'rejected'


def test_an_oom_kill_is_read_from_the_container_reason_not_the_exit_code():
    # 137 is SIGKILL, which the kubelet also uses for a graceful-stop timeout --
    # only reason=OOMKilled makes it unambiguous.
    for got in (jm.classify(pod_with(terminated={'exit_code': 137, 'reason': 'OOMKilled'})),
                lc.classify(as_dict(terminated={'exitCode': 137, 'reason': 'OOMKilled'}))):
        assert (got['outcome'], got['exitCode']) == ('oom', 137)


def test_a_non_zero_exit_is_a_catchup_failure_and_keeps_its_code():
    for got in (jm.classify(pod_with(terminated={'exit_code': 3, 'reason': 'Error'})),
                lc.classify(as_dict(terminated={'exitCode': 3}))):
        assert (got['outcome'], got['exitCode']) == ('failed', 3)


def test_exit_three_is_the_ambiguous_one_and_never_inherits_a_bigger_budget():
    # stellar-core drains to 3 on SIGTERM and a corrupt bucket also exits 3, so
    # reconcile retries it on the ordinary range budget -- but a genuinely
    # corrupt range must still be able to exhaust rather than retry 20 times.
    assert jm.CATCHUP_INCOMPLETE_EXIT == 3
    assert 'failed' not in jm.ENVIRONMENTAL_OUTCOMES
    assert 'ephemeral' not in jm.ENVIRONMENTAL_OUTCOMES, \
        "a deterministic failure must not get the disruption budget"
