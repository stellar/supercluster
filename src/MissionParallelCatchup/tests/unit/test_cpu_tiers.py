"""CPU request as a slack budget, keyed on rank rather than absolute seconds.

The request is not a demand estimate. Measured unthrottled 2026-07-30, replay
wants ~1.0 cores at every ledger position (1.04 at 63.7M, 0.96 at 43.2M) and is
80-95% of a job, so demand barely varies. What varies is how much throttling a
range can absorb, and that slack is free packing density: bin-packed, flat 1.0
needs 491 nodes / 3928 vCPU -- over the 2304 quota -- against 291 / 2328 tiered.

Rank rather than seconds because the absolute budget is set by the single
worst-throttled job: between two real runs it moved 1.79x while the median
range moved 1.55x, swinging the top two tiers from 127 ranges to 65.
"""

import pytest

import job_monitor as jm

TIERS = '85:0.5,98:0.75,99.5:1.0,100:1.25'
# 1000 ranges, 1s..1000s, so a range's value IS its percentile x 1000.
PROFILE = [(i, {'seconds': float(i)}) for i in range(1, 1001)]


@pytest.fixture
def tiered(monkeypatch):
    monkeypatch.setattr(jm, 'PROFILE_CPU_TIERS', TIERS)
    monkeypatch.setattr(jm, 'PROFILE', PROFILE)
    monkeypatch.setattr(jm, '_SORTED_SECONDS', None)


def test_tiering_is_off_unless_configured(monkeypatch):
    monkeypatch.setattr(jm, 'PROFILE_CPU_TIERS', '')
    assert jm._slack_cpu(500) is None


def test_the_cheap_bulk_gets_the_cheapest_tier(tiered):
    assert jm._slack_cpu(1) == '0.5'
    assert jm._slack_cpu(850) == '0.5'      # exactly the 85% cut


def test_each_band_maps_to_its_tier(tiered):
    assert jm._slack_cpu(851) == '0.75'     # just past 85%
    assert jm._slack_cpu(980) == '0.75'
    assert jm._slack_cpu(981) == '1.0'
    assert jm._slack_cpu(995) == '1.0'
    assert jm._slack_cpu(1000) == '1.25'    # the longest range


def test_an_unmeasured_range_gets_no_tier_at_all(tiered):
    """No usable runtime means no basis for a tier -- fall through to REQ_CPU.

    Returning the TOP tier here cost 206 vCPU on the 2026-07-31 run: 103 ranges
    lacked `seconds` not because they were new but because a resumed chain made
    their runtime unverifiable, and several were demonstrably small.
    """
    assert jm._slack_cpu(None) is None


@pytest.mark.parametrize('seconds', [0, -1, 'bad', float('nan'), float('inf')])
def test_an_invalid_runtime_safely_gets_no_tier(tiered, seconds):
    assert jm._slack_cpu(seconds) is None


def test_a_uniformly_slower_run_assigns_the_same_tiers(monkeypatch):
    """The property absolute-seconds keying does not have.

    Every range 3x slower must not shuffle anything: rank is unchanged, so the
    fleet needs the same shape. Under a `seconds <= longest` rule this is only
    true if the slowdown is perfectly uniform, which measurement shows it is not.
    """
    monkeypatch.setattr(jm, 'PROFILE_CPU_TIERS', TIERS)
    monkeypatch.setattr(jm, 'PROFILE', [(i, {'seconds': i * 3.0}) for i in range(1, 1001)])
    monkeypatch.setattr(jm, '_SORTED_SECONDS', None)
    assert jm._slack_cpu(850 * 3) == '0.5'
    assert jm._slack_cpu(981 * 3) == '1.0'
    assert jm._slack_cpu(1000 * 3) == '1.25'


def test_a_malformed_ladder_disables_tiering_rather_than_guessing(monkeypatch):
    # One list of pairs cannot desync the way two parallel lists could, but a
    # typo still has to fail safe rather than half-apply.
    monkeypatch.setattr(jm, 'PROFILE_CPU_TIERS', '85,0.5')
    assert jm._slack_cpu(100) is None


def test_cpu_is_requested_but_never_limited(monkeypatch, cluster):
    # A limit would cap the bucket phase, measured up to 2.53 cores and the one
    # part of a job that slicing cannot remove.
    prof = [(i, {'seconds': float(i)}) for i in range(1, 1001)]
    prof[299] = (300, {'seconds': 300.0, 'peakAnonBytes': 1 << 30})   # 30th pct
    monkeypatch.setattr(jm, 'PROFILE_CPU_TIERS', TIERS)
    monkeypatch.setattr(jm, 'PROFILE', prof)
    monkeypatch.setattr(jm, '_SORTED_SECONDS', None)
    r = jm._resources(end=300)
    assert r.requests['cpu'] == '0.5'
    assert not r.limits or 'cpu' not in r.limits


def test_a_one_range_profile_gives_that_range_the_top_tier(tiered, monkeypatch):
    # It is simultaneously the cheapest and the longest range measured, so the
    # 100th percentile is the honest answer -- not the cheapest tier.
    monkeypatch.setattr(jm, 'PROFILE', [(300, {'seconds': 1.0})])
    monkeypatch.setattr(jm, '_SORTED_SECONDS', None)
    assert jm._slack_cpu(1.0) == '1.25'
