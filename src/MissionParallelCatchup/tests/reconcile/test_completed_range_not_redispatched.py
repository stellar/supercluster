"""RACE #1 -- a completed range gets re-dispatched and re-run from scratch.

The interleaving these tests drive is the real one:

  A. range 300 attempt 1 is lost to node disruption. The verdict is
     `disrupted`, 1 < MAX_DISRUPTION_ATTEMPTS, so attempt 2 is created. The
     collector died with the node, so no `.done` marker exists for attempt 1
     and the monitor deliberately does NOT delete its Job -- it sits Failed,
     waiting on JOB_TTL_SECONDS.
  B. attempt 2 reuses the surviving PVC, finds the range already complete and
     exits 0. The next pass keys `live` on the highest attempt, records the
     range, releases the PVC and reaps -- but the reap is attempt-scoped, so
     only attempt 2's Job dies. The Failed attempt-1 Job outlives the winner.
  C. The pass after that lists only attempt 1, so `live[300]` is the Failed
     Job. Nothing in the `st.failed` branch asks whether the range is already
     in `completed`, so the disruption verdict is reached all over again and
     attempt 2 is created A SECOND TIME -- against a freshly recreated, empty
     PVC, so it replays the whole range from genesis.

Everything asserted below is observed state: which Jobs and PVCs exist, what
the API was asked to create, what landed in progress.json, and what reconcile()
itself reported. No source text is inspected.
"""

import job_monitor as jm


# -- helpers -----------------------------------------------------------------


def jobs_for(cluster, end):
    """Live Job names belonging to one range, oldest attempt first."""
    prefix = f"{cluster.run_name}-r{int(end)}-a"
    return sorted((n for n in cluster.jobs() if n.startswith(prefix)),
                  key=lambda n: int(n.rsplit('-a', 1)[1]))


def created(cluster, kind):
    return cluster.calls.names(verb='create', kind=kind)


def stale_predecessor(cluster):
    """Passes 1-2: dispatch, then lose 300/a1 to disruption.

    Leaves the range with two Jobs: Failed a1 (never finalized, so never
    reaped) and freshly created a2.
    """
    cluster.reconcile()
    cluster.advance(300, 'disrupted')          # collector dies with the node:
    cluster.reconcile()                        # no finalize() -> no .done


def win_on_attempt_two(cluster):
    """Pass 3: a2 succeeds and is recorded. Returns reconcile()'s summary."""
    cluster.advance(300, 'succeeded')          # newest attempt == a2
    cluster.finalize(300, 2, tx_apply=0.25, peaks={'peakRssBytes': 4096})
    return cluster.reconcile()


# -- the precondition, so a green suite cannot be green by accident ----------


def test_a_disrupted_attempt_that_never_finalized_outlives_its_successor(cluster):
    """Setup check: the losing Job really is still there when a2 starts.

    This is intended behaviour -- the monitor refuses to reap an attempt whose
    collector never wrote `.done`, because the Job's pod is the last place its
    measurements could still be read from. It is the *input* to the race, not
    the bug, and it must hold both before and after the fix.
    """
    stale_predecessor(cluster)

    assert jobs_for(cluster, 300) == ['pc-r300-a1', 'pc-r300-a2']
    assert cluster.k8s.job('pc-r300-a1').status.failed
    # The volume survives on purpose: that is what lets a2 resume instead of
    # replaying from genesis.
    assert 'pc-data-r300' in cluster.pvcs()
    assert cluster.completed() == {}


# -- the race ----------------------------------------------------------------


def test_recording_a_range_reaps_every_attempt_not_just_the_winner(cluster):
    """Completion is terminal for the RANGE, so no attempt of it may survive.

    RED (attempt-scoped reap): only pc-r300-a2 is deleted and the Failed
    pc-r300-a1 is still standing -- which is the entire fuel for the re-run.
    """
    stale_predecessor(cluster)
    win_on_attempt_two(cluster)

    assert cluster.completed()['300']['attempts'] == 2      # it really recorded
    assert jobs_for(cluster, 300) == []


def test_a_recorded_range_is_never_dispatched_again(cluster):
    """The core consequence: an already-paid-for range is re-run end to end.

    RED (no `completed` guard in the failed branch): the pass after the record
    sees the leftover Failed a1, re-reaches the `disrupted` verdict and creates
    pc-r300-a2 for the second time.
    """
    stale_predecessor(cluster)
    win_on_attempt_two(cluster)
    recorded = dict(cluster.completed()['300'])

    for _ in range(3):                        # the monitor loops forever
        cluster.reconcile()

    assert jobs_for(cluster, 300) == []
    # Exactly two Jobs were ever created for this range: a1 and its one retry.
    assert created(cluster, 'job').count('pc-r300-a2') == 1
    assert [n for n in created(cluster, 'job') if n.startswith('pc-r300-')] == \
        ['pc-r300-a1', 'pc-r300-a2']
    # And the durable record was not disturbed by the extra passes.
    assert cluster.completed()['300'] == recorded
    assert cluster.failed() == {}


def test_a_released_volume_is_not_resurrected_for_a_completed_range(cluster):
    """Why the re-run is worst case: the PVC is gone, so there is nothing to
    resume from. build_job() calls ensure_pvc(), which recreates it empty --
    no /data/.job-key, RESUME declined, new-db, full replay from genesis.

    RED: pc-data-r300 is released by the recording pass and then created a
    second time by the spurious re-dispatch.
    """
    stale_predecessor(cluster)
    win_on_attempt_two(cluster)

    assert 'pc-data-r300' not in cluster.pvcs()          # released on record
    cluster.reconcile()
    cluster.reconcile()

    assert 'pc-data-r300' not in cluster.pvcs()
    assert created(cluster, 'pvc').count('pc-data-r300') == 1


def test_a_phantom_rerun_does_not_breach_parallelism(cluster):
    """The slot freed by 300 goes to 100 -- and then 300 must not take one back.

    Recording 300 frees a slot, so pass 3 dispatches range 100 and the run is
    at its cap of 2. The re-dispatch happens *outside* the capacity check (the
    failed branch creates the Job and appends to in_progress unconditionally),
    so it does not wait for a slot -- it takes a third one.

    RED: in_progress is ['100/420', '200/420', '300/420'] -- three concurrent
    ranges under PARALLELISM 2, one of them already finished.
    """
    stale_predecessor(cluster)
    win_on_attempt_two(cluster)
    assert 'pc-r100-a1' in cluster.jobs()          # the slot did free up

    result = cluster.reconcile()

    assert '300/420' not in result['in_progress']
    assert sorted(result['in_progress']) == ['100/420', '200/420']
    assert len(result['in_progress']) <= jm.PARALLELISM


def test_the_range_scoped_reap_still_waits_for_the_done_marker(cluster):
    """Widening the reap from one attempt to the whole range must not widen
    *when* it fires. Deleting a Job reaps its pod, and .metrics is the only
    place peaks live, so nothing may be reaped before the collector has
    written `.done` -- JOB_TTL_SECONDS is the backstop for a collector that
    never gets there.

    This is the range-scoped half of the guarantee; the attempt-scoped half is
    unit/test_reaping.py::test_the_reap_waits_for_the_collectors_done_marker.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')

    waiting = cluster.reconcile()              # recorded; collector not done
    assert '300' in cluster.completed()
    assert jobs_for(cluster, 300) == ['pc-r300-a1']
    assert cluster.deleted.names(verb='delete', kind='job') == []
    assert waiting['finalizing'] == ['300/420'], \
        "the mission could publish its final profile before metrics landed"

    cluster.finalize(300, 1, tx_apply=0.1, peaks={'peakRssBytes': 1})
    finished = cluster.reconcile()
    assert jobs_for(cluster, 300) == []
    assert cluster.deleted.names(verb='delete', kind='job') == ['pc-r300-a1']
    assert finished['finalizing'] == []


def test_remaining_never_goes_negative_and_the_run_reports_done(cluster,
                                                                monkeypatch):
    """The mission driver waits for `remaining == 0 and in_progress == []`.

    With every range finished, that condition must hold and keep holding.

    RED: the leftover Failed a1 puts range 300 back into in_progress while it
    is also in completed, so it is subtracted twice -- remaining reads -1, and
    in_progress is never empty, so the driver's completion test never fires.
    """
    monkeypatch.setattr(jm, 'PARALLELISM', 3)   # all three ranges at once

    cluster.reconcile()
    cluster.advance(300, 'disrupted')
    cluster.reconcile()                         # 300/a1 Failed and unfinalized

    for end, attempt in ((300, 2), (200, 1), (100, 1)):
        cluster.advance(end, 'succeeded', attempt=attempt)
        cluster.finalize(end, attempt, tx_apply=0.1, peaks={'peakRssBytes': 1})
    done = cluster.reconcile()

    assert done['completed'] == 3
    assert done['in_progress'] == []
    assert done['remaining'] == 0

    # ...and it stays done. This is the pass that re-dispatches under the bug.
    again = cluster.reconcile()
    assert again['completed'] == 3
    assert again['remaining'] == 0          # reads -1 while the bug is present
    assert again['in_progress'] == []
    assert again['created'] == 0
    assert cluster.jobs() == []
