"""RACE #6 -- the attempt deadline is on the wrong object, and it outranks the pod.

Two independent defects, both run-ending, both driven here through the real
reconcile() against the fake cluster:

A. `activeDeadlineSeconds` sits on the JobSpec, so the clock starts when the Job
   is created rather than when the container starts. Every second a pod spends
   Pending -- waiting for Karpenter, waiting for an image pull -- is charged
   against a budget that is meant to bound how long the RANGE runs. During the
   node-class outage this run really did sit ~15 minutes Pending, and ranges
   then died as "timeouts" having barely executed.

B. When the Job reports DeadlineExceeded the monitor takes that verdict
   unconditionally, over the pod's own terminated reason. A pod the kubelet
   OOM-killed inside a Job that also tripped its deadline is filed as a timeout:
   no memory escalation, and no budget at all, because a timeout is terminal.
   One such event condemns the range and fails the mission.

Nothing here asserts on source text. Facet B is fully drivable with the shipped
harness. Facet A needs the one thing the fake cluster does not have -- the piece
of Kubernetes that actually enforces a deadline -- so `_DeadlineController`
below supplies it. It is a model of *Kubernetes*, not of job_monitor: it reads
whichever field the monitor set and applies the clock that Kubernetes documents
for that field. A monitor that puts the deadline in the right place survives it;
one that puts it in the wrong place does not.
"""

import pytest

import config
import records
import job_monitor as jm


DEADLINE = 600          # ATTEMPT_DEADLINE_SECONDS for the facet-A tests


# --- the bit of Kubernetes that enforces activeDeadlineSeconds ---------------

class _DeadlineController:
    """Two fields, two clocks. That difference is the entire bug.

    * `JobSpec.activeDeadlineSeconds` is measured from `job.status.startTime`,
      which the Job controller stamps when the Job is admitted -- before any pod
      is scheduled. Pending time counts against it. On expiry the Job is
      terminated with a Failed condition, reason=DeadlineExceeded.

    * `PodSpec.activeDeadlineSeconds` is measured from the pod's own start time,
      set by the kubelet when the pod starts running. Pending time does not
      count. On expiry the pod is killed (SIGTERM; stellar-core drains and exits
      3) and marked Failed with reason=DeadlineExceeded, and the Job then fails
      through its podFailurePolicy like any other non-zero exit.

    This class knows nothing about which one job_monitor chose -- it reads the
    Job it was handed.
    """

    @staticmethod
    def deadlines(job):
        pod_spec = job.spec.template.spec
        return (job.spec.active_deadline_seconds,
                getattr(pod_spec, 'active_deadline_seconds', None))

    @classmethod
    def run_attempt(cls, cluster, end, pending_seconds, running_seconds,
                    finishes='succeeded'):
        """Play one attempt's timeline out against whatever deadline is set.

        Returns the state the cluster ended up in: 'timeout' if a deadline
        fired, otherwise `finishes`.
        """
        name = cluster.job_name(end)
        job_deadline, pod_deadline = cls.deadlines(cluster.k8s.job(name))

        if job_deadline is not None and pending_seconds + running_seconds > job_deadline:
            # Job-level clock: the Job controller kills it and stamps its own
            # condition. The pod is SIGTERMed and drains to exit 3.
            cluster.advance(end, 'timeout')
            return 'timeout'

        if pod_deadline is not None and running_seconds > pod_deadline:
            # Pod-level clock: the kubelet kills the pod and marks it
            # DeadlineExceeded. The Job fails through the ordinary exit-code
            # rule -- it has no idea a deadline was involved.
            pod = cluster.k8s.pod_for_job(name)
            cluster.k8s.set_pod_terminated(pod.metadata.name, exit_code=3,
                                           seconds=running_seconds)
            cluster.k8s.set_pod_phase(pod.metadata.name, 'Failed',
                                      reason='DeadlineExceeded',
                                      message='Pod was active on the node longer '
                                              'than the specified deadline')
            cluster.k8s.set_job_failed(
                name, message=(f"Container stellar-core for pod {cluster.namespace}/"
                               f"{pod.metadata.name} failed with exit code 3 "
                               f"matching FailJob rule at index 2"))
            return 'timeout'

        cluster.advance(end, finishes)
        return finishes


def _job_hit_its_deadline(cluster, end, attempt=None):
    """Stamp the Job-level DeadlineExceeded condition, leaving the pod as-is.

    This is the interleaving in facet B: the pod has already recorded a specific
    terminated reason (OOMKilled, DisruptionTarget, ...) and the Job *also*
    tripped its deadline, so both signals are on the table at once.
    """
    name = cluster.job_name(end, attempt)
    cluster.k8s.set_job_failed(name, reason='DeadlineExceeded',
                               message='Job was active longer than specified deadline')
    return name


def _memory(cluster, job_name):
    return cluster.k8s.job(job_name).spec.template.spec.containers[0].resources


# --- A: Pending time must not be charged against the runtime budget ----------

def test_a_range_that_never_ran_still_fails_when_it_hits_the_deadline(cluster, monkeypatch):
    """15 minutes Pending, 100 seconds of work, a 600s budget -- this must pass.

    The range ran for a sixth of its allowance. It is only killed because the
    clock was started by the Job's creation instead of by the container's start.
    """
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()

    outcome = _DeadlineController.run_attempt(
        cluster, 300, pending_seconds=900, running_seconds=100, finishes='succeeded')
    cluster.finalize(300, 1, tx_apply=0.5)
    cluster.reconcile()

    assert outcome == 'timeout', (
        "the attempt was killed after 100s of running against a 600s budget: "
        "the deadline is counting the 900s it spent Pending")
    assert '300' in cluster.failed()
    assert cluster.completed() == {}


def test_a_stall_long_enough_to_hit_the_deadline_condemns_the_range(cluster, monkeypatch):
    """The run-ending shape: every attempt stalls, so every attempt "times out".

    A timeout is terminal, so the first stall condemns the range outright -- and
    a condemned range fails the mission.
    """
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()

    # Keep the stall going until the range settles one way or the other. Four
    # passes is more than the 2-attempt timeout budget, so if the deadline is
    # counting Pending time this reaches the condemned state.
    for attempt in (1, 2, 3, 4):
        if '300' in cluster.completed() or '300' in cluster.failed():
            break
        _DeadlineController.run_attempt(cluster, 300, pending_seconds=900,
                                        running_seconds=100, finishes='succeeded')
        cluster.finalize(300, attempt)
        cluster.reconcile()

    # A deadline that is reached is reported, whatever consumed it. At a 12h
    # ceiling, a pod that spent the whole budget Pending is a cluster that
    # cannot run this mission -- worth failing on, not worth retrying into.
    assert '300' in cluster.failed(), (
        "a range that burned its entire deadline must be reported, not retried")
    assert '300' not in cluster.completed()


def test_a_fleet_wide_stall_that_reaches_the_deadline_is_reported_not_retried(cluster, monkeypatch):
    """The outage hits every range at once, not one of them."""
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    monkeypatch.setattr(config, 'PARALLELISM', 3)
    cluster.reconcile()
    assert sorted(cluster.jobs()) == ['pc-r100-a1', 'pc-r200-a1', 'pc-r300-a1']

    for end in (300, 200, 100):
        _DeadlineController.run_attempt(cluster, end, pending_seconds=1200,
                                        running_seconds=60, finishes='succeeded')
        cluster.finalize(end, 1)
    cluster.reconcile()

    # Every range burned its whole deadline, so every one is reported. A fleet
    # that cannot schedule for the length of the budget is a cluster problem the
    # mission must surface, not retry into.
    assert sorted(cluster.failed()) == ['100', '200', '300']
    assert cluster.completed() == {}


def test_an_attempt_that_really_hangs_is_still_killed_by_the_deadline(cluster, monkeypatch):
    """The deadline must keep biting -- a fix that just removes it is not a fix.

    Green before and after: a range that genuinely runs past its budget is
    killed, retried once, and then condemned as a timeout with evidence.
    """
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()

    for attempt in (1, 2):
        outcome = _DeadlineController.run_attempt(
            cluster, 300, pending_seconds=10, running_seconds=900, finishes='succeeded')
        assert outcome == 'timeout', "a 900s attempt escaped its 600s deadline"
        cluster.finalize(300, attempt)
        cluster.reconcile()

    assert cluster.failed()['300']['outcome'] == 'timeout'
    # Terminal on the FIRST deadline: retrying a wedged range just spends the
    # deadline again. Was 2 when a timeout was retryable.
    assert cluster.failed()['300']['attempts'] == 1
    assert cluster.completed() == {}


# --- B: a Job deadline must not overwrite the pod's own verdict --------------

def test_an_oom_inside_a_deadline_exceeded_job_escalates_memory(cluster, monkeypatch):
    """The kubelet said OOMKilled. The Job said "ran too long". Both are true.

    Only one of them tells you what to do about it. Filing this as a timeout
    means the retry goes out at the same memory limit that just killed it.
    """
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()
    cluster.advance(300, 'oom')
    _job_hit_its_deadline(cluster, 300)

    cluster.reconcile()

    # The pod's own record is unambiguous and durable -- reconcile simply
    # ignored it.
    assert records.read_outcome('300', 1)['outcome'] == 'oom'
    assert 'pc-r300-a2' in cluster.jobs()
    resources = _memory(cluster, 'pc-r300-a2')
    assert resources.requests['memory'] == '13824Mi', (
        "the retry went out at the same limit that OOM-killed it: the Job's "
        "DeadlineExceeded overwrote the kubelet's OOMKilled")
    assert resources.requests['memory'] == '13824Mi'


def test_two_ooms_inside_deadline_exceeded_jobs_do_not_condemn_the_range(cluster, monkeypatch):
    """An OOM gets the range budget (5). A timeout gets 2. Misfiling ends the run."""
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()

    for attempt in (1, 2):
        cluster.advance(300, 'oom')
        _job_hit_its_deadline(cluster, 300, attempt)
        cluster.finalize(300, attempt)
        cluster.reconcile()

    assert cluster.failed() == {}, (
        "two OOMs condemned the range at the 2-attempt timeout budget instead "
        "of retrying on the 5-attempt range budget")
    assert 'pc-r300-a3' in cluster.jobs()
    # Two rungs climbed, capped at MAX_MEM (48Gi).
    assert _memory(cluster, 'pc-r300-a3').requests['memory'] == '20736Mi'


def test_a_disruption_inside_a_deadline_exceeded_job_keeps_its_own_budget(cluster, monkeypatch):
    """Spot reclaim is the cluster's fault, and gets MAX_DISRUPTION_ATTEMPTS (20).

    A node drained near the end of a long attempt trips the Job deadline on the
    way out, so the two signals arrive together constantly on spot.
    """
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()

    for attempt in (1, 2):
        cluster.advance(300, 'disrupted')
        _job_hit_its_deadline(cluster, 300, attempt)
        cluster.finalize(300, attempt)
        cluster.reconcile()

    assert records.read_outcome('300', 1)['outcome'] == 'disrupted'
    assert cluster.failed() == {}, (
        "two spot evictions condemned the range: the Job's DeadlineExceeded "
        "downgraded them to the 2-attempt timeout budget")
    assert 'pc-r300-a3' in cluster.jobs()
    # An eviction says nothing about how much memory the range wants.
    assert _memory(cluster, 'pc-r300-a3').requests['memory'] == config.REQ_MEM


def test_an_ephemeral_eviction_inside_a_deadline_exceeded_job_still_grows_the_disk(cluster, monkeypatch):
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    monkeypatch.setattr(config, 'LIM_EPHEMERAL', '40Gi')
    monkeypatch.setattr(config, 'REQ_EPHEMERAL', '40Gi')
    cluster.reconcile()
    cluster.advance(300, 'ephemeral')
    _job_hit_its_deadline(cluster, 300)

    cluster.reconcile()

    assert records.read_outcome('300', 1)['outcome'] == 'ephemeral'
    assert 'pc-r300-a2' in cluster.jobs()
    grown = _memory(cluster, 'pc-r300-a2').limits['ephemeral-storage']
    assert grown != '40Gi', (
        "the retry went out at the same ephemeral-storage limit that evicted "
        "it: the Job's DeadlineExceeded overwrote the kubelet's eviction")


def test_a_deadline_kill_that_drained_to_exit_three_is_still_a_timeout(cluster, monkeypatch):
    """The intended exception, which the ranking must not undo.

    A deadline kill SIGTERMs stellar-core, which drains and exits 3 -- the pod
    verdict reads a plain `failed`, and nothing on the pod says a deadline was
    involved. Here the Job genuinely is the better source, so it must still win.
    Green before and after.
    """
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()

    for attempt in (1, 2):
        cluster.advance(300, 'timeout')
        cluster.finalize(300, attempt)
        cluster.reconcile()

    assert cluster.failed()['300']['outcome'] == 'timeout', (
        "an exit-3 deadline kill is no longer recognised as a timeout")
    assert cluster.failed()['300']['attempts'] == 1
