"""Streaming log collector for parallel catchup.

Runs as a sidecar next to job_monitor, sharing its /logs volume. One cycle:
list the run's pods, then read every pod's log and sample every node's kubelet
off that list. Nothing is read after a Job finishes -- Karpenter deletes the
node about a minute after its last pod exits, taking every pod object with it,
so the answers have to be taken while the pod still exists.

Two conditions carry all the work. The FIRST read of an attempt carries its
resume decision; the read that finds the pod terminal carries its verdict, its
tx-apply total and the .done marker the monitor reaps on. Everything between is
appending bytes.

Resume is idempotent across a dropped read and a restart: reconnect with
sinceTime=<last durable timestamp>, which has second granularity and so overlaps
on purpose, then drop any line whose own kubelet timestamp is <= it. Dying
between the append and the state write replays one read's worth of lines, so
this is at least once, deduped to near-exact.
"""

import asyncio
import gzip
import io
import json
import os
import re
from datetime import datetime, timedelta

import aiohttp
from logger import build_logger
import collector_config as cc
import kube_http
import config
import records
import state_files
import tx_scan
import verdicts

logger = build_logger('log_collector', name='log-collector', to_file=False)

# Bounds concurrent log reads, one coroutine per pod per cycle.
_poll_slots = asyncio.Semaphore(cc.MAX_CONCURRENT_POLLS)
# One request per NODE, and a dead one costs the connect timeout.
_sample_slots = asyncio.Semaphore(cc.MAX_CONCURRENT_SAMPLES)
# Follows get their own budget: one holds its slot for a whole drain, so sharing
# would let a mass reclaim starve every ordinary read in the run.
_follow_slots = asyncio.Semaphore(cc.MAX_DOOMED_FOLLOWS)
# (end, attempt) -> resume timestamp, hydrated from .state at boot and written
# back through. Keyed like every file on the volume, so a vanished attempt is
# still identifiable once its pod is gone.
_last_ts = {}
# (end, attempt) -> the follow task held through a drain. The only work that
# outlives a cycle.
_follows = {}


async def main():
    os.makedirs(config.LOG_DIR, exist_ok=True)
    conn = aiohttp.TCPConnector(
        limit=cc.MAX_CONCURRENT_POLLS + cc.MAX_DOOMED_FOLLOWS + 64,
        ssl=kube_http.ssl_ctx())
    # sock_read, because a cycle gathers every pod: one wedged read would hold
    # up the whole pass. No total timeout -- a follow is meant to be held.
    timeout = aiohttp.ClientTimeout(total=None, sock_connect=10,
                                    sock_read=cc.READ_TIMEOUT_SECONDS)
    _last_ts.clear()
    _last_ts.update(state_files.hydrate_states())
    logger.info("resuming %d attempts from disk", len(_last_ts))

    async with aiohttp.ClientSession(connector=conn, timeout=timeout) as session:
        logger.info("streaming logs for run=%s into %s", config.RUN_NAME, config.LOG_DIR)
        if cc.WATCH_TIMEOUT_SECONDS > 0:
            # The one thing that cannot ride the cycle: a held watch starts a
            # follow between sweeps, which is the whole point of it.
            asyncio.create_task(watch_condemnations(session))
        while True:
            try:
                pods = await list_pods(session)
                ranges = {p['metadata']['name']: _identify(p)
                          for p in pods if _identify(p)}
                # Claimed while Pending too, so the sweep below can finish an
                # attempt whose pod is deleted before it is ever pollable.
                for end, attempt in ranges.values():
                    if (end, attempt) not in _last_ts and not os.path.exists(
                            records.done_path(end, attempt)):
                        _write_state(end, attempt, '')
                nodes = {p['status']['hostIP'] for p in pods
                         if p.get('status', {}).get('hostIP')
                         and p.get('status', {}).get('phase') == 'Running'}
                await asyncio.gather(
                    _bounded(_poll_slots,
                             [service_pod(session, p) for p in pods if _wanted(p)]),
                    _bounded(_sample_slots,
                             [sample_node(session, ip, ranges) for ip in nodes]))
                _sweep_vanished(set(ranges.values()))
            except asyncio.CancelledError:
                raise
            except Exception as e:
                logger.warning("cycle failed: %s", e)
            await asyncio.sleep(cc.POLL_SECONDS)


async def _bounded(slots, coros):
    """Run coros under `slots`. One failure must not cancel the other 4095."""
    async def run(c):
        async with slots:
            return await c
    return await asyncio.gather(*(run(c) for c in coros), return_exceptions=True)


def _identify(pod):
    """(end, attempt) for a worker pod, or None if it is not one of ours."""
    labels = pod['metadata'].get('labels', {})
    end = labels.get(config.LABEL_RANGE)
    return (end, labels.get(config.LABEL_ATTEMPT, '1')) if end else None


# Phases whose log endpoint can answer. Pending has no container yet and Unknown
# means the node stopped reporting; the terminal phases are kept because that is
# where a pod's final output lives.
POLLABLE_PHASES = ('Running', 'Succeeded', 'Failed')


def _wanted(pod):
    """Pods whose log endpoint can answer and whose attempt is not finished.

    Pending has no container yet and Unknown means the node stopped reporting;
    the terminal phases are kept because that is where the final output lives.
    """
    key = _identify(pod)
    if key is None or pod.get('status', {}).get('phase') not in POLLABLE_PHASES:
        return False
    return not os.path.exists(records.done_path(*key))


async def service_pod(session, pod):
    """One pod, one cycle: read what is new, then apply whichever condition fits."""
    name = pod['metadata']['name']
    end, attempt = _identify(pod)
    phase = pod.get('status', {}).get('phase')
    terminal = phase in ('Succeeded', 'Failed')

    if not terminal and _start_follow(session, name, end, attempt, pod):
        return                        # the follow owns this attempt's stream
    if (end, attempt) not in _last_ts:
        _write_state(end, attempt, '')
    since = _last_ts[(end, attempt)]
    # A terminal read reaches further back, so a medida block split across two
    # reads is whole in this one. _ingest still dedups, so the archive is
    # unchanged -- only the scan sees the overlap.
    text, gone = await _read(session, name, _rewind(since) if terminal else since)
    if text:
        _write_state(end, attempt, _ingest(end, attempt, text, since))
        _scan(end, attempt, text)
    if terminal or gone:
        _finish(None if gone else pod, end, attempt, phase == 'Succeeded')


def _start_follow(session, name, end, attempt, pod):
    """Hold a stream through a drain, if this pod has been condemned.

    stellar-core prints its medida block ~4ms after SIGTERM and the object is
    deleted seconds later, so an interval read straddles the whole thing -- on
    the 2048-worker run, 810 evictions lost 809 txApply values. A held
    connection already has those bytes, and is held only for the drain.
    """
    key = (end, attempt)
    if key in _follows:
        return not _follows[key].done()
    if cc.DOOMED_FOLLOW_SECONDS <= 0:
        return False
    doom = verdicts.condemnation_reason(pod)
    if not doom:
        return False
    # Recorded now: once the object is gone there is no telling a drain we lost
    # a race with from a corpse that never had a metric to lose. The duration
    # goes with it, and for the same reason -- an evicted pod is usually deleted
    # before it is ever seen terminal, and _finish has nothing to read it from.
    # It runs short by the drain, and merges by max, so it is only ever a floor.
    write_metrics(end, attempt, {'disruptionReason': doom, **_duration(pod)})
    logger.info("range %s condemned (%s), opening follow", end, doom)
    _follows[key] = asyncio.create_task(_run_follow(session, name, end, attempt))
    return True


async def _run_follow(session, name, end, attempt):
    async with _follow_slots:
        since = _last_ts.get((end, attempt), '')
        try:
            text, _ = await _read(session, name, since, follow=True)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            logger.info("range %s follow ended (%s); back to interval reads", end, e)
            return
        if text:
            _write_state(end, attempt, _ingest(end, attempt, text, since))
            _scan(end, attempt, text)


def _finish(pod, end, attempt, succeeded):
    """Everything the attempt owes, then the marker that licenses a reap."""
    if pod is not None:
        if (pod.get('status') or {}).get('phase') == 'Failed':
            verdicts.record_outcome(pod, end, attempt)
        write_metrics(end, attempt, _duration(pod))
    if not cc.SAVE_SUCCESS_LOGS and succeeded:
        # .metrics survives on purpose: it holds tx_apply for a range that
        # succeeded, and a retention flag must not delete a Grafana series.
        discard(end, attempt)
    task = _follows.pop((end, attempt), None)
    if task is not None and not task.done():
        task.cancel()
    _last_ts.pop((end, attempt), None)
    # LAST. The monitor treats this as "nothing further is coming" and only then
    # reaps the Job -- which deletes the pod, the one place peaks can be read
    # from. Ahead of the metrics it would license exactly the reap it prevents.
    try:
        records.write_atomic(records.done_path(end, attempt), '')
    except OSError as e:
        # Costs a Job that waits out its TTL, never correctness.
        logger.warning("could not mark range %s attempt %s done: %s", end, attempt, e)
    logger.info("range %s attempt %s finished", end, attempt)


def _duration(pod):
    """How long the container ran, and how well that is known.

    The pod's own start->finish is exact. Its startTime against now is off by
    the cycle that noticed, but still measures the CONTAINER -- the distinction
    the monitor's chain gate cares about, and the only duration a disrupted
    attempt ever produces. A clock this process started measures neither, and is
    not recorded: the monitor rejects it anyway.
    """
    st = pod.get('status') or {}
    start = st.get('startTime')
    if not start:
        return {}
    try:
        began = datetime.strptime(start, '%Y-%m-%dT%H:%M:%SZ')
    except ValueError:
        return {}
    for cs in (st.get('containerStatuses') or []):
        fin = ((cs.get('state') or {}).get('terminated') or {}).get('finishedAt')
        if fin:
            try:
                ended = datetime.strptime(fin, '%Y-%m-%dT%H:%M:%SZ')
            except ValueError:
                break
            return {'attemptSeconds': round((ended - began).total_seconds(), 1),
                    'attemptSecondsExact': True}
    since = (datetime.utcnow() - began).total_seconds()
    if since <= 0:
        return {}
    return {'attemptSeconds': round(since, 1), 'attemptSecondsExact': False,
            'attemptSecondsFromContainerStart': True}


def _sweep_vanished(live):
    """Attempts with state on the volume and no pod left to read.

    A reaped node, an eviction or the monitor deleting a finished Job all take
    the pod without it ever being seen terminal. The archive still holds real
    bytes, so mark the attempt done rather than leaving its Job to time out.
    """
    for key in [k for k in _last_ts if k not in live]:
        logger.info("range %s attempt %s: pod gone, finishing on what was read", *key)
        _finish(None, key[0], key[1], False)


# --- reading -----------------------------------------------------------------

def _rewind(ts):
    """`ts` moved back a few seconds, for the overlapping terminal read."""
    if not ts:
        return ts
    try:
        return (datetime.strptime(ts[:19], '%Y-%m-%dT%H:%M:%S')
                - timedelta(seconds=cc.TERMINAL_REREAD_SECONDS)
                ).strftime('%Y-%m-%dT%H:%M:%SZ')
    except ValueError:
        return ts


async def _read(session, name, last_ts, follow=False):
    """One read of a pod's log. Returns (text, gone).

    No follow=true on the ordinary path: the request completes and the
    connection is released, so concurrency is bounded by the gather rather than
    by how many pods exist. Held connections cost the sidecar 1444 MiB of a
    2048 MiB limit at 2096 streams.
    """
    params = {'container': cc.CONTAINER, 'timestamps': 'true'}
    if follow:
        params['follow'] = 'true'
    if last_ts:
        # Second granularity, so this overlaps on purpose; _ingest removes it.
        params['sinceTime'] = last_ts[:19] + 'Z'
    url = f"{kube_http.API}/api/v1/namespaces/{config.NAMESPACE}/pods/{name}/log"
    async with session.get(url, params=params,
                           headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
        if resp.status == 404:
            return '', True
        resp.raise_for_status()
        # Chunked, not line-wise: aiohttp raises above 512 KiB on one line and a
        # carriage-return progress meter exceeds that -- a 628 MiB download once
        # arrived as a single "line". The cap is the backstop against a blob
        # that never terminates.
        body = ''
        async for chunk in resp.content.iter_chunked(65536):
            body += chunk.decode('utf-8', 'replace')
            if len(body) > cc.MAX_POLL_CHARS:
                break
    return body, False


def _ingest(end, attempt, body, last_ts):
    """Append the new lines to the archive; return the resume point."""
    pending = None
    # Compressed into memory, then appended in ONE write, so the file only ever
    # gains whole gzip members. Appending through gzip.open left it ending in a
    # member with no end-of-stream marker for most of a large write, and the
    # monitor reads that same file -- one in-flight append could abort a
    # reconcile pass for every range.
    member = io.BytesIO()
    wrote = False
    with gzip.GzipFile(fileobj=member, mode='wb') as fh:
        for line in (l for l in re.split(r'[\r\n]', body) if l):
            ts, _, rest = line.partition(' ')
            if not state_files.TS_RE.match(ts):
                fh.write((line + '\n').encode('utf-8'))    # keep, never resume from
                wrote = True
                continue
            if last_ts and ts <= last_ts:
                continue                                   # exact dedup of the overlap
            fh.write((rest + '\n').encode('utf-8'))
            wrote = True
            pending = ts
    if wrote:
        with open(records.log_path(end, attempt), 'ab') as out:
            out.write(member.getvalue())
    return pending or last_ts


def _scan(end, attempt, text):
    """Record whatever this text proves: that the attempt resumed, or its total.

    Both are printed once -- RESUME before stellar-core starts, the medida block
    at exit -- so a throwaway scanner over each read finds them with nothing
    carried between cycles.
    """
    scanner = tx_scan.TxApplyScanner()
    for line in text.splitlines():
        scanner.feed(line)
    found = {}
    if scanner.resumed:
        found['resumed'] = True
    if scanner.seconds is not None:
        found['txApplySeconds'] = scanner.seconds
    write_metrics(end, attempt, found)


# --- the volume --------------------------------------------------------------

def _write_state(end, attempt, ts):
    _last_ts[(end, attempt)] = ts
    try:
        records.write_atomic(records.state_path(end, attempt), ts)
    except OSError as e:
        logger.warning("could not persist state for range %s: %s", end, e)


def discard(end, attempt):
    for path in (records.log_path(end, attempt), records.state_path(end, attempt)):
        try:
            os.remove(path)
        except OSError:
            pass


# Fields that only ever grow, so a merge maxes them instead of overwriting.
PEAK_KEYS = ('peakAnonBytes', 'peakWorkingSetBytes', 'peakEphemeralBytes')


def write_metrics(end, attempt, values):
    """Merge measurements into .metrics for the monitor's reconcile to read.

    Never lets a peak or a duration go backwards, and never un-proves a flag: a
    later writer can only know LESS -- a restarted collector re-accumulates from
    whatever the pod is using now, and its lower reading would undersize the
    range next run, which is the one direction that costs an OOM.
    """
    if not values:
        return
    path = records.metrics_path(end, attempt)
    try:
        with open(path) as fh:
            prior = json.load(fh)
    except (OSError, ValueError):
        prior = {}
    merged = {**prior, **values}
    for k in PEAK_KEYS + ('attemptSeconds',):
        a, b = prior.get(k), values.get(k)
        if a is not None and b is not None:
            merged[k] = max(a, b)
    for flag in ('resumed', 'attemptSecondsExact', 'attemptSecondsFromContainerStart'):
        if prior.get(flag) is True or values.get(flag) is True:
            merged[flag] = True
    if merged == prior:
        return             # nothing new -- this is what makes per-cycle sampling cheap
    try:
        records.write_atomic(path, json.dumps(merged))
        logger.info("range %s attempt %s metrics=%s", end, attempt, merged)
    except OSError as e:
        logger.warning("could not persist metrics for range %s: %s", end, e)


# --- kubelet -----------------------------------------------------------------

async def sample_node(session, ip, ranges):
    """Peak memory and disk for every worker on one node, straight to .metrics.

    Nothing is held in memory: write_metrics keeps the running max on the
    volume, so a restart cannot lower a high-water and there is no per-pod peak
    to lose. kubelet's rssBytes is cgroup v2 anon, the only limit-independent
    memory figure this workload has -- page cache grows to fill whatever
    memory.max allows, so memory.peak is always ~= the limit and useless for
    sizing. Prometheus can answer neither axis: no pod label on fs usage, and a
    30s scrape is the undersampling that let profiled ranges OOM.

    Straight at the kubelet, not the apiserver's node proxy: that needs
    nodes/proxy, which authorizes GET on every kubelet path including
    /containerLogs for any namespace on that node. ssl=False because EKS serving
    certs are self-signed; this is an in-VPC hop to the node's own address.
    """
    url = f"https://{ip}:{kube_http.KUBELET_PORT}/stats/summary"
    try:
        async with session.get(url, ssl=False,
                               headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
            resp.raise_for_status()
            summary = await resp.json()
    except Exception as e:
        # Not debug: a silent failure here leaves the profile looking merely
        # absent rather than broken.
        logger.warning("kubelet stats unavailable on %s: %s", ip, e)
        return
    for entry in summary.get('pods', []):
        key = ranges.get((entry.get('podRef') or {}).get('name'))
        if key is None:
            continue
        found = {}
        used = (entry.get('ephemeral-storage') or {}).get('usedBytes')
        if used is not None and config.STORAGE_MODE == 'ephemeral':
            found['peakEphemeralBytes'] = int(used)
        for c in entry.get('containers', []):
            # The worker container only: sidecars share the pod, and letting the
            # last one win would size the range from whichever kubelet listed.
            if c.get('name') != cc.CONTAINER:
                continue
            mem = c.get('memory') or {}
            if mem.get('workingSetBytes') is not None:
                found['peakWorkingSetBytes'] = int(mem['workingSetBytes'])
            if mem.get('rssBytes') is not None:
                found['peakAnonBytes'] = int(mem['rssBytes'])
        write_metrics(key[0], key[1], found)


# --- the apiserver -----------------------------------------------------------

async def list_pods(session):
    url = f"{kube_http.API}/api/v1/namespaces/{config.NAMESPACE}/pods"
    params = {'labelSelector': f"{config.LABEL_RUN}={config.RUN_NAME}"}
    async with session.get(url, params=params,
                           headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
        resp.raise_for_status()
        return (await resp.json()).get('items', [])


async def watch_condemnations(session):
    """Start follows the moment a condemnation is written, ahead of the cycle.

    Only ever earlier than the cycle would have been -- the difference between
    opening a follow while stellar-core still runs and opening it on a 404. One
    connection served from the apiserver's cache, sending deltas, where the
    cycle re-lists every pod. Never fatal: any failure falls back to the cycle.
    """
    url = f"{kube_http.API}/api/v1/namespaces/{config.NAMESPACE}/pods"
    rv = None
    while True:
        params = {'labelSelector': f"{config.LABEL_RUN}={config.RUN_NAME}",
                  'watch': 'true', 'allowWatchBookmarks': 'true',
                  'timeoutSeconds': str(cc.WATCH_TIMEOUT_SECONDS)}
        if rv:
            params['resourceVersion'] = rv
        try:
            async with session.get(url, params=params,
                                   headers={'Authorization': f'Bearer {kube_http.token()}'}) as resp:
                if resp.status == 410:
                    rv = None          # aged out of history; re-sync from scratch
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
                    # Tracked on every event, bookmarks included -- that is what
                    # they are for -- so a reconnect resumes rather than re-syncs.
                    rv = meta.get('resourceVersion') or rv
                    if ev.get('type') == 'ERROR':
                        if obj.get('code') == 410:
                            rv = None
                        break
                    if ev.get('type') not in ('ADDED', 'MODIFIED') or not meta:
                        continue
                    key = _identify(obj)
                    if key and (obj.get('status') or {}).get('phase') == 'Running':
                        _start_follow(session, meta['name'], key[0], key[1], obj)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            logger.warning("condemnation watch dropped (%s); retrying", exc)
            await asyncio.sleep(cc.WATCH_RETRY_SECONDS)


if __name__ == '__main__':
    asyncio.run(main())
