"""Restart invisibility: a monitor restart between any two reconcile passes
must change nothing an observer can see.

The monitor is a reconciler. Every decision it makes has to be derivable from
the Kubernetes objects plus the durable files on the logs volume; anything it
keeps only in RAM is lost the moment the pod is rescheduled, and a 10-hour run
gets rescheduled. `restart()` below is the whole trick: it discards exactly
what a process death discards -- the `state` dict reconcile() carries across
passes, and the module-level owner cache -- and keeps exactly what survives,
the logs volume and the cluster.

Everything here asserts on observed state: the durable progress record, the
live Job/Pod objects, and the API call log. Nothing reads job_monitor's source.
"""

import json
import os
import random

import pytest

import job_monitor as jm

# The states the fuzz drives Jobs through. A real run is dominated by success,
# with spot evictions the most common failure, then OOM, then a hung archive
# fetch tripping the attempt deadline. `unknown` is the restart's own signature
# -- the Job failed while the monitor was down and the pod was reaped with it,
# so nothing is left to classify from.
DRIVE_STATES = ('succeeded', 'disrupted', 'oom', 'timeout', 'unknown')
DRIVE_WEIGHTS = (6, 3, 2, 2, 1)

# 30 seeds x 24 passes runs in ~9s. RESTART_FUZZ_SEEDS / RESTART_FUZZ_PASSES
# widen it for a soak without editing the file -- 400 x 40 takes ~2.5 minutes.
PASSES = int(os.getenv('RESTART_FUZZ_PASSES', 24))
SEEDS = list(range(int(os.getenv('RESTART_FUZZ_SEEDS', 30))))


# --- the restart ------------------------------------------------------------

def restart(cluster):
    """Simulate the monitor process dying and being rescheduled.

    Gone: the in-memory `state` dict (owner reference, histogram replay guard,
    the monotonic-progress high-water mark, the counter deltas) and the
    module-level owner cache. Kept: the logs volume and every object in the
    cluster -- which between them are the only inputs a reconciler is allowed
    to have.
    """
    cluster.state = {'owner': None, 'replayed': set(), 'max_completed': 0,
                     'halted': False, 'counted': {}}
    jm._progress_owner.clear()
    jm.PROFILE = None


# --- cluster inspection -----------------------------------------------------

def _terminal(job):
    st = job.status
    return bool(st and (st.succeeded or st.failed))


def _jobs_by_range(cluster):
    """range-end (str) -> [(attempt, job)], from the cluster, not from state."""
    out = {}
    for name in cluster.jobs():
        job = cluster.k8s.job(name)
        labels = job.metadata.labels or {}
        end = labels.get(jm.LABEL_RANGE)
        attempt = int(labels.get(jm.LABEL_ATTEMPT, 1))
        out.setdefault(end, []).append((attempt, job))
    return out


def _range_of_job(name):
    """'pc-r1200-a3' -> ('1200', 3)."""
    stem, _, attempt = name.rpartition('-a')
    return stem.split('-r', 1)[1], int(attempt)


# --- the invariants ---------------------------------------------------------

class Ledger:
    """Cross-pass bookkeeping the invariants need (high-water marks, first
    sighting of a completion, every pod ever seen)."""

    def __init__(self, ends):
        self.ends = set(ends)
        self.total = len(ends)
        self.dispatched = set()        # every range that has ever had a Job
        self.recorded_at = {}          # end -> len(calls) when first completed
        self.peaks = {}                # end -> {field: high-water value}
        self.pods = {}                 # (end, attempt) -> pod name


def check(cluster, result, led, where):
    """Assert I1..I6 against observed state. `where` names the pass."""
    progress = cluster.progress()
    completed = set(progress.get('completed', {}))
    failed = set(progress.get('failed', {}))
    by_range = _jobs_by_range(cluster)

    for end, entries in by_range.items():
        led.dispatched.add(end)
    for name in cluster.calls.names(verb='create', kind='job'):
        led.dispatched.add(_range_of_job(name)[0])

    # A Job that has succeeded or failed is a record, not work in flight. The
    # monitor deliberately leaves a finished Job standing until the collector
    # finalizes it, so "live" has to mean unfinished, not merely present.
    live = {end for end, entries in by_range.items()
            if any(not _terminal(j) for _, j in entries)}

    # -- I1: exactly one of completed / failed / live ------------------------
    assert completed <= led.ends, f"{where}: completed has unknown ranges {completed - led.ends}"
    assert failed <= led.ends, f"{where}: failed has unknown ranges {failed - led.ends}"
    assert live <= led.ends, f"{where}: live has unknown ranges {live - led.ends}"
    assert not (completed & failed), \
        f"{where}: ranges both completed and failed: {sorted(completed & failed)}"
    assert not (completed & live), \
        f"{where}: completed ranges with work still in flight: {sorted(completed & live)}"
    assert not (failed & live), \
        f"{where}: failed ranges with work still in flight: {sorted(failed & live)}"
    # Never zero: a range that has been dispatched must stay accounted for.
    # Undispatched ranges are simply queued behind PARALLELISM -- that is the
    # fourth, legitimate bucket, and it only ever shrinks.
    lost = led.dispatched - completed - failed - live
    assert not lost, (f"{where}: dispatched ranges accounted for nowhere -- "
                      f"no record and no live Job: {sorted(lost)}")

    # -- I2: at most one live Job per range ----------------------------------
    for end, entries in by_range.items():
        unfinished = [a for a, j in entries if not _terminal(j)]
        assert len(unfinished) <= 1, \
            f"{where}: range {end} has {len(unfinished)} live Jobs (attempts {unfinished})"

    # ...and the run never runs wider than it was told to. A restart that
    # forgot what was in flight would show up here first.
    assert len(result['in_progress']) <= jm.PARALLELISM, \
        f"{where}: {len(result['in_progress'])} in flight over PARALLELISM {jm.PARALLELISM}"

    # A completed range has nothing left to resume, so its volume is gone --
    # 79 TiB of orphaned gp3 is what this costs when it regresses.
    held = {end for end in completed
            if f"{cluster.run_name}-data-r{end}" in cluster.pvcs()}
    assert not held, f"{where}: completed ranges still holding a PVC: {sorted(held)}"

    # -- I3: a completed range is never re-dispatched ------------------------
    for end in completed:
        led.recorded_at.setdefault(end, len(cluster.calls))
    for index, call in enumerate(cluster.calls):
        if call.verb != 'create' or call.kind != 'job':
            continue
        end, attempt = _range_of_job(call.name)
        mark = led.recorded_at.get(end)
        if mark is not None and index >= mark:
            raise AssertionError(
                f"{where}: range {end} was re-dispatched ({call.name}) after it "
                f"was recorded complete")

    # -- I4: remaining is sane -----------------------------------------------
    assert result['remaining'] >= 0, f"{where}: remaining went negative: {result}"
    drained = result['remaining'] == 0 and not result['in_progress']
    assert drained == (len(completed) + len(failed) == led.total), (
        f"{where}: remaining/in_progress say drained={drained} but the record "
        f"has {len(completed)} completed + {len(failed)} failed of {led.total}")

    # -- I5: recorded peaks are a high-water mark ----------------------------
    for end, record in (progress.get('completed') or {}).items():
        seen = led.peaks.setdefault(end, {})
        for field in jm.PEAK_FIELDS:
            value = record.get(field)
            if value is None:
                continue
            previous = seen.get(field)
            assert previous is None or value >= previous, (
                f"{where}: range {end} peak {field} went backwards "
                f"{previous} -> {value}")
            seen[field] = value

    # -- I6: one pod per (range, attempt) ------------------------------------
    for (_, name), pod in cluster.k8s.pods.items():
        labels = pod.metadata.labels or {}
        end = labels.get(jm.LABEL_RANGE)
        if end is None:
            continue
        key = (end, labels.get(jm.LABEL_ATTEMPT))
        previous = led.pods.setdefault(key, name)
        assert previous == name, (
            f"{where}: range {key[0]} attempt {key[1]} has two distinct pods "
            f"({previous} and {name}) -- the attempt was replayed")


# --- driving the cluster ----------------------------------------------------

def collector_catches_up(cluster, rng):
    """Write what the log-collector sidecar writes, for some finished attempts.

    Not all of them: the monitor's reap is gated on the .done marker, so
    leaving attempts unfinalized is what keeps finished Jobs standing and
    exercises the backfill path.
    """
    for end, entries in _jobs_by_range(cluster).items():
        for attempt, job in entries:
            if not _terminal(job) or os.path.exists(jm.done_path(end, attempt)):
                continue
            if rng.random() < 0.35:
                continue
            cluster.finalize(
                end, attempt,
                tx_apply=round(rng.uniform(0.0, 5.0), 4),
                peaks={'peakAnonBytes': rng.randrange(1, 20) * 10 ** 8,
                       'peakRssBytes': rng.randrange(1, 20) * 10 ** 8},
                resumed=(attempt > 1 and rng.random() < 0.5),
                attempt_seconds=round(rng.uniform(10.0, 300.0), 2))


def cluster_moves(cluster, rng):
    """Drive live Jobs to terminal states, the way the cluster would."""
    for end, entries in _jobs_by_range(cluster).items():
        for attempt, job in entries:
            if _terminal(job) or rng.random() < 0.45:
                continue
            state = rng.choices(DRIVE_STATES, weights=DRIVE_WEIGHTS)[0]
            cluster.advance(int(end), state, attempt=attempt)


@pytest.fixture
def big_run(cluster, monkeypatch):
    """Twelve ranges, four at a time -- enough queueing that a dropped range
    would be silently re-dispatched rather than obviously stuck."""
    monkeypatch.setattr(jm, 'LATEST_LEDGER_NUM', 1200)
    monkeypatch.setattr(jm, 'PARALLELISM', 4)
    return cluster


# --- the fuzz ---------------------------------------------------------------

def _observable(cluster):
    """Everything a restart is allowed to leave untouched."""
    return (cluster.progress(), cluster.jobs(), cluster.pvcs())


@pytest.mark.parametrize('seed', SEEDS)
def test_restart_is_invisible_under_fuzz(big_run, seed):
    cluster = big_run
    rng = random.Random(seed)
    ends = [str(end) for end, _ in jm.generate_ranges()]
    assert len(ends) == 12
    led = Ledger(ends)

    # One guaranteed restart while the run is still busy, plus a scattering of
    # others -- a reconciler should survive any number of them, anywhere.
    restarts = {rng.randrange(1, 12)}
    restarts |= {i for i in range(1, PASSES) if rng.random() < 0.12}

    for i in range(PASSES):
        if i in restarts:
            restart(cluster)
        result = cluster.reconcile()
        where = f"seed={seed} pass={i}{' (post-restart)' if i in restarts else ''}"
        check(cluster, result, led, where)
        # The restart must not trip the anti-tamper halt: max_completed comes
        # back as 0 and climbs again from the record on disk.
        assert cluster.state['halted'] is False, \
            f"{where}: dispatch halted -- progress read as going backwards"

        if i in restarts:
            # The lens at its sharpest: with nothing changing in the cluster,
            # restarting and reconciling again must be a no-op. Anything that
            # moves here was being decided from memory.
            before = _observable(cluster)
            restart(cluster)
            shadow = cluster.reconcile()
            check(cluster, shadow, led, f"{where} (shadow)")
            assert _observable(cluster) == before, (
                f"{where}: a restart + reconcile with an unchanged cluster "
                f"moved something")
            assert shadow['created'] == 0, \
                f"{where}: shadow pass dispatched {shadow['created']} Job(s)"
            # ...and it reports the same run, not just leaves the same objects.
            assert (shadow['completed'], shadow['remaining'],
                    sorted(shadow['in_progress']), sorted(shadow['failed_ranges'])) == \
                   (result['completed'], result['remaining'],
                    sorted(result['in_progress']), sorted(result['failed_ranges'])), \
                f"{where}: the post-restart summary disagrees: {result} -> {shadow}"

        collector_catches_up(cluster, rng)
        cluster_moves(cluster, rng)

    assert restarts
    # The run has to have gone somewhere, or the fuzz proved nothing.
    progress = cluster.progress()
    assert progress.get('completed'), f"seed={seed}: no range ever completed"
    # Retries have to have actually happened, or the fuzz only exercised the
    # happy path.
    assert any(name.endswith('.verdict') for name in os.listdir(jm.LOG_DIR)), \
        f"seed={seed}: no attempt ever failed"


# --- focused restarts, to localise anything the fuzz turns up ----------------

def test_restart_does_not_redispatch_a_recorded_range(big_run):
    cluster = big_run
    cluster.reconcile()
    for end in ('1200', '1100', '1000', '900'):
        cluster.advance(int(end), 'succeeded')
        cluster.finalize(end, 1, tx_apply=1.0, peaks={'peakRssBytes': 5})
    cluster.reconcile()
    recorded = set(cluster.completed())
    assert recorded == {'1200', '1100', '1000', '900'}
    mark = len(cluster.calls)

    restart(cluster)
    cluster.reconcile()

    assert set(cluster.completed()) >= recorded
    after = [_range_of_job(c.name)[0] for c in cluster.calls[mark:]
             if c.verb == 'create' and c.kind == 'job']
    assert not (set(after) & recorded), \
        f"recorded ranges re-dispatched after restart: {sorted(set(after) & recorded)}"


def test_restart_mid_retry_keeps_the_attempt_number(big_run):
    cluster = big_run
    cluster.reconcile()
    cluster.advance(1200, 'oom')
    cluster.reconcile()
    assert cluster.attempt_of(1200) == 2
    limit = (cluster.k8s.job('pc-r1200-a2')
             .spec.template.spec.containers[0].resources.limits['memory'])

    restart(cluster)
    cluster.advance(1200, 'oom', attempt=2)
    cluster.reconcile()

    # The escalation ladder is counted off the .outcome files on the volume,
    # so the restart must not reset it to the first rung.
    assert cluster.attempt_of(1200) == 3
    escalated = (cluster.k8s.job('pc-r1200-a3')
                 .spec.template.spec.containers[0].resources.limits['memory'])
    assert jm._quantity_bytes(escalated) > jm._quantity_bytes(limit)
    assert cluster.failed() == {}


def test_restart_does_not_reset_a_spent_budget(big_run):
    """MAX_TIMEOUT_ATTEMPTS is 2. Spend one, restart, spend the second: the
    range must be condemned, not handed a fresh budget."""
    cluster = big_run
    cluster.reconcile()
    cluster.advance(1200, 'timeout')
    cluster.reconcile()
    assert cluster.attempt_of(1200) == 2
    assert cluster.failed() == {}

    restart(cluster)
    cluster.advance(1200, 'timeout', attempt=2)
    cluster.reconcile()

    assert cluster.failed()['1200']['outcome'] == 'timeout'
    assert 'pc-r1200-a3' not in cluster.jobs()


def test_restart_does_not_halt_on_its_own_progress(big_run):
    cluster = big_run
    cluster.reconcile()
    cluster.advance(1200, 'succeeded')
    cluster.finalize('1200', 1)
    cluster.reconcile()
    assert '1200' in cluster.completed()

    restart(cluster)
    result = cluster.reconcile()

    assert '1200' in cluster.completed()
    # Dispatch is not frozen: the slot the completion freed was already refilled
    # before the restart, so the run comes back at full width.
    assert len(result['in_progress']) == 4

    # ...and the next completion still pulls a new range in.
    cluster.advance(1100, 'succeeded')
    cluster.finalize('1100', 1)
    assert cluster.reconcile()['created'] == 1


# --- two gaps the fuzz does not reach ---------------------------------------
# Both are held in memory, so both are exactly as durable as the process. They
# are marked xfail(strict) rather than asserted-as-is: the assertions below say
# what the monitor SHOULD do, so they flip to a hard failure the day either gap
# is closed, instead of quietly cementing today's behaviour.

@pytest.mark.xfail(strict=True, reason=(
    "state['max_completed'] is memory-only, so the PROGRESS WENT BACKWARDS "
    "guard is disabled for the life of a fresh process -- the one event it "
    "most needs to survive"))
def test_the_backwards_progress_guard_survives_a_restart(big_run):
    """Destroy the record under a running monitor and it refuses to dispatch.

    Destroy it under a monitor that then restarts and it re-runs the range from
    genesis. Same fault, opposite outcome, decided purely by whether the
    process happened to be the same one.
    """
    cluster = big_run
    cluster.reconcile()
    for end in ('1200', '1100'):
        cluster.advance(int(end), 'succeeded')
        cluster.finalize(end, 1)
    cluster.reconcile()
    assert set(cluster.completed()) == {'1200', '1100'}

    # Both copies of the record go, the way the guard's own log line describes.
    os.remove(jm.PROGRESS_FILE)
    cluster.k8s.core_v1.delete_namespaced_config_map(jm.PROGRESS_CM, cluster.namespace)

    restart(cluster)
    cluster.reconcile()
    # Free a slot so dispatch has capacity to misuse.
    cluster.advance(1000, 'succeeded')
    cluster.finalize('1000', 1)
    mark = len(cluster.calls)
    cluster.reconcile()

    redispatched = [c.name for c in cluster.calls[mark:]
                    if c.verb == 'create' and c.kind == 'job'
                    and _range_of_job(c.name)[0] in {'1200', '1100'}]
    assert cluster.state['halted'] is True
    assert not redispatched, f"already-completed ranges re-dispatched: {redispatched}"


@pytest.mark.xfail(strict=True, reason=(
    "load_progress falls back to the ConfigMap mirror, which _state_only has "
    "stripped of every measurement; the next save_progress then writes that "
    "stripped record back over the authoritative file"))
def test_a_measurement_survives_the_configmap_fallback(big_run):
    """I5, in its purest form: a recorded peak that goes away.

    progress.json becomes unreadable, load_progress falls back to the mirror,
    and range 1200's peakRssBytes / txApply / seconds are gone -- not stale,
    absent -- and then persisted absent.
    """
    cluster = big_run
    cluster.reconcile()
    cluster.advance(1200, 'succeeded')
    cluster.finalize('1200', 1, tx_apply=2.5, peaks={'peakRssBytes': 12345})
    cluster.reconcile()
    assert cluster.completed()['1200']['peakRssBytes'] == 12345

    os.remove(jm.PROGRESS_FILE)
    restart(cluster)
    # Any later completion rewrites the file from the fallback record.
    cluster.advance(1100, 'succeeded')
    cluster.finalize('1100', 1, tx_apply=1.0, peaks={'peakRssBytes': 999})
    cluster.reconcile()

    assert cluster.completed()['1200'].get('peakRssBytes') == 12345
    assert cluster.completed()['1200'].get('txApply') == 2.5


# --- the checker has teeth --------------------------------------------------
# A fuzz run that passes is only worth what its assertions would have caught.
# Each of these breaks one invariant deliberately and requires check() to say
# so; if one of them ever stops failing, the corresponding invariant above has
# gone vacuous.

def test_checker_catches_progress_held_in_memory(big_run, monkeypatch):
    """A monitor that kept `completed` in RAM instead of on the volume.

    Up to the restart it behaves identically -- which is exactly why this has
    to be caught by the restart and not by anything before it.
    """
    cluster = big_run
    cache = {}
    monkeypatch.setattr(jm, 'load_progress', lambda: cache)
    monkeypatch.setattr(jm, 'save_progress', lambda progress: cache.update(progress))
    monkeypatch.setattr(cluster, 'progress', lambda: cache)

    led = Ledger([str(e) for e, _ in jm.generate_ranges()])
    rng = random.Random(1)
    with pytest.raises(AssertionError, match='accounted for nowhere|re-dispatched'):
        for i in range(12):
            if i == 5:
                cache.clear()          # the process died; RAM went with it
                restart(cluster)
            result = cluster.reconcile()
            check(cluster, result, led, f"mutant pass={i}")
            collector_catches_up(cluster, rng)
            cluster_moves(cluster, rng)


def test_checker_catches_two_live_jobs_for_one_range(big_run):
    cluster = big_run
    led = Ledger([str(e) for e, _ in jm.generate_ranges()])
    result = cluster.reconcile()
    check(cluster, result, led, 'mutant pre')

    cluster.k8s.batch_v1.create_namespaced_job(
        cluster.namespace, jm.build_job(1200, 420, 2, cluster.state['owner']))

    with pytest.raises(AssertionError, match='live Jobs'):
        check(cluster, result, led, 'mutant post')


def test_checker_catches_a_peak_going_backwards(big_run):
    cluster = big_run
    led = Ledger([str(e) for e, _ in jm.generate_ranges()])
    cluster.reconcile()
    cluster.advance(1200, 'succeeded')
    cluster.finalize('1200', 1, peaks={'peakRssBytes': 900})
    result = cluster.reconcile()
    check(cluster, result, led, 'mutant pre')

    record = cluster.progress()
    record['completed']['1200']['peakRssBytes'] = 5
    cluster.write(jm.PROGRESS_FILE, json.dumps(record))

    with pytest.raises(AssertionError, match='went backwards'):
        check(cluster, result, led, 'mutant post')


def test_checker_catches_a_replayed_attempt(big_run):
    cluster = big_run
    led = Ledger([str(e) for e, _ in jm.generate_ranges()])
    result = cluster.reconcile()
    check(cluster, result, led, 'mutant pre')
    # The range's only Job is destroyed with no record of the range, so the
    # next pass has to re-create attempt 1 -- a second pod wearing attempt 1.
    cluster.k8s.batch_v1.delete_namespaced_job('pc-r1200-a1', cluster.namespace)

    result = cluster.reconcile()

    with pytest.raises(AssertionError, match='two distinct pods'):
        check(cluster, result, led, 'mutant post')
