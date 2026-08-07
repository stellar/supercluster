"""Resource escalation ladders and the quantity arithmetic under them.

Everything here is a pure function of config, so the tests set the config and
call it. The budgets these ladders climb are asserted against the module
defaults -- the numbers a run gets when the chart passes nothing.
"""

import pytest

import config
import units
import sizing
import job_monitor as jm


@pytest.fixture
def mem(monkeypatch):
    def configure(lim='1000Mi', bump=None, cap='48Gi'):
        monkeypatch.setattr(config, 'REQ_MEM', lim)
        monkeypatch.setattr(config, 'MEM_BUMP_FACTOR',
                            config.MEM_BUMP_FACTOR if bump is None else bump)
        monkeypatch.setattr(config, 'MEM_ESCALATION_CAP', cap)
    return configure


@pytest.fixture
def eph(monkeypatch):
    def configure(lim='40Gi', bump=1.5, cap='200Gi'):
        monkeypatch.setattr(config, 'LIM_EPHEMERAL', lim)
        monkeypatch.setattr(config, 'EPH_BUMP_FACTOR', bump)
        monkeypatch.setattr(config, 'EPH_ESCALATION_CAP', cap)
    return configure


# --- quantity arithmetic -----------------------------------------------------

@pytest.mark.parametrize('quantity,want', [
    ('1024Ki', 1024 * 1024),
    ('9Gi', 9 * 1024**3),
    ('24000Mi', 24000 * 1024**2),
    ('1G', 1000**3),          # SI, not binary -- kubernetes accepts both
    ('1500', 1500),           # bare bytes
])
def test_kubernetes_quantities_are_read_in_the_right_base(quantity, want):
    assert units.quantity_bytes(quantity) == want


def test_a_size_is_always_rendered_back_in_mebibytes():
    # One unit everywhere means a limit can be compared to a request without
    # re-parsing, and Mi is fine-grained enough for the packing this run does.
    assert units.bytes_to_quantity(3 * 1024**3) == '3072Mi'
    assert units.bytes_to_quantity(0) == '1Mi', "a zero-byte limit is unschedulable"


# --- the memory ladder -------------------------------------------------------

@pytest.mark.parametrize('attempt,want', [(1, 1.0), (2, 1.5), (3, 2.25), (4, 3.375)])
def test_the_memory_escalation_ladder_compounds(mem, attempt, want):
    # 1.5x per OOM off what the attempt actually ran with. A factor of 1.0
    # would retry an OOM at the identical limit, forever.
    mem(lim='1000Mi', bump=1.5)
    assert sizing.mem_for_attempt(attempt, '1000Mi') == f"{int(1000 * want)}Mi"


def test_the_escalation_ladder_is_capped(mem):
    mem(lim='1000Mi', bump=1.5, cap='4Gi')
    assert sizing.mem_for_attempt(20, '1000Mi') == '4096Mi', "cap not applied"


def test_oom_escalation_starts_from_what_the_attempt_actually_had(mem):
    # Escalating a 209Mi profiled range off the configured 24000Mi limit jumps
    # to 36000Mi -- a 172x overshoot that discards the packing win on first OOM.
    mem(lim='24000Mi', bump=1.5)
    assert sizing.mem_for_attempt(2, '702Mi') == '1053Mi'
    assert sizing.mem_for_attempt(2) == '36000Mi'      # unprofiled keeps old behaviour


# --- the disk ladder ---------------------------------------------------------

def test_ephemeral_storage_escalates_and_caps_the_same_way(eph):
    eph(lim='40Gi', bump=1.5, cap='200Gi')
    assert sizing.eph_for_attempt(1) == '40960Mi'
    assert sizing.eph_for_attempt(2) == '61440Mi'
    assert sizing.eph_for_attempt(20) == '204800Mi', "cap not applied"


# --- the budgets the ladders are climbing ------------------------------------

def test_attempt_budgets_are_ordered_by_whose_fault_the_failure_was():
    # A genuinely broken range gets the middle budget. Anything the cluster did
    # to us gets the most -- on spot, evictions are routine and must not condemn
    # a range. A hang has no budget at all: a timeout is terminal.
    assert config.ATTEMPT_BUDGETS['oom'] < config.ATTEMPT_BUDGETS['disrupted'], (
        f"budgets out of order: range={config.ATTEMPT_BUDGETS['oom']} "
        f"disruption={config.ATTEMPT_BUDGETS['disrupted']}")
    assert config.ATTEMPT_BUDGETS['oom'] > 1, "a range that OOMs once could never escalate"
    assert config.ATTEMPT_BUDGETS['ephemeral'] > 1, "a range evicted on disk once could never grow"
    # Effectively unlimited on purpose: a healthy spot range can be evicted
    # dozens of times, and only a misclassification should ever reach the gate.
    assert config.ATTEMPT_BUDGETS['disrupted'] >= 100, \
        "spot eviction would condemn ranges at this budget"


def test_the_oom_budget_stops_short_of_the_cap_on_purpose():
    # 5 rungs is 1.5^4 = 5x the profile figure. A range needing more is broken,
    # not mis-sized, and chasing it to MEM_ESCALATION_CAP parks a whole node on
    # it. The price is that such a range is condemned -- which today aborts the
    # run, so this coupling is what must not be forgotten.
    n = config.ATTEMPT_BUDGETS['oom']
    assert 2 <= n <= 8, f"{n} rungs: below 2 cannot escalate, above 8 chases a broken range"
    assert config.MEM_BUMP_FACTOR ** (n - 1) >= 3.0, \
        "the ladder cannot even treble the request before giving up"


# --- the profile arithmetic --------------------------------------------------
#
# Added 2026-08-06 after mutation testing: every line below was EXECUTED by the
# suite and none of it was asserted. A margin applied as a division, an
# escalation ladder running backwards, and an inverted runtime weighting all
# left the suite green. These pin the shapes, not just the outcomes.
#
# This path runs whenever POOL_PREFIX is unset -- the chart default -- so it
# sizes every worker on a run that does not pass --pubnet-parallel-catchup-pool-
# prefix.

@pytest.fixture
def unpooled(monkeypatch):
    """Profile-driven sizing with the tier ladder switched off."""
    def configure(entries, **overrides):
        monkeypatch.setattr(config, 'POOL_PREFIX', '')
        monkeypatch.setattr(config, 'PROFILE', sorted(entries.items()))
        monkeypatch.setattr(config, '_SORTED_SECONDS', None)
        for k, v in overrides.items():
            monkeypatch.setattr(config, k, v)
    return configure


def test_each_escalation_rung_asks_for_more_than_the_one_below(mem, eph, monkeypatch):
    """A divide where the bump multiplies makes an OOM retry ask for LESS.

    The ladder exists so an OOMing range gets a bigger node; running it
    backwards retries the same range at a size it has already proved too small,
    burning its whole budget without ever changing the outcome.
    """
    monkeypatch.setattr(config, 'POOL_PREFIX', '')
    mem(lim='1000Mi', bump=1.5, cap='48Gi')
    eph(lim='40Gi', bump=1.5, cap='200Gi')

    for name, ladder in (('memory', sizing.mem_for_attempt),
                         ('ephemeral', sizing.eph_for_attempt)):
        sizes = [units.quantity_bytes(ladder(a)) for a in range(1, 6)]
        assert all(b > a for a, b in zip(sizes, sizes[1:])), \
            f"the {name} ladder does not climb: {sizes}"


def test_the_margin_multiplies_the_measured_peak(unpooled):
    """Exact bytes, because `peak * 1.15` and `peak / 1.15` both "work".

    Dividing by the margin requests 76% of what the range was measured using --
    an OOM on the attempt the profile was supposed to make safe.
    """
    rss = 2 * 1024 ** 3
    unpooled({300: {'peakAnonBytes': rss, 'seconds': 300.0}})

    want = (int(rss * config.PROFILE_MARGIN)
            + units.quantity_bytes(config.PROFILE_CACHE_HEADROOM)
            + units.quantity_bytes(config.PROFILE_RUNTIME_MEMORY_INSURANCE))
    assert sizing._profile_overrides(300, escalated=False)['memory'] == \
        units.bytes_to_quantity(want)


def test_the_disk_margin_multiplies_too(unpooled):
    disk = 20 * 1024 ** 3
    unpooled({300: {'peakEphemeralBytes': disk, 'seconds': 300.0}},
             LIM_EPHEMERAL='40Gi')

    want = (int(disk * config.PROFILE_MARGIN)
            + units.quantity_bytes(config.PROFILE_EPHEMERAL_HEADROOM)
            + units.quantity_bytes(config.PROFILE_RUNTIME_EPHEMERAL_INSURANCE))
    assert sizing._profile_overrides(300, escalated=False)['ephemeral-storage'] == \
        units.bytes_to_quantity(want)


def test_the_insurance_is_weighted_by_runtime_not_against_it(unpooled):
    """The longest range gets the whole allowance; half as long gets half.

    An inverted ratio hands the most disk to the ranges least at risk of
    running out of it, and the profile's own longest range the least.
    """
    rss = 2 * 1024 ** 3
    unpooled({300: {'peakAnonBytes': rss, 'seconds': 300.0},
              900: {'peakAnonBytes': rss, 'seconds': 600.0}})
    base = (int(rss * config.PROFILE_MARGIN)
            + units.quantity_bytes(config.PROFILE_CACHE_HEADROOM))
    full = units.quantity_bytes(config.PROFILE_RUNTIME_MEMORY_INSURANCE)

    assert sizing._profile_overrides(900, escalated=False)['memory'] == \
        units.bytes_to_quantity(base + full), "the longest range gets all of it"
    assert sizing._profile_overrides(300, escalated=False)['memory'] == \
        units.bytes_to_quantity(base + full // 2), "half the runtime, half the share"


def test_a_range_with_no_measured_runtime_gets_no_insurance(unpooled):
    """Insurance is priced off time-at-risk, so an unknown runtime buys none.

    Inverting the bail spends the whole allowance on exactly the ranges nothing
    is known about.
    """
    rss = 2 * 1024 ** 3
    unpooled({300: {'peakAnonBytes': rss}})

    want = (int(rss * config.PROFILE_MARGIN)
            + units.quantity_bytes(config.PROFILE_CACHE_HEADROOM))
    assert sizing._profile_overrides(300, escalated=False)['memory'] == \
        units.bytes_to_quantity(want)


def test_a_zero_allowance_turns_the_insurance_off(unpooled):
    """The documented way to disable it, so it has to reach zero exactly."""
    rss = 2 * 1024 ** 3
    unpooled({300: {'peakAnonBytes': rss, 'seconds': 300.0}},
             PROFILE_RUNTIME_MEMORY_INSURANCE='0')

    want = (int(rss * config.PROFILE_MARGIN)
            + units.quantity_bytes(config.PROFILE_CACHE_HEADROOM))
    assert sizing._profile_overrides(300, escalated=False)['memory'] == \
        units.bytes_to_quantity(want)
