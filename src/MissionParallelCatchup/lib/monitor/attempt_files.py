"""What an attempt left behind, read back for retry accounting.

The collector writes these files while the pod still exists; the monitor reads
them to decide whether an attempt may be retried and how far its resources must
climb. The counters are per CAUSE, not per attempt -- escalation must climb once
per OOM, not once per retry.
"""
import json
import os

import config
import records


# Per RANGE, not per attempt: wallSeconds spans the range's whole life, so the
# only start that matters is the first one. Later attempts are the mess in
# between and are deliberately not recorded.
def started_path(end):
    return os.path.join(config.LOG_DIR, f"range-{end}.started")



def read_outcome(end, attempt):
    try:
        with open(records.outcome_path(end, attempt)) as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None



def _oom_count(end, attempt):
    """How many earlier attempts at this range were OOM-killed.

    Escalation must climb once per OOM, not once per attempt. On spot most
    retries are evictions -- measured on ssc-test 2026-07-30, 288 disruption
    retries against 7 OOM retries -- and a range disrupted three times then
    OOMing once would otherwise jump to base * 1.5^4, a 5x request for a single
    OOM. That inflation is fleet-wide and it is what exhausts the vCPU quota.
    """
    return sum(1 for n in range(1, int(attempt) + 1)
               if _verdict_of(end, n) == 'oom')



def verdict_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.verdict")



def _verdict_of(end, attempt):
    try:
        with open(verdict_path(end, attempt)) as fh:
            verdict = fh.read().strip()
    except OSError:
        # Pre-fix runs, or an attempt whose verdict write lost the volume:
        # the pod-derived classification is the next best thing.
        outcome = (read_outcome(end, attempt) or {}).get('outcome')
        return outcome if outcome in config.ATTEMPT_OUTCOMES else None
    return verdict if verdict in config.ATTEMPT_OUTCOMES else None



def _cause_count(end, attempt, causes):
    """How many of attempts 1..N at this range failed for one of `causes`.

    Budgets are per cause, not per attempt. One shared attempt index meant
    cluster churn -- which has its own deliberately large budget -- drained the
    small budgets belonging to the causes that say something about the range: a
    range evicted MAX_ATTEMPTS times had an effective OOM and disk budget of
    zero, was condemned on its first real OOM without ever being escalated, and
    took the whole mission with it.
    """
    return sum(1 for n in range(1, int(attempt) + 1)
               if _verdict_of(end, n) in causes)


