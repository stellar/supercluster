"""RACE #3 -- a torn .log.gz member kills a whole reconcile pass.

The log-collector appends a gzip member to range-<end>-a<n>.log.gz in place,
with no temp+rename, so a reader that looks while a poll is mid-write sees a
truncated member. job_monitor reads that same file to recover txApplySeconds
and guards it with `except OSError`, which does not cover the EOFError that
gzip raises on a truncated member.

Consequence: one in-flight log write aborts the entire reconcile pass -- no
recording, no PVC release, no reaping and no dispatch for ANY of the ~4000
ranges, not just the one whose archive was being written. And because the torn
bytes stay on disk, it repeats on every subsequent pass.

Every test here drives the real code and asserts on observed state: what
reconcile() returned, what landed in progress.json, which Jobs/PVCs exist, and
what a reader sees on disk while the collector writes.
"""

import asyncio
import gzip
import io
import os
import random

import pytest

import job_monitor as jm
import log_collector as lc


# --- building the artefact the race leaves on disk --------------------------

def _gzip_member(text):
    """One complete, self-contained gzip member -- what one finished poll adds."""
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode='wb', mtime=0) as fh:
        fh.write(text.encode())
    return buf.getvalue()


def write_torn_archive(path, settled="startup line\n", in_flight=None):
    """A .log.gz exactly as an interrupted in-place append leaves it.

    One complete member from an earlier poll, followed by the first half of the
    member the current poll is still writing. This is byte-for-byte the shape an
    in-place gzip append produces once its buffer has flushed but the member has
    not been closed; the live-writer version of the same thing is
    test_collector_append_never_exposes_a_partial_member_to_a_reader below.
    """
    if in_flight is None:
        in_flight = "".join(f"line {i} of the poll that is still running\n"
                            for i in range(200))
    partial = _gzip_member(in_flight)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as fh:
        fh.write(_gzip_member(settled))
        fh.write(partial[:max(24, len(partial) // 2)])
    # Guard: the file we just built must actually be the torn artefact, or the
    # test below would pass for the wrong reason.
    with pytest.raises(EOFError):
        with gzip.open(path, 'rt') as fh:
            fh.read()
    return path


CORE_TAIL = (
    "2026-07-30T00:00:00Z metric 'ledger.transaction.apply'\n"
    "2026-07-30T00:00:00Z   count = 12345\n"
    "2026-07-30T00:00:00Z   sum = 4200.0ms\n"
)


def write_whole_archive(path, text=CORE_TAIL):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as fh:
        fh.write(_gzip_member(text))
    return path


# --- reader side: the reconcile pass ----------------------------------------

def test_a_torn_archive_does_not_abort_the_reconcile_pass(cluster):
    """A succeeded range whose archive is mid-append must still be recorded.

    Nothing about this range is unusual apart from the collector happening to
    be writing when reconcile looked.
    """
    cluster.reconcile()                       # dispatches r300, r200
    cluster.advance(300, 'succeeded')
    # The collector has not flushed .metrics yet, so the archive is the only
    # source for txApply -- and it is exactly the file being written.
    write_torn_archive(jm.log_path('300', 1))

    result = cluster.reconcile()

    # The pass completed and did all of its work.
    assert '300' in cluster.completed(), "succeeded range was never recorded"
    assert cluster.completed()['300']['attempts'] == 1
    assert 'pc-data-r300' not in cluster.pvcs(), "completed range kept its volume"
    assert result['created'] == 1, "the freed slot was never refilled"
    assert 'pc-r100-a1' in cluster.jobs()
    # The unreadable archive costs the metric for this range, nothing more.
    assert cluster.completed()['300']['txApply'] is None


def test_a_torn_archive_costs_one_range_not_the_other_ranges_in_the_pass(cluster):
    """Blast radius. Two ranges finish together; one has a torn archive.

    The healthy one must be recorded, keep its measurements and be reaped in
    the same pass, and the third range must still be dispatched.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.advance(200, 'succeeded')
    # r300: collector finished cleanly.
    cluster.finalize(300, 1, tx_apply=12.5, peaks={'peakRssBytes': 111})
    # r200: collector is mid-poll, archive torn, nothing durable yet.
    write_torn_archive(jm.log_path('200', 1))

    result = cluster.reconcile()

    completed = cluster.completed()
    assert set(completed) == {'300', '200'}
    # The healthy range is untouched by its neighbour's corrupt file.
    assert completed['300']['txApply'] == 12.5
    assert completed['300']['peakRssBytes'] == 111
    assert 'pc-r300-a1' not in cluster.jobs(), "finalized range was not reaped"
    # The torn range pays, and only the torn range.
    assert completed['200']['txApply'] is None
    # Dispatch still happened.
    assert result['created'] == 1
    assert 'pc-r100-a1' in cluster.jobs()
    assert cluster.failed() == {}


def test_a_never_repaired_torn_archive_does_not_wedge_the_run(cluster):
    """The torn bytes are durable, so the reader hits them on every pass.

    Once a range is recorded with txApply=None the backfill branch re-reads the
    archive each cycle, so a single corrupt file is not a one-pass outage -- it
    stops the run permanently. Drive the whole run to completion over it.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.advance(200, 'succeeded')
    cluster.finalize(200, 1, tx_apply=7.0)
    # r300's archive is torn and nobody ever fixes it.
    torn = write_torn_archive(jm.log_path('300', 1))

    cluster.reconcile()                        # records 300 + 200, dispatches 100
    assert 'pc-r100-a1' in cluster.jobs()
    cluster.advance(100, 'succeeded')
    cluster.finalize(100, 1, tx_apply=3.0)

    result = cluster.reconcile()               # records 100
    result = cluster.reconcile()               # steady state, still re-reading 300

    assert os.path.exists(torn), "test no longer exercises the corrupt file"
    assert set(cluster.completed()) == {'300', '200', '100'}
    assert cluster.failed() == {}
    assert result['remaining'] == 0
    assert result['in_progress'] == []


def test_a_range_recovers_its_metric_once_the_collector_finishes(cluster):
    """Bounded in time as well as in scope.

    The torn read costs txApply for exactly as long as the archive is torn: the
    moment the collector lands .metrics, the backfill branch picks it up.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    write_torn_archive(jm.log_path('300', 1))

    cluster.reconcile()
    assert cluster.completed()['300']['txApply'] is None

    # The collector's poll completes and it writes what it scanned out of the
    # stream. The archive on disk is still torn.
    cluster.finalize(300, 1, tx_apply=88.25, peaks={'peakRssBytes': 222})
    cluster.reconcile()

    assert cluster.completed()['300']['txApply'] == 88.25
    assert cluster.completed()['300']['peakRssBytes'] == 222
    assert 'pc-r300-a1' not in cluster.jobs()


def test_a_readable_archive_is_still_the_txapply_fallback(cluster):
    """Guard rail: widening the except must not swallow a good read.

    Without this, 'catch everything and return None' would pass every test
    above while silently deleting the archive fallback.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    write_whole_archive(jm.log_path('300', 1))   # no .metrics: archive is the source

    cluster.reconcile()

    assert cluster.completed()['300']['txApply'] == pytest.approx(4.2)


# --- writer side: the collector's append ------------------------------------

class _FakeContent:
    def __init__(self, body):
        self._body = body.encode()

    async def iter_chunked(self, n):
        for i in range(0, len(self._body), n):
            yield self._body[i:i + n]


class _FakeResponse:
    status = 200

    def __init__(self, body):
        self.content = _FakeContent(body)

    def raise_for_status(self):
        pass

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False


class _FakeSession:
    """Just enough aiohttp for _poll_once: one GET returning a log body."""

    def __init__(self, body):
        self._body = body

    def get(self, url, params=None, headers=None):
        return _FakeResponse(self._body)


class _ReadingScanner(lc.TxApplyScanner):
    """The monitor, reading the archive while the collector writes it.

    feed() is called once per log line from inside the collector's write loop,
    which makes "a reader looked mid-append" deterministic instead of a timing
    coin flip.
    """

    def __init__(self, path, every=250):
        super().__init__()
        self.path = path
        self.every = every
        self.lines = 0
        self.observations = []
        self.errors = []

    def feed(self, line):
        super().feed(line)
        self.lines += 1
        if self.lines % self.every:
            return
        try:
            with gzip.open(self.path, 'rt') as fh:
                self.observations.append(fh.read())
        except Exception as exc:                     # noqa: BLE001 -- that's the point
            self.errors.append((self.lines, type(exc).__name__))


def _log_body(start, count, rng):
    """Timestamped, poorly-compressible pod log lines, as kubelet serves them."""
    return "".join(
        "2026-07-30T00:00:00.%09dZ %064x %064x\n"
        % (i, rng.getrandbits(256), rng.getrandbits(256))
        for i in range(start, start + count)
    )


def test_collector_append_never_exposes_a_partial_member_to_a_reader(tmp_path, monkeypatch):
    """The archive on disk must only ever hold complete members.

    Two polls. The first settles a complete member. The second is a large poll
    -- well inside MAX_POLL_CHARS -- during which a reader inspects the file
    every 250 lines. Every one of those reads must succeed and must see exactly
    the last settled content.
    """
    monkeypatch.setattr(lc, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(lc, 'token', lambda: 'test-token')
    path = lc.base('300', 1) + '.log.gz'
    rng = random.Random(7)

    first = asyncio.run(lc._poll_once(
        _FakeSession(_log_body(0, 5, rng)), 'pod-a', '300', 1, None,
        lc.TxApplyScanner()))
    last_ts, gone = first
    assert not gone
    with gzip.open(path, 'rt') as fh:
        settled = fh.read()
    assert settled, "first poll wrote nothing; the test has no baseline"

    watcher = _ReadingScanner(path)
    asyncio.run(lc._poll_once(
        _FakeSession(_log_body(5, 12000, rng)), 'pod-a', '300', 1, last_ts, watcher))

    assert watcher.observations, "the reader never got to look"
    assert watcher.errors == [], (
        f"{len(watcher.errors)} of {len(watcher.errors) + len(watcher.observations)} "
        f"mid-append reads hit a torn member, e.g. {watcher.errors[:3]}")
    assert set(watcher.observations) == {settled}, (
        "a reader saw content that was neither the previous complete archive "
        "nor the finished one")

    # And the append still did its job once it finished.
    with gzip.open(path, 'rt') as fh:
        final = fh.read()
    assert final.startswith(settled)
    assert len(final.splitlines()) == 12005
