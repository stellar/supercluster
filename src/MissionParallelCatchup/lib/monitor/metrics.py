"""The run's Prometheus metrics, and the only correct way to move them.

Counters and histograms are monotonic and reset to zero when the process
restarts, while the record on the volume survives. So the totals live in the
record, updated when a fact becomes final, and each pass pushes the delta --
idempotent, and self-healing after a restart without rescanning anything.
"""
from prometheus_client import Counter, Gauge, Histogram

import config

#                5m   15m   30m    1h  1.5h    2h
BUCKETS = (300, 900, 1800, 3600, 5400, 7200, float('inf'))

queues = Gauge('ssc_parallel_catchup_queues', 'Size of each job queue', ['queue'])
workers = Gauge('ssc_parallel_catchup_workers', 'Worker liveness', ['status'])
sweep_duration = Gauge('ssc_parallel_catchup_workers_refresh_duration_seconds',
                       'Seconds the last liveness sweep took')
mission_duration = Gauge('ssc_parallel_catchup_mission_duration_seconds',
                         'Seconds since the mission started')

full_duration = Histogram('ssc_parallel_catchup_job_full_duration_seconds',
                          'Compute seconds across the resumed attempt chain',
                          buckets=BUCKETS)
wall_duration = Histogram('ssc_parallel_catchup_job_wall_duration_seconds',
                          "Attempt 1's dispatch to the winning attempt's completion",
                          buckets=BUCKETS)
tx_apply_duration = Histogram('ssc_parallel_catchup_job_tx_apply_duration_seconds',
                              'Transaction-apply seconds per range', buckets=BUCKETS)

retries = Counter('ssc_parallel_catchup_job_retried_count',
                  'Retry attempts dispatched after a predecessor failed')
oom_retries = Counter('ssc_parallel_catchup_job_oom_retried_count',
                      'Retries dispatched after an OOM, with an escalated request')
eph_retries = Counter('ssc_parallel_catchup_job_ephemeral_retried_count',
                      'Retries dispatched after an ephemeral eviction')
# Separates infrastructure churn from application failure: many evictions with
# zero app failures is spot behaving as intended.
evictions = Counter('ssc_parallel_catchup_job_spot_eviction_count',
                    'Attempts classified as lost to node disruption')
disruption_retried = Counter('ssc_parallel_catchup_job_spot_disruption_retried_count',
                             'Ranges that dispatched a successor after a disruption')
retry_reasons = Counter('ssc_parallel_catchup_job_retried_reason_count',
                        'Attempts by the verdict that ended them', ['reason'])

_COUNTERS = (('retries', retries), ('oom', oom_retries), ('ephemeral', eph_retries),
             ('evicted', evictions), ('disruption_retried', disruption_retried))


def settled(progress, cause, end=None):
    """One attempt's verdict became final: a retry followed it, or it condemned
    the range. `end` is passed only for the retry, which is what makes the
    disruption count per range rather than per eviction."""
    counters = progress['counters']
    counters[f'reason:{cause}'] += 1
    if cause == 'disrupted':
        counters['evicted'] += 1
    if end is None:
        return
    counters['retries'] += 1
    if cause in ('oom', 'ephemeral'):
        counters[cause] += 1
    if cause == 'disrupted':
        progress['disruptedRanges'].add(end)
        counters['disruption_retried'] = len(progress['disruptedRanges'])


def sync_counters(progress, applied):
    """Walk each counter up to the record's total.

    Delta from a total rather than .inc() at the event: the counters reset with
    the process and the record does not, so a restart walks them back up.
    """
    counters = progress['counters']
    keys = [k for k, _ in _COUNTERS] + [f'reason:{r}' for r in config.ATTEMPT_OUTCOMES]
    for key in keys:
        delta = counters[key] - applied.get(key, 0)
        if delta <= 0:
            continue
        if key.startswith('reason:'):
            retry_reasons.labels(reason=key[7:]).inc(delta)
        else:
            dict(_COUNTERS)[key].inc(delta)
        applied[key] = counters[key]


def observe_completed(progress, replayed):
    """Feed recorded completions into the histograms, once each per process.

    Keyed on (range, FIELD), never the range alone: a range is usually recorded
    before the collector has flushed its .metrics, so txApply is absent at first
    sight and backfilled a pass or two later. Guarding per range means that
    backfill can never be observed and the histogram permanently disagrees with
    the record.
    """
    for end, rec in progress.get('completed', {}).items():
        for field, metric in (('seconds', full_duration),
                              ('wallSeconds', wall_duration),
                              ('txApply', tx_apply_duration)):
            key = (end, field)
            if key in replayed:
                continue
            value = rec.get(field)
            # Presence, not truth: a txApply sum of 0 is a real observation, and
            # so is a sub-second duration.
            if value is None:
                continue
            replayed.add(key)
            metric.observe(value)


# The dashboard's panels query this name and these label values. The internal
# vocabulary has no queue in it, but renaming a published series only moves the
# break to a consumer that lives in another repo and cannot be tested here.
_QUEUE = {'remaining': 'remain', 'running': 'in_progress',
          'completed': 'succeeded', 'condemned': 'failed'}


def publish_gauges(counts, liveness, sweep_seconds, mission_seconds):
    for state, value in counts.items():
        queues.labels(queue=_QUEUE[state]).set(value)
    for status, value in liveness.items():
        workers.labels(status=status).set(value)
    sweep_duration.set(sweep_seconds)
    mission_duration.set(mission_seconds)
