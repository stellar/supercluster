"""Streaming log collector for parallel catchup.

Runs as a sidecar next to job_monitor, sharing its /logs volume.

Why stream rather than read logs after a Job finishes: worker pods are one per
ledger range, and Karpenter deletes the node roughly a minute after its last
running pod exits, taking every pod object with it. Anything that reads after
the fact is racing that deletion. Holding `follow=true` from pod start means we
already have everything the pod wrote by the time it disappears -- and it makes
a straggler's log readable *while* it is stuck, which is the case that turns a
5h run into a 10h one.

Resume is idempotent across both a dropped stream and a restart of this
process:

  coarse   reconnect with sinceTime=<last durably written timestamp>; the API
           only accepts second granularity, so this deliberately overlaps
  precise  every line carries a kubelet RFC3339Nano timestamp (timestamps=true),
           so drop any line <= last_ts. That removes the overlap exactly and
           does not depend on stellar-core's own log format.

Residual: if this dies between flushing log bytes and rewriting the state file,
the next run replays from a slightly older timestamp and a few lines duplicate.
Bounded by STATE_FLUSH_SECONDS. "At least once, deduped to near-exact" rather
than exactly once.
"""

import asyncio
import gzip
import json
import logging
import os
import re
import ssl
import sys
from datetime import datetime

import aiohttp

NAMESPACE = os.getenv('NAMESPACE', 'default')
RUN_NAME = os.getenv('RUN_NAME', 'parallel-catchup')
LOG_DIR = os.getenv('LOG_DIR', '/logs')
CONTAINER = os.getenv('WORKER_CONTAINER', 'stellar-core')
POLL_SECONDS = float(os.getenv('COLLECTOR_POLL_SECONDS', 5))
STATE_FLUSH_SECONDS = float(os.getenv('STATE_FLUSH_SECONDS', 10))
# Poll cycles a stream gets to finalize itself after its pod leaves the pod list
# before it is cancelled outright. One cycle is usually enough; the margin is for
# a stream still finalizing: writing its .metrics and closing its archive.
VANISHED_GRACE_CYCLES = int(os.getenv('COLLECTOR_VANISHED_GRACE_CYCLES', 3))
# Whether to keep the archive for a range that succeeded. Enforced here rather
# than in job_monitor: we cannot know in advance whether a range will fail, so
# the stream always runs and the archive is discarded on success instead.
SAVE_SUCCESS_LOGS = os.getenv('SAVE_SUCCESS_LOGS', 'true').lower() == 'true'
# Peak working set per range, for sizing a later run's requests. Empty disables.
# Queried rather than sampled: cgroup memory.peak counts page cache (measured:
# 1.5GB peak for a process using 0.3MB of anon), and a sampler inside the worker
# would mean dropping the `exec`, which is what keeps stellar-core at PID 1 and
# able to see SIGTERM.
STORAGE_MODE = os.getenv('STORAGE_MODE', 'pvc')
# Peak memory now comes from kubelet, not Prometheus. kubelet reports rssBytes
# and workingSetBytes per container in the same /stats/summary payload this
# already fetches for ephemeral storage, at ~10s cAdvisor housekeeping against a
# 30s scrape -- and without depending on Prometheus being up, being reachable,
# or still retaining the window. cpu is not sampled at all: the request is fixed
# at REQ_CPU, so a measured value has nothing to size.
# Peaks are held per pod and flushed on significant growth, so a restart loses
# at most PEAK_FLUSH_RATIO of a range's high-water rather than all of it --
# Prometheus's server-side max_over_time needed no such state.
PEAK_FLUSH_RATIO = float(os.getenv('PEAK_FLUSH_RATIO', 1.05))
# Most a single unterminated blob may buffer before we start discarding its
# head. stellar-core's own lines are well under a kilobyte; anything larger is a
# progress meter or a stack dump, and neither is worth killing the stream over.
#
# This is charged PER LIVE STREAM. Measured on ssc-test at 2096 follow streams
# the collector already sat at 1444 MiB of a 2048 MiB limit with memory.events
# max=2617, so a 256 KiB worst case here is another 512 MiB at 2096 and 1 GiB at
# 4096 -- on its own enough to OOM the sidecar. 64 KiB is ~64x the longest line
# stellar-core actually emits.
MAX_LINE_CHARS = int(os.getenv('MAX_LINE_CHARS', 65536))
# Seconds between polls of one pod's log. Latency here is archive lag, not
# anything a decision waits on; 4096 pods at 10s is ~90 concurrent polls.
LOG_POLL_SECONDS = float(os.getenv('LOG_POLL_SECONDS', 10))
# Concurrent in-flight log polls, across all pods. This is the whole point of
# polling: it is independent of how many pods exist, where follow=true needed
# one held connection per pod forever.
MAX_CONCURRENT_POLLS = int(os.getenv('MAX_CONCURRENT_POLLS', 96))
# Most one poll may read before it stops. A pod that has been unwatched for a
# while has a large backlog; this bounds a single response, and the next poll
# picks up from the timestamp this one reached.
MAX_POLL_CHARS = int(os.getenv('MAX_POLL_CHARS', 8388608))
# Bounds in-flight polls across every pod. Lives beside its own constant rather
# than among the peak dicts, where it landed inside the region the scanner tests
# exec and broke six of them on an asyncio NameError.
_poll_slots = asyncio.Semaphore(MAX_CONCURRENT_POLLS)
# pod name -> Event, set by the main loop the moment it first observes the pod
# terminal. poll_pod waits on it instead of sleeping blind, so the final read
# happens within the pod-list cadence rather than up to LOG_POLL_SECONDS later.
# That window is the only thing standing between a spot reclaim and the last
# lines the container wrote.
_wake = {}
# pod name -> its own start->finish, read off the pod while it still exists.
_pod_secs = {}
# Fields that only ever grow. write_metrics maxes these instead of overwriting,
# so a restarted poller starting its high-water at zero cannot lower one.
PEAK_KEYS = ('peakAnonBytes', 'peakWorkingSetBytes', 'peakEphemeralBytes')
# Failed polls tolerated after a pod goes terminal before we stop asking. Its
# log is not coming back, and spinning on it holds a task and a poll slot for
# the rest of the run; a couple of retries still absorb a transient 500.
TERMINAL_POLL_ATTEMPTS = int(os.getenv('TERMINAL_POLL_ATTEMPTS', 3))
# Phases whose log endpoint can actually answer. Pending has no container yet
# and Unknown means the node stopped reporting; the terminal phases are kept
# because that is where a pod's final output lives.
POLLABLE_PHASES = ('Running', 'Succeeded', 'Failed')

LABEL_RUN = 'catchup.stellar.org/run'
LABEL_RANGE = 'catchup.stellar.org/range-end'
LABEL_ATTEMPT = 'catchup.stellar.org/attempt'

SA = '/var/run/secrets/kubernetes.io/serviceaccount'
API = f"https://{os.getenv('KUBERNETES_SERVICE_HOST', 'kubernetes.default')}:{os.getenv('KUBERNETES_SERVICE_PORT', '443')}"

logging.basicConfig(level=os.getenv('LOGGING_LEVEL', 'INFO'),
                    format='%(asctime)s - %(levelname)s - %(message)s',
                    handlers=[logging.StreamHandler(sys.stdout)])
logger = logging.getLogger('log-collector')


def token():
    # Projected service account tokens rotate, so this is re-read per request
    # rather than cached at startup.
    with open(os.path.join(SA, 'token')) as fh:
        return fh.read().strip()


def ssl_ctx():
    return ssl.create_default_context(cafile=os.path.join(SA, 'ca.crt'))


def base(end, attempt):
    return os.path.join(LOG_DIR, f"range-{end}-a{attempt}")


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
    tmp = path + '.tmp'
    try:
        with open(tmp, 'w') as fh:
            fh.write(ts)
        os.replace(tmp, path)
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
# medida prints the sum in scientific notation once it exceeds 1e6 ms, which is
# every range that applies a real transaction load. The old [0-9.]+ pattern
# matched "1.30722" then demanded "ms" and hit "e+06ms" instead, so tx_apply was
# silently missing for 25% of ranges -- 91-99% of everything above ledger 35M,
# exactly the expensive end.
_SUM_RE = re.compile(r"sum\s*=\s*([0-9.]+(?:[eE][+-]?[0-9]+)?)ms")


class TxApplyScanner:
    """Pull the medida tx-apply total out of the stream as it goes past.

    stellar-core prints this block once, just before exit. Scanning here rather
    than re-reading the log later is what makes the metric independent of pod
    lifetime: by the time job_monitor sees the Job succeed, Karpenter may
    already have reaped the node, and with saveSuccessLogs=false the archive is
    gone too. These bytes pass through this process exactly once, so this is the
    only place guaranteed to see them.
    """

    WINDOW = 15          # same span job_monitor uses when reading an archive

    # Printed by RESUME_SCRIPT before stellar-core starts. Its counterpart,
    # "RESUME DECLINED", means new-db ran and this attempt did the whole range,
    # so the colon is load-bearing -- it is what separates the two.
    RESUME_MARK = 'RESUME: '

    def __init__(self):
        self.seconds = None
        self.resumed = False
        self._left = 0

    def feed(self, line):
        if self.RESUME_MARK in line:
            self.resumed = True
        if _TX_METRIC in line:
            self._left = self.WINDOW
            return
        if self._left <= 0:
            return
        self._left -= 1
        m = _SUM_RE.search(line)
        if m:
            self.seconds = float(m.group(1)) / 1000.0
            self._left = 0








def write_metrics(end, attempt, values):
    """Persist per-range measurements for job_monitor's reconcile to read.

    Kept out of .outcome on purpose: that file answers "why did this attempt
    fail" and is only written for failed pods, whereas these are only
    meaningful for one that succeeded.
    """
    path = base(end, attempt) + '.metrics'
    tmp = path + '.tmp'
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
    for k in PEAK_KEYS:
        a, b = prior.get(k), values.get(k)
        if a is not None and b is not None:
            merged[k] = max(a, b)
    values = merged
    try:
        with open(tmp, 'w') as fh:
            json.dump(values, fh)
        os.replace(tmp, path)
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
    for cond in status.get('conditions', []):
        if cond.get('type') == 'DisruptionTarget' and cond.get('status') == 'True':
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
    return {'outcome': 'failed', 'exitCode': None}


def record_outcome(pod, end, attempt):
    """Write the verdict next to the log, for job_monitor's reconcile to read."""
    path = base(end, attempt) + '.outcome'
    if os.path.exists(path):
        return
    data = classify(pod)
    data['pod'] = pod['metadata']['name']
    try:
        tmp = path + '.tmp'
        with open(tmp, 'w') as fh:
            json.dump(data, fh)
        os.replace(tmp, path)
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
_peak_flushed = {}
# pod name -> (end, attempt), so a mid-flight peak flush can find its file.
_streaming = {}



async def sample_kubelet(session, nodes):
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
    for node in nodes:
        url = f"{API}/api/v1/nodes/{node}/proxy/stats/summary"
        try:
            async with session.get(url, headers={'Authorization': f'Bearer {token()}'}) as resp:
                resp.raise_for_status()
                summary = await resp.json()
        except Exception as e:
            # Not debug: if this fails the ephemeral axis is silently empty and
            # the profile looks merely "absent" rather than broken.
            logger.warning("kubelet stats unavailable on %s: %s", node, e)
            continue
        for entry in summary.get('pods', []):
            name = entry.get('podRef', {}).get('name')
            if not name:
                continue
            used = (entry.get('ephemeral-storage') or {}).get('usedBytes')
            if used is not None and STORAGE_MODE == 'ephemeral':
                prev = _eph_peak.get(name, 0)
                if int(used) > prev:
                    _eph_peak[name] = int(used)
                    logger.info("peak ephemeral for %s: %.2f GiB", name, used / 1073741824)
            for c in entry.get('containers', []):
                if c.get('name') != CONTAINER:
                    continue
                # Absent for the first seconds of a container's life, before
                # cAdvisor has stats for it. Every later poll carries it, so a
                # miss here costs nothing: anon is at its lowest during startup.
                mem = c.get('memory') or {}
                ws = mem.get('workingSetBytes')
                if ws is not None and int(ws) > _ws_peak.get(name, 0):
                    _ws_peak[name] = int(ws)
                rss = mem.get('rssBytes')
                if rss is None:
                    continue
                if int(rss) <= _anon_peak.get(name, 0):
                    continue
                _anon_peak[name] = int(rss)
                # Held in memory until the stream ends, so a collector restart
                # would otherwise reset a range's high-water to whatever it is
                # using at that moment -- under-reporting, which sizes the next
                # run too small. Flushing only on PEAK_FLUSH_RATIO growth keeps
                # this to a handful of writes over a pod's life instead of one
                # per sample per pod.
                if int(rss) >= _peak_flushed.get(name, 0) * PEAK_FLUSH_RATIO:
                    _peak_flushed[name] = int(rss)
                    ref = _streaming.get(name)
                    if ref:
                        write_metrics(ref[0], ref[1], {'peakAnonBytes': int(rss)})


def _mark_done(end, attempt):
    path = done_path(end, attempt)
    tmp = path + '.tmp'
    try:
        with open(tmp, 'w') as fh:
            fh.write('')
        os.replace(tmp, path)
    except OSError as e:
        # Costs a Job that waits out JOB_TTL_SECONDS, never correctness.
        logger.warning("could not mark range %s attempt %s done: %s", end, attempt, e)


async def finalize(session, pod, end, attempt, tx, done_ok, started=None):
    """Persist everything this attempt owes, then let its stream go.

    Reached from two places: a clean end of stream once the pod is terminal,
    and a 404 once the pod object is gone. The second path used to not exist,
    so a pod deleted while Running -- reaped node, eviction, or the monitor
    deleting a finished Job -- left its stream retrying every 30s for the rest
    of the run, holding a connection slot the whole time.
    """
    # Before discard: on success the archive is about to be deleted.
    measured = {}
    observed = _pod_secs.pop(pod, None)
    if observed is not None:
        # The pod's own timestamps, not how long this poller happened to watch.
        measured['attemptSeconds'] = round(observed, 1)
    elif started is not None:
        # Fallback only: the monitor's figure comes from the pod's terminated
        # timestamps and is preferred when it exists.
        measured['attemptSeconds'] = round(
            asyncio.get_event_loop().time() - started, 1)
    if tx.resumed:
        # Not a peak -- PEAK_FIELDS filters it out of the profile. peaks_for_range
        # reads it to decide how far back to aggregate: a resumed attempt only
        # measured the tail of its range, so the attempt before it still counts.
        measured['resumed'] = True
    if tx.seconds is not None:
        measured['txApplySeconds'] = tx.seconds
    _peak_flushed.pop(pod, None)
    _streaming.pop(pod, None)
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
    if not SAVE_SUCCESS_LOGS and done_ok(pod):
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
    url = f"{API}/api/v1/namespaces/{NAMESPACE}/pods/{pod}/log"
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

    pending = None
    lines = [l for l in re.split(r'[\r\n]', body) if l]
    if not lines:
        return last_ts, False
    # Opened per poll, not held for the pod's life. A live gzip deflate buffer
    # per stream is what put the sidecar at 1444 MiB of a 2048 MiB limit at 2096
    # follow streams; here nothing is retained between polls.
    with gzip.open(base(end, attempt) + '.log.gz', 'at') as fh:
        for line in lines:
            ts, _, rest = line.partition(' ')
            if not _TS_RE.match(ts):
                # Untimestamped kubelet text. Keep it, but never let it become
                # the resume point.
                fh.write(line + '\n')
                continue
            if last_ts and ts <= last_ts:
                continue          # exact dedup of the resume overlap
            fh.write(rest + '\n')
            tx.feed(rest)
            pending = ts
    if pending:
        write_state(end, attempt, pending)
        return pending, False
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
    tx = TxApplyScanner()
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
        try:
            last_ts, gone = await _poll_once(session, pod, end, attempt, last_ts, tx)
            backoff = LOG_POLL_SECONDS
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
        # Not a blind sleep: a pod going terminal cuts it short. Polling faster
        # would not help -- sinceTime has second granularity, so anything under
        # ~1s re-reads the same second -- and the delay that matters is between
        # the container exiting and the last read, not between routine polls.
        try:
            await asyncio.wait_for(_wake.setdefault(pod, asyncio.Event()).wait(),
                                   timeout=backoff)
        except asyncio.TimeoutError:
            pass


async def list_pods(session):
    url = f"{API}/api/v1/namespaces/{NAMESPACE}/pods"
    params = {'labelSelector': f"{LABEL_RUN}={RUN_NAME}"}
    async with session.get(url, params=params,
                           headers={'Authorization': f'Bearer {token()}'}) as resp:
        resp.raise_for_status()
        return (await resp.json()).get('items', [])


async def main():
    os.makedirs(LOG_DIR, exist_ok=True)
    # Connection-pool limit, not a task limit: there is no semaphore above it,
    # so a stream that cannot get a connection blocks here for as long as the
    # pool stays full -- and every holder is a follow=true stream open for the
    # life of its pod. Below the live pod count this does not degrade, it
    # starves, and it starves the pods created last, which are the retries.
    # Sized for concurrent polls plus headroom for the pod-list and kubelet
    # calls, not for one connection per pod. Under follow=true this had to
    # exceed parallelism or pods silently starved -- 1200 against 2048 workers
    # left 896 blocked forever, and retries, created last, never got a slot.
    conn = aiohttp.TCPConnector(limit=MAX_CONCURRENT_POLLS + 64, ssl=ssl_ctx())
    # No total timeout: these streams are meant to stay open for the life of a
    # range, which can be hours.
    timeout = aiohttp.ClientTimeout(total=None, sock_connect=10)
    tasks, terminal, succeeded, vanished = {}, {}, {}, {}
    # Streams that ran to completion. Without this a finished task is deleted
    # from `tasks` and the next poll re-opens the stream, forever: one full log
    # re-read per pod per cycle, which at 1024 workers is a lot of apiserver.
    streamed = set()

    async with aiohttp.ClientSession(connector=conn, timeout=timeout) as session:
        logger.info("streaming logs for run=%s into %s", RUN_NAME, LOG_DIR)
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
                        del tasks[name]
                        vanished.pop(name, None)
                        streamed.add(name)
                        logger.info("cancelled stream for vanished pod %s", name)
                # Unconditional: this used to be gated on ephemeral mode, back
                # when it only sampled disk. Memory is sized in both modes, so
                # gating it here left every pvc run with no anon peak at all.
                if True:
                    # Once per cycle, before the per-pod branches below: those
                    # end in `continue` for every pod already being streamed, so
                    # anything after them runs only on the cycle a stream opens
                    # -- when the range has barely written anything yet.
                    await sample_kubelet(session, {
                        p['spec']['nodeName'] for p in pods
                        if p.get('spec', {}).get('nodeName')
                        and p.get('status', {}).get('phase') == 'Running'})
                for pod in pods:
                    name = pod['metadata']['name']
                    labels = pod['metadata'].get('labels', {})
                    end = labels.get(LABEL_RANGE)
                    if end is None:
                        continue
                    phase = pod.get('status', {}).get('phase')
                    terminal[name] = phase in ('Succeeded', 'Failed')
                    if terminal[name]:
                        # Recorded while the pod object still exists. Beats the
                        # poller's own elapsed time, which only measures how long
                        # WE watched -- ~0 for a pod that finished before this
                        # poller started.
                        secs = pod_seconds(pod)
                        if secs is not None:
                            _pod_secs[name] = secs
                    if terminal[name] and name in _wake:
                        # Wake its poller now rather than at the next tick.
                        _wake[name].set()
                    succeeded[name] = phase == 'Succeeded'
                    if phase == 'Failed':
                        record_outcome(pod, end, labels.get(LABEL_ATTEMPT, '1'))
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
                    attempt = labels.get(LABEL_ATTEMPT, '1')
                    if phase not in POLLABLE_PHASES:
                        # Allowlist, not "skip Pending". A container that has not
                        # started answers 400 "waiting to start" -- 60 of 88 poll
                        # failures right after the polling switch -- and Unknown
                        # means the node stopped reporting, so that poll cannot
                        # succeed either. Both are picked up on the cycle they
                        # become pollable. Succeeded and Failed stay in: a
                        # terminal pod is where the final output lives.
                        continue
                    _streaming[name] = (end, attempt)
                    tasks[name] = asyncio.create_task(
                        poll_pod(session, name, end, attempt,
                                   lambda p: terminal.get(p, False),
                                   lambda p: succeeded.get(p, False)))
                    logger.info("opened stream for range %s attempt %s (%d active)",
                                end, attempt, len(tasks))
            except Exception as e:
                logger.warning("pod list failed: %s", e)
            await asyncio.sleep(POLL_SECONDS)


if __name__ == '__main__':
    asyncio.run(main())
