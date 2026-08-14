"""Liveness of the workers' stellar-core /info endpoint, one sweep per reconcile.

The reconcile loop already holds the authoritative pod list. This probes that
list concurrently and returns a snapshot: up if /info answered 200, down for
anything else, unknown for whatever the sweep did not get to before its deadline.

No state is carried between sweeps -- no hysteresis, no scheduler, no threads.
The numbers feed a Grafana panel and nothing else reads them, so a stale-free
snapshot is worth more than a smoothed one.
"""
import asyncio
import logging

import aiohttp

import monitor_config as mc

logger = logging.getLogger()

_ADMIN_PORT = 11626       # stellar-core's admin/HTTP port


def targets(pods):
    """Current Running-with-IP pods, keyed by pod identity.

    A UID change is a replacement even when the Job name or IP is reused. Tests
    and unusually incomplete API objects may lack a UID, where the pod name is
    still unique for its lifetime.
    """
    out = {}
    for pod in pods:
        pod_status = getattr(pod, 'status', None)
        metadata = getattr(pod, 'metadata', None)
        ip = getattr(pod_status, 'pod_ip', None)
        if getattr(pod_status, 'phase', None) != 'Running' or not ip or metadata is None:
            continue
        name = getattr(metadata, 'name', None)
        identity = getattr(metadata, 'uid', None) or name
        if identity and name:
            out[str(identity)] = (str(name), str(ip))
    return out


async def _probe(session, ip, timeout):
    """True only for HTTP 200. A timeout or refused connection is False."""
    host = f"[{ip}]" if ':' in ip else ip
    async with session.get(f"http://{host}:{_ADMIN_PORT}/info",
                           timeout=aiohttp.ClientTimeout(total=timeout)) as resp:
        return resp.status == 200


async def sweep(targets, concurrency=None, timeout=None, deadline=None):
    """Probe every target concurrently; return {'up','down','unknown'}.

    Not a TaskGroup: a TaskGroup cancels its siblings when one task raises,
    which is the opposite of what a fleet sweep wants -- one unreachable pod
    must not discard the other 1023 answers. asyncio.wait with a deadline keeps
    whatever finished and cancels only the stragglers.
    """
    concurrency = int(concurrency or mc.LIVENESS_MAX_CONCURRENCY)
    timeout = float(timeout or mc.LIVENESS_PROBE_TIMEOUT_SECONDS)
    deadline = float(deadline or mc.LIVENESS_SWEEP_SECONDS)
    counts = {'up': 0, 'down': 0, 'unknown': len(targets)}
    if not targets:
        return {'up': 0, 'down': 0, 'unknown': 0}

    # `limit` is the concurrency bound: aiohttp holds a task at connect until a
    # slot frees, so a semaphore on top of it would be enforcing the same number
    # twice. force_close because a worker pod can vanish between sweeps and a
    # pooled socket to a dead pod would be handed straight back out.
    connector = aiohttp.TCPConnector(limit=concurrency, force_close=True)
    async with aiohttp.ClientSession(connector=connector) as session:
        tasks = [asyncio.create_task(_probe(session, ip, timeout))
                 for _, ip in targets.values()]
        done, pending = await asyncio.wait(tasks, timeout=deadline)
        for task in pending:
            task.cancel()
        if pending:
            await asyncio.gather(*pending, return_exceptions=True)
        for task in done:
            try:
                up = task.result()
            except Exception:
                up = False            # timeout, refused, DNS, malformed response
            counts['up' if up else 'down'] += 1
            counts['unknown'] -= 1
    return counts


def publish(targets):
    """Run one sweep and return its counts. Called from the reconcile loop.

    Blocks for at most LIVENESS_SWEEP_SECONDS: everything still outstanding at
    the deadline is cancelled and reported unknown, so a fleet of unreachable
    pods costs the deadline and never the sum of their timeouts.
    """
    if not targets:
        return {'up': 0, 'down': 0, 'unknown': 0}
    try:
        return asyncio.run(sweep(targets))
    except Exception as e:
        logger.warning("liveness sweep failed (%s); reporting all workers unknown", e)
        return {'up': 0, 'down': 0, 'unknown': len(targets)}
