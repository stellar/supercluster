"""Retry counters reconstructed from durable attempt state."""

import json

from prometheus_client import generate_latest

import config
import records
import metrics
import job_monitor as jm


def _write(path, value):
    with open(path, 'w') as fh:
        if isinstance(value, dict):
            json.dump(value, fh)
        else:
            fh.write(value)


def _totals(progress=None, current_attempts=()):
    return jm._retry_counter_totals(progress or {}, current_attempts)


def test_verdict_is_preferred_over_outcome(logdir):
    _write(records.outcome_path(100, 1), {'outcome': 'disrupted'})
    _write(records.verdict_path(100, 1), 'oom')

    totals = _totals(current_attempts={('100', 2)})

    assert totals['retries'] == 1
    assert totals['reasons']['oom'] == 1
    assert totals['reasons']['disrupted'] == 0
    assert totals['oom'] == 1
    assert totals['evicted'] == 0


def test_legacy_outcome_is_used_when_no_verdict_exists(logdir):
    _write(records.outcome_path(100, 1), {'outcome': 'disrupted'})

    totals = _totals(current_attempts={('100', 2)})

    assert totals['reasons']['disrupted'] == 1
    assert totals['evicted'] == 1
    assert totals['spot_disruption_retried'] == 1


def test_matching_verdict_and_outcome_are_counted_once(logdir):
    _write(records.outcome_path(100, 1), {'outcome': 'disrupted'})
    _write(records.verdict_path(100, 1), 'disrupted')

    totals = _totals(current_attempts={('100', 2)})

    assert totals['reasons']['disrupted'] == 1
    assert totals['evicted'] == 1
    assert totals['spot_disruption_retried'] == 1


def test_repeated_disruptions_of_one_range_count_as_one_retried_range(logdir):
    for attempt in (1, 2, 3):
        _write(records.verdict_path(100, attempt), 'disrupted')

    totals = _totals(current_attempts={('100', 4)})

    assert totals['retries'] == 3
    assert totals['evicted'] == 3
    assert totals['reasons']['disrupted'] == 3
    assert totals['spot_disruption_retried'] == 1


def test_disruptions_of_distinct_ranges_each_count_once(logdir):
    for end in (100, 200):
        _write(records.outcome_path(end, 1), {'outcome': 'disrupted'})
        _write(records.verdict_path(end, 1), 'disrupted')

    totals = _totals(current_attempts={('100', 2), ('200', 2)})

    assert totals['evicted'] == 2
    assert totals['spot_disruption_retried'] == 2


def test_active_successor_counts_before_range_progress_exists(logdir):
    _write(records.verdict_path(100, 1), 'rejected')

    totals = _totals(current_attempts={('100', 1), ('100', 2)})

    assert totals['retries'] == 1
    assert totals['reasons']['rejected'] == 1


def test_reconcile_counts_the_successor_on_its_dispatch_pass(cluster):
    cluster.reconcile()
    cluster.advance(300, 'disrupted')

    cluster.reconcile()

    assert cluster.attempt_of(300) == 2
    assert cluster.state['counted']['retries'] == 1
    assert cluster.state['counted']['spot_disruption_retried'] == 1
    assert cluster.state['counted'][('reason', 'disrupted')] == 1
    assert not cluster.completed()
    assert not cluster.failed()


def test_terminal_verdict_without_successor_is_not_a_retry(logdir):
    _write(records.verdict_path(100, 1), 'oom')

    totals = _totals(
        {'failed': {'100': {'attempts': 1, 'outcome': 'oom'}}},
        current_attempts={('100', 1)})

    assert totals['retries'] == 0
    assert totals['oom'] == 0
    assert totals['reasons']['oom'] == 0


def test_terminal_disruption_without_successor_is_only_a_raw_attempt(logdir):
    _write(records.verdict_path(100, 1), 'disrupted')

    totals = _totals(
        {'failed': {'100': {'attempts': 1, 'outcome': 'disrupted'}}},
        current_attempts={('100', 1)})

    assert totals['evicted'] == 1
    assert totals['spot_disruption_retried'] == 0
    assert totals['reasons']['disrupted'] == 0


def test_counter_sync_is_idempotent_and_replays_after_restart(logdir):
    _write(records.verdict_path(100, 1), 'disrupted')
    attempts = {('100', 2)}
    retry_before = metrics.retries._value.get()
    eviction_before = metrics.evictions._value.get()
    unique_before = metrics.spot_disruption_retried._value.get()
    reason_metric = metrics.retry_reasons.labels(reason='disrupted')
    reason_before = reason_metric._value.get()

    counted = {}
    jm.sync_counters({}, counted, attempts)
    first = (metrics.retries._value.get(),
             metrics.evictions._value.get(),
             metrics.spot_disruption_retried._value.get(),
             reason_metric._value.get())
    jm.sync_counters({}, counted, attempts)
    assert (metrics.retries._value.get(),
            metrics.evictions._value.get(),
            metrics.spot_disruption_retried._value.get(),
            reason_metric._value.get()) == first

    jm.sync_counters({}, {}, attempts)
    assert metrics.retries._value.get() == retry_before + 2
    assert metrics.evictions._value.get() == eviction_before + 2
    assert metrics.spot_disruption_retried._value.get() == unique_before + 2
    assert reason_metric._value.get() == reason_before + 2


def test_multiple_attempts_and_every_retry_reason(logdir):
    for attempt, reason in enumerate(config.ATTEMPT_OUTCOMES, 1):
        _write(records.verdict_path(100, attempt), reason)

    totals = _totals({'completed': {'100': {
        'attempts': len(config.ATTEMPT_OUTCOMES) + 1}}})

    assert totals['retries'] == len(config.ATTEMPT_OUTCOMES)
    assert totals['reasons'] == {reason: 1 for reason in config.ATTEMPT_OUTCOMES}
    assert totals['evicted'] == 1
    assert totals['spot_disruption_retried'] == 1
    assert totals['oom'] == 1
    assert totals['ephemeral'] == 1


def test_malformed_and_missing_records_do_not_invent_reasons(logdir):
    _write(records.outcome_path(100, 1), {'outcome': 'disrupted'})
    _write(records.verdict_path(100, 1), 'not-a-verdict')
    _write(records.outcome_path(200, 1), 'not-json')
    _write(logdir / 'range-300-a1.verdict.tmp', 'oom')
    _write(logdir / 'unrelated', 'disrupted')

    totals = _totals(
        {'completed': 'malformed', 'failed': {'x': {'attempts': 'bad'}}},
        current_attempts={('100', 2), ('200', 2), ('bad', 'attempt')})

    assert totals['retries'] == 2
    assert sum(totals['reasons'].values()) == 0
    assert totals['evicted'] == 0
    assert totals['spot_disruption_retried'] == 0
    assert totals['oom'] == 0


def test_existing_and_reason_labelled_metrics_are_exported():
    for reason in config.ATTEMPT_OUTCOMES:
        metrics.retry_reasons.labels(reason=reason)
    text = generate_latest().decode()

    assert '# HELP ssc_parallel_catchup_job_retried_count_total ' \
           'Retry attempts dispatched after a predecessor attempt failed' in text
    assert '# HELP ssc_parallel_catchup_job_spot_eviction_count_total ' \
           'Pod attempts classified as lost to node disruption' in text
    assert '# HELP ssc_parallel_catchup_job_spot_disruption_retried_count_total ' \
           'Unique ledger ranges that dispatched a successor after a node disruption verdict' in text
    assert '# HELP ssc_parallel_catchup_job_oom_retried_count_total ' \
           'Retry attempts dispatched after an OOM verdict, with an escalated memory limit' in text
    assert '# HELP ssc_parallel_catchup_job_ephemeral_retried_count_total ' \
           'Retry attempts dispatched after an ephemeral-storage verdict, with an escalated limit' in text
    assert '# HELP ssc_parallel_catchup_job_retried_reason_count_total ' \
           'Retry attempts dispatched, by the effective verdict of the predecessor attempt' in text
    for reason in config.ATTEMPT_OUTCOMES:
        assert f'ssc_parallel_catchup_job_retried_reason_count_total{{reason="{reason}"}}' in text
