"""The monitor's HTTP surface, on the same event loop as the reconcile pass.

/start   begin a run (the range spec and an optional profile)
/status  the counts the driver decides on
/logs    the volume, because nothing else can read it
/prometheus, /healthz
"""
import logging
import os

from aiohttp import web
from prometheus_client import CONTENT_TYPE_LATEST, generate_latest

import config
import monitor_config as mc
import record

logger = logging.getLogger('job_monitor')


def build(state):
    app = web.Application()
    app['state'] = state
    app.add_routes([
        web.post('/start', _start),
        web.get('/status', _status),
        web.get('/logs', _logs),
        web.get('/logs/{name}', _log_file),
        web.get('/prometheus', _prometheus),
        web.get('/healthz', _healthz),
    ])
    return app


async def serve(state, stop):
    """Serve until `stop` is set, then drain.

    /status must answer for the whole life of the process, including while a
    reconcile pass is in flight -- the driver reads it to decide whether the
    mission is still alive.
    """
    runner = web.AppRunner(build(state), access_log=None)
    await runner.setup()
    site = web.TCPSite(runner, '0.0.0.0', mc.HTTP_PORT)
    await site.start()
    logger.info("listening on :%d", mc.HTTP_PORT)
    try:
        await stop.wait()
    finally:
        await runner.cleanup()


async def _start(request):
    """Apply the /start document. A ValueError is answered 400.

    Validated at the first moment the configuration is complete -- the profile
    arrives with this POST -- so a misconfigured run is rejected here rather
    than crash-looping a pod the driver can only time out on.
    """
    state = request.app['state']
    try:
        doc = await request.json()
    except Exception:
        raise web.HTTPBadRequest(text="body is not JSON")
    try:
        state.start(doc)
    except ValueError as e:
        logger.warning("rejected /start: %s", e)
        raise web.HTTPBadRequest(text=str(e))
    # Only after it validates, and only here: resume() replays what is already
    # on disk.
    record.save_run(doc)
    return web.json_response({'ranges': len(state.ranges),
                              'profile': len(mc.PROFILE)})


async def _status(request):
    return web.json_response(request.app['state'].status())


async def _logs(request):
    # A bare array, not an object: the driver parses the body as a JArray.
    return web.json_response(record.manifest())


async def _log_file(request):
    name = request.match_info['name']
    # basename, so a traversal cannot reach off the volume.
    path = os.path.join(config.LOG_DIR, os.path.basename(name))
    if not os.path.isfile(path):
        raise web.HTTPNotFound(text=f"no such artifact: {name}")
    return web.FileResponse(path)


async def _prometheus(request):
    # Through headers: aiohttp rejects the charset that media type carries.
    return web.Response(body=generate_latest(),
                        headers={'Content-Type': CONTENT_TYPE_LATEST})


async def _healthz(request):
    return web.Response(text='ok')
