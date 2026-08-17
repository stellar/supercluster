"""Whether the workers' stellar-core is answering, one sweep per pass.

The reconcile loop already holds the authoritative pod list, so this probes that
rather than listing anything itself. No state is carried between sweeps -- no
hysteresis, no smoothing, no scheduler. These numbers feed a dashboard and
nothing else reads them, so a stale-free snapshot beats a smoothed one.
"""
import asyncio
import logging

import aiohttp

import config

logger = logging.getLogger('job_monitor')

_ADMIN_PORT = 11626

EMPTY = {'up': 0, 'down': 0, 'unknown': 0}


def targets(pods):
    """Running-with-an-IP pods, keyed by pod UID.

    A UID change is a replacement even when the Job name or the IP is reused.
    """
    out = {}
    for pod in pods:
        status, meta = getattr(pod, 'status', None), getattr(pod, 'metadata', None)
        ip = getattr(status, 'pod_ip', None)
        if getattr(status, 'phase', None) != 'Running' or not ip or meta is None:
            continue
        name = getattr(meta, 'name', None)
        identity = getattr(meta, 'uid', None) or name
        if identity and name:
            out[str(identity)] = (str(name), str(ip))
    return out


async def sweep(targets):
    """{'up','down','unknown'} for the whole fleet, bounded by one deadline.

    Deliberately not a TaskGroup: a TaskGroup cancels its siblings when one task
    raises, which is the opposite of what a fleet sweep wants -- one unreachable
    pod must not discard the other 1023 answers. asyncio.wait keeps whatever
    finished and cancels only the stragglers, so an unreachable fleet costs one
    deadline rather than the sum of its timeouts.
    """
    if not targets:
        return dict(EMPTY)
    counts = {'up': 0, 'down': 0, 'unknown': len(targets)}
    # force_close: a pooled socket to a vanished pod gets handed back out.
    # `limit` is the concurrency bound; a semaphore would double-enforce it.
    connector = aiohttp.TCPConnector(limit=config.LIVENESS_MAX_CONCURRENCY,
                                     force_close=True)
    timeout = aiohttp.ClientTimeout(total=config.LIVENESS_PROBE_TIMEOUT_SECONDS)
    async with aiohttp.ClientSession(connector=connector, timeout=timeout) as session:
        tasks = [asyncio.create_task(_probe(session, ip)) for _, ip in targets.values()]
        done, pending = await asyncio.wait(tasks, timeout=config.LIVENESS_SWEEP_SECONDS)
        for task in pending:
            task.cancel()
        if pending:
            await asyncio.gather(*pending, return_exceptions=True)
        for task in done:
            try:
                up = task.result()
            except Exception:
                up = False        # timeout, refused, DNS, malformed response
            counts['up' if up else 'down'] += 1
            counts['unknown'] -= 1
    return counts


async def _probe(session, ip):
    host = f"[{ip}]" if ':' in ip else ip
    async with session.get(f"http://{host}:{_ADMIN_PORT}/info") as resp:
        return resp.status == 200


async def publish(pods):
    """One sweep, never fatal. Liveness is observability and may not stop a run."""
    found = targets(pods)
    if not found:
        return dict(EMPTY)
    try:
        return await sweep(found)
    except Exception as e:
        logger.warning("liveness sweep failed (%s); reporting all workers unknown", e)
        return {'up': 0, 'down': 0, 'unknown': len(found)}
