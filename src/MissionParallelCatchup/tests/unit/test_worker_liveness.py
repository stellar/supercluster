"""Worker responsiveness: one concurrent sweep per reconcile pass.

Driven against real sockets rather than a faked session -- the whole behaviour is
"what did stellar-core's admin port actually answer", so a fake that returns
whatever the test wants proves very little.
"""

import asyncio
import contextlib
import os
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest
from kubernetes import client

import config
import worker_liveness
import job_monitor as jm


def _server(status=200, delay=0.0, counter=None):
    """A local HTTP endpoint standing in for stellar-core's admin port."""
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            if counter is not None:
                counter.enter()
            if delay:
                time.sleep(delay)
            self.send_response(status)
            self.end_headers()
            self.wfile.write(b'{}')
            if counter is not None:
                counter.leave()

        def log_message(self, *_a):
            pass

    srv = HTTPServer(('127.0.0.1', 0), Handler)
    srv.daemon_threads = True
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


class _Concurrency:
    """Server-side count of overlapping requests, and its high-water mark."""

    def __init__(self):
        self.lock = threading.Lock()
        self.now = 0
        self.peak = 0

    def enter(self):
        with self.lock:
            self.now += 1
            self.peak = max(self.peak, self.now)

    def leave(self):
        with self.lock:
            self.now -= 1


def _targets(port, count=1):
    return {f"uid-{i}": (f"pod-{i}", '127.0.0.1') for i in range(count)}


@contextlib.contextmanager
def _serving(monkeypatch, **kw):
    """Point the module's admin port at a local endpoint for the duration."""
    srv = _server(**kw)
    monkeypatch.setattr(worker_liveness, '_ADMIN_PORT', srv.server_address[1])
    try:
        yield srv
    finally:
        srv.shutdown()


def _closed_port():
    s = socket.socket()
    s.bind(('127.0.0.1', 0))
    port = s.getsockname()[1]
    s.close()
    return port


def _sweep(targets, port, **kw):
    """Run a sweep against a chosen port by pointing the module's URL at it."""
    kw.setdefault('timeout', 2)
    kw.setdefault('deadline', 5)
    kw.setdefault('concurrency', 8)
    return asyncio.run(worker_liveness.sweep(targets, **kw))


# --- what counts as up --------------------------------------------------------

@pytest.mark.parametrize('status, verdict', [
    (200, 'up'),
    # A busy core used to count as up. "Answered, badly" is not answering, and
    # nothing downstream smooths it.
    (503, 'down'),
    # Not just 5xx: `status < 500` passes the 503 case while counting a wrong
    # path or a proxy in the way as a healthy core.
    (404, 'down'),
])
def test_only_a_200_is_up(monkeypatch, status, verdict):
    with _serving(monkeypatch, status=status):
        counts = _sweep(_targets(0, 2), None)
    assert counts[verdict] == 2 and sum(counts.values()) == 2


def test_a_refused_connection_is_down(monkeypatch):
    monkeypatch.setattr(worker_liveness, '_ADMIN_PORT', _closed_port())
    assert _sweep(_targets(0, 2), None) == {'up': 0, 'down': 2, 'unknown': 0}


def test_a_probe_slower_than_its_timeout_is_down(monkeypatch):
    with _serving(monkeypatch, status=200, delay=1.0):
        assert _sweep(_targets(0, 1), None, timeout=0.2) == \
            {'up': 0, 'down': 1, 'unknown': 0}


def test_no_targets_is_not_a_sweep():
    assert worker_liveness.publish({}) == {'up': 0, 'down': 0, 'unknown': 0}


# --- the bounds the reconcile loop depends on --------------------------------

def test_the_sweep_stops_at_its_deadline_and_reports_the_rest_unknown(monkeypatch):
    """The reconcile loop waits for this, so it must be bounded by wall clock.

    Ten pods that each hang for a second, two at a time, is five seconds of work.
    With a half-second deadline the sweep keeps what finished and calls the rest
    unknown rather than making dispatch wait.
    """
    with _serving(monkeypatch, status=200, delay=1.0):
        started = time.monotonic()
        counts = _sweep(_targets(0, 10), None, concurrency=2, timeout=5, deadline=0.5)
        elapsed = time.monotonic() - started
    assert elapsed < 2.0, f"the sweep ran {elapsed:.2f}s past a 0.5s deadline"
    assert counts['unknown'] >= 6, counts
    assert sum(counts.values()) == 10, "every target must be accounted for"


def test_concurrency_is_bounded_at_the_server(monkeypatch):
    counter = _Concurrency()
    with _serving(monkeypatch, status=200, delay=0.05, counter=counter):
        counts = _sweep(_targets(0, 60), None, concurrency=4, timeout=5, deadline=10)
    assert counts == {'up': 60, 'down': 0, 'unknown': 0}
    assert counter.peak <= 4, f"{counter.peak} overlapping requests, limit 4"


def test_one_unreachable_pod_does_not_discard_the_others(monkeypatch):
    """Why this is asyncio.wait and not a TaskGroup.

    A TaskGroup cancels its siblings when a task raises. Every other answer has
    to survive one pod being unreachable, so four pods point at a live endpoint
    and one at a loopback address with nothing bound.
    """
    with _serving(monkeypatch, status=200):
        targets = {f"uid-{i}": (f"pod-{i}", '127.0.0.1') for i in range(4)}
        targets['uid-dead'] = ('pod-dead', '127.0.0.2')
        counts = _sweep(targets, None, timeout=1)
    assert counts == {'up': 4, 'down': 1, 'unknown': 0}


def test_publish_reports_every_target_unknown_when_the_sweep_itself_fails(monkeypatch):
    """The production call path: job_monitor calls publish(targets) and nothing else."""
    async def boom(*_a, **_kw):
        raise RuntimeError("no event loop for you")
    monkeypatch.setattr(worker_liveness, 'sweep', boom)
    assert worker_liveness.publish(_targets(0, 7)) == \
        {'up': 0, 'down': 0, 'unknown': 7}


# --- candidate selection ------------------------------------------------------

def test_only_running_pods_with_ips_are_candidates_and_uid_is_identity():
    def pod(name, uid, phase, ip):
        return client.V1Pod(
            metadata=client.V1ObjectMeta(name=name, uid=uid),
            status=client.V1PodStatus(phase=phase, pod_ip=ip))

    targets = worker_liveness.targets([
        pod('ready', 'uid-ready', 'Running', '10.0.0.1'),
        pod('pending', 'uid-pending', 'Pending', '10.0.0.2'),
        pod('no-ip', 'uid-no-ip', 'Running', None),
    ])
    assert targets == {'uid-ready': ('ready', '10.0.0.1')}


def test_malformed_liveness_configuration_fails_with_an_explicit_message():
    env = {
        'PATH': os.environ.get('PATH', ''),
        'HOME': os.environ.get('HOME', ''),
        # apps/ and lib/ both, the way the container's flat /app has them.
        'PYTHONPATH': os.pathsep.join((os.path.dirname(jm.__file__),
                                       os.path.dirname(config.__file__))),
        'LIVENESS_MAX_CONCURRENCY': 'many',
    }
    result = subprocess.run(
        [sys.executable, '-c', 'import job_monitor'],
        text=True, capture_output=True, env=env,
        cwd=os.path.dirname(jm.__file__))
    assert result.returncode != 0
    assert 'LIVENESS_MAX_CONCURRENCY must be an integer' in result.stderr


def test_a_blocked_sweep_does_not_delay_dispatch(cluster, monkeypatch):
    """Dispatch must not wait on the fleet answering.

    The sweep is bounded by its deadline, and reconcile pays that at most once
    per pass -- so this pins the cost rather than the independence the old
    background sampler gave.
    """
    monkeypatch.setattr(config, 'LIVENESS_SWEEP_SECONDS', 0.3)
    with _serving(monkeypatch, status=200, delay=5.0):
        started = time.monotonic()
        counts = worker_liveness.publish(_targets(0, 50))
        elapsed = time.monotonic() - started
        assert elapsed < 1.5, f"publish took {elapsed:.2f}s against a 0.3s deadline"
        assert counts['unknown'] > 0
        assert sum(counts.values()) == 50

        started = time.monotonic()
        cluster.reconcile()
        assert time.monotonic() - started < 1.0
