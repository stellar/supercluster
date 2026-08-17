"""The volume: the collector's files, the monitor's own, and the run record.

Filenames are the entire cross-process contract. The collector writes while a
pod still exists and the monitor reads them back, so a disagreement about a name
is a measurement silently lost and nothing reports it.

Every write goes through tmp+rename. Both sides write while the other reads, so
a torn .metrics or .outcome reads as corrupt and the measurement is gone.
"""
import collections
import json
import os
import time

import config

# --- the collector writes these ---------------------------------------------


def log_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.log.gz")


def outcome_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.outcome")


def metrics_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.metrics")


def done_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.done")


# --- the monitor writes these -----------------------------------------------


def started_path(end):
    """Per RANGE, not per attempt: wallSeconds spans the range's whole life, so
    the only start that matters is the first one."""
    return os.path.join(config.LOG_DIR, f"range-{end}.started")


PROGRESS_PATH = os.path.join(config.LOG_DIR, 'progress.json')
RUN_PATH = os.path.join(config.LOG_DIR, 'run.json')
MISSION_START_PATH = os.path.join(config.LOG_DIR, 'mission_started')


def write_atomic(path, body):
    tmp = path + '.tmp'
    with open(tmp, 'wt') as fh:
        fh.write(body)
    os.replace(tmp, path)


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
