"""Streaming log collector for parallel catchup.

Runs as a sidecar next to job_monitor, sharing its /logs volume. Polls each
pod's log rather than reading after the Job finishes: Karpenter deletes the node
about a minute after its last pod exits, taking every pod object with it, and
polling also keeps a straggler readable *while* it is stuck. A condemned pod
gets a follow stream so its last lines land before it goes.

Resume is idempotent across a dropped stream and a restart of this process:
reconnect with sinceTime=<last durably written timestamp>, which has second
granularity and so overlaps on purpose, then drop any line whose own kubelet
RFC3339Nano timestamp is <= last_ts. Residual: dying between flushing bytes and
rewriting the state file replays one poll's worth of lines, so this is at least
once, deduped to near-exact.
"""

import asyncio
import gzip
import io
import json
import logging
import os
import re
import sys
from datetime import datetime

import aiohttp
from logger import build_logger
import collector_config as cc
import kube_http
import tx_scan
import verdicts
import config
import records

# Every piece of live runtime state this process holds, and the only module-level
# names here that are not settings: two semaphores, which carry their own waiter
# queues, and the dicts recording what is known about the pods being watched.
# Everything tunable lives in collector_config.

# Bounds in-flight polls across every pod.
_poll_slots = asyncio.Semaphore(cc.MAX_CONCURRENT_POLLS)
# Separate from _poll_slots on purpose -- see MAX_DOOMED_FOLLOWS.
_follow_slots = asyncio.Semaphore(cc.MAX_DOOMED_FOLLOWS)
# pod name -> Event, set when the pod is first seen terminal, gone or condemned;
# poll_pod waits on it rather than sleeping, so LOG_POLL_SECONDS is a ceiling and
# the final read is not left until after it.
_wake = {}
# pod name -> its own start->finish, read off the pod while it still exists.
_pod_secs = {}
# pod name -> status.startTime, so an attempt whose object vanished before any
# cycle saw its terminated timestamp can still be dated from the container's own
# start rather than from whenever this poller happened to open.
_pod_start = {}
# Pods carrying a DisruptionTarget condition: the cluster has committed to
# destroying them and, on spot, gives about two minutes' notice. Waking the
# poller is not enough on its own -- stellar-core prints its medida block ~4ms
# after SIGTERM and the object is deleted seconds later, so an interval poll
# straddles the whole thing (2048-worker run: 810 evictions lost 809 txApply
# values), where a held connection already has those bytes. Safe because it is
# scoped to the drain, not because the count is small: global follow=true cost
# the sidecar 1444 MiB of a 2048 MiB limit at 2096 whole-range streams.
_doomed = {}

# Peak ephemeral disk, for sizing a later run's ephemeral-storage request. Only
# meaningful in ephemeral mode, and sampled for every pod but only kept for
# ranges that finished -- what invalidates a sample is being cut short, not the
# capacity type. Prometheus cannot answer it (cAdvisor reports fs usage per node,
# with no pod label), so this samples kubelet directly and keeps a running max.
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
# pod appears instead of waiting for the pod-list loop. One registry with one
# guard is the whole point: read_state is consulted once, at poll_pod start, so
# two creators would each hold their own in-memory last_ts, re-append the same
# lines and race each other's write_state.
_tasks = {}
_streamed = set()
# session + the terminal/succeeded views poll_pod closes over, published once by
# main() so ensure_stream can be called from outside it.
_stream_ctx = {}


logger = build_logger('log_collector', name='log-collector', to_file=False)


async def main():
    os.makedirs(config.LOG_DIR, exist_ok=True)
    # Connection-pool limit, not a task limit: there is no semaphore above it,
    # so a stream that cannot get a connection blocks here until the pool drains
    # -- it does not degrade, it starves, and it starves the pods created last,
    # which are the retries. Sized for concurrent polls plus headroom for the
    # pod-list and kubelet calls, not one connection per pod: under follow=true
    # a 1200 limit against 2048 workers left 896 blocked forever.
    conn = aiohttp.TCPConnector(
        limit=cc.MAX_CONCURRENT_POLLS + cc.MAX_DOOMED_FOLLOWS + 64, ssl=kube_http.ssl_ctx())
    # No total timeout: these streams are meant to stay open for the life of a
    # range, which can be hours.
    timeout = aiohttp.ClientTimeout(total=None, sock_connect=10)
    # The module-level registry under local names, so the watch shares the same
    # guard as the bookkeeping below.
    tasks, streamed = _tasks, _streamed
    # Cleared rather than assumed empty: a second main() in one process would
    # otherwise find every pod already registered and open no streams at all.
    tasks.clear()
    streamed.clear()
    _stream_ctx.clear()
    terminal, succeeded, vanished = {}, {}, {}
    # `streamed` holds streams that ran to completion. Without it a finished
    # task is deleted from `tasks` and the next poll re-opens the stream,
    # forever: one full log re-read per pod every POLL_SECONDS.

    async with aiohttp.ClientSession(connector=conn, timeout=timeout) as session:
        logger.info("streaming logs for run=%s into %s", config.RUN_NAME, config.LOG_DIR)
        # Published before the watch starts: ensure_stream is a no-op until this
        # exists, so a watch event arriving first would silently open nothing.
        _stream_ctx.update(session=session, terminal=terminal, succeeded=succeeded)
        if cc.WATCH_TIMEOUT_SECONDS > 0:
            asyncio.create_task(watch_condemnations(session))
        while True:
            try:
                pods = await list_pods(session)
                live = {p['metadata']['name'] for p in pods}
                # A pod can leave the list without ever being observed terminal
                # -- reaped node, eviction, or the monitor deleting its finished
                # Job -- and `terminal` is only written for pods in this list, so
                # its stream would retry until the run ended. Marking them
                # terminal lets the stream finalize and free the slot;
                # cancelling is the backstop for one wedged in a connection
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
                    if vanished[name] >= cc.VANISHED_GRACE_CYCLES:
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
                                tx_scan.TxApplyScanner(recreated=True),
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
                    # NOT gated on phase: a pod being deleted keeps phase Running
                    # until its object disappears, so gating this on terminal
                    # meant no disrupted pod ever recorded an exact duration and
                    # every one fell back to the poller's clock -- 268s reported
                    # against a ~500s attempt. terminated.finishedAt is present
                    # for the ~8s the object outlives the container, and
                    # pod_seconds returns None until then, so asking every cycle
                    # is self-guarding.
                    secs = pod_seconds(pod)
                    if secs is not None:
                        _pod_secs[name] = secs
                    start = (pod.get('status') or {}).get('startTime')
                    if start:
                        # So finalize can date the attempt from when the
                        # container STARTED if the object is deleted before any
                        # cycle catches its terminated timestamp.
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
                        verdicts.record_outcome(pod, end, labels.get(config.LABEL_ATTEMPT, '1'))
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
                    # Backstop: the watch normally opens this the moment the pod
                    # appears, and this covers events dropped across a
                    # reconnect. Same registry and guard, so whichever gets
                    # there first wins -- two readers on one pod would duplicate
                    # the archive and race write_state.
                    ensure_stream(name, end, labels.get(config.LABEL_ATTEMPT, '1'), phase)

                # AFTER the per-pod branches, never before them: this is a
                # serial sweep of every node's kubelet and on spot a dead one
                # costs the 10s connect timeout apiece, which once stretched a
                # cycle to 925s. It must also stay outside the `for` loop, whose
                # branches `continue` for a pod already streaming, so a sampler
                # among them fires only on the cycle a stream opens.
                # Unconditional, not gated on ephemeral mode: memory is sized in
                # both modes, and gating left every pvc run with no anon peak.
                await sample_kubelet(session, {
                    p['status']['hostIP'] for p in pods
                    if p.get('status', {}).get('hostIP')
                    and p.get('status', {}).get('phase') == 'Running'})
            except Exception as e:
                logger.warning("pod list failed: %s", e)
            await asyncio.sleep(cc.POLL_SECONDS)


def _mark_condemned(pod, name, end, attempt):
    """Flag a condemned pod so its poller opens a follow. Idempotent.

    Shared by the pod-list sweep and the watch so the two cannot drift:
    whichever sees the condition first does the work, the other no-ops.
    Detection latency, not the follow, is what loses the metric -- stellar-core
    exits about a second after SIGTERM and the object is reaped right behind it.
    """
    if name in _doomed:
        return False
    if (pod.get('status') or {}).get('phase') in ('Succeeded', 'Failed'):
        # Already finished. Its log is complete and a follow would only re-read
        # a dead pod every iteration.
        return False
    doom = verdicts.condemnation_reason(pod)
    if not doom:
        return False
    _doomed[name] = doom
    # Recorded now: once the object is gone there is no way to tell a drain we
    # lost a race with from a corpse that never had a metric to lose.
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


def read_state(end, attempt):
    try:
        with open(records.state_path(end, attempt)) as fh:
            ts = fh.read().strip()
    except OSError:
        return None
    # Also repairs a state file poisoned by an earlier build.
    return ts if ts and _TS_RE.match(ts) else None


def write_state(end, attempt, ts):
    path = records.state_path(end, attempt)
    try:
        records.write_atomic(path, ts)
    except OSError as e:
        logger.warning("could not persist state for range %s: %s", end, e)


def discard(end, attempt):
    # .metrics deliberately survives: it holds tx_apply for a range that
    # succeeded, which is the only case this runs in. Dropping it would let a
    # log-retention flag silently delete a Grafana series.
    for path in (records.log_path(end, attempt), records.state_path(end, attempt)):
        try:
            os.remove(path)
        except OSError:
            pass


# kubelet returns untimestamped plain text ("unable to retrieve container logs
# for containerd://...") when a container is not up yet. Partitioning that on
# the first space yields "unable", which lands in the state file and makes every
# later request ask for sinceTime=unableZ -> HTTP 400, forever.
_TS_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?$")

def write_metrics(end, attempt, values):
    """Persist per-range measurements for job_monitor's reconcile to read.

    Kept out of .outcome on purpose: that file answers "why did this attempt
    fail" and is only written for failed pods, whereas these are only
    meaningful for one that succeeded.
    """
    path = records.metrics_path(end, attempt)
    # Merge, and never let a peak go backwards. A measurement already on disk
    # must survive a later write that lacks it, but a plain overwrite is wrong
    # for a monotonic quantity: after a restart the fresh poller's first flush
    # would replace a higher pre-restart value with a lower one, undersizing the
    # range next run -- the one direction that costs an OOM.
    try:
        with open(path) as fh:
            prior = json.load(fh)
    except (OSError, ValueError):
        prior = {}
    merged = {**prior, **values}
    # attemptSeconds takes the same rule: it is fixed once the attempt ends and
    # every source is a lower bound on it. An attempt is finalized more than
    # once whenever a poller re-opens for a pod that is still listed, and there
    # the fallback clock starts at the restart -- newest-wins turned a recorded
    # 3600s into 0.0s.
    for k in cc.PEAK_KEYS + ('attemptSeconds',):
        a, b = prior.get(k), values.get(k)
        if a is not None and b is not None:
            merged[k] = max(a, b)
    # Once any poller or archive read proves this attempt resumed, a later
    # restarted poller cannot un-prove it: a merge containing resumed=False must
    # never lower the durable decision back to fresh.
    if prior.get('resumed') is True or values.get('resumed') is True:
        merged['resumed'] = True
    if (prior.get('attemptSecondsExact') is True
            or values.get('attemptSecondsExact') is True):
        merged['attemptSecondsExact'] = True
    # Same one-way rule: once a duration is dated from the container's own
    # startTime, a later poller-clock write must not strip the provenance the
    # monitor needs to use it as a chain leg.
    if (prior.get('attemptSecondsFromContainerStart') is True
            or values.get('attemptSecondsFromContainerStart') is True):
        merged['attemptSecondsFromContainerStart'] = True
    values = merged
    try:
        records.write_atomic(path, json.dumps(values))
        logger.info("range %s attempt %s metrics=%s", end, attempt, values)
    except OSError as e:
        logger.warning("could not persist metrics for range %s: %s", end, e)


def _flush_peak(name, axis, field, value):
    """Persist a high-water so a sidecar restart cannot lose it.

    Every key in PEAK_KEYS needs this: the peaks live in module dicts, so a
    restarted collector starts from zero and re-accumulates only from whatever
    the pod is using at that moment. Missing it for peakWorkingSetBytes is how
    136 of 3095 ranges came back with a working set BELOW their own anon, which
    cannot happen in one sample. write_metrics max-merges on PEAK_KEYS, so
    re-flushing a lower value later is harmless.
    """
    ref = _streaming.get(name)
    if not ref:
        return
    key = name + '/' + axis
    if value < _peak_flushed.get(key, 0) * cc.PEAK_FLUSH_RATIO:
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
    kubelet's `rssBytes` is cgroup v2 `anon`, the only limit-independent memory
    figure this workload has: page cache expands to fill whatever `memory.max`
    allows, so `memory.peak` is always ~= the limit (a range needing 862 MiB
    reported 12704 MiB against a 24000 MiB limit) and is useless for sizing.
    Sampled rather than exact -- cAdvisor housekeeping is ~10s, still ~3x finer
    than the 30s Prometheus scrape whose undersampling let profiled ranges OOM.
    """
    for ip in node_ips:
        # Straight at the kubelet, not through the apiserver's node proxy: that
        # needs `nodes/proxy`, which authorizes GET on EVERY kubelet path,
        # /pods and /containerLogs included, for any namespace on that node.
        # Going direct is the same data under `nodes/stats`, a grant that cannot
        # read pod inventory or logs at all. ssl=False because EKS kubelet
        # serving certs are self-signed; in-VPC hop to the node's own address.
        url = f"https://{ip}:{kube_http.KUBELET_PORT}/stats/summary"
        try:
            async with session.get(url, ssl=False,
                                   headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
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
                    # Flushed on growth, and re-measuring cannot recover it:
                    # disk use is not monotonic -- stellar-core drops its
                    # download staging once buckets are applied -- so a
                    # replacement sidecar sees a fraction of the real high-water.
                    # This sizes the next run's request, and one that comes back
                    # too small is an eviction.
                    _flush_peak(name, 'eph', 'peakEphemeralBytes', int(used))
            for c in entry.get('containers', []):
                # The worker container only. Sidecars share the pod, so summing
                # across containers -- or letting the last one win -- would size
                # the range from whichever one kubelet happened to list last.
                if c.get('name') != cc.CONTAINER:
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
                # Held in memory until the stream ends, so a restart would reset
                # the high-water to whatever the pod is using then, sizing the
                # next run too small. Flushing only on PEAK_FLUSH_RATIO growth
                # keeps this to a handful of writes over a pod's life.
                _flush_peak(name, 'anon', 'peakAnonBytes', int(rss))


def _mark_done(end, attempt):
    path = records.done_path(end, attempt)
    try:
        records.write_atomic(path, '')
    except OSError as e:
        # Costs a Job that waits out JOB_TTL_SECONDS, never correctness.
        logger.warning("could not mark range %s attempt %s done: %s", end, attempt, e)


async def finalize(session, pod, end, attempt, tx, done_ok, started=None):
    """Persist everything this attempt owes, then let its stream go.

    Reached from three places, and deliberately ONE implementation: a clean end
    of stream once the pod is terminal, a 404 once the object is gone, and a
    terminal pod whose polls keep failing past TERMINAL_POLL_ATTEMPTS. Two
    copies is how one path silently stops writing peakAnonBytes while the other
    keeps working. The converse matters too: an interrupted read on a pod that
    is STILL RUNNING must not come here, or it writes a truncated peak and
    leaves the range looking measured when it is not.
    """
    # Before discard: on success the archive is about to be deleted.
    measured = {}
    observed = _pod_secs.pop(pod, None)
    since_start = None
    began = _pod_start.pop(pod, None)
    if observed is None and began:
        # The container started at `began` and has just stopped: finalize runs
        # within a second or two of the exit. Not exact -- the true end is
        # terminated.finishedAt -- but it dates the attempt from the container
        # rather than from this poller, whose clock can be near zero against a
        # multi-hour run.
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
        # Not exact, but it measures the container's own lifetime rather than
        # this process's attention span, which is the distinction the monitor's
        # chain gate cares about. Measured against two evicted pods: 370.9s and
        # 375.1s against a true ~373s, versus the poller clock's -46%.
        measured['attemptSecondsExact'] = False
        measured['attemptSecondsFromContainerStart'] = True
    elif started is not None:
        # Fallback only: the monitor's figure comes from the pod's terminated
        # timestamps and is preferred. write_metrics keeps this from lowering a
        # duration already on the volume, since a second poller's clock starts
        # at the restart.
        measured['attemptSeconds'] = round(
            asyncio.get_event_loop().time() - started, 1)
        measured['attemptSecondsExact'] = False
    # RESUME is printed before stellar-core starts and medida once at exit, so a
    # recreated poller can miss either forever. The archive was appended before
    # finalization; recover only the state this scanner could have missed.
    archived = None
    need_resume = int(attempt) > 1 and not tx.resume_decided
    # Not gated on `recreated`: stellar-core prints the block once at exit, so a
    # poller that ran start to finish but ended a beat early has no total.
    need_tx = tx.seconds is None
    if need_resume or need_tx:
        archived = tx_scan.scan_archive(end, attempt, need_tx=need_tx)
    if tx.resumed or (archived is not None and archived.resumed):
        # Not a peak -- PEAK_FIELDS filters it out of the profile.
        # peaks_for_range reads it to decide how far back to aggregate: a
        # resumed attempt only measured the tail of its range, so the attempt
        # before it still counts.
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
        # Recorded for every attempt, not just the winner: peaks_for_range takes
        # the max across attempts, so a partial attempt can only raise the
        # figure, which is what makes a resumed range report the download-phase
        # peak it actually hit rather than its tail. The monitor drops an attempt
        # from the axis it died on, since an OOM-killed peak measures the limit.
        measured['peakAnonBytes'] = anon
    ws = _ws_peak.pop(pod, None)
    if ws is not None:
        # Diagnostic only -- working set counts active page cache, which grows
        # to fill whatever limit the pod was given, so it must never size
        # anything. Kept because the anon/ws gap is what tells you a range is
        # cache-heavy rather than large.
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
    # write nothing further for this attempt" and only then reaps the Job, which
    # deletes the pod -- the one place peaks can still be read from -- so it has
    # to land after .metrics or it licenses the reap it exists to prevent.
    _mark_done(end, attempt)


async def _poll_once(session, pod, end, attempt, last_ts, tx):
    """One short read of a pod's log. Returns (new_last_ts, gone).

    No follow=true: the request completes and the connection is released, so
    concurrency is bounded by _poll_slots rather than by how many pods exist. A
    single poll takes ~0.22s, so 2096 pods on a 10s interval need ~46 concurrent
    slots against the 2096 permanently-held connections follow=true required.
    """
    params = {'container': cc.CONTAINER, 'timestamps': 'true'}
    if last_ts:
        # Second granularity, so this overlaps on purpose; the per-line
        # comparison below removes the overlap exactly.
        params['sinceTime'] = last_ts[:19] + 'Z'
    url = f"{kube_http.API}/api/v1/namespaces/{config.NAMESPACE}/pods/{pod}/log"
    async with _poll_slots:
        async with session.get(url, params=params,
                               headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
            if resp.status == 404:
                return last_ts, True
            resp.raise_for_status()
            # Chunked, not line-wise: aiohttp raises above 512 KiB on a single
            # line and a carriage-return progress meter trivially exceeds that
            # -- one 628 MiB download arrived as a single "line".
            body = ''
            async for chunk in resp.content.iter_chunked(65536):
                body += chunk.decode('utf-8', 'replace')
                if len(body) > cc.MAX_POLL_CHARS:
                    break

    return _ingest(body, end, attempt, last_ts, tx), False


def _ingest(body, end, attempt, last_ts, tx):
    """Append one block of timestamped log text to the archive; new last_ts.

    Split out of _poll_once so the doomed-pod follow stream lands its bytes
    through exactly the same path -- dedup, gzip member framing, tx scanning and
    resume-point bookkeeping. Two copies is how one route silently stops feeding
    TxApplyScanner while the other keeps working.
    """
    pending = None
    lines = [l for l in re.split(r'[\r\n]', body) if l]
    if not lines:
        return last_ts
    # Compressed into memory first, then appended in ONE write, so the file only
    # ever gains whole members. Appending with gzip.open(..., 'at') left the
    # archive ending in a member with no end-of-stream marker for most of a large
    # poll, and job_monitor reads that same file to recover txApplySeconds --
    # gzip raises EOFError on a truncated member, so one in-flight poll could
    # abort a reconcile pass for every range. Costs no extra memory: `body` above
    # is already the entire poll uncompressed, and nothing is held between polls.
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
    path = records.log_path(end, attempt)
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

    Opened only for pods the cluster has already condemned, so the connection is
    held for the couple of minutes before the node goes away, not for the hours
    a range runs. Proven on ssc-test: with the stream held, SIGTERM yields `got
    signal 15` -> `metric 'ledger.transaction.apply'` -> `Application destroyed`
    inside 4ms, all captured, where the same pod polled at 5s recorded `pod gone
    before disruption seen`. Bytes are ingested as they arrive, so a node that
    disappears mid-read still leaves everything up to that point in the archive.
    """
    params = {'container': cc.CONTAINER, 'timestamps': 'true', 'follow': 'true'}
    if last_ts:
        params['sinceTime'] = last_ts[:19] + 'Z'
    url = f"{kube_http.API}/api/v1/namespaces/{config.NAMESPACE}/pods/{pod}/log"
    deadline = asyncio.get_event_loop().time() + cc.DOOMED_FOLLOW_SECONDS
    buf = ''
    if _follow_slots.locked():
        # Every follow budget is spoken for, so this pod polls instead. Better
        # than queueing: the pod has ~2 minutes to live, and a follow that opens
        # after it dies captures nothing while still holding a slot.
        logger.info("range %s: no follow slot free (%d in use), polling instead",
                    end, cc.MAX_DOOMED_FOLLOWS)
        _doomed.pop(pod, None)
        return await _poll_once(session, pod, end, attempt, last_ts, tx)
    async with _follow_slots:
        async with session.get(url, params=params,
                               headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
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
                                end, cc.DOOMED_FOLLOW_SECONDS)
                    _doomed.pop(pod, None)
                    break
    if buf:
        last_ts = _ingest(buf, end, attempt, last_ts, tx)
    # One follow per pod. The stream ending means the container exited, so the
    # caller falls back to a normal poll for the terminal check and finalize;
    # leaving the flag set would re-open a stream on a dead pod every iteration.
    _doomed.pop(pod, None)
    return last_ts, False


async def poll_pod(session, pod, end, attempt, done, done_ok):
    """Read one pod's log to completion, by repeated short polls.

    Replaces a follow=true stream, whose cost scaled with parallelism: it held a
    connection, a deflate buffer and aiohttp buffers for the pod's entire life,
    and at 2096 pods the sidecar sat at 1444 MiB of a 2048 MiB limit and 1.00 of
    2 cpu, extrapolating past both at 4096. The one thing follow did better is
    the tail, so on seeing the pod go terminal this polls once more immediately
    before finalizing -- without it every spot eviction loses up to one interval
    of exactly the log we most want.
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
    tx = tx_scan.TxApplyScanner(recreated=bool(last_ts))
    backoff = cc.LOG_POLL_SECONDS
    failures = 0

    first_pass = True
    while True:
        was_terminal = done(pod)
        if first_pass and was_terminal:
            # The pod was already terminal before this poller existed, so
            # `started` measures how long WE have been watching, not how long
            # the container ran -- across two collector restarts, 150 metrics
            # files recorded a sub-5s duration beside a >500MiB anon peak.
            # Report nothing rather than a fabricated near-zero; the monitor's
            # figure from the pod's own timestamps is authoritative anyway.
            started = None
        first_pass = False
        followed = False
        try:
            if _doomed.get(pod) and not was_terminal and cc.DOOMED_FOLLOW_SECONDS > 0:
                followed = True
                # Condemned and still running: hold the connection through the
                # kill. Returns when the container exits or the notice is
                # withdrawn, and the loop re-checks terminal immediately after.
                last_ts, gone = await _follow_tail(
                    session, pod, end, attempt, last_ts, tx)
            else:
                last_ts, gone = await _poll_once(
                    session, pod, end, attempt, last_ts, tx)
            # Fallback interval for a condemned pod that could not follow. 1s
            # sampling alone still closes the ~9s window between the medida
            # block and the object being deleted, so a mass reclaim degrades
            # rather than loses.
            backoff = (cc.DOOMED_POLL_SECONDS if _doomed.get(pod)
                       else cc.LOG_POLL_SECONDS)
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
            if was_terminal and failures >= cc.TERMINAL_POLL_ATTEMPTS:
                # The container has exited and its log is not coming back. A
                # follow stream finalized here because it already held the bytes;
                # polling has to decide to stop asking, or it spins on a dead pod
                # for the rest of the run and never writes its metrics.
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
            # The follow only returns once the container has exited, so the very
            # next read is the one that matters. Sleeping here would hand the
            # interval back to the race the follow exists to win.
            continue
        # Not a blind sleep: a pod going terminal cuts it short. Polling faster
        # would not help -- sinceTime has second granularity -- and the delay
        # that matters is between the container exiting and the last read, not
        # between routine polls.
        ev = _wake.setdefault(pod, asyncio.Event())
        try:
            await asyncio.wait_for(ev.wait(), timeout=backoff)
        except asyncio.TimeoutError:
            pass
        finally:
            # Left set, the Event makes every later wait return instantly, so
            # the terminal-poll backoff never sleeps and TERMINAL_POLL_ATTEMPTS
            # is spent in one millisecond, giving the pod no time for its final
            # log to become readable. A wake is consumed by the poll it triggers.
            ev.clear()


async def list_pods(session):
    url = f"{kube_http.API}/api/v1/namespaces/{config.NAMESPACE}/pods"
    params = {'labelSelector': f"{config.LABEL_RUN}={config.RUN_NAME}"}
    async with session.get(url, params=params,
                           headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
        resp.raise_for_status()
        return (await resp.json()).get('items', [])


def ensure_stream(name, end, attempt, phase):
    """Open this pod's poller if it has none. Idempotent; returns whether it did.

    Called by the watch as a pod appears and again if it is condemned, and by
    the pod-list loop as a backstop for events dropped across a reconnect.
    Opening a stream is time-critical -- a condemned pod is gone a second after
    stellar-core exits -- so it must not be reachable only from a poll cycle: on
    the 900-worker run the loop's cycle stretched to 925s and five -a2 legs
    lived and died with no reader at all.
    """
    if name in _tasks or name in _streamed or not _stream_ctx:
        return False
    if phase not in cc.POLLABLE_PHASES:
        # Allowlist, not "skip Pending": a container that has not started
        # answers 400 "waiting to start", and Unknown means the node stopped
        # reporting. Both are retried on the cycle they become pollable.
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
    discovery, bookkeeping and finalize, and this only ever sets _doomed earlier
    than the list would have -- the difference between opening a follow while
    stellar-core is still running and opening it on a 404. Cheaper than the
    sweep it front-runs, too: one connection served from the apiserver's cache,
    sending only deltas. Never fatal -- any failure falls back to the sweep.
    """
    url = f"{kube_http.API}/api/v1/namespaces/{config.NAMESPACE}/pods"
    rv = None
    while True:
        params = {'labelSelector': f"{config.LABEL_RUN}={config.RUN_NAME}", 'watch': 'true',
                  'allowWatchBookmarks': 'true',
                  'timeoutSeconds': str(cc.WATCH_TIMEOUT_SECONDS)}
        if rv:
            params['resourceVersion'] = rv
        try:
            async with session.get(url, params=params,
                                   headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
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
                    # Order is not load-bearing: create_task only schedules the
                    # poller, so _wake has no entry yet either way, and poll_pod
                    # reads _doomed at the top of its first pass. The wake below
                    # is for a poller from an earlier event, already asleep.
                    ensure_stream(name, end, attempt,
                                  (obj.get('status') or {}).get('phase'))
                    _mark_condemned(obj, name, end, attempt)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            logger.warning("condemnation watch dropped (%s); retrying", exc)
            await asyncio.sleep(cc.WATCH_RETRY_SECONDS)


if __name__ == '__main__':
    asyncio.run(main())
