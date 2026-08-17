"""The range list, and the Job that runs one attempt of it."""
import logging

from kubernetes.aio import client

import cluster
import config
import sizing

logger = logging.getLogger('job_monitor')

# Resume replays from LCL+1 on a PVC. LCL comes from core's own accessor: v27
# dropped ledgerheaders, and the log fallback goes blind above INFO.
RESUME_SCRIPT = r'''set -e
KEY="%(key)s"
TARGET=%(target)d
COUNT=%(count)d
MARK=/data/.job-key
RESUME=false
LCL=""
if [ -f "$MARK" ] && [ "$(cat "$MARK" 2>/dev/null)" = "$KEY" ]; then
  LCL=$(/usr/bin/stellar-core --conf /config/stellar-core.cfg offline-info --console 2>/dev/null \
        | sed -n 's/.*"num"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' | head -1 || true)
  if [ -z "$LCL" ]; then
    PREV_LOG=$(ls -t /data/stellar-core*.log 2>/dev/null | head -n 1 || true)
    if [ -n "$PREV_LOG" ]; then
      LCL=$(grep -oE "Ledger close complete: [0-9]+" "$PREV_LOG" 2>/dev/null | tail -1 | grep -oE "[0-9]+$" || true)
    fi
  fi
  echo "RESUME PROBE: lcl '${LCL:-none}'"
  # Re-running catchup on a finished range applies nothing and exits 2
  # identically every time, so it would burn the whole budget on completed work.
  if [ -n "$LCL" ] && [ "$LCL" -ge "$TARGET" ] 2>/dev/null; then
    echo "ALREADY COMPLETE: $KEY reached $LCL >= target $TARGET"
    exit 0
  fi
  if [ -n "$LCL" ] && [ "$LCL" -ge $((TARGET - COUNT)) ] && [ "$LCL" -lt "$TARGET" ] 2>/dev/null; then
    RESUME=true; echo "RESUME: $KEY reached $LCL; skipping new-db"
  else
    echo "RESUME DECLINED: $KEY last close '${LCL:-none}'; bucket phase incomplete"
  fi
fi
printf '%%s' "$KEY" > "$MARK"
if [ "$RESUME" != "true" ]; then
  /usr/bin/stellar-core --conf /config/stellar-core.cfg new-db --console
fi
exec /usr/bin/stellar-core --conf /config/stellar-core.cfg catchup "$KEY" \
  --metric 'ledger.transaction.apply' --console
'''


def range_list():
    """(end, count) for every range this run owes, in dispatch order.

    A pure function of the /start spec. A restart must reproduce it exactly: a
    different list means work silently duplicated or skipped, and nothing else
    would notice.
    """
    start, latest = config.STARTING_LEDGER, config.LATEST_LEDGER_NUM
    per_job, overlap = config.LEDGERS_PER_JOB, config.OVERLAP_LEDGERS

    # Strictly greater, and overlap added on top of the clamped stride: `>=`
    # emits a range ending AT the start ledger, which is below genesis and
    # exits 2, and clamping the total would drop the overlap at that end.
    ranges, end = [], latest
    while end > start:
        stride = min(end - start, per_job)
        ranges.append((end, stride + overlap))
        end -= stride

    if config.RANGE_ORDER == 'oldest-first':
        # The cheap ranges finish first, which is what a profiling run wants: it
        # measures the inexpensive end before anything can interrupt it.
        return list(reversed(ranges))
    if config.RANGE_ORDER == 'longest-first':
        # Validated at /start, so a profile exists here.
        return sorted(ranges, key=_measured_seconds, reverse=True)
    # tip-first: the bucket set only grows with ledger position, so the tip
    # ranges are the slowest and the most worth starting early.
    return ranges


def _measured_seconds(item):
    """Sort key for longest-first. An unmeasured range sorts FIRST.

    profile_for returns the nearest measured end ABOVE, so a range with no
    seconds is newer than anything ever measured -- and cost rises with ledger
    position. Unknown means assume worst, not assume average. It also runs
    early under the most generous sizing, which is what makes the next profile
    cover it instead of it being the range a run dies before reaching.
    """
    seconds = (sizing.profile_for(item[0]) or {}).get('seconds')
    return (1, 0) if seconds is None else (0, seconds)


def job_name(end, attempt):
    return f"{config.RUN_NAME}-r{end}-a{attempt}"


def job_key(end, count):
    return f"{end}/{count}"


async def create(end, count, attempt, oom_count=0, memory=None, ephemeral=None):
    """Create one attempt's Job, idempotent by name."""
    owner = await cluster.owner_ref()
    volume = await _data_volume(end, owner)
    body = _job(end, count, attempt, owner, volume, oom_count, memory, ephemeral)
    return await cluster.create_job(body)


async def _data_volume(end, owner):
    if config.STORAGE_MODE == 'pvc':
        name = await cluster.ensure_pvc(end, owner)
        return client.V1Volume(name='data', persistent_volume_claim=(
            client.V1PersistentVolumeClaimVolumeSource(claim_name=name)))
    return client.V1Volume(name='data', empty_dir=client.V1EmptyDirVolumeSource())


def _job(end, count, attempt, owner, data_volume, oom_count, memory, ephemeral):
    labels = {config.LABEL_RUN: config.RUN_NAME,
              config.LABEL_RANGE: str(end),
              config.LABEL_ATTEMPT: str(attempt)}
    return client.V1Job(
        metadata=client.V1ObjectMeta(name=job_name(end, attempt),
                                     owner_references=owner, labels=labels),
        spec=client.V1JobSpec(
            # Retries are the monitor's, not the controller's: raising a memory
            # request needs a new Job, because spec.template is immutable.
            backoff_limit=0,
            active_deadline_seconds=config.ATTEMPT_DEADLINE_SECONDS or None,
            ttl_seconds_after_finished=config.JOB_TTL_SECONDS,
            pod_failure_policy=client.V1PodFailurePolicy(rules=_failure_rules()),
            template=client.V1PodTemplateSpec(
                # LABEL_ATTEMPT has to be on the POD too: the collector reads it
                # to pick which range-<end>-a<n>.* files the attempt owns.
                metadata=client.V1ObjectMeta(labels=labels),
                spec=_pod(end, count, attempt, data_volume, oom_count,
                          memory, ephemeral))))


def _failure_rules():
    """Rules in evaluation order, which IS the contract with classify-from-Job.

    All FailJob so the Job fails with reason=PodFailurePolicy and the message
    names the rule index; a Count action surfaces as BackoffLimitExceeded and
    loses the signal entirely.
    """
    return [
        client.V1PodFailurePolicyRule(
            action='FailJob',
            on_pod_conditions=[client.V1PodFailurePolicyOnPodConditionsPattern(
                type='DisruptionTarget', status='True')]),
        client.V1PodFailurePolicyRule(
            action='FailJob',
            on_exit_codes=client.V1PodFailurePolicyOnExitCodesRequirement(
                container_name='stellar-core', operator='In', values=[137])),
        client.V1PodFailurePolicyRule(
            action='FailJob',
            on_exit_codes=client.V1PodFailurePolicyOnExitCodesRequirement(
                container_name='stellar-core', operator='NotIn', values=[0])),
    ]


def _pod(end, count, attempt, data_volume, oom_count, memory, ephemeral):
    requests, limits = sizing.requests_for(end, oom_count, memory, ephemeral)
    script = RESUME_SCRIPT % {'key': job_key(end, count), 'target': end, 'count': count}
    container = client.V1Container(
        name='stellar-core', image=config.CORE_IMAGE,
        command=['/bin/sh', '-c', script],
        env=([client.V1EnvVar(name='ASAN_OPTIONS', value=config.ASAN_OPTIONS)]
             if config.ASAN_OPTIONS else []),
        resources=client.V1ResourceRequirements(requests=requests, limits=limits),
        ports=[client.V1ContainerPort(container_port=11626, name='http')],
        lifecycle=_prestop(),
        volume_mounts=[client.V1VolumeMount(name='data', mount_path='/data'),
                       client.V1VolumeMount(name='config', mount_path='/config')])
    return client.V1PodSpec(
        # IRSA for the S3 history mirror; without it workers fall back to the
        # public archive, which throttles at 1024.
        service_account_name=config.WORKER_SERVICE_ACCOUNT or None,
        # Never restarted in place: the pod stays terminal and inspectable.
        restart_policy='Never',
        termination_grace_period_seconds=config.WORKER_GRACE_SECONDS,
        affinity=_affinity(end, oom_count),
        tolerations=([client.V1Toleration(key=config.TOLERATE_TAINT, effect='NoSchedule')]
                     if config.TOLERATE_TAINT else None),
        containers=[container],
        volumes=[data_volume, client.V1Volume(
            name='config', config_map=client.V1ConfigMapVolumeSource(
                name=f"{config.RUN_NAME}-stellar-core-config"))])


def _affinity(end, oom_count):
    """Require and avoid in ONE matchExpressions list.

    Expressions within a term are ANDed and separate terms are ORed, so an
    avoid-only pod in its own term would match every node.
    """
    match = []
    if config.NODE_LABEL_KEY:
        match.append(client.V1NodeSelectorRequirement(
            key=config.NODE_LABEL_KEY, operator='In',
            values=[sizing.node_label_value(end, oom_count)]))
    for key, value in config.label_pairs(config.REQUIRE_NODE_LABELS):
        # Literal, unlike the pool-routed pair above: properties of the pool
        # rather than of the range, so they do not vary per attempt.
        match.append(client.V1NodeSelectorRequirement(
            key=key, operator='In', values=[value]))
    if config.AVOID_NODE_LABEL_KEY:
        # No value means "avoid the label however it is set", which is
        # DoesNotExist; NotIn [""] would only exclude the empty value.
        match.append(client.V1NodeSelectorRequirement(
            key=config.AVOID_NODE_LABEL_KEY,
            operator='NotIn' if config.AVOID_NODE_LABEL_VALUE else 'DoesNotExist',
            values=[config.AVOID_NODE_LABEL_VALUE] if config.AVOID_NODE_LABEL_VALUE else None))
    if not match:
        return None
    return client.V1Affinity(node_affinity=client.V1NodeAffinity(
        required_during_scheduling_ignored_during_execution=client.V1NodeSelector(
            node_selector_terms=[client.V1NodeSelectorTerm(match_expressions=match)])))


def _prestop():
    """A preStop that stalls the kubelet, or None.

    Refuses to install one that cannot finish inside the grace period: the
    kubelet kills it mid-sleep, reports FailedPreStopHook, and signals the
    container anyway -- so the delay is not bought and an error is logged for
    every evicted pod.
    """
    sleep = config.WORKER_PRESTOP_SLEEP_SECONDS
    if sleep <= 0:
        return None
    if sleep >= config.WORKER_GRACE_SECONDS:
        logger.warning("preStop %ss does not fit in grace %ss; not installing it",
                       sleep, config.WORKER_GRACE_SECONDS)
        return None
    return client.V1Lifecycle(pre_stop=client.V1LifecycleHandler(
        _exec=client.V1ExecAction(command=['/bin/sleep', str(sleep)])))
