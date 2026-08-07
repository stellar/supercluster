"""The attempt deadline is flat, and must stay flat.

The deadline exists for ONE failure mode, reproduced on ssc-test 2026-07-30:
with an unreachable archive, stellar-core retries the bucket download forever.
It logs "Missing HAS for ledger N: maybe stale archive", re-selects a different
mirror and goes again -- RETRY_A_FEW is per archive, so the budget never
exhausts. Measured: 0 ledgers closed, 9 fetch failures in 2.5 min, no give-up
wording, no exit. Nothing but this deadline stops it.

Scaling it by each range's profiled runtime was tried and removed, and this
file is the guard against it coming back. A deadline has to bound a range's
WORST case; a profile only offers a neighbour's TYPICAL case. Range keys are
anchored to the network tip, so a profile from an earlier run matches ZERO keys
exactly and every lookup lands on a neighbour -- and ~2% of neighbours are
3-38x cheaper than their surroundings. Backtested across that real grid offset
(run4 profile -> r5 actuals, 3983 ranges): a 2x factor falsely kills 134
ranges, 4x kills 46, 6x kills 21. Flat 12h kills none.

The asymmetry is what settles it. A false kill loses a range, and a timeout is
terminal, so it fails the whole mission. A genuine wedge holds ONE slot of
1092-1500 for 12h, about 0.1% of a run's capacity.

Asserted against the Jobs reconcile actually creates, not against a helper.
"""

import config
import job_monitor as jm

DEADLINE = 43200

# The two ranges the fixture dispatches (PARALLELISM 2, tip-first), given
# measured costs that differ by 17x. Under the removed scaling these produced
# deadlines of 1800s and 30000s; they must now be identical.
PROFILE = [(200, {'seconds': 600.0}), (300, {'seconds': 10000.0})]


def _deadline_of(cluster, end, attempt=1):
    return cluster.k8s.job(jm.job_name(int(end), attempt)).spec.active_deadline_seconds


def test_the_cheapest_and_costliest_ranges_get_the_same_deadline(cluster, monkeypatch):
    """The regression guard. A 600s range and a 10000s range are bounded alike.

    Tightening the cheap one is exactly what killed 134 ranges in the backtest:
    its `seconds` came from a neighbour, and the neighbour was wrong.
    """
    monkeypatch.setattr(config, 'PROFILE', PROFILE)
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()

    assert _deadline_of(cluster, 200) == DEADLINE
    assert _deadline_of(cluster, 300) == DEADLINE


def test_an_unprofiled_range_gets_the_same_deadline_too(cluster, monkeypatch):
    """No profile at all changes nothing -- there is nothing to scale by."""
    monkeypatch.setattr(config, 'PROFILE', [])
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', DEADLINE)
    cluster.reconcile()

    assert _deadline_of(cluster, 300) == DEADLINE


def test_zero_disables_the_deadline_entirely(cluster, monkeypatch):
    """0 must mean absent, not 0 -- a zero-second deadline kills every attempt
    the moment it is created."""
    monkeypatch.setattr(config, 'PROFILE', PROFILE)
    monkeypatch.setattr(config, 'ATTEMPT_DEADLINE_SECONDS', 0)
    cluster.reconcile()

    assert _deadline_of(cluster, 300) is None
