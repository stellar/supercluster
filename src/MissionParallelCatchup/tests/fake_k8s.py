"""An in-memory stand-in for the slice of the Kubernetes API job_monitor uses.

Not a general-purpose mock. It implements exactly the calls the monitor makes,
with the behaviours the monitor *branches on*:

  * 409 AlreadyExists on a duplicate create -- dispatch and the retry path both
    swallow 409 and treat name uniqueness as the mutex, so a fake that silently
    overwrote would hide the only thing those handlers do.
  * 404 NotFound on a missing read -- load_progress(), ensure_pvc(),
    _patch_cm() and read_mission_start() all key on it.
  * a Job create materialises a Pod, the way the Job controller does, because
    pods_by_job() is how reconcile finds the object that carries the exit code.

Objects are the real kubernetes.client models, so attribute access in the
monitor (`job.status.succeeded`, `pod.status.container_statuses[0].state
.terminated.exit_code`) is exercised rather than duck-typed around.

Reads and lists return deep copies: the API server hands out snapshots, and a
test that mutated a listed object would otherwise be writing to the store.
"""

import copy
from datetime import datetime, timedelta, timezone

from kubernetes import client
from kubernetes.client.rest import ApiException

JOB_NAME_LABEL = 'batch.kubernetes.io/job-name'


def _now():
    return datetime.now(timezone.utc)


def api_exception(status, reason, message=''):
    """A real ApiException; the monitor reads .status and .reason off it."""
    e = ApiException(status=status, reason=reason)
    e.body = message or reason
    return e


def _not_found(kind, name):
    return api_exception(404, 'Not Found', f'{kind} "{name}" not found')


def _already_exists(kind, name):
    return api_exception(409, 'Conflict', f'{kind} "{name}" already exists')


def _match_selector(labels, selector):
    """Equality-based label selectors: "a=b,c!=d". Enough for this monitor."""
    if not selector:
        return True
    labels = labels or {}
    for term in selector.split(','):
        term = term.strip()
        if not term:
            continue
        if '!=' in term:
            key, value = term.split('!=', 1)
            if labels.get(key.strip()) == value.strip():
                return False
        elif '=' in term:
            key, value = term.split('=', 1)
            if labels.get(key.strip()) != value.strip():
                return False
        elif term not in labels:
            return False
    return True


def _match_fields(pod, selector):
    if not selector:
        return True
    for term in selector.split(','):
        key, _, value = term.partition('=')
        key, value = key.strip(), value.strip()
        if key == 'status.phase':
            if (pod.status.phase if pod.status else None) != value:
                return False
        elif key == 'metadata.name':
            if pod.metadata.name != value:
                return False
    return True


class Call:
    """One API call, as recorded for assertions."""

    __slots__ = ('verb', 'kind', 'name', 'namespace')

    def __init__(self, verb, kind, name, namespace):
        self.verb, self.kind, self.name, self.namespace = verb, kind, name, namespace

    def __iter__(self):          # lets a test write (verb, kind, name) tuples
        return iter((self.verb, self.kind, self.name))

    def __eq__(self, other):
        if isinstance(other, Call):
            return tuple(self) == tuple(other)
        return tuple(self) == tuple(other)

    def __hash__(self):
        return hash(tuple(self))

    def __repr__(self):
        return f"{self.verb} {self.kind}/{self.name}"


class CallLog(list):
    def record(self, verb, kind, name, namespace):
        self.append(Call(verb, kind, name, namespace))

    def names(self, verb=None, kind=None):
        """Names touched, in order -- the usual assertion."""
        return [c.name for c in self
                if (verb is None or c.verb == verb) and (kind is None or c.kind == kind)]

    def verbs(self, kind=None):
        return [c.verb for c in self if kind is None or c.kind == kind]

    def of(self, kind):
        return [c for c in self if c.kind == kind]

    def __repr__(self):
        return "[" + ", ".join(repr(c) for c in self) + "]"


class FakeCluster:
    """Holds the objects and hands out the two API facades.

    cluster.core_v1 / cluster.batch_v1 are what get monkeypatched into
    job_monitor; everything else on here is for the test to drive and inspect.
    """

    def __init__(self, namespace='default'):
        self.namespace = namespace
        self.jobs = {}            # (ns, name) -> V1Job
        self.pods = {}            # (ns, name) -> V1Pod
        self.pvcs = {}            # (ns, name) -> V1PersistentVolumeClaim
        self.config_maps = {}     # (ns, name) -> V1ConfigMap
        self.pod_logs = {}        # (ns, name) -> str
        self.calls = CallLog()
        # Deleted names, in order, so a test can assert a reap happened even
        # after the object is gone from the dicts.
        self.deleted = CallLog()
        self._pod_seq = 0
        # Set to an ApiException factory to make the next matching call fail;
        # keyed by "verb kind", e.g. {'create job': api_exception(500, 'boom')}.
        self.fail_next = {}
        self.core_v1 = FakeCoreV1Api(self)
        self.batch_v1 = FakeBatchV1Api(self)

    # -- internals -----------------------------------------------------------

    def _key(self, namespace, name):
        return (namespace, name)

    def _maybe_fail(self, verb, kind):
        exc = self.fail_next.pop(f"{verb} {kind}", None)
        if exc is not None:
            raise exc

    def _record(self, verb, kind, name, namespace):
        self._maybe_fail(verb, kind)
        self.calls.record(verb, kind, name, namespace)

    # -- seeding -------------------------------------------------------------

    def add_config_map(self, name, data=None, namespace=None, uid=None):
        ns = namespace or self.namespace
        cm = client.V1ConfigMap(
            metadata=client.V1ObjectMeta(name=name, namespace=ns,
                                         uid=uid or f"uid-{name}"),
            data=dict(data or {}))
        self.config_maps[self._key(ns, name)] = cm
        return cm

    # -- inspection ----------------------------------------------------------

    def job(self, name, namespace=None):
        return self.jobs[self._key(namespace or self.namespace, name)]

    def pod(self, name, namespace=None):
        return self.pods[self._key(namespace or self.namespace, name)]

    def pod_for_job(self, job_name, namespace=None):
        """The Pod the fake created for this Job, or None once it is reaped."""
        ns = namespace or self.namespace
        for (pod_ns, _), pod in self.pods.items():
            if pod_ns != ns:
                continue
            if (pod.metadata.labels or {}).get(JOB_NAME_LABEL) == job_name:
                return pod
        return None

    def job_names(self, namespace=None):
        ns = namespace or self.namespace
        return sorted(name for (pod_ns, name) in self.jobs if pod_ns == ns)

    def pvc_names(self, namespace=None):
        ns = namespace or self.namespace
        return sorted(name for (pvc_ns, name) in self.pvcs if pvc_ns == ns)

    def config_map_data(self, name, namespace=None):
        cm = self.config_maps.get(self._key(namespace or self.namespace, name))
        return dict(cm.data or {}) if cm is not None else None

    # -- Job controller emulation -------------------------------------------

    def _spawn_pod(self, namespace, job):
        self._pod_seq += 1
        name = f"{job.metadata.name}-{self._pod_seq:05d}"
        labels = dict((job.spec.template.metadata.labels or {})
                      if job.spec and job.spec.template and job.spec.template.metadata
                      else {})
        labels[JOB_NAME_LABEL] = job.metadata.name
        labels['job-name'] = job.metadata.name
        pod = client.V1Pod(
            metadata=client.V1ObjectMeta(
                name=name, namespace=namespace, labels=labels,
                owner_references=[client.V1OwnerReference(
                    api_version='batch/v1', kind='Job', name=job.metadata.name,
                    uid=job.metadata.uid or f"uid-{job.metadata.name}",
                    controller=True)]),
            spec=job.spec.template.spec if job.spec and job.spec.template else None,
            status=client.V1PodStatus(phase='Pending', container_statuses=[],
                                      conditions=[]))
        self.pods[self._key(namespace, name)] = pod
        return pod

    # -- state the monitor branches on --------------------------------------

    def set_job_running(self, job_name, namespace=None):
        job = self.job(job_name, namespace)
        job.status = client.V1JobStatus(active=1, start_time=job.status.start_time or _now())
        pod = self.pod_for_job(job_name, namespace)
        if pod is not None:
            self.set_pod_running(pod.metadata.name, namespace=namespace)
        return job

    def set_job_succeeded(self, job_name, namespace=None, seconds=60,
                          start_time=None, completion_time=None):
        job = self.job(job_name, namespace)
        start = start_time or job.status.start_time or (_now() - timedelta(seconds=seconds))
        job.status = client.V1JobStatus(
            succeeded=1, active=0, start_time=start,
            completion_time=completion_time or (start + timedelta(seconds=seconds)))
        return job

    def set_job_failed(self, job_name, namespace=None, reason='PodFailurePolicy',
                       message='', seconds=60, start_time=None):
        """Failed with a Job condition -- the message is what classify_from_job parses."""
        job = self.job(job_name, namespace)
        start = start_time or job.status.start_time or (_now() - timedelta(seconds=seconds))
        conditions = []
        if reason is not None:
            conditions.append(client.V1JobCondition(
                type='Failed', status='True', reason=reason, message=message,
                last_transition_time=_now()))
        job.status = client.V1JobStatus(failed=1, active=0, start_time=start,
                                        conditions=conditions)
        return job

    def set_pod_phase(self, pod_name, phase, namespace=None, reason=None, message=None):
        pod = self.pod(pod_name, namespace)
        pod.status.phase = phase
        if reason is not None:
            pod.status.reason = reason
        if message is not None:
            pod.status.message = message
        return pod

    def set_pod_running(self, pod_name, namespace=None, ip='10.0.0.1', start_time=None):
        pod = self.pod(pod_name, namespace)
        pod.status.phase = 'Running'
        pod.status.pod_ip = ip
        pod.status.start_time = start_time or pod.status.start_time or _now()
        pod.status.container_statuses = [client.V1ContainerStatus(
            name='stellar-core', image='core', image_id='', ready=True,
            restart_count=0, state=client.V1ContainerState(
                running=client.V1ContainerStateRunning(started_at=pod.status.start_time)))]
        return pod

    def set_pod_terminated(self, pod_name, exit_code=0, reason=None, namespace=None,
                           seconds=60, start_time=None, finished_at=None,
                           container='stellar-core', phase=None):
        """Terminal container state: exit code plus OOMKilled/Error reason."""
        pod = self.pod(pod_name, namespace)
        start = start_time or pod.status.start_time or (_now() - timedelta(seconds=seconds))
        pod.status.start_time = start
        pod.status.phase = phase or ('Succeeded' if exit_code == 0 else 'Failed')
        pod.status.container_statuses = [client.V1ContainerStatus(
            name=container, image='core', image_id='', ready=False, restart_count=0,
            state=client.V1ContainerState(terminated=client.V1ContainerStateTerminated(
                exit_code=exit_code,
                reason=reason or ('Completed' if exit_code == 0 else 'Error'),
                started_at=start,
                finished_at=finished_at or (start + timedelta(seconds=seconds)))))]
        return pod

    def set_pod_condition(self, pod_name, cond_type, status='True', namespace=None,
                          reason=None):
        pod = self.pod(pod_name, namespace)
        pod.status.conditions = [c for c in (pod.status.conditions or [])
                                 if c.type != cond_type]
        pod.status.conditions.append(client.V1PodCondition(
            type=cond_type, status=status, reason=reason,
            last_transition_time=_now()))
        return pod

    def set_pod_log(self, pod_name, text, namespace=None):
        self.pod_logs[self._key(namespace or self.namespace, pod_name)] = text

    def delete_pod(self, pod_name, namespace=None):
        """Reap the pod out from under the monitor, the way Karpenter does."""
        self.pods.pop(self._key(namespace or self.namespace, pod_name), None)


class _Api:
    def __init__(self, cluster):
        self._c = cluster


class FakeCoreV1Api(_Api):

    # -- ConfigMaps ----------------------------------------------------------

    def read_namespaced_config_map(self, name, namespace, **_):
        self._c._record('read', 'configmap', name, namespace)
        cm = self._c.config_maps.get((namespace, name))
        if cm is None:
            raise _not_found('configmaps', name)
        return copy.deepcopy(cm)

    def create_namespaced_config_map(self, namespace, body, **_):
        name = body.metadata.name
        self._c._record('create', 'configmap', name, namespace)
        if (namespace, name) in self._c.config_maps:
            raise _already_exists('configmaps', name)
        cm = copy.deepcopy(body)
        cm.metadata.namespace = namespace
        cm.metadata.uid = cm.metadata.uid or f"uid-{name}"
        cm.data = dict(cm.data or {})
        self._c.config_maps[(namespace, name)] = cm
        return copy.deepcopy(cm)

    def patch_namespaced_config_map(self, name, namespace, body, **_):
        self._c._record('patch', 'configmap', name, namespace)
        cm = self._c.config_maps.get((namespace, name))
        if cm is None:
            raise _not_found('configmaps', name)
        data = body.get('data') if isinstance(body, dict) else (body.data or {})
        cm.data = dict(cm.data or {})
        cm.data.update(data or {})
        return copy.deepcopy(cm)

    def replace_namespaced_config_map(self, name, namespace, body, **_):
        self._c._record('replace', 'configmap', name, namespace)
        if (namespace, name) not in self._c.config_maps:
            raise _not_found('configmaps', name)
        cm = copy.deepcopy(body)
        cm.metadata.namespace = namespace
        cm.data = dict(cm.data or {})
        self._c.config_maps[(namespace, name)] = cm
        return copy.deepcopy(cm)

    def delete_namespaced_config_map(self, name, namespace, **_):
        self._c._record('delete', 'configmap', name, namespace)
        if self._c.config_maps.pop((namespace, name), None) is None:
            raise _not_found('configmaps', name)
        self._c.deleted.record('delete', 'configmap', name, namespace)

    def list_namespaced_config_map(self, namespace, label_selector=None, **_):
        self._c._record('list', 'configmap', '', namespace)
        items = [copy.deepcopy(cm) for (ns, _), cm in sorted(self._c.config_maps.items())
                 if ns == namespace and _match_selector(cm.metadata.labels, label_selector)]
        return client.V1ConfigMapList(items=items)

    # -- Pods ----------------------------------------------------------------

    def list_namespaced_pod(self, namespace, label_selector=None, field_selector=None,
                            resource_version=None, **_):
        self._c._record('list', 'pod', '', namespace)
        items = [copy.deepcopy(p) for (ns, _), p in sorted(self._c.pods.items())
                 if ns == namespace
                 and _match_selector(p.metadata.labels, label_selector)
                 and _match_fields(p, field_selector)]
        return client.V1PodList(items=items)

    def read_namespaced_pod(self, name, namespace, **_):
        self._c._record('read', 'pod', name, namespace)
        pod = self._c.pods.get((namespace, name))
        if pod is None:
            raise _not_found('pods', name)
        return copy.deepcopy(pod)

    def read_namespaced_pod_log(self, name, namespace, container=None, tail_lines=None, **_):
        self._c._record('read', 'podlog', name, namespace)
        if (namespace, name) not in self._c.pods:
            raise _not_found('pods', name)
        text = self._c.pod_logs.get((namespace, name), '')
        if tail_lines:
            text = "\n".join(text.splitlines()[-tail_lines:])
        return text

    def delete_namespaced_pod(self, name, namespace, **_):
        self._c._record('delete', 'pod', name, namespace)
        if self._c.pods.pop((namespace, name), None) is None:
            raise _not_found('pods', name)
        self._c.deleted.record('delete', 'pod', name, namespace)

    # -- PersistentVolumeClaims ---------------------------------------------

    def read_namespaced_persistent_volume_claim(self, name, namespace, **_):
        self._c._record('read', 'pvc', name, namespace)
        pvc = self._c.pvcs.get((namespace, name))
        if pvc is None:
            raise _not_found('persistentvolumeclaims', name)
        return copy.deepcopy(pvc)

    def create_namespaced_persistent_volume_claim(self, namespace, body, **_):
        name = body.metadata.name
        self._c._record('create', 'pvc', name, namespace)
        if (namespace, name) in self._c.pvcs:
            raise _already_exists('persistentvolumeclaims', name)
        pvc = copy.deepcopy(body)
        pvc.metadata.namespace = namespace
        pvc.metadata.uid = pvc.metadata.uid or f"uid-{name}"
        pvc.status = client.V1PersistentVolumeClaimStatus(phase='Bound')
        self._c.pvcs[(namespace, name)] = pvc
        return copy.deepcopy(pvc)

    def delete_namespaced_persistent_volume_claim(self, name, namespace, **_):
        self._c._record('delete', 'pvc', name, namespace)
        if self._c.pvcs.pop((namespace, name), None) is None:
            raise _not_found('persistentvolumeclaims', name)
        self._c.deleted.record('delete', 'pvc', name, namespace)

    def list_namespaced_persistent_volume_claim(self, namespace, label_selector=None, **_):
        self._c._record('list', 'pvc', '', namespace)
        items = [copy.deepcopy(p) for (ns, _), p in sorted(self._c.pvcs.items())
                 if ns == namespace and _match_selector(p.metadata.labels, label_selector)]
        return client.V1PersistentVolumeClaimList(items=items)


class FakeBatchV1Api(_Api):

    def create_namespaced_job(self, namespace, body, **_):
        name = body.metadata.name
        self._c._record('create', 'job', name, namespace)
        if (namespace, name) in self._c.jobs:
            # Name uniqueness is the monitor's dispatch mutex; it swallows this.
            raise _already_exists('jobs.batch', name)
        job = copy.deepcopy(body)
        job.metadata.namespace = namespace
        job.metadata.uid = job.metadata.uid or f"uid-{name}"
        # The apiserver assigns this synchronously on every create, so a Job
        # without one cannot exist. status.start_time is set by the Job
        # controller instead, i.e. after the create response -- but the harness
        # has no controller loop, so it stands in for one here.
        job.metadata.creation_timestamp = job.metadata.creation_timestamp or _now()
        job.status = client.V1JobStatus(active=0, start_time=_now())
        self._c.jobs[(namespace, name)] = job
        self._c._spawn_pod(namespace, job)
        return copy.deepcopy(job)

    def read_namespaced_job(self, name, namespace, **_):
        self._c._record('read', 'job', name, namespace)
        job = self._c.jobs.get((namespace, name))
        if job is None:
            raise _not_found('jobs.batch', name)
        return copy.deepcopy(job)

    def list_namespaced_job(self, namespace, label_selector=None, field_selector=None, **_):
        self._c._record('list', 'job', '', namespace)
        items = [copy.deepcopy(j) for (ns, _), j in sorted(self._c.jobs.items())
                 if ns == namespace and _match_selector(j.metadata.labels, label_selector)]
        return client.V1JobList(items=items)

    def delete_namespaced_job(self, name, namespace, propagation_policy=None, body=None, **_):
        self._c._record('delete', 'job', name, namespace)
        if self._c.jobs.pop((namespace, name), None) is None:
            raise _not_found('jobs.batch', name)
        self._c.deleted.record('delete', 'job', name, namespace)
        # Background/Foreground both reap the pods; Orphan is the only one that
        # does not, and the monitor never asks for it.
        if propagation_policy != 'Orphan':
            for key in [k for k, p in self._c.pods.items()
                        if k[0] == namespace
                        and (p.metadata.labels or {}).get(JOB_NAME_LABEL) == name]:
                self._c.pods.pop(key, None)

    def patch_namespaced_job(self, name, namespace, body, **_):
        self._c._record('patch', 'job', name, namespace)
        job = self._c.jobs.get((namespace, name))
        if job is None:
            raise _not_found('jobs.batch', name)
        return copy.deepcopy(job)

    def replace_namespaced_job(self, name, namespace, body, **_):
        self._c._record('replace', 'job', name, namespace)
        if (namespace, name) not in self._c.jobs:
            raise _not_found('jobs.batch', name)
        job = copy.deepcopy(body)
        job.metadata.namespace = namespace
        self._c.jobs[(namespace, name)] = job
        return copy.deepcopy(job)
