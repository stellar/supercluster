"""poll_pod / _poll_once: when a read ends an attempt and when it does not.

Every one of these is executed rather than pattern-matched. That is not a
stylistic preference: an earlier generation of these tests asserted on an
`except ClientResponseError` branch that raise_for_status could never reach and
passed green against dead code, and another pinned the literal
`gzip.open(..., 'at')` and went red over the atomic-append fix, which preserved
everything the test existed to protect.

The fake apiserver here answers the log endpoint only. What is asserted is what
lands on the shared volume -- the archive, .metrics, .done -- because that is
the entire interface the monitor sees.
"""

import asyncio
import gzip
import os

import pytest

import job_monitor as jm
import log_collector as lc


@pytest.fixture
def volume(tmp_path, monkeypatch):
    monkeypatch.setattr(lc, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(jm, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(lc, 'token', lambda: 'tok')
    monkeypatch.setattr(lc, 'LOG_POLL_SECONDS', 0.02)
    monkeypatch.setattr(lc, 'TERMINAL_POLL_ATTEMPTS', 2)
    for name in ('_eph_peak', '_anon_peak', '_ws_peak', '_peak_flushed',
                 '_streaming', '_pod_secs', '_wake'):
        monkeypatch.setattr(lc, name, {})
    return tmp_path


class _Resp:
    def __init__(self, status, body='', after_read=None):
        self.status = status
        self._body = body.encode()
        self._after_read = after_read

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False

    def raise_for_status(self):
        if self.status >= 400:
            raise RuntimeError(f"HTTP {self.status}")

    @property
    def content(self):
        data, after = self._body, self._after_read

        class _Chunks:
            async def iter_chunked(self, n):
                for i in range(0, len(data), n):
                    yield data[i:i + n]
                if after is not None:
                    after()

        return _Chunks()


class Apiserver:
    """Answers each log GET from `answers`, repeating the last one forever."""

    def __init__(self, *answers):
        self.answers = list(answers)
        self.params = []

    def get(self, url, params=None, headers=None):
        self.params.append(dict(params or {}))
        i = min(len(self.params) - 1, len(self.answers) - 1)
        return self.answers[i]


def archive(end='300', attempt='1'):
    path = lc.base(end, attempt) + '.log.gz'
    if not os.path.exists(path):
        return ''
    with gzip.open(path, 'rt') as fh:
        return fh.read()


def drive(session, terminal, timeout=3):
    async def go():
        await asyncio.wait_for(
            lc.poll_pod(session, 'w-1', '300', '1',
                        lambda p: terminal(), lambda p: False),
            timeout=timeout)
    asyncio.run(go())


# --- the pod object is gone ---------------------------------------------------

def test_a_404_finalizes_what_was_already_streamed(volume):
    """The pod object is gone, but the bytes already read still owe a tx_apply,
    and .done is what lets the monitor stop waiting on the Job.

    This path used to not exist: a pod deleted while Running left its stream
    retrying for the rest of the run, holding a connection slot."""
    body = ("2026-07-30T00:00:01Z metric 'ledger.transaction.apply'\n"
            "2026-07-30T00:00:02Z              sum = 1500.0ms\n")
    drive(Apiserver(_Resp(200, body), _Resp(404)), lambda: False)

    assert jm._attempt_finalized('300', 1), "a vanished pod never finalized"
    assert jm.tx_apply_for_range('300', 1) == pytest.approx(1.5)
    assert 'sum = 1500.0ms' in archive()


def test_an_interrupted_read_on_a_live_pod_does_not_finalize(volume):
    """Still running, so retrying is correct. Finalizing here writes a
    truncated peak and leaves the range looking measured when it is not."""
    with pytest.raises(asyncio.TimeoutError):
        drive(Apiserver(_Resp(500)), lambda: False, timeout=0.4)

    assert not jm._attempt_finalized('300', 1), \
        "a live pod's attempt was closed out on a transient read failure"


def test_a_terminal_pod_whose_polls_keep_failing_still_finalizes(volume):
    """The other side of it: the container has exited and its log is not
    coming back, so the loop has to decide to stop asking rather than spin on a
    dead pod and never write its metrics."""
    drive(Apiserver(_Resp(500)), lambda: True)

    assert jm._attempt_finalized('300', 1)


# --- the read that catches the last lines ------------------------------------

def test_terminal_is_sampled_before_the_poll_not_after(volume):
    """A pod that exits mid-poll must still get one more read.

    If `done()` were consulted after the poll instead of before it, the poll
    that was in flight when the container exited would be treated as the final
    one -- and everything the container wrote on its way out, which is where
    the medida block lives, is dropped.
    """
    state = {'terminal': False}
    first = _Resp(200, "2026-07-30T00:00:01Z catchup ledger 42000000\n",
                  after_read=lambda: state.update(terminal=True))
    last = _Resp(200,
                 "2026-07-30T00:00:09Z metric 'ledger.transaction.apply'\n"
                 "2026-07-30T00:00:10Z              sum = 1500.0ms\n")

    drive(Apiserver(first, last), lambda: state['terminal'])

    assert 'catchup ledger 42000000' in archive()
    assert 'sum = 1500.0ms' in archive(), \
        "the read after the pod went terminal never happened"
    assert jm.tx_apply_for_range('300', 1) == pytest.approx(1.5)


# --- resuming a read ----------------------------------------------------------

def test_a_poll_resumes_from_the_last_durable_timestamp(volume):
    """Without sinceTime a reconnect re-reads the whole log from the start:
    one full re-read per pod per reconnect, at 2096 pods."""
    api = Apiserver(_Resp(200, "2026-07-30T00:00:05Z line\n"))
    scanner = lc.TxApplyScanner()
    last, gone = asyncio.run(lc._poll_once(api, 'w-1', '300', '1', None, scanner))

    assert api.params[0].get('sinceTime') is None
    assert last == '2026-07-30T00:00:05Z' and gone is False

    asyncio.run(lc._poll_once(api, 'w-1', '300', '1', last, scanner))
    assert api.params[1]['sinceTime'] == '2026-07-30T00:00:05Z', \
        "the second poll did not resume where the first stopped"


def test_the_second_granularity_overlap_is_deduped_exactly(volume):
    """sinceTime only accepts whole seconds, so a resume deliberately re-reads
    the second it stopped in. Every line carries a nanosecond timestamp, so the
    overlap is removed per line rather than tolerated as duplicates."""
    body = ("2026-07-30T00:00:05.100000000Z already seen\n"
            "2026-07-30T00:00:05.900000000Z brand new\n")
    api = Apiserver(_Resp(200, body))
    asyncio.run(lc._poll_once(api, 'w-1', '300', '1',
                              '2026-07-30T00:00:05.100000000Z',
                              lc.TxApplyScanner()))

    written = archive()
    assert 'brand new' in written
    assert 'already seen' not in written


def test_untimestamped_kubelet_text_is_kept_but_never_resumed_from(volume):
    """"unable to retrieve container logs for containerd://..." partitions to
    "unable", and sinceTime=unableZ is a 400 on every later request for that
    pod, forever."""
    api = Apiserver(_Resp(200, "unable to retrieve container logs for "
                               "containerd://9f2c1a\n"))
    last, _ = asyncio.run(lc._poll_once(api, 'w-1', '300', '1', None,
                                        lc.TxApplyScanner()))

    assert last is None, f"junk became a resume point: {last!r}"
    assert 'unable to retrieve' in archive(), "the line was dropped instead"


# --- bounds -------------------------------------------------------------------

def test_an_unterminated_blob_is_capped_not_buffered_forever(volume, monkeypatch):
    """A meter that never emits a newline would otherwise grow the buffer until
    the collector OOMs -- 2096 streams doing it at once."""
    monkeypatch.setattr(lc, 'MAX_POLL_CHARS', 1024)
    api = Apiserver(_Resp(200, 'x' * (4 * 1024 * 1024)))

    asyncio.run(lc._poll_once(api, 'w-1', '300', '1', None, lc.TxApplyScanner()))

    # One chunk's worth of overshoot is inherent -- the cap is checked between
    # chunks -- but the 4 MiB body must not have been buffered whole.
    assert len(archive()) < 256 * 1024, "the poll buffered the entire blob"


def test_polls_are_bounded_by_a_semaphore(volume, monkeypatch):
    """The whole point of polling over follow=true: concurrency is a tuning
    parameter, not a function of how many pods exist."""
    live = {'now': 0, 'max': 0}

    class _Counting(Apiserver):
        def get(self, url, params=None, headers=None):
            live['now'] += 1
            live['max'] = max(live['max'], live['now'])
            resp = super().get(url, params, headers)
            live['now'] -= 1
            return resp

    async def go():
        monkeypatch.setattr(lc, '_poll_slots', asyncio.Semaphore(2))
        api = _Counting(_Resp(200, "2026-07-30T00:00:01Z line\n"))
        await asyncio.gather(*[
            lc._poll_once(api, 'w-1', '300', str(n), None, lc.TxApplyScanner())
            for n in range(8)])

    asyncio.run(go())
    assert live['max'] <= 2, f"{live['max']} polls were in flight at once"


# --- the allowlist that decides a pod is worth polling at all -----------------

def test_only_phases_whose_log_endpoint_can_answer_are_pollable():
    """An allowlist, not "skip Pending". A container that has not started
    answers 400 "waiting to start" -- 60 of 88 poll failures right after the
    polling switch -- and Unknown means the node stopped reporting, so that
    poll cannot succeed either. The terminal phases stay in: a terminal pod is
    where the final output lives.
    """
    assert set(lc.POLLABLE_PHASES) == {'Running', 'Succeeded', 'Failed'}


def test_the_terminal_retry_budget_can_absorb_a_transient_failure():
    """At 1 a single 500 ends the attempt on whatever had been read."""
    assert lc.TERMINAL_POLL_ATTEMPTS >= 2


def test_poll_concurrency_is_a_modest_default():
    """It sizes the connection pool as well (MAX_CONCURRENT_POLLS + 64), so
    both directions cost: too low starves the retries, too high recreates the
    per-pod connection load polling exists to remove."""
    assert 16 <= lc.MAX_CONCURRENT_POLLS <= 256


def test_the_wake_entry_is_dropped_when_the_attempt_finishes(volume):
    """One _wake entry per pod, and pods are per range per attempt: 3979 ranges
    plus their retries would otherwise accumulate for the life of the run."""
    drive(Apiserver(_Resp(404)), lambda: True)

    assert jm._attempt_finalized('300', 1)
    assert lc._wake == {}, f"the poller's Event outlived its attempt: {lc._wake}"
