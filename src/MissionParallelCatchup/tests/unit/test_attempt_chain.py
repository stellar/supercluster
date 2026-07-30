"""Aggregating measurements across the attempts that make up one range.

In pvc mode a pod killed after replay starts leaves /data, and the next attempt
resumes at LCL+1 with RESUME=true -- skipping the archive download and bucket
apply, which is where peak memory happens. Profiling only the winning attempt
therefore under-reports a resumed range by the whole download gap, and on spot
(where eviction is routine and resume is the point of durable /data) that would
make the run unprofileable. medida's total and a pod's duration are per-process
for exactly the same reason, so both are tail-only in the same way.
"""

import gzip
import io
import json

import pytest

import job_monitor as jm


GIB = 1024 ** 3
MIB = 1024 ** 2


def _gzip_member(text):
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode='wb', mtime=0) as fh:
        fh.write(text.encode())
    return buf.getvalue()


def _archive(end, attempt, *members):
    with open(jm.log_path(end, attempt), 'wb') as fh:
        for member in members:
            fh.write(_gzip_member(member))


@pytest.fixture
def attempts(logdir):
    """Lay down the files the collector and the monitor leave per attempt."""
    def write(end, spec):
        for n, (metrics, outcome) in spec.items():
            if metrics is not None:
                with open(jm.metrics_path(end, n), 'w') as fh:
                    fh.write(metrics if isinstance(metrics, str) else json.dumps(metrics))
            if outcome is not None:
                with open(jm.outcome_path(end, n), 'w') as fh:
                    json.dump(outcome, fh)
    return write


# --- which attempts describe the range ---------------------------------------

def test_the_chain_is_the_run_of_resumed_attempts_ending_at_this_one(attempts):
    # a1 interrupted then superseded by a fresh a2; a3 resumed from a2. Only
    # a2+a3 describe the same continuous pass over the range.
    attempts(999, {1: ({}, None), 2: ({}, None), 3: ({'resumed': True}, None)})
    assert list(jm._resumed_chain(999, 3)) == [2, 3]
    assert list(jm._resumed_chain(999, 1)) == [1]


def test_an_attempt_with_no_metrics_file_is_not_treated_as_resumed(attempts):
    attempts(999, {1: ({}, None)})
    assert jm._attempt_resumed(999, 2) is False


def test_resume_falls_back_to_the_archive_when_metrics_lacks_the_field(attempts):
    attempts(999, {2: ({'attemptSeconds': 300.0}, None)})
    _archive(999, 2, 'RESUME: reached ledger 900; skipping new-db\n')

    assert jm._attempt_resumed(999, 2) is True


def test_resume_declined_is_not_a_true_resume(attempts):
    attempts(999, {2: ({}, None)})
    _archive(999, 2, 'RESUME DECLINED: no usable local state; running new-db\n')

    assert jm._attempt_resumed(999, 2) is False


def test_resume_is_found_across_concatenated_gzip_members(attempts):
    attempts(999, {2: ({}, None)})
    _archive(999, 2, 'worker startup\n',
             'RESUME: reached ledger 900; skipping new-db\n')

    assert jm._attempt_resumed(999, 2) is True


def test_missing_truncated_and_corrupt_archives_are_safe(attempts):
    attempts(999, {2: ({}, None), 3: ({}, None), 4: ({}, None)})
    with open(jm.log_path(999, 3), 'wb') as fh:
        fh.write(_gzip_member('worker startup\n')[:-8])
    with open(jm.log_path(999, 4), 'wb') as fh:
        fh.write(b'not a gzip archive')

    assert jm._attempt_resumed(999, 2) is False
    assert jm._attempt_resumed(999, 3) is False
    assert jm._attempt_resumed(999, 4) is False


def test_three_attempt_chain_can_be_recovered_entirely_from_archives(attempts):
    attempts(999, {1: ({}, None), 2: ({}, None), 3: ({}, None)})
    _archive(999, 2, 'RESUME: reached ledger 700; skipping new-db\n')
    _archive(999, 3, 'RESUME: reached ledger 800; skipping new-db\n')

    assert list(jm._resumed_chain(999, 3)) == [1, 2, 3]


# --- peaks --------------------------------------------------------------------

def test_a_resumed_range_keeps_the_peak_from_the_attempt_that_did_the_download(attempts):
    # a1 evicted mid-replay having already done the download; a2 resumes at
    # LCL+1 and only replays the tail. a2 alone would report 400MiB for a range
    # that really needs 2GiB.
    attempts(999, {1: ({'peakAnonBytes': 2 * GIB}, {'outcome': 'disrupted'}),
                   2: ({'peakAnonBytes': 400 * MIB, 'resumed': True}, None)})
    assert jm.peaks_for_range(999, 2)['peakAnonBytes'] == 2 * GIB


def test_a_fresh_retry_supersedes_an_interrupted_one(attempts):
    # No RESUME line means new-db ran and this attempt did the whole range, so
    # its sample is complete. An earlier attempt that was merely interrupted
    # measured the same work and only adds noise.
    attempts(999, {1: ({'peakAnonBytes': 8 * GIB}, {'outcome': 'disrupted'}),
                   2: ({'peakAnonBytes': 900 * MIB}, None)})
    assert jm.peaks_for_range(999, 2)['peakAnonBytes'] == 900 * MIB


@pytest.mark.parametrize('outcome,field,hit,quiet', [
    ('oom', 'peakAnonBytes', 8 * GIB, 900 * MIB),
    ('oom', 'peakEphemeralBytes', 30 * GIB, 5 * GIB),   # died on memory, its disk figure is real
    ('ephemeral', 'peakEphemeralBytes', 40 * GIB, 9 * GIB),
    ('ephemeral', 'peakAnonBytes', 3 * GIB, 1 * GIB),
])
@pytest.mark.parametrize('resumed', [True, False])
def test_an_attempt_killed_at_a_ceiling_counts_wherever_it_sits(attempts, outcome,
                                                                field, hit, quiet, resumed):
    # A pod OOM-killed at 8Gi really did allocate ~8Gi and wanted more, so its
    # peak is a lower bound on demand, not an artifact of the limit -- and it is
    # the attempt most worth keeping, because download concurrency scales with
    # available cpu and a pod that bursted on an idle node can peak above the
    # one that eventually succeeded. Sizing off the quieter attempt would OOM
    # the range again.
    #
    # It survives a fresh start too, which the chain rule alone would drop.
    # Measured on ssc-30: an OOM in replay resumes and stays in the chain
    # (224 of 252), an OOM in download does not (25 of 252), and a higher-cpu
    # run is download-bound -- so the self-correcting loop would go quiet
    # exactly when it is most needed.
    later = {field: quiet}
    if resumed:
        later['resumed'] = True
    attempts(999, {1: ({field: hit}, {'outcome': outcome}), 2: (later, None)})
    assert jm.peaks_for_range(999, 2)[field] == hit


def test_the_ceiling_exception_is_peaks_only(attempts):
    # tx_apply and seconds are summed, and a fresh start redoes the work the
    # dropped attempt already did, so counting it there would double-count.
    attempts(999, {1: ({'txApplySeconds': 100.0, 'attemptSeconds': 900.0},
                       {'outcome': 'oom'}),
                   2: ({'txApplySeconds': 7.0}, None)})       # fresh start
    assert jm.tx_apply_for_range(999, 2) == 7.0
    assert jm.seconds_for_range(999, 2, 300.0) == 300.0


def test_a_missing_or_malformed_metrics_file_is_tolerated(attempts):
    attempts(999, {2: ("not json at all", None),
                   3: ({'peakAnonBytes': 5, 'resumed': True}, None)})
    assert jm.peaks_for_range(999, 3) == {'peakAnonBytes': 5}
    assert jm.peaks_for_range(999, 9) == {}


def test_an_absent_peak_never_reaches_the_profile_as_a_null(attempts):
    # The consumer falls back to a default on a missing field, so a null defeats it.
    attempts(999, {1: ({'peakAnonBytes': None, 'peakRssBytes': 7}, None)})
    assert jm.peaks_for_range(999, 1) == {'peakRssBytes': 7}


def test_both_measured_peaks_reach_the_progress_record():
    # peaks_for_range filters to PEAK_FIELDS and the ConfigMap mirror strips
    # _PROFILE_ONLY_FIELDS; a measurement absent from either is silently
    # dropped between the collector and the profile.
    for field in ('peakAnonBytes', 'peakWorkingSetBytes', 'peakEphemeralBytes'):
        assert field in jm.PEAK_FIELDS, field
    assert 'peakAnonBytes' in jm._PROFILE_ONLY_FIELDS


# --- durations ----------------------------------------------------------------

def test_seconds_sums_the_whole_resumed_chain(attempts):
    # a1 ran 900s then was evicted mid-replay; a2 resumed and took 300s. The
    # range cost 1200s of compute, not 300.
    attempts(999, {1: ({}, {'outcome': 'disrupted', 'attemptSeconds': 900.0}),
                   2: ({'resumed': True}, None)})
    assert jm.seconds_for_range(999, 2, 300.0) == 1200.0


def test_seconds_ignores_attempts_before_a_fresh_start(attempts):
    # a2 ran new-db and did the whole range itself, so a1's 900s is not part of
    # the same pass.
    attempts(999, {1: ({}, {'outcome': 'oom', 'attemptSeconds': 900.0}),
                   2: ({}, None)})
    assert jm.seconds_for_range(999, 2, 300.0) == 300.0


def test_seconds_survives_a_leg_with_no_recorded_duration(attempts):
    # An attempt whose pod vanished before it was classified has no
    # attemptSeconds. Better to under-report one leg than return nothing.
    attempts(999, {1: ({}, {'outcome': 'disrupted'}), 2: ({'resumed': True}, None)})
    assert jm.seconds_for_range(999, 2, 300.0) == 300.0


def test_seconds_is_none_when_nothing_is_known(attempts):
    attempts(999, {1: ({}, None)})
    assert jm.seconds_for_range(999, 1, None) is None


def test_seconds_falls_back_to_the_collectors_figure(attempts):
    # The authoritative .outcome is missing for every reaped pod -- measured on
    # ssc-test 2026-07-30, 212 of 212 spot disruptions were classified from the
    # Job condition with the pod already gone, so record_outcome never ran.
    # Without this fallback the chain drops that leg entirely.
    attempts(999, {1: ({'attemptSeconds': 850.0}, None),
                   2: ({'resumed': True}, None)})
    assert jm.seconds_for_range(999, 2, 300.0) == 1150.0


def test_the_authoritative_outcome_wins_over_the_collector_estimate(attempts):
    # .outcome comes from the pod's terminated timestamps; the collector's is a
    # stream-lifetime approximation that starts up to one poll late.
    attempts(999, {1: ({'attemptSeconds': 850.0},
                       {'outcome': 'disrupted', 'attemptSeconds': 900.0}),
                   2: ({'resumed': True}, None)})
    assert jm.seconds_for_range(999, 2, 300.0) == 1200.0


# --- completed profile reconstruction -----------------------------------------

def test_repair_recovers_predecessor_peaks_and_seconds_idempotently(attempts):
    attempts(999, {
        1: ({'attemptSeconds': 900.0, 'peakAnonBytes': 2 * GIB,
             'peakWorkingSetBytes': 3 * GIB}, None),
        2: ({'attemptSeconds': 300.0, 'peakAnonBytes': 400 * MIB,
             'peakWorkingSetBytes': 500 * MIB}, None),
    })
    _archive(999, 2, 'RESUME: reached ledger 800; skipping new-db\n')
    progress = {'completed': {'999': {
        'attempts': 2, 'seconds': 300.0,
        'peakAnonBytes': 400 * MIB, 'peakWorkingSetBytes': 500 * MIB,
    }}}

    assert jm.repair_completed_profiles(progress) == 1
    repaired = progress['completed']['999']
    assert repaired['seconds'] == 1200.0
    assert repaired['peakAnonBytes'] == 2 * GIB
    assert repaired['peakWorkingSetBytes'] == 3 * GIB

    snapshot = json.loads(json.dumps(progress))
    assert jm.repair_completed_profiles(progress) == 0
    assert progress == snapshot


def test_reconstruction_sums_available_txapply_legs_in_a_three_attempt_chain(attempts):
    attempts(999, {
        1: ({'txApplySeconds': 10.0}, None),
        2: ({}, None),                              # this leg's metric was unavailable
        3: ({'txApplySeconds': 3.0}, None),
    })
    _archive(999, 2, 'RESUME: reached ledger 700; skipping new-db\n')
    _archive(999, 3, 'RESUME: reached ledger 800; skipping new-db\n')

    rebuilt = jm.reconstruct_completed_profile(999, 3)
    assert rebuilt['txApply'] == 13.0


def test_reconstruction_leaves_txapply_absent_when_every_leg_is_missing(attempts):
    attempts(999, {1: ({}, None), 2: ({}, None)})
    _archive(999, 2, 'RESUME: reached ledger 800; skipping new-db\n')

    assert 'txApply' not in jm.reconstruct_completed_profile(999, 2)


def test_reconstruction_does_not_cross_a_fresh_restart_boundary(attempts):
    attempts(999, {
        1: ({'attemptSeconds': 900.0, 'txApplySeconds': 100.0,
             'peakAnonBytes': 8 * GIB}, None),
        2: ({'attemptSeconds': 300.0, 'txApplySeconds': 7.0,
             'peakAnonBytes': 900 * MIB}, None),
        3: ({'attemptSeconds': 60.0, 'txApplySeconds': 2.0,
             'peakAnonBytes': 400 * MIB}, None),
    })
    _archive(999, 2, 'RESUME DECLINED: running new-db\n')
    _archive(999, 3, 'RESUME: reached ledger 950; skipping new-db\n')

    rebuilt = jm.reconstruct_completed_profile(999, 3)
    assert rebuilt['seconds'] == 360.0
    assert rebuilt['txApply'] == 9.0
    assert rebuilt['peakAnonBytes'] == 900 * MIB


def test_reconcile_repairs_a_completed_record_with_no_live_job(cluster):
    cluster.write(jm.PROGRESS_FILE, json.dumps({
        'completed': {'300': {'attempts': 2, 'count': 100, 'seconds': 300.0,
                              'txApply': 2.0, 'peakAnonBytes': 400 * MIB}},
        'failed': {},
    }))
    cluster.finalize(300, 1, tx_apply=10.0, attempt_seconds=900.0,
                     peaks={'peakAnonBytes': 2 * GIB})
    cluster.finalize(300, 2, tx_apply=2.0, attempt_seconds=300.0,
                     peaks={'peakAnonBytes': 400 * MIB})
    _archive(300, 2, 'RESUME: reached ledger 250; skipping new-db\n')

    cluster.reconcile()

    repaired = cluster.completed()['300']
    assert repaired['seconds'] == 1200.0
    assert repaired['txApply'] == 12.0
    assert repaired['peakAnonBytes'] == 2 * GIB


# --- counting causes, not attempts --------------------------------------------

def test_escalation_counts_ooms_not_attempts(attempts):
    # On spot most retries are evictions: 288 disruption retries against 7 OOM
    # retries on ssc-test 2026-07-30. Keying the exponent on the attempt index
    # meant a range disrupted three times then OOMing once jumped to
    # base * 1.5^4 -- a 5x request for one OOM, inflated fleet-wide.
    attempts(9, {1: (None, {'outcome': 'disrupted'}),
                 2: (None, {'outcome': 'disrupted'}),
                 3: (None, {'outcome': 'disrupted'}),
                 4: (None, {'outcome': 'oom'})})
    assert jm._oom_count(9, 4) == 1, "three evictions were counted as escalations"
    attempts(9, {5: (None, {'outcome': 'oom'}), 6: (None, {'outcome': 'oom'})})
    assert jm._oom_count(9, 6) == 3
