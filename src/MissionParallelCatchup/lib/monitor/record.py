"""What the monitor keeps on the volume: the run record, and reads of the
collector's files.

The filenames themselves are NOT here. They are the cross-process contract and
live in records.py, which both processes import -- a second copy is a second
place for the two sides to disagree about a name, which is a measurement lost
with nothing to report it.
"""
import collections
import json
import os
import time

import config
from records import (done_path, log_path, metrics_path, outcome_path,  # noqa: F401
                     state_path, write_atomic)

# --- the monitor writes these -----------------------------------------------


def started_path(end):
    """Per RANGE, not per attempt: wallSeconds spans the range's whole life, so
    the only start that matters is the first one."""
    return os.path.join(config.LOG_DIR, f"range-{end}.started")


PROGRESS_PATH = os.path.join(config.LOG_DIR, 'progress.json')
RUN_PATH = os.path.join(config.LOG_DIR, 'run.json')
MISSION_START_PATH = os.path.join(config.LOG_DIR, 'mission_started')


def _read_json(path):
    try:
        with open(path) as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


# --- what an attempt left behind --------------------------------------------


def is_done(end, attempt):
    """The collector has finished with this attempt.

    The only licence to decide a failed attempt or to reap. Never inferred from
    measurements being present -- an attempt may legitimately have none.
    """
    return os.path.exists(done_path(end, attempt))


def unclaimed(end, attempt):
    """No .state, so the collector never opened this attempt and no .done is
    coming. A pod deleted before its container started is never pollable."""
    return not os.path.exists(state_path(end, attempt))


def read_outcome(end, attempt):
    return _read_json(outcome_path(end, attempt))


def read_metrics(end, attempt):
    return _read_json(metrics_path(end, attempt)) or {}


def record_start(end, created):
    """Attempt 1's Job creationTimestamp, written once.

    Not status.startTime: the controller sets that asynchronously, so it is
    absent from the create response, and the gap between the two is part of what
    wallSeconds measures. Written at creation because attempt 1's Job is gone by
    the first retry.
    """
    path = started_path(end)
    if created is None or os.path.exists(path):
        return
    try:
        write_atomic(path, created.isoformat())
    except OSError:
        pass


def started_at(end):
    try:
        with open(started_path(end)) as fh:
            from datetime import datetime
            return datetime.fromisoformat(fh.read().strip())
    except (OSError, ValueError):
        return None


# --- the run record ---------------------------------------------------------


def load_progress():
    """The durable record, read back rather than rebuilt.

    It is what makes a reaped range stay completed instead of reading as
    pending and being dispatched a second time -- and it carries the counter
    totals, so a restart resumes them in one read instead of rescanning every
    attempt on the volume.
    """
    doc = _read_json(PROGRESS_PATH) or {}
    return {'completed': doc.get('completed') or {},
            'condemned': doc.get('condemned') or {},
            'causes': doc.get('causes') or {},
            'counters': collections.Counter(doc.get('counters') or {}),
            'disruptedRanges': set(doc.get('disruptedRanges') or ())}


def note_cause(progress, end, attempt, cause):
    """Record why one attempt ended, once. Returns this range's cause counts.

    Keyed on the highest attempt already counted, so re-settling the same
    attempt on a later pass cannot double-count it. Budgets then read a number
    instead of walking every past attempt off the volume.
    """
    rec = progress['causes'].setdefault(str(end), {})
    if rec.get('last', 0) < int(attempt):
        rec['last'] = int(attempt)
        rec[cause] = rec.get(cause, 0) + 1
    return rec


def save_progress(progress):
    """One file holding every terminal fact.

    If it is lost or truncated every range reads as pending and the run
    redispatches all of them, so nothing may write it by any other means.
    """
    doc = dict(progress,
               counters=dict(progress['counters']),
               disruptedRanges=sorted(progress['disruptedRanges']))
    write_atomic(PROGRESS_PATH, json.dumps(doc, separators=(',', ':')))


def save_run(doc):
    write_atomic(RUN_PATH, json.dumps(doc, separators=(',', ':')))


def load_run():
    return _read_json(RUN_PATH)


def mission_start():
    """When this run began, surviving a monitor restart."""
    try:
        with open(MISSION_START_PATH) as fh:
            return float(fh.read().strip())
    except (OSError, ValueError):
        now = time.time()
        try:
            write_atomic(MISSION_START_PATH, repr(now))
        except OSError:
            pass
        return now


def manifest():
    """Every artifact the driver can pull, as [{name, size}].

    A bare ARRAY of objects, both of which the driver depends on: it parses the
    body as an array and reads .name and .size off each entry, using size to
    skip what it already has. .tmp files are excluded -- a half-written file has
    no size worth comparing.
    """
    out = []
    try:
        names = sorted(os.listdir(config.LOG_DIR))
    except OSError:
        return out
    for name in names:
        if name.endswith('.tmp'):
            continue
        try:
            out.append({'name': name,
                        'size': os.path.getsize(os.path.join(config.LOG_DIR, name))})
        except OSError:
            continue          # vanished between listing and stat
    return out
