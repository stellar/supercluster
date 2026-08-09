"""The monitor's HTTP surface, driven over a real socket.

This is the driver's only channel into a run -- profile in, status and logs out
-- so it is exercised through an actual server rather than by calling handler
methods, which would not catch a Range header the socket layer mishandles.
"""

import json
import threading
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

import pytest

import config
import http_server
import job_monitor as jm


@pytest.fixture
def server(tmp_path, monkeypatch):
    """A live monitor HTTP surface on a throwaway port and volume."""
    monkeypatch.setattr(config, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(config, 'RUN_PATH', str(tmp_path / 'run.json'))
    monkeypatch.setattr(http_server, 'started', threading.Event())
    monkeypatch.setattr(http_server, 'on_start', jm.start_run)
    monkeypatch.setattr(http_server, 'status_source',
                        lambda: (jm.status, jm.status_lock))

    httpd = ThreadingHTTPServer(('127.0.0.1', 0), http_server.RequestHandler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    yield f"http://127.0.0.1:{httpd.server_address[1]}", tmp_path
    httpd.shutdown()


def _get(base, path, headers=None):
    req = urllib.request.Request(base + path, headers=headers or {})
    with urllib.request.urlopen(req, timeout=5) as r:
        return r.status, r.read(), dict(r.headers)


def _post(base, path, body):
    req = urllib.request.Request(base + path, data=body.encode(), method='POST')
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def test_start_rejects_a_bad_config_with_the_reason(server, monkeypatch):
    """The whole point of validating here: the driver gets told why.

    Coercing at import made this a crashlooping pod instead, which the driver
    could only observe as a 600s timeout on a monitor that never answered.
    """
    base, _ = server
    monkeypatch.setattr(config, 'LIVENESS_MAX_CONCURRENCY', 'many')

    code, body = _post(base, '/start', json.dumps({"range": {"startingLedger": 0, "latestLedgerNum": 1000, "ledgersPerJob": 100}}))

    assert code == 400
    assert 'LIVENESS_MAX_CONCURRENCY must be an integer' in json.loads(body)['error']
    assert not http_server.started.is_set(), "a rejected config must not open the gate"


def test_start_opens_the_gate_and_is_idempotent(server):
    """A driver that retries after a timeout must not restart a live run."""
    base, vol = server

    assert _post(base, '/start', json.dumps({"range": {"startingLedger": 0, "latestLedgerNum": 1000, "ledgersPerJob": 100}, "profile": {"ranges": {"300": {"seconds": 1.0}}}}))[0] == 200
    assert http_server.started.is_set()
    assert (vol / 'run.json').exists(), "the profile is kept for a restart"

    # A second POST carrying nothing must not wipe the profile already installed.
    assert _post(base, '/start', json.dumps({"range": {"startingLedger": 0, "latestLedgerNum": 1000, "ledgersPerJob": 100}}))[0] == 200
    assert config.PROFILE == [(300, {'seconds': 1.0})]


def test_status_is_served_from_memory(server):
    base, _ = server
    code, body, _ = _get(base, '/status')

    assert code == 200
    assert json.loads(body)['num_remain'] == jm.status['num_remain']


def test_logs_manifest_carries_what_a_puller_diffs_on(server):
    base, vol = server
    (vol / 'range-300-a1.log.gz').write_bytes(b'x' * 1234)

    entries = {e['name']: e for e in json.loads(_get(base, '/logs')[1])}

    assert entries['range-300-a1.log.gz']['size'] == 1234
    assert 'mtime' in entries['range-300-a1.log.gz']


def test_a_file_resumes_from_the_byte_it_stopped_at(server):
    """Range is what makes a cut transfer cost the remainder rather than the
    whole file -- the truncation that lost 12 ranges their logs on 2026-08-07."""
    base, vol = server
    (vol / 'range-300-a1.log.gz').write_bytes(bytes(range(256)))

    whole = _get(base, '/logs/range-300-a1.log.gz')
    assert whole[0] == 200 and len(whole[1]) == 256

    code, body, headers = _get(base, '/logs/range-300-a1.log.gz',
                               {'Range': 'bytes=200-'})
    assert code == 206
    assert body == bytes(range(200, 256))
    assert headers['Content-Range'] == 'bytes 200-255/256'


def test_a_path_outside_the_volume_is_refused(server):
    """The route is reachable from outside the cluster once an HTTPRoute is
    attached, so the filename is the whole security boundary."""
    base, _ = server
    for bad in ('..%2f..%2fetc%2fpasswd', '.hidden', 'sub%2fdir'):
        with pytest.raises(urllib.error.HTTPError) as e:
            _get(base, '/logs/' + bad)
        assert e.value.code == 404


def test_the_collectors_resume_cursor_is_not_offered_for_pulling(server):
    """.state is one timestamp rewritten on every poll of a live range.

    It means nothing once the pods are gone, and it changes constantly -- so a
    manifest diff would re-fetch one per in-flight range on every pass. The tar
    it replaced excluded it deliberately; this keeps that.
    """
    base, vol = server
    (vol / 'range-300-a1.log.gz').write_bytes(b'kept')
    (vol / 'range-300-a1.state').write_text('2026-08-08T21:44:01.867115384Z')

    names = {e['name'] for e in json.loads(_get(base, '/logs')[1])}

    assert 'range-300-a1.log.gz' in names
    assert 'range-300-a1.state' not in names


def test_a_restart_resumes_without_waiting_for_another_start(server, tmp_path):
    """run.json on the volume is what says "this monitor has a run".

    Whoever installs it opens the gate -- a POST, or a restart reading it back.
    It did not: the gate lived in the POST handler, so a restarted monitor
    blocked on it forever while /status kept answering with its placeholder.
    Nothing was unreachable, so the driver polled a dead run indefinitely.
    Observed on ssc-test 2026-08-08 with 7 ranges already completed on the
    volume and reconcile never running again.
    """
    base, vol = server
    run = {"range": {"startingLedger": 0, "latestLedgerNum": 1000,
                     "ledgersPerJob": 100}}
    assert _post(base, '/start', json.dumps(run))[0] == 200
    assert (vol / 'run.json').exists()

    # A fresh process: same volume, gate closed again.
    http_server.started.clear()
    jm.start_run(json.loads((vol / 'run.json').read_text()))

    assert http_server.started.is_set(), (
        "a restart restored the run but never opened the gate, so reconcile "
        "would block forever and the run would hang silently")


def test_status_says_whether_the_run_has_started(server):
    """Placeholder zeros are indistinguishable from a run with nothing done.

    Before the first reconcile pass /status answers with the module defaults, so
    a caller cannot tell "nothing recorded yet" from "never going to dispatch".
    That ambiguity is what let a wedged monitor be polled indefinitely: it kept
    answering 200 and nothing was ever unreachable.
    """
    base, _ = server
    assert json.loads(_get(base, '/status')[1])['started'] is False

    _post(base, '/start', json.dumps({"range": {"startingLedger": 0,
                                                "latestLedgerNum": 1000,
                                                "ledgersPerJob": 100}}))

    assert json.loads(_get(base, '/status')[1])['started'] is True
