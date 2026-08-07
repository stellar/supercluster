"""The measured profile of a previous run, and the lookup into it.

Loaded once at startup into config.PROFILE and read through it thereafter, so a
test that patches the profile is seen here without reloading anything.
"""

import bisect
import json
import logging

import config

logger = logging.getLogger()


def load_profile():
    """Per-range measurements from an earlier run, keyed by range end.

    Absent, unreadable or malformed all mean the same thing: size from the
    configured defaults. A profile is an optimisation, never a prerequisite.
    """
    if not config.PROFILE_PATH:
        return []
    try:
        with open(config.PROFILE_PATH) as fh:
            doc = json.load(fh)
    except (OSError, ValueError) as e:
        logger.warning("range profile %s unreadable (%s); using configured requests",
                       config.PROFILE_PATH, e)
        return []
    mode = doc.get('storageMode')
    cross_mode = bool(mode) and mode != config.STORAGE_MODE
    if cross_mode:
        # cpu and memory carry across modes -- they measure the same work. Disk
        # does not: a pvc run puts /data on the volume, so it never measures
        # node-local usage, and an ephemeral run's figure says nothing about a
        # pvc one. Keep the transferable axes and let disk fall back to the
        # configured default.
        logger.warning("range profile is for storageMode=%s but this run is %s; "
                       "using its cpu and memory, defaulting ephemeral storage",
                       mode, config.STORAGE_MODE)
    out = []
    for end, rec in (doc.get('ranges') or {}).items():
        try:
            end = int(end)
        except (TypeError, ValueError):
            continue
        if cross_mode:
            rec = {k: v for k, v in rec.items() if k != 'peakEphemeralBytes'}
        out.append((end, rec))
    out.sort()
    logger.info("loaded range profile: %d ranges from %s", len(out), config.PROFILE_PATH)
    return out


def profile_for(end):
    """Measurements to size this range from, or None to use the defaults.

    Exact end, else the nearest measured end ABOVE it. Cost rises with ledger
    position -- the bucket set only grows -- so a lower neighbour under-reports,
    and under-provisioning costs an eviction while over-provisioning only costs
    packing. Past the top of the profile there is nothing safe to extrapolate
    from, so fall back to the configured defaults.
    """
    if not config.PROFILE:
        return None
    end = int(end)
    idx = bisect.bisect_left(config.PROFILE, (end,))
    if idx < len(config.PROFILE) and config.PROFILE[idx][0] == end:
        return config.PROFILE[idx][1]
    return config.PROFILE[idx][1] if idx < len(config.PROFILE) else None
