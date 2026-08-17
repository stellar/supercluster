"""Everything that talks to the apiserver, on the aiohttp-backed aio client.

kubernetes>=36 ships kubernetes.aio: the REST layer is genuinely async and the
generated methods return the coroutine, so `await core_v1.list_namespaced_pod()`
is real non-blocking I/O and nothing here needs a thread.

Two traps in that client:

  * the generated docstrings are stale boilerplate from the sync generator --
    they claim "makes a synchronous HTTP request" and advertise async_req=True.
    async_req routes through a ThreadPool and returns an AsyncResult, not an
    awaitable, so it must never be used here.
  * load_incluster_config is sync while load_kube_config is a coroutine.

The rest of the monitor takes Jobs and pods as plain data, so a fake cluster in
a test replaces this module and no decision function needs a client at all.
"""
import asyncio
import contextlib
import logging

from kubernetes.aio import client, config as kube_config
from kubernetes.aio.client import ApiException

import config
import monitor_config as mc

logger = logging.getLogger('job_monitor')

batch_v1 = None
core_v1 = None
_api = None
_slots = None
_owner = None


@contextlib.asynccontextmanager
async def session():
    """Own the ApiClient for the life of the process.

    One client, one connection pool. Creating them per call would open a new
    aiohttp session per request and leak connectors on every pass.
    """
    global batch_v1, core_v1, _api, _slots
    try:
        kube_config.load_incluster_config()
    except Exception:
        await kube_config.load_kube_config()
    _slots = asyncio.Semaphore(mc.APISERVER_CONCURRENCY)
    async with client.ApiClient() as api:
        _api, batch_v1, core_v1 = api, client.BatchV1Api(api), client.CoreV1Api(api)
        try:
            yield
        finally:
            batch_v1 = core_v1 = _api = None


def _selector():
    return f"{config.LABEL_RUN}={config.RUN_NAME}"


async def snapshot():
    """This run's Jobs by range, and its pods by owning Job.

    Both lists in flight together, and one of each per pass: a range's state
    must not be assembled from two different moments.
    """
    jobs_raw, pods_raw = await asyncio.gather(
        batch_v1.list_namespaced_job(config.NAMESPACE, label_selector=_selector()),
        core_v1.list_namespaced_pod(config.NAMESPACE, label_selector=_selector()))

    jobs = {}
    for job in jobs_raw.items:
        end = (job.metadata.labels or {}).get(config.LABEL_RANGE)
        if end is not None:
            jobs.setdefault(str(end), []).append(job)

    pods = {}
    for pod in pods_raw.items:
        owner = next((o.name for o in (pod.metadata.owner_references or [])
                      if o.kind == 'Job'), None)
        if owner is not None:
            pods[owner] = pod
    return jobs, pods


async def owner_ref():
    """The run's ConfigMap, so deleting the release collects everything.

    Read once and cached: it is the same object for the life of the run.
    """
    global _owner
    if _owner is None:
        cm = await core_v1.read_namespaced_config_map(
            f"{config.RUN_NAME}-stellar-core-config", config.NAMESPACE)
        _owner = [client.V1OwnerReference(
            api_version='v1', kind='ConfigMap', name=cm.metadata.name,
            uid=cm.metadata.uid, block_owner_deletion=True)]
    return _owner


async def create_job(body):
    """Create, treating AlreadyExists as success.

    The Job name carries range and attempt, so name uniqueness IS the mutex: a
    409 proves another pass already dispatched this attempt, which is the
    outcome that was wanted.
    """
    async with _slots:
        try:
            return await batch_v1.create_namespaced_job(config.NAMESPACE, body)
        except ApiException as e:
            if e.status != 409:
                raise
            return None


async def ensure_pvc(end, owner):
    name = f"{config.RUN_NAME}-data-r{end}"
    async with _slots:
        try:
            await core_v1.read_namespaced_persistent_volume_claim(name, config.NAMESPACE)
            return name
        except ApiException as e:
            if e.status != 404:
                raise
        spec = client.V1PersistentVolumeClaimSpec(
            access_modes=['ReadWriteOnce'],
            resources=client.V1VolumeResourceRequirements(
                requests={'storage': mc.STORAGE_SIZE}))
        if mc.STORAGE_CLASS:
            spec.storage_class_name = mc.STORAGE_CLASS
        try:
            await core_v1.create_namespaced_persistent_volume_claim(
                config.NAMESPACE, client.V1PersistentVolumeClaim(
                    metadata=client.V1ObjectMeta(
                        name=name, owner_references=owner,
                        labels={config.LABEL_RUN: config.RUN_NAME,
                                config.LABEL_RANGE: str(end)}),
                    spec=spec))
        except ApiException as e:
            if e.status != 409:
                raise
    return name


async def reap(end, job_names):
    """Delete every Job this range has, then release its volume.

    Every Job, not just the winner: an earlier failed attempt left its own, and
    leaving it behind holds a PVC and shows up in the next pass's list.
    """
    await asyncio.gather(*(delete_job(name) for name in job_names))
    if config.STORAGE_MODE == 'pvc':
        await _release_pvc(end)


async def delete_job(name):
    async with _slots:
        try:
            await batch_v1.delete_namespaced_job(name, config.NAMESPACE,
                                                 propagation_policy='Background')
            return True
        except ApiException as e:
            if e.status != 404:
                logger.warning("could not delete job %s: %s", name, e)
            return False


async def _release_pvc(end):
    async with _slots:
        try:
            await core_v1.delete_namespaced_persistent_volume_claim(
                f"{config.RUN_NAME}-data-r{end}", config.NAMESPACE)
            return True
        except ApiException as e:
            if e.status != 404:
                logger.warning("could not release pvc for range %s: %s", end, e)
            return False
