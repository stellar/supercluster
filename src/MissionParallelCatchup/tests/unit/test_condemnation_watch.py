"""Detecting a condemnation fast enough to still open a follow.

The follow itself was never the problem: `_follow_slots` is a 256-wide semaphore
and a real reclaim condemns tens of pods, so it never fell back to polling. What
lost the metric was seeing the condition too late. stellar-core exits about a
second after SIGTERM and the pod object is reaped behind it, so a condemned pod
exists for a few seconds -- and the pod-list sweep runs every POLL_SECONDS=5.

Measured on ssc-test at prestopSleepSeconds=5: of 52 mid-replay legs, 32 lost
txApply. Seven were never seen condemned at all; the other 25 were seen, wrote
their disruptionReason, and still lost it because the follow opened after the
pod was gone.

So these tests are about latency and about the two detectors agreeing, not about
whether a follow works.
"""

import asyncio
import json

import pytest

import config
import log_collector as lc


def pod(name='w-1', phase='Running', end='300', attempt='1',
        reason='EvictionByEvictionAPI', rv='100'):
    conditions = ([{'type': 'DisruptionTarget', 'status': 'True', 'reason': reason}]
                  if reason else [])
    return {'metadata': {'name': name,
                         'resourceVersion': rv,
                         'labels': {config.LABEL_RUN: config.RUN_NAME,
                                    config.LABEL_RANGE: end,
                                    config.LABEL_ATTEMPT: attempt}},
            'status': {'phase': phase, 'conditions': conditions}}


@pytest.fixture
def collector(tmp_path, monkeypatch):
    """Collector module state pointed at a temp volume, reset between tests."""
    monkeypatch.setattr(config, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(lc, '_doomed', {})
    monkeypatch.setattr(lc, '_wake', {})
    monkeypatch.setattr(lc, 'token', lambda: 'test-token')
    # The real backoff is a wall-clock second; these tests advance the loop by
    # ticks, not time, so a retry would never come back.
    monkeypatch.setattr(lc, 'WATCH_RETRY_SECONDS', 0)
    monkeypatch.setattr(lc, '_tasks', {})
    monkeypatch.setattr(lc, '_streamed', set())
    monkeypatch.setattr(lc, '_stream_ctx', {})
    monkeypatch.setattr(lc, '_streaming', {})
    return tmp_path


@pytest.fixture
def ready(collector, monkeypatch):
    """A collector whose ensure_stream can actually open something.

    Records every poll_pod that gets started, so a second reader on one pod is
    visible rather than silent.
    """
    opened = []

    async def fake_poll(session, name, end, attempt, done, done_ok):
        opened.append((name, end, attempt))
        await asyncio.sleep(3600)

    monkeypatch.setattr(lc, 'poll_pod', fake_poll)
    lc._stream_ctx.update(session=object(), terminal={}, succeeded={})
    return opened


async def drain(fn, ticks=30):
    fn()
    for _ in range(ticks):
        await asyncio.sleep(0)
    for t in list(lc._tasks.values()):
        t.cancel()


# --- ensure_stream: one registry, one reader ---------------------------------

def test_a_stream_is_opened_once_and_only_once(collector, ready):
    async def go():
        await drain(lambda: [lc.ensure_stream('w-1', '300', '1', 'Running'),
                             lc.ensure_stream('w-1', '300', '1', 'Running'),
                             lc.ensure_stream('w-1', '300', '1', 'Running')])
    asyncio.run(go())
    assert ready == [('w-1', '300', '1')], \
        "two readers would re-append the same lines and race write_state"


def test_a_completed_stream_is_not_reopened(collector, ready):
    lc._streamed.add('w-1')
    async def go():
        await drain(lambda: lc.ensure_stream('w-1', '300', '1', 'Running'))
    asyncio.run(go())
    assert ready == []


@pytest.mark.parametrize('phase', ['Pending', 'Unknown'])
def test_an_unpollable_phase_is_left_for_a_later_call(collector, ready, phase):
    # Its log endpoint answers 400 "waiting to start"; the retry is the next
    # event or the next sweep, whichever lands first.
    async def go():
        await drain(lambda: lc.ensure_stream('w-1', '300', '1', phase))
    asyncio.run(go())
    assert ready == []
    assert 'w-1' not in lc._tasks


def test_nothing_opens_before_main_publishes_its_context(collector, monkeypatch):
    # ensure_stream is reachable from the watch, which starts inside main(). If
    # an event landed first, opening with no session would throw in a task
    # nobody awaits.
    monkeypatch.setattr(lc, '_stream_ctx', {})
    assert lc.ensure_stream('w-1', '300', '1', 'Running') is False


# --- the two callers cannot double up ----------------------------------------

def test_the_watch_opens_the_stream_without_waiting_for_the_sweep(collector, ready):
    # The whole point: at 900 pods the pod-list cycle reached 925s, and a pod
    # condemned in that window died unread.
    asyncio.run(run_watch(FakeSession([FakeResponse([
        {'type': 'ADDED', 'object': pod(reason=None)},
    ])])))
    assert ready == [('w-1', '300', '1')]


def test_the_sweep_still_opens_a_stream_the_watch_missed(collector, ready):
    # Events are genuinely dropped across a reconnect, so the loop stays as a
    # backstop rather than being retired.
    async def go():
        await drain(lambda: lc.ensure_stream('w-1', '300', '1', 'Running'))
    asyncio.run(go())
    assert ready == [('w-1', '300', '1')]


def test_watch_then_sweep_still_yields_one_reader(collector, ready):
    async def go():
        lc.ensure_stream('w-1', '300', '1', 'Running')   # watch
        lc.ensure_stream('w-1', '300', '1', 'Running')   # sweep, same cycle
        for _ in range(30):
            await asyncio.sleep(0)
        for t in list(lc._tasks.values()):
            t.cancel()
    asyncio.run(go())
    assert ready == [('w-1', '300', '1')]


def test_a_pod_condemned_as_it_appears_gets_a_reader_before_being_marked(collector, ready):
    # Ordering inside the watch: _mark_condemned only sets _doomed and fires
    # _wake, both no-ops when no poller exists. Marking first would leave the
    # condemnation with nothing to act on -- which is exactly how five -a2 legs
    # produced 0-byte archives.
    asyncio.run(run_watch(FakeSession([FakeResponse([
        {'type': 'ADDED', 'object': pod()},
    ])])))
    assert ready == [('w-1', '300', '1')], "the stream must exist first"
    assert lc._doomed.get('w-1') == 'EvictionByEvictionAPI'


def metrics_of(vol, end='300', attempt='1'):
    path = vol / f"range-{end}-a{attempt}.metrics"
    return json.loads(path.read_text()) if path.exists() else {}


# --- _mark_condemned: the shared decision ------------------------------------

def test_a_condemnation_is_recorded_and_wakes_the_poller(collector):
    lc._wake['w-1'] = asyncio.Event()

    assert lc._mark_condemned(pod(), 'w-1', '300', '1') is True
    assert lc._doomed['w-1'] == 'EvictionByEvictionAPI'
    assert metrics_of(collector)['disruptionReason'] == 'EvictionByEvictionAPI'
    assert lc._wake['w-1'].is_set(), "the poller must not sleep out its interval"


def test_marking_twice_is_a_no_op(collector):
    # The watch and the sweep both see the same object. Whichever is first does
    # the work; the second must not re-open a stream or rewrite the reason.
    assert lc._mark_condemned(pod(), 'w-1', '300', '1') is True
    assert lc._mark_condemned(pod(reason='DeletionByTaintManager'),
                              'w-1', '300', '1') is False
    assert lc._doomed['w-1'] == 'EvictionByEvictionAPI'


def test_an_uncondemned_pod_is_left_alone(collector):
    assert lc._mark_condemned(pod(reason=None), 'w-1', '300', '1') is False
    assert lc._doomed == {}
    assert metrics_of(collector) == {}


@pytest.mark.parametrize('phase', ['Succeeded', 'Failed'])
def test_a_finished_pod_is_not_followed(collector, phase):
    # Its log is already complete, and leaving the flag set would re-open a
    # stream on a dead pod every iteration.
    assert lc._mark_condemned(pod(phase=phase), 'w-1', '300', '1') is False
    assert lc._doomed == {}


def test_the_reason_is_carried_through_to_the_metrics_file(collector):
    # EvictionByEvictionAPI is a drain that still owes a SIGTERM; a TaintManager
    # stamp lands on a container that already died unsignalled. A lost txApply
    # means different things in the two cases, so the label has to survive.
    lc._mark_condemned(pod(reason='DeletionByTaintManager'), 'w-1', '300', '1')
    assert metrics_of(collector)['disruptionReason'] == 'DeletionByTaintManager'


# --- watch_condemnations: the stream -----------------------------------------

class FakeResponse:
    def __init__(self, lines, status=200):
        self.status = status
        self.content = self._iter(lines)

    async def _iter(self, lines):
        for line in lines:
            yield line if isinstance(line, bytes) else json.dumps(line).encode()

    def raise_for_status(self):
        if self.status >= 400:
            raise RuntimeError(f"status {self.status}")

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False


class FakeSession:
    """Serves one scripted watch response per connection.

    Once the script runs out every further connection raises, which both stops
    the test looping forever and exercises the retry path.
    """

    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []

    def get(self, url, params=None, headers=None):
        self.calls.append(dict(params or {}))
        if not self.responses:
            raise ConnectionError("no more scripted responses")
        nxt = self.responses.pop(0)
        if isinstance(nxt, Exception):
            raise nxt
        return nxt


async def run_watch(session, ticks=40):
    task = asyncio.create_task(lc.watch_condemnations(session))
    for _ in range(ticks):
        await asyncio.sleep(0)
    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass


def test_a_modified_event_condemns_immediately(collector):
    monkey = FakeSession([FakeResponse([
        {'type': 'MODIFIED', 'object': pod()},
    ])])
    asyncio.run(run_watch(monkey))

    assert lc._doomed.get('w-1') == 'EvictionByEvictionAPI', \
        "the watch, not the 5s sweep, is what has to catch this"
    assert metrics_of(collector)['disruptionReason'] == 'EvictionByEvictionAPI'


def test_a_healthy_pod_event_does_nothing(collector):
    asyncio.run(run_watch(FakeSession([FakeResponse([
        {'type': 'MODIFIED', 'object': pod(reason=None)},
    ])])))
    assert lc._doomed == {}


def test_deleted_events_are_ignored(collector):
    # By DELETED the object is already gone; acting on it would open a stream
    # against a pod that cannot answer.
    asyncio.run(run_watch(FakeSession([FakeResponse([
        {'type': 'DELETED', 'object': pod()},
    ])])))
    assert lc._doomed == {}


def test_a_reconnect_resumes_from_the_last_resourceVersion(collector):
    session = FakeSession([
        FakeResponse([{'type': 'MODIFIED', 'object': pod(reason=None, rv='517')}]),
        FakeResponse([{'type': 'MODIFIED', 'object': pod(rv='518')}]),
    ])
    asyncio.run(run_watch(session))

    assert 'resourceVersion' not in session.calls[0], "first connect starts cold"
    assert session.calls[1]['resourceVersion'] == '517', \
        "resuming re-delivers only what was missed instead of re-syncing"


def test_a_bookmark_advances_the_resume_point(collector):
    # Bookmarks exist so an idle watch does not fall behind and get a 410 on
    # reconnect. Ignoring them would strand the resume point at the last real
    # change, which on a quiet run can be far in the past.
    session = FakeSession([
        FakeResponse([{'type': 'BOOKMARK',
                       'object': {'metadata': {'resourceVersion': '900'}}}]),
        FakeResponse([]),
    ])
    asyncio.run(run_watch(session))
    assert session.calls[1]['resourceVersion'] == '900'


def test_an_expired_resourceVersion_restarts_cold(collector):
    # 410 Gone means our position aged out of the apiserver's history. Retrying
    # with the same version loops forever; dropping it re-syncs.
    session = FakeSession([
        FakeResponse([{'type': 'MODIFIED', 'object': pod(reason=None, rv='7')}]),
        FakeResponse([], status=410),
        FakeResponse([]),
    ])
    asyncio.run(run_watch(session))

    assert session.calls[1]['resourceVersion'] == '7'
    assert 'resourceVersion' not in session.calls[2], "a 410 has to reset it"


def test_an_error_event_carrying_410_also_restarts_cold(collector):
    # The same condition arrives as an in-stream ERROR event, not only as a
    # status code on the connection.
    session = FakeSession([
        FakeResponse([{'type': 'ERROR',
                       'object': {'code': 410, 'metadata': {}}}]),
        FakeResponse([]),
    ])
    asyncio.run(run_watch(session))
    assert 'resourceVersion' not in session.calls[1]


def test_the_watch_survives_a_dropped_connection(collector):
    # Detection degrading to the pod-list sweep is survivable; the collector
    # dying is not. A watch that raised out of main() would take the whole
    # sidecar and every in-flight stream with it.
    session = FakeSession([
        ConnectionError("apiserver went away"),
        FakeResponse([{'type': 'MODIFIED', 'object': pod()}]),
    ])
    asyncio.run(run_watch(session))
    assert lc._doomed.get('w-1') == 'EvictionByEvictionAPI'


def test_malformed_lines_do_not_kill_the_stream(collector):
    session = FakeSession([FakeResponse([
        b'{not json',
        b'',
        json.dumps({'type': 'MODIFIED', 'object': pod()}).encode(),
    ])])
    asyncio.run(run_watch(session))
    assert lc._doomed.get('w-1') == 'EvictionByEvictionAPI'


def test_a_pod_without_a_range_label_is_skipped(collector):
    # The job-monitor pod carries the run label too, and it has no range.
    stray = pod()
    del stray['metadata']['labels'][config.LABEL_RANGE]
    asyncio.run(run_watch(FakeSession([FakeResponse([
        {'type': 'MODIFIED', 'object': stray},
    ])])))
    assert lc._doomed == {}


def test_the_watch_asks_for_bookmarks_and_a_bounded_lifetime(collector):
    # An unbounded watch that dies silently stops detecting and nothing notices;
    # the timeout is what makes it self-heal.
    session = FakeSession([FakeResponse([])])
    asyncio.run(run_watch(session))
    assert session.calls[0]['watch'] == 'true'
    assert session.calls[0]['allowWatchBookmarks'] == 'true'
    assert session.calls[0]['timeoutSeconds'] == str(lc.WATCH_TIMEOUT_SECONDS)
    assert session.calls[0]['labelSelector'] == f"{config.LABEL_RUN}={config.RUN_NAME}"
