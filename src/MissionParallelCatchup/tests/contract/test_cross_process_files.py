"""Two processes, one volume, one set of filenames.

The monitor and the collector never talk. Everything they agree on is a file on
the shared /logs PVC: the archive, the per-attempt metrics, the verdict, the
resume bookkeeping, and the marker that licenses the monitor to reap a Job. A
disagreement about any of those names is silent -- the reader simply finds
nothing, which reads as "not measured yet" and never as "broken".

The names are compared by calling both sides' path functions against the same
LOG_DIR, so a refactor that keeps the layout is free. The chart is checked too:
the layout only means anything if both containers mount the same volume there.
"""

import gzip
import os

import pytest

import config
import records
import attempts
import job_monitor as jm
import log_collector as lc

import _artifacts as art

END, ATTEMPT = '31005951', 2


@pytest.fixture
def shared(tmp_path, monkeypatch):
    """Both modules pointed at one directory, as the pod's volume gives them."""
    monkeypatch.setattr(config, 'LOG_DIR', str(tmp_path))
    return tmp_path


# --- the filenames ------------------------------------------------------------

def test_both_processes_name_the_same_metrics_file(shared):
    """The collector writes it; the monitor reads peaks and txApply out of it."""
    assert records.metrics_path(END, ATTEMPT) == lc.base(END, ATTEMPT) + '.metrics'


def test_both_processes_name_the_same_done_marker(shared):
    """The marker is the collector's "I am finished with this attempt".

    The monitor will not reap a Job without it -- and reaping deletes the pod,
    which is the last place peaks can still be read from. A mismatch means the
    monitor never reaps and every Job waits out its TTL instead.
    """
    assert records.done_path(END, ATTEMPT) == lc.done_path(END, ATTEMPT)


def test_both_processes_name_the_same_archive_and_verdict(shared):
    """The monitor falls back to the archive for txApply and reads .outcome for
    the authoritative verdict; the collector writes both."""
    assert records.log_path(END, ATTEMPT) == lc.base(END, ATTEMPT) + '.log.gz'
    assert records.outcome_path(END, ATTEMPT) == lc.base(END, ATTEMPT) + '.outcome'
    assert records.state_path(END, ATTEMPT) == lc.base(END, ATTEMPT) + '.state'


def test_the_filenames_carry_the_attempt_as_well_as_the_range(shared):
    """Peaks are maxed across a resumed chain, per attempt.

    With one file per range, a retry would overwrite its predecessor instead of
    being compared against it -- which destroys exactly the OOM evidence the
    chain exists to keep.
    """
    for path in (records.metrics_path, records.log_path, records.outcome_path, records.done_path):
        assert path(END, 1) != path(END, 2)
        assert path('1', 1) != path('2', 1)


def test_discarding_a_successful_archive_keeps_what_is_still_read(shared):
    """saveSuccessLogs=false drops the bulk of the volume, not the measurements.

    .metrics holds txApply for a range that succeeded, and .done is what lets
    the Job be reaped at all. Dropping either would let a log-retention flag
    silently delete a Grafana series or strand every finished Job on its TTL.
    """
    for suffix in ('.log.gz', '.state', '.metrics', '.done'):
        with open(lc.base(END, ATTEMPT) + suffix, 'w') as fh:
            fh.write('x')
    lc.discard(END, ATTEMPT)

    assert os.path.exists(records.metrics_path(END, ATTEMPT)), "discard dropped the measurements"
    assert os.path.exists(records.done_path(END, ATTEMPT)), "discard dropped the reap marker"
    assert not os.path.exists(records.log_path(END, ATTEMPT)), "discard kept the archive"


def test_the_monitor_can_read_an_archive_the_collector_wrote(shared):
    """gzip, appended member by member, read whole.

    The monitor reads it with gzip.open() to decide whether an exit 3 was a
    fetch fault. A writer that produced anything other than a concatenation of
    complete members would give it a truncated read -- which it treats as no
    evidence, and no evidence condemns the range.
    """
    path = lc.base(END, ATTEMPT) + '.log.gz'
    for chunk in ("first line\n", "second line\n", "last line\n"):
        with gzip.open(path, 'ab') as fh:
            fh.write(chunk.encode())
    assert [l.strip() for l in attempts._archive_tail(END, ATTEMPT)] == [
        'first line', 'second line', 'last line']


def test_a_carriage_return_meter_does_not_become_one_giant_line(shared, monkeypatch):
    """The AWS CLI draws its transfer meter with \\r and no newline.

    A 628 MiB bucket download therefore arrives as one multi-megabyte "line".
    The mission passes --no-progress to stop it at the source (see
    test_fsharp_driver_contract), but the collector must not be the only thing
    standing between a \\r-heavy line and its own stream: splitting on \\r as
    well as \\n is what keeps the archive line-oriented for the monitor's
    reader, whatever the worker emits.
    """
    import asyncio

    body = ("2026-07-30T00:00:01Z Completed 1.0 MiB\r"
            "2026-07-30T00:00:02Z Completed 2.0 MiB\r"
            "2026-07-30T00:00:03Z metric 'ledger.transaction.apply'\n"
            "2026-07-30T00:00:04Z               sum = 1500.0ms\n")

    class _Resp:
        status = 200
        async def __aenter__(self): return self
        async def __aexit__(self, *exc): return False
        def raise_for_status(self): pass
        @property
        def content(self):
            data = body.encode()
            class _C:
                async def iter_chunked(self, n):
                    for i in range(0, len(data), n):
                        yield data[i:i + n]
            return _C()

    class _Session:
        def get(self, url, params=None, headers=None):
            return _Resp()

    monkeypatch.setattr(lc, 'token', lambda: 't')
    scanner = lc.TxApplyScanner()
    asyncio.run(lc._poll_once(_Session(), 'pod-1', END, ATTEMPT, None, scanner))

    assert scanner.seconds == pytest.approx(1.5), \
        "the metric block was swallowed by the meter's unterminated line"
    with gzip.open(lc.base(END, ATTEMPT) + '.log.gz', 'rt') as fh:
        lines = fh.read().splitlines()
    assert len(lines) >= 4, f"the meter stayed one blob: {lines}"


# --- the volume the layout lives on ------------------------------------------

def test_both_containers_mount_one_volume_at_the_directory_they_both_use():
    """The filenames only agree if the directory does.

    Two emptyDirs would render identically and share nothing; a volume mounted
    at a different path in each container would give each process its own
    private copy of every measurement.
    """
    # One constant read through config by both processes now: they cannot
    # disagree about the directory, only about mounting it.
    log_dir = art.defaults('config')['LOG_DIR']

    mounts = {}
    for name, container in art.containers().items():
        by_path = {m['mountPath']: m['name'] for m in container['volumeMounts']}
        assert log_dir in by_path, f"{name} does not mount {log_dir}"
        mounts[name] = by_path[log_dir]
    assert len(set(mounts.values())) == 1, (
        f"the two containers mount different volumes at {log_dir}: {mounts}")

    volume = mounts[art.MONITOR_CONTAINER]
    spec = art.monitor_deployment()['spec']['template']['spec']
    backing = {v['name']: v for v in spec['volumes']}[volume]
    assert 'persistentVolumeClaim' in backing, (
        f"{log_dir} is backed by {sorted(backing)} -- every measurement dies with the pod")


def test_the_chart_tells_both_containers_where_that_directory_is():
    """LOG_DIR is env, not a constant, so the mount and the env must agree."""
    log_dir = art.defaults('config')['LOG_DIR']
    for name, container in art.containers().items():
        assert art.env_of(container)['LOG_DIR'] == log_dir, name


def test_the_progress_record_lives_on_that_volume_too():
    """progress.json is what a restarted monitor reads back, and what the
    mission driver `cat`s out of the pod at teardown.

    Written to the monitor's emptyDir instead, an OOM-retry storm's record
    would not survive a monitor restart and the mission would build its range
    profile from the ConfigMap mirror -- which has every measurement stripped.
    """
    assert os.path.dirname(config.PROGRESS_FILE) == config.LOG_DIR


def test_the_shared_directory_is_a_single_writer_pvc():
    """One archive per attempt for thousands of ranges, outliving the pod."""
    claims = art.of_kind('PersistentVolumeClaim')
    assert len(claims) == 1, "the monitor's log volume is not a PVC"
    assert claims[0]['spec']['accessModes'] == ['ReadWriteOnce'], (
        "two writers on one archive; the layout assumes a single collector")
