"""CPU request as a slack budget, not a demand estimate.

Measured unthrottled on ssc-test 2026-07-30: replay wants ~1.0 cores at every
ledger position (1.04 at 63.7M, 0.96 at 43.2M) and replay is 80-95% of a job,
so demand barely varies. What varies is how much throttling a range can absorb
before it stops finishing inside the longest job's shadow -- and that slack is
free packing density. 3267 of 3859 profiled ranges still fit at 0.5 cores;
tiering took the fleet from 491 nodes / 3928 vCPU (over the 2304 quota) to 291
nodes / 2328 vCPU at the same 3.13h makespan.
"""

import pytest

import job_monitor as jm

TIERS = '0.5,0.75,1.0,1.25'
SLOW = '1.53,1.17,1.06,1.0'
FLOOR = 10000.0          # the longest range in the pretend profile


@pytest.fixture
def tiered(monkeypatch):
    monkeypatch.setattr(jm, 'PROFILE_CPU_TIERS', TIERS)
    monkeypatch.setattr(jm, 'PROFILE_CPU_SLOWDOWN', SLOW)
    monkeypatch.setattr(jm, '_PROFILE_FLOOR', FLOOR)


def test_tiering_is_off_unless_configured(monkeypatch):
    monkeypatch.setattr(jm, 'PROFILE_CPU_TIERS', '')
    assert jm._slack_cpu(500) is None


def test_a_short_range_gets_the_cheapest_tier(tiered):
    # 500s even at 1.53x slowdown is 765s, far inside a 10000s floor.
    assert jm._slack_cpu(500) == '0.5'


def test_the_floor_setting_range_gets_the_top_tier(tiered):
    # Nothing slower than saturation fits, so it must not be throttled at all.
    assert jm._slack_cpu(FLOOR) == '1.25'


def test_each_tier_is_the_cheapest_that_still_fits(tiered):
    # 7000 * 1.53 = 10710 > floor, but 7000 * 1.17 = 8190 fits -> 0.75.
    assert jm._slack_cpu(7000) == '0.75'
    # 9000 * 1.17 = 10530 > floor, 9000 * 1.06 = 9540 fits -> 1.0.
    assert jm._slack_cpu(9000) == '1.0'


def test_an_unmeasured_range_gets_the_top_tier(tiered):
    # Matches the dispatch order: unprofiled means newer than anything measured,
    # so assume worst rather than assume average.
    assert jm._slack_cpu(None) == '1.25'


def test_the_floor_comes_from_the_profile_not_a_constant(monkeypatch):
    # It has to move on its own as the chain grows.
    monkeypatch.setattr(jm, 'PROFILE', [(100, {'seconds': 42}), (200, {'seconds': 900})])
    monkeypatch.setattr(jm, '_PROFILE_FLOOR', None)
    assert jm.profile_floor() == 900


def test_cpu_is_requested_but_never_limited(tiered, monkeypatch, cluster):
    # A limit would cap the bucket phase, which measured up to 2.53 cores and is
    # the one part of a job that slicing cannot remove.
    monkeypatch.setattr(jm, 'PROFILE', [(300, {'seconds': 500, 'peakAnonBytes': 1 << 30})])
    monkeypatch.setattr(jm, '_PROFILE_FLOOR', FLOOR)
    r = jm._resources(end=300)
    assert r.requests['cpu'] == '0.5'
    assert 'cpu' not in r.limits
