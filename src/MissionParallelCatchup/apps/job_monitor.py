"""Parallel catchup job monitor.


Singleton state manager for a MissionParallelCatchup run. 

State is held on the shared volume and mirrored to a ConfigMap for the mission driver to read. 

The first reconcile thread is responsible for generating the full ledger range list
and dispatching Kubernetes Jobs per ledger range.

It marks Jobs as completed, retries if failed due to OOM, spot eviction, 
or other transient causes, and records the outcome of each attempt.
Genuine catchup failures are not retried and marked as failed. 
The mission driver reads the ConfigMap to determine the overall progress of the catchup mission.
The mission driver is responsible for tearing down the mission when detecting a job failure or the completion of all ledger ranges.

On each job completion, metrics are updated to reflect the duration and txApply progress, as well as the current state of the mission, 
including the number of remaining jobs, succeeded jobs, failed jobs, and in-progress jobs.

The second worker_liveness thread probes the /info endpoint of each running worker pod to determine its liveness.

Finally the monitor exposes a simple HTTP server for health checks, status, and Prometheus metrics.

"""

import bisect
import collections
import gzip
import json
import math
import os
import re
import threading
import time
import zlib
from datetime import datetime, timezone

from kubernetes import client
from kubernetes.client.rest import ApiException

import attempts
import config
import http_server
import kube
import metrics
import profiles
import ranges
import records
import sizing
import worker_liveness
from logger import build_logger


logger = build_logger('job_monitor')
if not kube.IN_CLUSTER:
    logger.warning("KUBERNETES_SERVICE_HOST is unset: no in-cluster config loaded. "
                   "Every API call will fail until kube.core_v1/kube.batch_v1 are replaced.")


def main():
    # The driver POSTs the profile to /start. Kept on the volume so a restarted
    # monitor resumes a run already under way instead of waiting for a /start
    # that was delivered to its predecessor.
    config.RUN_PATH = os.path.join(config.LOG_DIR, 'run.json')
    if os.path.exists(config.RUN_PATH):
        # Same path as a /start, so the range and the profile are both restored
        # and validated exactly as they were.
        with open(config.RUN_PATH) as fh:
            start_run(json.load(fh))

    http_server.status_source = lambda: (status, status_lock)
    http_server.on_start = start_run

    # This is the reconcile loop --
    # dispatch, progress record, metrics, status.
    reconcile_thread = threading.Thread(target=reconcile_loop, daemon=True)
    reconcile_thread.start()

    http_server.serve()


def start_run(doc):
    """Install the run the driver POSTed: the ledger range, and the profile.

    Both are per-run input, so neither is env-derived any more -- the chart
    installs a generic monitor and this defines what it runs. Written to the
    volume before the gate opens, so the first Job dispatched is already sized
    by the profile and a restart resumes the same run.
    """
    for key, name in (('generator', 'RANGE_GENERATOR'), ('order', 'RANGE_ORDER'),
                      ('startingLedger', 'STARTING_LEDGER'),
                      ('latestLedgerNum', 'LATEST_LEDGER_NUM'),
                      ('ledgersPerJob', 'LEDGERS_PER_JOB'),
                      ('overlapLedgers', 'OVERLAP_LEDGERS')):
        if key in (doc.get('range') or {}):
            setattr(config, name, (doc['range'])[key])
    profile = profiles.load_profile_doc(doc.get('profile') or {})
    # The whole config, judged at the first moment it is complete. Anything
    # wrong rejects the POST with the reason rather than dispatching a run that
    # is already misconfigured.
    validate_config()
    if config.RANGE_ORDER == 'longest-first' and not profile:
        raise ValueError(
            "RANGE_ORDER=longest-first requires a profile: it orders ranges by "
            "their measured seconds, and with no profile every range ties and "
            "dispatch stays tip-first. POST a profile, or set RANGE_ORDER.")
    records.write_atomic(config.RUN_PATH, json.dumps(doc, separators=(',', ':')))
    config.PROFILE = profile
    logger.info("profile installed: %d ranges", len(config.PROFILE))
    # Opened here rather than in the POST handler, so a restart that reads
    # run.json back resumes on exactly the same path. It did not, and a
    # restarted monitor blocked on this forever while /status kept answering
    # with its placeholder -- nothing was unreachable, so the driver polled a
    # dead run indefinitely. Observed on ssc-test 2026-08-08.
    http_server.started.set()


_LIVENESS_NUMBERS = (('LIVENESS_PROBE_TIMEOUT_SECONDS', float),
                     ('LIVENESS_SWEEP_SECONDS', float),
                     ('LIVENESS_MAX_CONCURRENCY', int))


def validate_config():
    """Every fatal config check, against the whole config.

    Runs from /start rather than at import, because that is the first moment the
    config is complete -- the profile arrives with the POST. One validation
    point, one failure channel: whatever is wrong comes back as a 400 with the
    reason instead of a crashlooping pod the driver can only time out on.

    Coerces the numeric env vars and rebinds them, so no caller ever sees the
    string form.
    """
    for name, cast in _LIVENESS_NUMBERS:
        raw = getattr(config, name)
        try:
            value = cast(raw)
        except (TypeError, ValueError):
            raise ValueError(
                "LIVENESS_PROBE_TIMEOUT_SECONDS and LIVENESS_SWEEP_SECONDS must be "
                "numbers; LIVENESS_MAX_CONCURRENCY must be an integer") from None
        if value <= 0:
            raise ValueError(f"{name} must be greater than zero, got {raw!r}")
        setattr(config, name, value)

    if config.RANGE_GENERATOR not in config.VALID_RANGE_GENERATORS:
        raise ValueError("RANGE_GENERATOR must be one of %s, got %r"
                         % (', '.join(config.VALID_RANGE_GENERATORS), config.RANGE_GENERATOR))
    if config.RANGE_ORDER not in config.VALID_RANGE_ORDERS:
        raise ValueError("RANGE_ORDER must be one of %s, got %r"
                         % (', '.join(config.VALID_RANGE_ORDERS), config.RANGE_ORDER))
    # The ledger range, which nothing checked while it came from helm values --
    # an inverted or zero-width range generates no work and the run just ends,
    # reporting success on nothing.
    if config.LEDGERS_PER_JOB <= 0:
        raise ValueError("ledgersPerJob must be greater than zero, got %r"
                         % (config.LEDGERS_PER_JOB,))
    if config.OVERLAP_LEDGERS < 0:
        raise ValueError("overlapLedgers cannot be negative, got %r"
                         % (config.OVERLAP_LEDGERS,))
    if config.LATEST_LEDGER_NUM <= config.STARTING_LEDGER:
        raise ValueError(
            "latestLedgerNum must be greater than startingLedger, got %r and %r"
            % (config.LATEST_LEDGER_NUM, config.STARTING_LEDGER))

status = {
    'num_remain': 1,  # non-zero until the first real update, so callers don't see a premature 0
    'queue_remain_count': 0,
    'queue_succeeded_count': 0,
    'queue_failed_count': 0,
    'queue_in_progress_count': 0,
    'jobs_failed': [],
    'workers_refresh_duration': 0,
    'mission_duration': 0,
}
status_lock = threading.Lock()

# Beside progress.json: the volume is the only durable store.
_MISSION_START = os.path.join(config.LOG_DIR, 'mission_started')


# --- the run itself ---------------------------------------------------------
def reconcile_loop():
    global status
    # Nothing is dispatched until the driver has POSTed /start: a range sized
    # before the profile lands is sized wrong, and it cannot be re-sized later.
    http_server.started.wait()
    # None until reconcile has an owner reference to attach it to; until then
    # process start is correct anyway, because that IS the start of a new run.
    mission_start_time = read_mission_start() or time.time()
    state = {'owner': None, 'replayed': set(),
             'counted': {}}
    while True:
        try:
            if state['owner'] is None:
                state['owner'] = owner_ref()
                _progress_owner['ref'] = state['owner']
                if read_mission_start() is None:
                    records.write_atomic(_MISSION_START, repr(mission_start_time))

            r = reconcile(state)

            # Grafana-only worker responsiveness, from the pod snapshot
            # reconcile already has. One bounded sweep per pass, so this waits at
            # most LIVENESS_SWEEP_SECONDS -- see worker_liveness.publish.
            refresh_start = time.time()
            targets = r.pop('_worker_targets')
            try:
                worker_counts = worker_liveness.publish(targets)
            except Exception as e:
                worker_counts = {'up': 0, 'down': 0, 'unknown': len(targets)}
                now = time.time()
                if now - state.get('last_liveness_error_log', 0) >= 60:
                    state['last_liveness_error_log'] = now
                    logger.exception(
                        "worker liveness publication failed (%s); reporting all "
                        "current candidates unknown and continuing reconcile", e)
            workers_refresh_duration = time.time() - refresh_start

            mission_duration = time.time() - mission_start_time
            with status_lock:
                visible_in_progress = r['in_progress'] + r['finalizing']
                status = {
                    'num_remain': r['remaining'],
                    'queue_remain_count': r['remaining'],
                    'queue_succeeded_count': r['completed'],
                    'queue_failed_count': len(r['failed_ranges']),
                    'queue_in_progress_count': len(visible_in_progress),
                    'jobs_failed': r['failed_ranges'],
                    'workers_refresh_duration': workers_refresh_duration,
                    'mission_duration': mission_duration,
                }
            metrics.catchup_queues.labels(queue="remain").set(r['remaining'])
            metrics.catchup_queues.labels(queue="succeeded").set(r['completed'])
            metrics.catchup_queues.labels(queue="failed").set(len(r['failed_ranges']))
            metrics.catchup_queues.labels(queue="in_progress").set(
                len(visible_in_progress))
            metrics.workers.labels(status="up").set(worker_counts['up'])
            metrics.workers.labels(status="down").set(worker_counts['down'])
            metrics.workers.labels(status="unknown").set(worker_counts['unknown'])
            metrics.refresh_duration.set(workers_refresh_duration)
            metrics.mission_duration.set(mission_duration)
            logger.info("Status: %s", json.dumps(status))

        except Exception as e:
            logger.exception("Error while reconciling: %s", str(e))

        time.sleep(config.RECONCILE_INTERVAL_SECONDS)


def reconcile(state):
    desired = ranges.generate_ranges()
    by_end = {str(end): count for end, count in desired}
    progress = load_progress()
    completed = progress.setdefault('completed', {})
    failed = progress.setdefault('failed', {})

    jobs = kube.batch_v1.list_namespaced_job(
        config.NAMESPACE, label_selector=f"{config.LABEL_RUN}={config.RUN_NAME}").items
    job_pods = pods_by_job()

    live = {}           # range-end -> (attempt, job)
    current_attempts = set()
    for j in jobs:
        end = (j.metadata.labels or {}).get(config.LABEL_RANGE)
        attempt = int((j.metadata.labels or {}).get(config.LABEL_ATTEMPT, 1))
        current_attempts.add((str(end), attempt))
        prev = live.get(end)
        if prev is None or attempt >= prev[0]:
            live[end] = (attempt, j)

    in_progress = []
    finalizing = []
    # The same ranges as `in_progress`, keyed by end. `remaining` is a COUNT
    # over this run's range list, never `total - completed`: the shared progress
    # record can carry ends from a run with a different ledgersPerJob, and a
    # subtraction lets those move a number describing THIS run.
    in_flight = set()
    for end, (attempt, j) in list(live.items()):
        st = j.status
        if st.succeeded:
            # Record before the Job's TTL can reclaim it: `seconds` is the
            # pod's own start -> finish, and the pod goes ~1 min after the node
            # empties.
            if end not in completed:
                pod = job_pods.get(j.metadata.name)
                completed[end] = completion_record(end, attempt, st, pod,
                                                   by_end.get(end))
                if pod is not None and config.SAVE_SUCCESS_LOGS:
                    backstop_save_pod_log(pod.metadata.name, end, attempt)
                # Durably recorded first: the record is what makes the volume
                # and the Job disposable, so it must land before either goes.
                save_progress(progress)
            else:
                # Backfill. The record is written when the Job flips to
                # succeeded, usually before the collector finalizes, so
                # reconstruct the whole profile rather than a field-by-field
                # subset that can leave a record permanently short.
                late = attempts._repair_completed_profile(end, attempt, completed[end])
                if late:
                    save_progress(progress)
                    logger.info("range %s: measurements arrived late, backfilled %s",
                                end, sorted(late))
            # Per sighting of a recorded range, not per first sight: both are
            # idempotent, and hanging them off the run-once branch above leaks
            # the volume and the Job whenever the process dies between
            # save_progress and here.
            release_pvc(end)
            if _attempt_finalized(end, attempt):
                # Nothing more can be learned from the Job. Deleting it reaps the
                # pod, and .metrics is the only place peaks live, so this waits
                # for the collector's marker; JOB_TTL_SECONDS reclaims anything
                # the collector never finishes.
                reap_range_jobs(end)
            else:
                # Keep the range counted as in-progress until that marker
                # lands: the driver writes the final profile as soon as the
                # count reaches zero. Not in `in_progress`, so it costs no
                # dispatch capacity below.
                finalizing.append(job_key(int(end), by_end.get(end, 0)))
        elif st.failed:
            # Completion is terminal, so a Failed Job for a recorded range is
            # garbage: classifying it would redispatch the range against a PVC
            # that was already released. Sweep it.
            if end in completed:
                logger.info("range %s already recorded complete; discarding "
                            "leftover Job for attempt %d", end, attempt)
                reap_range_jobs(end)
                continue
            pod = job_pods.get(j.metadata.name)
            if pod is not None:
                record_outcome(end, attempt, pod)
                backstop_save_pod_log(pod.metadata.name, end, attempt)
            if end in failed:
                # The decision is recorded and cannot change: no successor is
                # coming and no budget is left. Everything worth keeping -- the
                # archive, .outcome, .verdict -- is durable by the time the
                # collector marks the attempt done, so the Job and the volume
                # are holding nothing. Deleting the Job reaps the pod, which is
                # why this waits for that marker like the success path.
                #
                # Without it the range stays the newest Job for its range and
                # every pass re-derives the same verdict and re-logs the same
                # condemnation until JOB_TTL_SECONDS -- measured at 15 identical
                # lines over 9 minutes.
                if _attempt_finalized(end, attempt):
                    release_pvc(end)
                    reap_range_jobs(end)
                continue
            verdict = verdict_for(end, attempt, j, pod)
            # Durable before anything reads a tally: _oom_count and the budget
            # below both count this attempt.
            save_verdict(end, attempt, verdict['outcome'])

            decision = retry_decision(verdict, end, attempt)
            if decision.action == 'defer':
                in_progress.append(job_key(int(end), by_end[end]))
                in_flight.add(str(end))
                continue

            spent, cap = budget_for(verdict, end, attempt)
            if decision.action == 'retry' and spent < cap:
                _log_retry(end, attempt, verdict, decision, cap)
                try:
                    kube.batch_v1.create_namespaced_job(config.NAMESPACE, build_job(
                        int(end), by_end[end], attempt + 1, state['owner'],
                        decision.memory, decision.ephemeral))
                except ApiException as e:
                    if e.status != 409:
                        raise
                current_attempts.add((str(end), attempt + 1))
                # After the successor exists, never before: if the create failed
                # with the predecessor gone, the next pass would redispatch at
                # attempt 1 and lose the escalated request. live[] keys on the
                # highest attempt, so the two coexisting for a pass is handled.
                #
                # Gated on .done like the success path, which means the collector
                # has finalized this attempt's peaks, tx_apply and duration.
                # JOB_TTL_SECONDS reaps it if the collector never gets there.
                if _attempt_finalized(end, attempt):
                    delete_job(end, attempt)
                in_progress.append(job_key(int(end), by_end[end]))
                in_flight.add(str(end))
                continue

            if decision.reason is not None:
                logger.error("range %s exhausted %d attempts (%s)", end, cap,
                             decision.reason)
            else:
                # Say it plainly: otherwise the range only appears under
                # failed{} and the mission aborts with no explanation.
                logger.error("!!! RANGE CONDEMNED !!! %s failed with outcome=%s exitCode=%s "
                             "on attempt %d and is NOT retryable; this fails the mission",
                             end, verdict['outcome'], verdict.get('exitCode'), attempt)

            if end not in failed:
                failed[end] = {'attempts': attempt,
                               'pod': verdict.get('pod', pod.metadata.name if pod else ''),
                               'outcome': verdict['outcome'],
                               'exitCode': verdict['exitCode']}
                save_progress(progress)
        else:
            in_progress.append(job_key(int(end), by_end.get(end, 0)))
            in_flight.add(str(end))

    # Nothing halts dispatch -- not a shrinking `completed` record, not a
    # condemned range. The mission waits for `remaining == 0 and in_progress ==
    # []`, so a frozen dispatch deadlocks the driver; a condemned range is
    # reported once the run drains, keeping the work already paid for.
    #
    # Dispatch, heaviest range first (index 0 is the tip), up to PARALLELISM.
    created = 0
    # No slots: a range's PVC is keyed by the range itself, so concurrency is
    # simply how many are in flight.
    capacity = config.PARALLELISM - len(in_progress)
    for end, count in desired:
        if capacity <= 0:
            break
        key = str(end)
        if key in completed or key in failed or key in live:
            continue
        try:
            record_range_start(end, kube.batch_v1.create_namespaced_job(
                config.NAMESPACE, build_job(end, count, 1, state['owner'])))
            current_attempts.add((str(end), 1))
            created += 1
            capacity -= 1
            in_progress.append(job_key(end, count))
            in_flight.add(str(end))
        except ApiException as e:
            if e.status != 409:   # AlreadyExists: name uniqueness is the mutex
                raise
            current_attempts.add((str(end), 1))
            # Losing the mutex means the Job exists and is in flight, so it
            # occupies a slot exactly like one we created and must spend
            # capacity.
            capacity -= 1
            in_progress.append(job_key(end, count))
            in_flight.add(str(end))

    observe_recorded(progress, state['replayed'])
    sync_counters(progress, state['counted'], current_attempts)
    return {
        'total': len(desired),
        'completed': len(completed),
        'failed_ranges': [f"{job_key(int(k), by_end.get(k, 0))}|{v.get('pod', '')}"
                          for k, v in failed.items()],
        'in_progress': in_progress,
        'finalizing': finalizing,
        'created': created,
        'remaining': sum(1 for end, _ in desired
                         if str(end) not in completed
                         and str(end) not in failed
                         and str(end) not in in_flight),
        # A Kubernetes snapshot only. The caller hands this to the independent
        # liveness sampler after every dispatch/progress decision is complete.
        '_worker_targets': worker_liveness.targets(job_pods.values()),
    }


# The driver parses this out of the status ConfigMap -- `end/count`, joined
# with `|pod` in failed_ranges. Changing the shape breaks it silently.
def job_key(end, count):
    return f"{end}/{count}"


def job_name(end, attempt):
    return f"{config.RUN_NAME}-r{end}-a{attempt}"


# --- durable progress record ------------------------------------------------
# Jobs are reclaimed during a long run, so completion cannot live only in Job
# objects. Written before a Job becomes TTL-eligible.

# Set once at startup; the same ConfigMap the Jobs and PVCs hang off.
_progress_owner = {}


def load_progress():
    """The completed/failed record, or empty on a first start.

    The volume is the only source. An unreadable file replays rather than
    halts, which is safe -- the PVCs survive and each range resumes at its last
    closed ledger.
    """
    try:
        with open(config.PROGRESS_FILE) as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {}


def save_progress(progress):
    # The monitor's own state, and the only copy. The driver's view of the run
    # is status.json in the ConfigMap; this document is not published.
    blob = json.dumps(progress, separators=(',', ':'))
    records.write_atomic(config.PROGRESS_FILE, blob)




# --- worker log capture -----------------------------------------------------

def backstop_save_pod_log(pod_name, end, attempt):
    """Last-resort archive for a range the collector never captured.

    Covers only the gap where a pod lived and died while the collector was down,
    detected by the absence of the .state file it writes when it claims a range.
    Never overwrites a claimed or existing archive: two writers appending to one
    gzip interleave members and duplicate lines.
    """
    if os.path.exists(records.state_path(end, attempt)):
        return True          # collector has it (streaming or already finished)
    path = records.log_path(end, attempt)
    if os.path.exists(path):
        return True
    try:
        body = kube.core_v1.read_namespaced_pod_log(pod_name, config.NAMESPACE, container='stellar-core')
    except ApiException as e:
        logger.warning("could not save log for range %s attempt %d (pod %s): %s",
                       end, attempt, pod_name, e.reason)
        return False
    try:
        os.makedirs(config.LOG_DIR, exist_ok=True)
        records.write_atomic(path, body, gzip.open)
        return True
    except OSError as e:
        logger.warning("could not write %s: %s", path, e)
        return False


def record_range_start(end, job):
    """Persist attempt 1's Job creationTimestamp, once.

    Not status.startTime: the controller sets that asynchronously, so it is
    absent from the create response. The gap between the two is what
    wallSeconds measures. Written at creation because attempt 1's Job is gone
    on the first retry.
    """
    path = records.started_path(end)
    if os.path.exists(path):
        return
    created = job.metadata.creation_timestamp if job and job.metadata else None
    if created is None:
        return
    try:
        records.write_atomic(path, created.isoformat())
    except OSError as e:
        logger.warning("could not persist start time for range %s: %s", end, e)


def range_started_at(end):
    """attempt 1's Job creationTimestamp, or None if it was never recorded."""
    try:
        with open(records.started_path(end)) as fh:
            return datetime.fromisoformat(fh.read().strip())
    except (OSError, ValueError):
        return None


def classify(pod):
    """Why did this pod fail? The Job object cannot answer this.

    Job.status only carries a Failed condition with reason BackoffLimitExceeded
    -- no exit code, no OOM. The detail lives on the pod, which is exactly the
    object Karpenter deletes with the node, so this is recorded the moment the
    watch sees it rather than when reconcile next runs.
    """
    for cond in (pod.status.conditions or []):
        if cond.type == 'DisruptionTarget' and cond.status == 'True':
            return {'outcome': 'disrupted', 'exitCode': None}
    # Kubelet can reject a pod before any container runs, e.g.
    # VolumeAttachmentLimitExceeded. No exit code and no DisruptionTarget, so
    # without this a transient admission rejection reads as a real failure.
    if pod.status.reason == 'Evicted' and 'ephemeral' in (pod.status.message or ''):
        # A limit eviction sets no DisruptionTarget, and stellar-core drains to
        # exit 3, so the Job condition reads it as a catchup failure.
        # status.message is the only discriminator, and only the pod carries it.
        return {'outcome': 'ephemeral', 'exitCode': None, 'reason': pod.status.message}
    if pod.status.reason in ('VolumeAttachmentLimitExceeded', 'OutOfcpu', 'OutOfmemory',
                             'OutOfpods', 'UnexpectedAdmissionError', 'NodeAffinity',
                             'Shutdown', 'Evicted'):
        return {'outcome': 'rejected', 'exitCode': None, 'reason': pod.status.reason}
    if pod.status.reason == 'DeadlineExceeded':
        # The deadline is on the PodSpec, so the kubelet fires it and the pod
        # carries the reason; the Job sees only a non-zero exit.
        return {'outcome': 'timeout', 'exitCode': None, 'reason': pod.status.reason}
    started = any(cs.state and cs.state.terminated for cs in (pod.status.container_statuses or []))
    if not started:
        # No container ever reached a terminal state: nothing ran, so this is
        # not evidence about the ledger range.
        return {'outcome': 'rejected', 'exitCode': None,
                'reason': pod.status.reason or 'no container status'}
    for cs in (pod.status.container_statuses or []):
        t = cs.state.terminated if cs.state else None
        if t is None:
            continue
        # 137 is SIGKILL, which the kubelet also uses for a graceful-stop
        # timeout -- but with reason OOMKilled it is unambiguous.
        if t.reason == 'OOMKilled':
            return {'outcome': 'oom', 'exitCode': t.exit_code}
        if t.exit_code not in (0, None):
            return {'outcome': 'failed', 'exitCode': t.exit_code}
    return {'outcome': 'failed', 'exitCode': None}


def record_outcome(end, attempt, pod):
    path = records.outcome_path(end, attempt)
    if os.path.exists(path):
        return
    data = classify(pod)
    data['pod'] = pod.metadata.name
    # The only place a failed attempt's duration is available: reconcile
    # computes `seconds` on the success path only, and the pod is about to go.
    data['attemptSeconds'] = _pod_seconds(pod)
    try:
        records.write_atomic(path, json.dumps(data))
    except OSError as e:
        logger.warning("could not persist outcome for range %s: %s", end, e)


# The Job controller writes the exit code and pod name into the failure
# condition message, e.g.
#   "Container stellar-core for pod ns/kic-r400000-a1-xxxxx failed with exit
#    code 137 matching FailJob rule at index 1"
# Unlike the pod, this survives node consolidation.
_JOB_MSG = re.compile(r"for pod \S+?/(?P<pod>\S+) failed with exit code (?P<code>\d+)")
_JOB_RULE = re.compile(r"rule at index (?P<idx>\d+)")


def _failure_rules():
    """podFailurePolicy rules, in evaluation order, tagged with what they mean.

    First match wins, so reaching the exit-137 rule proves DisruptionTarget did
    not match -- that ordering is what separates an OOM kill from a
    grace-period SIGKILL after the pod is gone.

    All FailJob: the Job must fail with reason=PodFailurePolicy so the message
    names the rule index. A Count action would surface as BackoffLimitExceeded
    and lose the signal. Retries stay with the monitor because raising a memory
    limit needs a new Job -- spec.template is immutable.
    """
    return [
        ('disrupted', client.V1PodFailurePolicyRule(
            action='FailJob',
            on_pod_conditions=[client.V1PodFailurePolicyOnPodConditionsPattern(
                type='DisruptionTarget', status='True')])),
        ('oom', client.V1PodFailurePolicyRule(
            action='FailJob',
            on_exit_codes=client.V1PodFailurePolicyOnExitCodesRequirement(
                container_name='stellar-core', operator='In', values=[137]))),
        ('failed', client.V1PodFailurePolicyRule(
            action='FailJob',
            on_exit_codes=client.V1PodFailurePolicyOnExitCodesRequirement(
                container_name='stellar-core', operator='NotIn', values=[0]))),
    ]


# Order here is the contract with the Job controller's "rule at index N".
RULE_ORDER = ['disrupted', 'oom', 'failed']
_RULE_OUTCOME = dict(enumerate(RULE_ORDER))


def classify_from_job(job):
    """Recover a verdict from the Job when the pod is already gone.

    Rule index is the signal, not the exit code: rules are evaluated
    first-match-wins, so reaching the exit-137 rule proves the DisruptionTarget
    rule did not match, which is the only way to tell an OOM kill from a
    grace-period SIGKILL once the pod is gone.

    Index and exit code are parsed independently -- a rule matching on
    onPodConditions reports no exit code at all, so requiring one would make the
    disruption case unreadable.
    """
    for cond in (job.status.conditions or []):
        if cond.type != 'Failed' or cond.status != 'True':
            continue
        msg = cond.message or ''
        if cond.reason == 'DeadlineExceeded':
            # activeDeadlineSeconds fired: the attempt hung rather than failing.
            # Retryable -- a genuinely stuck range will exhaust its attempts.
            return {'outcome': 'timeout', 'exitCode': None, 'pod': '',
                    'source': 'job-condition'}
        if cond.reason != 'PodFailurePolicy':
            # e.g. BackoffLimitExceeded -- carries no per-rule detail.
            continue
        rule = _JOB_RULE.search(msg)
        detail = _JOB_MSG.search(msg)
        outcome = _RULE_OUTCOME.get(int(rule.group('idx'))) if rule else None
        code = int(detail.group('code')) if detail else None
        if outcome is None:
            if code is None:
                return None
            # No usable rule index. A drained stellar-core exits 3, not 137, so
            # a bare 137 is an OOM and a bare 3 without DisruptionTarget is a
            # catchup failure.
            outcome = 'oom' if code == 137 else 'failed'
        return {'outcome': outcome, 'exitCode': code,
                'pod': detail.group('pod') if detail else '',
                'source': 'job-condition'}
    return None


def save_verdict(end, attempt, outcome):
    """Persist the EFFECTIVE verdict for one attempt, so budgets can be tallied.

    The .outcome file is not enough on its own: it is classified from the pod,
    and a deadline kill reads as a plain exit-3 `failed` there -- only the Job's
    DeadlineExceeded condition says `timeout`. Reconcile resolves that conflict
    once, and this is where the answer is kept, on the same durable logs volume
    as everything else, so a monitor restart does not reset a range's budgets.
    """
    path = records.verdict_path(end, attempt)
    try:
        records.write_atomic(path, str(outcome))
    except OSError as e:
        logger.warning("could not persist verdict for range %s attempt %s: %s",
                       end, attempt, e)


# --- tx_apply ---------------------------------------------------------------


def _pod_seconds(pod):
    """Container start -> finish for one attempt, or None if unreadable."""
    start = pod.status.start_time if pod.status else None
    if start is None:
        return None
    for cs in (pod.status.container_statuses or []):
        t = cs.state.terminated if cs.state else None
        if t is not None and t.finished_at:
            return (t.finished_at - start).total_seconds()
    return None


# --- job construction -------------------------------------------------------

# Resume decision, before catchup. Skip new-db only when the DB on /data belongs
# to this range AND replay had started: bucket apply assumes a fresh DB, so a
# crash during it must start over, and "Ledger close complete" is the
# discriminator -- bucket apply never closes a ledger.
#
# The LCL comes from stellar-core's own log, not the database: core 27 dropped
# the ledgerheaders table.
RESUME_SCRIPT = r'''set -e
KEY="%(key)s"
TARGET=%(target)d
COUNT=%(count)d
MARK=/data/.job-key
RESUME=false
LCL=""
if [ -f "$MARK" ] && [ "$(cat "$MARK" 2>/dev/null)" = "$KEY" ]; then
  # Ask core for its own LCL through its own accessor, so this survives the v27
  # schema change and any log level. Safe because core has not started, so
  # nothing holds /data/buckets/stellar-core.lock. Core logs to the console
  # alongside the JSON, hence grepping rather than parsing.
  # One "num" key in the document and it is the ledger's. Do NOT window with
  # `grep -A<n> '"ledger":'`: bucketlist puts ~40 lines of hashes in between.
  LCL=$(/usr/bin/stellar-core --conf /config/stellar-core.cfg offline-info --console 2>/dev/null \
        | sed -n 's/.*"num"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' | head -1 || true)
  if [ -n "$LCL" ]; then
    echo "RESUME PROBE: offline-info reports lcl $LCL"
  else
    # Fallback: the previous incarnation's log on /data. Goes blind above INFO,
    # which is why it is no longer the primary probe.
    PREV_LOG=$(ls -t /data/stellar-core*.log 2>/dev/null | head -n 1 || true)
    if [ -n "$PREV_LOG" ]; then
      LCL=$(grep -oE "Ledger close complete: [0-9]+" "$PREV_LOG" 2>/dev/null | tail -1 | grep -oE "[0-9]+$" || true)
    fi
    echo "RESUME PROBE: offline-info gave nothing; log fallback says '${LCL:-none}'"
  fi
  # Already at the target: replay finished and the attempt was evicted before it
  # could exit 0. Re-running catchup applies nothing and exits 2 identically
  # every time, so the range would burn its whole budget over completed work.
  if [ -n "$LCL" ] && [ "$LCL" -ge "$TARGET" ] 2>/dev/null; then
    echo "ALREADY COMPLETE: $KEY reached ledger $LCL >= target $TARGET; nothing left to replay"
    exit 0
  fi
  if [ -n "$LCL" ] && [ "$LCL" -ge $((TARGET - COUNT)) ] && [ "$LCL" -lt "$TARGET" ] 2>/dev/null; then
    RESUME=true; echo "RESUME: $KEY reached ledger $LCL, replay had started; skipping new-db"
  else
    echo "RESUME DECLINED: $KEY last close was '${LCL:-none}' (need >= $((TARGET - COUNT))); bucket phase incomplete, starting fresh"
  fi
fi
printf '%%s' "$KEY" > "$MARK"
if [ "$RESUME" != "true" ]; then
  /usr/bin/stellar-core --conf /config/stellar-core.cfg new-db --console
fi
exec /usr/bin/stellar-core --conf /config/stellar-core.cfg catchup "$KEY" \
  --metric 'ledger.transaction.apply' --console
'''


def owner_ref():
    cm = kube.core_v1.read_namespaced_config_map(f"{config.RUN_NAME}-stellar-core-config", config.NAMESPACE)
    return [client.V1OwnerReference(api_version='v1', kind='ConfigMap',
                                    name=cm.metadata.name, uid=cm.metadata.uid,
                                    block_owner_deletion=True)]


def release_pvc(end):
    """Drop a completed range's volume.

    The PVC exists so an interrupted range resumes at L+1; a succeeded range has
    nothing to resume. They are owner-referenced to the release, so without this
    every volume survives until `helm uninstall`.

    Best-effort: a failure costs disk, never correctness.
    """
    if config.STORAGE_MODE != 'pvc':
        return
    name = f"{config.RUN_NAME}-data-r{end}"
    try:
        kube.core_v1.delete_namespaced_persistent_volume_claim(name, config.NAMESPACE)
        metrics.pvc_released.inc()
    except ApiException as e:
        if e.status != 404:
            logger.warning("could not release PVC for completed range %s: %s", end, e)


def _attempt_finalized(end, attempt):
    """Has the collector written everything it will for this attempt?

    It writes this file last, after .metrics. Anything inferred instead -- peaks
    being present, tx_apply being readable -- is a guess: tx_apply falls back to
    the archive so it is available long before the collector finishes, and an
    attempt can legitimately finalize with no peaks at all.
    """
    return os.path.exists(records.done_path(end, attempt))


def reap_range_jobs(end):
    """Delete every Job this range has, not just the attempt that won.

    Completion is terminal for the RANGE. An attempt-scoped reap leaves an
    older Failed Job standing -- the common case is an attempt lost to node
    disruption whose collector died with the node, so it was never finalized
    and was deliberately not deleted. Once the winner's Job is gone, that
    leftover is the range's highest live attempt, and the next pass feeds it
    straight into the retry decision and re-runs an already-recorded range
    against a freshly recreated, empty PVC.
    """
    try:
        jobs = kube.batch_v1.list_namespaced_job(
            config.NAMESPACE,
            label_selector=f"{config.LABEL_RUN}={config.RUN_NAME},{config.LABEL_RANGE}={end}").items
    except ApiException as e:
        logger.warning("could not list jobs for completed range %s: %s", end, e)
        return
    for j in jobs:
        try:
            kube.batch_v1.delete_namespaced_job(j.metadata.name, config.NAMESPACE,
                                           propagation_policy='Background')
            metrics.jobs_reaped.inc()
        except ApiException as e:
            if e.status != 404:
                logger.warning("could not delete finished job %s for range %s: %s",
                               j.metadata.name, end, e)


def delete_job(end, attempt):
    """Drop a finished Job once nothing more is owed by it.

    reconcile() lists every Job and Pod each pass, so a lingering finished Job
    inflates both LIST calls. Background propagation is what takes the pod with
    it; orphan would leave the next pass listing just as much.

    Callers must have persisted what they need first. Best-effort: a 404 is the
    race with the TTL controller, and raising would strand every other range in
    the pass -- JOB_TTL_SECONDS still reclaims the object.
    """
    try:
        kube.batch_v1.delete_namespaced_job(job_name(end, attempt), config.NAMESPACE,
                                       propagation_policy='Background')
        metrics.jobs_reaped.inc()
    except ApiException as e:
        if e.status != 404:
            logger.warning("could not delete finished job for range %s attempt %d: %s",
                           end, attempt, e)


def ensure_pvc(end, owner):
    name = f"{config.RUN_NAME}-data-r{end}"
    try:
        kube.core_v1.read_namespaced_persistent_volume_claim(name, config.NAMESPACE)
        return name
    except ApiException as e:
        if e.status != 404:
            raise
    spec = client.V1PersistentVolumeClaimSpec(
        access_modes=['ReadWriteOnce'],
        resources=client.V1VolumeResourceRequirements(requests={'storage': config.STORAGE_SIZE}))
    if config.STORAGE_CLASS:
        spec.storage_class_name = config.STORAGE_CLASS
    kube.core_v1.create_namespaced_persistent_volume_claim(config.NAMESPACE, client.V1PersistentVolumeClaim(
        metadata=client.V1ObjectMeta(name=name, owner_references=owner,
                                     labels={config.LABEL_RUN: config.RUN_NAME, config.LABEL_RANGE: str(end)}),
        spec=spec))
    return name


def _resources(mem=None, eph=None, end=None, attempt=1):
    # Before mem is defaulted below -- reading it afterwards can never see None,
    # which silently disabled profile sizing entirely.
    overrides = sizing._profile_overrides(end, escalated=(mem is not None or eph is not None),
                                   attempt=attempt)
    # `mem` is the escalated request on an OOM retry, else the configured one.
    req = {'cpu': config.REQ_CPU, 'memory': mem or config.REQ_MEM}
    # Only ephemeral-storage is limited: it is the one dimension where an
    # unbounded pod takes the node down rather than itself.
    lim = {}

    # Only meaningful in ephemeral mode. In PVC mode a large request makes disk
    # the binding dimension and halves workers-per-node for no reason.
    if config.REQ_EPHEMERAL:
        # Raise the request with the limit: ephemeral-storage is a scheduling
        # dimension, so a pod that outgrew it no longer fits where it was.
        req['ephemeral-storage'] = eph or config.REQ_EPHEMERAL
    else:
        # pvc mode: /data is not on the node disk, so an ephemeral override
        # would size a dimension this run does not use.
        overrides.pop('ephemeral-storage', None)
    if config.LIM_EPHEMERAL:
        lim['ephemeral-storage'] = eph or config.LIM_EPHEMERAL

    # The profile moves requests only. Disk excepted, because its limit is what
    # the kubelet enforces.
    for key, value in overrides.items():
        req[key] = value
        if key == 'ephemeral-storage' and config.LIM_EPHEMERAL:
            lim[key] = value
    # Unmeasured range: the configured requests, exactly as if there were no
    # profile at all.
    return client.V1ResourceRequirements(requests=req, limits=lim or None)


def volume_spread_constraints():
    """Keep PVC-mounting workers under the per-node EBS attachment limit.

    Only in pvc mode: in ephemeral mode /data is an emptyDir, no volume is
    attached, and spreading would just cost density.
    """
    if config.STORAGE_MODE != 'pvc' or config.MAX_VOLUMES_PER_NODE <= 0:
        return None
    min_domains = max(1, -(-config.PARALLELISM // config.MAX_VOLUMES_PER_NODE))   # ceil
    return [client.V1TopologySpreadConstraint(
        max_skew=config.MAX_VOLUMES_PER_NODE,
        min_domains=min_domains,
        topology_key='kubernetes.io/hostname',
        when_unsatisfiable='DoNotSchedule',
        label_selector=client.V1LabelSelector(match_labels={config.LABEL_RUN: config.RUN_NAME}))]


def pod_labels(end, attempt):
    """Labels on the worker POD, which are not the Job's.

    LABEL_ATTEMPT has to be here too: the collector reads it off the pod to pick
    which range-<end>-a<n>.* files the attempt owns, defaulting to "1". On the
    Job alone, every retry overwrites attempt 1's peaks instead of being maxed
    against them.
    """
    labels = {config.LABEL_RUN: config.RUN_NAME, config.LABEL_RANGE: str(end),
              config.LABEL_ATTEMPT: str(attempt)}
    if config.EMIT_MISSION_LABEL and config.MISSION:
        labels['mission'] = config.MISSION
    return labels


def _prestop_delay():
    """A preStop that stalls the kubelet, or None when the knob is off.

    `sleep` from the image rather than `sh -c sleep`: one less process to exist
    in a container that is being torn down, and it fails loudly at hook-exec
    time if the binary is missing rather than silently succeeding.

    Refuses to install a hook that cannot finish inside the grace period. A
    preStop longer than the grace is worse than none: the kubelet kills it
    mid-sleep, reports FailedPreStopHook, and the container is signalled
    anyway -- so the delay is not bought and an error is logged for every
    evicted pod.
    """
    if config.WORKER_PRESTOP_SLEEP_SECONDS <= 0:
        return None
    if config.WORKER_PRESTOP_SLEEP_SECONDS >= config.WORKER_GRACE_SECONDS:
        logger.warning(
            "PRESTOP_SLEEP_SECONDS=%s does not fit in GRACE_SECONDS=%s; "
            "not installing a preStop hook that the kubelet would kill",
            config.WORKER_PRESTOP_SLEEP_SECONDS, config.WORKER_GRACE_SECONDS)
        return None
    return client.V1Lifecycle(
        pre_stop=client.V1LifecycleHandler(
            _exec=client.V1ExecAction(
                command=['/bin/sleep', str(config.WORKER_PRESTOP_SLEEP_SECONDS)])))


def build_job(end, count, attempt, owner, mem=None, eph=None):
    key = job_key(end, count)
    script = RESUME_SCRIPT % {'key': key, 'target': end, 'count': count}

    if config.STORAGE_MODE == 'pvc':
        data_vol = client.V1Volume(name='data', persistent_volume_claim=(
            client.V1PersistentVolumeClaimVolumeSource(claim_name=ensure_pvc(end, owner))))
    else:
        data_vol = client.V1Volume(name='data', empty_dir=client.V1EmptyDirVolumeSource())

    env = [client.V1EnvVar(name='ASAN_OPTIONS', value=config.ASAN_OPTIONS)] if config.ASAN_OPTIONS else []
    command = ['/bin/sh', '-c', script]
    volumes = [data_vol, client.V1Volume(
        name='config', config_map=client.V1ConfigMapVolumeSource(
            name=f"{config.RUN_NAME}-stellar-core-config"))]
    volume_mounts = [
        client.V1VolumeMount(name='data', mount_path='/data'),
        client.V1VolumeMount(name='config', mount_path='/config')]

    # Require and avoid go in ONE matchExpressions list: expressions within a
    # term are ANDed, separate terms are ORed, and an avoid-only pod in its own
    # term would match every node.
    match = []
    if config.NODE_LABEL_KEY:
        # Pooled runs route per range: the label names the tier this range's
        # memory puts it in. An escalated attempt resolves to a promoted tier,
        # which is what moves the pod to nodes its memory fits.
        tier = sizing.pool_for(end, attempt)
        value = f"{config.POOL_PREFIX}-{tier}" if tier else config.NODE_LABEL_VALUE
        match.append(client.V1NodeSelectorRequirement(
            key=config.NODE_LABEL_KEY, operator='In', values=[value]))
    if config.CAPACITY_TYPE:
        # Capacity type is a NodePool property a pod cannot otherwise express,
        # and Karpenter labels every node with it. ANDing it here keeps a
        # pvc-mode run off on-demand nodes and vice versa.
        match.append(client.V1NodeSelectorRequirement(
            key='karpenter.sh/capacity-type', operator='In', values=[config.CAPACITY_TYPE]))
    if config.AVOID_NODE_LABEL_KEY:
        # No value means "avoid the label however it is set", which is
        # DoesNotExist; NotIn [""] would only exclude the empty value.
        match.append(client.V1NodeSelectorRequirement(
            key=config.AVOID_NODE_LABEL_KEY,
            operator='NotIn' if config.AVOID_NODE_LABEL_VALUE else 'DoesNotExist',
            values=[config.AVOID_NODE_LABEL_VALUE] if config.AVOID_NODE_LABEL_VALUE else None))
    affinity = None
    if match:
        affinity = client.V1Affinity(node_affinity=client.V1NodeAffinity(
            required_during_scheduling_ignored_during_execution=client.V1NodeSelector(
                node_selector_terms=[client.V1NodeSelectorTerm(match_expressions=match)])))

    # Taint value must be absent: the mission emits {key, effect} with no value,
    # and the default Equal operator does not match "" against "true".
    tolerations = [client.V1Toleration(key=config.TOLERATE_TAINT, effect='NoSchedule')] if config.TOLERATE_TAINT else None

    container = client.V1Container(
        name='stellar-core', image=config.CORE_IMAGE,
        command=command, env=env, resources=_resources(mem, eph, end, attempt),
        ports=[client.V1ContainerPort(container_port=11626, name='http')],
        lifecycle=_prestop_delay(),
        volume_mounts=volume_mounts)

    return client.V1Job(
        metadata=client.V1ObjectMeta(
            name=job_name(end, attempt), owner_references=owner,
            labels={config.LABEL_RUN: config.RUN_NAME, config.LABEL_RANGE: str(end),
                    config.LABEL_ATTEMPT: str(attempt)}),
        spec=client.V1JobSpec(
            # The monitor owns retries, not the Job controller: backoffLimit 0
            # means the Job fails once and stays put, so reconcile classifies the
            # failure and decides on attempt N+1.
            #
            # On the JobSpec, not the pod: a pod-level deadline is immutable once
            # the pod exists, so a mis-set value could not be corrected on a live
            # run.
            active_deadline_seconds=config.ATTEMPT_DEADLINE_SECONDS or None,
            backoff_limit=0,
            pod_failure_policy=client.V1PodFailurePolicy(
                rules=[r for _, r in _failure_rules()]),
            ttl_seconds_after_finished=config.JOB_TTL_SECONDS,
            template=client.V1PodTemplateSpec(
                metadata=client.V1ObjectMeta(labels=pod_labels(end, attempt)),
                spec=client.V1PodSpec(
                    # On the POD, not the JobSpec: activeDeadlineSeconds runs
                    # from the Job's startTime, charging Pending time against a
                    # budget meant to bound how long the range RUNS. The
                    # pod-level field starts at container start.
                    # IRSA for the S3 history mirror; without it workers fall
                    # back to the public archive, which throttles at 1024.
                    service_account_name=config.WORKER_SERVICE_ACCOUNT or None,
                    # Keeps PVC-mounting workers under the per-node EBS
                    # attachment cap; inert at realistic CPU-bound density.
                    topology_spread_constraints=volume_spread_constraints(),
                    # Never restarted in place: the pod stays terminal and
                    # inspectable for classification and the backstop log read.
                    restart_policy='Never',
                    termination_grace_period_seconds=config.WORKER_GRACE_SECONDS,
                    affinity=affinity, tolerations=tolerations,
                    containers=[container],
                    volumes=volumes))))


# --- what a pass decides ----------------------------------------------------
# Counters, verdicts and retry policy: everything reconcile() calls to turn a
# finished attempt into a decision.

_ATTEMPT_FILE = re.compile(
    r'^range-(?P<end>\d+)-a(?P<attempt>[1-9]\d*)\.'
    r'(?:verdict|outcome|state|metrics|done|log\.gz)$')


def _retry_counter_totals(progress, current_attempts=()):
    """Reconstruct retry metrics from durable records and observed attempts.

    A verdict says why an attempt ended; it does not say a retry was dispatched.
    Attempt N therefore contributes to retry totals only when attempt N+1 is
    evidenced by progress, a persisted per-attempt file, or the current Job
    snapshot. The latter makes a newly-created successor visible before its range
    completes, while the durable sources rebuild the same truth after restart.
    """
    try:
        names = os.listdir(config.LOG_DIR)
    except OSError:
        names = []

    max_attempt = {}
    terminal = set()

    def remember(end, attempt):
        try:
            attempt = int(attempt)
        except (TypeError, ValueError):
            return
        if attempt < 1:
            return
        end = str(end)
        max_attempt[end] = max(max_attempt.get(end, 0), attempt)

    if isinstance(progress, dict):
        for bucket in ('completed', 'failed'):
            bucket_records = progress.get(bucket)
            if not isinstance(bucket_records, dict):
                continue
            for end, record in bucket_records.items():
                if not isinstance(record, dict):
                    continue
                try:
                    attempt = int(record.get('attempts', 1))
                except (TypeError, ValueError):
                    continue
                if attempt < 1:
                    continue
                remember(end, attempt)
                terminal.add((str(end), attempt))

    for item in current_attempts:
        try:
            end, attempt = item
        except (TypeError, ValueError):
            continue
        remember(end, attempt)

    verdict_files = set()
    outcome_files = set()
    for name in names:
        match = _ATTEMPT_FILE.match(name)
        if not match:
            continue
        key = (match.group('end'), int(match.group('attempt')))
        remember(*key)
        if name.endswith('.verdict'):
            verdict_files.add(key)
        elif name.endswith('.outcome'):
            outcome_files.add(key)

    effective = {}
    for end, attempt in verdict_files:
        try:
            with open(records.verdict_path(end, attempt)) as fh:
                verdict = fh.read().strip()
        except OSError:
            continue
        if verdict in config.ATTEMPT_OUTCOMES:
            effective[(end, attempt)] = verdict

    # .outcome predates .verdict and is safe only for a completed chain: a
    # collector outcome can still be superseded by reconcile's verdict. Any
    # verdict file, even malformed, means this is not a legacy attempt.
    for end, attempt in outcome_files - verdict_files:
        if attempt >= max_attempt.get(end, 0) and (end, attempt) not in terminal:
            continue
        try:
            with open(records.outcome_path(end, attempt)) as fh:
                record = json.load(fh)
        except (OSError, ValueError):
            continue
        outcome = record.get('outcome') if isinstance(record, dict) else None
        if outcome in config.ATTEMPT_OUTCOMES:
            effective[(end, attempt)] = outcome

    retries = sum(max(0, attempt - 1) for attempt in max_attempt.values())
    reasons = {reason: 0 for reason in config.ATTEMPT_OUTCOMES}
    for (end, attempt), reason in effective.items():
        if attempt < max_attempt.get(end, 0):
            reasons[reason] += 1
    disruption_retried_ranges = {
        end for (end, attempt), reason in effective.items()
        if reason == 'disrupted' and attempt < max_attempt.get(end, 0)
    }

    return {
        'retries': retries,
        'evicted': sum(1 for verdict in effective.values() if verdict == 'disrupted'),
        'spot_disruption_retried': len(disruption_retried_ranges),
        'oom': reasons['oom'],
        'ephemeral': reasons['ephemeral'],
        'reasons': reasons,
    }


def sync_counters(progress, counted, current_attempts=()):
    """Drive the counters from persisted state instead of from events.

    Two reasons not to .inc() as things happen:

    * a terminally-failed range stays the newest Job for its range, so an
      event-driven inc fires again on every reconcile until teardown
    * the process resets to zero on restart, while verdicts and attempt state on
      the PVC survive

    Computing the true total and incrementing by the delta is monotonic,
    idempotent, and self-heals after a restart: the counter starts at 0 and the
    first sync walks it up to the recorded total.
    """
    totals = _retry_counter_totals(progress, current_attempts)
    for key, total, metric in (('retries', totals['retries'], metrics.retries),
                               ('oom', totals['oom'], metrics.oom_retries),
                               ('ephemeral', totals['ephemeral'], metrics.eph_retries),
                               ('evicted', totals['evicted'], metrics.evictions),
                               ('spot_disruption_retried',
                                totals['spot_disruption_retried'],
                                metrics.spot_disruption_retried)):
        delta = total - counted.get(key, 0)
        if delta > 0:
            metric.inc(delta)
            counted[key] = total
    for reason in config.ATTEMPT_OUTCOMES:
        metric = metrics.retry_reasons.labels(reason=reason)
        key = ('reason', reason)
        total = totals['reasons'][reason]
        delta = total - counted.get(key, 0)
        if delta > 0:
            metric.inc(delta)
            counted[key] = total


def observe_recorded(progress, replayed):
    """Feed recorded completions into the histograms.

    Prometheus histograms are append-only and reset to zero when the process
    restarts, so replaying every recorded range rebuilds the exact cumulative
    total rather than double counting. Guarded per-process by `replayed`.

    Keyed on (range, field), not on the range alone: a range is usually
    recorded before the collector has flushed its .metrics, so txApply is null
    at first sight and backfilled a pass or two later. Marking the whole range
    as replayed on first sight meant that backfill could never be observed, and
    the histogram permanently disagreed with progress.json.
    """
    for end, rec in progress.get('completed', {}).items():
        # `is not None`, not truthiness: sum = 0ms records txApply 0.0, which is
        # a real observation. Same for a sub-second duration.
        for field, metric in (('seconds', metrics.full_duration),
                              ('wallSeconds', metrics.wall_duration),
                              ('txApply', metrics.tx_apply_duration)):
            if (end, field) in replayed:
                continue
            value = rec.get(field)
            if value is None:
                continue
            replayed.add((end, field))
            metric.observe(value)


def _range_wall_seconds(end, status):
    """Attempt 1 created -> winner completed, or None if the start was never recorded.

    The range's whole life: every retry, every gap between them, every wait for a
    node. Deliberately not falling back to the winning Job's own start -- that
    measures one leg and understates exactly the mess this is here to capture.
    """
    started = range_started_at(end)
    if not started or not status.completion_time:
        return None
    return (status.completion_time - started).total_seconds()


def _range_compute_seconds(end, attempt, pod, wall):
    """Compute seconds across the whole resumed chain, not this leg alone.

    A fresh single attempt may fall back to the winner's own seconds or to the
    Job wall; a resumed chain is every leg or nothing, never winner-only.
    """
    pod_seconds = _pod_seconds(pod) if pod is not None else None
    chain = attempts.seconds_for_range(end, attempt, pod_seconds)
    if chain is not None:
        return chain
    if len(attempts._resumed_chain(end, attempt)) == 1:
        return pod_seconds if pod_seconds is not None else wall
    return None


def completion_record(end, attempt, status, pod, count=None):
    """What a finished range cost and where it ran.

    Assembled from the winning Job's status, the pod if it still exists, and the
    per-attempt files the collector wrote. Every pod-derived field is optional on
    purpose: a reaped node costs that field, never the record.
    """
    wall = _range_wall_seconds(end, status)
    # Not gated on `pod`: the collector's record outlives it, so a reaped node
    # must not cost us the metric.
    tx = attempts.tx_apply_for_range(end, attempt)
    if tx is None:
        logger.warning("could not read tx_apply for range %s (pod gone?); "
                       "metric will be missing for this range", end)
    record = {'seconds': _range_compute_seconds(end, attempt, pod, wall),
              'wallSeconds': wall, 'txApply': tx, 'attempts': attempt}
    # Ledger count travels with the record: the logarithmic generator varies it
    # per range, so it cannot be recomputed from config when the profile is read
    # back.
    if count is not None:
        record['count'] = count
    record.update(attempts.peaks_for_range(end, attempt))
    return record


# A failed attempt resolves to one of three actions. `reason` names the cause for
# the log line and is None only when the range is condemned outright.
Decision = collections.namedtuple('Decision', 'action reason memory ephemeral')


def _retry(reason, memory=None, ephemeral=None):
    return Decision('retry', reason, memory, ephemeral)


CONDEMN = Decision('condemn', None, None, None)
# Wait for the collector's .done marker and decide on a later pass.
DEFER = Decision('defer', None, None, None)


def verdict_for(end, attempt, job, pod):
    """Why this attempt failed, from the pod if it survived and the Job if not.

    Two classifications, ranked:
      1. the pod named a mechanism (OOM, DisruptionTarget, eviction, deadline)
         -- it wins, being the precise one
      2. else the Job says timeout -- only the Job knows the deadline fired, and
         the drained pod reads as a plain `failed`
      3. else whichever exists, unknown over nothing: retry rather than condemn
    """
    from_pod = records.read_outcome(end, attempt)
    from_job = classify_from_job(job)
    if from_pod and from_pod.get('outcome') in config.POD_AUTHORITATIVE_OUTCOMES:
        verdict = from_pod
    elif from_job and from_job.get('outcome') == 'timeout':
        verdict = from_job
    else:
        verdict = from_pod or from_job or {'outcome': 'unknown', 'exitCode': None}
    if verdict.get('source') == 'job-condition':
        logger.info("range %s attempt %d classified from Job condition "
                    "(exit %s); pod was already gone",
                    end, attempt, verdict.get('exitCode'))
    # A third source of evidence for the one ambiguous exit code. Exit 3 is
    # "did not complete", which a SIGTERM drain and a real failure share, so the
    # archive is what separates them -- and only once the collector has finished
    # writing it. Until then the verdict stays `failed` and the decision defers.
    if (verdict.get('exitCode') == config.CATCHUP_INCOMPLETE_EXIT
            and _attempt_finalized(end, attempt)
            and attempts.exit3_retry_cause(end, attempt)):
        verdict = dict(verdict, outcome='fetch-fault')
    return verdict


def _condemn_timeout(end, attempt):
    """Terminal: the deadline is the only thing that ends a wedged range.

    A range stuck on an unreachable archive closes no ledgers and never exits, so
    retrying spends the deadline again for nothing.
    """
    logger.error("!!! RANGE CONDEMNED !!! %s hit its %ss attempt deadline "
                 "on attempt %s; this fails the mission. Check its archived "
                 "log for 'maybe stale archive' -- an unreachable history "
                 "mirror is the usual cause.",
                 end, config.ATTEMPT_DEADLINE_SECONDS, attempt)
    return CONDEMN


def _retry_oom(end, attempt):
    """Retry with the next memory rung.

    Rungs climbed = OOMs seen, not attempts made; this attempt's outcome is
    already on disk, so the count includes it. `had` is what this attempt
    actually ran with, by the same derivation that sized it -- indexing on
    `attempt` instead names a rung nobody occupied.
    """
    base = (sizing._profile_overrides(end, escalated=False) or {}).get('memory')
    ooms = records._oom_count(end, attempt)
    had = (sizing.pool_memory(sizing.pool_for(end, attempt)) if config.POOL_PREFIX
           else sizing.mem_for_attempt(ooms, base))
    return _retry(f"OOM-killed at memory request {had}",
                  memory=sizing.mem_for_attempt(ooms + 1, base, end=end))


def _retry_ephemeral(end, attempt):
    """Retry with the next disk rung.

    Rungs climbed = evictions seen, not attempts made, as with the OOM ladder:
    on spot most retries are disruptions. The count includes this attempt.
    """
    evictions = records._cause_count(end, attempt, ('ephemeral',))
    had = sizing.eph_for_attempt(evictions)
    reason = (f"evicted for exceeding its {had} ephemeral-storage limit" if had
              else "evicted under node disk pressure with no configured limit")
    return _retry(reason, ephemeral=sizing.eph_for_attempt(evictions + 1))


def _decide_exit3(end, attempt):
    """A plain exit 3: the archive named no fetch fault, or has not landed yet.

    verdict_for already promotes an exit 3 to `fetch-fault` once the archive
    says so, so reaching here means either the collector is still writing it --
    wait, bounded by JOB_TTL_SECONDS -- or nothing in it explains the failure, in
    which case the range is condemned and its archive survives on the volume.
    """
    if not _attempt_finalized(end, attempt):
        return DEFER
    return CONDEMN


def retry_decision(verdict, end, attempt):
    """Retry this range with what, condemn it, or wait for more evidence."""
    outcome = verdict['outcome']
    if outcome == 'timeout':
        return _condemn_timeout(end, attempt)
    elif outcome == 'rejected':
        # The pod never started, so a retry cannot mask a broken range -- but it
        # is the range's own budget now, not the disruption one.
        return _retry(f"rejected by the node before starting "
                      f"({verdict.get('reason', '?')})")
    elif outcome == 'disrupted':
        return _retry("lost to node disruption")
    elif outcome == 'fetch-fault':
        return _retry(f"exited {config.CATCHUP_INCOMPLETE_EXIT} after a fetch fault "
                      f"({attempts.exit3_retry_cause(end, attempt)})")
    elif outcome == 'oom':
        return _retry_oom(end, attempt)
    elif outcome == 'ephemeral':
        return _retry_ephemeral(end, attempt)
    elif outcome == 'unknown':
        # Nothing classified the pod. Without evidence the monitor cannot tell a
        # reaped node from a range that really failed, and a run that reports
        # success on a range nobody verified is worse than one that stops.
        return CONDEMN
    elif verdict.get('exitCode') == config.CATCHUP_INCOMPLETE_EXIT:
        return _decide_exit3(end, attempt)
    elif verdict.get('exitCode') is None:
        # The verdict came from the Job condition, which says Failed and nothing
        # about why. Same absence of evidence as `unknown`, same answer.
        return CONDEMN
    else:
        return CONDEMN      # a genuine catchup failure


def budget_for(verdict, end, attempt):
    """(spent, cap) for the cause that killed this attempt.

    config.ATTEMPT_BUDGETS is the whole retry policy; a cause with no entry caps
    at 0 and is condemned on sight. `spent` counts only THIS cause, so evictions
    cannot drain the OOM or disk budgets. This verdict is already on disk, so
    the Nth failure is the one that exhausts a budget of N.
    """
    outcome = verdict['outcome']
    return (records._cause_count(end, attempt, (outcome,)),
            config.ATTEMPT_BUDGETS.get(outcome, 0))


def _log_retry(end, attempt, verdict, decision, cap):
    if verdict['outcome'] == 'oom':
        logger.error(
            "!!! OOM RETRY !!! range %s was OOM-killed on attempt %d/%d; retrying with "
            "memory limit %s -- RAISE THE CONFIGURED MEMORY LIMIT, this run is only "
            "surviving by escalating at runtime", end, attempt, cap, decision.memory)
    elif verdict['outcome'] == 'ephemeral':
        logger.error(
            "!!! DISK RETRY !!! range %s %s on attempt %d/%d; retrying with "
            "ephemeral-storage %s -- RAISE THE CONFIGURED EPHEMERAL STORAGE, this "
            "run is only surviving by escalating at runtime",
            end, decision.reason, attempt, cap, decision.ephemeral)
    else:
        logger.warning("range %s %s on attempt %d/%d; retrying",
                       end, decision.reason, attempt, cap)


def pods_by_job():
    """One list per reconcile, indexed by Job name.
    """
    out = {}
    for p in kube.core_v1.list_namespaced_pod(
            config.NAMESPACE, label_selector=f"{config.LABEL_RUN}={config.RUN_NAME}").items:
        jn = (p.metadata.labels or {}).get('batch.kubernetes.io/job-name')
        if jn:
            out.setdefault(jn, p)
    return out


def read_mission_start():
    """When this run first started, or None if not recorded yet.

    Its own file: progress.json is keyed by ledger range, and anything else in
    it would be walked as one. On the volume so it survives a monitor restart,
    which is what makes mission_duration span the run rather than the process.
    """
    try:
        with open(_MISSION_START) as fh:
            return float(fh.read())
    except (OSError, ValueError):
        return None


if __name__ == '__main__':
    main()
