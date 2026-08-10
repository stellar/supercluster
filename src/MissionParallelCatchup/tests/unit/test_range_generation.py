"""The ledger range list, and the order it is dispatched in.

generate_ranges() must stay a pure function of config: dispatch derives the
full list on every reconcile, so a restart has to reproduce it exactly.
"""

import os

import pytest

import config
import ranges
import job_monitor as jm


@pytest.fixture
def build(monkeypatch):
    """Configure the generator and return a callable that runs it."""
    def configure(order='tip-first', parallelism=4,
                  start=39990000, latest=40000000, per_job=1000, overlap=320):
        monkeypatch.setattr(config, 'RANGE_ORDER', order)
        monkeypatch.setattr(config, 'PARALLELISM', parallelism)
        monkeypatch.setattr(config, 'STARTING_LEDGER', start)
        monkeypatch.setattr(config, 'LATEST_LEDGER_NUM', latest)
        monkeypatch.setattr(config, 'LEDGERS_PER_JOB', per_job)
        monkeypatch.setattr(config, 'OVERLAP_LEDGERS', overlap)
        return ranges.generate_ranges()
    return configure


def test_ranges_are_emitted_tip_first_by_default(build):
    r = build()
    assert r[0][0] > r[-1][0], "index 0 must be the tip"


def test_oldest_first_reverses_dispatch_without_dropping_ranges(build):
    # A profiling run wants the cheap early ranges measured first: the bucket
    # set only grows with ledger position, so tip-first front-loads the
    # expensive ones and an interrupted run profiles nothing cheap.
    tip = build(order='tip-first')
    old = build(order='oldest-first')
    assert old == list(reversed(tip))
    assert sorted(old) == sorted(tip), "reversing must not change the range set"


def test_every_range_carries_the_overlap_on_top_of_its_ledger_count(build):
    # The count is what the worker is asked to catch up, and it is always the
    # segment plus OVERLAP_LEDGERS -- measuring with overlap 0 measures nothing
    # the run will ever dispatch.
    r = build(per_job=1000, overlap=320)
    assert {count for _, count in r} == {1320}


def test_the_ranges_tile_the_ledger_space_with_no_gap(build):
    r = sorted(build(start=0, latest=10000, per_job=1000, overlap=320))
    ends = [end for end, _ in r]
    assert ends == list(range(1000, 10001, 1000))
    assert ends[-1] == 10000, "the tip must be covered"


def test_a_short_tail_segment_is_not_padded_past_the_start(build):
    # The last segment is min(remaining, seg_size), so a range list over a span
    # that does not divide evenly must not reach below STARTING_LEDGER.
    r = build(start=0, latest=2500, per_job=1000, overlap=0)
    assert sorted(r) == [(500, 500), (1500, 1000), (2500, 1000)]


def test_longest_first_is_inert_without_a_profile(build, monkeypatch):
    """Ordering is driven by RANGE_ORDER, never by profile detection.

    profile_for returns None for every range when no profile is loaded, so
    every sort key ties and Python's stable sort leaves the generator's own
    tip-first order untouched. The two flags are independent in configuration
    and only coupled in effect -- which is why validate_config() refuses the
    combination at startup rather than letting the flag look set and do nothing.
    """
    monkeypatch.setattr(config, 'PROFILE', {})
    assert build(order='longest-first') == build(order='tip-first')


@pytest.mark.parametrize('order', ['tipfirst', 'longest', '', 'TIP-FIRST'])
def test_an_unrecognised_order_fails_instead_of_becoming_tip_first(build, order):
    with pytest.raises(ValueError, match='RANGE_ORDER'):
        build(order=order)


# --- validate_config: the startup preflight ----------------------------------
#
# These checks exist at startup specifically because the reconcile loop catches
# and logs every exception then sleeps. A raise reached from inside it is an
# infinite log loop that never dispatches, so "fails loudly" depends entirely on
# validate_config being called from __main__ before the thread starts.

@pytest.fixture
def preflight(monkeypatch):
    def configure(order='tip-first', profile=None):
        monkeypatch.setattr(config, 'RANGE_ORDER', order)
        monkeypatch.setattr(config, 'PROFILE', profile)
        return jm.validate_config
    return configure


def test_valid_config_passes(preflight):
    preflight(order='oldest-first')()


def test_preflight_rejects_an_unknown_order(preflight):
    with pytest.raises(ValueError, match='RANGE_ORDER'):
        preflight(order='longest')()


def test_preflight_rejects_longest_first_without_a_profile(monkeypatch, tmp_path):
    """The check moved to /start: the profile arrives with the POST, so startup
    is too early to judge it. Rejecting there fails the driver fast instead of
    dispatching a run whose ordering silently degrades to tip-first."""
    monkeypatch.setattr(config, 'RANGE_ORDER', 'longest-first')
    monkeypatch.setattr(config, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(config, 'RUN_PATH', str(tmp_path / 'run.json'))

    with pytest.raises(ValueError, match='longest-first requires a profile'):
        jm.start_run({"range": {'startingLedger': 0, 'latestLedgerNum': 1000, 'ledgersPerJob': 100}})

    # A profile with ranges is accepted, and nothing is written until it passes.
    jm.start_run({'range': {'startingLedger': 0, 'latestLedgerNum': 1000, 'ledgersPerJob': 100}, 'profile': {'ranges': {'300': {'seconds': 1.0}}}})
    assert config.PROFILE == [(300, {'seconds': 1.0})]


def test_preflight_allows_longest_first_with_a_profile(preflight):
    preflight(order='longest-first', profile=[(40000000, {'seconds': 900.0})])()


def test_the_preflight_runs_before_anything_is_dispatched(monkeypatch, tmp_path):
    """Validation moved to /start, which is the first moment the config is
    whole. It still has to bind before dispatch: a run that is misconfigured
    must be refused, not started and then discovered."""
    monkeypatch.setattr(config, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(config, 'RUN_PATH', str(tmp_path / 'run.json'))
    monkeypatch.setattr(config, 'RANGE_ORDER', 'nonsense')

    with pytest.raises(ValueError, match='RANGE_ORDER must be one of'):
        jm.start_run({"range": {'startingLedger': 0, 'latestLedgerNum': 1000, 'ledgersPerJob': 100}})

    # Rejected, so nothing was written and no run can proceed from it.
    assert not os.path.exists(config.RUN_PATH)


def jm_source():
    import inspect
    return inspect.getsource(jm)
