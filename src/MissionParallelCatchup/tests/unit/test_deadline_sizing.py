"""How long an attempt may run before it is called wedged.

The deadline exists for ONE failure mode, reproduced on ssc-test 2026-07-30:
with an unreachable archive, stellar-core retries the bucket download forever.
It logs "Missing HAS for ledger N: maybe stale archive", re-selects a different
mirror and goes again -- RETRY_A_FEW is per archive, so the budget never
exhausts. Measured: 0 ledgers closed, 9 fetch failures in 2.5 min, no give-up
wording, no exit. Nothing but this deadline stops it.

One global number cannot bound it, because runtimes span 190x (p25 771s, max
5.9h): 3h killed 941 legitimate ranges, 12h kills none but lets a wedged 771s
range burn 56x its cost first. So take whichever bound is tighter.
"""

import pytest

import job_monitor as jm

PROFILE = [(100, {'seconds': 600.0}), (200, {'seconds': 10000.0})]


@pytest.fixture
def sized(monkeypatch):
    monkeypatch.setattr(jm, 'PROFILE', PROFILE)
    monkeypatch.setattr(jm, 'ATTEMPT_DEADLINE_SECONDS', 43200)
    monkeypatch.setattr(jm, 'PROFILE_DEADLINE_FACTOR', 3.0)


def test_disabled_by_default_keeps_the_configured_ceiling(monkeypatch):
    monkeypatch.setattr(jm, 'ATTEMPT_DEADLINE_SECONDS', 43200)
    monkeypatch.setattr(jm, 'PROFILE_DEADLINE_FACTOR', 0)
    assert jm._attempt_deadline(100) == 43200


def test_no_ceiling_and_no_factor_means_no_deadline(monkeypatch):
    monkeypatch.setattr(jm, 'ATTEMPT_DEADLINE_SECONDS', 0)
    monkeypatch.setattr(jm, 'PROFILE_DEADLINE_FACTOR', 0)
    assert jm._attempt_deadline(100) is None


def test_a_cheap_range_gets_a_tight_bound_not_the_ceiling(sized):
    # 600s x3 = 1800s. Under a 12h ceiling a wedged 10-minute range would
    # otherwise burn 72x its cost before anything noticed.
    assert jm._attempt_deadline(100) == 1800


def test_an_expensive_range_still_gets_room(sized):
    assert jm._attempt_deadline(200) == 30000      # 10000 x 3, under the ceiling


def test_the_ceiling_still_wins_when_it_is_tighter(monkeypatch):
    monkeypatch.setattr(jm, 'PROFILE', PROFILE)
    monkeypatch.setattr(jm, 'ATTEMPT_DEADLINE_SECONDS', 7200)
    monkeypatch.setattr(jm, 'PROFILE_DEADLINE_FACTOR', 3.0)
    assert jm._attempt_deadline(200) == 7200       # 30000 scaled, 7200 ceiling


def test_an_unprofiled_range_falls_back_to_the_ceiling(sized):
    # Newer than anything measured, so there is no honest estimate to tighten
    # with -- and it is the most expensive kind, so guessing low is the bad
    # direction.
    assert jm._attempt_deadline(999999) == 43200
