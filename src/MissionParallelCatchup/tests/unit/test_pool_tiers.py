"""Nodepool routing: memory picks the pool, and the pool is the whole sizing.

Replaces the cpu-ladder tests. The ladder tuned cpu REQUESTS, which measurement
showed were not buying throughput -- replay draws ~1.05 cores whatever it is
given, and is flat in core count from 2 upward (+2.8% at 2->4, +1.5% at 4->8,
against +16% for AMD-over-Intel at fixed cores). What a request actually bought
was neighbours-per-node. Memory is the dimension that FAILS rather than slows:
a working set that does not fit is OOMKilled.
"""

import pytest

import config
import records
import sizing
import job_monitor as jm

GiB = 1024 ** 3
TIERS = '0:subdwarf,0.79:dwarf,1.61:subgiant,3.87:giant,8.85:supergiant,18.38:hypergiant,:supernova'
CPU = ('subdwarf:0.85,dwarf:0.85,subgiant:1.85,giant:1.85,supergiant:1.85,hypergiant:1.85,supernova:3.80,protostar:1.85,nebula:3.80')
# vCPU of the smallest shape in each tier's pool. hypergiant, protostar and
# supernova used to sit on x8i, which put their smallest shape below the rest of
# the tier -- hypergiant read 4 off an x8i.xlarge at w80 while Karpenter served
# 8-vCPU 2xlarges from w100. The x8i spot pools were removed on 2026-08-04, so
# each tier's smallest shape is now also its top-weighted one.
VCPU = ('subdwarf:2,dwarf:2,subgiant:4,giant:4,supergiant:4,hypergiant:8,supernova:16,protostar:8,nebula:8')
# 50% of each tier node's ALLOCATABLE, which is what both isolates the pod and
# lets it schedule -- see test_the_request_is_half_the_node.
MEM = ('subdwarf:1280Mi,dwarf:1280Mi,subgiant:2816Mi,giant:6656Mi,supergiant:14336Mi,hypergiant:29696Mi,supernova:60416Mi,protostar:29696Mi,nebula:14336Mi')


@pytest.fixture
def pooled(monkeypatch):
    monkeypatch.setattr(config, 'POOL_TIERS', TIERS)
    monkeypatch.setattr(config, 'POOL_CPU', CPU)
    monkeypatch.setattr(config, 'POOL_VCPU', VCPU)
    monkeypatch.setattr(config, 'POOL_PREFIX', 'catchup')
    monkeypatch.setattr(config, 'POOL_UNPROFILED', 'protostar')
    monkeypatch.setattr(config, 'POOL_NO_PROFILE', 'nebula')
    monkeypatch.setattr(config, 'POOL_MEM', MEM)


def _profile(entries):
    """entries: {end: {...}} -> the sorted (end, rec) list PROFILE holds."""
    return sorted(entries.items())


def _profiled(monkeypatch, entries):
    monkeypatch.setattr(config, 'PROFILE', _profile(entries))


def _pool(monkeypatch, anon, ws=None, end=10, **kw):
    """Route one range measured at these peaks."""
    peaks = {'peakAnonBytes': int(anon)}
    if ws is not None:
        peaks['peakWorkingSetBytes'] = int(ws)
    _profiled(monkeypatch, {end: peaks})
    return sizing.pool_for(end, **kw)


def test_a_range_lands_in_the_tier_its_working_set_fits(pooled, monkeypatch):
    _profiled(monkeypatch, {
        100: {'peakAnonBytes': int(0.20 * GiB)},
        200: {'peakAnonBytes': int(1.50 * GiB)},
        300: {'peakAnonBytes': int(3.00 * GiB)},
        400: {'peakAnonBytes': int(7.00 * GiB)},
        500: {'peakAnonBytes': int(16.0 * GiB)},
        600: {'peakAnonBytes': int(40.0 * GiB)},
    })
    # subdwarf's cut is 0, so even the smallest range falls through to dwarf
    assert sizing.pool_for(100) == 'dwarf'
    assert sizing.pool_for(200) == 'subgiant'
    assert sizing.pool_for(300) == 'giant'
    assert sizing.pool_for(400) == 'supergiant'
    assert sizing.pool_for(500) == 'hypergiant'
    assert sizing.pool_for(600) == 'supernova'


def test_nothing_is_ever_routed_to_subdwarf(pooled, monkeypatch):
    """Its cut is 0, and no range can satisfy `gib < 0`.

    The tier stays defined and provisionable -- the pools exist -- but c8a.medium
    has 1.42Gi allocatable, and after daemonsets that cannot hold any range the
    profile actually contains. Emptying it by cut rather than deleting it keeps
    the bottom of the ladder available to experiment with.
    """
    _profiled(monkeypatch, {
        10: {'peakAnonBytes': 1},                       # 1 byte
        20: {'peakAnonBytes': int(0.27 * GiB)},         # the profile's true minimum
    })
    assert sizing.pool_for(10) == 'dwarf'
    assert sizing.pool_for(20) == 'dwarf'


def test_the_cut_is_exclusive_so_a_range_never_lands_on_a_node_it_fills(pooled, monkeypatch):
    """A range exactly AT a cut belongs in the tier above.

    The cut is node_usable/1.60, so a range sitting on it would have exactly the
    p99 margin and nothing more. Being one byte over must move it up, not leave
    it to be the range that proves the margin was too thin.
    """
    _profiled(monkeypatch, {
        10: {'peakAnonBytes': int(0.7899 * GiB)},
        20: {'peakAnonBytes': int(0.7901 * GiB)},
    })
    assert sizing.pool_for(10) == 'dwarf'
    assert sizing.pool_for(20) == 'subgiant'
    # The comparison is `<`, so a range sitting exactly on a cut goes UP. Not
    # asserted at the exact byte: 0.79 GiB is not representable, and pinning the
    # test to a float's rounding would make it about IEEE754 rather than about
    # which side of the boundary a range belongs on.


def test_a_range_past_the_top_of_the_profile_goes_to_protostar(pooled, monkeypatch):
    """Unprofiled means NEWEST, and the newest ledgers are the densest.

    profile_for returns the nearest measured end ABOVE the target, so falling
    off the end means this range is newer than anything ever measured. It gets a
    rich pool rather than an average one.
    """
    _profiled(monkeypatch, {10: {'peakAnonBytes': int(0.50 * GiB)}})
    assert sizing.pool_for(999) == 'protostar'


def test_no_profile_at_all_goes_to_nebula(pooled, monkeypatch):
    monkeypatch.setattr(config, 'PROFILE', [])
    assert sizing.pool_for(10) == 'nebula'


def test_an_entry_with_no_memory_measurement_is_treated_as_unprofiled(pooled, monkeypatch):
    """Sizing needs a measurement, and `seconds` is not one.

    A record can carry a runtime but no peak -- reconstruction omits what it
    cannot verify. Guessing a tier from runtime would reintroduce exactly the
    cpu-ladder mistake: sizing memory off a dimension that does not predict it
    (peakEphemeral/anon correlate r2 0.32).
    """
    _profiled(monkeypatch, {10: {'seconds': 9000.0}})
    assert sizing.pool_for(10) == 'protostar'


def test_an_oom_promotes_the_pool_not_just_the_request(pooled, monkeypatch):
    """The whole point of tier escalation.

    Raising the request while the pod stays pinned to a tier whose nodes cannot
    hold it produces a pod that can never schedule -- Pending forever, which
    reads as a hang rather than a failure.
    """
    _profiled(monkeypatch, {10: {'peakAnonBytes': int(0.50 * GiB)}})
    assert sizing.pool_for(10, rungs=0) == 'dwarf'
    assert sizing.pool_for(10, rungs=1) == 'subgiant'
    assert sizing.pool_for(10, rungs=2) == 'giant'
    assert sizing.pool_for(10, rungs=3) == 'supergiant'


def test_only_ooms_climb_the_ladder_not_every_retry(pooled, monkeypatch):
    """A spot reclaim is not evidence the range needed a bigger node.

    Promoting on attempt number put 65 ranges onto 8-vCPU supernova nodes during
    the 2026-08-03 spot run whose attempt-1 verdict was `timeout` -- they
    belonged on 4-vCPU hypergiant, so it burned ~260 vCPU of a 2304 quota
    escalating away from a problem that was never memory. Reclaims, disruptions
    and timeouts all produce retries; only an OOM says the tier was too small.
    """
    _profiled(monkeypatch, {10: {'peakAnonBytes': int(0.50 * GiB)}})
    monkeypatch.setattr(records, '_oom_count', lambda end, attempt: 0)
    for attempt in (1, 2, 3, 9):
        assert sizing.pool_for(10, attempt=attempt) == 'dwarf', \
            f"attempt {attempt} climbed a tier without an OOM"
    monkeypatch.setattr(records, '_oom_count', lambda end, attempt: 2)
    assert sizing.pool_for(10, attempt=3) == 'giant'


def test_promotion_counts_ooms_from_disk_when_rungs_is_not_given(pooled, monkeypatch):
    _profiled(monkeypatch, {10: {'peakAnonBytes': int(0.50 * GiB)}})
    seen = {}
    def fake(end, attempt):
        seen['attempt'] = attempt
        return 1
    monkeypatch.setattr(records, '_oom_count', fake)
    assert sizing.pool_for(10, attempt=4) == 'subgiant'
    # attempts BEFORE this one -- this attempt has not run, so its own outcome
    # cannot be on disk yet.
    assert seen['attempt'] == 3


def test_promotion_stops_at_the_top_instead_of_running_off_the_ladder(pooled, monkeypatch):
    _profiled(monkeypatch, {10: {'peakAnonBytes': int(40.0 * GiB)}})
    assert sizing.pool_for(10, rungs=0) == 'supernova'
    assert sizing.pool_for(10, rungs=8) == 'supernova'


def test_the_off_ladder_pools_escalate_straight_to_the_top(pooled, monkeypatch):
    """nebula and protostar are not rungs, so there is nothing to walk.

    Both hold ranges whose size is unknown, so an OOM says the guess was too
    small with no information about by how much. The top tier is the only answer
    that cannot be wrong again for the same reason.
    """
    monkeypatch.setattr(config, 'PROFILE', [])
    assert sizing.pool_for(10, rungs=1) == 'supernova'
    _profiled(monkeypatch, {10: {'peakAnonBytes': GiB}})
    assert sizing.pool_for(999, rungs=1) == 'supernova'


def test_the_request_lands_exactly_two_pods_per_node(pooled):
    """Two per node comes from the arithmetic, not from goodwill.

    The ladder ran one-pod-per-node until 2026-08-04, when every spot pool's
    instance size was doubled to test whether a neighbour is worth more than a
    dedicated node. Measured that day: a pod on a 4-vCPU node beat the same pod
    on a 2-vCPU node by 25% (x8i.large 0.74 vs x8i.xlarge 1.07 against profile,
    same tier, same silicon) while drawing under one core -- so the replay
    thread is not what wants the extra cores, and a neighbour may be able to
    use them without costing the first pod.

    Two pods must FIT (2*req + daemonsets <= allocatable) and three must NOT.
    Getting the second condition wrong is the expensive one: at the old claims
    against doubled nodes, 3-4 pods would pack per node and the isolation the
    whole ladder exists for is gone without anything failing.

    Sized off ALLOCATABLE, not nameplate. On the small nodes the gap decides the
    outcome: a 2Gi c8a.medium allocates 1181Mi and the scheduler was measured
    counting 1477Mi for a 983Mi request. A nameplate-derived request fit the
    node on paper and left 24 pods Pending with "no instance type has enough
    resources".
    """
    # MEASURED on live ssc-test nodes 2026-08-03. These are the same numbers as
    # before the doubling, shifted one tier up -- what used to be supergiant's
    # 16Gi node is now giant's. 128Gi is extrapolated from the 64Gi measurement
    # at the same 94% ratio; nothing that large has run yet.
    ALLOC = {'dwarf': 2798, 'subgiant': 6502, 'giant': 14654,
             'supergiant': 30259, 'hypergiant': 61604, 'supernova': 124000}
    OVERHEAD = 154          # measured daemonset requests on a live node
    for tier, alloc in ALLOC.items():
        req = int(sizing.pool_memory(tier).removesuffix('Mi'))
        assert 2 * req + OVERHEAD <= alloc, f"{tier}: a second pod will not schedule"
        assert 3 * req + OVERHEAD > alloc, f"{tier}: three pods would fit"


def test_every_routable_tier_has_a_request(pooled):
    # A tier with no entry silently keeps the flat configured request, which is
    # both too small to isolate and unrelated to the node it landed on.
    # cpu claims are checked against the real chart values by
    # test_the_chart_ships_a_coherent_pool_ladder; POOL_MEM is not, so this is
    # the only thing standing between a promoted tier and the flat request.
    for _, tier in sizing._parsed_pool_tiers():
        assert sizing.pool_memory(tier), f"tier {tier} has no memory request"
    for off_ladder in ('protostar', 'nebula'):
        assert sizing.pool_memory(off_ladder)


def test_an_empty_prefix_disables_pooling_entirely(monkeypatch):
    """The change has to be opt-in: an unset prefix is exactly today's run."""
    monkeypatch.setattr(config, 'POOL_PREFIX', '')
    _profiled(monkeypatch, {10: {'peakAnonBytes': GiB}})
    assert sizing.pool_for(10) is None


def test_a_malformed_cpu_map_falls_back_rather_than_crashing(pooled, monkeypatch):
    monkeypatch.setattr(config, 'POOL_CPU', 'dwarf:notanumber,giant:1.1')
    assert sizing.pool_cpu('dwarf') is None
    assert sizing.pool_cpu('giant') == 1.1


def test_pooled_memory_carries_no_margin_because_the_node_carries_it(pooled, monkeypatch, cluster):
    """PROFILE_MARGIN and friends existed to keep a pod under its own LIMIT.

    There is no memory limit any more and the pod owns the node, so a margin in
    the REQUEST constrains nothing the kubelet acts on -- it only wastes
    schedulable space. The 1.60x lives in the node size, where node pressure can
    actually enforce it.
    """
    _profiled(monkeypatch, {300: {'peakAnonBytes': int(0.50 * GiB), 'seconds': 300.0}})
    r = jm._resources(end=300)
    # dwarf's half-node request, not 0.50 * PROFILE_MARGIN + headroom + insurance
    assert r.requests['memory'] == '1280Mi'
    assert r.requests['cpu'] == 0.85
    assert not r.limits or 'memory' not in r.limits


def test_an_escalated_retry_requests_the_tier_it_was_promoted_to(pooled, monkeypatch, cluster):
    """The label and the request have to agree.

    The affinity path knows the attempt, so an OOM retry lands on the promoted
    pool. If the sizing path did not, the pod would arrive at a supergiant node
    still asking for dwarf's memory -- under-requesting on the very node it was
    escalated onto, and leaving room for a second pod on a tier whose whole
    purpose is one pod per node.
    """
    _profiled(monkeypatch, {300: {'peakAnonBytes': int(0.50 * GiB), 'seconds': 300.0}})
    # every prior attempt OOMed: pool_for asks for attempts BEFORE this one
    monkeypatch.setattr(records, '_oom_count', lambda end, attempt: attempt)
    first = jm._resources(end=300, attempt=1)
    assert first.requests['memory'] == '1280Mi'      # dwarf
    assert first.requests['cpu'] == 0.85

    third = jm._resources(end=300, attempt=3)
    assert sizing.pool_for(300, attempt=3) == 'giant'
    assert third.requests['memory'] == '6656Mi'     # giant
    assert third.requests['cpu'] == 1.85


def test_escalation_does_not_opt_out_of_the_profile_when_pooled(pooled, monkeypatch, cluster):
    """Unpooled, an escalated request outranks the profile and short-circuits it.

    Pooled, the promotion IS the escalation -- so short-circuiting would hand
    the pod the flat configured request instead of the promoted tier's cut.
    """
    _profiled(monkeypatch, {300: {'peakAnonBytes': int(0.50 * GiB), 'seconds': 300.0}})
    # every prior attempt OOMed: pool_for asks for attempts BEFORE this one
    monkeypatch.setattr(records, '_oom_count', lambda end, attempt: attempt)
    # `mem` set is what marks a retry as escalated
    r = jm._resources(mem='9999Mi', end=300, attempt=2)
    assert r.requests['memory'] == '2816Mi'         # subgiant


# --- cache bump ------------------------------------------------------------
#
# peakAnonBytes decides which node can HOLD a range; peakWorkingSetBytes decides
# whether that node can CACHE it. The two diverge by a median 2.5x and up to
# 10.4x across the profile, so a range can sit safely inside its tier's memory
# and still thrash. Measured on ssc-test 2026-08-03, one range on two 2-vCPU
# Intel nodes differing only in RAM:
#
#     m8in.large  8 GiB   540 reads/ledger   21% iowait   1.86 lps
#     r8in.large 16 GiB    65 reads/ledger    7% iowait   3.14 lps
#
# 44648511's real numbers are used throughout so these tests fail if the ladder
# ever moves such that the range we physically measured stops being promoted.
MEASURED_ANON = int(2.63 * GiB)     # -> giant  (1.61 <= 2.63 < 3.87)
MEASURED_WS = int(18.31 * GiB)      # -> reaches supergiant and beyond


def test_a_range_that_cannot_cache_its_working_set_moves_up_one_rung(pooled, monkeypatch):
    assert sizing._tier_for_bytes(MEASURED_ANON) == 'giant'
    assert _pool(monkeypatch, MEASURED_ANON, MEASURED_WS) == 'supergiant'


def test_the_bump_is_one_rung_even_when_the_working_set_wants_two(pooled, monkeypatch):
    """Clearing the thrash cliff is the goal, not fitting the working set.

    18.31 GiB would land in hypergiant on its own, and hypergiant is two rungs
    from giant. The measured range reaches full profile rate on supergiant while
    still 1.29x UNDER its working set, so the extra rung buys nothing and costs
    a doubling of cores.
    """
    assert sizing._tier_for_bytes(MEASURED_WS) == 'hypergiant'
    assert _pool(monkeypatch, MEASURED_ANON, MEASURED_WS) == 'supergiant'


def test_a_working_set_that_stays_inside_its_tier_is_not_promoted(pooled, monkeypatch):
    assert sizing._tier_for_bytes(int(3.50 * GiB)) == 'giant'   # same tier as the anon
    assert _pool(monkeypatch, 2.00 * GiB, 3.50 * GiB) == 'giant'


def test_only_the_supergiant_rung_ships_open(pooled, monkeypatch):
    """supergiant->hypergiant runs by default; hypergiant->supernova does not.

    Both cross a vCPU class now. Until 2026-08-04 hypergiant listed x8i.xlarge
    (4 vCPU) at w80 beneath two 8-vCPU rungs, so pool_vcpu -- which reads the
    SMALLEST shape -- reported 4 and supergiant->hypergiant priced as free, while
    Karpenter tried w100 first and the promotion really cost 4->8. Removing the
    x8i spot pools made the map honest: supergiant 4, hypergiant 8, supernova 16.

    So supergiant->hypergiant is now carried by POOL_CROSS_RUNGS rather than by
    the guard, and hypergiant->supernova is denied outright -- with x8i.2xlarge
    gone, supernova's only spot shapes are 4xlarges, so that rung buys 8->16 vCPU
    for a promotion decided by working set, which does not predict throughput.
    """
    _profiled(monkeypatch, {
        10: {'peakAnonBytes': int(7.00 * GiB), 'peakWorkingSetBytes': int(30.0 * GiB)},
        20: {'peakAnonBytes': int(16.0 * GiB), 'peakWorkingSetBytes': int(40.0 * GiB)},
    })
    assert config.POOL_BLOCK_RUNGS == 'hypergiant->supernova'
    assert sizing.pool_vcpu('supergiant') != sizing.pool_vcpu('hypergiant'), \
        "the rung crosses a class; the whitelist is what carries it, not the guard"
    assert sizing.pool_for(10) == 'hypergiant'
    assert sizing.pool_for(20) == 'hypergiant'      # denied, stays put

    # dropping the denylist is NOT enough to reopen the supernova rung: it still
    # crosses 8->16, so it also needs a POOL_CROSS_RUNGS entry
    monkeypatch.setattr(config, 'POOL_BLOCK_RUNGS', '')
    assert sizing.pool_for(20) == 'hypergiant'
    monkeypatch.setattr(config, 'POOL_CROSS_RUNGS',
                        'supergiant->hypergiant,hypergiant->supernova')
    assert sizing.pool_for(20) == 'supernova'


def test_a_blocked_rung_beats_the_crossing_whitelist(pooled, monkeypatch):
    """Deny wins over allow, so one stale env cannot silently re-open a rung."""
    monkeypatch.setattr(config, 'POOL_BLOCK_RUNGS', 'hypergiant->supernova')
    monkeypatch.setattr(config, 'POOL_CROSS_RUNGS', 'hypergiant->supernova')
    assert _pool(monkeypatch, 16.0 * GiB, 40.0 * GiB, end=20) == 'hypergiant'


def test_the_whitelist_only_opens_the_rung_it_names(pooled, monkeypatch):
    """An exception must not become a blanket "ignore vCPU classes" switch.

    dwarf->subgiant also crosses a class (1 -> 2 vCPU) and is deliberately shut:
    the longest dwarf range is 1663 s against a 10340 s critical path, so nothing
    there can reach the tail and the promotion would be pure cost.
    """
    monkeypatch.setattr(config, 'POOL_CROSS_RUNGS', 'hypergiant->supernova')
    assert _pool(monkeypatch, 0.50 * GiB, 5.00 * GiB) == 'dwarf'


def test_the_free_rung_is_decided_by_node_vcpu_not_by_the_cpu_claim(pooled, monkeypatch):
    """POOL_CPU stopped being a proxy for node size, so the guard must not read it.

    Claims were half the node everywhere, which made equal claims imply equal
    nodes. That stopped being true once tiers were sized to the smallest shape in
    their pool, so two tiers can carry different claims while sitting on
    identically sized nodes. A claim comparison silently refuses such a rung.

    giant->supergiant is the case that still has equal nodes (4 vCPU both sides)
    after the x8i pools were removed on 2026-08-04, so it is what exercises the
    guard. The rung is not on either list, which is the point -- it must be judged
    free on node size alone.
    """
    # Force the claims apart. In production they happen to match right now, but
    # the guard must read POOL_VCPU either way -- a claim comparison broke a rung
    # once already when a tier was sized to its smallest shape.
    monkeypatch.setattr(config, 'POOL_CPU', CPU.replace('supergiant:1.85', 'supergiant:1.20'))
    assert sizing.pool_cpu('giant') != sizing.pool_cpu('supergiant')
    assert sizing.pool_vcpu('giant') == sizing.pool_vcpu('supergiant')
    assert not sizing._rung_listed(config.POOL_CROSS_RUNGS, 'giant', 'supergiant'), \
        "the whitelist must not be what carries this rung"
    assert _pool(monkeypatch, 3.87 * GiB, 12.0 * GiB) == 'supergiant'


def test_every_tier_the_ladder_can_reach_has_a_vcpu_mapping(pooled):
    """An unmapped tier makes the guard refuse every rung into or out of it.

    _cache_bump bails when either side is None, so a missing entry does not
    crash -- it silently turns the whole rule off for that tier, which is the
    kind of failure that only shows up as a run that cost more than it should.
    """
    for tier in [name for _, name in sizing._parsed_pool_tiers()] + [
            config.POOL_UNPROFILED, config.POOL_NO_PROFILE]:
        assert sizing.pool_vcpu(tier) is not None, f"{tier} has no POOL_VCPU entry"


def test_the_dwarf_rung_is_refused_because_it_also_crosses_a_cpu_class(pooled, monkeypatch):
    """dwarf->subgiant is 0.50 -> 1.00, a 1-vCPU node to a 2-vCPU one.

    Blocking it is affordable: the longest dwarf range is 1663 s against a 10340 s
    critical path, it ranks #1768 of 3985 in longest-first order, and dwarf is
    5.4% of total work. Nothing there can reach the tail.
    """
    assert _pool(monkeypatch, 0.50 * GiB, 5.00 * GiB) == 'dwarf'


def test_the_top_of_the_ladder_has_nowhere_to_go(pooled, monkeypatch):
    assert _pool(monkeypatch, 40.0 * GiB, 90.0 * GiB) == 'supernova'


def test_a_profile_without_working_set_data_routes_exactly_as_before(pooled, monkeypatch):
    """Every profile generated before this change lacks peakWorkingSetBytes.

    Those must keep their old placement rather than crash or silently shift, so
    the field being absent has to mean "no opinion", not "zero".
    """
    _profiled(monkeypatch, {
        10: {'peakAnonBytes': MEASURED_ANON},
        20: {'peakAnonBytes': MEASURED_ANON, 'peakWorkingSetBytes': 0},
        30: {'peakAnonBytes': MEASURED_ANON, 'peakWorkingSetBytes': None},
    })
    assert sizing.pool_for(10) == 'giant'
    assert sizing.pool_for(20) == 'giant'
    assert sizing.pool_for(30) == 'giant'


def test_the_bump_is_the_same_every_run(pooled, monkeypatch):
    """Deriving from the bytes each run is what stops the bump compounding.

    The same measurements must yield the same tier however many runs have read
    them. Nothing carries a tier forward, so there is no verdict to bump on top
    of and the promotion cannot ratchet a range to the ceiling one run at a time.
    """
    _profiled(monkeypatch, {
        10: {'peakAnonBytes': MEASURED_ANON, 'peakWorkingSetBytes': MEASURED_WS},
    })
    assert sizing.pool_for(10) == 'supergiant'
    assert sizing.pool_for(10) == 'supergiant'


def test_an_oom_still_climbs_from_the_bumped_tier(pooled, monkeypatch):
    """The cache bump is a starting point, not a replacement for OOM escalation.

    A range bumped to supergiant that then OOMs there has proved it needs more
    memory, and must keep climbing -- including onto the cpu-class rungs the
    bump itself refuses to take speculatively.
    """
    _profiled(monkeypatch, {
        10: {'peakAnonBytes': MEASURED_ANON, 'peakWorkingSetBytes': MEASURED_WS},
    })
    assert sizing.pool_for(10, rungs=0) == 'supergiant'
    assert sizing.pool_for(10, rungs=1) == 'hypergiant'
    assert sizing.pool_for(10, rungs=2) == 'supernova'


def test_the_bumped_tier_is_what_the_pod_actually_requests(pooled, monkeypatch, cluster):
    """Routing to a pool and sizing for it have to agree.

    A pod pinned to supergiant nodes but carrying giant's request would let a
    second pod share the node, which defeats the isolation the whole ladder
    exists to buy.
    """
    _profiled(monkeypatch, {
        10: {'peakAnonBytes': MEASURED_ANON,
             'peakWorkingSetBytes': MEASURED_WS,
             'seconds': 300.0},
    })
    r = jm._resources(end=10, attempt=1)
    assert r.requests['memory'] == '14336Mi'      # supergiant, not giant's 4096Mi
    assert r.requests['cpu'] == 1.85


def test_a_range_past_the_profile_is_sized_by_its_pool_not_the_flat_request(pooled, monkeypatch, cluster):
    """Pooled placement without pooled sizing is a pod that never schedules.

    pool_for resolves a tier for EVERY range -- protostar when the range is
    newer than anything measured -- but _profile_overrides used to bail on the
    missing profile entry before it reached the pooled branch. The pod then got
    protostar's node affinity with the run's flat REQ_CPU.

    Measured on ssc-test 2026-08-04: a 1200-worker run passed
    --pubnet-parallel-catchup-cpu-request 6780m, so the past-the-profile ranges
    asked for 6780m at a pool whose largest node is 4 vCPU. Permanently Pending,
    retried forever, and silent -- earlier runs had only two such ranges and
    nobody checked whether they had scheduled.
    """
    monkeypatch.setattr(config, 'REQ_CPU', '6780m')
    monkeypatch.setattr(config, 'REQ_MEM', '9Gi')
    _profiled(monkeypatch, {10: {'peakAnonBytes': int(0.50 * GiB), 'seconds': 300.0}})
    assert sizing.pool_for(999) == 'protostar'          # past the top of the profile
    r = jm._resources(end=999, attempt=1)
    assert r.requests['cpu'] == sizing.pool_cpu('protostar')
    assert r.requests['memory'] == sizing.pool_memory('protostar')
    assert r.requests['cpu'] != '6780m'


def test_no_profile_at_all_is_also_sized_by_its_pool(pooled, monkeypatch, cluster):
    """Same failure one step further out: nebula gets a tier, so it needs a cut."""
    monkeypatch.setattr(config, 'REQ_CPU', '6780m')
    monkeypatch.setattr(config, 'PROFILE', [])
    assert sizing.pool_for(10) == 'nebula'
    r = jm._resources(end=10, attempt=1)
    assert r.requests['cpu'] == sizing.pool_cpu('nebula')
    assert r.requests['memory'] == sizing.pool_memory('nebula')


def test_every_poolable_tier_fits_the_smallest_node_it_can_land_on(pooled):
    """A claim above the smallest shape's usable cpu silently drops that shape.

    protostar at 1800m is deliberately above x8i.large's 1715m -- it is meant to
    take the 4-vCPU shapes and leave the 128-vCPU X quota to hypergiant. Every
    other tier must fit the node POOL_VCPU says is its smallest, or the tier
    quietly loses its cheapest option.
    """
    DAEMONSETS = 215                     # alloy 10 + aws-node 75 + ebs-csi 30 + kube-proxy 100
    def usable(vcpu):
        reserved = 60 + (10 if vcpu >= 2 else 0) + (5 if vcpu >= 3 else 0) + (5 if vcpu >= 4 else 0)
        return vcpu * 1000 - reserved - DAEMONSETS
    for tier in [name for _, name in sizing._parsed_pool_tiers()] + [config.POOL_NO_PROFILE]:
        cpu, vcpu = sizing.pool_cpu(tier), sizing.pool_vcpu(tier)
        if cpu is None or vcpu is None:
            continue
        assert cpu * 1000 <= usable(vcpu), (
            f"{tier} claims {cpu * 1000:.0f}m but its smallest node "
            f"({vcpu} vCPU) only offers {usable(vcpu)}m")
