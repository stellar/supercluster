"""Hostile durable state: the monitor must never mistake foreign or corrupt
state for progress.

Every test here drives the shipped reconcile() against the fake cluster and
asserts on observed state -- the durable record, the call log, the Job set --
never on source text.

The volume the monitor resumes from is not private to a run. It is a PVC that
outlives `helm uninstall`, gets reused across missions, and is mirrored into a
ConfigMap that a second writer can clobber. So progress.json can arrive
truncated, rolled back, or written by a run with a completely different
ledgersPerJob. None of those are hypothetical, and none of them may be read as
"work already done".
"""

import json

import pytest

import fake_k8s
import config
import records
import attempts
import job_monitor as jm


# A progress record left by a DIFFERENT slicing of the same ledger space: the
# ends are real range ends, just not ends of THIS run's range list.
FOREIGN = {'attempts': 1, 'count': 111, 'seconds': 12.0, 'wallSeconds': 12.0,
           'txApply': 1.0}


def seed_progress(cluster, completed=None, failed=None):
    cluster.write(config.PROGRESS_FILE, json.dumps(
        {'completed': dict(completed or {}), 'failed': dict(failed or {})}))


# --- the headline case -------------------------------------------------------


def test_foreign_completed_keys_do_not_shrink_remaining(cluster):
    """`remaining` must count THIS run's outstanding ranges, not subtract a
    number that a foreign record can inflate.

    Seeded: one completed key ('333') from a run with a different ledgersPerJob.
    This run's ranges are 300/200/100 and not one of them has been touched.
    Subtraction gives 3 - 1 - 0 - 2 == 0 on the very first pass: the mission's
    `num_remain` reads zero while three ranges are outstanding.
    """
    seed_progress(cluster, completed={'333': FOREIGN})

    result = cluster.reconcile()

    # Two of the three went out; range 100 is queued behind PARALLELISM.
    assert sorted(result['in_progress']) == ['200/420', '300/420']
    # ...so exactly one range of this run is still waiting to be dispatched.
    assert result['remaining'] == 1

    # And the foreign key really is being carried in the record -- the test
    # above is not passing because something quietly dropped it.
    assert '333' in cluster.completed()
    assert set(cluster.completed()) & {'100', '200', '300'} == set()


def test_foreign_completed_keys_do_not_drive_remaining_negative(cluster):
    """The mirror image, and the one that hangs a real run.

    The mission finishes on `num_remain == 0 && queue_in_progress_count == 0`
    (MissionHistoryPubnetParallelCatchupV2.fs). With three foreign keys in the
    record, subtraction lands on -3 once every real range has actually
    completed -- never 0 -- so the driver waits forever on a run that is done.
    """
    seed_progress(cluster, completed={'111': FOREIGN, '222': FOREIGN,
                                      '333': FOREIGN})

    result = cluster.reconcile()
    for end in (300, 200):
        cluster.advance(end, 'succeeded')
        cluster.finalize(end, 1)
    result = cluster.reconcile()
    cluster.advance(100, 'succeeded')
    cluster.finalize(100, 1)
    result = cluster.reconcile()

    # Every range of this run really did run and really is recorded.
    assert {'100', '200', '300'} <= set(cluster.completed())
    assert result['in_progress'] == []
    # The terminating condition the mission actually tests.
    assert result['remaining'] == 0


def test_foreign_failed_keys_do_not_shrink_remaining(cluster):
    """Same subtraction, other bucket. `failed` is foreign-writable too."""
    seed_progress(cluster, failed={'111': {'attempts': 1, 'outcome': 'failed',
                                           'exitCode': 1, 'pod': 'gone'},
                                   '222': {'attempts': 1, 'outcome': 'failed',
                                           'exitCode': 1, 'pod': 'gone'}})

    result = cluster.reconcile()

    assert sorted(result['in_progress']) == ['200/420', '300/420']
    assert result['remaining'] == 1


def test_a_range_end_shared_with_the_foreign_slicing_is_still_skipped(cluster):
    """Honest about the limit of the fix.

    `remaining` becomes a count over THIS run's range list, so it is immune to
    keys that do not name one of our ranges. It cannot save us from a foreign
    key that happens to collide with one of them -- '300' is an end under
    ledgersPerJob=150 as well as under 100 -- because at that point the record
    is indistinguishable from a legitimate resume. The count stays consistent
    with what dispatch does, which is the property that matters: no phantom
    zero, no phantom negative.
    """
    seed_progress(cluster, completed={'300': FOREIGN, '333': FOREIGN})

    result = cluster.reconcile()

    # 300 is treated as done (a resume, as far as anything here can tell)...
    assert 'pc-r300-a1' not in cluster.jobs()
    assert sorted(result['in_progress']) == ['100/420', '200/420']
    # ...and remaining agrees with that: nothing left unaccounted for.
    assert result['remaining'] == 0


# --- corruption --------------------------------------------------------------


def test_an_unreadable_progress_json_replays_rather_than_halting(cluster):
    """An unreadable record reads as "nothing has been done".

    There is no monotonic-progress guard -- its high-water mark lived in memory
    and a restart erased it. Replay is safe: the PVCs survive, so each range
    resumes at its last closed ledger.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1)
    cluster.reconcile()
    assert '300' in cluster.completed()

    cluster.write(config.PROGRESS_FILE, 'not json at all')

    result = cluster.reconcile()

    # The record is empty, so the range is eligible again -- and the pass does
    # not crash, which is the property that actually matters here.
    assert cluster.state['halted'] is False
    assert cluster.completed() == {}
    assert result['remaining'] + len(result['in_progress']) == 3


def test_progress_rolled_back_to_an_older_version_makes_it_eligible_again(cluster):
    """A stale writer wins the volume: completed goes 2 -> 1.

    The ConfigMap-mirror-loses-a-race shape. The monitor cannot distinguish it
    from deletion and no longer tries: the range simply becomes eligible again.
    Redoing it costs a resumed attempt, not the work.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1)
    cluster.reconcile()
    older = json.dumps(cluster.progress())        # snapshot at completed == 1

    cluster.advance(200, 'succeeded')
    cluster.finalize(200, 1)
    cluster.reconcile()
    assert set(cluster.completed()) == {'200', '300'}

    # The stale copy lands back on the volume.
    cluster.write(config.PROGRESS_FILE, older)
    before = set(cluster.jobs())
    created_before = cluster.calls.names(verb='create', kind='job')

    result = cluster.reconcile()

    # The rolled-back range is eligible again rather than the run stopping.
    assert set(cluster.completed()) == {'300'}
    assert result['remaining'] + len(result['in_progress']) + result['completed'] == 3
    # 200's Job was reaped when it completed, so re-dispatch is a fresh attempt
    # against its surviving PVC -- it resumes, it does not replay from genesis.
    assert 'pc-data-r200' in cluster.pvcs()


# --- the collector's markers -------------------------------------------------


def test_metrics_without_done_must_not_reap(cluster):
    """.done is written last. Without it the collector may still be reading the
    pod's log, and deleting the Job reaps the pod out from under it."""
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    # .metrics only -- exactly the window between the collector's two writes.
    cluster.write(records.metrics_path('300', 1),
                  json.dumps({'txApplySeconds': 2.5, 'peakAnonBytes': 999}))

    cluster.reconcile()

    assert cluster.deleted.names(verb='delete', kind='job') == []
    assert 'pc-r300-a1' in cluster.jobs()
    # The range is recorded and its measurements were read -- the reap is the
    # only thing being withheld.
    assert cluster.completed()['300']['txApply'] == 2.5
    assert cluster.completed()['300']['peakAnonBytes'] == 999

    # Withheld, not leaked: the Job carries a TTL, so declining to reap costs a
    # late reclaim rather than an object that lives until `helm uninstall`.
    assert (cluster.k8s.job('pc-r300-a1').spec.ttl_seconds_after_finished
            == config.JOB_TTL_SECONDS)

    # And the withheld reap does not turn into a re-dispatch on later passes.
    cluster.reconcile()
    assert cluster.deleted.names(verb='delete', kind='job') == []
    assert 'pc-r300-a2' not in cluster.jobs()
    assert cluster.completed()['300']['attempts'] == 1


def test_the_reap_lands_once_the_done_marker_arrives(cluster):
    """The other side of the same gate: while the record is still incomplete,
    reconcile keeps coming back, and the pass that sees .done reaps."""
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.reconcile()                       # recorded with nothing measured

    assert cluster.completed()['300']['txApply'] is None
    assert cluster.deleted.names(verb='delete', kind='job') == []

    # The collector finally finishes this attempt.
    cluster.finalize(300, 1, tx_apply=2.5, peaks={'peakAnonBytes': 999})
    cluster.reconcile()

    # Backfilled from the durable files, then reaped.
    assert cluster.completed()['300']['txApply'] == 2.5
    assert cluster.completed()['300']['peakAnonBytes'] == 999
    assert cluster.deleted.names(verb='delete', kind='job') == ['pc-r300-a1']


def test_done_without_metrics_reaps_but_does_not_invent_measurements(cluster):
    """The other half-write: .done present, .metrics never landed.

    .done is the authority on "nothing more is coming", so the reap is correct
    and must happen -- a range whose collector died would otherwise pin its Job
    forever. What must NOT happen is a fabricated or crashed record.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.write(records.done_path('300', 1), '')

    cluster.reconcile()

    record = cluster.completed()['300']
    assert record['attempts'] == 1
    assert record['count'] == 420
    # No .metrics and no history archive to fall back on: the gap is reported
    # as a gap, not as zero.
    assert record['txApply'] is None
    assert not any(record.get(k) is not None for k in attempts.PEAK_FIELDS)
    # Timing comes from the pod, which is real.
    assert record['seconds'] == pytest.approx(60.0)

    assert cluster.deleted.names(verb='delete', kind='job') == ['pc-r300-a1']
    # Recorded once and never re-dispatched, even though the record is thin.
    assert cluster.reconcile()['created'] == 0
    assert 'pc-r300-a1' not in cluster.jobs()
    assert 'pc-r300-a2' not in cluster.jobs()


def test_an_empty_metrics_file_is_not_read_as_zero(cluster):
    """A zero-length .metrics is a torn write, not a measurement of nothing."""
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.write(records.metrics_path('300', 1), '')
    cluster.write(records.done_path('300', 1), '')

    cluster.reconcile()

    record = cluster.completed()['300']
    assert record['txApply'] is None
    assert not any(record.get(k) is not None for k in attempts.PEAK_FIELDS)


# --- two monitors ------------------------------------------------------------


def test_two_monitors_racing_the_same_volume_never_double_dispatch(cluster):
    """Job name uniqueness is the intended mutex. Prove it actually holds.

    The realistic race is not "B runs after A" -- B would simply see A's Jobs
    in its LIST and skip them. It is both monitors LISTING before either
    CREATES. That is reproduced here by handing the second reconcile the job
    list as it was before the first pass ran, while its writes go to the one
    real cluster.
    """
    stale_jobs = cluster.k8s.batch_v1.list_namespaced_job(
        cluster.namespace, label_selector=f"{config.LABEL_RUN}={config.RUN_NAME}")
    assert stale_jobs.items == []

    a = cluster.reconcile()
    assert a['created'] == 2

    real_list = cluster.k8s.batch_v1.list_namespaced_job
    calls = {'n': 0}

    def list_from_before_the_race(namespace, **kw):
        calls['n'] += 1
        if calls['n'] == 1:
            return stale_jobs          # B's snapshot: taken before A created
        return real_list(namespace, **kw)

    cluster.k8s.batch_v1.list_namespaced_job = list_from_before_the_race
    try:
        # A second monitor process: its own state dict, sharing nothing but the
        # cluster and the volume.
        b_state = {'owner': jm.owner_ref(), 'replayed': set(),
                   'max_completed': 0, 'halted': False, 'counted': {}}
        jm.reconcile(b_state)
    finally:
        cluster.k8s.batch_v1.list_namespaced_job = real_list

    created = cluster.calls.names(verb='create', kind='job')
    # B really did re-attempt the two ranges A had just taken -- otherwise this
    # test proves nothing about the mutex.
    assert created.count('pc-r300-a1') == 2
    assert created.count('pc-r200-a1') == 2

    # The mutex: the duplicate creates were rejected, so each range has exactly
    # ONE Job object and exactly one pod. Nothing ran twice.
    for end in (300, 200):
        name = f'pc-r{end}-a1'
        assert cluster.jobs().count(name) == 1
        pods = [p for (_, _), p in cluster.k8s.pods.items()
                if (p.metadata.labels or {}).get('job-name') == name]
        assert len(pods) == 1, f"{name} spawned {len(pods)} pods"
    # No range was escalated to a second attempt by the losing writer, and the
    # shared volume was not double-provisioned either.
    assert not any(n.endswith('-a2') for n in cluster.jobs())
    for end in (300, 200):
        assert cluster.calls.names(verb='create', kind='pvc').count(
            f'pc-data-r{end}') == 1

    # Neither process crashed on the 409s, and B recorded nothing.
    assert cluster.progress() == {}


def test_a_second_monitor_does_not_re_dispatch_recorded_ranges(cluster):
    """A restart mid-run -- the same thing from the durable side.

    A fresh state dict has max_completed 0 and an empty replay set. Reading the
    volume back must reproduce the run exactly: no redispatch of a recorded
    range, no false regression halt from the counter starting at zero.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1)
    cluster.reconcile()
    assert '300' in cluster.completed()
    created_before = list(cluster.calls.names(verb='create', kind='job'))

    fresh = {'owner': jm.owner_ref(), 'replayed': set(), 'counted': {}}
    result = jm.reconcile(fresh)

    assert cluster.calls.names(verb='create', kind='job').count('pc-r300-a1') == 1
    assert 'pc-r300-a2' not in cluster.jobs()
    # The restart picks the record up rather than starting from zero.
    assert result['completed'] == 1
    assert result['remaining'] + len(result['in_progress']) + result['completed'] == 3
    # Only ranges that were genuinely unstarted moved.
    assert set(cluster.calls.names(verb='create', kind='job')) - set(created_before) <= {
        'pc-r100-a1'}


# --- other people's objects --------------------------------------------------


def test_a_foreign_run_s_jobs_in_the_namespace_are_ignored(cluster):
    """The namespace is shared. Another run's Jobs carry another RUN_NAME and
    must not be read as this run's ranges."""
    other = cluster.k8s.batch_v1.create_namespaced_job(
        cluster.namespace,
        jm.build_job(300, 420, 1, None))
    other.metadata.name = 'other-r300-a1'
    other.metadata.labels = dict(other.metadata.labels or {})
    other.metadata.labels[config.LABEL_RUN] = 'other-run'
    cluster.k8s.jobs[(cluster.namespace, 'other-r300-a1')] = other
    del cluster.k8s.jobs[(cluster.namespace, 'pc-r300-a1')]

    result = cluster.reconcile()

    # Our own range 300 was dispatched despite the foreign Job for the same
    # ledger range already existing.
    assert 'pc-r300-a1' in cluster.jobs()
    assert sorted(result['in_progress']) == ['200/420', '300/420']
    assert result['remaining'] == 1
    assert result['total'] == 3


def test_the_status_configmap_being_deleted_mid_run_is_survivable(cluster):
    """The ConfigMap is the driver's view. Losing it must not lose the run."""
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1)
    cluster.reconcile()
    jm.save_status(jm.status)                 # what the reconcile loop publishes

    cluster.k8s.core_v1.delete_namespaced_config_map(config.PROGRESS_CM,
                                                     cluster.namespace)
    cluster.advance(200, 'succeeded')
    cluster.finalize(200, 1)
    result = cluster.reconcile()

    assert set(cluster.completed()) == {'200', '300'}
    assert cluster.state['halted'] is False
    assert result['completed'] == 2

    # Recreated by the next publish, so the driver is not blind for the rest of
    # the run.
    jm.save_status(jm.status)
    assert 'status.json' in cluster.k8s.config_map_data(config.PROGRESS_CM,
                                                        cluster.namespace)


