"""Worker responsiveness metrics stay truthful without entering reconcile."""

import os
import subprocess
import sys
import threading
import time

import job_monitor as jm
from kubernetes import client


def _task(sampler, identity):
    record = sampler._records[identity]
    return identity, record['generation'], record['target']


def _result(sampler, identity, success, now):
    sampler._record_result(*_task(sampler, identity), success,
                           None if success else TimeoutError("busy"), now=now)


def test_hysteresis_unknown_down_and_immediate_recovery():
    sampler = jm.WorkerLivenessSampler(
        interval=30, timeout=5, failure_threshold=3, max_concurrency=1)
    sampler.replace_candidates({'uid-1': ('pod-1', '10.0.0.1')}, now=0)

    assert sampler._records['uid-1']['status'] == 'unknown'
    _result(sampler, 'uid-1', True, 1)
    assert sampler._records['uid-1']['status'] == 'up'

    _result(sampler, 'uid-1', False, 31)
    assert sampler._records['uid-1']['status'] == 'unknown'
    _result(sampler, 'uid-1', False, 61)
    assert sampler._records['uid-1']['status'] == 'unknown'
    _result(sampler, 'uid-1', False, 91)
    assert sampler._records['uid-1']['status'] == 'down'

    _result(sampler, 'uid-1', True, 121)
    assert sampler._records['uid-1']['status'] == 'up'
    assert sampler._records['uid-1']['failures'] == 0


def test_disappearance_and_replacement_discard_stale_probe_results():
    sampler = jm.WorkerLivenessSampler(max_concurrency=1)
    sampler.replace_candidates({'old-uid': ('pod-1', '10.0.0.1')}, now=0)
    old_task = _task(sampler, 'old-uid')
    _result(sampler, 'old-uid', True, 1)

    # A new UID is a new attempt/pod even if Kubernetes reuses the IP.
    sampler.replace_candidates({'new-uid': ('pod-2', '10.0.0.1')}, now=2)
    assert set(sampler._records) == {'new-uid'}
    assert sampler._records['new-uid']['status'] == 'unknown'

    # The old request may finish after the replacement snapshot. It cannot
    # resurrect the vanished pod or update the replacement.
    sampler._record_result(*old_task, True, now=3)
    assert set(sampler._records) == {'new-uid'}
    assert sampler._records['new-uid']['status'] == 'unknown'

    sampler.replace_candidates({}, now=4)
    assert sampler.counts() == {'up': 0, 'down': 0, 'unknown': 0}


def test_ip_change_on_same_identity_resets_to_unknown():
    sampler = jm.WorkerLivenessSampler(max_concurrency=1)
    sampler.replace_candidates({'uid': ('pod', '10.0.0.1')}, now=0)
    old_task = _task(sampler, 'uid')
    _result(sampler, 'uid', True, 1)

    sampler.replace_candidates({'uid': ('pod', '10.0.0.2')}, now=2)
    assert sampler._records['uid']['status'] == 'unknown'
    assert sampler._records['uid']['failures'] == 0

    sampler._record_result(*old_task, False, TimeoutError(), now=3)
    assert sampler._records['uid']['status'] == 'unknown'
    assert sampler._records['uid']['failures'] == 0


def test_sampler_failure_reports_every_current_candidate_unknown():
    release = threading.Event()

    def blocked_probe(_ip, _timeout):
        release.wait(2)

    sampler = jm.WorkerLivenessSampler(
        interval=30, timeout=1, failure_threshold=3,
        max_concurrency=1, probe=blocked_probe)
    sampler.start()
    try:
        sampler.replace_candidates({
            'a': ('pod-a', '10.0.0.1'),
            'b': ('pod-b', '10.0.0.2'),
        })
        with sampler._condition:
            sampler._failed = 'synthetic scheduler failure'
        assert sampler.counts() == {'up': 0, 'down': 0, 'unknown': 2}
    finally:
        release.set()
        sampler.close()


def test_probe_uses_stellar_core_info_and_any_http_response_is_up(monkeypatch):
    called = []

    class Response:
        status_code = 503

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class Session:
        def mount(self, *_args):
            pass

        def get(self, url, timeout):
            called.append((url, timeout))
            return Response()

        def close(self):
            pass

    monkeypatch.setattr(jm.requests, 'Session', Session)
    sampler = jm.WorkerLivenessSampler(
        interval=30, timeout=5, failure_threshold=3, max_concurrency=1)
    sampler.start()
    try:
        sampler.replace_candidates({'uid': ('pod', '10.2.3.4')})
        deadline = time.monotonic() + 1
        while time.monotonic() < deadline and sampler._records['uid']['status'] != 'up':
            time.sleep(0.01)
        assert called == [('http://10.2.3.4:11626/info', 5.0)]
        assert sampler._records['uid']['status'] == 'up', (
            "an HTTP 503 is a busy but responsive admin endpoint")
    finally:
        sampler.close()


def test_only_running_pods_with_ips_are_candidates_and_uid_is_identity():
    def pod(name, uid, phase, ip):
        return client.V1Pod(
            metadata=client.V1ObjectMeta(name=name, uid=uid),
            status=client.V1PodStatus(phase=phase, pod_ip=ip))

    targets = jm._worker_targets([
        pod('ready', 'uid-ready', 'Running', '10.0.0.1'),
        pod('pending', 'uid-pending', 'Pending', '10.0.0.2'),
        pod('no-ip', 'uid-no-ip', 'Running', None),
    ])
    assert targets == {'uid-ready': ('ready', '10.0.0.1')}


def test_malformed_liveness_configuration_fails_with_an_explicit_message():
    env = {
        'PATH': os.environ.get('PATH', ''),
        'HOME': os.environ.get('HOME', ''),
        'PYTHONPATH': os.path.dirname(jm.__file__),
        'LIVENESS_MAX_CONCURRENCY': 'many',
    }
    result = subprocess.run(
        [sys.executable, '-c', 'import job_monitor'],
        text=True, capture_output=True, env=env,
        cwd=os.path.dirname(jm.__file__))
    assert result.returncode != 0
    assert 'LIVENESS_MAX_CONCURRENCY must be integers' in result.stderr


def test_2096_slow_workers_have_bounded_work_and_do_not_delay_reconcile(
        cluster):
    release = threading.Event()
    active_lock = threading.Lock()
    active = 0
    peak_active = 0

    def blocked_probe(_ip, _timeout):
        nonlocal active, peak_active
        with active_lock:
            active += 1
            peak_active = max(peak_active, active)
        try:
            release.wait(3)
        finally:
            with active_lock:
                active -= 1

    concurrency = 8
    sampler = jm.WorkerLivenessSampler(
        interval=0.2, timeout=1, failure_threshold=3,
        max_concurrency=concurrency, probe=blocked_probe)
    targets = {
        f"uid-{i}": (f"pod-{i}", f"10.{i // 65536}.{(i // 256) % 256}.{i % 256}")
        for i in range(2096)
    }
    sampler.start()
    sampler.replace_candidates(targets)
    try:
        deadline = time.monotonic() + 2
        while time.monotonic() < deadline and sampler.stats()['active'] < concurrency:
            time.sleep(0.01)

        stats = sampler.stats()
        assert stats['records'] == 2096
        assert stats['active'] <= concurrency
        assert stats['queued'] <= concurrency
        assert stats['outstanding'] <= 2 * concurrency
        assert stats['threads'] == concurrency + 1
        assert peak_active <= concurrency

        # Exercise the exact handoff used by update_status_and_metrics while all
        # request slots are blocked. It copies the candidate snapshot and reads
        # counts, but never waits for a request.
        started = time.monotonic()
        counts = jm.publish_worker_liveness(targets, sampler=sampler)
        publish_elapsed = time.monotonic() - started
        assert publish_elapsed < 0.5, (
            f"blocked probes delayed liveness publication by {publish_elapsed:.3f}s")
        assert counts == {'up': 0, 'down': 0, 'unknown': 2096}
        assert sum(counts.values()) == len(targets)

        # Dispatch itself remains equally independent.
        started = time.monotonic()
        result = cluster.reconcile()
        elapsed = time.monotonic() - started
        assert result['created'] == 2
        assert elapsed < 0.5, f"blocked liveness probes delayed reconcile by {elapsed:.3f}s"
    finally:
        release.set()
        sampler.close()
