"""The measured profile of a previous run, and the lookup into it.

Parsed once from the /start POST into mc.PROFILE and read through it thereafter,
so a test that patches the profile is seen here without reloading anything.
"""

import bisect

import monitor_config as mc


def load_profile_doc(doc):
    """The sorted (end, record) list a parsed profile document yields.

    An unprofiled run POSTs {} and gets [] -- a profile is an optimisation,
    never a prerequisite, so "no ranges" is a valid answer rather than an error.
    """
    ranges = (doc or {}).get('ranges') or {}
    return sorted((int(k), v) for k, v in ranges.items())


def profile_for(end):
    """Measurements to size this range from, or None to use the defaults.

    Exact end, else the nearest measured end ABOVE it. Cost rises with ledger
    position -- the bucket set only grows -- so a lower neighbour under-reports,
    and under-provisioning costs an eviction while over-provisioning only costs
    packing. Past the top of the profile there is nothing safe to extrapolate
    from, so fall back to the configured defaults.
    """
    if not mc.PROFILE:
        return None
    end = int(end)
    idx = bisect.bisect_left(mc.PROFILE, (end,))
    if idx < len(mc.PROFILE) and mc.PROFILE[idx][0] == end:
        return mc.PROFILE[idx][1]
    return mc.PROFILE[idx][1] if idx < len(mc.PROFILE) else None
