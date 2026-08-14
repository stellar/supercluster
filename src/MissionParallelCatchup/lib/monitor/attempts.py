"""What a range's attempts add up to.

One layer above `records`, which reads a single attempt's files: everything here
answers a question about the RANGE by walking the attempts behind it -- the peak
it reached, what it cost, whether an exit 3 is worth retrying.

Nothing here touches the cluster. It is also where the blocking file reads live,
so it is the seam an executor would wrap if the monitor ever moved onto a loop.
"""
import gzip
import json
import logging
import zlib

import attempt_files
import records

logger = logging.getLogger()


# PVC size is not profiled: growing it buys no packing. Ephemeral storage is,
# but only in ephemeral mode on on-demand nodes. Any field may be absent, and
# the consumer falls back to its default.
PEAK_FIELDS = ('peakAnonBytes', 'peakWorkingSetBytes',
               'peakEphemeralBytes')


def peaks_for_range(end, attempt=1):
    """Highest peak any attempt at this range reached, per axis.

    Not just the successful attempt. In pvc mode a pod that dies once replay has
    started leaves /data behind, and the next attempt resumes at LCL+1 with
    RESUME=true -- skipping the archive download and the bucket apply, which is
    where peak memory actually happens. Its peak describes the tail of the range,
    not the range, so profiling the winner alone under-reports by the whole
    download-vs-replay gap. On spot, where eviction is routine and resume is the
    entire point of durable /data, that would make the run unprofileable.

    Attempts that hit a ceiling are counted too. A pod OOM-killed at 8Gi really
    did allocate ~8Gi and wanted more, so its peak is a lower bound on demand,
    not an artifact of the limit -- and it is the attempt most worth keeping,
    because download concurrency scales with available cpu and a pod that
    bursted on an idle node can peak above the one that eventually succeeded.
    Sizing off the quieter attempt would OOM the range again. There is no false
    ratchet: a pod given 8Gi that only touches 1Gi records 1Gi.

    Advisory: used to size a LATER run's requests, never to decide anything
    about this one. Any field may be absent.
    """
    out = {}
    for n in _peak_attempts(end, attempt):
        try:
            with open(records.metrics_path(end, n)) as fh:
                data = json.load(fh)
        except (OSError, ValueError):
            continue
        for k in PEAK_FIELDS:
            v = data.get(k)
            if v is not None and v > out.get(k, 0):
                out[k] = v
    return out


def _hit_a_ceiling(end, attempt):
    """Was this attempt killed at one of its own resource limits?"""
    return (attempt_files.read_outcome(end, attempt) or {}).get('outcome') in ('oom', 'ephemeral')


def _peak_attempts(end, attempt):
    """Attempts whose peaks describe this range: the resumed chain, plus any
    attempt that died at a limit, wherever it sits.

    A ceiling-hit peak is evidence about the range no matter which pass
    produced it -- the process really did allocate that much and want more, so
    it is a lower bound on demand and the next run must size above it. That is
    the whole self-correcting loop: a range that OOMs at L records L, and
    L * PROFILE_MARGIN + PROFILE_CACHE_HEADROOM clears it next time.

    Without this the fresh-start rule silently drops it. Measured on ssc-test
    2026-07-30: an OOM during replay resumes (RESUME accepted, 224 of 252) and
    stays in the chain, but an OOM during download does not (25 of 252) -- and
    a run at higher cpu is download-bound, so the loop would go quiet exactly
    when it is most needed.

    Peaks only. tx_apply and seconds are summed, and a fresh start redoes work
    the dropped attempt already did, so including it there would double-count.
    """
    chain = set(_resumed_chain(end, attempt))
    return sorted(chain | {n for n in range(1, int(attempt) + 1)
                           if n not in chain and _hit_a_ceiling(end, n)})


def _resumed_chain(end, attempt):
    """Attempts describing one continuous pass over the range, oldest first.

    Stops at the last attempt that ran new-db: that one covered the whole range
    on its own, so nothing before it is part of the same pass.
    """
    first = int(attempt)
    while first > 1 and _attempt_resumed(end, first):
        first -= 1
    return range(first, int(attempt) + 1)


def _attempt_resumed(end, attempt):
    """Did this attempt pick up at LCL+1 rather than run new-db?

    The collector's record is authoritative, and only records a resume. It
    decides from the live stream at pod startup and re-reads its own archive at
    finalization if it could have missed the line, so an absent flag means the
    attempt did not resume -- there is nothing a second archive read here could
    find that the collector did not. Measured across a 4805-attempt run: 744
    resumes, and not one the record missed.
    """
    try:
        with open(records.metrics_path(end, attempt)) as fh:
            return json.load(fh).get('resumed') is True
    except FileNotFoundError:
        return False
    except ValueError as e:
        logger.warning("could not parse resume metrics for range %s attempt %s: %s",
                       end, attempt, e)
    except OSError as e:
        logger.warning("could not read resume metrics for range %s attempt %s: %s",
                       end, attempt, e)
    return False


# Exit 3 covers a graceful SIGTERM as well as a real failure, so what decides is
# the cascade stellar-core prints when a history fetch fails.
#
# The anchor pair is adjacent by construction: GetHistoryArchiveStateWork emits
# its message on the same scheduler tick as its child's WORK_FAILURE. The aws
# stderr is relayed unsynchronised, so it is searched for nearby instead.
_FETCH_ANCHOR = 'maybe stale archive'


_FETCH_GAVE_UP = 'Catchup failed'


# Faults in front of S3: the object is fine, this pod could not reach it. A fresh
# pod on another node is the fix, which is what a retry is.
_FETCH_TRANSIENT = ('Could not connect to the endpoint URL',
                    'Unable to locate credentials', 'ExpiredToken',
                    'RequestTimeout', 'SlowDown', 'ConnectTimeoutError')


# The object genuinely is not there. Retrying cannot help.
_FETCH_TERMINAL = ('Key does not exist', '(404)', 'NoSuchKey')


# Lines between the anchor and the give-up line. Small, so a wider window
# cannot credit an earlier fetch failure the range recovered from.
_ANCHOR_WINDOW = 6


# Lines back from the anchor to find the aws stderr that explains it. Wider,
# because concurrent downloads interleave with it during the bucket phase.
_CAUSE_WINDOW = 25


# Tail of the archive to read. Catchup failed is always near the end, and a
# bucket-phase archive can be very large.
_TAIL_LINES = 400


def _archive_tail(end, attempt):
    """Last _TAIL_LINES lines of an attempt's archive, or [] if unreadable."""
    path = records.log_path(end, attempt)
    tail = []
    try:
        with gzip.open(path, 'rt', errors='replace') as fh:
            for line in fh:
                tail.append(line)
                if len(tail) > _TAIL_LINES:
                    del tail[0]
    except FileNotFoundError:
        return []
    except (EOFError, gzip.BadGzipFile, zlib.error) as e:
        logger.warning("could not read archive %s: %s", path, e)
        return []
    except OSError as e:
        logger.warning("could not open archive %s: %s", path, e)
        return []
    return tail


def exit3_retry_cause(end, attempt):
    """Why an exit-3 attempt is retryable, or None to condemn it.

    Conservative on purpose: only a fetch fault this function can name earns a
    retry. An archive it cannot read, a give-up with no fetch cascade in front of
    it, or an aws error it does not recognise all condemn the range -- the
    archive survives on the volume, so an unrecognised cause can be read off a
    failed run and added here rather than guessed at now.
    """
    tail = _archive_tail(end, attempt)
    if not tail:
        return None
    gave_up = max((i for i, l in enumerate(tail) if _FETCH_GAVE_UP in l),
                  default=None)
    if gave_up is None:
        return None
    anchor = max((i for i in range(max(0, gave_up - _ANCHOR_WINDOW), gave_up)
                  if _FETCH_ANCHOR in tail[i]), default=None)
    if anchor is None:
        return None
    window = tail[max(0, anchor - _CAUSE_WINDOW):anchor + 1]
    for line in reversed(window):
        for mark in _FETCH_TERMINAL:
            if mark in line:
                return None
        for mark in _FETCH_TRANSIENT:
            if mark in line:
                return mark
    return None


def tx_apply_for_range(end, attempt=1):
    """Exact known 'ledger.transaction.apply' seconds for the whole range.

    Summed across the resumed chain, not read from the winning attempt alone.
    medida's total is per-process, so a pod that resumes at LCL+1 reports only
    the transactions it replayed -- on a range that was interrupted mid-replay
    that is the tail, not the range.

    Slightly over-counts: replay restarts at the checkpoint boundary containing
    LCL, so up to 64 ledgers can be applied twice. Against a 16320-ledger range
    that is <=0.4%, but it is a fixed ledger cost rather than a percentage, so
    it grows as ranges shrink.
    """
    total = None
    for n in _resumed_chain(end, attempt):
        leg = _tx_apply_for_attempt(end, n)
        if leg is None:
            # A disrupted process often never prints its final medida block.
            # Absence says the chain is incomplete; a partial sum under-reports.
            return None
        total = leg if total is None else total + leg
    return total


def seconds_for_range(end, attempt=1, final=None):
    """Compute time for the whole range, summed across the resumed chain.

    `final` is the winning attempt's own duration, which reconcile has in hand
    from the pod. Earlier legs come from their .outcome, written when the
    monitor classified the failure and still had the pod.

    This is compute, not elapsed: scheduling, image pull, node startup and gaps
    between attempts are not in it (see wallSeconds for total scheduling and k8s
    noise time).
    """
    total = None
    for n in _resumed_chain(end, attempt):
        if n == int(attempt) and final is not None:
            leg = final
        else:
            leg = _attempt_seconds(end, n)
        if leg is None:
            return None
        total = leg if total is None else total + leg
    return total


def _attempt_seconds(end, attempt):
    """Best durable duration for one attempt, or None when it was never saved."""
    # .outcome carries the pod's own terminated timestamps, and is absent
    # whenever the pod was reaped before classification -- every spot eviction --
    # so fall back to the collector's estimate.
    leg = (attempt_files.read_outcome(end, attempt) or {}).get('attemptSeconds')
    if leg is not None:
        return leg
    try:
        with open(records.metrics_path(end, attempt)) as fh:
            data = json.load(fh)
        # A poller clock starts when the collector attached, so after a restart
        # it is a lower bound and must not pass as chain compute. A clock dated
        # from the container's own startTime is accepted: it measures the
        # container, and it is the only duration a disrupted attempt produces.
        if (data.get('attemptSecondsExact') is False
                and data.get('attemptSecondsFromContainerStart') is not True):
            return None
        return data.get('attemptSeconds')
    except (OSError, ValueError):
        return None


def reconstruct_completed_profile(end, attempt):
    """Recompute recoverable profile fields from immutable attempt artifacts.

    Durations and tx-apply totals follow only the continuous resumed chain, so a
    fresh retry never double-counts discarded work. Peaks use that chain plus
    every attempt that hit a resource ceiling. Missing duration or tx-apply legs
    make that aggregate absent rather than publishing a lower bound as a total.
    Complete tx-apply legs retain the existing <=64-ledger overlap.

    Reconstructable: persisted sampled peaks, complete attemptSeconds chains in
    .outcome/.metrics, and complete txApplySeconds chains in .metrics/.log.gz.
    Not reconstructable: whole-chain wall time, samples never persisted, or a
    duration/tx-apply leg whose process and archive are both gone.
    """
    rebuilt = peaks_for_range(end, attempt)
    seconds = seconds_for_range(end, attempt)
    if seconds is not None:
        rebuilt['seconds'] = seconds
    tx_apply = tx_apply_for_range(end, attempt)
    if tx_apply is not None:
        rebuilt['txApply'] = tx_apply
    return rebuilt


def _apply_profile_reconstruction(record, rebuilt):
    """Merge reconstruction without lowering stronger persisted evidence."""
    updates = {}
    for key, value in rebuilt.items():
        current = record.get(key)
        if current is None or value > current:
            updates[key] = value
    record.update(updates)
    return updates


def _repair_completed_profile(end, attempt, record):
    """Merge exact reconstruction and remove unverifiable chain aggregates."""
    rebuilt = reconstruct_completed_profile(end, attempt)
    updates = _apply_profile_reconstruction(record, rebuilt)
    if len(list(_resumed_chain(end, attempt))) > 1:
        for key in ('seconds', 'txApply'):
            if key not in rebuilt and record.get(key) is not None:
                # Once resume proves this is a chain, the sum of surviving legs
                # is a lower bound rather than a total, so omit it.
                record.pop(key)
                updates[key] = None
    return updates


def _tx_apply_for_attempt(end, attempt=1):
    """Exact 'ledger.transaction.apply' seconds for ONE attempt, or None.

    Read from the collector's record and nowhere else. The collector parses the
    block out of the live stream and re-reads its own archive at finalization
    when it has no total, so a second reader here could only repeat that work
    over the same bytes with the same medida window -- which is how the monitor
    came to carry its own copy of the parser.
    """
    try:
        with open(records.metrics_path(end, attempt)) as fh:
            value = json.load(fh).get('txApplySeconds')
    except (OSError, ValueError):
        return None
    return None if value is None else float(value)
