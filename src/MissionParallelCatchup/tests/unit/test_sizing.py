"""Resource escalation ladders and the quantity arithmetic under them.

Everything here is a pure function of config, so the tests set the config and
call it. The budgets these ladders climb are asserted against the module
defaults -- the numbers a run gets when the chart passes nothing.
"""

import pytest

import job_monitor as jm


@pytest.fixture
def mem(monkeypatch):
    def configure(lim='1000Mi', bump=None, cap='48Gi'):
        monkeypatch.setattr(jm, 'LIM_MEM', lim)
        monkeypatch.setattr(jm, 'MEM_BUMP_FACTOR',
                            jm.MEM_BUMP_FACTOR if bump is None else bump)
        monkeypatch.setattr(jm, 'MEM_ESCALATION_CAP', cap)
    return configure


@pytest.fixture
def eph(monkeypatch):
    def configure(lim='40Gi', bump=1.5, cap='200Gi'):
        monkeypatch.setattr(jm, 'LIM_EPHEMERAL', lim)
        monkeypatch.setattr(jm, 'EPH_BUMP_FACTOR', bump)
        monkeypatch.setattr(jm, 'EPH_ESCALATION_CAP', cap)
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
    assert jm._quantity_bytes(quantity) == want


def test_a_size_is_always_rendered_back_in_mebibytes():
    # One unit everywhere means a limit can be compared to a request without
    # re-parsing, and Mi is fine-grained enough for the packing this run does.
    assert jm._bytes_to_quantity(3 * 1024**3) == '3072Mi'
    assert jm._bytes_to_quantity(0) == '1Mi', "a zero-byte limit is unschedulable"


def test_sizing_applies_the_margin_and_never_exceeds_the_limit():
    # 1 GB * 1.1, well under the cap
    assert jm._sized(1_000_000_000, 1.1, '10Gi') == '1049Mi'
    # capped: a huge peak cannot produce a request above its own limit
    assert jm._sized(50_000_000_000, 1.1, '8Gi') == '8192Mi'


# --- the memory ladder -------------------------------------------------------

@pytest.mark.parametrize('attempt,want', [(1, 1.0), (2, 1.5), (3, 2.25), (4, 3.375)])
def test_the_memory_escalation_ladder_compounds(mem, attempt, want):
    # 1.5x per OOM off what the attempt actually ran with. A factor of 1.0
    # would retry an OOM at the identical limit, forever.
    mem(lim='1000Mi', bump=1.5)
    assert jm.mem_for_attempt(attempt, '1000Mi') == f"{int(1000 * want)}Mi"


def test_the_escalation_ladder_is_capped(mem):
    mem(lim='1000Mi', bump=1.5, cap='4Gi')
    assert jm.mem_for_attempt(20, '1000Mi') == '4096Mi', "cap not applied"


def test_oom_escalation_starts_from_what_the_attempt_actually_had(mem):
    # Escalating a 209Mi profiled range off the configured 24000Mi limit jumps
    # to 36000Mi -- a 172x overshoot that discards the packing win on first OOM.
    mem(lim='24000Mi', bump=1.5)
    assert jm.mem_for_attempt(2, '702Mi') == '1053Mi'
    assert jm.mem_for_attempt(2) == '36000Mi'      # unprofiled keeps old behaviour


# --- the disk ladder ---------------------------------------------------------

def test_ephemeral_storage_escalates_and_caps_the_same_way(eph):
    eph(lim='40Gi', bump=1.5, cap='200Gi')
    assert jm.eph_for_attempt(1) == '40960Mi'
    assert jm.eph_for_attempt(2) == '61440Mi'
    assert jm.eph_for_attempt(20) == '204800Mi', "cap not applied"


# --- the budgets the ladders are climbing ------------------------------------

def test_attempt_budgets_are_ordered_by_whose_fault_the_failure_was():
    # A genuinely broken range gets the middle budget. Anything the cluster did
    # to us gets the most -- on spot, evictions are routine and must not condemn
    # a range. A hang has no budget at all: a timeout is terminal.
    assert jm.MAX_ATTEMPTS_PER_RANGE < jm.MAX_DISRUPTION_ATTEMPTS, (
        f"budgets out of order: range={jm.MAX_ATTEMPTS_PER_RANGE} "
        f"disruption={jm.MAX_DISRUPTION_ATTEMPTS}")
    assert jm.MAX_ATTEMPTS_PER_RANGE > 1, "a range that OOMs once could never escalate"
    assert jm.MAX_EPHEMERAL_ATTEMPTS > 1, "a range evicted on disk once could never grow"
    assert jm.MAX_DISRUPTION_ATTEMPTS >= 10, \
        "spot eviction would condemn ranges at this budget"


def test_the_oom_budget_stops_short_of_the_cap_on_purpose():
    # 5 rungs is 1.5^4 = 5x the profile figure. A range needing more is broken,
    # not mis-sized, and chasing it to MEM_ESCALATION_CAP parks a whole node on
    # it. The price is that such a range is condemned -- which today aborts the
    # run, so this coupling is what must not be forgotten.
    n = jm.MAX_ATTEMPTS_PER_RANGE
    assert 2 <= n <= 8, f"{n} rungs: below 2 cannot escalate, above 8 chases a broken range"
    assert jm.MEM_BUMP_FACTOR ** (n - 1) >= 3.0, \
        "the ladder cannot even treble the request before giving up"
