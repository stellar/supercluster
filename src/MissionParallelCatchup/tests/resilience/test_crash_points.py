"""Crash the monitor mid-pass, at every side-effect boundary, and restart it.

reconcile() is a reconciler: it must derive everything it needs from Kubernetes
plus the files on its own volume, so a process that dies halfway through a pass
and comes back with a zeroed in-memory state must converge to the same place.

The side-effect boundaries inside one pass, in order, are:

  dispatch      create_namespaced_job -> `created`/`capacity`/in_progress
  success       completed[end] = ...  -> save_progress -> release_pvc -> reap
  backfill      completed[end].update(late) -> save_progress -> reap
  retry         save_verdict -> create attempt N+1 -> delete attempt N

Every test here kills the process at one of those arrows and then restarts it
with a fresh state dict -- the same thing a pod replacement does -- and asserts
on observed cluster state and the durable record: every range recorded exactly
once, no PVC left behind, no Job left orphaned, no completed range re-run.

Nothing is asserted about the source text; the injections wrap the fake API or
a single job_monitor function, and everything checked afterwards is either a
file on the volume or an object in the fake cluster.
"""

import pytest
from kubernetes.client.rest import ApiException

import fake_k8s
import config
import records
import job_monitor as jm


TOTAL_RANGES = 3          # conftest's DEFAULT_CONFIG generates 300/200/100


class Crash(RuntimeError):
    """A hard process death -- deliberately NOT an ApiException.

    The monitor handles ApiException in several places; a crash is the thing it
    cannot handle, and is what a SIGKILL, an OOM or a node eviction looks like
    from inside a pass.
    """


# --- injection helpers -------------------------------------------------------

def crash_before(monkeypatch, target, name, times=1):
    """Die on the way INTO `target.name` -- the effect never happens."""
    real = getattr(target, name)
    left = {'n': times}

    def wrapper(*args, **kwargs):
        if left['n'] > 0:
            left['n'] -= 1
            raise Crash(f"crash before {name}")
        return real(*args, **kwargs)

    monkeypatch.setattr(target, name, wrapper)
    return left


def crash_after(monkeypatch, target, name, times=1, match=None):
    """Die on the way OUT of `target.name` -- the effect happened, the caller
    never learned about it. This is the boundary that can duplicate work."""
    real = getattr(target, name)
    left = {'n': times}

    def wrapper(*args, **kwargs):
        result = real(*args, **kwargs)
        if left['n'] > 0 and (match is None or match(*args, **kwargs)):
            left['n'] -= 1
            raise Crash(f"crash after {name}")
        return result

    monkeypatch.setattr(target, name, wrapper)
    return left


def restart(cluster):
    """Replace the monitor process: fresh in-memory state, same volume+cluster.

    Identical to the dict reconcile_loop() builds on entry, so a
    restarted monitor starts from exactly what the shipped loop starts from.
    """
    cluster.state = {'owner': None, 'replayed': set(), 'max_completed': 0,
                     'halted': False, 'counted': {}}
    return cluster


# --- driving -----------------------------------------------------------------

def split(job_name):
    """'pc-r300-a2' -> (300, 2)"""
    stem, _, attempt = job_name.rpartition('-a')
    return int(stem.rsplit('-r', 1)[1]), int(attempt)


def finish_live_jobs(cluster, outcome='succeeded', finalize=True):
    """Take every not-yet-terminal Job to a terminal state, as the cluster would."""
    touched = []
    for name in sorted(cluster.jobs()):
        end, attempt = split(name)
        status = cluster.k8s.job(name).status
        if status and (status.succeeded or status.failed):
            continue
        cluster.advance(end, outcome, attempt)
        if finalize:
            cluster.finalize(end, attempt, tx_apply=1.0,
                             peaks={'peakAnonBytes': 1024})
        touched.append(name)
    return touched


def run_to_quiescence(cluster, passes=15):
    """Succeed everything still in flight until the run drains (or we give up)."""
    for _ in range(passes):
        finish_live_jobs(cluster)
        cluster.reconcile()
        if len(cluster.completed()) == TOTAL_RANGES and not cluster.jobs():
            break
    return cluster


def assert_converged(cluster):
    """The end state of a healthy run, whatever happened on the way there."""
    completed = cluster.completed()
    assert sorted(completed) == ['100', '200', '300'], completed
    assert cluster.failed() == {}
    # Every range recorded once and only once -- a dict cannot hold a duplicate
    # key, so the observable form of "counted twice" is a re-run: a second
    # attempt for a range that had already been recorded.
    for end, record in completed.items():
        assert record['attempts'] == 1, (end, record)
    assert cluster.jobs() == [], f"orphaned Jobs: {cluster.jobs()}"
    assert cluster.pvcs() == [], f"leaked PVCs: {cluster.pvcs()}"


def creates_of(cluster, name):
    return cluster.calls.names(verb='create', kind='job').count(name)


# --- dispatch boundary -------------------------------------------------------

def test_crash_after_create_before_the_range_is_tracked(cluster, monkeypatch):
    """create_namespaced_job returned, then the process died.

    The Job exists and nobody recorded that it does. A restart must find it by
    LIST and adopt it, not dispatch the range a second time.
    """
    crash_after(monkeypatch, cluster.k8s.batch_v1, 'create_namespaced_job')

    with pytest.raises(Crash):
        cluster.reconcile()

    # The Job that the dying pass created is real and running.
    assert cluster.jobs() == ['pc-r300-a1']

    restart(cluster)
    result = cluster.reconcile()

    # Adopted, not recreated: one create call ever for this name, and the
    # restarted pass counts it against capacity instead of dispatching a third.
    assert creates_of(cluster, 'pc-r300-a1') == 1
    assert cluster.jobs() == ['pc-r200-a1', 'pc-r300-a1']
    assert sorted(result['in_progress']) == ['200/420', '300/420']
    assert result['created'] == 1

    run_to_quiescence(cluster)
    assert_converged(cluster)


def test_crash_after_pvc_create_before_job_create(cluster, monkeypatch):
    """ensure_pvc() ran, the Job create never did. The volume must be reused."""
    crash_after(monkeypatch, cluster.k8s.core_v1,
                'create_namespaced_persistent_volume_claim')

    with pytest.raises(Crash):
        cluster.reconcile()

    assert cluster.pvcs() == ['pc-data-r300']
    assert cluster.jobs() == []

    restart(cluster)
    cluster.reconcile()

    # One volume for the range, not two, and the Job now mounts it.
    assert cluster.calls.names(verb='create', kind='pvc').count('pc-data-r300') == 1
    job = cluster.k8s.job('pc-r300-a1')
    claim = job.spec.template.spec.volumes[0].persistent_volume_claim
    assert claim.claim_name == 'pc-data-r300'

    run_to_quiescence(cluster)
    assert_converged(cluster)


# --- success boundary: record -> save_progress -> release_pvc -> reap ---------

def test_crash_before_save_progress_records_the_range_exactly_once(cluster,
                                                                   monkeypatch):
    """completed[end] existed only in memory. Nothing durable, so redo it."""
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.5, peaks={'peakAnonBytes': 7})

    crash_before(monkeypatch, jm, 'save_progress')
    with pytest.raises(Crash):
        cluster.reconcile()

    # Nothing was written, so nothing is claimed -- and crucially the Job was
    # NOT reaped, because the reap sits after the write.
    assert cluster.progress() == {}
    assert 'pc-r300-a1' in cluster.jobs()

    restart(cluster)
    cluster.reconcile()

    record = cluster.completed()['300']
    assert record['attempts'] == 1
    assert record['txApply'] == 1.5
    assert record['peakAnonBytes'] == 7
    assert creates_of(cluster, 'pc-r300-a1') == 1
    assert 'pc-r300-a2' not in cluster.jobs(), "a recorded range must never re-run"

    run_to_quiescence(cluster)
    assert_converged(cluster)


def test_crash_after_save_progress_before_release_pvc_does_not_leak_the_volume(
        cluster, monkeypatch):
    """The record is durable and the volume is not yet freed.

    A completed range has nothing left to resume, so its PVC is dead weight --
    40Gi of gp3 apiece, which is what put 79 TiB on ssc-test. The release must
    therefore be reached on a LATER pass too, because the pass that would have
    done it is never repeated: the record already exists.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.5, peaks={'peakAnonBytes': 7})

    crash_before(monkeypatch, jm, 'release_pvc')
    with pytest.raises(Crash):
        cluster.reconcile()

    assert '300' in cluster.progress()['completed']
    assert 'pc-data-r300' in cluster.pvcs()

    restart(cluster)
    for _ in range(3):
        cluster.reconcile()

    assert 'pc-data-r300' not in cluster.pvcs(), (
        "the volume of a range recorded complete before the crash was never "
        "released; only the first-sight branch releases it and that branch "
        "never runs again")

    run_to_quiescence(cluster)
    assert_converged(cluster)


def test_crash_after_release_pvc_before_the_reap_does_not_orphan_the_job(
        cluster, monkeypatch):
    """The window that leaves a Job with no owner.

    The range is recorded, its volume is gone, and its Job is still standing.
    Nothing in the cluster will ever ask about that Job again -- it is not in
    flight, it is not retryable, and its range is complete -- so the reconciler
    is the only thing that can clean it up.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.5, peaks={'peakAnonBytes': 7})

    crash_before(monkeypatch, jm, 'reap_range_jobs')
    with pytest.raises(Crash):
        cluster.reconcile()

    assert '300' in cluster.progress()['completed']
    assert 'pc-data-r300' not in cluster.pvcs()
    assert 'pc-r300-a1' in cluster.jobs(), "precondition: the ownerless Job"

    restart(cluster)
    for _ in range(3):
        cluster.reconcile()

    assert 'pc-r300-a1' not in cluster.jobs(), (
        "a Job whose range is already recorded complete was left standing "
        "forever; it inflates every later LIST and its pod holds a node")
    # ...and cleaning it up must not have cost anything: the range stays
    # recorded once, with its measurements.
    assert cluster.completed()['300']['txApply'] == 1.5
    assert cluster.completed()['300']['attempts'] == 1

    run_to_quiescence(cluster)
    assert_converged(cluster)


def test_crash_after_the_reap_leaves_nothing_behind(cluster, monkeypatch):
    """Last arrow in the success path: everything is done, the pass just dies."""
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.5, peaks={'peakAnonBytes': 7})

    crash_after(monkeypatch, jm, 'reap_range_jobs')
    with pytest.raises(Crash):
        cluster.reconcile()

    assert 'pc-r300-a1' not in cluster.jobs()
    assert 'pc-data-r300' not in cluster.pvcs()

    restart(cluster)
    cluster.reconcile()

    # The slot the reaped range freed is refilled, and the range is not redone.
    assert cluster.completed()['300']['attempts'] == 1
    assert creates_of(cluster, 'pc-r300-a1') == 1
    assert 'pc-r100-a1' in cluster.jobs()

    run_to_quiescence(cluster)
    assert_converged(cluster)


# --- backfill boundary -------------------------------------------------------

def test_crash_mid_backfill_backfills_on_a_later_pass(cluster, monkeypatch):
    """The record was written before the collector finalized; a crash in the
    middle of the catch-up write must not make the measurements unreachable."""
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.reconcile()                       # recorded with nothing to read yet

    record = cluster.completed()['300']
    assert record['txApply'] is None
    assert 'peakAnonBytes' not in record
    assert 'pc-r300-a1' in cluster.jobs(), "not finalized, so not reaped"

    # The collector lands, and the monitor dies on the backfill write.
    cluster.finalize(300, 1, tx_apply=2.5, peaks={'peakAnonBytes': 99})
    crash_after(monkeypatch, jm, 'save_progress')
    with pytest.raises(Crash):
        cluster.reconcile()

    restart(cluster)
    cluster.reconcile()

    record = cluster.completed()['300']
    assert record['txApply'] == 2.5
    assert record['peakAnonBytes'] == 99
    assert record['attempts'] == 1
    assert 'pc-r300-a1' not in cluster.jobs(), "finalized and backfilled: reap it"

    run_to_quiescence(cluster)
    assert_converged(cluster)


# --- retry boundary: verdict -> create N+1 -> delete N -----------------------

def test_crash_after_the_successor_exists_before_the_predecessor_is_deleted(
        cluster, monkeypatch):
    """Both attempts are live for a moment. The pass that dies there must not
    leave the loser standing once the range finishes."""
    cluster.reconcile()
    cluster.advance(300, 'incomplete')
    cluster.finalize(300, 1, archive='fetch_fault')

    crash_after(monkeypatch, cluster.k8s.batch_v1, 'create_namespaced_job',
                match=lambda ns, body, **kw: body.metadata.name == 'pc-r300-a2')
    with pytest.raises(Crash):
        cluster.reconcile()

    assert 'pc-r300-a1' in cluster.jobs() and 'pc-r300-a2' in cluster.jobs()

    restart(cluster)
    cluster.reconcile()

    # The dead a-1 must never be re-classified into a third attempt: live[]
    # keys on the highest attempt for the range.
    assert 'pc-r300-a3' not in cluster.jobs()
    assert creates_of(cluster, 'pc-r300-a2') == 1
    assert records._cause_count('300', 2, ('fetch-fault',)) == 1, \
        "attempt 1 must be counted once, not once per pass that saw it"

    cluster.advance(300, 'succeeded', attempt=2)
    cluster.finalize(300, 2, tx_apply=1.0, peaks={'peakAnonBytes': 1024})
    cluster.reconcile()

    assert cluster.completed()['300']['attempts'] == 2
    assert 'pc-r300-a1' not in cluster.jobs(), "the loser must be swept too"
    assert 'pc-r300-a2' not in cluster.jobs()
    assert 'pc-data-r300' not in cluster.pvcs()


def test_crash_between_the_verdict_and_the_retry_create(cluster, monkeypatch):
    """The verdict is on disk and the successor was never created.

    The verdict is what spends the range's budget, so replaying the same failed
    attempt after a restart must not spend it a second time -- and for an OOM,
    must not climb a second escalation rung either.
    """
    cluster.reconcile()
    cluster.advance(300, 'oom')

    crash_before(monkeypatch, cluster.k8s.batch_v1, 'create_namespaced_job')
    with pytest.raises(Crash):
        cluster.reconcile()

    assert records._verdict_of('300', 1) == 'oom'
    assert 'pc-r300-a1' in cluster.jobs(), \
        "the predecessor must survive: without it the range restarts at attempt 1"

    restart(cluster)
    cluster.reconcile()

    assert 'pc-r300-a2' in cluster.jobs()
    resources = (cluster.k8s.job('pc-r300-a2')
                 .spec.template.spec.containers[0].resources)
    # One OOM seen, so exactly one rung: 24000Mi * 1.5. Two would mean the
    # replayed attempt was counted twice.
    assert resources.requests['memory'] == '13824Mi'
    assert records._cause_count('300', 1, ('oom', 'failed')) == 1
    assert cluster.failed() == {}


# --- API errors on create ----------------------------------------------------

def test_409_on_dispatch_is_benign_and_does_not_double_record(cluster):
    """AlreadyExists is the dispatch mutex, not an error."""
    cluster.k8s.fail_next['create job'] = fake_k8s.api_exception(
        409, 'Conflict', 'jobs.batch "pc-r300-a1" already exists')

    result = cluster.reconcile()          # must not raise

    # Whatever the pass counts, it must not count a Job it did not create...
    assert result['created'] == len(cluster.jobs())
    # ...nor claim anything about the range.
    assert cluster.progress() == {}, "a swallowed 409 must not record anything"
    assert cluster.failed() == {}
    assert 'pc-r300-a1' not in cluster.jobs()

    run_to_quiescence(cluster)
    assert_converged(cluster)
    # Each range ran once: no range was ever dispatched at attempt 2.
    creates = cluster.calls.names(verb='create', kind='job')
    assert sorted(creates) == ['pc-r100-a1', 'pc-r200-a1', 'pc-r300-a1']


def test_409_means_the_slot_is_taken_and_must_not_over_dispatch(cluster,
                                                                monkeypatch):
    """Losing the create race means the Job EXISTS and is in flight.

    The monitor's own comment calls name uniqueness the dispatch mutex, which is
    only true if losing it is treated as "someone else holds this slot". A 409
    that does not spend capacity dispatches PARALLELISM+1 workers -- and at 1024
    parallelism that is a fleet-wide overshoot, not a rounding error.
    """
    real_create = cluster.k8s.batch_v1.create_namespaced_job
    lost = []

    def loser(namespace, body, **kwargs):
        if body.metadata.name == 'pc-r300-a1' and not lost:
            lost.append(body.metadata.name)
            real_create(namespace, body, **kwargs)   # the other writer's object
            raise fake_k8s.api_exception(
                409, 'Conflict', 'jobs.batch "pc-r300-a1" already exists')
        return real_create(namespace, body, **kwargs)

    monkeypatch.setattr(cluster.k8s.batch_v1, 'create_namespaced_job', loser)

    result = cluster.reconcile()

    assert lost, "precondition: the create actually lost the race"
    assert len(cluster.jobs()) <= config.PARALLELISM, (
        f"dispatched {cluster.jobs()} against PARALLELISM={config.PARALLELISM}: "
        "a 409 left the slot looking free")
    assert '300/420' in result['in_progress'], \
        "the range whose Job exists is in flight and must be reported as such"
    assert result['remaining'] == 1, \
        "a range with a running Job is not still waiting to be dispatched"

    run_to_quiescence(cluster)
    assert_converged(cluster)


def test_500_on_dispatch_is_retried_on_a_later_pass(cluster):
    """A server error is not a verdict: the range must survive it."""
    cluster.k8s.fail_next['create job'] = fake_k8s.api_exception(500, 'boom')

    with pytest.raises(ApiException) as err:
        cluster.reconcile()
    assert err.value.status == 500

    assert cluster.jobs() == [], "nothing was created by the aborted pass"
    assert cluster.progress() == {}

    restart(cluster)
    result = cluster.reconcile()

    assert cluster.jobs() == ['pc-r200-a1', 'pc-r300-a1']
    assert result['created'] == 2
    # The volume the aborted pass provisioned is reused, not duplicated.
    assert cluster.calls.names(verb='create', kind='pvc').count('pc-data-r300') == 1

    run_to_quiescence(cluster)
    assert_converged(cluster)


def test_500_on_the_retry_create_does_not_lose_or_double_spend_the_range(cluster):
    """The retry create fails hard. The range keeps its budget and its history."""
    cluster.reconcile()
    cluster.advance(300, 'incomplete')
    cluster.finalize(300, 1, archive='fetch_fault')

    cluster.k8s.fail_next['create job'] = fake_k8s.api_exception(500, 'boom')
    with pytest.raises(ApiException):
        cluster.reconcile()

    assert 'pc-r300-a1' in cluster.jobs(), \
        "deleting the predecessor before the successor exists restarts the range"
    assert cluster.failed() == {}

    restart(cluster)
    cluster.reconcile()

    assert 'pc-r300-a2' in cluster.jobs()
    assert records._cause_count('300', 2, ('fetch-fault',)) == 1
    assert cluster.calls.names(verb='create', kind='pvc').count('pc-data-r300') == 1

    cluster.advance(300, 'succeeded', attempt=2)
    cluster.finalize(300, 2, tx_apply=1.0, peaks={'peakAnonBytes': 1024})
    cluster.reconcile()
    assert cluster.completed()['300']['attempts'] == 2


def test_a_range_that_exhausts_its_budget_across_crashes_fails_once(cluster,
                                                                    monkeypatch):
    """Budgets are spent by durable verdicts, so restarts must not stretch or
    shrink them. Five attempts, a crash before each retry create.

    An exit-3 fetch fault is an unreachable archive, so it spends the
    environmental budget; the cap is lowered here rather than looping to the
    configured one.
    """
    # A fetch fault spends its own budget, so that is the one to lower.
    monkeypatch.setitem(config.ATTEMPT_BUDGETS, 'fetch-fault', 5)
    cluster.reconcile()
    for attempt in range(1, config.ATTEMPT_BUDGETS['fetch-fault'] + 1):
        cluster.advance(300, 'incomplete', attempt=attempt)
        cluster.finalize(300, attempt, archive='fetch_fault')
        crash_before(monkeypatch, cluster.k8s.batch_v1, 'create_namespaced_job')
        with pytest.raises(Crash):
            cluster.reconcile()
        restart(cluster)
        cluster.reconcile()

    assert cluster.failed()['300']['attempts'] == config.ATTEMPT_BUDGETS['fetch-fault']
    assert cluster.failed()['300']['outcome'] == 'fetch-fault'
    # Exactly that many Jobs were ever created for the range, despite five
    # crashed passes replaying the same failed attempts.
    creates = cluster.calls.names(verb='create', kind='job')
    assert sorted(n for n in creates if n.startswith('pc-r300-')) == [
        f'pc-r300-a{n}' for n in range(1, config.ATTEMPT_BUDGETS['fetch-fault'] + 1)]


# --- end to end --------------------------------------------------------------

def test_the_run_converges_with_a_crash_at_every_boundary(cluster, monkeypatch):
    """One crash at each arrow, spread across one run, restarting every time."""
    # 1. after the Job create, before the range is tracked
    crash_after(monkeypatch, cluster.k8s.batch_v1, 'create_namespaced_job')
    with pytest.raises(Crash):
        cluster.reconcile()
    restart(cluster)
    cluster.reconcile()

    # 2. after the record, before save_progress
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.0, peaks={'peakAnonBytes': 1024})
    crash_before(monkeypatch, jm, 'save_progress')
    with pytest.raises(Crash):
        cluster.reconcile()
    restart(cluster)
    cluster.reconcile()

    # 3. after save_progress, before release_pvc
    cluster.advance(200, 'succeeded')
    cluster.finalize(200, 1, tx_apply=1.0, peaks={'peakAnonBytes': 1024})
    crash_before(monkeypatch, jm, 'release_pvc')
    with pytest.raises(Crash):
        cluster.reconcile()
    restart(cluster)
    cluster.reconcile()

    # 4. after release_pvc, before the reap
    cluster.advance(100, 'succeeded')
    cluster.finalize(100, 1, tx_apply=1.0, peaks={'peakAnonBytes': 1024})
    crash_before(monkeypatch, jm, 'reap_range_jobs')
    with pytest.raises(Crash):
        cluster.reconcile()
    restart(cluster)

    run_to_quiescence(cluster)
    assert_converged(cluster)

    # Three ranges, three Jobs, ever. Nothing was replayed by a restart.
    assert sorted(cluster.calls.names(verb='create', kind='job')) == [
        'pc-r100-a1', 'pc-r200-a1', 'pc-r300-a1']
    assert sorted(cluster.calls.names(verb='create', kind='pvc')) == [
        'pc-data-r100', 'pc-data-r200', 'pc-data-r300']
    # ...and every measurement survived the crashes.
    for end in ('100', '200', '300'):
        assert cluster.completed()[end]['txApply'] == 1.0
        assert cluster.completed()[end]['peakAnonBytes'] == 1024


def test_a_restart_between_every_single_pass_changes_nothing(cluster):
    """The control: the same run with a fresh process for every pass."""
    for _ in range(12):
        restart(cluster)
        finish_live_jobs(cluster)
        cluster.reconcile()
        if len(cluster.completed()) == TOTAL_RANGES and not cluster.jobs():
            break

    assert_converged(cluster)
    assert sorted(cluster.calls.names(verb='create', kind='job')) == [
        'pc-r100-a1', 'pc-r200-a1', 'pc-r300-a1']
