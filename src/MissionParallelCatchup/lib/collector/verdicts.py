"""Why an attempt ended, decided from the pod while the pod still exists.

The collector already lists every pod every few seconds to discover streams, so
it sees terminal transitions first-hand. The Job object cannot answer this: its
condition carries no exit code until a podFailurePolicy rule matches, and an
admission rejection matches none.
"""

import json
import logging
import os

import records

logger = logging.getLogger('log_collector')


def condemnation_reason(pod):
    """The DisruptionTarget reason if the cluster has committed to destroying
    this pod, else None -- a spot reclaim, a drain or node pressure.
    """
    for cond in ((pod.get('status') or {}).get('conditions') or []):
        if cond.get('type') == 'DisruptionTarget' and cond.get('status') == 'True':
            return cond.get('reason') or 'Unknown'
    return None


def classify(pod):
    """The outcome this pod's status implies, as a verdict dict."""
    status = pod.get('status', {})
    if condemnation_reason(pod):
        return {'outcome': 'disrupted', 'exitCode': None}
    reason = status.get('reason')
    if reason == 'Evicted' and 'ephemeral' in (status.get('message') or ''):
        # The range's own disk use, not something the cluster did to it. The
        # kubelet sets no DisruptionTarget for a limit eviction and stellar-core
        # exits 3 on the eviction SIGTERM, so the Job condition reads as a plain
        # catchup failure, which gets no retry at all. status.message is the only
        # discriminator and only the pod carries it, so it must be caught here.
        return {'outcome': 'ephemeral', 'exitCode': None, 'reason': status.get('message')}
    if reason in ('VolumeAttachmentLimitExceeded', 'OutOfcpu', 'OutOfmemory', 'OutOfpods',
                  'UnexpectedAdmissionError', 'NodeAffinity', 'Shutdown', 'Evicted'):
        return {'outcome': 'rejected', 'exitCode': None, 'reason': reason}
    terms = [cs.get('state', {}).get('terminated') for cs in status.get('containerStatuses', [])]
    terms = [t for t in terms if t]
    if not terms:
        # Nothing ever ran, so this says nothing about the ledger range.
        return {'outcome': 'rejected', 'exitCode': None, 'reason': reason or 'no container status'}
    for t in terms:
        if t.get('reason') == 'OOMKilled':
            return {'outcome': 'oom', 'exitCode': t.get('exitCode')}
        if t.get('exitCode') not in (0, None):
            return {'outcome': 'failed', 'exitCode': t.get('exitCode')}
    # Terminated, but not one container said with what. `failed` here is a lie
    # that costs the whole run -- it reads as a genuine catchup failure, the one
    # outcome that gets no retry: range 59018943 was condemned on attempt 1 with
    # exitCode null, failing a mission that was otherwise 554 for 554.
    return {'outcome': 'unknown', 'exitCode': None}


def record_outcome(pod, end, attempt):
    """Write the verdict next to the log, for job_monitor's reconcile to read."""
    path = records.outcome_path(end, attempt)
    if os.path.exists(path):
        return
    data = classify(pod)
    data['pod'] = pod['metadata']['name']
    try:
        records.write_atomic(path, json.dumps(data))
        logger.info("range %s attempt %s classified: %s", end, attempt, data['outcome'])
    except OSError as e:
        logger.warning("could not persist outcome for range %s: %s", end, e)
