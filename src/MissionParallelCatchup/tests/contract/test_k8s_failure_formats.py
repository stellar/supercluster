"""Captured Kubernetes status text against the classifiers that decode it.

None of these strings are ours. The Job controller's podFailurePolicy condition
message, the kubelet's admission-rejection reasons, its eviction message and the
plain text its log endpoint returns for a container that has not started are all
formats a Kubernetes upgrade can change under us. They are pinned from real
captures so that change fails here rather than degrading a run silently -- a
misread verdict does not stop anything, it just picks the wrong retry budget or
condemns a healthy range.

Everything is driven through the real classify()/classify_from_job() rather than
a mirror of them, so only the FORMAT is pinned, not the implementation.
"""

import re
from types import SimpleNamespace as NS

import pytest

import job_monitor as jm
import log_collector as lc

import _artifacts as art

# --- captures ----------------------------------------------------------------

# EKS 1.34 Job condition messages. Only the wording is pinned; pod and container
# names are renamed for readability.
DISRUPTED = ("Pod sandbox/jterm-catchup-snfr2 has condition DisruptionTarget "
             "matching FailJob rule at index 0")
OOMKILLED = ("Container oom-container for pod sandbox/oom-test-job-qvq8b failed with "
             "exit code 137 matching FailJob rule at index 1")
NONZERO_EXIT = ("Container exit-1-container for pod sandbox/exit-1-job-wbhkq failed with "
                "exit code 1 matching FailJob rule at index 2")

# RECONSTRUCTED 2026-07-30 after an over-broad test deletion removed the
# originals -- twice. Shaped to what the code parses (the rule index, and the
# substring 'ephemeral' in status.message) but no longer a verbatim capture.
# Re-pin from a real eviction on the next run.
EPH_EVICT_JOB_CONDITION = (
    "Container stellar-core for pod stellar-supercluster/"
    "parallel-catchup-r31005951-a1-x7k2p failed with exit code 3 "
    "matching FailJob rule at index 2")
EPH_EVICT_MESSAGE = (
    "Pod ephemeral local storage usage exceeds the total limit of containers 40Gi")

# Kubelet reasons seen on ssc-test for a pod refused or removed before -- or
# without -- the container saying anything about the ledger range. None of these
# is evidence that the range is bad.
ADMISSION_REJECTIONS = ('VolumeAttachmentLimitExceeded', 'OutOfcpu', 'OutOfmemory',
                        'OutOfpods', 'UnexpectedAdmissionError', 'NodeAffinity',
                        'Shutdown', 'Evicted')


# --- shims: the two shapes the classifiers read ------------------------------

def failed_job(message, reason='PodFailurePolicy'):
    return NS(status=NS(conditions=[
        NS(type='Failed', status='True', reason=reason, message=message)]))


def pod(reason=None, message=None, disrupted=False, exit_code=None,
        terminated_reason=None):
    conditions = ([NS(type='DisruptionTarget', status='True')] if disrupted else [])
    statuses = []
    if exit_code is not None or terminated_reason is not None:
        statuses = [NS(state=NS(terminated=NS(exit_code=exit_code,
                                              reason=terminated_reason)))]
    return NS(metadata=NS(name='p'),
              status=NS(conditions=conditions, reason=reason, message=message,
                        container_statuses=statuses))


# --- the Job condition, which is all that is left once the pod is gone -------

@pytest.mark.parametrize('message,outcome,code,pod_name', [
    (DISRUPTED, 'disrupted', None, ''),
    (OOMKILLED, 'oom', 137, 'oom-test-job-qvq8b'),
    (NONZERO_EXIT, 'failed', 1, 'exit-1-job-wbhkq'),
])
def test_a_job_condition_message_still_parses(message, outcome, code, pod_name):
    """Index, exit code and pod name are parsed independently.

    A rule matching on onPodConditions reports no exit code at all, so requiring
    one would make the disruption case -- the common case on spot -- unreadable.
    """
    verdict = jm.classify_from_job(failed_job(message))
    assert verdict['outcome'] == outcome
    assert verdict['exitCode'] == code
    assert verdict['pod'] == pod_name


def test_the_rule_index_outranks_the_exit_code():
    """A disrupted pod that also exited non-zero must read as disrupted.

    stellar-core catches the eviction SIGTERM and exits 3, so the exit code says
    "failed" for something the cluster did to us. Only the index carries the
    DisruptionTarget match.
    """
    message = ("Container stellar-core for pod ns/p failed with exit code 3 "
               "matching FailJob rule at index 0")
    assert jm.classify_from_job(failed_job(message))['outcome'] == 'disrupted'


def test_a_bare_exit_code_with_no_index_is_still_usable():
    """Some conditions carry the exit code and no rule index.

    Measured on ssc-test 2026-07-28: a drained stellar-core exits 3 in ~7s, well
    inside the 100s grace, so evictions do NOT produce 137 -- which makes a bare
    137 an OOM with high confidence, and a bare 3 a real catchup failure.
    """
    bare = "Container c for pod ns/p failed with exit code %d"
    assert jm.classify_from_job(failed_job(bare % 137))['outcome'] == 'oom'
    assert jm.classify_from_job(failed_job(bare % 3))['outcome'] == 'failed'


def test_a_condition_with_no_detail_at_all_yields_no_verdict():
    """BackoffLimitExceeded carries no index and no exit code.

    Returning a verdict here would be an invention. "No verdict" is what routes
    the range to the environmental budget instead of condemning it -- a monitor
    restart while a node was reaped produces exactly this message, and
    condemning on it would fail a 10-hour job on no evidence.
    """
    assert jm.classify_from_job(
        failed_job("Job has reached the specified backoff limit",
                   reason='BackoffLimitExceeded')) is None


def test_the_deadline_is_reported_by_the_job_and_nothing_else():
    """activeDeadlineSeconds fires as its own reason, not as a policy rule.

    The pod that gets SIGTERMed drains and exits 3, which reads as a plain
    catchup failure. Only the Job knows the deadline was what killed it.
    """
    verdict = jm.classify_from_job(
        failed_job("Job was active longer than specified deadline",
                   reason='DeadlineExceeded'))
    assert verdict['outcome'] == 'timeout'
    assert verdict['exitCode'] is None


# --- the pod, which carries everything the Job cannot ------------------------

@pytest.mark.parametrize('reason', ADMISSION_REJECTIONS)
def test_an_admission_rejection_is_not_a_catchup_failure(reason):
    """The kubelet refused the pod; stellar-core never ran.

    Observed on ssc-test: reason=VolumeAttachmentLimitExceeded, "Node has
    reached its volume attachment limit, rejecting pod". Without this the pod
    falls through to 'failed', and a transient admission rejection condemns a
    range and kills the whole run.
    """
    assert jm.classify(pod(reason=reason))['outcome'] == 'rejected'


def test_an_ephemeral_eviction_is_told_apart_from_every_other_eviction():
    """status.message is the only discriminator, and only the pod carries it.

    Measured end-to-end on ssc-test: the kubelet sets no DisruptionTarget for a
    limit eviction, and stellar-core drains and exits 3 -- so the Job condition
    matches the generic non-zero rule and reads as a plain catchup failure,
    which gets no retry at all. Both the ephemeral branch and the generic
    Evicted branch key on reason='Evicted', so the ephemeral one has to be
    reached first.
    """
    assert 'index 2' in EPH_EVICT_JOB_CONDITION, "the Job matches the generic non-zero rule"
    assert jm.classify_from_job(failed_job(EPH_EVICT_JOB_CONDITION))['outcome'] == 'failed'

    evicted = pod(reason='Evicted', message=EPH_EVICT_MESSAGE, exit_code=3)
    assert jm.classify(evicted)['outcome'] == 'ephemeral'
    # ...and an eviction for anything else stays a plain rejection.
    other = pod(reason='Evicted', message='The node was low on resource: memory.',
                exit_code=3)
    assert jm.classify(other)['outcome'] == 'rejected'


def payload(reason=None, message=None, disrupted=False, exit_code=None,
            terminated_reason=None):
    """The same pod as pod(), in the raw JSON shape the collector reads.

    The collector talks to the apiserver over plain HTTP and classifies a dict;
    the monitor classifies a client object. Same pod, two spellings.
    """
    status = {}
    if disrupted:
        status['conditions'] = [{'type': 'DisruptionTarget', 'status': 'True'}]
    if reason is not None:
        status['reason'] = reason
    if message is not None:
        status['message'] = message
    if exit_code is not None or terminated_reason is not None:
        term = {}
        if exit_code is not None:
            term['exitCode'] = exit_code
        if terminated_reason is not None:
            term['reason'] = terminated_reason
        status['containerStatuses'] = [{'state': {'terminated': term}}]
    return {'metadata': {'name': 'p'}, 'status': status}


CLASSIFIER_CASES = [
    ('a spot reclaim', dict(disrupted=True, exit_code=3)),
    ('a disk eviction', dict(reason='Evicted', message=EPH_EVICT_MESSAGE, exit_code=3)),
    ('any other eviction', dict(reason='Evicted', message='node was low on memory')),
    ('an admission rejection', dict(reason='VolumeAttachmentLimitExceeded')),
    ('an oom kill', dict(exit_code=137, terminated_reason='OOMKilled')),
    ('a graceful-stop sigkill', dict(exit_code=137, terminated_reason='Error')),
    ('a catchup failure', dict(exit_code=1)),
    ('an interrupted catchup', dict(exit_code=3)),
    ('nothing ever ran', dict()),
]


@pytest.mark.parametrize('label,case', CLASSIFIER_CASES, ids=[c[0] for c in CLASSIFIER_CASES])
def test_both_processes_reach_the_same_verdict_about_the_same_pod(label, case):
    """Two independent classifiers, one pod, one answer.

    The collector classifies while the pod still exists and writes the
    authoritative .outcome; the monitor classifies again at reconcile when that
    file is missing. If they disagreed, a range's verdict -- and therefore which
    attempt budget it spends -- would depend on which process saw it first.
    """
    from_monitor = jm.classify(pod(**case))
    from_collector = lc.classify(payload(**case))
    assert from_monitor['outcome'] == from_collector['outcome'], label
    assert from_monitor['exitCode'] == from_collector['exitCode'], label


def test_a_disruption_beats_everything_the_pod_says():
    """A spot reclaim sets the condition and the container still exits 3."""
    assert jm.classify(pod(disrupted=True, exit_code=3))['outcome'] == 'disrupted'


def test_an_oom_kill_is_named_by_the_kubelet_not_inferred_from_137():
    """137 is SIGKILL, which the kubelet also uses for a graceful-stop timeout.

    On the pod the reason is available and unambiguous, so it is used; only the
    Job-condition path has to infer from the code alone.
    """
    assert jm.classify(pod(exit_code=137, terminated_reason='OOMKilled'))['outcome'] == 'oom'
    assert jm.classify(pod(exit_code=137, terminated_reason='Error'))['outcome'] == 'failed'


def test_a_pod_whose_container_never_terminated_is_not_evidence():
    """Nothing ran, so nothing was learned about the ledger range."""
    assert jm.classify(pod())['outcome'] == 'rejected'


def test_the_pod_deadline_is_a_timeout_not_a_catchup_failure():
    """The deadline lives on the PodSpec, so the kubelet fires it and the pod
    carries the reason -- the Job only sees a non-zero exit."""
    assert jm.classify(pod(reason='DeadlineExceeded', exit_code=3))['outcome'] == 'timeout'


# --- the vocabulary both classifiers speak -----------------------------------

def test_every_outcome_the_classifiers_can_produce_has_a_budget():
    """A new outcome string with no branch falls through to "condemn".

    Each outcome routes to one of three attempt budgets. An outcome nobody
    routed would take the zero-retry path, and a condemned range fails the
    mission.
    """
    produced = set()
    for source in (art.module_source(jm), art.module_source(lc)):
        produced |= set(re.findall(r"'outcome':\s*'(\w+)'", source))
    budgeted = set(jm.ENVIRONMENTAL_OUTCOMES) | {'oom', 'ephemeral', 'timeout', 'failed'}
    assert produced <= budgeted, f"unrouted outcomes: {sorted(produced - budgeted)}"
    assert 'disrupted' in produced and 'rejected' in produced


def test_the_deterministic_failures_do_not_get_the_environmental_budget():
    """Environmental means "the cluster did this to us" and gets ~20 attempts.

    An OOM, a disk eviction, a hang and a genuinely corrupt range are all
    statements about the range; giving them 20 attempts would park a node on a
    broken range for hours. 'unknown' IS environmental on purpose -- an
    unclassifiable failure is usually a monitor restart racing a reaped node.
    """
    environmental = set(jm.ENVIRONMENTAL_OUTCOMES)
    assert not (environmental & {'oom', 'ephemeral', 'timeout', 'failed'}), \
        f"a deterministic failure inherited the disruption budget: {sorted(environmental)}"
    assert {'disrupted', 'rejected', 'unknown'} <= environmental


# --- the log endpoint, which does not always return log lines ----------------

def test_untimestamped_kubelet_text_never_becomes_a_resume_point():
    """A pod that has just been replaced returns prose, not log lines.

    Partitioning that on the first space yields "unable", which as a resume
    point makes every later request sinceTime=unableZ -> HTTP 400, for the life
    of the range. Observed on ssc-test the moment evicted pods were replaced.
    """
    kubelet = "unable to retrieve container logs for containerd://9f2c1a"
    assert lc._TS_RE.match(kubelet.partition(' ')[0]) is None
    for good in ("2026-07-28T20:29:27.927795721Z", "2026-07-28T20:29:27Z"):
        assert lc._TS_RE.match(good), good


def test_a_poisoned_state_file_is_repaired_rather_than_replayed(tmp_path, monkeypatch):
    """The guard has to be on the READ as well as the write.

    A state file written by an earlier build already holds "unable" on some
    volumes, and nothing rewrites it until a poll succeeds -- which it cannot,
    because the poisoned value is what makes the poll 400.
    """
    monkeypatch.setattr(lc, 'LOG_DIR', str(tmp_path))
    with open(lc.base('300', 1) + '.state', 'w') as fh:
        fh.write('unable')
    assert lc.read_state('300', 1) is None
    lc.write_state('300', 1, '2026-07-28T20:29:27Z')
    assert lc.read_state('300', 1) == '2026-07-28T20:29:27Z'
