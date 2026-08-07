"""Dispatch order, and why longest-first is the one that shortens a run.

Makespan is bounded below by the single longest job: every range dispatched
after it is free, and every hour it starts late lands on the end of the run.
"""

import pytest

import config
import ranges
import job_monitor as jm

RANGES = [(600, 420), (500, 420), (400, 420), (300, 420)]   # generators emit tip-first


def _order(monkeypatch, mode, profile=None):
    monkeypatch.setattr(config, 'RANGE_ORDER', mode)
    monkeypatch.setattr(config, 'PROFILE', profile)
    return [e for e, _ in ranges._ordered(list(RANGES))]


def test_tip_first_is_unchanged(monkeypatch):
    assert _order(monkeypatch, 'tip-first') == [600, 500, 400, 300]


def test_oldest_first_reverses(monkeypatch):
    assert _order(monkeypatch, 'oldest-first') == [300, 400, 500, 600]


def test_longest_first_sorts_by_measured_seconds_not_position(monkeypatch):
    # The whole point: 400 is the expensive one even though 600 is nearer the
    # tip. Measured 2026-07-30, ranges at 41-45M ran as long as the tip on a
    # third of the memory, so position is a proxy that fails in the tail.
    prof = [(300, {'seconds': 10}), (400, {'seconds': 9000}),
            (500, {'seconds': 20}), (600, {'seconds': 100})]
    assert _order(monkeypatch, 'longest-first', prof) == [400, 600, 500, 300]


def test_an_unprofiled_range_sorts_first(monkeypatch):
    # profile_for returns the nearest measured end ABOVE the target and None
    # past its ceiling, so an unprofiled range is newer than anything ever
    # measured -- the most expensive kind. Unknown means assume worst.
    prof = [(300, {'seconds': 10}), (400, {'seconds': 9000})]
    assert _order(monkeypatch, 'longest-first', prof)[:2] == [600, 500]


def test_ties_keep_tip_first_order(monkeypatch):
    # Among ranges the profile cannot separate, position is still the better
    # guess, so a tie must not scramble them.
    prof = [(e, {'seconds': 50}) for e in (300, 400, 500, 600)]
    assert _order(monkeypatch, 'longest-first', prof) == [600, 500, 400, 300]


def test_no_profile_at_all_falls_back_to_tip_first(monkeypatch):
    # A run with no profile has nothing to sort on; every range is "unknown",
    # so the tie rule must leave the generator's order intact.
    assert _order(monkeypatch, 'longest-first', None) == [600, 500, 400, 300]
