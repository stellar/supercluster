"""The run's Prometheus metrics.

Declaration only -- prometheus_client builds the default REGISTRY at import and
the monitor serves it from /prometheus, so there is nothing to instantiate here.
Names carry no metric_ prefix: they are read as metrics.<name> at the call site.
"""
from prometheus_client import Counter, Gauge, Histogram


# Histogram buckets
#                  5m  15m   30m    1h  1.5h    2h
buckets = (300, 900, 1800, 3600, 5400, 7200, float("inf"))
catchup_queues = Gauge('ssc_parallel_catchup_queues', 'Exposes size of each job queues', ["queue"])
workers = Gauge('ssc_parallel_catchup_workers', 'Exposes catch up worker status', ["status"])
refresh_duration = Gauge('ssc_parallel_catchup_workers_refresh_duration_seconds', 'Time it took to refresh status of all workers')
full_duration = Histogram('ssc_parallel_catchup_job_full_duration_seconds', 'Compute seconds across the complete resumed attempt chain', buckets=buckets)
tx_apply_duration = Histogram('ssc_parallel_catchup_job_tx_apply_duration_seconds', 'Exposes job TX apply duration as histogram', buckets=buckets)
# wallSeconds is Kubernetes's startTime -> completionTime for the winning Job
# only. Failed-attempt timestamps and inter-attempt gaps were never persisted, so
# it cannot be reconstructed as first dispatch -> success after those Jobs go.
wall_duration = Histogram('ssc_parallel_catchup_job_wall_duration_seconds',
                                 'Winning Kubernetes Job start to completion',
                                 buckets=buckets)
mission_duration = Gauge('ssc_parallel_catchup_mission_duration_seconds', 'Number of seconds since the mission started ')
retries = Counter(
    'ssc_parallel_catchup_job_retried_count',
    'Retry attempts dispatched after a predecessor attempt failed')
# Separates infrastructure churn from application failure: many evictions with
# zero app failures is spot behaving as intended.
evictions = Counter(
    'ssc_parallel_catchup_job_spot_eviction_count',
    'Pod attempts classified as lost to node disruption')
spot_disruption_retried = Counter(
    'ssc_parallel_catchup_job_spot_disruption_retried_count',
    'Unique ledger ranges that dispatched a successor after a node disruption verdict')
pvc_released = Counter('ssc_parallel_catchup_pvc_released_count', 'PVCs deleted after their range completed')
jobs_reaped = Counter('ssc_parallel_catchup_jobs_reaped_count', 'Finished Jobs deleted after their record was durable')
oom_retries = Counter(
    'ssc_parallel_catchup_job_oom_retried_count',
    'Retry attempts dispatched after an OOM verdict, with an escalated memory limit')
eph_retries = Counter(
    'ssc_parallel_catchup_job_ephemeral_retried_count',
    'Retry attempts dispatched after an ephemeral-storage verdict, with an escalated limit')
retry_reasons = Counter(
    'ssc_parallel_catchup_job_retried_reason_count',
    'Retry attempts dispatched, by the effective verdict of the predecessor attempt',
    ['reason'])
