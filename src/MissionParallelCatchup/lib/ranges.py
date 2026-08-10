"""The ledger range list and the order it is dispatched in.

A pure function of config: dispatch recomputes the whole list on every reconcile,
so a restarted monitor has to reproduce it exactly. Nothing here reads the
cluster or the volume.
"""

import config
import profiles


def _uniform_segment(start_ledger, end_ledger, seg_size):
    """Ranges over (start_ledger, end_ledger], largest ledger first."""
    out = []
    el = end_ledger
    while el > start_ledger:
        ledgers_per_job = min(el - start_ledger, seg_size)
        out.append((el, ledgers_per_job + config.OVERLAP_LEDGERS))
        el -= ledgers_per_job
    return out


def _longest_first(ranges):
    """Sort on the profile's own measured seconds, longest job first.

    Makespan is bounded below by the single longest job, so every range that
    starts after it is free and every hour it starts late is an hour on the end.
    That is classic longest-processing-time scheduling.

    A range the profile has never seen sorts FIRST. profile_for returns the
    nearest measured end ABOVE the target, so an unprofiled range is by
    construction newer than anything ever measured -- the newest ranges are the
    most expensive, so "unknown" means "assume worst", not "assume average".
    That also makes the next profile better: those ranges run early, under the
    most generous sizing, instead of being the ones a run dies before reaching.

    Requires a profile; validate_config() refuses the combination without one,
    because every key would tie and the stable sort would leave dispatch in the
    generator's tip-first order while looking configured.
    """
    def cost(item):
        prof = profiles.profile_for(item[0])
        secs = (prof or {}).get('seconds')
        # None sorts first; ties keep tip-first order, which is the better guess
        # among ranges the profile cannot separate.
        return (0 if secs is None else 1, -(secs or 0))
    return sorted(ranges, key=cost)


def _ordered(ranges):
    """Dispatch order. Generators emit tip-first; the other two re-order that.

    tip-first only approximates longest-first. Position predicts cost on average
    and badly in the tail: measured 2026-07-30, ranges at 41-45M ran as long as
    the tip (3.1h) on a third of the memory, and the 50-60M band is CHEAPER than
    40-50M. Sorting on measured seconds uses the real number instead of a proxy.
    """
    # validate_config() rejects an unknown order at startup; the raise here is
    # the backstop for a caller that skipped it, never the primary check.
    if config.RANGE_ORDER == 'tip-first':
        return ranges
    elif config.RANGE_ORDER == 'oldest-first':
        return list(reversed(ranges))
    elif config.RANGE_ORDER == 'longest-first':
        return _longest_first(ranges)
    else:
        raise ValueError("RANGE_ORDER must be one of %s, got %r"
                         % (', '.join(config.VALID_RANGE_ORDERS), config.RANGE_ORDER))


def generate_ranges():
    """Uniform ranges over the whole window, in the configured dispatch order.

    A logarithmic generator lived here -- big chunks over cheap early history,
    halving toward the tip, aiming for equal wall-time per job. longest-first
    supersedes it: same goal, but ordered from what ranges actually measured
    rather than from an assumption about where the expensive ledgers are. It
    also became unreachable when the range moved into /start, which carries no
    generator. Recover it from 8553e77 if the guess ever beats the measurement.
    """
    return _ordered(_uniform_segment(config.STARTING_LEDGER,
                                     config.LATEST_LEDGER_NUM,
                                     config.LEDGERS_PER_JOB))
