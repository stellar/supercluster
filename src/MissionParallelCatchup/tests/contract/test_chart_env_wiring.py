"""The rendered Deployment against the env vars each process actually reads.

Two containers share one pod and one volume but not one env block, so a
variable the monitor has is not automatically one the collector has. STORAGE_MODE
was missing from the collector and the peak-ephemeral sampler silently recorded
nothing -- it defaults to 'pvc', which is exactly the mode where the sampler is
supposed to stand down.

The conditional blocks matter as much as the unconditional ones: node targeting,
taint toleration and the profile mount only render when the mission passes the
matching values, so "the chart sets it" has to be checked with those values
present.
"""

from pathlib import Path

import config
import job_monitor as jm
import log_collector as lc

import _artifacts as art

# Injected by the kubelet or genuinely optional. Everything else the code reads
# has to come from the chart, or it silently runs on its built-in fallback.
KUBELET_INJECTED = {'KUBERNETES_SERVICE_HOST', 'KUBERNETES_SERVICE_PORT'}

# Operator-facing switches with no chart key on purpose: they are set by hand on
# a running Deployment when something needs debugging, and a chart key would
# freeze them at install time.
DEBUG_ONLY = {'LOGGING_LEVEL', 'CONNECTION_POOL', 'WORKER_CONTAINER'}

# Rendered with everything the mission can send, so the conditional blocks are
# present: a profile ConfigMap, a required node label, an avoided node label
# and a tolerated taint. avoidNodeLabels was declared in values.yaml and read
# by no template at all -- absent from here, that stays invisible.
FULL = (
    'monitor.profileConfigMap=p',
    'worker.requireNodeLabels[0].key=purpose',
    'worker.requireNodeLabels[0].operator=In',
    'worker.requireNodeLabels[0].values[0]=catchup8-spot',
    'worker.avoidNodeLabels[0].key=reserved',
    'worker.avoidNodeLabels[0].operator=NotIn',
    'worker.avoidNodeLabels[0].values[0]=true',
    'worker.tolerateNodeTaints[0].key=catchup8-spot',
    'worker.tolerateNodeTaints[0].effect=NoSchedule',
)


def _missing(container_name, module):
    reads = art.reads_env(art.module_source(module))
    set_by_chart = set(art.env_of(art.containers(FULL)[container_name]))
    return sorted(reads - set_by_chart - KUBELET_INJECTED - DEBUG_ONLY)


def test_every_env_the_monitor_reads_is_set_on_the_monitor_container():
    missing = _missing(art.MONITOR_CONTAINER, jm)
    assert not missing, f"the monitor reads {missing} but the chart never sets them"


def test_every_env_the_collector_reads_is_set_on_the_collector_container():
    missing = _missing(art.COLLECTOR_CONTAINER, lc)
    assert not missing, f"the collector reads {missing} but the chart never sets them"


def test_liveness_sweep_settings_reach_only_the_monitor():
    monitor = art.env_of(art.containers()[art.MONITOR_CONTAINER])
    collector = art.env_of(art.containers()[art.COLLECTOR_CONTAINER])
    expected = {
        'LIVENESS_PROBE_TIMEOUT_SECONDS': '5',
        'LIVENESS_SWEEP_SECONDS': '15',
        'LIVENESS_MAX_CONCURRENCY': '32',
    }
    assert {name: monitor.get(name) for name in expected} == expected
    assert not set(expected) & set(collector)


def test_the_node_targeting_the_mission_sends_reaches_the_monitor():
    """A label/taint the mission passes must arrive as env, not just as YAML.

    The monitor puts the affinity on the WORKER pods it builds; the chart's job
    is only to hand it the pair. Rendering the values into some other shape --
    or into the Deployment's own nodeSelector -- would place the monitor and
    leave every worker unconstrained.
    """
    env = art.env_of(art.containers(FULL)[art.MONITOR_CONTAINER])
    assert env.get('NODE_LABEL_KEY') == 'purpose'
    assert env.get('NODE_LABEL_VALUE') == 'catchup8-spot'
    assert env.get('TOLERATE_TAINT') == 'catchup8-spot'


def test_node_targeting_is_absent_rather_than_empty_when_unset():
    """An empty NODE_LABEL_KEY is how the monitor knows not to constrain a pod.

    Setting it to "" would work by accident today, but the guard the monitor
    uses is truthiness of the key, so an empty-string env and an unset env must
    stay interchangeable -- and the chart should not emit a knob it is not
    configuring.
    """
    env = art.env_of(art.containers()[art.MONITOR_CONTAINER])
    for name in ('NODE_LABEL_KEY', 'NODE_LABEL_VALUE', 'TOLERATE_TAINT'):
        assert env.get(name, '') == '', f"{name} rendered without a value to carry"
    assert art.defaults('config')['NODE_LABEL_KEY'] == '', \
        "the code fallback must be the falsy 'no targeting' value"


def test_the_two_containers_run_the_same_image_from_one_build():
    """The monitor and the collector are two entrypoints in one image.

    They share file formats on a shared volume, so shipping them from separate
    images would let the pair skew by a release -- which is the failure every
    cross-process test in this directory exists to prevent.
    """
    cs = art.containers(FULL)
    assert (cs[art.MONITOR_CONTAINER]['image']
            == cs[art.COLLECTOR_CONTAINER]['image'])


def test_the_collector_is_started_as_the_collector():
    """Same image, so the collector needs an explicit entrypoint.

    Without one it runs the image's default command -- a second job_monitor,
    which would be a second writer of progress.json and every Job.
    """
    collector = art.containers(FULL)[art.COLLECTOR_CONTAINER]
    started = " ".join(collector.get('command', []) + collector.get('args', []))
    assert 'log_collector.py' in started, (
        f"the collector container does not run log_collector.py: {started!r}")
    monitor = art.containers(FULL)[art.MONITOR_CONTAINER]
    monitor_started = " ".join(monitor.get('command', []) + monitor.get('args', []))
    assert 'log_collector.py' not in monitor_started


def test_only_one_monitor_ever_runs():
    """Single writer is what removes the claim/requeue races the redis queue had.

    Two replicas -- or a rolling update that briefly overlaps them -- would give
    two processes the same progress.json, the same Job names and the same PVCs,
    with no leader election anywhere in the monitor.
    """
    spec = art.monitor_deployment(FULL)['spec']
    assert spec['replicas'] == 1
    assert spec['strategy']['type'] == 'Recreate', (
        "a RollingUpdate briefly runs two monitors against one progress record")


def test_the_run_name_the_monitor_labels_with_is_the_helm_release():
    """Every Job, PVC and ConfigMap this run owns is found by that label.

    Two releases in one namespace is the normal case on a shared test cluster.
    If RUN_NAME were not the release name, one release's reconcile would list
    the other's Jobs and reap them.
    """
    env = art.env_of(art.containers(release='pc-abc')[art.MONITOR_CONTAINER])
    assert env['RUN_NAME'] == 'pc-abc'
    collector = art.env_of(art.containers(release='pc-abc')[art.COLLECTOR_CONTAINER])
    assert collector['RUN_NAME'] == 'pc-abc', \
        "the collector would watch a different run's pods"
    assert config.LABEL_RUN == config.LABEL_RUN, \
        "the two processes select on different label keys"


def test_the_namespace_comes_from_the_pod_not_from_a_value():
    """helm --namespace and a values key can disagree; the downward API cannot.

    The monitor creates Jobs in NAMESPACE. A stale value there would dispatch a
    whole run into a namespace the release does not own.
    """
    for name in (art.MONITOR_CONTAINER, art.COLLECTOR_CONTAINER):
        entry = [e for e in art.containers(FULL)[name]['env']
                 if e['name'] == 'NAMESPACE']
        assert entry, f"{name} has no NAMESPACE"
        field = entry[0]['valueFrom']['fieldRef']['fieldPath']
        assert field == 'metadata.namespace', f"{name} reads NAMESPACE from {field}"


def test_no_container_declares_the_same_env_var_twice():
    """A duplicate env entry is rejected by the API server, not by helm.

    `helm template` renders duplicates happily and every reader here collapses
    env into a dict, so a merge that lands the same block twice looks fine right
    up until `helm install`, which fails with

        .spec.template.spec.containers[name="job-monitor"].env:
            duplicate entries for key [name="LIVENESS_PROBE_INTERVAL_SECONDS"]

    and leaves a half-created release behind. That is exactly what happened
    merging the liveness sampler in on 2026-07-31: both branches carried the
    block, in different positions, so neither side conflicted.

    Rendered with FULL so the conditional blocks are present too -- a duplicate
    that only appears when a profile ConfigMap is mounted is still a duplicate.
    """
    for values in ((), FULL):
        for name, container in art.containers(values).items():
            seen = [e['name'] for e in (container.get('env') or [])]
            dupes = sorted({n for n in seen if seen.count(n) > 1})
            assert not dupes, (
                f"container {name} declares {dupes} more than once "
                f"(values={'FULL' if values else 'defaults'}); "
                "the API server rejects the Deployment outright")


def test_the_chart_ships_a_coherent_pool_ladder():
    """The ladder must be defined even though pooling ships OFF.

    poolPrefix empty is deliberate -- pool routing is opt-in, and an unset
    prefix is exactly the pre-tier behaviour. But the ladder itself has to be
    present and well-formed, because the mission turns pooling on by setting
    only the prefix: a malformed or out-of-order poolTiers would then route
    ranges to tiers whose nodes cannot hold them, which is an OOM per range
    rather than a slow run.

    Cuts must ascend. A descending pair would make an earlier tier shadow a
    later one and every range past the inversion would land one tier too low --
    measured consequence: a 13.75Gi range on a 14.1Gi node OOMKilled during
    bucket-apply, before closing a single ledger.
    """
    env = art.env_of(art.containers(FULL)[art.MONITOR_CONTAINER])
    tiers = env.get('POOL_TIERS')
    assert tiers, "the chart ships no pool ladder; enabling poolPrefix would route nowhere"
    parsed = []
    for item in tiers.split(','):
        cut, _, name = item.rpartition(':')
        assert name, f"tier entry with no name: {item!r}"
        parsed.append((float(cut) if cut else float('inf'), name))
    cuts = [c for c, _ in parsed]
    assert cuts == sorted(cuts), f"pool cuts out of order: {tiers}"
    assert cuts[-1] == float('inf'), (
        "the last tier must be unbounded, or a range above the final cut has "
        f"nowhere to go: {tiers}")
    # Every tier that can be routed to needs a cpu claim, or the pod keeps the
    # flat REQ_CPU and two of them can share a node -- which defeats the whole
    # design: isolating a pod from its neighbours raised throughput 29-92%.
    claims = dict(item.split(':') for item in env['POOL_CPU'].split(','))
    for _, name in parsed:
        assert name in claims, f"tier {name} has no cpu claim in POOL_CPU"
    for extra in (env['POOL_UNPROFILED'], env['POOL_NO_PROFILE']):
        assert extra in claims, f"off-ladder pool {extra} has no cpu claim"


def test_neither_module_defines_the_same_symbol_twice():
    """A merge can land the same block twice and Python will not complain.

    Both branches carried the worker-liveness subsystem, positioned differently,
    so git merged them into two byte-identical copies of _worker_targets,
    WorkerLivenessSampler and publish_worker_liveness -- 285 lines that shipped
    in the monitor and were never executed, because the later definition binds.

    Nothing catches this on its own: it imports, it renders, it runs. The only
    reason it was benign is that the copies happened to be identical; had the
    merge taken one edited copy and one stale one, the stale one would silently
    have won or lost depending on file order.

    Assignments count too, and only because this test missed one: the same merge
    left two `worker_liveness_sampler = WorkerLivenessSampler()` lines with a
    function between them. The first instance was constructed and thrown away --
    inert only because __init__ starts no thread.
    """
    import ast, collections
    for f in sorted(list(Path(art.APPS_DIR).glob('*.py'))
                    + list(Path(art.LIB_DIR).glob('*.py'))):
        tree = ast.parse(f.read_text())
        seen = collections.defaultdict(list)
        for node in tree.body:
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                seen[node.name].append(False)   # a def is never a coercion
            elif isinstance(node, ast.Assign) and len(node.targets) == 1 \
                    and isinstance(node.targets[0], ast.Name):
                name = node.targets[0].id
                # `X = int(X)` is a coercion of the value above it, not a second
                # definition -- config.py does this to every liveness knob.
                coercion = any(isinstance(x, ast.Name) and x.id == name
                               for x in ast.walk(node.value))
                seen[name].append(coercion)
        dupes = sorted(n for n, hits in seen.items()
                       if len(hits) > 1 and not all(hits[1:]))
        assert not dupes, \
            f"{f.name} defines {dupes} more than once; the later one silently wins"


def test_the_chart_never_pins_an_absolute_interpreter_path():
    """A container command must resolve python on PATH, not at /usr/bin.

    Dockerfile.jobmonitor builds on python:3.12-slim, which ships the
    interpreter at /usr/local/bin/python3. `/usr/bin/python3` shipped in the
    collector's command and failed as StartError -- and the failure is
    asymmetric, so it hides: the monitor container inherits the image CMD and
    comes up healthy while only the sidecar crashloops, which reads as a sidecar
    bug rather than a chart/base-image mismatch. Observed on ssc-test
    2026-08-07, 3 restarts before it was caught.
    """
    chart = Path(__file__).resolve().parents[2] / 'parallel_catchup_helm'
    for path in chart.rglob('*.yaml'):
        for n, line in enumerate(path.read_text().splitlines(), 1):
            if 'command:' not in line or line.lstrip().startswith('#'):
                continue
            assert '/usr/bin/python' not in line and '/usr/local/bin/python' not in line, (
                f"{path.name}:{n} pins an absolute interpreter path, which ties "
                f"the chart to one base image: {line.strip()}")
