"""RACE #4 -- the _wake Event is never cleared, so the terminal-poll backoff
never sleeps and TERMINAL_POLL_ATTEMPTS is spent in a tight loop.

These tests run the real `log_collector.poll_pod` against a fake kubelet log
endpoint and assert on what ends up on the logs volume (.log.gz, .metrics) and
on when the requests were actually issued. No source text is inspected: the
existing suite already pattern-matches this loop and still shipped the bug.

The scenario is the one that costs data in production: a worker pod goes
terminal and the kubelet needs a moment before it will serve the container's
final log. The collector is supposed to absorb that with three spaced retries.
With a sticky Event it burns all three inside a millisecond and finalizes on
nothing, losing the range's log, its txApply and its final peaks.
"""

import asyncio
import gzip
import json
import os
import time

import pytest

import config
import log_collector as lc

# Everything is scaled down from the shipped 10s so the tests run in ~2s. The
# ratios are what matter: the kubelet gate opens well after a millisecond-fast
# giveup and well before the third attempt of a correctly-spaced retry.
POLL = 0.2            # LOG_POLL_SECONDS under test
GATE = 0.4            # how long the kubelet 500s before serving the final log
ATTEMPTS = 3          # TERMINAL_POLL_ATTEMPTS, the shipped default

POD = 'pc-r300-a1-00001'
END = '300'
ATTEMPT = '1'

# What stellar-core prints on its way out. The medida block is the only place
# txApply exists -- the pod is about to be reaped, so if this read is missed the
# number is gone for good.
FINAL_LOG = (
    "2026-07-30T00:00:01Z catchup ledger 42000000\n"
    "2026-07-30T00:00:02Z metric 'ledger.transaction.apply'\n"
    "2026-07-30T00:00:03Z   count = 12\n"
    "2026-07-30T00:00:04Z   sum = 1500.0ms\n"
    "2026-07-30T00:00:05Z catchup completed\n"
)
EXPECTED_TX_SECONDS = 1.5


# --- fake kubelet log endpoint ----------------------------------------------

class _Resp:
    def __init__(self, status, body):
        self.status = status
        self._body = body.encode()

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False

    def raise_for_status(self):
        if self.status >= 400:
            raise RuntimeError(f"HTTP {self.status}")

    @property
    def content(self):
        body = self._body

        class _Chunks:
            async def iter_chunked(self, n):
                for i in range(0, len(body), n):
                    yield body[i:i + n]

        return _Chunks()


class FakeKubelet:
    """Serves one pod's log, with a delay before the final read is available.

    `open_after=None` never serves. Timestamps every request so a test can see
    whether the retries were spaced or fired back to back.
    """

    def __init__(self, open_after, body=FINAL_LOG):
        self.open_after = open_after
        self.body = body
        self.requests = []

    def get(self, url, params=None, headers=None):
        now = time.monotonic()
        self.requests.append(now)
        if self.open_after is None or now - self.requests[0] < self.open_after:
            return _Resp(500, '')
        return _Resp(200, self.body)

    @property
    def span(self):
        return self.requests[-1] - self.requests[0]


# --- driver ------------------------------------------------------------------

async def _drive(session, terminal_at_start=True, flip_after=None, timeout=10):
    """Run the real poll_pod, with a stand-in for one main-loop wake cycle.

    main() marks a pod terminal and then does `if name in _wake: set()`. That
    key only exists once the poller has reached its first wait, so the real loop
    lands the wake on a later cycle -- reproduced here by waiting for the key.
    The wake is delivered ONCE, as one main-loop cycle would: the bug is that
    one set is enough to disable every wait that follows.
    """
    terminal = {'v': terminal_at_start}

    async def main_loop_wake():
        if flip_after is not None:
            await asyncio.sleep(flip_after)
            terminal['v'] = True
        while POD not in lc._wake:
            await asyncio.sleep(0.001)
        lc._wake[POD].set()

    waker = asyncio.create_task(main_loop_wake())
    try:
        await asyncio.wait_for(
            lc.poll_pod(session, POD, END, ATTEMPT,
                        lambda p: terminal['v'],      # done()
                        lambda p: False),             # done_ok(): pod Failed
            timeout=timeout)
    finally:
        waker.cancel()


@pytest.fixture
def logs(tmp_path, monkeypatch):
    monkeypatch.setattr(config, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(lc, 'token', lambda: 'tok')
    monkeypatch.setattr(lc, 'LOG_POLL_SECONDS', POLL)
    monkeypatch.setattr(lc, 'TERMINAL_POLL_ATTEMPTS', ATTEMPTS)
    for d in (lc._wake, lc._pod_secs, lc._anon_peak, lc._ws_peak,
              lc._eph_peak, lc._peak_flushed, lc._streaming):
        d.clear()
    yield tmp_path
    for d in (lc._wake, lc._pod_secs, lc._anon_peak, lc._ws_peak,
              lc._eph_peak, lc._peak_flushed, lc._streaming):
        d.clear()


def _metrics(d):
    path = os.path.join(d, f'range-{END}-a{ATTEMPT}.metrics')
    if not os.path.exists(path):
        return {}
    with open(path) as fh:
        return json.load(fh)


def _archive(d):
    path = os.path.join(d, f'range-{END}-a{ATTEMPT}.log.gz')
    if not os.path.exists(path):
        return ''
    with gzip.open(path, 'rt') as fh:
        return fh.read()


def _done(d):
    return os.path.exists(os.path.join(d, f'range-{END}-a{ATTEMPT}.done'))


# --- tests -------------------------------------------------------------------

def test_a_terminal_pods_final_log_survives_a_moment_of_kubelet_lag(logs):
    # The pod is already terminal when its stream opens (Failed is a pollable
    # phase). The kubelet cannot serve the container's log yet -- the ordinary
    # case, it needs a moment after termination -- so the first reads 500.
    # TERMINAL_POLL_ATTEMPTS exists precisely to ride that out, and the gate
    # here opens inside the window three spaced retries cover.
    kubelet = FakeKubelet(open_after=GATE)
    asyncio.run(_drive(kubelet))

    assert _done(logs), "attempt never finalized"
    assert len(kubelet.requests) == ATTEMPTS, (
        f"expected the {ATTEMPTS}-attempt budget, saw {len(kubelet.requests)}")

    m = _metrics(logs)
    assert m.get('txApplySeconds') == EXPECTED_TX_SECONDS, (
        "the retry budget was spent before the kubelet could answer: txApply "
        f"lost (metrics={m}, retries spanned {kubelet.span * 1000:.1f}ms)")
    assert 'sum = 1500.0ms' in _archive(logs), (
        "the range's final log was never captured")
    assert 'catchup completed' in _archive(logs)


def test_the_terminal_retry_budget_is_spent_over_time_not_in_one_millisecond(logs):
    # Same pod, but the log endpoint never recovers. What is under test is the
    # shape of the giveup: three attempts must be spread across the backoff,
    # not fired back to back. Anything less and the budget is decorative.
    kubelet = FakeKubelet(open_after=None)
    asyncio.run(_drive(kubelet))

    assert _done(logs), "attempt never finalized"
    assert len(kubelet.requests) == ATTEMPTS, (
        f"expected the {ATTEMPTS}-attempt budget, saw {len(kubelet.requests)}")
    assert kubelet.span >= POLL, (
        f"{ATTEMPTS} terminal polls were spent in {kubelet.span * 1000:.1f}ms; "
        f"they should span at least one {POLL}s backoff")


def test_a_pod_going_terminal_still_cuts_the_routine_wait_short(logs, monkeypatch):
    # Guard on the other side of the fix: clearing the Event must not turn the
    # wait back into a blind sleep. The poll interval is 5s here; the pod goes
    # terminal just after the first poll, and its final read has to happen
    # within the pod-list cadence, not 5s later.
    monkeypatch.setattr(lc, 'LOG_POLL_SECONDS', 5.0)
    kubelet = FakeKubelet(open_after=0)          # always serves

    started = time.monotonic()
    asyncio.run(_drive(kubelet, terminal_at_start=False, flip_after=0.05,
                       timeout=2))
    elapsed = time.monotonic() - started

    assert _done(logs)
    assert _metrics(logs).get('txApplySeconds') == EXPECTED_TX_SECONDS
    assert elapsed < 2, (
        f"final read waited {elapsed:.2f}s for a pod that went terminal "
        "immediately; the wake was not delivered")
