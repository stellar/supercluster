#!/usr/bin/env python3
"""Run the opt-in collector-restart scenario in the sandbox namespace.

The release name is the isolation boundary. This runner refuses every namespace
except ``sandbox``, renders and validates the chart before installing it, and
only queries or deletes exact release-owned names and labels.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import yaml


HERE = Path(__file__).resolve().parent
MODULE_DIR = HERE.parent
CHART = MODULE_DIR / 'parallel_catchup_helm'
JOB_MONITOR = MODULE_DIR / 'job_monitor.py'
LOG_COLLECTOR = MODULE_DIR / 'log_collector.py'
NAMESPACE = 'sandbox'
RELEASE_RE = re.compile(r'^mpc-resume-[a-z0-9]{6,20}$')
RANGE_END = 64
ATTEMPT_NAMES = {
    1: lambda release: f'{release}-r{RANGE_END}-a1',
    2: lambda release: f'{release}-r{RANGE_END}-a2',
}
SYNTHETIC_PEAKS = {
    'peakAnonBytes': 48 * 1024 * 1024,
    'peakWorkingSetBytes': 56 * 1024 * 1024,
}


class HarnessError(RuntimeError):
    pass


def validate_scope(namespace, release):
    if namespace != NAMESPACE:
        raise HarnessError(f'namespace must be exactly {NAMESPACE!r}')
    if not RELEASE_RE.fullmatch(release):
        raise HarnessError(
            'release must match mpc-resume- plus 6-20 lowercase alphanumerics')


def run(command, *, input_text=None, check=True, timeout=120):
    result = subprocess.run(
        command, input=input_text, capture_output=True, text=True, timeout=timeout)
    if check and result.returncode:
        raise HarnessError(
            f"command failed ({result.returncode}): {' '.join(command)}\n"
            f"{result.stderr.strip()}")
    return result


def kubectl(namespace, *args, check=True, timeout=120, input_text=None):
    command = ['kubectl']
    if namespace:
        command += ['--namespace', namespace]
    command += list(args)
    return run(command, check=check, timeout=timeout, input_text=input_text)


def helm_sets(release, source_config_map, image):
    return [
        f'worker.stellar_core_image={image}',
        'worker.replicas=1',
        'worker.storageMode=pvc',
        'worker.storageSize=1Gi',
        'worker.maxVolumesPerNode=0',
        'worker.resources.requests.cpu=25m',
        'worker.resources.requests.memory=64Mi',
        'worker.resources.limits.cpu=100m',
        'worker.resources.limits.memory=128Mi',
        f'monitor.image={image}',
        f'monitor.sourceConfigMap={source_config_map}',
        'monitor.sourceInstallDependencies=false',
        'monitor.loggingIntervalSeconds=1',
        'monitor.livenessProbeIntervalSeconds=300',
        'monitor.maxAttempts=2',
        'monitor.maxTimeoutAttempts=2',
        'monitor.maxDisruptionAttempts=2',
        'monitor.attemptDeadlineSeconds=240',
        'monitor.jobTtlSeconds=300',
        'monitor.logStorageSize=1Gi',
        'monitor.saveSuccessLogs=true',
        'monitor.collectorPollSeconds=1',
        'monitor.logPollSeconds=1',
        'monitor.maxConcurrentPolls=4',
        'monitor.maxPollChars=1048576',
        'monitor.terminalPollAttempts=3',
        'monitor.collectorResources.requests.cpu=25m',
        'monitor.collectorResources.requests.memory=128Mi',
        'monitor.collectorResources.limits.cpu=250m',
        'monitor.collectorResources.limits.memory=512Mi',
        'monitor.resources.requests.cpu=25m',
        'monitor.resources.requests.memory=128Mi',
        'monitor.resources.limits.cpu=250m',
        'monitor.resources.limits.memory=512Mi',
        'range.generator=uniform',
        'range.startingLedger=0',
        f'range.latestLedgerNum={RANGE_END}',
        f'range.ledgersPerJob={RANGE_END}',
        'range.overlapLedgers=0',
        'integration.syntheticWorker.enabled=true',
        'integration.syntheticWorker.imagePullPolicy=IfNotPresent',
        'integration.syntheticWorker.predecessorSeconds=12',
        'integration.syntheticWorker.successorMinimumSeconds=12',
        'integration.syntheticWorker.maximumWaitSeconds=180',
    ]


def helm_args(sets):
    args = []
    for value in sets:
        args += ['--set', value]
    return args


def inspect_rendered(manifest, release, image):
    docs = [doc for doc in yaml.safe_load_all(manifest) if doc]
    expected_names = {
        f'{release}-job-monitor',
        f'stellar-supercluster-{release}',
        f'{release}-stellar-core-config',
        f'{release}-synthetic-worker',
        f'{release}-job-monitor-logs',
    }
    allowed_kinds = {
        'ServiceAccount', 'ConfigMap', 'PersistentVolumeClaim',
        'Role', 'RoleBinding', 'Deployment',
    }
    names = []
    for doc in docs:
        kind = doc.get('kind')
        name = (doc.get('metadata') or {}).get('name')
        namespace = (doc.get('metadata') or {}).get('namespace')
        if kind not in allowed_kinds:
            raise HarnessError(f'unexpected rendered kind {kind!r}')
        if name not in expected_names:
            raise HarnessError(f'unexpected rendered resource {kind}/{name}')
        if namespace not in (None, NAMESPACE):
            raise HarnessError(f'{kind}/{name} targets namespace {namespace!r}')
        names.append(f'{kind}/{name}')

    deployment = next(doc for doc in docs if doc['kind'] == 'Deployment')
    pod_spec = deployment['spec']['template']['spec']
    if deployment['spec']['replicas'] != 1:
        raise HarnessError('monitor Deployment must have exactly one replica')
    if pod_spec.get('nodeSelector') or pod_spec.get('affinity') or pod_spec.get('tolerations'):
        raise HarnessError('synthetic Deployment must not target or tolerate special nodes')
    containers = {container['name']: container for container in pod_spec['containers']}
    if set(containers) != {'job-monitor', 'log-collector'}:
        raise HarnessError(f'unexpected monitor containers {sorted(containers)}')
    if {container['image'] for container in containers.values()} != {image}:
        raise HarnessError('monitor and collector must use only the requested monitor image')
    for container in containers.values():
        command = ' '.join(container.get('command', []) + container.get('args', []))
        if 'pip install' in command:
            raise HarnessError('source mode would make an external package request')
    synthetic = next(
        doc for doc in docs
        if doc['kind'] == 'ConfigMap'
        and doc['metadata']['name'] == f'{release}-synthetic-worker')
    script = synthetic['data']['worker.py']
    if 'subprocess' in script or 'stellar-core' in script or 'curl ' in script:
        raise HarnessError('synthetic worker contains an external command surface')
    return sorted(names)


def create_source_config_map(release):
    name = f'{release}-source'
    generated = kubectl(
        NAMESPACE, 'create', 'configmap', name,
        f'--from-file=job_monitor.py={JOB_MONITOR}',
        f'--from-file=log_collector.py={LOG_COLLECTOR}',
        '--dry-run=client', '-o', 'yaml').stdout
    kubectl(NAMESPACE, 'apply', '-f', '-', input_text=generated)
    return name


def json_get(resource, *, labels=None, name=None):
    args = ['get', resource]
    if name:
        args.append(name)
    if labels:
        args += ['--selector', labels]
    args += ['-o', 'json']
    result = kubectl(NAMESPACE, *args, check=False)
    if result.returncode:
        if 'NotFound' in result.stderr or 'not found' in result.stderr:
            return None
        raise HarnessError(result.stderr.strip())
    return json.loads(result.stdout)


def monitor_pod(release):
    payload = json_get('pods', labels=f'app=job-monitor,release={release}')
    items = (payload or {}).get('items', [])
    if len(items) != 1:
        return None
    return items[0]


def monitor_startup(pod):
    if not pod:
        return None
    statuses = {}
    for status in pod.get('status', {}).get('containerStatuses', []):
        state = status.get('state') or {}
        statuses[status['name']] = {
            'ready': status.get('ready', False),
            'restartCount': status.get('restartCount', 0),
            'state': state,
        }
    return {
        'name': pod['metadata']['name'],
        'phase': pod.get('status', {}).get('phase'),
        'conditions': pod.get('status', {}).get('conditions', []),
        'containers': statuses,
    }


def ready_monitor_pod(release, evidence):
    pod = monitor_pod(release)
    evidence['monitorStartup'] = monitor_startup(pod)
    if not pod:
        return None
    statuses = pod.get('status', {}).get('containerStatuses', [])
    return pod if len(statuses) == 2 and all(s.get('ready') for s in statuses) else None


def monitor_logs(release):
    pod = monitor_pod(release)
    if not pod:
        return {}
    name = pod['metadata']['name']
    captured = {}
    for container in ('job-monitor', 'log-collector'):
        current = kubectl(
            NAMESPACE, 'logs', name, '-c', container,
            '--tail=200', check=False, timeout=30)
        previous = kubectl(
            NAMESPACE, 'logs', name, '-c', container, '--previous',
            '--tail=200', check=False, timeout=30)
        captured[container] = {
            'current': current.stdout if current.returncode == 0 else current.stderr,
            'previous': previous.stdout if previous.returncode == 0 else previous.stderr,
        }
    return captured


def worker_snapshot(release):
    selector = f'catchup.stellar.org/run={release}'
    jobs = (json_get('jobs', labels=selector) or {}).get('items', [])
    pods = (json_get('pods', labels=selector) or {}).get('items', [])
    return jobs, pods


def collect_snapshot(release, evidence):
    jobs, pods = worker_snapshot(release)
    expected_jobs = {factory(release) for factory in ATTEMPT_NAMES.values()}
    for job in jobs:
        name = job['metadata']['name']
        if name not in expected_jobs:
            raise HarnessError(f'unexpected worker Job {name}')
        evidence['jobsSeen'].add(name)
    live = []
    attempts = {}
    for pod in pods:
        name = pod['metadata']['name']
        labels = pod['metadata']['labels']
        attempt = int(labels['catchup.stellar.org/attempt'])
        if attempt not in ATTEMPT_NAMES:
            raise HarnessError(f'unexpected worker attempt {attempt}')
        attempts[attempt] = attempts.get(attempt, 0) + 1
        evidence['podsSeen'].add(name)
        if pod.get('status', {}).get('phase') in ('Pending', 'Running'):
            live.append(name)
    if any(count > 1 for count in attempts.values()):
        raise HarnessError(f'duplicate worker pods in one attempt: {attempts}')
    if len(live) > 1:
        raise HarnessError(f'duplicate live workers for one range: {live}')
    evidence['maxConcurrentLiveWorkers'] = max(
        evidence['maxConcurrentLiveWorkers'], len(live))

    for pvc_name in (f'{release}-job-monitor-logs', f'{release}-data-r{RANGE_END}'):
        pvc = json_get('pvc', name=pvc_name)
        volume = ((pvc or {}).get('spec') or {}).get('volumeName')
        if volume:
            evidence['persistentVolumes'].add(volume)
    return jobs, pods


def wait_for(description, predicate, *, timeout, interval=1):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(interval)
    raise HarnessError(f'timed out waiting for {description}')


def container_restarts(pod):
    statuses = {
        status['name']: status.get('restartCount', 0)
        for status in pod.get('status', {}).get('containerStatuses', [])
    }
    if set(statuses) != {'job-monitor', 'log-collector'}:
        raise HarnessError(f'incomplete container status: {statuses}')
    return statuses


_ARTIFACT_SCRIPT = r"""
import gzip
import json
import os

root = "/logs"
prefix = "range-64-"
names = sorted(name for name in os.listdir(root) if name.startswith(prefix))
out = {"files": names}
for attempt in (1, 2):
    base = os.path.join(root, f"range-64-a{attempt}")
    for suffix in ("metrics", "outcome"):
        path = base + "." + suffix
        if os.path.exists(path):
            with open(path) as stream:
                out[f"a{attempt}_{suffix}"] = json.load(stream)
    verdict = base + ".verdict"
    if os.path.exists(verdict):
        with open(verdict) as stream:
            out[f"a{attempt}_verdict"] = stream.read().strip()
    out[f"a{attempt}_done"] = os.path.exists(base + ".done")
    archive = base + ".log.gz"
    if os.path.exists(archive):
        try:
            with gzip.open(archive, "rt", errors="replace") as stream:
                out[f"a{attempt}_log"] = stream.read()
        except (EOFError, OSError) as error:
            out[f"a{attempt}_log_error"] = str(error)
progress = os.path.join(root, "progress.json")
if os.path.exists(progress):
    with open(progress) as stream:
        out["progress"] = json.load(stream)
print(json.dumps(out, sort_keys=True))
"""


def artifact_bundle(release):
    pod = monitor_pod(release)
    if not pod:
        return None
    result = kubectl(
        NAMESPACE, 'exec', pod['metadata']['name'], '-c', 'job-monitor', '--',
        'python3', '-c', _ARTIFACT_SCRIPT, check=False, timeout=30)
    if result.returncode:
        return None
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError:
        return None


def attempt_pod(release, attempt):
    _, pods = worker_snapshot(release)
    matches = [
        pod for pod in pods
        if pod['metadata']['labels'].get('catchup.stellar.org/attempt') == str(attempt)
        and pod.get('status', {}).get('phase') == 'Running'
    ]
    if len(matches) > 1:
        raise HarnessError(f'more than one running pod for attempt {attempt}')
    return matches[0] if matches else None


def assert_completed_profile(bundle):
    missing = [
        name for name in ('a1_metrics', 'a1_outcome', 'a1_verdict',
                          'a1_log', 'a2_metrics', 'a2_log', 'progress')
        if name not in bundle
    ]
    if missing:
        raise HarnessError(f'missing final artifacts: {missing}')
    if not bundle.get('a1_done') or not bundle.get('a2_done'):
        raise HarnessError('both collector .done markers must be durable')
    if bundle['a1_verdict'] != 'failed':
        raise HarnessError(f"attempt 1 verdict is {bundle['a1_verdict']!r}")
    if bundle['a2_metrics'].get('resumed') is not True:
        raise HarnessError('attempt 2 metrics lacks resumed=true')
    if 'RESUME: 64/64 reached ledger 63' not in bundle['a2_log']:
        raise HarnessError('attempt 2 archive lacks the true RESUME decision')
    if 'RESUME DECLINED:' in bundle['a2_log']:
        raise HarnessError('attempt 2 archive contains a declined resume')

    profile = (bundle['progress'].get('completed') or {}).get(str(RANGE_END))
    if not profile:
        raise HarnessError('progress has no completed range 64')
    if profile.get('attempts') != 2:
        raise HarnessError(f"completed attempts is {profile.get('attempts')!r}, not 2")
    expected_seconds = (
        float(bundle['a1_outcome']['attemptSeconds'])
        + float(bundle['a2_metrics']['attemptSeconds']))
    if abs(float(profile.get('seconds', -1)) - expected_seconds) > 0.2:
        raise HarnessError(
            f"profile seconds {profile.get('seconds')} != chain {expected_seconds}")
    for field, expected in SYNTHETIC_PEAKS.items():
        if profile.get(field) != expected:
            raise HarnessError(f'profile {field}={profile.get(field)!r}, expected {expected}')
    if abs(float(profile.get('txApply', -1)) - 3.75) > 1e-9:
        raise HarnessError(f"profile txApply={profile.get('txApply')!r}, expected 3.75")
    if any(name.startswith('range-64-a3.') for name in bundle['files']):
        raise HarnessError('a third attempt artifact proves duplicate retry dispatch')
    return {
        'record': profile,
        'attempt1Metrics': bundle['a1_metrics'],
        'attempt1Outcome': bundle['a1_outcome'],
        'attempt1Verdict': bundle['a1_verdict'],
        'attempt2Metrics': bundle['a2_metrics'],
        'expectedChainSecondsFromArtifacts': expected_seconds,
        'attempt1ResumeLines': [
            line for line in bundle['a1_log'].splitlines() if 'RESUME' in line],
        'attempt2ResumeLines': [
            line for line in bundle['a2_log'].splitlines() if 'RESUME' in line],
        'done': {'attempt1': bundle['a1_done'], 'attempt2': bundle['a2_done']},
        'artifactFiles': bundle['files'],
    }


def release_worker(release):
    pod = attempt_pod(release, 2)
    if not pod:
        return False
    result = kubectl(
        NAMESPACE, 'exec', pod['metadata']['name'], '-c', 'stellar-core', '--',
        'python3', '-c',
        'from pathlib import Path; Path("/data/.synthetic-release").touch()',
        check=False, timeout=30)
    if result.returncode:
        raise HarnessError(f'could not release successor: {result.stderr.strip()}')
    return True


def resource_absent(kind, name):
    return json_get(kind, name=name) is None


def cleanup(release, source_config_map, observed, evidence):
    cleanup_result = {'releaseUninstalled': False, 'resourcesAbsent': {},
                      'persistentVolumesAbsent': {}}
    if observed:
        try:
            collect_snapshot(release, evidence)
        except HarnessError:
            pass

    uninstall = run(
        ['helm', 'uninstall', release, '--namespace', NAMESPACE,
         '--wait', '--timeout', '2m'], check=False, timeout=150)
    cleanup_result['releaseUninstalled'] = uninstall.returncode == 0

    exact = [
        ('deployment', f'{release}-job-monitor'),
        ('role', f'{release}-job-monitor'),
        ('rolebinding', f'{release}-job-monitor'),
        ('serviceaccount', f'{release}-job-monitor'),
        ('serviceaccount', f'stellar-supercluster-{release}'),
        ('configmap', f'{release}-stellar-core-config'),
        ('configmap', f'{release}-synthetic-worker'),
        ('configmap', f'{release}-catchup-progress'),
        ('configmap', source_config_map),
        ('pvc', f'{release}-job-monitor-logs'),
        ('pvc', f'{release}-data-r{RANGE_END}'),
    ]
    exact.extend(('job', name) for name in sorted(evidence['jobsSeen']))
    exact.extend(('pod', name) for name in sorted(evidence['podsSeen']))
    for kind, name in exact:
        kubectl(
            NAMESPACE, 'delete', kind, name, '--ignore-not-found=true',
            '--wait=true', '--timeout=60s', check=False, timeout=70)

    for volume in sorted(evidence['persistentVolumes']):
        result = run(['kubectl', 'get', 'pv', volume, '-o', 'name'], check=False)
        if result.returncode == 0:
            run(
                ['kubectl', 'delete', 'pv', volume, '--wait=true', '--timeout=60s'],
                check=False, timeout=70)

    for kind, name in exact:
        key = f'{kind}/{name}'
        cleanup_result['resourcesAbsent'][key] = resource_absent(kind, name)
    remaining_jobs, remaining_pods = worker_snapshot(release)
    cleanup_result['selectorAbsent'] = not remaining_jobs and not remaining_pods
    for volume in sorted(evidence['persistentVolumes']):
        result = run(['kubectl', 'get', 'pv', volume, '-o', 'name'], check=False)
        cleanup_result['persistentVolumesAbsent'][volume] = result.returncode != 0
    status = run(
        ['helm', 'status', release, '--namespace', NAMESPACE], check=False)
    cleanup_result['helmStatusAbsent'] = status.returncode != 0
    if not (
        cleanup_result['selectorAbsent']
        and cleanup_result['helmStatusAbsent']
        and all(cleanup_result['resourcesAbsent'].values())
        and all(cleanup_result['persistentVolumesAbsent'].values())
    ):
        raise HarnessError(f'incomplete cleanup: {cleanup_result}')
    return cleanup_result


def execute(args):
    validate_scope(args.namespace, args.release)
    source_config_map = f'{args.release}-source'
    evidence = {
        'namespace': args.namespace,
        'release': args.release,
        'context': run(['kubectl', 'config', 'current-context']).stdout.strip(),
        'jobsSeen': set(),
        'podsSeen': set(),
        'persistentVolumes': set(),
        'maxConcurrentLiveWorkers': 0,
    }
    installed = False
    scope_started = False
    failure = None
    try:
        sets = helm_sets(args.release, source_config_map, args.image)
        rendered = run(
            ['helm', 'template', args.release, str(CHART),
             '--namespace', NAMESPACE] + helm_args(sets)).stdout
        Path(args.rendered).write_text(rendered)
        evidence['renderedResources'] = inspect_rendered(
            rendered, args.release, args.image)
        create_source_config_map(args.release)
        scope_started = True

        run(
            ['helm', 'install', args.release, str(CHART),
             '--namespace', NAMESPACE, '--timeout', '3m']
            + helm_args(sets),
            timeout=210)
        installed = True

        pod = wait_for(
            'one ready monitor pod',
            lambda: ready_monitor_pod(args.release, evidence), timeout=120)
        initial_restarts = container_restarts(pod)
        evidence['restartCountsBefore'] = initial_restarts

        def successor_ready():
            collect_snapshot(args.release, evidence)
            return attempt_pod(args.release, 2)

        successor = wait_for(
            'running successor attempt', successor_ready, timeout=120)
        evidence['successorPod'] = successor['metadata']['name']

        def archived_resume():
            collect_snapshot(args.release, evidence)
            bundle = artifact_bundle(args.release)
            if not bundle or bundle.get('a2_log_error'):
                return None
            return bundle if 'RESUME: 64/64 reached ledger 63' in bundle.get(
                'a2_log', '') else None

        wait_for(
            'durable successor RESUME line in gzip archive',
            archived_resume, timeout=60)

        monitor = monitor_pod(args.release)
        monitor_name = monitor['metadata']['name']
        before = container_restarts(monitor)
        killed = kubectl(
            NAMESPACE, 'exec', monitor_name, '-c', 'log-collector', '--',
            '/bin/sh', '-c', 'kill -TERM 1', check=False, timeout=30)
        evidence['collectorKillExitCode'] = killed.returncode

        def collector_restarted():
            current = monitor_pod(args.release)
            if not current or current['metadata']['name'] != monitor_name:
                raise HarnessError('monitor pod was recreated during collector restart')
            counts = container_restarts(current)
            if counts['job-monitor'] != before['job-monitor']:
                raise HarnessError('job-monitor restarted with the collector')
            if counts['log-collector'] == before['log-collector'] + 1:
                return counts
            if counts['log-collector'] > before['log-collector'] + 1:
                raise HarnessError('collector restarted more than once')
            return None

        after = wait_for(
            'exactly one collector-only restart',
            collector_restarted, timeout=60)
        evidence['restartCountsAfter'] = after
        time.sleep(2)
        if not release_worker(args.release):
            raise HarnessError('successor stopped before it could be released')

        def completed():
            collect_snapshot(args.release, evidence)
            bundle = artifact_bundle(args.release)
            record = ((bundle or {}).get('progress', {}).get('completed') or {}).get(
                str(RANGE_END))
            if record and bundle.get('a1_done') and bundle.get('a2_done'):
                return bundle
            return None

        bundle = wait_for(
            'completed profile and both collector done markers',
            completed, timeout=120)
        evidence['profileAssertions'] = assert_completed_profile(bundle)
        if evidence['jobsSeen'] != {
                ATTEMPT_NAMES[1](args.release), ATTEMPT_NAMES[2](args.release)}:
            raise HarnessError(f"unexpected Job set {sorted(evidence['jobsSeen'])}")
        if evidence['maxConcurrentLiveWorkers'] != 1:
            raise HarnessError(
                f"max concurrent live workers was {evidence['maxConcurrentLiveWorkers']}")
    except Exception as error:
        failure = error
        evidence['error'] = f'{type(error).__name__}: {error}'
    finally:
        try:
            evidence['monitorLogs'] = monitor_logs(args.release)
            evidence['cleanup'] = cleanup(
                args.release, source_config_map, scope_started or installed, evidence)
        except Exception as cleanup_error:
            evidence['cleanupError'] = (
                f'{type(cleanup_error).__name__}: {cleanup_error}')
            if failure is None:
                failure = cleanup_error

        for key in ('jobsSeen', 'podsSeen', 'persistentVolumes'):
            evidence[key] = sorted(evidence[key])
        Path(args.evidence).write_text(json.dumps(evidence, indent=2, sort_keys=True))

    if failure is not None:
        raise failure
    return evidence


def parse_args(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--namespace', required=True)
    parser.add_argument('--release', required=True)
    parser.add_argument('--image', default='stellar/ssc-job-monitor:latest')
    parser.add_argument('--evidence', required=True)
    parser.add_argument('--rendered', required=True)
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    evidence = execute(args)
    print(json.dumps({
        'release': evidence['release'],
        'restartCountsBefore': evidence['restartCountsBefore'],
        'restartCountsAfter': evidence['restartCountsAfter'],
        'record': evidence['profileAssertions']['record'],
        'cleanup': evidence['cleanup'],
    }, indent=2, sort_keys=True))


if __name__ == '__main__':
    main()
