"""What a worker pod asks for: nodepool tier, memory, ephemeral disk.

This is the layer tuned between runs. It reads the profile and the per-attempt
records, and it never touches the cluster -- what it returns is a request, not an
applied change.

Escalation counts CAUSES, not attempts: a spot reclaim, a disruption and a
timeout all produce a retry and none of them says the range needed a bigger node.
"""
import logging
import math

import monitor_config as mc
import profiles
import attempt_files
import units

logger = logging.getLogger()


def mem_for_attempt(attempt, base=None, end=None):
    """Memory REQUEST after N OOMs.

    Pooled: the tier ladder IS the ladder. Attempt N resolves to a tier N-1
    steps up, and the request is that tier's cut, so the request and the pool
    move together. A multiplicative bump cannot do this -- tiers are ~2.2-2.5x
    apart while MEM_BUMP_FACTOR is 1.5, so a bump lands BETWEEN tiers: too big
    for the current pool's nodes, too small to have earned the next one, and the
    pod sits Pending on a pool that can never satisfy it.

    Unpooled: the original behaviour. `base` is what attempt 1 actually ran
    with, which matters when a profile sized the range -- escalating a 209Mi
    profiled range off the configured default jumps straight to 36000Mi, a 172x
    overshoot that throws away the whole packing win on the first OOM.

    Escalating the request, not a limit, because there is no limit any more. It
    still buys the same two things an OOMing range needs -- placement somewhere
    with the memory actually free, and a higher bar before the kubelet picks it
    as an eviction victim.
    """
    if mc.POOL_PREFIX:
        promoted = pool_memory(pool_for(end, attempt))
        if promoted:
            return promoted
        # Above the ladder (nebula/protostar/supernova): nothing left to promote
        # into, so hold at the configured request rather than inventing a value.
        return base or mc.REQ_MEM
    base_q = units.quantity_bytes(base or mc.REQ_MEM)
    want = int(base_q * (mc.MEM_BUMP_FACTOR ** max(0, attempt - 1)))
    cap = units.quantity_bytes(mc.MEM_ESCALATION_CAP)
    return units.bytes_to_quantity(min(want, cap))


def eph_for_attempt(attempt):
    """Ephemeral-storage size for attempt N, escalating after an eviction.

    None when no limit is configured: a pod with no ephemeral-storage limit can
    still be evicted under node disk pressure, and there is nothing to raise.
    Every other reader of LIM_EPHEMERAL already guards on it being set.
    """
    if not mc.LIM_EPHEMERAL:
        return None
    base_q = units.quantity_bytes(mc.LIM_EPHEMERAL)
    want = int(base_q * (mc.EPH_BUMP_FACTOR ** max(0, attempt - 1)))
    return units.bytes_to_quantity(min(want, units.quantity_bytes(mc.EPH_ESCALATION_CAP)))


def _rung_listed(raw, tier, nxt):
    want = f"{tier}->{nxt}"
    return any(item.strip() == want for item in raw.split(','))


def _rung_blocked(tier, nxt):
    """Is this rung denied outright? Beats every other consideration."""
    return _rung_listed(mc.POOL_BLOCK_RUNGS, tier, nxt)


def _parsed_pool_tiers():
    """[(gib_cut, tier_name)] cheapest first, the last entry unbounded.

    An empty cut on the final entry means "everything above the previous one",
    which is how supernova is expressed without inventing a ceiling.
    """
    out = []
    for item in mc.POOL_TIERS.split(','):
        item = item.strip()
        if not item:
            continue
        cut, _, name = item.rpartition(':')
        if not name:
            continue
        out.append((float(cut) if cut else float('inf'), name))
    return out


def _tier_for_bytes(anon_bytes):
    """Tier whose node can hold this working set, or None if unsizable."""
    tiers = _parsed_pool_tiers()
    if not tiers or not anon_bytes:
        return None
    gib = anon_bytes / float(1024 ** 3)
    for cut, name in tiers:
        if gib < cut:
            return name
    return tiers[-1][1]


def _promote(tier, steps):
    """Move `steps` tiers up the ladder, stopping at the top.

    OOM escalation moves the POOL, not just the request. Bumping a request while
    the pod is still pinned to a tier whose nodes cannot hold it produces a pod
    that can never schedule -- Pending forever, which reads as a hang rather
    than a failure. nebula and protostar sit outside the ladder and escalate
    straight to the top, since there is no tier above them to walk to.
    """
    tiers = [name for _, name in _parsed_pool_tiers()]
    if not tiers:
        return tier
    top = tiers[-1]
    if steps <= 0 or tier is None:
        return tier
    if tier not in tiers:
        return top
    return tiers[min(tiers.index(tier) + steps, len(tiers) - 1)]


def _cache_bump(tier, anon_bytes, ws_bytes):
    """One rung up when the working set reaches the next tier and the rung is free.

    peakAnonBytes picks the base tier because anon is what OOM-kills. Page cache
    is elastic -- it evicts rather than dying -- so it has no business in that
    decision. It does decide throughput: replay is single-threaded, so every
    bucket lookup that misses cache is a serial ~0.5 ms EBS stall. Measured on
    ssc-test 2026-08-03, range 44648511 (anon 2.63 GiB, ws 18.31 GiB) run twice
    on identical 2-vCPU Intel nodes differing only in RAM:

        m8in.large  8 GiB   540 reads/ledger   21% iowait   1.86 lps
        r8in.large 16 GiB    65 reads/ledger    7% iowait   3.14 lps  (100% of profile)

    Which rungs are worth taking is stated outright in POOL_BLOCK_RUNGS, not
    inferred. giant->supergiant is m8a.large->r8a.large: twice the RAM for the
    same 2 vCPU and +8% spot. supergiant->hypergiant is r8a.large->x8i.large,
    also 2 vCPU, and that rung measured 1.86x on ssc-test 2026-08-03 -- 1.64 ->
    2.99 lps across nine ranges, the largest gain found anywhere.

    Blocked: hypergiant->supernova, whose ranges measured healthy at 6-11
    reads/ledger and gained only 1.23x, and dwarf->subgiant, the same doubling
    at the bottom of the ladder. Both double the cores for a weak return.

    This used to be derived instead, by refusing any rung whose POOL_VCPU
    differed and keeping an allowlist of exceptions. POOL_VCPU reads the
    SMALLEST shape a pool can land on, so it mispriced every tier spanning node
    sizes -- an x8i at w80 hid a 4->8 promotion -- and the allowlist existed
    only to undo its wrong answers. Deriving it bought one rung that a block
    entry states directly, so a list of refusals replaced both. The cost is that
    a new tier is allowed by default: a rung that should be refused now has to
    be named here.

    Deliberately loose about false positives. Promoting a range that did not need
    it costs +8% on its node-hours and nothing in quota; leaving one starving
    costs 40% of its throughput. Against 50 pods probed for reads/ledger and
    iowait on the same run: 11 correctly promoted, 11 unnecessarily, 1 missed --
    and the 11 unnecessary ones are free.

    One rung, never two, even when the working set would justify more. 44648511
    lands on supergiant still 1.29x UNDER its working set and reaches full
    profile rate there; fitting the working set costs multiples for nothing.
    """
    if not (tier and anon_bytes and ws_bytes):
        return tier
    nxt = _promote(tier, 1)
    if nxt == tier:
        return tier                     # already at the top of the ladder
    order = [name for _, name in _parsed_pool_tiers()]
    want = _tier_for_bytes(ws_bytes)
    if tier not in order or not want or order.index(want) <= order.index(tier):
        return tier                     # working set does not reach the next tier
    if _rung_blocked(tier, nxt):
        return tier                     # denied outright, see POOL_BLOCK_RUNGS
    return nxt


def pool_for(end, attempt=1, rungs=None):
    """Which nodepool tier this range belongs in, or None when not pooling.

    Three cases, and they are deliberately different pools:
      no profile at all      -> POOL_NO_PROFILE (nebula), sized by the configured
                                defaults because nothing is known
      profiled run, this
      range past the top     -> POOL_UNPROFILED (protostar). Only the newest
                                ledgers land here and they are the densest, so
                                it is a rich pool rather than an average one
      profiled range         -> the tier its peakAnonBytes fits, then one rung
                                up if its working set cannot be cached there and
                                the rung is free -- see _cache_bump

    `rungs` is how many tiers to climb, and it counts OOMs -- NOT attempts. A
    spot reclaim, a disruption and a timeout all produce a retry, and none of
    them says the range needed a bigger node. Promoting on attempt number put 65
    ranges onto 8-vCPU supernova nodes during the 2026-08-03 spot run whose
    attempt-1 verdicts were `timeout`; they belonged on 4-vCPU hypergiant, so it
    burned ~260 vCPU of a 2304 quota escalating away from a problem that was
    never memory.
    """
    if not mc.POOL_PREFIX:
        return None
    if rungs is None:
        # Attempts before this one, since this attempt has not run yet. Anything
        # on disk for it is from a previous incarnation of the same attempt.
        rungs = attempt_files._oom_count(end, attempt - 1) if attempt and attempt > 1 else 0
    if not mc.PROFILE:
        return _promote(mc.POOL_NO_PROFILE, rungs)
    prof = profiles.profile_for(end) if end is not None else None
    if not prof:
        return _promote(mc.POOL_UNPROFILED, rungs)
    anon = prof.get('peakAnonBytes')
    tier = _cache_bump(_tier_for_bytes(anon), anon,
                       prof.get('peakWorkingSetBytes'))
    if not tier:
        # An entry with no memory measurement tells us nothing about size --
        # treat it as unprofiled rather than guessing a tier.
        return _promote(mc.POOL_UNPROFILED, rungs)
    return _promote(tier, rungs)


def _pool_map(raw, what):
    out = {}
    for item in raw.split(','):
        item = item.strip()
        if not item:
            continue
        name, _, value = item.partition(':')
        try:
            out[name.strip()] = float(value)
        except ValueError:
            logger.error("%s is malformed at %r; that tier falls back to the "
                         "configured request", what, item)
    return out


def pool_cpu(tier):
    """cpu request for a tier, or None to keep the configured one."""
    return _pool_map(mc.POOL_CPU, 'POOL_CPU').get(tier)


def pool_memory(tier):
    """Memory request for a tier: half its node's allocatable.

    Not the range's own measurement. Sizing from the peak would let two small
    ranges share a node, and isolation is the whole point -- freeing a pod of
    its three neighbours raised throughput 29-92% while its cpu draw FELL, so
    the contended resource is memory bandwidth and shared cache, not compute.

    Half is the smallest value that still excludes a second pod once the
    daemonsets are counted, and the largest that reliably schedules the first.
    """
    return _pool_str_map(mc.POOL_MEM, 'POOL_MEM').get(tier)


def _pool_str_map(raw, what):
    out = {}
    for item in raw.split(','):
        item = item.strip()
        if not item:
            continue
        name, _, value = item.partition(':')
        if not value:
            logger.error("%s is malformed at %r; that tier keeps the configured "
                         "request", what, item)
            continue
        out[name.strip()] = value.strip()
    return out


def _positive_seconds(value):
    """A finite positive runtime, or None when the profile cannot supply one."""
    try:
        seconds = float(value)
    except (TypeError, ValueError):
        return None
    return seconds if math.isfinite(seconds) and seconds > 0 else None


def _profile_seconds():
    """Every valid measured runtime in the profile, sorted."""
    if mc._SORTED_SECONDS is None:
        values = (_positive_seconds(r.get('seconds')) for _, r in (mc.PROFILE or []))
        mc._SORTED_SECONDS = sorted(seconds for seconds in values if seconds is not None)
    return mc._SORTED_SECONDS


def _runtime_insurance(seconds, allowance):
    """Runtime-weighted share of a configured allowance.

    The longest range in the profile gets all of it and one half as long gets
    half, so the allowance follows time-at-risk. Zero when the profile cannot
    supply a runtime to weight by.
    """
    seconds = _positive_seconds(seconds)
    everything = _profile_seconds()
    longest = everything[-1] if everything else None
    insurance = units.quantity_bytes(allowance)
    # No `longest <= 0` guard: _profile_seconds only keeps finite positives.
    if seconds is None or longest is None or insurance <= 0:
        return 0
    return int(insurance * (seconds / longest))


def _profile_overrides(end, escalated, attempt=1):
    """Request overrides for this range from the profile, or {} for none.

    Escalated retries opt out: an escalation is a measurement of THIS run and
    outranks anything an earlier one saw.
    """
    if end is None:
        return {}
    if escalated and not mc.POOL_PREFIX:
        # Unpooled: an escalation measures THIS run and outranks anything an
        # earlier one saw. Pooled: the promotion IS the escalation, and the
        # promoted tier's cut is the escalated request -- bailing out here would
        # send the pod to the new pool still asking for the old tier's memory.
        return {}
    prof = profiles.profile_for(end)
    out = {}
    if prof:
        disk = prof.get('peakEphemeralBytes')
        if disk and mc.LIM_EPHEMERAL:
            want = (int(disk * mc.PROFILE_MARGIN)
                    + units.quantity_bytes(mc.PROFILE_EPHEMERAL_HEADROOM)
                    + _runtime_insurance(prof.get('seconds'),
                                         mc.PROFILE_RUNTIME_EPHEMERAL_INSURANCE))
            out['ephemeral-storage'] = units.bytes_to_quantity(
                min(want, units.quantity_bytes(mc.PROFILE_MAX_EPHEMERAL)))
    if mc.POOL_PREFIX:
        # Deliberately BEFORE the no-profile bail. pool_for resolves a tier for
        # every range -- protostar when the range is newer than the profile,
        # nebula when there is no profile at all -- so returning {} here would
        # pin the pod to that pool while sizing it from the flat REQ_CPU. On
        # 2026-08-04 that shipped a 6780m request (the run's
        # --pubnet-parallel-catchup-cpu-request) at a protostar pool whose
        # largest node is 4 vCPU: permanently Pending, retried forever, and
        # invisible until a run had enough past-the-profile ranges to notice.
        # Pooled: the tier's cut is the request, and the margin lives in the
        # node size instead. PROFILE_MARGIN, cache headroom and runtime
        # insurance all existed to keep a pod under its own memory LIMIT; there
        # is no memory limit any more and the pod owns the node, so a margin in
        # the request constrains nothing the kubelet acts on. Disk keeps its
        # margin above -- that limit IS enforced.
        tier = pool_for(end, attempt)
        mem = pool_memory(tier)
        if mem:
            out['memory'] = mem
        cpu = pool_cpu(tier)
        if cpu:
            out['cpu'] = cpu
        return out
    if not prof:
        # Unpooled and unmeasured: nothing to size from, so the configured
        # requests stand exactly as if there were no profile at all.
        return out
    # Unpooled (the pre-tier behaviour, and what nebula-style runs fall back to
    # when no prefix is configured): size memory from the range's own peak, with
    # the margins that a limit-bearing pod needed.
    #
    # peakAnonBytes is kubelet's rssBytes, sampled by the collector on its own
    # the finer one and fall back, so a profile captured before the collector
    # tracked anon still sizes exactly as it used to.
    rss = prof.get('peakAnonBytes')
    if rss:
        want = (int(rss * mc.PROFILE_MARGIN)
                + units.quantity_bytes(mc.PROFILE_CACHE_HEADROOM)
                + _runtime_insurance(prof.get('seconds'),
                                     mc.PROFILE_RUNTIME_MEMORY_INSURANCE))
        out['memory'] = units.bytes_to_quantity(min(want, units.quantity_bytes(mc.PROFILE_MAX_MEM)))
    return out
