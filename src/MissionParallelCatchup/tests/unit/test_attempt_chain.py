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
import os

import pytest

import config
import units
import records
import sizing
import attempts
import job_monitor as jm


GIB = 1024 ** 3
MIB = 1024 ** 2


def _gzip_member(text):
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode='wb', mtime=0) as fh:
        fh.write(text.encode())
    return buf.getvalue()


def _archive(end, attempt, *members):
    with open(records.log_path(end, attempt), 'wb') as fh:
        for member in members:
            fh.write(_gzip_member(member))


@pytest.fixture
def write_attempts(logdir):
    """Lay down the files the collector and the monitor leave per attempt."""
    def write(end, spec):
        for n, (metrics, outcome) in spec.items():
            if metrics is not None:
                with open(records.metrics_path(end, n), 'w') as fh:
                    fh.write(metrics if isinstance(metrics, str) else json.dumps(metrics))
            if outcome is not None:
                with open(records.outcome_path(end, n), 'w') as fh:
                    json.dump(outcome, fh)
    return write


# --- which attempts describe the range ---------------------------------------

def test_the_chain_is_the_run_of_resumed_attempts_ending_at_this_one(write_attempts):
    # a1 interrupted then superseded by a fresh a2; a3 resumed from a2. Only
    # a2+a3 describe the same continuous pass over the range.
    write_attempts(999, {1: ({}, None), 2: ({}, None), 3: ({'resumed': True}, None)})
    assert list(attempts._resumed_chain(999, 3)) == [2, 3]
    assert list(attempts._resumed_chain(999, 1)) == [1]


def test_an_attempt_with_no_metrics_file_is_not_treated_as_resumed(write_attempts):
    write_attempts(999, {1: ({}, None)})
    assert attempts._attempt_resumed(999, 2) is False


def test_a_three_attempt_chain_is_read_from_the_records(write_attempts):
    write_attempts(999, {1: ({}, None), 2: ({'resumed': True}, None),
                   3: ({'resumed': True}, None)})

    assert list(attempts._resumed_chain(999, 3)) == [1, 2, 3]


# --- peaks --------------------------------------------------------------------

def test_a_resumed_range_keeps_the_peak_from_the_attempt_that_did_the_download(write_attempts):
    # a1 evicted mid-replay having already done the download; a2 resumes at
    # LCL+1 and only replays the tail. a2 alone would report 400MiB for a range
    # that really needs 2GiB.
    write_attempts(999, {1: ({'peakAnonBytes': 2 * GIB}, {'outcome': 'disrupted'}),
                   2: ({'peakAnonBytes': 400 * MIB, 'resumed': True}, None)})
    assert attempts.peaks_for_range(999, 2)['peakAnonBytes'] == 2 * GIB


def test_a_fresh_retry_supersedes_an_interrupted_one(write_attempts):
    # No RESUME line means new-db ran and this attempt did the whole range, so
    # its sample is complete. An earlier attempt that was merely interrupted
    # measured the same work and only adds noise.
    write_attempts(999, {1: ({'peakAnonBytes': 8 * GIB}, {'outcome': 'disrupted'}),
                   2: ({'peakAnonBytes': 900 * MIB}, None)})
    assert attempts.peaks_for_range(999, 2)['peakAnonBytes'] == 900 * MIB


@pytest.mark.parametrize('outcome,field,hit,quiet', [
    ('oom', 'peakAnonBytes', 8 * GIB, 900 * MIB),
    ('oom', 'peakEphemeralBytes', 30 * GIB, 5 * GIB),   # died on memory, its disk figure is real
    ('ephemeral', 'peakEphemeralBytes', 40 * GIB, 9 * GIB),
    ('ephemeral', 'peakAnonBytes', 3 * GIB, 1 * GIB),
])
@pytest.mark.parametrize('resumed', [True, False])
def test_an_attempt_killed_at_a_ceiling_counts_wherever_it_sits(write_attempts, outcome,
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
    write_attempts(999, {1: ({field: hit}, {'outcome': outcome}), 2: (later, None)})
    assert attempts.peaks_for_range(999, 2)[field] == hit


def test_the_ceiling_exception_is_peaks_only(write_attempts):
    # tx_apply and seconds are summed, and a fresh start redoes the work the
    # dropped attempt already did, so counting it there would double-count.
    write_attempts(999, {1: ({'txApplySeconds': 100.0, 'attemptSeconds': 900.0},
                       {'outcome': 'oom'}),
                   2: ({'txApplySeconds': 7.0}, None)})       # fresh start
    assert attempts.tx_apply_for_range(999, 2) == 7.0
    assert attempts.seconds_for_range(999, 2, 300.0) == 300.0


def test_a_missing_or_malformed_metrics_file_is_tolerated(write_attempts):
    write_attempts(999, {2: ("not json at all", None),
                   3: ({'peakAnonBytes': 5, 'resumed': True}, None)})
    assert attempts.peaks_for_range(999, 3) == {'peakAnonBytes': 5}
    assert attempts.peaks_for_range(999, 9) == {}


def test_an_absent_peak_never_reaches_the_profile_as_a_null(write_attempts):
    # The consumer falls back to a default on a missing field, so a null defeats it.
    write_attempts(999, {1: ({'peakAnonBytes': None, 'peakAnonBytes': 7}, None)})
    assert attempts.peaks_for_range(999, 1) == {'peakAnonBytes': 7}


def test_both_measured_peaks_reach_the_progress_record():
    # peaks_for_range filters to PEAK_FIELDS; a measurement missing from it is
    # dropped silently between the collector and the profile.
    for field in ('peakAnonBytes', 'peakWorkingSetBytes', 'peakEphemeralBytes'):
        assert field in attempts.PEAK_FIELDS, field


# --- durations ----------------------------------------------------------------

def test_seconds_sums_the_whole_resumed_chain(write_attempts):
    # a1 ran 900s then was evicted mid-replay; a2 resumed and took 300s. The
    # range cost 1200s of compute, not 300.
    write_attempts(999, {1: ({}, {'outcome': 'disrupted', 'attemptSeconds': 900.0}),
                   2: ({'resumed': True}, None)})
    assert attempts.seconds_for_range(999, 2, 300.0) == 1200.0


def test_seconds_ignores_attempts_before_a_fresh_start(write_attempts):
    # a2 ran new-db and did the whole range itself, so a1's 900s is not part of
    # the same pass.
    write_attempts(999, {1: ({}, {'outcome': 'oom', 'attemptSeconds': 900.0}),
                   2: ({}, None)})
    assert attempts.seconds_for_range(999, 2, 300.0) == 300.0


def test_seconds_is_absent_when_a_resumed_leg_has_no_recorded_duration(write_attempts):
    # Winner-only is a lower bound, not the chain total. Missing accurately
    # tells the profile consumer not to size from it.
    write_attempts(999, {1: ({}, {'outcome': 'disrupted'}), 2: ({'resumed': True}, None)})
    assert attempts.seconds_for_range(999, 2, 300.0) is None


def test_seconds_is_none_when_nothing_is_known(write_attempts):
    write_attempts(999, {1: ({}, None)})
    assert attempts.seconds_for_range(999, 1, None) is None


def test_seconds_falls_back_to_the_collectors_figure(write_attempts):
    # The authoritative .outcome is missing for every reaped pod -- measured on
    # ssc-test 2026-07-30, 212 of 212 spot disruptions were classified from the
    # Job condition with the pod already gone, so record_outcome never ran.
    # Without this fallback the chain drops that leg entirely.
    write_attempts(999, {1: ({'attemptSeconds': 850.0}, None),
                   2: ({'resumed': True}, None)})
    assert attempts.seconds_for_range(999, 2, 300.0) == 1150.0


def test_a_poller_clock_estimate_is_refused_as_a_chain_leg(write_attempts):
    # attemptSecondsExact False with no other provenance means the figure came
    # from the collector's own clock, which starts when that process attached --
    # a lower bound, not the attempt. Summing it would publish a total that is
    # quietly short.
    write_attempts(999, {1: ({'attemptSeconds': 850.0, 'attemptSecondsExact': False}, None),
                   2: ({'resumed': True}, None)})
    assert attempts.seconds_for_range(999, 2, 300.0) is None


def test_a_duration_dated_from_container_start_is_accepted(write_attempts):
    # The only duration a DISRUPTED attempt can produce. A pod being deleted
    # keeps phase Running, so its terminated timestamps are usually never
    # observed and the exact path never fires; dating from the container's own
    # startTime measured within 1% on ssc-test (370.9s and 375.1s against ~373s)
    # where the poller clock was 46% short. Refusing it left every resumed chain
    # with no `seconds` at all, which is the whole reason spot runs came back
    # unprofiled.
    write_attempts(999, {1: ({'attemptSeconds': 850.0, 'attemptSecondsExact': False,
                        'attemptSecondsFromContainerStart': True}, None),
                   2: ({'resumed': True}, None)})
    assert attempts.seconds_for_range(999, 2, 300.0) == 1150.0


def test_the_authoritative_outcome_wins_over_the_collector_estimate(write_attempts):
    # .outcome comes from the pod's terminated timestamps; the collector's is a
    # stream-lifetime approximation that starts up to one poll late.
    write_attempts(999, {1: ({'attemptSeconds': 850.0},
                       {'outcome': 'disrupted', 'attemptSeconds': 900.0}),
                   2: ({'resumed': True}, None)})
    assert attempts.seconds_for_range(999, 2, 300.0) == 1200.0


# --- completed profile reconstruction -----------------------------------------

def test_repair_recovers_predecessor_peaks_and_seconds_idempotently(write_attempts):
    write_attempts(999, {
        1: ({'attemptSeconds': 900.0, 'peakAnonBytes': 2 * GIB,
             'peakWorkingSetBytes': 3 * GIB}, None),
        2: ({'attemptSeconds': 300.0, 'peakAnonBytes': 400 * MIB,
             'peakWorkingSetBytes': 500 * MIB, 'resumed': True}, None),
    })
    record = {'attempts': 2, 'seconds': 300.0,
              'peakAnonBytes': 400 * MIB, 'peakWorkingSetBytes': 500 * MIB}

    assert attempts._repair_completed_profile('999', 2, record)
    assert record['seconds'] == 1200.0
    assert record['peakAnonBytes'] == 2 * GIB
    assert record['peakWorkingSetBytes'] == 3 * GIB

    snapshot = json.loads(json.dumps(record))
    assert not attempts._repair_completed_profile('999', 2, record)
    assert record == snapshot


def test_reconstruction_omits_txapply_when_one_chain_leg_is_missing(write_attempts):
    write_attempts(999, {
        1: ({'txApplySeconds': 10.0}, None),
        2: ({'resumed': True}, None),               # this leg's metric was unavailable
        3: ({'txApplySeconds': 3.0, 'resumed': True}, None),
    })

    rebuilt = attempts.reconstruct_completed_profile(999, 3)
    assert 'txApply' not in rebuilt


def test_reconstruction_leaves_txapply_absent_when_every_leg_is_missing(write_attempts):
    write_attempts(999, {1: ({}, None), 2: ({'resumed': True}, None)})

    assert 'txApply' not in attempts.reconstruct_completed_profile(999, 2)


def test_repair_removes_legacy_winner_only_chain_aggregates(write_attempts):
    write_attempts(999, {
        1: ({}, {'outcome': 'disrupted'}),
        2: ({'resumed': True, 'attemptSeconds': 300.0,
             'txApplySeconds': 3.0}, None),
    })
    record = {'attempts': 2, 'seconds': 300.0, 'txApply': 3.0}

    assert attempts._repair_completed_profile('999', 2, record)
    assert 'seconds' not in record
    assert 'txApply' not in record
    assert not attempts._repair_completed_profile('999', 2, record)


def test_reconstruction_does_not_cross_a_fresh_restart_boundary(write_attempts):
    write_attempts(999, {
        1: ({'attemptSeconds': 900.0, 'txApplySeconds': 100.0,
             'peakAnonBytes': 8 * GIB}, None),
        # No resume marker: new-db ran, so this attempt starts the chain.
        2: ({'attemptSeconds': 300.0, 'txApplySeconds': 7.0,
             'peakAnonBytes': 900 * MIB}, None),
        3: ({'attemptSeconds': 60.0, 'txApplySeconds': 2.0,
             'peakAnonBytes': 400 * MIB, 'resumed': True}, None),
    })

    rebuilt = attempts.reconstruct_completed_profile(999, 3)
    assert rebuilt['seconds'] == 360.0
    assert rebuilt['txApply'] == 9.0
    assert rebuilt['peakAnonBytes'] == 900 * MIB


def test_wall_seconds_spans_a_resumed_chain_from_attempt_one(cluster):
    """wallSeconds is the range's whole life, retries and gaps included.

    It used to be omitted for a resumed chain, because the winning Job's own
    start covers the LAST leg only and read smaller than chain-summed `seconds`.
    Anchoring on attempt 1's creationTimestamp removes that inversion: the span
    contains every leg plus every gap between them, so wall - seconds is the
    overhead the Job-per-range design introduced.
    """
    cluster.reconcile()
    first_start = jm.range_started_at(300)
    assert first_start is not None, "attempt 1's Job creation must be recorded"

    cluster.advance(300, 'disrupted')
    cluster.finalize(300, 1, tx_apply=10.0, attempt_seconds=60.0)
    cluster.reconcile()

    cluster.advance(300, 'succeeded', attempt=2)
    cluster.finalize(300, 2, tx_apply=2.0, attempt_seconds=60.0, resumed=True)
    cluster.reconcile()

    record = cluster.completed()['300']
    assert record['seconds'] == 120.0
    assert record['txApply'] == 12.0
    # Present, and still anchored at attempt 1 -- attempt 2's dispatch must not
    # re-stamp it, or the span silently shrinks to the last leg again.
    assert record['wallSeconds'] is not None
    assert jm.range_started_at(300) == first_start


def test_wall_seconds_is_absent_when_attempt_one_was_never_recorded(cluster):
    """No anchor means no wall, rather than a winner-only span.

    Falling back to the winning Job's own start would measure one leg and
    understate exactly the overhead this field exists to expose, so absent is
    the honest answer -- the same choice `seconds` and `txApply` make when a
    chain leg is missing.
    """
    cluster.reconcile()
    os.remove(records.started_path(300))

    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.5, attempt_seconds=60.0)
    cluster.reconcile()

    record = cluster.completed()['300']
    assert record['seconds'] == pytest.approx(60.0)
    assert record['wallSeconds'] is None


def test_disrupted_predecessor_without_final_medida_makes_txapply_absent(write_attempts):
    write_attempts(999, {
        1: ({'attemptSeconds': 900.0}, {'outcome': 'disrupted'}),
        2: ({'resumed': True, 'txApplySeconds': 3.0}, None),
    })

    assert attempts.tx_apply_for_range(999, 2) is None
    assert 'txApply' not in attempts.reconstruct_completed_profile(999, 2)


# --- counting causes, not attempts --------------------------------------------

def test_escalation_counts_ooms_not_attempts(write_attempts):
    # On spot most retries are evictions: 288 disruption retries against 7 OOM
    # retries on ssc-test 2026-07-30. Keying the exponent on the attempt index
    # meant a range disrupted three times then OOMing once jumped to
    # base * 1.5^4 -- a 5x request for one OOM, inflated fleet-wide.
    write_attempts(9, {1: (None, {'outcome': 'disrupted'}),
                 2: (None, {'outcome': 'disrupted'}),
                 3: (None, {'outcome': 'disrupted'}),
                 4: (None, {'outcome': 'oom'})})
    assert records._oom_count(9, 4) == 1, "three evictions were counted as escalations"
    write_attempts(9, {5: (None, {'outcome': 'oom'}), 6: (None, {'outcome': 'oom'})})
    assert records._oom_count(9, 6) == 3


def test_disk_escalation_counts_evictions_not_attempts(write_attempts, monkeypatch):
    """Same inflation as the OOM ladder, on the disk ladder.

    The budget check for an ephemeral eviction already counts causes; the SIZE
    did not, so a range disrupted four times then evicted once escalated as if
    it had been evicted five times.
    """
    monkeypatch.setattr(config, 'LIM_EPHEMERAL', '4Gi')
    monkeypatch.setattr(config, 'EPH_BUMP_FACTOR', 1.5)
    write_attempts(9, {1: (None, {'outcome': 'disrupted'}),
                 2: (None, {'outcome': 'disrupted'}),
                 3: (None, {'outcome': 'disrupted'}),
                 4: (None, {'outcome': 'disrupted'}),
                 5: (None, {'outcome': 'ephemeral'})})

    evictions = records._cause_count(9, 5, ('ephemeral',))
    assert evictions == 1, "four disruptions were counted as disk escalations"
    # One rung, not five: 4Gi -> 6Gi, where attempt-indexing gave 4Gi * 1.5^5.
    bytes_of = units.quantity_bytes
    assert bytes_of(sizing.eph_for_attempt(evictions + 1)) == bytes_of('6Gi')
    assert bytes_of(sizing.eph_for_attempt(evictions)) == bytes_of('4Gi'), \
        "the limit this attempt ran at"


def test_disk_escalation_climbs_on_each_real_eviction(write_attempts, monkeypatch):
    monkeypatch.setattr(config, 'LIM_EPHEMERAL', '4Gi')
    monkeypatch.setattr(config, 'EPH_BUMP_FACTOR', 1.5)
    write_attempts(9, {1: (None, {'outcome': 'ephemeral'}),
                 2: (None, {'outcome': 'ephemeral'})})

    assert records._cause_count(9, 2, ('ephemeral',)) == 2
    assert units.quantity_bytes(sizing.eph_for_attempt(3)) == units.quantity_bytes('9Gi')


def test_the_disk_ladder_is_capped(monkeypatch):
    monkeypatch.setattr(config, 'LIM_EPHEMERAL', '4Gi')
    monkeypatch.setattr(config, 'EPH_BUMP_FACTOR', 1.5)
    monkeypatch.setattr(config, 'EPH_ESCALATION_CAP', '20Gi')
    assert units.quantity_bytes(sizing.eph_for_attempt(99)) == units.quantity_bytes('20Gi')
