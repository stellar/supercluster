"""Safety and profile assertions for the opt-in live runner."""

import importlib.util
from pathlib import Path

import pytest


PATH = (
    Path(__file__).resolve().parents[2]
    / 'integration' / 'synthetic_resume_harness.py')
SPEC = importlib.util.spec_from_file_location('synthetic_resume_harness', PATH)
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)


def test_scope_guard_accepts_only_unique_sandbox_release_names():
    HARNESS.validate_scope('sandbox', 'mpc-resume-a1b2c3')
    for namespace, release in (
            ('stellar-supercluster', 'mpc-resume-a1b2c3'),
            ('default', 'mpc-resume-a1b2c3'),
            ('sandbox', 'parallel-catchup-ssc-1959z-ef177a-r5'),
            ('sandbox', 'mpc-resume-short')):
        with pytest.raises(HARNESS.HarnessError):
            HARNESS.validate_scope(namespace, release)


def test_render_inspection_rejects_cluster_scoped_or_unprefixed_resources():
    manifest = """
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: mpc-resume-a1b2c3
rules: []
"""
    with pytest.raises(HARNESS.HarnessError):
        HARNESS.inspect_rendered(
            manifest, 'mpc-resume-a1b2c3', 'stellar/ssc-job-monitor:latest')


def test_profile_assertion_requires_both_legs_and_predecessor_peaks():
    bundle = {
        'a1_metrics': {
            'attemptSeconds': 12.0,
            'peakAnonBytes': 48 * 1024 * 1024,
            'peakWorkingSetBytes': 56 * 1024 * 1024,
            'txApplySeconds': 1.25,
        },
        'a1_outcome': {'attemptSeconds': 12.0, 'outcome': 'failed'},
        'a1_verdict': 'failed',
        'a1_log': "metric 'ledger.transaction.apply'\nsum = 1250ms\n",
        'a1_done': True,
        'a2_metrics': {
            'attemptSeconds': 14.0,
            'peakAnonBytes': 24 * 1024 * 1024,
            'peakWorkingSetBytes': 32 * 1024 * 1024,
            'txApplySeconds': 2.5,
            'resumed': True,
        },
        'a2_log': (
            'RESUME PROBE: offline-info reports lcl 63\n'
            'RESUME: 64/64 reached ledger 63, replay had started; skipping new-db\n'),
        'a2_done': True,
        'files': [
            'range-64-a1.done', 'range-64-a1.log.gz',
            'range-64-a1.metrics', 'range-64-a1.outcome',
            'range-64-a1.verdict', 'range-64-a2.done',
            'range-64-a2.log.gz', 'range-64-a2.metrics',
        ],
        'progress': {'completed': {'64': {
            'attempts': 2,
            'seconds': 26.0,
            'peakAnonBytes': 48 * 1024 * 1024,
            'peakWorkingSetBytes': 56 * 1024 * 1024,
            'txApply': 3.75,
        }}},
    }
    result = HARNESS.assert_completed_profile(bundle)
    assert result['expectedChainSecondsFromArtifacts'] == 26.0

    bundle['progress']['completed']['64']['peakAnonBytes'] = 24 * 1024 * 1024
    with pytest.raises(HARNESS.HarnessError):
        HARNESS.assert_completed_profile(bundle)


def test_runner_uses_a_handled_signal_for_collector_only_restart():
    source = PATH.read_text()
    assert 'kill -TERM 1' in source
    assert 'kill -9 1' not in source


def test_monitor_readiness_requires_both_containers():
    pod = {
        'metadata': {'name': 'monitor'},
        'status': {'phase': 'Running', 'containerStatuses': [
            {'name': 'job-monitor', 'ready': True, 'restartCount': 0, 'state': {}},
            {'name': 'log-collector', 'ready': False, 'restartCount': 1, 'state': {}},
        ]},
    }
    evidence = {}
    original = HARNESS.monitor_pod
    try:
        HARNESS.monitor_pod = lambda _release: pod
        assert HARNESS.ready_monitor_pod('mpc-resume-a1b2c3', evidence) is None
        pod['status']['containerStatuses'][1]['ready'] = True
        assert HARNESS.ready_monitor_pod('mpc-resume-a1b2c3', evidence) is pod
    finally:
        HARNESS.monitor_pod = original
