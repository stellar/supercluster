"""RACE #7: a condemned range must not freeze dispatch, or the mission hangs.

The mission driver (MissionHistoryPubnetParallelCatchupV2.fs) no longer aborts
on the first failure -- it drains first, and only reports once

    num_remain == 0 && jobs_in_progress.Count == 0

which are `reconcile()`'s own `remaining` and `in_progress` verbatim
(job_monitor.update_status_and_metrics maps them straight into the status JSON).

If dispatch is gated on `not failed`, the first condemned range stops the
monitor sending any further work. `in_progress` still drains to empty as the
in-flight ranges land, but `remaining` stays pinned at however many ranges were
never dispatched. The driver's condition is then unsatisfiable and the mission
waits forever with an idle, fully-billed node pool -- strictly worse than the
immediate abort it replaced.

These tests drive the real reconcile() through the fake cluster and assert only
on what it returns and on what ends up in progress.json. No source text.
"""

import pytest

import job_monitor as jm


# -- helpers (local to this file; the shared harness is not touched) ----------

def _end_of(key):
    """'300/420' -> 300. `in_progress` entries are job_key(end, count)."""
    return int(key.split('/')[0])


def _drained(poll):
    """The mission driver's completion test, applied to a reconcile summary."""
    return poll['remaining'] == 0 and not poll['in_progress']


def drive_like_the_mission(cluster, condemn=(), max_passes=40):
    """Poll reconcile() the way the driver polls the monitor, until it drains.

    Between polls the cluster does its job: every range currently in flight
    finishes -- successfully, unless it is in `condemn`, in which case it exits
    1 (a genuine catchup failure, which the monitor never retries).

    Returns the list of poll results, or None if the run never drained inside
    `max_passes` -- which is what a hang looks like when you cannot wait forever.
    """
    condemn = {int(e) for e in condemn}
    polls = []
    for _ in range(max_passes):
        poll = cluster.reconcile()
        polls.append(poll)
        if _drained(poll):
            return polls
        for key in list(poll['in_progress']):
            end = _end_of(key)
            attempt = cluster.attempt_of(end)
            if end in condemn:
                cluster.advance(end, 'condemned', attempt=attempt)
            else:
                cluster.advance(end, 'succeeded', attempt=attempt)
                cluster.finalize(end, attempt, tx_apply=1.0,
                                 peaks={'peakRssBytes': 1})
    return None


def _why_stuck(polls):
    last = polls[-1]
    return (f"run never drained; last poll remaining={last['remaining']} "
            f"in_progress={last['in_progress']} created={last['created']} "
            f"completed={last['completed']} failed={last['failed_ranges']}")


# -- the race ----------------------------------------------------------------

def test_a_condemned_range_does_not_pin_remaining_above_zero(cluster):
    """The exact interleaving: one range condemned while another succeeds.

    Three ranges, PARALLELISM 2, so range 100 is still undispatched when 300 is
    condemned. If the condemn freezes dispatch, 100 is never sent, `in_progress`
    empties anyway, and `remaining` sticks at 1 -- the deadlock.
    """
    first = cluster.reconcile()
    assert sorted(first['in_progress']) == ['200/420', '300/420']
    assert first['remaining'] == 1, "range 100 has not been dispatched yet"

    cluster.advance(300, 'condemned')          # exit 1: never retried
    cluster.advance(200, 'succeeded')
    cluster.finalize(200, 1, tx_apply=1.0, peaks={'peakRssBytes': 1})

    second = cluster.reconcile()

    # The condemn is recorded and the good range is banked...
    assert '300' in cluster.failed()
    assert '200' in cluster.completed()
    # ...and the range behind them goes out into the freed capacity. Frozen
    # dispatch gives in_progress == [] with remaining == 1, and from there the
    # driver's `remaining == 0 && in_progress == []` can never come true.
    assert second['in_progress'] == ['100/420'], (
        "the condemned range froze dispatch: range 100 was never sent, so the "
        "mission's drain condition is now unsatisfiable")
    assert second['remaining'] == 0

    # And it really does finish.
    cluster.advance(100, 'succeeded')
    cluster.finalize(100, 1, tx_apply=1.0, peaks={'peakRssBytes': 1})
    third = cluster.reconcile()
    assert _drained(third)
    assert sorted(cluster.completed()) == ['100', '200']
    assert list(cluster.failed()) == ['300']


def test_the_mission_drains_and_then_fails_instead_of_hanging(cluster):
    """End to end through the driver's own loop: it must terminate.

    A run with a condemned range has to reach `remaining == 0 and
    in_progress == []` -- the mission then fails on the recorded failure. With
    dispatch frozen the loop below simply never exits.
    """
    polls = drive_like_the_mission(cluster, condemn=[300])

    assert polls is not None, (
        "the mission never drained: reconcile() never reported "
        "remaining == 0 with in_progress empty, so the driver would poll forever")
    assert _drained(polls[-1]), _why_stuck(polls)

    # It drains, but it does not pass: the failure is still reported, which is
    # what makes the mission fail after the drain.
    assert polls[-1]['failed_ranges'], "the condemned range must still be reported"
    assert polls[-1]['failed_ranges'][0].startswith('300/420|')
    assert polls[-1]['completed'] == 2, "the other two ranges must still be run"


def test_a_condemned_tip_does_not_discard_every_range_behind_it(cluster,
                                                                monkeypatch):
    """The production shape: one early condemn, nine ranges still to dispatch.

    This is the 2026-07-30 incident at small scale -- a condemned range at the
    tip stranded everything queued behind it. Freezing dispatch loses all nine.
    """
    monkeypatch.setattr(jm, 'LATEST_LEDGER_NUM', 1000)   # ends 100..1000

    polls = drive_like_the_mission(cluster, condemn=[1000])

    assert polls is not None, "the mission hung with nine ranges never dispatched"
    assert _drained(polls[-1]), _why_stuck(polls)

    assert sorted(int(e) for e in cluster.completed()) == [
        100, 200, 300, 400, 500, 600, 700, 800, 900]
    assert list(cluster.failed()) == ['1000']
    assert polls[-1]['total'] == 10


def test_the_stuck_state_never_settles_into_a_reportable_one(cluster):
    """A hang is a state that repeats, so poll it the way the driver does.

    Once everything that can finish has finished, every subsequent poll must
    report the drained state. Frozen dispatch instead reports the same
    `remaining > 0, in_progress == []` forever -- work outstanding, nobody
    doing it, no new Jobs. That pair is the deadlock signature.
    """
    drive_like_the_mission(cluster, condemn=[300])

    for _ in range(5):
        poll = cluster.reconcile()
        assert not (poll['remaining'] > 0 and not poll['in_progress']), (
            f"deadlock signature: remaining={poll['remaining']} with nothing in "
            f"flight and created={poll['created']}")
        assert _drained(poll)

    # Nothing new was invented to get there, either: three ranges, three Jobs.
    assert cluster.calls.names(verb='create', kind='job') == [
        'pc-r300-a1', 'pc-r200-a1', 'pc-r100-a1']


def test_two_condemned_ranges_still_leave_the_run_drainable(cluster,
                                                            monkeypatch):
    """More than one failure must not make it worse, and must not double-count.

    `remaining` subtracts completed, failed and in-flight; a second condemn has
    to land in `failed` exactly once or the arithmetic stops reaching zero.
    """
    monkeypatch.setattr(jm, 'LATEST_LEDGER_NUM', 500)    # ends 100..500

    polls = drive_like_the_mission(cluster, condemn=[500, 400])

    assert polls is not None, "the mission hung after two condemned ranges"
    assert _drained(polls[-1]), _why_stuck(polls)
    assert sorted(cluster.failed()) == ['400', '500']
    assert sorted(int(e) for e in cluster.completed()) == [100, 200, 300]
    assert len(polls[-1]['failed_ranges']) == 2


# -- the safety valve the fix must not take with it ---------------------------

def test_nothing_gates_dispatch_at_all(cluster):
    """The gate is gone on purpose, and no new one may appear.

    `failed` stopped gating dispatch because it deadlocked the driver. `halted`
    stopped gating it because its high-water mark lived in memory: a restart
    reset it to zero, so the guard was disarmed by the very event it was there
    to survive. A reconciler must not gate a decision on state a restart erases.

    The cost is re-running a range, which is idempotent -- the PVC still holds
    /data so the attempt resumes at its last closed ledger and the measurements
    are re-recorded rather than lost.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.0, peaks={'peakRssBytes': 1})
    cluster.reconcile()

    cluster.write(jm.PROGRESS_FILE, '{}')    # the record is wiped underneath us

    poll = cluster.reconcile()

    # The range returns to the pool rather than the run stopping. Nothing is
    # created on this pass only because PARALLELISM is already spent on the
    # other two ranges -- capacity, not a gate.
    assert '300' not in cluster.completed()
    assert poll['completed'] == 0
    assert poll['remaining'] + len(poll['in_progress']) == 3

    # Free a slot and it really is dispatched again: the run is not wedged.
    cluster.advance(200, 'succeeded')
    cluster.finalize(200, 1, tx_apply=1.0, peaks={'peakRssBytes': 1})
    assert cluster.reconcile()['created'] >= 1
