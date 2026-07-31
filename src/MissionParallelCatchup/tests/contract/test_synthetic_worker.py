"""Default-off and deterministic contracts for the live integration worker."""

import os
import subprocess
import sys

import log_collector as lc

import _artifacts as art


ENABLED = ('integration.syntheticWorker.enabled=true',)
MIB = 1024 * 1024


def test_default_render_has_no_synthetic_resource_or_runtime_switch():
    names = {d['metadata']['name'] for d in art.docs()}
    assert 't-synthetic-worker' not in names
    for container in art.containers().values():
        env = set(art.env_of(container))
        assert not any(name.startswith('SYNTHETIC_') for name in env)


def test_opt_in_render_adds_fixed_worker_and_narrow_runtime_wiring():
    config_maps = {d['metadata']['name']: d
                   for d in art.of_kind('ConfigMap', ENABLED)}
    worker = config_maps['t-synthetic-worker']
    assert set(worker['data']) == {'worker.py'}
    assert 'stellar-core' not in worker['data']['worker.py']
    assert 'subprocess' not in worker['data']['worker.py']

    containers = art.containers(ENABLED)
    monitor_env = art.env_of(containers[art.MONITOR_CONTAINER])
    collector_env = art.env_of(containers[art.COLLECTOR_CONTAINER])
    assert monitor_env['SYNTHETIC_WORKER_CONFIG_MAP'] == 't-synthetic-worker'
    assert collector_env['SYNTHETIC_WORKER'] == 'true'


def test_source_mode_can_skip_dependency_install_without_changing_its_default():
    source = ('monitor.sourceConfigMap=source',)
    for container in art.containers(source).values():
        assert 'pip install' in ' '.join(container.get('args') or [])

    offline = source + ('monitor.sourceInstallDependencies=false',)
    for container in art.containers(offline).values():
        command = ' '.join(container.get('args') or [])
        assert 'pip install' not in command
        assert 'exec python3 /app/' in command


def test_fixed_worker_persists_then_resumes_the_same_pvc(tmp_path):
    worker = next(d for d in art.of_kind('ConfigMap', ENABLED)
                  if d['metadata']['name'] == 't-synthetic-worker')
    script = tmp_path / 'worker.py'
    script.write_text(worker['data']['worker.py'])
    env = {
        **os.environ,
        'SYNTHETIC_DATA_DIR': str(tmp_path),
        'SYNTHETIC_TARGET': '64',
        'SYNTHETIC_COUNT': '64',
        'SYNTHETIC_KEY': '64/64',
        'SYNTHETIC_PREDECESSOR_SECONDS': '0',
        'SYNTHETIC_SUCCESSOR_MINIMUM_SECONDS': '0',
        'SYNTHETIC_MAXIMUM_WAIT_SECONDS': '1',
        'SYNTHETIC_PREDECESSOR_ANON_MIB': '2',
        'SYNTHETIC_PREDECESSOR_WORKING_SET_MIB': '3',
        'SYNTHETIC_SUCCESSOR_ANON_MIB': '1',
        'SYNTHETIC_SUCCESSOR_WORKING_SET_MIB': '2',
        'SYNTHETIC_PREDECESSOR_TX_APPLY_MS': '1250',
        'SYNTHETIC_SUCCESSOR_TX_APPLY_MS': '2500',
    }

    first = subprocess.run(
        [sys.executable, str(script)], env={**env, 'SYNTHETIC_ATTEMPT': '1'},
        capture_output=True, text=True, timeout=5)
    assert first.returncode == 3
    assert 'SYNTHETIC PREDECESSOR: 64/64 persisted ledger 63' in first.stdout
    assert 'sum = 1250ms' in first.stdout

    (tmp_path / '.synthetic-release').touch()
    second = subprocess.run(
        [sys.executable, str(script)], env={**env, 'SYNTHETIC_ATTEMPT': '2'},
        capture_output=True, text=True, timeout=5)
    assert second.returncode == 0, second.stderr
    assert 'RESUME PROBE: offline-info reports lcl 63' in second.stdout
    assert 'RESUME: 64/64 reached ledger 63, replay had started; skipping new-db' \
        in second.stdout
    assert 'sum = 2500ms' in second.stdout


def test_synthetic_peak_marker_is_inert_unless_the_harness_is_enabled(monkeypatch):
    line = f'SYNTHETIC PEAK: anonBytes={48 * MIB} workingSetBytes={56 * MIB}'
    scanner = lc.TxApplyScanner()
    scanner.feed(line)
    assert scanner.synthetic_anon is None

    monkeypatch.setattr(lc, 'SYNTHETIC_WORKER', True)
    scanner = lc.TxApplyScanner()
    scanner.feed(line)
    assert scanner.synthetic_anon == 48 * MIB
    assert scanner.synthetic_working_set == 56 * MIB
