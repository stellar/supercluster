"""What killed an attempt.

The collector classifies the live pod into .outcome; this resolves that against
the Job condition and against the archive, and the answer is what budgets are
spent on.
"""
import gzip
import logging
import re

import record

logger = logging.getLogger('job_monitor')

# stellar-core's "did not complete". Ambiguous by construction: a corrupt
# bucket, a SIGTERM during replay and an archive fetch fault all produce it.
CATCHUP_INCOMPLETE_EXIT = 3

# Outcomes naming a MECHANISM. A Job-level DeadlineExceeded must never
# overwrite one: it says the Job ran long, not which of these caused it.
SPECIFIC = frozenset({'oom', 'disrupted', 'ephemeral', 'rejected', 'fetch-fault'})

# Rule ORDER is the contract with the Job controller's "rule at index N".
RULE_ORDER = ('disrupted', 'oom', 'failed')
_RULE_INDEX = dict(enumerate(RULE_ORDER))
_JOB_RULE = re.compile(r'rule at index (?P<idx>\d+)')
_JOB_MSG = re.compile(r'Container (?P<pod>\S+) .*exit code (?P<code>\d+)')

# Archive scan. The windows are different widths on purpose.
_TAIL_LINES = 400
_TAIL_BYTES = 1 << 18      # comfortably more than 400 lines of stellar-core log
_STALE_WINDOW = 6      # tight: a wider one credits a fault the range recovered from
_MARKER_WINDOW = 25    # wide: concurrent downloads interleave
_CATCHUP_FAILED = 'Catchup failed'
_STALE_ARCHIVE = 'maybe stale archive'
_TERMINAL = ('Key does not exist', '(404)', 'NoSuchKey')
_TRANSIENT = ('Could not connect to the endpoint URL', 'Unable to locate credentials',
              'ExpiredToken', 'RequestTimeout', 'SlowDown', 'ConnectTimeoutError')


def from_job(job):
    """Recover a verdict from the Job when the pod is already gone.

    The rule INDEX is the signal, not the exit code: rules are first-match-wins,
    so reaching the exit-137 rule proves DisruptionTarget did not match -- the
    only way to tell an OOM kill from a grace-period SIGKILL once the pod object
    is gone. Index and code are parsed independently, because a rule matching on
    onPodConditions reports no exit code at all.
    """
    for cond in (job.status.conditions or []):
        if cond.type != 'Failed' or cond.status != 'True':
            continue
        if cond.reason == 'DeadlineExceeded':
            return {'outcome': 'timeout', 'exitCode': None, 'pod': ''}
        if cond.reason != 'PodFailurePolicy':
            continue          # e.g. BackoffLimitExceeded: no per-rule detail
        msg = cond.message or ''
        rule = _JOB_RULE.search(msg)
        detail = _JOB_MSG.search(msg)
        code = int(detail.group('code')) if detail else None
        outcome = _RULE_INDEX.get(int(rule.group('idx'))) if rule else None
        if outcome is None:
            if code is None:
                return None
            # No usable index. A drained core exits 3, not 137, so a bare 137 is
            # an OOM and a bare 3 without DisruptionTarget is a catchup failure.
            outcome = 'oom' if code == 137 else 'failed'
        return {'outcome': outcome, 'exitCode': code,
                'pod': detail.group('pod') if detail else ''}
    return None


def effective(end, attempt, job):
    """The verdict this attempt is judged on, promotions applied.

    An attempt with no evidence at all is `unknown`, which has no budget and so
    condemns: without evidence the monitor cannot tell a reaped node from a
    range that really failed, and a run reporting success on a range nobody
    verified is worse than one that stops.
    """
    from_pod = record.read_outcome(end, attempt)
    from_condition = from_job(job) if job is not None else None

    if from_pod and from_pod.get('outcome') in SPECIFIC:
        found = from_pod
    else:
        found = from_condition or from_pod or {'outcome': 'unknown', 'exitCode': None}

    if found.get('outcome') == 'failed' and found.get('exitCode') == CATCHUP_INCOMPLETE_EXIT:
        cause = exit3_cause(end, attempt)
        if cause:
            return dict(found, outcome='fetch-fault', reason=cause)
    return found


def exit3_cause(end, attempt):
    """The transient fetch fault behind an exit 3, or None.

    Three conditions, all required. Missing any leaves the attempt `failed`,
    which has no budget -- so a bare `Catchup failed` ends the run rather than
    burning 20 attempts proving the chain is broken.
    """
    lines = _tail(end, attempt)
    if not lines:
        return None
    failed_at = _last_index(lines, _CATCHUP_FAILED)
    if failed_at is None:
        return None
    stale_at = _last_index(lines[max(0, failed_at - _STALE_WINDOW):failed_at],
                           _STALE_ARCHIVE)
    if stale_at is None:
        return None
    anchor = max(0, failed_at - _STALE_WINDOW) + stale_at
    # Most-recent-first: a range that recovered from a transient fault and then
    # hit a 404 is terminal, and the 404 is the later line.
    for line in reversed(lines[max(0, anchor - _MARKER_WINDOW):anchor + 1]):
        if any(marker in line for marker in _TERMINAL):
            return None       # the object genuinely is not there; retrying cannot help
        for marker in _TRANSIENT:
            if marker in line:
                return marker
    return None


def _last_index(lines, needle):
    for i in range(len(lines) - 1, -1, -1):
        if needle in lines[i]:
            return i
    return None


def _tail(end, attempt):
    """The archive's last lines. A missing or unreadable archive is not an
    error: it means the collector has nothing to say, and the caller treats
    that as "no fetch fault".

    Split only the final bytes, never readlines(): gzip cannot seek, so the
    whole archive is decompressed either way, but building a str per line to
    keep 400 of them is 90% of the cost on a tip range. The leading fragment is
    dropped because the slice lands mid-line.
    """
    try:
        with gzip.open(record.log_path(end, attempt), 'rb') as fh:
            data = fh.read()
    except (OSError, EOFError, gzip.BadGzipFile):
        return []
    lines = data[-_TAIL_BYTES:].decode('utf-8', 'replace').splitlines()
    if len(data) > _TAIL_BYTES:
        lines = lines[1:]           # only then did the slice land mid-line
    return lines[-_TAIL_LINES:]
