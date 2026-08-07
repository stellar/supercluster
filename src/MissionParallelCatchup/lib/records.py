"""Per-attempt facts on the shared volume, and the paths they live at.

The collector sidecar writes these files and the monitor reads them, so nothing
authoritative is held in memory: a restarted monitor rebuilds every decision from
these plus the live Job list. The counters here are per CAUSE, not per attempt --
escalation must climb once per OOM, not once per retry.
"""
import json
import os

import config


def log_path(end, attempt):
    """Canonical archive name, written by the log-collector sidecar.

    Deliberately carries no ok/failed suffix: which ranges failed is recorded in
    the progress ConfigMap, and encoding it here would mean two components
    disagreeing about a filename.
    """
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.log.gz")


def state_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.state")


def outcome_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.outcome")


# Per RANGE, not per attempt: wallSeconds spans the range's whole life, so the
# only start that matters is the first one. Later attempts are the mess in
# between and are deliberately not recorded.
def started_path(end):
    return os.path.join(config.LOG_DIR, f"range-{end}.started")


def read_outcome(end, attempt):
    try:
        with open(outcome_path(end, attempt)) as fh:
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


def metrics_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.metrics")


def done_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.done")


def write_atomic(path, body, opener=None):
    """Write `body` through tmp+rename so a reader never sees a partial file.

    Both processes write these files and both read them back: the collector
    writes while the monitor polls, so a torn .metrics or .outcome reads as
    corrupt and the measurement is lost, and a restarted monitor decides a
    range's remaining budget from .outcome and .verdict.
    """
    tmp = path + '.tmp'
    # `opener` resolves at call time, not as a default argument, so the write
    # seam stays patchable -- the tmp+rename discipline is only worth having if
    # a test can crash a write mid-flight and prove the real path is untouched.
    with (opener or open)(tmp, 'wt') as fh:
        fh.write(body)
    os.replace(tmp, path)
