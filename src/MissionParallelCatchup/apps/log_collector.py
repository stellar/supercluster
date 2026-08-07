"""Streaming log collector for parallel catchup.

Runs as a sidecar next to job_monitor, sharing its /logs volume.

Why not read logs after a Job finishes: worker pods are one per ledger range,
and Karpenter deletes the node roughly a minute after its last running pod
exits, taking every pod object with it. Anything that reads after the fact is
racing that deletion. Polling each pod's log on an interval also keeps a
straggler readable *while* it is stuck, which is the case that turns a 5h run
into a 10h one; a condemned pod gets a follow stream so its last lines land
before it goes.

Resume is idempotent across both a dropped stream and a restart of this
process:

  coarse   reconnect with sinceTime=<last durably written timestamp>; the API
           only accepts second granularity, so this deliberately overlaps
  precise  every line carries a kubelet RFC3339Nano timestamp (timestamps=true),
           so drop any line <= last_ts. That removes the overlap exactly and
           does not depend on stellar-core's own log format.

Residual: if this dies between flushing log bytes and rewriting the state file,
the next run replays from a slightly older timestamp and a few lines duplicate.
Bounded by one poll's worth of lines, since the state file is rewritten at the
end of every poll. "At least once, deduped to near-exact" rather than exactly
once.
"""

import asyncio
import gzip
import io
import json
import logging
import os
import re
import ssl
import sys
import zlib
from datetime import datetime

import aiohttp
from logger import build_logger
import config
import medida
import records

CONTAINER = os.getenv('WORKER_CONTAINER', 'stellar-core')
POLL_SECONDS = float(os.getenv('COLLECTOR_POLL_SECONDS', 5))
# Poll cycles a stream gets to finalize itself after its pod leaves the pod list
# before it is cancelled outright. One cycle is usually enough; the margin is for
# a stream still finalizing: writing its .metrics and closing its archive.
VANISHED_GRACE_CYCLES = int(os.getenv('COLLECTOR_VANISHED_GRACE_CYCLES', 3))
# Peak memory now comes from kubelet, not Prometheus. kubelet reports rssBytes
# and workingSetBytes per container in the same /stats/summary payload this
# already fetches for ephemeral storage, at ~10s cAdvisor housekeeping against a
# 30s scrape -- and without depending on Prometheus being up, being reachable,
# or still retaining the window. The old _promql helper swallowed all three of
# those failures into "no peak", so an outage produced a profile that looked
# complete and was empty. cpu is not sampled at all: the request is fixed at
# REQ_CPU, so a measured value has nothing to size.
# Peaks are held per pod and flushed on significant growth, so a restart loses
# at most PEAK_FLUSH_RATIO of a range's high-water rather than all of it --
# Prometheus's server-side max_over_time needed no such state.
PEAK_FLUSH_RATIO = float(os.getenv('PEAK_FLUSH_RATIO', 1.05))
# Seconds between polls of one pod's log. Latency here is archive lag, not
# anything a decision waits on; 4096 pods at 10s is ~90 concurrent polls.
LOG_POLL_SECONDS = float(os.getenv('LOG_POLL_SECONDS', 10))
# Concurrent in-flight log polls, across all pods. This is the whole point of
# polling: it is independent of how many pods exist, where follow=true needed
# one held connection per pod forever.
MAX_CONCURRENT_POLLS = int(os.getenv('MAX_CONCURRENT_POLLS', 96))
# Most one poll may read before it stops. A pod that has been unwatched for a
# while has a large backlog; this bounds a single response, and the next poll
# picks up from the timestamp this one reached. Also the backstop against a
# blob that never terminates -- a progress meter emitting only carriage returns
# would otherwise grow the buffer until the sidecar OOMs, with 2096 streams
# doing it at once.
MAX_POLL_CHARS = int(os.getenv('MAX_POLL_CHARS', 8388608))
# Bounds in-flight polls across every pod. Lives beside its own constant rather
# than among the peak dicts, where it landed inside the region the scanner tests
# exec and broke six of them on an asyncio NameError.
_poll_slots = asyncio.Semaphore(MAX_CONCURRENT_POLLS)
# Separate from _poll_slots on purpose -- see MAX_DOOMED_FOLLOWS.
_follow_slots = asyncio.Semaphore(int(os.getenv('MAX_DOOMED_FOLLOWS', 256)))
# pod name -> Event, set by the main loop the moment it first observes the pod
# terminal. poll_pod waits on it instead of sleeping blind, so the final read
# happens within the pod-list cadence rather than up to LOG_POLL_SECONDS later.
# That window is the only thing standing between a spot reclaim and the last
# lines the container wrote.
_wake = {}
# pod name -> its own start->finish, read off the pod while it still exists.
_pod_secs = {}
# pod name -> status.startTime, kept so an attempt whose object vanished before
# any cycle saw its terminated timestamp can still be dated from the container's
# own start rather than from whenever this poller happened to open.
_pod_start = {}
# Pods carrying a DisruptionTarget condition: the cluster has committed to
# destroying them and, on spot, gives about two minutes' notice.
#
# Waking the poller is not enough on its own. stellar-core prints its medida
# block ~4ms after SIGTERM and the pod object is deleted seconds later, so an
# interval poll straddles the whole thing -- measured on the 2048-worker run,
# 810 evictions lost 809 txApply values and 790 exact durations. A held
# connection already has those bytes when the process dies.
#
# Safe here precisely because it is scoped and short-lived, not because the
# count is small: global follow=true cost the sidecar 1444 MiB of a 2048 MiB
# limit at 2096 streams held for whole ranges, where these are held only for
# the drain. See MAX_DOOMED_FOLLOWS for the sizing.
_doomed = {}
# Longest a follow stream will hang on to a doomed pod. Spot gives 120s; past
# roughly double that the notice was withdrawn (Karpenter cancelled the drain)
# and the stream would otherwise be held for the life of the range.
# 0 disables the follow path entirely and leaves interval polling to do it,
# which is only safe when prestopSleepSeconds is holding the pod open instead.
DOOMED_FOLLOW_SECONDS = float(os.getenv('DOOMED_FOLLOW_SECONDS', 300))
# Follows get their OWN budget rather than sharing _poll_slots.
#
# Sharing was a starvation bug: a follow holds its slot for the whole drain, so
# a reclaim condemning more pods than there are slots would consume all of them
# and stop every other pod in the run from being polled at all -- turning a
# partial node loss into a run-wide blackout. With a separate budget the worst
# case is that some condemned pods fall back to polling, which is a proven path
# rather than a degradation -- 1s polling captured txApply on its own with no
# follow and no preStop.
#
# Sized well above any plausible simultaneous disruption rather than at the
# measured one. A whole-AZ spot reclaim is not bounded by Karpenter's
# disruption budget, so the ~43 pods a 10% budget implies is a floor, not a
# ceiling. The old always-on design sustained 2096 concurrent streams, and it
# paid far more per stream than this does: it held a persistent GzipFile and
# aiohttp buffers for a pod's entire multi-hour life, where _follow_tail builds
# a fresh gzip member per flush, keeps nothing between them, and lives for the
# 10-120s of a drain. 256 x the old 0.69 MiB upper bound is 177 MiB against a
# 2048 MiB limit, and the true figure is lower.
MAX_DOOMED_FOLLOWS = int(os.getenv('MAX_DOOMED_FOLLOWS', 256))
# Poll interval for a condemned pod, replacing LOG_POLL_SECONDS for as long as
# it is doomed. This is the cheap half of the fix and the one that does the
# work: measured on ssc-test, preStop delays SIGTERM but leaves the gap between
# the medida block and the pod object being deleted at ~9s, so a blind 10s poll
# straddles it -- which it did, losing txApply even with a 60s preStop holding
# the pod open. Polling that same window every second cannot miss it.
#
# Costs no held connections, unlike a follow stream: ~120 short requests over a
# 2-minute drain per condemned pod, bounded by the existing _poll_slots.
# sinceTime has 1s granularity, so going below 1s only re-reads the same second.
DOOMED_POLL_SECONDS = float(os.getenv('DOOMED_POLL_SECONDS', 1))
# How long each watch connection is allowed to live before the apiserver closes
# it and we reconnect. Bounded rather than infinite so a silently-dead stream
# self-heals; the reconnect resumes from the last resourceVersion, so nothing is
# missed across it. 0 disables the watch and leaves detection to the pod list.
WATCH_TIMEOUT_SECONDS = int(os.getenv('WATCH_TIMEOUT_SECONDS', 600))
# Pause before re-opening a watch that failed. Only covers hard errors: a clean
# timeout reconnects immediately.
WATCH_RETRY_SECONDS = float(os.getenv('WATCH_RETRY_SECONDS', 1))
# Fields that only ever grow. write_metrics maxes these instead of overwriting,
# so a restarted poller starting its high-water at zero cannot lower one.
PEAK_KEYS = ('peakAnonBytes', 'peakWorkingSetBytes', 'peakEphemeralBytes')
# Failed polls tolerated after a pod goes terminal before we stop asking. Its
# log is not coming back, and spinning on it holds a task and a poll slot for
# the rest of the run; a couple of retries still absorb a transient 500, which
# arrived in bursts at ramp. Returning bare on one of those used to drop the
# metrics for every range whose last read happened to throw.
TERMINAL_POLL_ATTEMPTS = int(os.getenv('TERMINAL_POLL_ATTEMPTS', 3))
# Phases whose log endpoint can actually answer. Pending has no container yet
# and Unknown means the node stopped reporting; the terminal phases are kept
# because that is where a pod's final output lives.
POLLABLE_PHASES = ('Running', 'Succeeded', 'Failed')


SA = '/var/run/secrets/kubernetes.io/serviceaccount'
API = f"https://{os.getenv('KUBERNETES_SERVICE_HOST', 'kubernetes.default')}:{os.getenv('KUBERNETES_SERVICE_PORT', '443')}"
# Read-only kubelet port. A seam for tests, which serve the payload on a
# loopback port rather than reaching a real node.
KUBELET_PORT = 10250

logger = build_logger('log_collector', name='log-collector', to_file=False)


def token():
    # Projected service account tokens rotate, so this is re-read per request
    # rather than cached at startup.
    with open(os.path.join(SA, 'token')) as fh:
        return fh.read().strip()


def ssl_ctx():
    return ssl.create_default_context(cafile=os.path.join(SA, 'ca.crt'))


def base(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}")


def _is_condemned(pod):
    """The DisruptionTarget reason if the cluster has committed to destroying
    this pod, else None.

    DisruptionTarget covers the cases that cost us measurements: a spot reclaim,
    a Karpenter drain, and node pressure. It does NOT cover a kubelet
    ephemeral-storage eviction -- classify() handles that one from
    status.message -- and it is deliberately not inferred from a deletionTimestamp,
    which is also set by the monitor reaping a Job that already finished.

    The reason separates a warning from a postmortem, which the bare condition
    cannot: EvictionByEvictionAPI is a drain that still has to deliver SIGTERM,
    while DeletionByTaintManager is stamped ~40s after the node went NotReady,
    on a container that already died unsignalled. In the second case no medida
    block was ever written, so a missing txApply is not a capture race.
    """
    for cond in ((pod.get('status') or {}).get('conditions') or []):
        if cond.get('type') == 'DisruptionTarget' and cond.get('status') == 'True':
            return cond.get('reason') or 'Unknown'
    return None


def _mark_condemned(pod, name, end, attempt):
    """Flag a condemned pod so its poller opens a follow. Idempotent.

    Shared by the pod-list sweep and the watch so the two cannot drift: whichever
    sees the condition first does the work, the other no-ops on the _doomed
    check.

    Detection latency, not the follow, is what loses the metric. stellar-core
    exits about a second after SIGTERM and the pod object is reaped right behind
    it, so a condemned pod exists for only a few seconds. Measured on this
    cluster at prestopSleepSeconds=5, the 5s list sweep caught that window about
    half the time: of 52 mid-replay legs, 32 lost txApply and 25 of those were
    seen but seen too late to open a stream.
    """
    if name in _doomed:
        return False
    if (pod.get('status') or {}).get('phase') in ('Succeeded', 'Failed'):
        # Already finished. Its log is complete and a follow would only re-read
        # a dead pod every iteration.
        return False
    doom = _is_condemned(pod)
    if not doom:
        return False
    _doomed[name] = doom
    # Recorded now, because the evidence does not survive the node: once the
    # object is gone there is no way to tell a drain we lost a race with from a
    # corpse that never had a metric to lose.
    write_metrics(end, attempt, {'disruptionReason': doom})
    if name in _wake:
        # Break the current sleep so the follow opens now rather than up to
        # LOG_POLL_SECONDS from now.
        _wake[name].set()
    logger.info("range %s: pod %s condemned (%s), opening follow", end, name, doom)
    return True


def pod_seconds(pod):
    """Container start -> finish from the pod's own status, or None.

    The same fields the monitor reads. A terminal pod still carries them until
    it is deleted, so this works even when the collector never watched the
    container run -- which the poller's own elapsed time cannot.
    """
    st = pod.get('status') or {}
    start = st.get('startTime')
    if not start:
        return None
    for cs in (st.get('containerStatuses') or []):
        term = (cs.get('state') or {}).get('terminated') or {}
        fin = term.get('finishedAt')
        if fin:
            try:
                a = datetime.strptime(start, '%Y-%m-%dT%H:%M:%SZ')
                b = datetime.strptime(fin, '%Y-%m-%dT%H:%M:%SZ')
            except ValueError:
                return None
            return (b - a).total_seconds()
    return None


def done_path(end, attempt):
    return base(end, attempt) + '.done'


def read_state(end, attempt):
    try:
        with open(base(end, attempt) + '.state') as fh:
            ts = fh.read().strip()
    except OSError:
        return None
    # Also repairs a state file poisoned by an earlier build.
    return ts if ts and _TS_RE.match(ts) else None


def write_state(end, attempt, ts):
    path = base(end, attempt) + '.state'
    try:
        records.write_atomic(path, ts)
    except OSError as e:
        logger.warning("could not persist state for range %s: %s", end, e)


def discard(end, attempt):
    # .metrics deliberately survives: it holds tx_apply for a range that
    # succeeded, which is the only case this runs in. Dropping it would let a
    # log-retention flag silently delete a Grafana series.
    for suffix in ('.log.gz', '.state'):
        try:
            os.remove(base(end, attempt) + suffix)
        except OSError:
            pass


# kubelet returns plain text such as "unable to retrieve container logs for
# containerd://..." when a container is not up yet. That has no timestamp, so
# partitioning on the first space yields "unable", which then goes into the
# state file and every later request asks for sinceTime=unableZ -> HTTP 400,
# forever. Observed on ssc-test the moment evicted pods were replaced.
_TS_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?$")

_TX_METRIC = "metric 'ledger.transaction.apply'"

class TxApplyScanner:
    """Pull the medida tx-apply total out of the stream as it goes past.

    stellar-core prints this block once, just before exit. Scanning here rather
    than re-reading the log later is what makes the metric independent of pod
    lifetime: by the time job_monitor sees the Job succeed, Karpenter may
    already have reaped the node, and with saveSuccessLogs=false the archive is
    gone too. These bytes pass through this process exactly once, so this is the
    only place guaranteed to see them.
    """

    # Shared with job_monitor's archive re-read rather than restated: they used
    # to agree by comment, and a divergence would hand the recovery path the
    # same blind spot it exists to cover. Measured on ssc-test 2026-08-04, a
    # /info liveness response interleaved into the block put `sum` 91 lines
    # below the header and both readers gave up 76 lines short -- one leg in 233.
    WINDOW = medida.WINDOW
    HARD_WINDOW = medida.HARD_WINDOW

    # Printed by RESUME_SCRIPT before stellar-core starts. Its counterpart,
    # "RESUME DECLINED", means new-db ran and this attempt did the whole range,
    # so the colon is load-bearing -- it is what separates the two.
    RESUME_MARK = 'RESUME: '
    RESUME_DECLINED_MARK = 'RESUME DECLINED:'

    def __init__(self, recreated=False):
        self.seconds = None
        self.resumed = False
        self.resume_decided = False
        # A new poller starting from durable .state missed every earlier line.
        # Finalization must recover scanner-only facts from the archive.
        self.recreated = recreated
        self._left = 0
        self._span = 0

    def feed(self, line):
        if self.RESUME_MARK in line:
            self.resumed = True
            self.resume_decided = True
        elif self.RESUME_DECLINED_MARK in line:
            self.resume_decided = True
        if _TX_METRIC in line:
            self._left = self.WINDOW
            self._span = self.HARD_WINDOW
            return
        if self._left <= 0:
            return
        m = medida.SUM_RE.search(line)
        if m:
            self.seconds = float(m.group(1)) / 1000.0
            self._left = 0
            return
        self._span -= 1
        if self._span <= 0 or medida.ANY_METRIC.search(line):
            # Ran out of rope, or another timer's block started -- either way our
            # sum is not coming.
            self._left = 0
            return
        if medida.METRIC_LINE.search(line):
            # Only medida's own statistics count. Interleaved output from another
            # thread is noise between us and the sum, not evidence we have passed
            # it.
            self._left -= 1


def scan_archive(end, attempt, need_tx=False):
    """Recover scanner state from complete gzip members already on disk."""
    path = base(end, attempt) + '.log.gz'
    scanner = TxApplyScanner()
    try:
        with gzip.open(path, 'rt', errors='replace') as fh:
            for line in fh:
                scanner.feed(line)
                # The resume decision is at process startup. Avoid decompressing
                # a multi-gigabyte worker log when that is all the caller needs.
                if scanner.resume_decided and not need_tx:
                    break
    except FileNotFoundError:
        return scanner
    except (EOFError, gzip.BadGzipFile, zlib.error) as e:
        # Keep facts found in complete prefix members. A torn final member cannot
        # invalidate an earlier RESUME line or complete medida block.
        logger.warning("could only partially recover scanner state from %s: %s", path, e)
    except OSError as e:
        logger.warning("could not open scanner archive %s: %s", path, e)
    return scanner


def write_metrics(end, attempt, values):
    """Persist per-range measurements for job_monitor's reconcile to read.

    Kept out of .outcome on purpose: that file answers "why did this attempt
    fail" and is only written for failed pods, whereas these are only
    meaningful for one that succeeded.
    """
    path = base(end, attempt) + '.metrics'
    # Merge, and never let a peak go backwards. A measurement already on disk
    # must survive a later write that lacks it -- the peaks are held in memory,
    # so a collector restart would otherwise drop them. But a plain overwrite is
    # wrong for a monotonic quantity: after a restart the fresh poller starts
    # its high-water at zero, and its first flush would replace the higher
    # pre-restart value with a lower one. Lowering a peak undersizes the range
    # next run, which is the one direction that costs an OOM.
    try:
        with open(path) as fh:
            prior = json.load(fh)
    except (OSError, ValueError):
        prior = {}
    merged = {**prior, **values}
    # attemptSeconds is not a peak, but it takes the same rule for the same
    # reason: it is a fixed quantity once the attempt ends, and every source is
    # a lower bound on it -- the pod's own start->finish is exact, the poller's
    # elapsed time covers only the part of the attempt that process was alive
    # for. An attempt is finalized more than once whenever a poller is re-opened
    # for a pod that is still listed (a restarted sidecar, or a 404 on the log
    # endpoint while the pod list is stale), and there the fallback clock starts
    # at the restart: newest-wins turned a recorded 3600s into 0.0s.
    for k in PEAK_KEYS + ('attemptSeconds',):
        a, b = prior.get(k), values.get(k)
        if a is not None and b is not None:
            merged[k] = max(a, b)
    # Once any poller or archive read proves that this attempt resumed, a later
    # restarted poller cannot un-prove it. In particular, a merge containing
    # resumed=False must never lower the durable decision back to fresh.
    if prior.get('resumed') is True or values.get('resumed') is True:
        merged['resumed'] = True
    if (prior.get('attemptSecondsExact') is True
            or values.get('attemptSecondsExact') is True):
        merged['attemptSecondsExact'] = True
    # Same one-way rule: once a duration has been dated from the container's own
    # startTime, a later poller-clock write must not strip the provenance that
    # makes the monitor willing to use it as a chain leg.
    if (prior.get('attemptSecondsFromContainerStart') is True
            or values.get('attemptSecondsFromContainerStart') is True):
        merged['attemptSecondsFromContainerStart'] = True
    values = merged
    try:
        records.write_atomic(path, json.dumps(values))
        logger.info("range %s attempt %s metrics=%s", end, attempt, values)
    except OSError as e:
        logger.warning("could not persist metrics for range %s: %s", end, e)


def classify(pod):
    """Why did this pod fail? Recorded here rather than in job_monitor.

    This process already lists every pod every few seconds to discover streams,
    so it sees terminal transitions first-hand -- a separate watch thread in the
    monitor was observing the same objects a second time. The Job object cannot
    answer this: its condition carries no exit code until a podFailurePolicy
    rule matches, and an admission rejection matches none.
    """
    status = pod.get('status', {})
    if _is_condemned(pod):
        return {'outcome': 'disrupted', 'exitCode': None}
    reason = status.get('reason')
    if reason == 'Evicted' and 'ephemeral' in (status.get('message') or ''):
        # The range's own disk use, not something the cluster did to it.
        # Measured on ssc-test: the kubelet sets no DisruptionTarget for a
        # limit eviction, and stellar-core drains on the eviction SIGTERM and
        # exits 3 -- so the Job condition matches the generic non-zero rule and
        # reads as a plain catchup failure, which gets no retry at all.
        # status.message is the only discriminator and only the pod carries it.
        # Recording it here, while the pod still exists, is the only way to
        # keep the signal.
        return {'outcome': 'ephemeral', 'exitCode': None, 'reason': status.get('message')}
    if reason in ('VolumeAttachmentLimitExceeded', 'OutOfcpu', 'OutOfmemory', 'OutOfpods',
                  'UnexpectedAdmissionError', 'NodeAffinity', 'Shutdown', 'Evicted'):
        return {'outcome': 'rejected', 'exitCode': None, 'reason': reason}
    terms = [cs.get('state', {}).get('terminated') for cs in status.get('containerStatuses', [])]
    terms = [t for t in terms if t]
    if not terms:
        # Nothing ever ran, so this says nothing about the ledger range.
        return {'outcome': 'rejected', 'exitCode': None, 'reason': reason or 'no container status'}
    for t in terms:
        if t.get('reason') == 'OOMKilled':
            return {'outcome': 'oom', 'exitCode': t.get('exitCode')}
        if t.get('exitCode') not in (0, None):
            return {'outcome': 'failed', 'exitCode': t.get('exitCode')}
    # Terminated, but not one container said with what. `failed` here is a lie
    # that costs the whole run: it reads as a genuine catchup failure, which is
    # the one outcome that gets no retry at all. Observed on the r5 run
    # 2026-07-30, range 59018943 -- condemned on attempt 1 with exitCode null,
    # failing a mission that was otherwise 554 for 554.
    return {'outcome': 'unknown', 'exitCode': None}


def record_outcome(pod, end, attempt):
    """Write the verdict next to the log, for job_monitor's reconcile to read."""
    path = base(end, attempt) + '.outcome'
    if os.path.exists(path):
        return
    data = classify(pod)
    data['pod'] = pod['metadata']['name']
    try:
        records.write_atomic(path, json.dumps(data))
        logger.info("range %s attempt %s classified: %s", end, attempt, data['outcome'])
    except OSError as e:
        logger.warning("could not persist outcome for range %s: %s", end, e)


# Peak ephemeral disk, for sizing a later run's ephemeral-storage request.
#
# Only meaningful in ephemeral mode. Sampled for every pod, but only kept for
# ranges that finished -- see the completion gate in finalize. Spot is fine:
# what invalidates a sample is being cut short, not the capacity type.
#
# Prometheus cannot answer this -- cAdvisor reports fs usage per node, with no
# pod label -- so this samples kubelet directly through the apiserver proxy and
# keeps a running max.
_eph_peak = {}
_anon_peak = {}
_ws_peak = {}
# Last value flushed to the volume, per axis: keyed by pod name for anon and by
# "<pod>/eph" for ephemeral. A pod name cannot contain '/', so the two key
# spaces cannot collide.
_peak_flushed = {}
# pod name -> (end, attempt), so a mid-flight peak flush can find its file.
_streaming = {}
# The poller registry, module-level so the watch can open a stream the moment a
# pod appears instead of waiting for the pod-list loop to come round.
#
# One registry with one guard is the whole point: two creators would each hold
# their own in-memory last_ts -- read_state is consulted once, at poll_pod start
# -- so both would re-append the same lines and race each other's write_state.
# main() binds its locals to these, so the loop's existing bookkeeping is
# unchanged and either caller can be the one that wins.
_tasks = {}
_streamed = set()
# session + the terminal/succeeded views poll_pod closes over, published once by
# main() so ensure_stream can be called from outside it.
_stream_ctx = {}


def _flush_peak(name, axis, field, value):
    """Persist a high-water so a sidecar restart cannot lose it.

    Every key in PEAK_KEYS needs this, not just the ones we remembered. The
    peaks live in module dicts, so a restarted collector starts from zero and
    re-accumulates only from whatever the pod is using at that moment. anon had
    it, ephemeral got it when a restart was shown to lose the high-water, and
    peakWorkingSetBytes was missed -- which is how a completed range came back
    with a working set BELOW its own anon, which cannot happen in one sample and
    is trivial across a restart. Measured on the 2026-07-30 run: 136 of 3095
    ranges, 55 of them single-attempt so no retry chain could explain them.

    write_metrics max-merges on PEAK_KEYS, so re-flushing a lower value later is
    harmless; the ratio only keeps this to a handful of writes per pod.
    """
    ref = _streaming.get(name)
    if not ref:
        return
    key = name + '/' + axis
    if value < _peak_flushed.get(key, 0) * PEAK_FLUSH_RATIO:
        return
    _peak_flushed[key] = value
    write_metrics(ref[0], ref[1], {field: value})


def _register_stream(name, end, attempt):
    """Register a poller and durably flush peaks sampled just before it opened."""
    _streaming[name] = (end, attempt)
    for axis, field, values in (
            ('anon', 'peakAnonBytes', _anon_peak),
            ('ws', 'peakWorkingSetBytes', _ws_peak),
            ('eph', 'peakEphemeralBytes', _eph_peak)):
        value = values.get(name)
        if value is not None:
            _flush_peak(name, axis, field, value)


async def sample_kubelet(session, node_ips):
    """Update each pod's peak ephemeral use and peak anon from one snapshot.

    Both axes come out of the same GET, so tracking memory here is free.

    kubelet's `rssBytes` is cgroup v2 `anon` -- measured against a live pod on
    ssc-test it read 482 MiB while the cgroup reported 492 MiB seconds later.
    Anon is the only limit-independent memory figure this workload has: page
    cache expands to fill whatever `memory.max` allows, so `memory.peak` is
    always ~= the limit (measured: a range needing 862 MiB of anon reported a
    12704 MiB peak when given a 24000 MiB limit) and is useless for sizing.

    Sampled rather than exact -- cAdvisor housekeeping is ~10s, so a shorter
    anon spike is invisible. Still ~3x finer than the 30s Prometheus scrape the
    profile used before, which is the undersampling that let profiled ranges
    OOM. The `time` field on this payload runs 1-3s behind wall clock; the ~80s
    lag applies only to the du-based ephemeral figure alongside it.
    """
    for ip in node_ips:
        # Straight at the kubelet, not through the apiserver's node proxy. The
        # proxy needs `nodes/proxy`, which authorizes GET on EVERY kubelet path
        # -- /pods and /containerLogs included, for any namespace scheduled on
        # that node. The kubelet maps /stats/* to its own `nodes/stats`
        # subresource, so going direct is the same data under a grant that
        # cannot read pod inventory or logs at all.
        #
        # ssl=False: EKS kubelet serving certs are self-signed, not issued by
        # the cluster CA the session's context trusts. In-VPC hop to the node's
        # own address.
        url = f"https://{ip}:{KUBELET_PORT}/stats/summary"
        try:
            async with session.get(url, ssl=False,
                                   headers={'Authorization': f'Bearer {token()}'}) as resp:
                resp.raise_for_status()
                summary = await resp.json()
        except Exception as e:
            # Not debug: if this fails the ephemeral axis is silently empty and
            # the profile looks merely "absent" rather than broken.
            logger.warning("kubelet stats unavailable on %s: %s", ip, e)
            continue
        for entry in summary.get('pods', []):
            name = entry.get('podRef', {}).get('name')
            if not name:
                continue
            used = (entry.get('ephemeral-storage') or {}).get('usedBytes')
            if used is not None and config.STORAGE_MODE == 'ephemeral':
                prev = _eph_peak.get(name, 0)
                if int(used) > prev:
                    _eph_peak[name] = int(used)
                    logger.info("peak ephemeral for %s: %.2f GiB", name, used / 1073741824)
                    # Flushed on growth for the same reason as anon below, and
                    # re-measuring does not recover it: disk use is not
                    # monotonic -- stellar-core drops its download staging once
                    # buckets are applied -- so a replacement sidecar watching
                    # the tail of the same pod sees a fraction of the real
                    # high-water. This figure sizes the next run's
                    # ephemeral-storage request, and one that comes back too
                    # small is an eviction.
                    _flush_peak(name, 'eph', 'peakEphemeralBytes', int(used))
            for c in entry.get('containers', []):
                # The worker container only. Sidecars share the pod, so summing
                # across containers -- or letting the last one win -- would size
                # the range from whichever one kubelet happened to list last.
                if c.get('name') != CONTAINER:
                    continue
                # Absent for the first seconds of a container's life, before
                # cAdvisor has stats for it. Every later poll carries it, so a
                # miss here costs nothing: anon is at its lowest during startup.
                mem = c.get('memory') or {}
                ws = mem.get('workingSetBytes')
                if ws is not None and int(ws) > _ws_peak.get(name, 0):
                    _ws_peak[name] = int(ws)
                    _flush_peak(name, 'ws', 'peakWorkingSetBytes', int(ws))
                rss = mem.get('rssBytes')
                if rss is None:
                    continue
                # High-water, never last-seen: anon oscillates through the
                # download phase, so a later lower sample must not lower it.
                if int(rss) <= _anon_peak.get(name, 0):
                    continue
                _anon_peak[name] = int(rss)
                # Held in memory until the stream ends, so a collector restart
                # would otherwise reset a range's high-water to whatever it is
                # using at that moment -- under-reporting, which sizes the next
                # run too small. Flushing only on PEAK_FLUSH_RATIO growth keeps
                # this to a handful of writes over a pod's life instead of one
                # per sample per pod.
                _flush_peak(name, 'anon', 'peakAnonBytes', int(rss))


def _mark_done(end, attempt):
    path = done_path(end, attempt)
    try:
        records.write_atomic(path, '')
    except OSError as e:
        # Costs a Job that waits out JOB_TTL_SECONDS, never correctness.
        logger.warning("could not mark range %s attempt %s done: %s", end, attempt, e)


async def finalize(session, pod, end, attempt, tx, done_ok, started=None):
    """Persist everything this attempt owes, then let its stream go.

    Reached from three places, and deliberately ONE implementation: a clean end
    of stream once the pod is terminal, a 404 once the pod object is gone, and
    a terminal pod whose polls keep failing past TERMINAL_POLL_ATTEMPTS. Two
    copies of the metrics/discard logic is how one path silently stops writing
    peakAnonBytes while the other keeps working.

    The 404 path used to not exist, so a pod deleted while Running -- reaped
    node, eviction, or the monitor deleting a finished Job -- left its stream
    retrying every 30s for the rest of the run, holding a connection slot the
    whole time. Note the converse: an interrupted read on a pod that is STILL
    RUNNING must not come here. Finalizing then writes a truncated peak and
    leaves the range looking measured when it is not.
    """
    # Before discard: on success the archive is about to be deleted.
    measured = {}
    observed = _pod_secs.pop(pod, None)
    since_start = None
    began = _pod_start.pop(pod, None)
    if observed is None and began:
        # The container started at `began` and has just stopped -- finalize is
        # reached on end of stream or a 404, both within a second or two of the
        # exit. Not exact, because the true end is terminated.finishedAt, but it
        # dates the attempt from the container rather than from this poller, and
        # a re-opened poller's clock can be near zero against a multi-hour run.
        try:
            since_start = (datetime.utcnow() - datetime.strptime(
                began, '%Y-%m-%dT%H:%M:%SZ')).total_seconds()
        except ValueError:
            since_start = None
    if observed is not None:
        # The pod's own timestamps, not how long this poller happened to watch.
        measured['attemptSeconds'] = round(observed, 1)
        measured['attemptSecondsExact'] = True
    elif since_start is not None and since_start > 0:
        measured['attemptSeconds'] = round(since_start, 1)
        # Not exact -- the true end is terminated.finishedAt, and this is
        # "now, a second or two after the stream ended". But it IS a measure of
        # the container's own lifetime rather than of this process's attention
        # span, which is the distinction the monitor's chain gate cares about.
        # Measured on ssc-test against two evicted pods: 370.9s and 375.1s
        # against a true ~373s, so +/-1%, versus the poller clock's -46%.
        measured['attemptSecondsExact'] = False
        measured['attemptSecondsFromContainerStart'] = True
    elif started is not None:
        # Fallback only: the monitor's figure comes from the pod's terminated
        # timestamps and is preferred when it exists. write_metrics keeps this
        # from lowering a duration already on the volume -- an attempt can be
        # finalized twice, and the second poller's clock started at the restart.
        measured['attemptSeconds'] = round(
            asyncio.get_event_loop().time() - started, 1)
        measured['attemptSecondsExact'] = False
    # RESUME is printed before stellar-core starts and medida once at exit, so a
    # recreated poller can miss either forever. The archive was appended before
    # finalization; recover only the state this scanner could have missed.
    archived = None
    need_resume = int(attempt) > 1 and not tx.resume_decided
    # Not gated on `recreated`: a poller that ran start to finish can still
    # miss the block, which stellar-core prints once at exit, so a stream that
    # ends a beat early has no total and nothing to recreate.
    need_tx = tx.seconds is None
    if need_resume or need_tx:
        archived = scan_archive(end, attempt, need_tx=need_tx)
    if tx.resumed or (archived is not None and archived.resumed):
        # Not a peak -- PEAK_FIELDS filters it out of the profile. peaks_for_range
        # reads it to decide how far back to aggregate: a resumed attempt only
        # measured the tail of its range, so the attempt before it still counts.
        measured['resumed'] = True
    tx_seconds = tx.seconds
    if tx_seconds is None and archived is not None:
        tx_seconds = archived.seconds
    if tx_seconds is not None:
        measured['txApplySeconds'] = tx_seconds
    _peak_flushed.pop(pod, None)
    _peak_flushed.pop(pod + '/eph', None)
    _streaming.pop(pod, None)
    # One _wake entry per pod, and pods are per range per attempt: 3979 ranges
    # plus their retries would accumulate here for the life of the run.
    _wake.pop(pod, None)
    anon = _anon_peak.pop(pod, None)
    if anon is not None:
        # Recorded for every attempt, not just the winner. peaks_for_range takes
        # the max across attempts, so a partial attempt can only ever raise the
        # figure, never lower it -- which is what makes a resumed range (pvc mode,
        # killed once replay started) report the download-phase peak it actually
        # hit rather than its tail. The monitor drops an attempt from the axis it
        # died on, since an OOM-killed peak measures the limit, not demand.
        measured['peakAnonBytes'] = anon
    ws = _ws_peak.pop(pod, None)
    if ws is not None:
        # Diagnostic only -- working set counts active page cache, which grows
        # to fill whatever limit the pod was given, so it must never size
        # anything. Kept because the anon/ws gap is what tells you a range is
        # cache-heavy rather than genuinely large.
        measured['peakWorkingSetBytes'] = ws
    eph = _eph_peak.pop(pod, None)
    if eph is not None:
        # Same as anon: max across attempts upstream, and an attempt evicted at
        # its ephemeral limit is dropped from this axis there.
        measured['peakEphemeralBytes'] = eph
    if measured:
        write_metrics(end, attempt, measured)
    if not config.SAVE_SUCCESS_LOGS and done_ok(pod):
        discard(end, attempt)
        logger.info("range %s attempt %s: succeeded, archive discarded "
                    "(saveSuccessLogs=false)", end, attempt)
    else:
        logger.info("range %s attempt %s: stream complete", end, attempt)
    # Last, deliberately. The monitor treats this file as "the collector will
    # write nothing further for this attempt" and only then reaps the Job --
    # which deletes the pod, the one place peaks can still be read from. It has
    # to land after .metrics or it would license exactly the reap it exists to
    # prevent. Inferring the same thing from peaks being present was wrong for
    # an attempt that legitimately has none.
    _mark_done(end, attempt)


async def _poll_once(session, pod, end, attempt, last_ts, tx):
    """One short read of a pod's log. Returns (new_last_ts, gone).

    No follow=true: the request completes and the connection is released, so
    concurrency is bounded by _poll_slots rather than by how many pods exist.
    Measured on ssc-test, a single poll takes ~0.22s from outside the cluster,
    so 2096 pods on a 10s interval need ~46 concurrent slots against the 2096
    permanently-held connections follow=true required.
    """
    params = {'container': CONTAINER, 'timestamps': 'true'}
    if last_ts:
        # Second granularity, so this overlaps on purpose; the per-line
        # comparison below removes the overlap exactly.
        params['sinceTime'] = last_ts[:19] + 'Z'
    url = f"{API}/api/v1/namespaces/{config.NAMESPACE}/pods/{pod}/log"
    async with _poll_slots:
        async with session.get(url, params=params,
                               headers={'Authorization': f'Bearer {token()}'}) as resp:
            if resp.status == 404:
                return last_ts, True
            resp.raise_for_status()
            # Chunked, not line-wise: aiohttp raises above 512 KiB on a single
            # line, and a carriage-return progress meter trivially exceeds that
            # -- one 628 MiB download arrived as a single "line". Split on \r as
            # well, and cap what a pathological blob may buffer.
            body = ''
            async for chunk in resp.content.iter_chunked(65536):
                body += chunk.decode('utf-8', 'replace')
                if len(body) > MAX_POLL_CHARS:
                    break

    return _ingest(body, end, attempt, last_ts, tx), False


def _ingest(body, end, attempt, last_ts, tx):
    """Append one block of timestamped log text to the archive; new last_ts.

    Split out of _poll_once so the doomed-pod follow stream lands its bytes
    through exactly the same path -- dedup, gzip member framing, tx scanning
    and resume-point bookkeeping. Two copies of this is how one route silently
    stops feeding TxApplyScanner while the other keeps working.
    """
    pending = None
    lines = [l for l in re.split(r'[\r\n]', body) if l]
    if not lines:
        return last_ts
    # Compressed into memory first, then appended in ONE write.
    #
    # Appending straight into the file with gzip.open(..., 'at') meant the
    # deflate buffer flushed partial output to disk repeatedly across the whole
    # loop, so for most of a large poll the archive on disk ended in a member
    # with no end-of-stream marker. job_monitor reads that same file to recover
    # txApplySeconds and gzip raises EOFError on a truncated member -- one
    # in-flight poll could abort a reconcile pass for every range. The window is
    # now a single append instead of the length of the write loop, and the file
    # only ever gains whole members.
    #
    # Costs no more memory than is already held: `body` above is the entire
    # poll uncompressed, and this is the same bytes compressed. Nothing is
    # retained between polls, which is the property that got the sidecar off
    # 1444 MiB of a 2048 MiB limit at 2096 follow streams.
    member = io.BytesIO()
    wrote = False
    with gzip.GzipFile(fileobj=member, mode='wb') as fh:
        for line in lines:
            ts, _, rest = line.partition(' ')
            if not _TS_RE.match(ts):
                # Untimestamped kubelet text. Keep it, but never let it become
                # the resume point.
                fh.write((line + '\n').encode('utf-8'))
                wrote = True
                continue
            if last_ts and ts <= last_ts:
                continue          # exact dedup of the resume overlap
            fh.write((rest + '\n').encode('utf-8'))
            wrote = True
            tx.feed(rest)
            pending = ts
    path = base(end, attempt) + '.log.gz'
    with open(path, 'ab') as out:
        # A poll whose lines were all deduped still touches the archive: its
        # existence is what job_monitor's backstop keys on.
        if wrote:
            out.write(member.getvalue())
    if pending:
        write_state(end, attempt, pending)
        return pending
    return last_ts


async def _follow_tail(session, pod, end, attempt, last_ts, tx):
    """Hold a follow=true stream on a doomed pod. Returns (last_ts, gone).

    Opened only for pods the cluster has already condemned, so this is the one
    place the cost of follow=true is worth paying: the connection is held for
    the couple of minutes between the DisruptionTarget condition and the node
    going away, not for the hours a range runs.

    Proven on ssc-test: with the stream held, SIGTERM to stellar-core yields
    `got signal 15` -> `metric 'ledger.transaction.apply'` -> `Application
    destroyed` inside 4ms, all of it captured. The same pod polled at 5s
    intervals recorded `pod gone before disruption seen`.

    Bytes are ingested as they arrive rather than at end of stream, so a node
    that disappears mid-read still leaves everything up to that point in the
    archive.
    """
    params = {'container': CONTAINER, 'timestamps': 'true', 'follow': 'true'}
    if last_ts:
        params['sinceTime'] = last_ts[:19] + 'Z'
    url = f"{API}/api/v1/namespaces/{config.NAMESPACE}/pods/{pod}/log"
    deadline = asyncio.get_event_loop().time() + DOOMED_FOLLOW_SECONDS
    buf = ''
    if _follow_slots.locked():
        # Every follow budget is spoken for, so this pod polls instead. Better
        # than queueing: the pod has ~2 minutes to live and a queued follow that
        # opens after it dies captures nothing while still holding a slot.
        logger.info("range %s: no follow slot free (%d in use), polling instead",
                    end, MAX_DOOMED_FOLLOWS)
        _doomed.pop(pod, None)
        return await _poll_once(session, pod, end, attempt, last_ts, tx)
    async with _follow_slots:
        async with session.get(url, params=params,
                               headers={'Authorization': f'Bearer {token()}'}) as resp:
            if resp.status == 404:
                return last_ts, True
            resp.raise_for_status()
            async for chunk in resp.content.iter_chunked(65536):
                buf += chunk.decode('utf-8', 'replace')
                # Flush on whole lines only: a partial trailing line has no
                # usable timestamp and must not become the resume point.
                cut = max(buf.rfind('\n'), buf.rfind('\r'))
                if cut >= 0:
                    last_ts = _ingest(buf[:cut + 1], end, attempt, last_ts, tx)
                    buf = buf[cut + 1:]
                if asyncio.get_event_loop().time() > deadline:
                    logger.info("range %s: doomed follow hit %.0fs, falling back to polling",
                                end, DOOMED_FOLLOW_SECONDS)
                    _doomed.pop(pod, None)
                    break
    if buf:
        last_ts = _ingest(buf, end, attempt, last_ts, tx)
    # One follow per pod. The stream ending means the container exited, and the
    # caller must fall back to a normal poll for the terminal check and
    # finalize; leaving the flag set would re-open a stream on a dead pod every
    # iteration. The pod-list loop does not clear it -- by then the pod is gone.
    _doomed.pop(pod, None)
    return last_ts, False


async def poll_pod(session, pod, end, attempt, done, done_ok):
    """Read one pod's log to completion, by repeated short polls.

    Replaces a follow=true stream. The stream held a connection, a gzip deflate
    buffer and aiohttp read buffers for the pod's entire life, so cost scaled
    with parallelism: measured at 2096 pods the sidecar sat at 1444 MiB of a
    2048 MiB limit with memory.events max=2617 and 1.00 of 2 cpu, which
    extrapolates past both limits at 4096. Polling makes concurrency a tuning
    parameter instead of a function of pod count.

    The one thing follow=true did better is the tail: it already held the bytes
    when a pod died. So on seeing the pod go terminal this polls once more,
    immediately, before finalizing -- without that, every spot eviction would
    lose up to one interval of exactly the log we most want.
    """
    last_ts = read_state(end, attempt)
    if last_ts is None:
        # Empty state = "claimed, nothing durable yet". job_monitor's backstop
        # skips any range with a state file, so this prevents both of us writing
        # the same log.
        write_state(end, attempt, '')
        last_ts = ''
    started = asyncio.get_event_loop().time()
    # Outside the poll loop: the medida block can straddle two polls, and a
    # fresh scanner per poll would lose the half it saw.
    tx = TxApplyScanner(recreated=bool(last_ts))
    backoff = LOG_POLL_SECONDS
    failures = 0

    first_pass = True
    while True:
        was_terminal = done(pod)
        if first_pass and was_terminal:
            # The pod was already terminal before this poller existed -- it
            # finished while the collector was down, or between pod-list polls.
            # `started` measures how long WE have been watching, which is about
            # to be zero, not how long the container ran. Measured on ssc-test
            # 2026-07-30 across two collector restarts: 150 metrics files
            # recorded a sub-5s duration alongside a >500MiB anon peak. Report
            # nothing rather than a fabricated near-zero; the monitor's own
            # figure, from the pod's terminated timestamps, is authoritative and
            # seconds_for_range prefers it anyway.
            started = None
        first_pass = False
        followed = False
        try:
            if _doomed.get(pod) and not was_terminal and DOOMED_FOLLOW_SECONDS > 0:
                followed = True
                # Condemned and still running: stop sampling and hold the
                # connection through the kill. Returns when the container exits
                # or the notice is withdrawn, and the loop re-checks terminal
                # immediately afterwards.
                last_ts, gone = await _follow_tail(
                    session, pod, end, attempt, last_ts, tx)
            else:
                last_ts, gone = await _poll_once(
                    session, pod, end, attempt, last_ts, tx)
            # Fallback interval for a condemned pod that could not follow: no
            # slot was free, or following is disabled. 1s sampling alone still
            # closes the ~9s window between the medida block and the pod object
            # being deleted, so a mass reclaim degrades rather than loses.
            backoff = (DOOMED_POLL_SECONDS if _doomed.get(pod)
                       else LOG_POLL_SECONDS)
            failures = 0
            if gone:
                logger.info("pod %s gone before/while polling range %s", pod, end)
                await finalize(session, pod, end, attempt, tx, done_ok, started)
                return
        except asyncio.CancelledError:
            raise
        except Exception as e:
            failures += 1
            logger.info("range %s poll failed (%s); retrying from %s",
                        end, e, last_ts or 'start')
            backoff = min(backoff * 2, 30)
            if was_terminal and failures >= TERMINAL_POLL_ATTEMPTS:
                # The container has exited and its log will not come back. A
                # follow=true stream finalized here because it already held the
                # bytes; polling has to decide to stop asking, or it spins on a
                # dead pod for the rest of the run and never writes its metrics.
                logger.warning("range %s attempt %s: %d failed polls after the pod "
                               "went terminal; finalizing on what was read",
                               end, attempt, failures)
                await finalize(session, pod, end, attempt, tx, done_ok, started)
                return
        else:
            if was_terminal:
                # Terminal BEFORE that poll, so the poll saw the container's
                # final output. Checking after would race a pod that exits
                # mid-poll and drop whatever it wrote on the way out.
                await finalize(session, pod, end, attempt, tx, done_ok, started)
                return
        if followed:
            # The follow only returns once the container has exited, so the
            # very next read is the one that matters. Sleeping here would hand
            # the interval back to exactly the race the follow exists to win.
            continue
        # Not a blind sleep: a pod going terminal cuts it short. Polling faster
        # would not help -- sinceTime has second granularity, so anything under
        # ~1s re-reads the same second -- and the delay that matters is between
        # the container exiting and the last read, not between routine polls.
        ev = _wake.setdefault(pod, asyncio.Event())
        try:
            await asyncio.wait_for(ev.wait(), timeout=backoff)
        except asyncio.TimeoutError:
            pass
        finally:
            # Standard set/clear pairing. Left set, the Event makes every later
            # wait return instantly, so the terminal-poll backoff never sleeps
            # and TERMINAL_POLL_ATTEMPTS is spent in one millisecond -- the pod
            # is given no time to have its final log become readable. A wake is
            # consumed by the poll it triggers.
            ev.clear()


async def list_pods(session):
    url = f"{API}/api/v1/namespaces/{config.NAMESPACE}/pods"
    params = {'labelSelector': f"{config.LABEL_RUN}={config.RUN_NAME}"}
    async with session.get(url, params=params,
                           headers={'Authorization': f'Bearer {token()}'}) as resp:
        resp.raise_for_status()
        return (await resp.json()).get('items', [])


def ensure_stream(name, end, attempt, phase):
    """Open this pod's poller if it has none. Idempotent; returns whether it did.

    Called by the watch as a pod appears and again if it is condemned, and by the
    pod-list loop as a backstop for events the watch drops across a reconnect.
    Opening a stream is time-critical -- a condemned pod is gone a second after
    stellar-core exits -- so it must not be reachable only from a poll cycle.
    Measured on the 900-worker run before this existed: the loop's cycle stretched
    to 925s behind a serial kubelet sweep, and five -a2 legs lived and died with
    no reader at all, one of them for 184.7s, losing txApply for good.
    """
    if name in _tasks or name in _streamed or not _stream_ctx:
        return False
    if phase not in POLLABLE_PHASES:
        # Allowlist, not "skip Pending". A container that has not started answers
        # 400 "waiting to start", and Unknown means the node stopped reporting.
        # Both are retried on the cycle they become pollable.
        return False
    ctx = _stream_ctx
    _register_stream(name, end, attempt)
    _tasks[name] = asyncio.create_task(
        poll_pod(ctx['session'], name, end, attempt,
                 lambda p: ctx['terminal'].get(p, False),
                 lambda p: ctx['succeeded'].get(p, False)))
    logger.info("opened stream for range %s attempt %s (%d active)",
                end, attempt, len(_tasks))
    return True


async def watch_condemnations(session):
    """Watch the run's pods and flag condemnations the moment they are written.

    Runs beside the pod-list loop rather than replacing it: the list still owns
    discovery, task bookkeeping and finalize. This only ever sets _doomed
    earlier than the list would have, which is the difference between opening a
    follow while stellar-core is still running and opening it on a 404.

    Cheaper than the sweep it front-runs, too. list_pods re-serialises every pod
    in the run every POLL_SECONDS; a watch is one connection served from the
    apiserver's cache that sends only deltas.

    Never fatal. Any failure falls back to the list sweep, which is exactly the
    behaviour that existed before this function.
    """
    url = f"{API}/api/v1/namespaces/{config.NAMESPACE}/pods"
    rv = None
    while True:
        params = {'labelSelector': f"{config.LABEL_RUN}={config.RUN_NAME}", 'watch': 'true',
                  'allowWatchBookmarks': 'true',
                  'timeoutSeconds': str(WATCH_TIMEOUT_SECONDS)}
        if rv:
            params['resourceVersion'] = rv
        try:
            async with session.get(url, params=params,
                                   headers={'Authorization': f'Bearer {token()}'}) as resp:
                if resp.status == 410:
                    # Our resourceVersion aged out of the apiserver's history.
                    # Restarting without one re-syncs; the list sweep covers the
                    # gap in the meantime.
                    rv = None
                    continue
                resp.raise_for_status()
                async for raw in resp.content:
                    if not raw.strip():
                        continue
                    try:
                        ev = json.loads(raw)
                    except ValueError:
                        continue
                    obj = ev.get('object') or {}
                    meta = obj.get('metadata') or {}
                    # Track on every event, bookmarks included -- that is what
                    # they are for -- so a reconnect resumes instead of re-syncing.
                    rv = meta.get('resourceVersion') or rv
                    if ev.get('type') == 'ERROR':
                        if obj.get('code') == 410:
                            rv = None
                        break
                    if ev.get('type') not in ('ADDED', 'MODIFIED'):
                        continue
                    labels = meta.get('labels') or {}
                    end = labels.get(config.LABEL_RANGE)
                    if end is None:
                        continue
                    name = meta.get('name')
                    attempt = labels.get(config.LABEL_ATTEMPT, '1')
                    # Before the condemnation check: a pod condemned in the same
                    # event it first becomes pollable needs the poller to exist
                    # first, or there is nothing for _mark_condemned to wake.
                    ensure_stream(name, end, attempt,
                                  (obj.get('status') or {}).get('phase'))
                    _mark_condemned(obj, name, end, attempt)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            logger.warning("condemnation watch dropped (%s); retrying", exc)
            await asyncio.sleep(WATCH_RETRY_SECONDS)


async def main():
    os.makedirs(config.LOG_DIR, exist_ok=True)
    # Connection-pool limit, not a task limit: there is no semaphore above it,
    # so a stream that cannot get a connection blocks here for as long as the
    # pool stays full -- and every holder is a follow=true stream open for the
    # life of its pod. Below the live pod count this does not degrade, it
    # starves, and it starves the pods created last, which are the retries.
    # Sized for concurrent polls plus headroom for the pod-list and kubelet
    # calls, not for one connection per pod. Under follow=true this had to
    # exceed parallelism or pods silently starved -- 1200 against 2048 workers
    # left 896 blocked forever, and retries, created last, never got a slot.
    conn = aiohttp.TCPConnector(
        limit=MAX_CONCURRENT_POLLS + MAX_DOOMED_FOLLOWS + 64, ssl=ssl_ctx())
    # No total timeout: these streams are meant to stay open for the life of a
    # range, which can be hours.
    timeout = aiohttp.ClientTimeout(total=None, sock_connect=10)
    # tasks/streamed are the module-level registry under local names, so the
    # bookkeeping below is unchanged while the watch shares the same guard.
    tasks, streamed = _tasks, _streamed
    # Cleared rather than assumed empty: a second main() in one process would
    # otherwise find every pod already registered and open no streams at all.
    tasks.clear()
    streamed.clear()
    _stream_ctx.clear()
    terminal, succeeded, vanished = {}, {}, {}
    # `streamed` holds streams that ran to completion. Without it a finished task
    # is deleted from `tasks` and the next poll re-opens the stream, forever: one
    # full log re-read per pod every POLL_SECONDS, which at 1024 workers is a lot
    # of apiserver -- measured, the completion block ran once per range per cycle
    # for the rest of the run.

    async with aiohttp.ClientSession(connector=conn, timeout=timeout) as session:
        logger.info("streaming logs for run=%s into %s", config.RUN_NAME, config.LOG_DIR)
        # Published before the watch starts: ensure_stream is a no-op until this
        # exists, so a watch event arriving first would silently open nothing.
        _stream_ctx.update(session=session, terminal=terminal, succeeded=succeeded)
        if WATCH_TIMEOUT_SECONDS > 0:
            asyncio.create_task(watch_condemnations(session))
        while True:
            try:
                pods = await list_pods(session)
                live = {p['metadata']['name'] for p in pods}
                # A pod can leave the list without ever being observed terminal:
                # Karpenter reaps the node, the kubelet evicts it, or the monitor
                # deletes its finished Job. `terminal` is only written for pods
                # in this list, so those would keep done() False forever and
                # their stream would retry until the run ended. Marking them
                # terminal lets the stream finalize on its own and free the slot;
                # cancelling is the backstop for one wedged inside a connection
                # attempt it will never win.
                for name in [n for n in tasks if n not in live]:
                    terminal[name] = True
                    if name in _wake:
                        # Gone is terminal. Without this its poller sleeps out
                        # the interval before taking the 404, delaying finalize
                        # and the .done that lets the monitor reap the Job.
                        _wake[name].set()
                    t = tasks[name]
                    if t.done():
                        del tasks[name]
                        streamed.add(name)
                        continue
                    vanished[name] = vanished.get(name, 0) + 1
                    if vanished[name] >= VANISHED_GRACE_CYCLES:
                        t.cancel()
                        try:
                            await t
                        except asyncio.CancelledError:
                            pass
                        del tasks[name]
                        vanished.pop(name, None)
                        ref = _streaming.get(name)
                        if ref is not None:
                            # The poller was wedged, but its archive and the
                            # sampler's process-local peaks still contain useful
                            # truth. Finalize them before licensing a reap.
                            await finalize(
                                session, name, ref[0], ref[1],
                                TxApplyScanner(recreated=True),
                                lambda p: succeeded.get(p, False))
                        streamed.add(name)
                        logger.info("cancelled and finalized stream for vanished pod %s",
                                    name)
                for pod in pods:
                    name = pod['metadata']['name']
                    labels = pod['metadata'].get('labels', {})
                    end = labels.get(config.LABEL_RANGE)
                    if end is None:
                        continue
                    phase = pod.get('status', {}).get('phase')
                    terminal[name] = phase in ('Succeeded', 'Failed')
                    # NOT gated on phase. A pod that is being DELETED keeps
                    # phase Running until its object disappears -- deletion
                    # never sets Succeeded or Failed -- so gating this on
                    # terminal meant no disrupted pod ever recorded an exact
                    # duration, and every one of them fell back to the poller's
                    # own clock. Measured on ssc-test: 268s reported against a
                    # ~500s attempt, because that clock starts when the POLLER
                    # opened, not when the container did. The container's
                    # terminated.finishedAt is present for the ~8s the object
                    # outlives it, and pod_seconds returns None until then, so
                    # asking every cycle is self-guarding.
                    secs = pod_seconds(pod)
                    if secs is not None:
                        _pod_secs[name] = secs
                    start = (pod.get('status') or {}).get('startTime')
                    if start:
                        # Second line: if the object is deleted before any cycle
                        # catches its terminated timestamp, finalize can still
                        # date the attempt from when the container STARTED
                        # rather than from when this poller happened to open.
                        _pod_start[name] = start
                    # Backstop only: the watch normally gets here first. This
                    # still runs so detection survives the watch being disabled
                    # or reconnecting.
                    if not terminal[name]:
                        _mark_condemned(pod, name, end,
                                        labels.get(config.LABEL_ATTEMPT, '1'))
                    if terminal[name] and name in _wake:
                        # Wake its poller now rather than at the next tick.
                        _wake[name].set()
                    succeeded[name] = phase == 'Succeeded'
                    if phase == 'Failed':
                        record_outcome(pod, end, labels.get(config.LABEL_ATTEMPT, '1'))
                    if name in tasks and not tasks[name].done():
                        continue
                    if name in tasks and tasks[name].done():
                        del tasks[name]
                        # Only bar a re-open once the pod itself is terminal. A
                        # task that ended while the pod is still running died
                        # early, and re-opening is the recovery path.
                        if terminal.get(name):
                            streamed.add(name)
                        continue
                    if name in streamed:
                        continue
                    # Backstop. The watch normally opens this the moment the pod
                    # appears; this covers events dropped across a reconnect.
                    # Same registry and the same guard, so whichever gets there
                    # first wins and the other no-ops -- two readers on one pod
                    # would duplicate the archive and race write_state.
                    ensure_stream(name, end, labels.get(config.LABEL_ATTEMPT, '1'), phase)

                # AFTER the per-pod branches, never before them. This is a serial
                # sweep of every node's kubelet, and on spot a dead one costs the
                # 10s connect timeout apiece -- measured, that stretched one cycle
                # to 925s. Ahead of the branches it delayed every stream by that
                # much; behind them it delays only the next cycle's sampling.
                # It must stay outside the `for` loop, though: those branches end
                # in `continue` for a pod already streaming, so a sampler placed
                # among them fires only on the cycle a stream opens, when the
                # range has barely written anything.
                #
                # Unconditional: this used to be gated on ephemeral mode, back
                # when it only sampled disk. Memory is sized in both modes, so
                # gating it here left every pvc run with no anon peak at all.
                # hostIP, not nodeName: the sampler talks to the kubelet
                # directly, and this list already carries the address, so it
                # costs no read of Node objects.
                await sample_kubelet(session, {
                    p['status']['hostIP'] for p in pods
                    if p.get('status', {}).get('hostIP')
                    and p.get('status', {}).get('phase') == 'Running'})
            except Exception as e:
                logger.warning("pod list failed: %s", e)
            await asyncio.sleep(POLL_SECONDS)


if __name__ == '__main__':
    asyncio.run(main())
