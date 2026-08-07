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

import config
import units
import records
import attempts
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

    outcome = records.read_outcome('300', 1)
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

    assert attempts.seconds_for_range('300', 2, 300.0) == pytest.approx(360.0, abs=5.0)


def test_a_verdict_already_on_the_volume_is_not_rewritten(cluster):
    """The collector writes this file too, from the pod, while it still exists.
    Its verdict is the one taken with the best evidence and must win."""
    cluster.reconcile()
    cluster.write(records.outcome_path('300', 1),
                  '{"outcome": "disrupted", "exitCode": null, "pod": "w-300", '
                  '"attemptSeconds": 1800.0}')
    cluster.advance(300, 'incomplete')
    cluster.reconcile()

    # The pod exited 3, which reads as a plain catchup failure. The collector
    # saw the eviction that caused it, so its verdict -- and its duration --
    # stand.
    assert records.read_outcome('300', 1)['outcome'] == 'disrupted'
    assert records.read_outcome('300', 1)['attemptSeconds'] == 1800.0


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
    # An OOM: the only cause that spends the range budget now, since a "did not
    # complete" is either a fetch fault (the cluster's problem) or a real
    # failure (condemned outright).
    for attempt in range(1, config.ATTEMPT_BUDGETS['oom'] + 1):
        cluster.advance(300, 'oom', attempt=attempt)
        with caplog.at_level(logging.ERROR, logger=jm.logger.name):
            cluster.reconcile()

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

    path = records.log_path('300', 1)
    assert os.path.exists(path), "a failed attempt left no archive at all"
    with gzip.open(path, 'rt') as fh:
        assert 'sum = 1500.0ms' in fh.read()
    # The archive is the evidence the backstop exists to preserve. The metric
    # is the collector's to record, and it never ran for this range.


def test_the_backstop_stands_down_for_a_range_the_collector_claimed(cluster):
    """Two writers appending to one gzip interleave members and duplicate
    lines. The collector's .state file is the claim, written the moment it
    opens a poller -- empty or not."""
    cluster.reconcile()
    cluster.write(records.state_path('300', 1), '')
    cluster.advance(300, 'incomplete')
    cluster.reconcile()

    assert not os.path.exists(records.log_path('300', 1)), \
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

    assert not os.path.exists(records.log_path('300', 1))
    assert attempts._tx_apply_for_attempt('300', 1) is None


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
    before = json.load(open(config.PROGRESS_FILE))
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

    monkeypatch.setattr(records, 'open', half_open, raising=False)
    cluster.advance(200, 'succeeded')
    cluster.finalize(200, 1, tx_apply=2.5, peaks={'peakAnonBytes': 9})
    with pytest.raises(OSError):
        cluster.reconcile()
    armed['v'] = False

    # Not truncated, not empty, and not half of two records spliced together.
    assert json.load(open(config.PROGRESS_FILE)) == before
    assert jm.load_progress()['completed']['300']['peakAnonBytes'] == 7


# --- which classifier wins ----------------------------------------------------
# Two independent sources, and the pod is not simply preferred: a deadline kill
# sends SIGTERM, stellar-core drains and exits 3, so the pod reads a plain
# `failed` that would CONDEMN a range which merely ran long. Only the Job knows
# the deadline fired. Where the pod named a mechanism it wins instead, because
# "ran too long" is also true of an OOM or an eviction and choosing it loses both
# the remediation and the retry budget.

def _verdict(end=300, attempt=1):
    return open(records.verdict_path(end, attempt)).read().strip()


@pytest.mark.parametrize('outcome', [
    'timeout',      # pod exit 3, Job DeadlineExceeded: the Job condition wins
    'unknown',      # pod deleted, Job has no condition: retry rather than condemn
])
def test_the_verdict_recorded_is_the_one_the_sources_agree_on(cluster, outcome):
    cluster.reconcile()
    cluster.advance(300, outcome)
    cluster.reconcile()

    assert _verdict() == outcome


@pytest.mark.parametrize('outcome', ['oom', 'disrupted'])
def test_what_the_pod_says_beats_a_job_deadline(cluster, outcome):
    """The escalation ladder needs the mechanism, and a timeout verdict is
    terminal where an oom is retried with more memory."""
    cluster.reconcile()
    name = cluster.advance(300, outcome)
    # The same attempt also tripped its deadline: the Job condition says so.
    cluster.k8s.set_job_failed(name, reason='DeadlineExceeded',
                               message='Job was active longer than specified deadline')
    cluster.reconcile()

    assert _verdict() == outcome


def test_a_disrupted_range_escalates_disk_one_rung_on_its_first_eviction(cluster, monkeypatch):
    """The size of the escalation, as reconcile actually builds it.

    Counting attempts instead of evictions handed a range disrupted four times a
    1.5^5 = 7.6x disk request for a single eviction. Asserted on the retry Job's
    own spec rather than on the helpers, because the helpers were already right
    -- it was the call site that passed the wrong index.
    """
    monkeypatch.setattr(config, 'STORAGE_MODE', 'ephemeral')
    monkeypatch.setattr(config, 'REQ_EPHEMERAL', '4Gi')
    monkeypatch.setattr(config, 'LIM_EPHEMERAL', '4Gi')
    monkeypatch.setattr(config, 'EPH_BUMP_FACTOR', 1.5)

    cluster.reconcile()
    for _ in range(4):
        cluster.advance(300, 'disrupted')
        cluster.reconcile()
    assert cluster.attempt_of(300) == 5, "four disruptions, four retries"

    cluster.advance(300, 'ephemeral')
    cluster.reconcile()

    retry = cluster.k8s.job(cluster.job_name(300))
    got = retry.spec.template.spec.containers[0].resources.limits['ephemeral-storage']
    assert units.quantity_bytes(got) == units.quantity_bytes('6Gi'), (
        f"first eviction must climb one rung to 6Gi, got {got}")


def test_an_exhausted_oom_reports_the_memory_the_attempt_actually_had(cluster, caplog,
                                                                       monkeypatch):
    """`reason` only surfaces when the budget runs out, so exhaust it.

    Four disruptions then one OOM, so the attempt index (5) and the OOM count (1)
    DIVERGE -- which is the whole bug. The range ran at the 9Gi base, and
    indexing the report on `attempt` claimed 9Gi * 1.5^4 = 45Gi instead.

    Disruptions spend their own budget, so an OOM budget of 1 is
    exhausted by the single OOM and nothing else.
    """
    monkeypatch.setattr(config, 'POOL_PREFIX', '')
    monkeypatch.setattr(config, 'REQ_MEM', '9Gi')
    monkeypatch.setattr(config, 'MEM_BUMP_FACTOR', 1.5)
    monkeypatch.setitem(config.ATTEMPT_BUDGETS, 'oom', 1)

    cluster.reconcile()
    for _ in range(4):
        cluster.advance(300, 'disrupted')
        cluster.reconcile()
    assert cluster.attempt_of(300) == 5, "four disruptions, four retries"

    cluster.advance(300, 'oom')
    with caplog.at_level(logging.ERROR, logger=jm.logger.name):
        cluster.reconcile()

    line = next(r.getMessage() for r in caplog.records if 'exhausted' in r.getMessage())
    reported = line.split('memory request ')[1].rstrip(')')
    assert units.quantity_bytes(reported) == units.quantity_bytes('9Gi'), line
