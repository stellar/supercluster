"""The Role the chart grants against the API calls the two processes make.

This boundary has failed twice, the same way both times, and both times it was
silent: the Role omitted `delete` on persistentvolumeclaims, so every completed
range logged a 403 warning and leaked its 40Gi volume (measured on ssc-test:
2032 bound PVCs and 79 TiB a third of the way through a 3982-range run, heading
for ~156 TiB -- enough to crash the EBS CSI controller); and it omitted `delete`
on jobs, so nothing reaped a finished Job and the dead ones outnumbered the live
ones within the hour.

A 403 does not stop the run. That is the whole problem, and it is why this is
checked statically rather than waiting for a cluster to tell us.

The required verbs are DERIVED from the calls in the source, not listed here:
adding a new call to the monitor must fail this test until the Role catches up.
"""

import re

import pytest

import job_monitor as jm
import log_collector as lc

import _artifacts as art

# kubernetes-client method names are `<verb>_namespaced_<resource>`. Only the
# mapping from client vocabulary to RBAC vocabulary is spelled out; which calls
# exist is read off the source.
VERB = {'read': 'get', 'list': 'list', 'create': 'create', 'delete': 'delete',
        'patch': 'patch', 'replace': 'update', 'watch': 'watch'}

RESOURCE = {
    'job': ('batch', 'jobs'),
    'pod': ('', 'pods'),
    'pod_log': ('', 'pods/log'),
    'config_map': ('', 'configmaps'),
    'persistent_volume_claim': ('', 'persistentvolumeclaims'),
}

_CALL = re.compile(r"\b(?:core_v1|batch_v1)\.(\w+?)_namespaced_(\w+)\(")


def monitor_calls():
    """{(apiGroup, resource): {verbs}} the monitor's own code needs."""
    need = {}
    for verb, resource in _CALL.findall(art.module_source(jm)):
        assert verb in VERB, f"unmapped client verb {verb!r}"
        assert resource in RESOURCE, f"unmapped client resource {resource!r}"
        need.setdefault(RESOURCE[resource], set()).add(VERB[verb])
    return need


def test_the_source_really_does_call_the_apiserver():
    """Guards the derivation itself.

    If the call regex stopped matching -- a rename, a wrapper, a different
    client object -- every assertion below would pass vacuously while granting
    nothing.
    """
    need = monitor_calls()
    assert len(need) >= 4, f"only found {sorted(need)}; the call scan has gone blind"
    assert ('batch', 'jobs') in need and ('', 'persistentvolumeclaims') in need


def test_the_role_grants_every_verb_the_monitor_uses():
    have = art.granted()
    missing = []
    for key, verbs in sorted(monitor_calls().items()):
        for verb in sorted(verbs):
            if verb not in have.get(key, set()):
                missing.append(f"{verb} on {key[1]} (Role has {sorted(have.get(key, ()))})")
    assert not missing, (
        "the monitor makes API calls the Role does not allow; each one is a 403 "
        "the run swallows:\n  " + "\n  ".join(missing))


def test_the_role_grants_what_the_collector_reads():
    """The collector shares the monitor's ServiceAccount -- same pod, same SA.

    It talks to the apiserver over raw HTTP rather than the client library, so
    its needs are read out of the URLs it builds.
    """
    source = art.module_source(lc)
    have = art.granted()
    assert re.search(r"/api/v1/namespaces/\{NAMESPACE\}/pods\"", source), \
        "the collector no longer lists pods -- update this test"
    assert 'list' in have[('', 'pods')]
    assert re.search(r"/pods/\{pod\}/log\"", source), \
        "the collector no longer reads pod logs -- update this test"
    assert 'get' in have[('', 'pods/log')]


@pytest.mark.xfail(strict=True, reason=(
    "GAP: the collector's peak sampler GETs /api/v1/nodes/<node>/proxy/stats/summary, "
    "which needs `get` on nodes/proxy -- a CLUSTER-scoped resource that a namespaced "
    "Role cannot carry however it is spelled. The chart ships no ClusterRole, so every "
    "peak this mission profiles from depends on a grant that lives outside this repo. "
    "Where that grant is absent the failure is soft and invisible: sample_kubelet logs "
    "'kubelet stats unavailable' and continues, peakAnonBytes and peakEphemeralBytes "
    "stay empty for the whole run, and the next run's profile looks merely absent "
    "rather than broken. Closing it means a ClusterRole plus binding in the chart"))
def test_the_chart_grants_the_kubelet_stats_read_the_sampler_needs():
    source = art.module_source(lc)
    assert '/nodes/{node}/proxy/stats/summary' in source, \
        "the sampler no longer proxies to the kubelet -- drop this xfail"
    # Cluster-scoped, so a Role cannot carry it however it is spelled.
    cluster_roles = art.of_kind('ClusterRole')
    granted = {(g, r)
               for role in cluster_roles
               for rule in role['rules']
               for g in rule['apiGroups']
               for r in rule['resources']
               if 'get' in rule['verbs']}
    assert ('', 'nodes/proxy') in granted


def test_the_monitor_cannot_touch_a_persistent_volume():
    """Namespaced and PV-free by design.

    Deleting a PVC is reclaim; touching a PV or its finalizers is how an EBS
    volume gets orphaned or a VolumeAttachment gets wedged. The blast radius of
    a bug in this monitor has to stop at the namespace.
    """
    forbidden = {'persistentvolumes', 'nodes', 'volumeattachments'}
    reachable = {resource for (_, resource) in art.granted()}
    assert not (forbidden & reachable), \
        f"the monitor's Role reaches cluster storage: {sorted(forbidden & reachable)}"
    assert not art.of_kind('ClusterRoleBinding'), \
        "a ClusterRoleBinding takes this ServiceAccount outside its namespace"


def test_the_role_is_bound_to_the_service_account_the_monitor_runs_as():
    """A Role nobody is bound to grants nothing, and renders perfectly.

    The worker ServiceAccount is deliberately a different one -- IRSA trust for
    the S3 history mirror is bound to its name -- so "there is a binding" is not
    enough; it has to name the SA on the monitor pod.
    """
    binding = art.of_kind('RoleBinding')
    assert len(binding) == 1, f"expected one RoleBinding, got {len(binding)}"
    binding = binding[0]
    role = art.of_kind('Role')[0]
    assert binding['roleRef']['name'] == role['metadata']['name']
    subjects = {s['name'] for s in binding['subjects'] if s['kind'] == 'ServiceAccount'}
    running_as = art.monitor_deployment()['spec']['template']['spec']['serviceAccountName']
    assert running_as in subjects, (
        f"the monitor runs as {running_as!r} but the Role is bound to {sorted(subjects)}")
    assert running_as in {sa['metadata']['name'] for sa in art.of_kind('ServiceAccount')}


def test_the_worker_service_account_is_not_the_monitors():
    """Workers must not inherit the monitor's Job/PVC/ConfigMap rights.

    A worker is stellar-core running an untrusted history archive's bytes; the
    only credential it needs is IRSA for the S3 mirror.
    """
    env = art.env_of(art.containers()[art.MONITOR_CONTAINER])
    worker_sa = env['WORKER_SERVICE_ACCOUNT']
    monitor_sa = art.monitor_deployment()['spec']['template']['spec']['serviceAccountName']
    assert worker_sa != monitor_sa
    assert worker_sa in {sa['metadata']['name'] for sa in art.of_kind('ServiceAccount')}, \
        "the workers' ServiceAccount is named but never created"
    bound = {s['name'] for b in art.of_kind('RoleBinding') for s in b['subjects']}
    assert worker_sa not in bound, "workers were granted the monitor's Role"
