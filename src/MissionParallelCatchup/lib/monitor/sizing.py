"""What a worker pod asks for: pool tier, memory, disk.

The layer tuned between runs. Reads the profile and past verdicts, touches
nothing, and returns a request rather than applying one.

Escalation counts CAUSES, not attempts: a spot reclaim, a disruption and a
timeout all produce a retry and none of them says the range needed a bigger
node.
"""
import bisect
import math
import re

import monitor_config as mc

_QUANTITY = re.compile(r'^(?P<n>\d+(?:\.\d+)?)(?P<unit>[EPTGMK]i?|m)?$')
_FACTOR = {'K': 10 ** 3, 'M': 10 ** 6, 'G': 10 ** 9, 'T': 10 ** 12,
           'P': 10 ** 15, 'E': 10 ** 18,
           'Ki': 2 ** 10, 'Mi': 2 ** 20, 'Gi': 2 ** 30, 'Ti': 2 ** 40,
           'Pi': 2 ** 50, 'Ei': 2 ** 60}


def quantity_bytes(value):
    m = _QUANTITY.match(str(value or '').strip())
    if not m:
        return 0
    n = float(m.group('n'))
    unit = m.group('unit')
    if unit == 'm':
        return int(n / 1000)
    return int(n * _FACTOR.get(unit, 1))


def bytes_to_quantity(n):
    for unit in ('Gi', 'Mi', 'Ki'):
        factor = _FACTOR[unit]
        if n >= factor and n % factor == 0:
            return f"{n // factor}{unit}"
    return str(int(n))


# --- the profile ------------------------------------------------------------


def load_profile(doc):
    """Sorted [(end, record)]. An unprofiled run POSTs {} and gets []: a profile
    is an optimisation, never a prerequisite."""
    ranges = (doc or {}).get('ranges') or {}
    return sorted((int(k), v) for k, v in ranges.items())


def profile_for(end):
    """Measurements to size this range from, or None.

    Exact end, else the nearest measured end ABOVE it: cost rises with ledger
    position because the bucket set only grows, so a lower neighbour
    under-reports. Past the top there is nothing safe to extrapolate from.
    """
    if not mc.PROFILE:
        return None
    idx = bisect.bisect_left(mc.PROFILE, (int(end),))
    if idx < len(mc.PROFILE):
        return mc.PROFILE[idx][1]
    return None


def _positive(value):
    try:
        n = float(value)
    except (TypeError, ValueError):
        return None
    return n if math.isfinite(n) and n > 0 else None


def _longest_seconds():
    if mc._SORTED_SECONDS is None:
        values = (_positive(rec.get('seconds')) for _, rec in (mc.PROFILE or []))
        mc._SORTED_SECONDS = sorted(v for v in values if v is not None)
    return mc._SORTED_SECONDS[-1] if mc._SORTED_SECONDS else None


def _runtime_insurance(seconds, allowance):
    """Runtime-weighted share of an allowance: the longest range gets all of it,
    one half as long gets half, so it follows time-at-risk."""
    seconds = _positive(seconds)
    longest = _longest_seconds()
    total = quantity_bytes(allowance)
    if seconds is None or longest is None or total <= 0:
        return 0
    return int(total * (seconds / longest))


# --- the pool ladder --------------------------------------------------------


def _tiers():
    """[(gib_cut, name)] cheapest first; an empty cut on the last entry means
    everything above the previous one."""
    out = []
    for item in mc.POOL_TIERS.split(','):
        cut, _, name = item.strip().rpartition(':')
        if name:
            out.append((float(cut) if cut else float('inf'), name))
    return out


def _str_map(raw):
    out = {}
    for item in raw.split(','):
        name, _, value = item.strip().partition(':')
        if name and value:
            out[name.strip()] = value.strip()
    return out


def pool_memory(tier):
    """The tier's cut: sized to fit once in its on-demand node and twice in its
    (one size larger) spot node. Not the range's own measurement -- isolation is
    the point, and freeing a pod of its neighbours raised throughput 29-92%
    while its cpu draw FELL."""
    return _str_map(mc.POOL_MEM).get(tier)


def pool_cpu(tier):
    return _str_map(mc.POOL_CPU).get(tier)


def _tier_for_bytes(anon):
    tiers = _tiers()
    if not tiers or not anon:
        return None
    gib = anon / float(2 ** 30)
    for cut, name in tiers:
        if gib < cut:
            return name
    return tiers[-1][1]


def _promote(tier, steps):
    """Climb `steps` rungs, stopping at the top. Tiers off the ladder go
    straight to it: there is nothing above them to walk to."""
    names = [name for _, name in _tiers()]
    if not names or tier is None or steps <= 0:
        return tier
    if tier not in names:
        return names[-1]
    return names[min(names.index(tier) + steps, len(names) - 1)]


def _rung_blocked(tier, nxt):
    want = f"{tier}->{nxt}"
    return any(item.strip() == want for item in mc.POOL_BLOCK_RUNGS.split(','))


def _cache_bump(tier, anon, working_set):
    """One rung up when the working set reaches the next tier and the rung is free.

    peakAnonBytes picks the base tier because anon is what OOM-kills; page cache
    evicts rather than dying. It does decide throughput -- replay is
    single-threaded, so every bucket lookup that misses cache is a serial EBS
    stall. One rung, never two: a range 1.29x under its working set still
    reaches full profile rate.
    """
    if not (tier and anon and working_set):
        return tier
    nxt = _promote(tier, 1)
    if nxt == tier or _rung_blocked(tier, nxt):
        return tier
    order = [name for _, name in _tiers()]
    want = _tier_for_bytes(working_set)
    if tier not in order or not want or order.index(want) <= order.index(tier):
        return tier
    return nxt


def pool_for(end, oom_count=0):
    """The tier this range belongs in, or None when not pooling.

    `oom_count` is how many rungs to climb and it counts OOMs, not attempts:
    promoting on attempt number once put 65 ranges onto 8-vCPU nodes whose
    attempt-1 verdicts were `timeout`, burning ~260 vCPU of a 2304 quota
    escalating away from a problem that was never memory.
    """
    if not mc.POOL_PREFIX:
        return None
    if not mc.PROFILE:
        return _promote(mc.POOL_NO_PROFILE, oom_count)
    prof = profile_for(end)
    if not prof:
        return _promote(mc.POOL_UNPROFILED, oom_count)
    anon = prof.get('peakAnonBytes')
    tier = _cache_bump(_tier_for_bytes(anon), anon, prof.get('peakWorkingSetBytes'))
    if not tier:
        return _promote(mc.POOL_UNPROFILED, oom_count)
    return _promote(tier, oom_count)


# --- escalation -------------------------------------------------------------


def next_memory(end, base, oom_count):
    """Memory request after N OOMs.

    Pooled: the tier ladder IS the ladder, and the promoted tier's cut is the
    escalated request, so request and placement move together. A multiplicative
    bump cannot do this -- tiers are 2.2-2.5x apart against a 1.5x factor, so
    the bump lands BETWEEN them and the pod sits Pending on a pool that can
    never satisfy it.

    Unpooled: base * factor^N, where base is what attempt 1 actually ran with.
    Escalating a 209Mi profiled range off the configured default jumps to
    36000Mi, a 172x overshoot that throws away the packing win on the first OOM.
    """
    if mc.POOL_PREFIX:
        promoted = pool_memory(pool_for(end, oom_count))
        # Above the ladder there is nothing to promote into, so hold rather than
        # invent a value.
        return promoted or base or mc.REQ_MEM
    want = int(quantity_bytes(base or mc.REQ_MEM)
               * (mc.MEM_BUMP_FACTOR ** max(0, oom_count)))
    return bytes_to_quantity(min(want, quantity_bytes(mc.MEM_ESCALATION_CAP)))


def next_ephemeral(base, eviction_count):
    """Disk after N evictions, or None when nothing is limited.

    Only ephemeral mode has this axis: a pvc run sets no ephemeral request or
    limit at all, so there is nothing to raise.
    """
    if not mc.LIM_EPHEMERAL:
        return None
    want = int(quantity_bytes(base or mc.LIM_EPHEMERAL)
               * (mc.EPH_BUMP_FACTOR ** max(0, eviction_count)))
    return bytes_to_quantity(min(want, quantity_bytes(mc.EPH_ESCALATION_CAP)))


# --- the request ------------------------------------------------------------


def _profile_overrides(end, escalated, oom_count):
    """Request overrides from the profile, or {}.

    Unpooled escalation opts out: an escalation measures THIS run and outranks
    anything an earlier one saw. Pooled escalation does NOT -- the promotion IS
    the escalation, and bailing here would send the pod to the new pool still
    asking for the old tier's memory.
    """
    if end is None or (escalated and not mc.POOL_PREFIX):
        return {}
    prof = profile_for(end)
    out = {}
    if prof:
        disk = prof.get('peakEphemeralBytes')
        if disk and mc.LIM_EPHEMERAL:
            want = (int(disk * mc.PROFILE_MARGIN)
                    + quantity_bytes(mc.PROFILE_EPHEMERAL_HEADROOM)
                    + _runtime_insurance(prof.get('seconds'),
                                         mc.PROFILE_RUNTIME_EPHEMERAL_INSURANCE))
            out['ephemeral-storage'] = bytes_to_quantity(
                min(want, quantity_bytes(mc.PROFILE_MAX_EPHEMERAL)))
    if mc.POOL_PREFIX:
        # Deliberately BEFORE the no-profile bail: pool_for resolves a tier for
        # every range, so returning {} here would pin the pod to that pool while
        # sizing it from the flat REQ_CPU. That shipped a 6780m request at a
        # pool whose largest node is 4 vCPU -- permanently Pending.
        tier = pool_for(end, oom_count)
        mem, cpu = pool_memory(tier), pool_cpu(tier)
        if mem:
            out['memory'] = mem
        if cpu:
            out['cpu'] = cpu
        return out
    if not prof:
        return out
    # Unpooled and profiled: the range's own peak plus a flat allowance and a
    # runtime-weighted one. The flat one is load-bearing -- 1.15x of 190Mi is
    # 19Mi of slack, and 90 ranges OOMKilled inside 90s without it.
    rss = prof.get('peakAnonBytes')
    if rss:
        want = (int(rss * mc.PROFILE_MARGIN)
                + quantity_bytes(mc.PROFILE_CACHE_HEADROOM)
                + _runtime_insurance(prof.get('seconds'),
                                     mc.PROFILE_RUNTIME_MEMORY_INSURANCE))
        out['memory'] = bytes_to_quantity(
            min(want, quantity_bytes(mc.PROFILE_MAX_MEM)))
    return out


def requests_for(end, oom_count=0, memory=None, ephemeral=None):
    """(requests, limits) for one attempt.

    Only disk is limited: it is the one dimension where an unbounded pod takes
    the node down rather than itself.
    """
    overrides = _profile_overrides(end, escalated=bool(memory or ephemeral),
                                   oom_count=oom_count)
    req = {'cpu': mc.REQ_CPU, 'memory': memory or mc.REQ_MEM}
    lim = {}

    # A profiled pooled range owns its node -- the tier's cut excludes a second
    # pod -- so a disk limit guards no neighbour and only turns spare disk into
    # an eviction. Unprofiled pooled runs keep both: nothing measured them.
    pooled_profiled = bool(mc.POOL_PREFIX and mc.PROFILE)
    if mc.REQ_EPHEMERAL and not pooled_profiled:
        req['ephemeral-storage'] = ephemeral or mc.REQ_EPHEMERAL
    else:
        overrides.pop('ephemeral-storage', None)
    if mc.LIM_EPHEMERAL and not pooled_profiled:
        lim['ephemeral-storage'] = ephemeral or mc.LIM_EPHEMERAL

    for key, value in overrides.items():
        req[key] = value
        if key == 'ephemeral-storage' and mc.LIM_EPHEMERAL:
            lim[key] = value
    return req, (lim or None)


def node_label_value(end, oom_count):
    tier = pool_for(end, oom_count)
    return f"{mc.POOL_PREFIX}-{tier}" if tier else mc.NODE_LABEL_VALUE
