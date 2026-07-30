"""What the monitor writes down about an attempt it is about to throw away.

Things only this process can record, each with a window that closes the moment
the Job is reaped:

  .outcome         why the attempt failed, and how long it ran
  .log.gz          the backstop archive, for a range the collector never claimed
  the log line     the one place a condemned range explains itself
  progress.json    the record that makes the volume and the Job disposable

Driven through the real reconcile against the fake cluster, because the window
is the point: each of these has to happen on the pass that classifies the
failure, while the pod object is still there.
"""

import gzip
import json
import logging
import os

import pytest

import job_monitor as jm


# --- a failed attempt's duration ----------------------------------------------

def test_a_failed_attempts_duration_is_persisted_with_its_verdict(cluster):
    """The only moment it is available.

    reconcile computes `seconds` solely on the success path, and the pod is
    about to be reaped -- so without this a resumed chain can only ever report
    its final leg, and every attempt lost to a spot eviction drops out of the
    range's compute total.
    """
    cluster.reconcile()
    cluster.advance(300, 'incomplete')
    cluster.reconcile()

    outcome = jm.read_outcome('300', 1)
    assert outcome['outcome'] == 'failed'
    assert outcome['attemptSeconds'] == pytest.approx(60.0, abs=5.0), outcome


def test_that_duration_is_what_the_chain_adds_up(cluster):
    """The consumer, not just the file: a range that resumes must report the
    compute of every leg, and the earlier legs exist only as .outcome."""
    cluster.reconcile()
    cluster.advance(300, 'incomplete')
    cluster.finalize(300, 1)
    cluster.reconcile()
    cluster.finalize(300, 2, resumed=True)

    assert jm.seconds_for_range('300', 2, 300.0) == pytest.approx(360.0, abs=5.0)


def test_a_verdict_already_on_the_volume_is_not_rewritten(cluster):
    """The collector writes this file too, from the pod, while it still exists.
    Its verdict is the one taken with the best evidence and must win."""
    cluster.reconcile()
    cluster.write(jm.outcome_path('300', 1),
                  '{"outcome": "disrupted", "exitCode": null, "pod": "w-300", '
                  '"attemptSeconds": 1800.0}')
    cluster.advance(300, 'incomplete')
    cluster.reconcile()

    # The pod exited 3, which reads as a plain catchup failure. The collector
    # saw the eviction that caused it, so its verdict -- and its duration --
    # stand.
    assert jm.read_outcome('300', 1)['outcome'] == 'disrupted'
    assert jm.read_outcome('300', 1)['attemptSeconds'] == 1800.0


# --- the condemned range has to say so ----------------------------------------

def test_a_condemned_range_is_logged_loudly(cluster, caplog):
    """The zero-retry path used to log nothing at all: the range appeared under
    failed{} and the mission aborted with no line saying why. A condemnation
    fails a ten-hour run, so it is the one verdict that must be impossible to
    miss in the monitor's own log -- which is the log the mission collects."""
    cluster.reconcile()
    cluster.advance(300, 'condemned')
    with caplog.at_level(logging.ERROR, logger=jm.logger.name):
        cluster.reconcile()

    condemned = [r for r in caplog.records if 'RANGE CONDEMNED' in r.getMessage()]
    assert condemned, [r.getMessage() for r in caplog.records]
    said = condemned[0].getMessage()
    assert '300' in said and 'failed' in said, said
    assert '300' in cluster.failed()


def test_an_exhausted_range_says_which_budget_it_spent(cluster, caplog):
    """The other way a range ends: it was retryable and ran out. That is a
    different operator action from a condemnation, so it reads differently."""
    cluster.reconcile()
    for attempt in range(1, jm.MAX_ATTEMPTS_PER_RANGE + 1):
        cluster.advance(300, 'incomplete', attempt=attempt)
        with caplog.at_level(logging.ERROR, logger=jm.logger.name):
            cluster.reconcile()
        cluster.finalize(300, attempt)

    exhausted = [r.getMessage() for r in caplog.records
                 if 'exhausted' in r.getMessage()]
    assert exhausted, [r.getMessage() for r in caplog.records]
    assert '300' in cluster.failed()


# --- the backstop archive ------------------------------------------------------

def test_the_backstop_saves_a_log_the_collector_never_claimed(cluster):
    """Last resort for a pod that lived and died entirely while the collector
    was down. The pod is about to be reaped, so this is the last read of it."""
    cluster.reconcile()
    pod = cluster.k8s.pod_for_job(cluster.job_name(300, 1))
    cluster.k8s.set_pod_log(pod.metadata.name,
                            "metric 'ledger.transaction.apply'\n"
                            "              sum = 1500.0ms\n")
    cluster.advance(300, 'incomplete')
    cluster.reconcile()

    path = jm.log_path('300', 1)
    assert os.path.exists(path), "a failed attempt left no archive at all"
    with gzip.open(path, 'rt') as fh:
        assert 'sum = 1500.0ms' in fh.read()
    # ...and the archive is what the monitor's own reader then recovers from.
    assert jm._tx_apply_for_attempt('300', 1) == pytest.approx(1.5)


def test_the_backstop_stands_down_for_a_range_the_collector_claimed(cluster):
    """Two writers appending to one gzip interleave members and duplicate
    lines. The collector's .state file is the claim, written the moment it
    opens a poller -- empty or not."""
    cluster.reconcile()
    cluster.write(jm.state_path('300', 1), '')
    cluster.advance(300, 'incomplete')
    cluster.reconcile()

    assert not os.path.exists(jm.log_path('300', 1)), \
        "the monitor wrote over an archive the collector had claimed"


def test_a_torn_backstop_archive_is_never_left_behind(cluster, monkeypatch):
    """job_monitor reads this same file back to recover txApplySeconds, and
    gzip raises on a truncated member. A half-written archive would cost the
    metric permanently, so the write goes through .tmp and a rename."""
    cluster.reconcile()
    real_replace = jm.os.replace
    monkeypatch.setattr(jm.os, 'replace',
                        lambda *a, **kw: (_ for _ in ()).throw(OSError(28, 'ENOSPC'))
                        if str(a[1]).endswith('.log.gz') else real_replace(*a, **kw))

    pod = cluster.k8s.pod_for_job(cluster.job_name(300, 1))
    assert jm.backstop_save_pod_log(pod.metadata.name, '300', 1) is False

    assert not os.path.exists(jm.log_path('300', 1))
    assert jm._tx_apply_for_attempt('300', 1) is None


# --- the progress record --------------------------------------------------------

def test_the_progress_record_is_replaced_whole_or_not_at_all(cluster, monkeypatch):
    """The mission driver reads progress.json off the volume while the monitor
    is still writing it, and a partial file is unparseable JSON -- which reads
    as "nothing has been done" and makes every recorded range eligible again.

    Written to a .tmp and renamed, so a write that dies leaves the previous
    record exactly as it was.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.5, peaks={'peakAnonBytes': 7})
    cluster.reconcile()
    before = json.load(open(jm.PROGRESS_FILE))
    assert '300' in before['completed']

    real_open = open

    class _HalfWrite:
        def __init__(self, path):
            self.fh = real_open(path, 'w')

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            self.fh.close()
            return False

        def write(self, blob):
            self.fh.write(blob[:len(blob) // 2])
            raise OSError(28, 'No space left on device')

    armed = {'v': True}

    def half_open(path, mode='r', *a, **kw):
        if armed['v'] and mode in ('w', 'wt') and str(path).endswith('.json.tmp'):
            return _HalfWrite(path)
        return real_open(path, mode, *a, **kw)

    monkeypatch.setattr(jm, 'open', half_open, raising=False)
    cluster.advance(200, 'succeeded')
    cluster.finalize(200, 1, tx_apply=2.5, peaks={'peakAnonBytes': 9})
    with pytest.raises(OSError):
        cluster.reconcile()
    armed['v'] = False

    # Not truncated, not empty, and not half of two records spliced together.
    assert json.load(open(jm.PROGRESS_FILE)) == before
    assert jm.load_progress()['completed']['300']['peakAnonBytes'] == 7
