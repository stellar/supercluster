"""Parallel catchup job monitor.

Owns dispatch as well as reporting. Redis, worker.sh and the range-generator
scripts are gone; a Kubernetes Job per ledger range replaces them.

State model -- the controller itself keeps nothing authoritative in memory:

  desired    computed from config by a pure function (uniform | logarithmic)
  completed  durable, in a ConfigMap -- Jobs are reclaimed during a long run,
             so their absence must NOT be read as "never ran"
  in-flight  live Jobs, by label selector

A restart recomputes all three and carries on. The single-writer property (one
replica, Recreate) is what removes the claim/requeue races the redis queue had:
work is *assigned*, never claimed.
"""

import asyncio
import gzip
import bisect
import json
import logging
import os
import re
import sys
import tempfile
import threading
import time
import zlib
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer

import aiohttp
from kubernetes import client, config
from kubernetes.client.rest import ApiException
from prometheus_client import (CONTENT_TYPE_LATEST, REGISTRY, Counter, Gauge,
                               Histogram, generate_latest)

# Histogram buckets
#                  5m  15m   30m    1h  1.5h    2h
metric_buckets = (300, 900, 1800, 3600, 5400, 7200, float("inf"))

# Configuration is grouped by who consumes the value:
#   1. stellar-core workload  -- goes into the worker container or catchup args
#   2. Kubernetes objects     -- shape of the Jobs, pods and PVCs we create
#   3. monitor behaviour      -- never leaves this process

# =============================================================================
# 1. stellar-core workload
# =============================================================================
CORE_IMAGE = os.getenv('CORE_IMAGE')
ASAN_OPTIONS = os.getenv('ASAN_OPTIONS', '')

# Which ledger ranges to run. These are pure inputs to the range generator:
# dispatch recomputes the whole list every reconcile, so a restart must
# reproduce it exactly.
RANGE_GENERATOR = os.getenv('RANGE_GENERATOR', 'uniform')      # uniform | logarithmic
# Both generators emit tip-first, which front-loads the most expensive ranges:
# the bucket set only grows with ledger position. 'oldest-first' reverses that,
# so a profiling run measures the cheap early ranges before it can be
# interrupted, and the expensive tip ranges last.
RANGE_ORDER = os.getenv('RANGE_ORDER', 'tip-first')            # tip-first | oldest-first
STARTING_LEDGER = int(os.getenv('STARTING_LEDGER', 0))
LATEST_LEDGER_NUM = int(os.getenv('LATEST_LEDGER_NUM', 0))
LEDGERS_PER_JOB = int(os.getenv('LEDGERS_PER_JOB', 16000))
OVERLAP_LEDGERS = int(os.getenv('OVERLAP_LEDGERS', 320))
# logarithmic only: chunk size halves toward the tip and stops shrinking here.
LOGARITHMIC_FLOOR_LEDGERS = int(os.getenv('LOGARITHMIC_FLOOR_LEDGERS', 64000))

# =============================================================================
# 2. Kubernetes objects this monitor creates
# =============================================================================
NAMESPACE = os.getenv('NAMESPACE', 'default')
RUN_NAME = os.getenv('RUN_NAME', 'parallel-catchup')
PROGRESS_CM = f"{RUN_NAME}-catchup-progress"
LABEL_RUN = 'catchup.stellar.org/run'
LABEL_RANGE = 'catchup.stellar.org/range-end'
LABEL_ATTEMPT = 'catchup.stellar.org/attempt'

# Workers need IRSA to read the S3 history mirror. Without it they silently fall
# back to the public archive, which throttles at 1024 and kills the run with
# curl 22 -> catchup exit 3. The name matches the old StatefulSet's so existing
# IRSA trust policies keep matching.
WORKER_SERVICE_ACCOUNT = os.getenv('WORKER_SERVICE_ACCOUNT', '')

# Pod resources.
REQ_CPU = os.getenv('REQ_CPU', '1800m')
REQ_MEM = os.getenv('REQ_MEM', '9Gi')
LIM_CPU = os.getenv('LIM_CPU', '2')
LIM_MEM = os.getenv('LIM_MEM', '24000Mi')
# Only meaningful in ephemeral storage mode; see check_storage_config().
# Range profile from an earlier run: tightens per-range requests so more
# workers fit per node. Requests only -- limits stay as configured, so the
# failure semantics and the OOM/disk escalation ladders are unchanged.
PROFILE_PATH = os.getenv('PROFILE_PATH', '')
PROFILE_MARGIN = float(os.getenv('PROFILE_MARGIN', 1.15))
# CPU limit for a range the profile has measured. Higher than the unprofiled
# default on purpose: at a 2-core limit every range pegs 2.0, so the measured
# peak is a ceiling and the profile can never learn real demand. Room above the
# request lets each run's peak climb until it finds the true one.
# Empty = no cpu limit at all on a measured range. Measured on ssc-test with
# one pod per node (m8id/NVMe, 16320-ledger range): 168s at limit 2, 111s at 4,
# 99s uncapped. cpu.weight still derives from the request, so a burst only uses
# cycles the neighbours are not using. Set a value to cap it again.
#
# The gain is in bucket-apply and replay, not download: at 65280 ledgers, two
# pods at limit 2 and limit 4 had written 8001 and 8013 MiB after 43 minutes --
# identical -- because the download phase is storage-bound, not CPU-bound.
PROFILE_CPU_LIMIT = os.getenv('PROFILE_CPU_LIMIT', '')
# No safety margin on cpu, unlike memory. Under-requesting cpu costs contention
# and the pod can still burst; under-requesting memory gets it OOMKilled.
# Ceiling for profile-derived memory, above the unprofiled limit for the same
# reason: a range that really needs more than the configured limit must be able
# to ask for it rather than be pinned under its own measured peak. The OOM
# escalation ladder can still climb past this on a retry.
PROFILE_MAX_MEM = os.getenv('PROFILE_MAX_MEM', '32Gi')
# Memory is sized from rss (the range's real demand), NOT from peak working
# set. Working set is whatever limit it was measured under -- the kernel grows
# page cache to fill it -- so sizing from it is circular. Measured on ssc-test
# with one 420-ledger range: working set went 2.33 -> 3.61 -> 7.48 -> 13.49 GiB
# under 2560Mi/4Gi/8Gi/24000Mi limits while rss moved only 2256 -> 2488 MiB, and
# wall-clock did not move at all (776s / 775s / 746s / 773s). Catchup streams --
# buckets are downloaded once, applied once, ledgers replayed once -- so cache
# has nothing to give back and PROFILE_MARGIN alone is the allowance.
# A multiplicative margin alone is not enough: memory.max bounds anon PLUS page
# cache, and at small rss 10% is nothing. Measured on ssc-test 2026-07-29 with
# headroom 0: ranges profiled at 190 MiB rss got a 209 MiB limit -- 19 MiB of
# slack for all growth and cache -- and 90 of them OOMKilled within 90s. The
# earlier 4Gi validation hid this because 1.1x of 2.4 GiB is 240 MiB of slack.
PROFILE_CACHE_HEADROOM = os.getenv('PROFILE_CACHE_HEADROOM', '512Mi')

REQ_EPHEMERAL = os.getenv('REQ_EPHEMERAL', '')
LIM_EPHEMERAL = os.getenv('LIM_EPHEMERAL', '')

# Placement. The taint toleration is emitted as {key, effect} with no value:
# the default Equal operator does not match "" against "true".
NODE_LABEL_KEY = os.getenv('NODE_LABEL_KEY', '')
NODE_LABEL_VALUE = os.getenv('NODE_LABEL_VALUE', '')
TOLERATE_TAINT = os.getenv('TOLERATE_TAINT', '')

# Worker /data. pvc keeps it across pods, so an evicted range resumes at L+1 --
# that is what makes spot viable. ephemeral puts it on the node disk: denser
# packing, no resume, and REQ_EPHEMERAL must be sized to hold the catchup DB.
# One PVC per range, not per concurrency slot: measured on ssc-test, 300 jobs
# with a PVC each cost no more wall-clock than 300 jobs reusing 40.
STORAGE_MODE = os.getenv('STORAGE_MODE', 'pvc')                # pvc | ephemeral
STORAGE_CLASS = os.getenv('STORAGE_CLASS', '')
STORAGE_SIZE = os.getenv('STORAGE_SIZE', '40Gi')
# A Nitro node allows ~26 EBS attachments (CSINode allocatable), and Karpenter
# sizes nodes on CPU/memory only -- it will happily put 40 volume-mounting pods
# on one 4-vCPU node, where they serialise through the attachment slots and get
# rejected with VolumeAttachmentLimitExceeded (observed on ssc-test).
#
# Guard with a spread constraint rather than a warning. maxSkew alone cannot cap
# per-node count -- with a single node there is one domain and therefore no skew
# -- so minDomains is what forces enough nodes. Both are inert at realistic
# density: REQ_CPU=1800m yields ~4 workers on an 8-vCPU node, so CPU demands far
# more nodes than this floor ever asks for. 0 disables.
MAX_VOLUMES_PER_NODE = int(os.getenv('MAX_VOLUMES_PER_NODE', 24))

# Job/pod lifetimes.
# SIGTERM -> SIGKILL budget. stellar-core exits ~7s after SIGTERM (measured), so
# this is slack rather than a target.
WORKER_GRACE_SECONDS = int(os.getenv('GRACE_SECONDS', 100))
# Must comfortably exceed any plausible monitor outage: completion is recorded
# to the ConfigMap by this process, and a Job reclaimed before that happens
# reads as "never ran" and gets redone.
# Backstop only. reconcile() deletes each Job explicitly once its record is
# durable, so the TTL exists for the cases that skip that path: a terminally
# failed range kept for inspection, or a success whose metrics never landed.
JOB_TTL_SECONDS = int(os.getenv('JOB_TTL_SECONDS', 600))
# Measured on ssc-test: stellar-core does NOT fail on an unreachable history
# archive, an absent ledger range, or a bucket that will not decompress. It
# retries every mirror with growing backoff and stays Running indefinitely --
# no exit code, no failure, the slot held for the life of the run. A hang is a
# more likely real failure than a non-zero exit, and this deadline is the only
# thing that makes it observable. 0 disables.
ATTEMPT_DEADLINE_SECONDS = int(os.getenv('ATTEMPT_DEADLINE_SECONDS', 0))

# kube-state-metrics turns a pod's `mission` label into label_mission, which the
# Grafana container panels join on. Every other mission gets it from
# StellarKubeSpecs; this chart never has, so parallel catchup has never appeared
# in those panels.
#
# OFF by default and deliberately so: those panels are sum() by (pod, container)
# with a legend table, so at 1024 workers they would pull ~1024 series into any
# view with mission=$__all selected, degrading a shared dashboard for people who
# did not ask for it. Enable per-run once the panels aggregate (topk).
MISSION = os.getenv('MISSION', '')
EMIT_MISSION_LABEL = os.getenv('EMIT_MISSION_LABEL', 'false').lower() == 'true'

# =============================================================================
# 3. This monitor's own behaviour
# =============================================================================
PARALLELISM = int(os.getenv('PARALLELISM', 3))
# Effectively the OOM budget: `failed` is the only other outcome that reaches
# it, and that one sets no retry reason. Escalation counts OOMs rather than
# attempts, so rung N means the range genuinely wanted more N times.
#
# Deliberately stops short of MEM_ESCALATION_CAP: 5 rungs is 1.5^4 = 5x the
# profile figure, and a range needing more than that is not mis-sized, it is
# broken -- chasing it to 48Gi parks a whole r8a.2xlarge on one range for hours.
# The cost of stopping is that the range is condemned, and today a condemned
# range aborts the run. That coupling is the thing to fix, not this number.
MAX_ATTEMPTS_PER_RANGE = int(os.getenv('MAX_ATTEMPTS', 5))
# A hang gets far fewer retries than an eviction. The measured causes -- an
# unreachable archive host, an absent checkpoint, a bucket that will not
# decompress -- are persistent, so retrying mostly burns another full deadline.
MAX_TIMEOUT_ATTEMPTS = int(os.getenv('MAX_TIMEOUT_ATTEMPTS', 2))
# Evictions, admission rejections and monitor restarts say nothing about the
# ledger range, so they get their own, larger budget. Sharing MAX_ATTEMPTS with
# real failures means cluster churn can fail a healthy range: measured on
# ssc-test, ten evictions across 25 workers put four ranges on attempt 3 of 5
# without a single genuine catchup error.
MAX_DISRUPTION_ATTEMPTS = int(os.getenv('MAX_DISRUPTION_ATTEMPTS', 20))
# An ephemeral-storage eviction repeats identically until the range gets more
# disk, so it must not sit on the environmental budget.
MAX_EPHEMERAL_ATTEMPTS = int(os.getenv('MAX_EPHEMERAL_ATTEMPTS', 4))
EPH_BUMP_FACTOR = float(os.getenv('EPH_BUMP_FACTOR', 1.5))
EPH_ESCALATION_CAP = os.getenv('EPH_ESCALATION_CAP', '200Gi')
ENVIRONMENTAL_OUTCOMES = ('disrupted', 'rejected', 'unknown')
# Verdicts only the pod can produce, and which a Job-level DeadlineExceeded must
# never overwrite. Each names a specific mechanism -- the kubelet OOM-killed it,
# the node was draining, the ephemeral limit blew -- and each earns a different
# retry budget and a different remediation. "The Job ran too long" is also true
# of every one of them and says nothing about which. An OOM downgraded to a
# timeout retries at the same memory limit that just killed it and gets 2
# attempts instead of 5; a spot eviction downgraded to a timeout gets 2 instead
# of 20.
POD_AUTHORITATIVE_OUTCOMES = ('oom', 'disrupted', 'ephemeral', 'timeout')
# stellar-core's "did not complete". Ambiguous by construction: a corrupt bucket
# and a SIGTERM during replay both produce it, so it must never be treated as
# proof that a range is broken.
CATCHUP_INCOMPLETE_EXIT = 3
# An OOM means requests/limits are mis-sized for this range. Escalate so the run
# can finish, but say so loudly -- surviving by escalating at runtime is a
# configuration bug, not a success.
MEM_BUMP_FACTOR = float(os.getenv('MEM_BUMP_FACTOR', 1.5))
# Ceiling for that escalation. Above the largest schedulable node the retry sits
# Pending forever, which looks like a hang rather than a failure.
MEM_ESCALATION_CAP = os.getenv('MAX_MEM', '48Gi')

# Reconcile loop: dispatch, refresh status, publish metrics. The env var is
# named LOGGING_INTERVAL_SECONDS for historical reasons, from when this loop
# only logged.
RECONCILE_INTERVAL_SECONDS = int(os.getenv('LOGGING_INTERVAL_SECONDS', 10))
# /healthz fails if the loop has not ticked within this long; a wedged loop
# stops all dispatch, so restart the container rather than run half-alive.
RECONCILE_STALE_SECONDS = float(os.getenv('WATCH_STALE_SECONDS', 600))
# Liveness ping to each running worker's admin port, fanned out on one event
# loop. Done serially with a 5s timeout this took a 192s median at 1024
# (measured in prod), which is why it is async and the timeout is short.
WORKER_PING_TIMEOUT_SECONDS = float(os.getenv('PING_TIMEOUT_SECS', 2))

# Shared with the log-collector sidecar, which owns writes here: it streams each
# worker's log and records the .outcome verdict while the pod still exists.
LOG_DIR = os.getenv('LOG_DIR', '/logs')
SAVE_SUCCESS_LOGS = os.getenv('SAVE_SUCCESS_LOGS', 'true').lower() == 'true'


def get_logging_level():
    name_to_level = {
        'CRITICAL': logging.CRITICAL,
        'ERROR': logging.ERROR,
        'WARNING': logging.WARNING,
        'INFO': logging.INFO,
        'DEBUG': logging.DEBUG,
    }
    result = name_to_level.get(os.getenv('LOGGING_LEVEL', 'INFO'))
    return result if result is not None else logging.INFO


# On the logs PVC, not the monitor's emptyDir: /data dies with the pod, and the
# mission tars /logs -- so an OOM-retry storm, the loudest signal this thing
# produces, was visible only in `kubectl logs` and never reached the run's
# destination directory. Falls back to /data if LOG_DIR is not mounted.
log_file_name = f"job_monitor_{datetime.now(timezone.utc).strftime('%Y-%m-%d_%H-%M-%S')}.log"
_log_dir = os.getenv('LOG_DIR', '/logs')
_chosen_log_dir = _log_dir if os.path.isdir(_log_dir) else '/data'
# Last resort, and unreachable in a pod: /data is the monitor's own emptyDir, so
# one of the two above always exists there. Off-cluster neither does, and a
# FileHandler on a missing directory made this module impossible to import --
# which is why nothing here was ever tested against a real reconcile().
if not os.path.isdir(_chosen_log_dir):
    _chosen_log_dir = tempfile.gettempdir()
log_file_path = os.path.join(_chosen_log_dir, log_file_name)
logging.basicConfig(level=get_logging_level(), format='%(asctime)s - %(levelname)s - %(message)s', handlers=[
    logging.StreamHandler(sys.stdout),
    logging.FileHandler(log_file_path),
])
logger = logging.getLogger()

# The env var is exactly what load_incluster_config() itself keys on, so in a pod
# this is the unconditional call it always was -- a missing token or CA still
# raises here and crash-loops the container rather than running blind. Outside a
# pod there is nothing to load and import stays pure; the caller injects clients.
if os.getenv('KUBERNETES_SERVICE_HOST'):
    config.load_incluster_config()
else:
    logger.warning("KUBERNETES_SERVICE_HOST is unset: no in-cluster config loaded. "
                   "Every API call will fail until core_v1/batch_v1 are replaced.")
# client-go's Python equivalent defaults are fine for a few LISTs per cycle, but
# dispatching ~1024 Jobs + PVCs at once needs headroom.
_cfg = client.Configuration.get_default_copy()
_cfg.connection_pool_maxsize = int(os.getenv('CONNECTION_POOL', 64))
client.Configuration.set_default(_cfg)
core_v1 = client.CoreV1Api()
batch_v1 = client.BatchV1Api()


def _gib(q):
    try:
        return _quantity_bytes(q) / (1024 ** 3)
    except Exception:
        return None


def check_storage_config():
    """The two halves of the storage choice are set independently and can disagree.

    In ephemeral mode /data is an emptyDir on the node disk, so the
    ephemeral-storage request must be large enough to hold the catchup DB and
    buckets -- otherwise the kubelet evicts the pod for exceeding it. In PVC
    mode the opposite is true: a large request makes disk the binding dimension
    and halves workers-per-node (measured: 2/node instead of 4 on a 2xlarge).
    """
    req = _gib(REQ_EPHEMERAL) if REQ_EPHEMERAL else None
    if STORAGE_MODE == 'ephemeral':
        if req is None or req < 20:
            logger.error("STORAGE_MODE=ephemeral but ephemeral-storage request is %s. "
                         "/data lives on the node disk in this mode; too small a request "
                         "gets the pod evicted mid-catchup. Expect ~35Gi.",
                         REQ_EPHEMERAL or "unset")
    if STORAGE_MODE == 'pvc':
        # One EBS volume per worker, and a Nitro node allows ~26 attachments
        # (CSINode allocatable). Density comes from the CPU request: 1800m gives
        # ~4 workers on an 8-vCPU node, far below the cap. A small request packs
        # many volume-mounting pods onto one node, where they serialise through
        # the attachment slots -- observed on ssc-test as pods rejected with
        # VolumeAttachmentLimitExceeded. Karpenter sizes on CPU/memory and does
        # not provision extra nodes for attachment capacity.
        try:
            cpu = REQ_CPU
            millis = int(cpu[:-1]) if cpu.endswith('m') else int(float(cpu) * 1000)
            if millis and 8000 // millis > 20:
                logger.warning("STORAGE_MODE=pvc with REQ_CPU=%s packs ~%d workers (and volumes) "
                               "onto an 8-vCPU node, near the ~26 EBS attachment limit. Expect "
                               "VolumeAttachmentLimitExceeded rejections under churn.",
                               REQ_CPU, 8000 // millis)
        except (ValueError, ZeroDivisionError):
            pass
    if req is not None and STORAGE_MODE == 'pvc' and req > 10:
        logger.warning("STORAGE_MODE=pvc but ephemeral-storage request is %s. /data is on "
                       "a PVC, so this only makes disk the binding dimension and reduces "
                       "workers per node. Expect ~2Gi.", REQ_EPHEMERAL)

status = {
    'num_remain': 1,  # non-zero until the first real update, so callers don't see a premature 0
    'queue_remain_count': 0,
    'queue_succeeded_count': 0,
    'queue_failed_count': 0,
    'queue_in_progress_count': 0,
    'jobs_failed': [],
    'jobs_in_progress': [],
    'workers': [],
    'workers_up': 0,
    'workers_down': 0,
    'workers_refresh_duration': 0,
    'mission_duration': 0,
}
status_lock = threading.Lock()
# Heartbeat for /healthz: a wedged reconcile loop stops all dispatch, so the
# container should be restarted rather than left running half-alive.
reconcile_alive = {'ts': 0.0}


metric_catchup_queues = Gauge('ssc_parallel_catchup_queues', 'Exposes size of each job queues', ["queue"])
metric_workers = Gauge('ssc_parallel_catchup_workers', 'Exposes catch up worker status', ["status"])
metric_refresh_duration = Gauge('ssc_parallel_catchup_workers_refresh_duration_seconds', 'Time it took to refresh status of all workers')
metric_full_duration = Histogram('ssc_parallel_catchup_job_full_duration_seconds', 'Exposes full job duration as histogram', buckets=metric_buckets)
metric_tx_apply_duration = Histogram('ssc_parallel_catchup_job_tx_apply_duration_seconds', 'Exposes job TX apply duration as histogram', buckets=metric_buckets)
# full_duration is the SUCCESSFUL attempt only, matching what worker.sh timed.
# wall_duration spans first dispatch to success, so (wall - full) is exactly the
# work lost to retries -- the cost of running on spot.
metric_wall_duration = Histogram('ssc_parallel_catchup_job_wall_duration_seconds',
                                 'First dispatch to success, including failed attempts',
                                 buckets=metric_buckets)
metric_mission_duration = Gauge('ssc_parallel_catchup_mission_duration_seconds', 'Number of seconds since the mission started ')
metric_retries = Counter('ssc_parallel_catchup_job_retried_count', 'Number of jobs that were retried')
# Separates infrastructure churn from application failure: many evictions with
# zero app failures is spot behaving as intended.
metric_evictions = Counter('ssc_parallel_catchup_job_spot_eviction_count', 'Pod attempts lost to node disruption')
metric_pvc_released = Counter('ssc_parallel_catchup_pvc_released_count', 'PVCs deleted after their range completed')
metric_jobs_reaped = Counter('ssc_parallel_catchup_jobs_reaped_count', 'Finished Jobs deleted after their record was durable')
metric_oom_retries = Counter('ssc_parallel_catchup_job_oom_retried_count', 'Jobs retried with an escalated memory limit')
metric_eph_retries = Counter('ssc_parallel_catchup_job_ephemeral_retried_count', 'Jobs retried with an escalated ephemeral-storage limit')


class RequestHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/healthz':
            stale = time.time() - reconcile_alive['ts']
            ok = reconcile_alive['ts'] > 0 and stale < RECONCILE_STALE_SECONDS
            self.send_response(200 if ok else 503)
            self.send_header('Content-type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps({'reconcile_age_seconds': round(stale, 1)}).encode())
        elif self.path == '/status':
            self.send_response(200)
            self.send_header('Content-type', 'application/json')
            self.end_headers()
            with status_lock:
                self.wfile.write(json.dumps(status).encode())
        elif self.path == '/prometheus':
            self.send_response(200)
            self.send_header('Content-type', CONTENT_TYPE_LATEST)
            self.end_headers()
            self.wfile.write(generate_latest(REGISTRY))
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, *args):
        pass  # the default handler logs every request to stderr


# --- range generation -------------------------------------------------------
# Ports uniform_range_generator.sh and logarithmic_range_generator.sh. These
# must stay pure functions of config: dispatch derives the full range list on
# every reconcile, so a restart has to reproduce it exactly.

def _uniform_segment(start_ledger, end_ledger, seg_size):
    """Ranges over (start_ledger, end_ledger], largest ledger first."""
    out = []
    el = end_ledger
    while el > start_ledger:
        ledgers_per_job = min(el - start_ledger, seg_size)
        out.append((el, ledgers_per_job + OVERLAP_LEDGERS))
        el -= ledgers_per_job
    return out


def _ordered(ranges):
    """Dispatch order. Generators emit tip-first; reverse for oldest-first."""
    return list(reversed(ranges)) if RANGE_ORDER == 'oldest-first' else ranges


def generate_ranges():
    if RANGE_GENERATOR == 'uniform':
        return _ordered(_uniform_segment(STARTING_LEDGER, LATEST_LEDGER_NUM, LEDGERS_PER_JOB))

    # Logarithmic: early history is cheap per ledger, so use big chunks there and
    # halve the chunk size as we approach the tip. Aims for roughly equal
    # wall-time per job rather than equal ledger count.
    out = []
    start_ledger = STARTING_LEDGER
    end_ledger = LATEST_LEDGER_NUM // 2
    chunk = (end_ledger - start_ledger + 1) // max(PARALLELISM, 1)
    while chunk > LOGARITHMIC_FLOOR_LEDGERS:
        out.extend(_uniform_segment(start_ledger, end_ledger, chunk))
        start_ledger = end_ledger + 1
        chunk //= 2
        end_ledger = start_ledger + (chunk * PARALLELISM)
    out.extend(_uniform_segment(end_ledger + 1, LATEST_LEDGER_NUM, LOGARITHMIC_FLOOR_LEDGERS))
    return _ordered(out)


def job_key(end, count):
    return f"{end}/{count}"


def job_name(end, attempt):
    return f"{RUN_NAME}-r{end}-a{attempt}"


# --- durable progress record ------------------------------------------------
# Jobs get reclaimed during a 10h run, so completion cannot live only in Job
# objects. Written BEFORE a Job becomes TTL-eligible.

# Set once at startup; the same ConfigMap the Jobs and PVCs hang off.
_progress_owner = {}


# The authoritative copy of the progress record lives on the logs PVC, not in
# the ConfigMap. A ConfigMap is capped at 1 MiB and this record is ~172 bytes
# per completed range, so it dies at ~6100 ranges -- reachable simply by halving
# ledgersPerJob. Worse, every completion rewrote the whole document through the
# API server, so a full run meant thousands of escalating-size etcd writes.
#
# The ConfigMap is still written, because the mission driver reads it without
# exec'ing into the pod, but it is now a best-effort mirror: if it fails, the
# run carries on from the file.
PROGRESS_FILE = os.path.join(LOG_DIR, 'progress.json')


def load_progress():
    try:
        with open(PROGRESS_FILE) as fh:
            return json.load(fh)
    except (OSError, ValueError):
        pass
    # First start on this volume, or an older run that only had the ConfigMap.
    try:
        cm = core_v1.read_namespaced_config_map(PROGRESS_CM, NAMESPACE)
        return json.loads((cm.data or {}).get('progress.json', '{}'))
    except ApiException as e:
        if e.status == 404:
            return {}
        raise


def save_status(snapshot):
    """Publish /status into the ConfigMap as well.

    The mission driver runs outside the cluster and already has a kube client,
    so reading a ConfigMap is simpler and more robust than exposing the monitor
    through a Gateway/HTTPRoute just to be polled. Shape is identical to the
    HTTP /status body, so the driver's parser is unchanged.
    """
    _patch_cm({'status.json': json.dumps(snapshot, separators=(',', ':'))})


# Measurements live only on the volume. The ConfigMap is the mission-state
# mirror the driver reads for visibility, and at ~172 bytes per range the
# profiling fields alone push it toward the 1 MiB cap at ~6100 ranges. Stripped
# to attempts/count it is ~30 bytes, so state stays readable at any slicing
# while the profile has no ceiling at all.
# A strip list, not a produce list: peakCpuCores is no longer measured, but a
# progress record resumed from an older run still carries it, and letting it
# through is what pushes the ConfigMap mirror toward the 1 MiB cap.
_PROFILE_ONLY_FIELDS = ('peakAnonBytes', 'peakRssBytes', 'peakWorkingSetBytes', 'peakCpuCores',
                        'peakEphemeralBytes', 'txApply', 'seconds', 'wallSeconds')


def _state_only(progress):
    out = dict(progress)
    completed = {}
    for end, rec in (progress.get('completed') or {}).items():
        completed[end] = {k: v for k, v in rec.items()
                          if k not in _PROFILE_ONLY_FIELDS}
    out['completed'] = completed
    return out


def save_progress(progress):
    blob = json.dumps(progress, separators=(',', ':'))
    # File first and atomically: it is what a restart reads back.
    tmp = PROGRESS_FILE + '.tmp'
    with open(tmp, 'w') as fh:
        fh.write(blob)
    os.replace(tmp, PROGRESS_FILE)
    # Mirror for the driver. Never fatal -- a 413 here used to throw inside
    # reconcile, and the loop swallows exceptions, so no completion would ever
    # be recorded again and every finished range would be dispatched forever.
    try:
        _patch_cm({'progress.json': json.dumps(_state_only(progress),
                                               separators=(',', ':'))})
    except ApiException as e:
        logger.warning("progress ConfigMap mirror failed (%s); the record on %s "
                       "is authoritative and the run continues", e.status, PROGRESS_FILE)


def _patch_cm(data, owner=None):
    body = {'data': data}
    try:
        core_v1.patch_namespaced_config_map(PROGRESS_CM, NAMESPACE, body)
    except ApiException as e:
        if e.status != 404:
            raise
        core_v1.create_namespaced_config_map(NAMESPACE, client.V1ConfigMap(
            # Owned by the chart's stellar-core ConfigMap like the Jobs and
            # PVCs, so `helm uninstall` reclaims it. Without an owner this
            # outlived every run and accumulated in the shared namespace.
            metadata=client.V1ObjectMeta(name=PROGRESS_CM, labels={LABEL_RUN: RUN_NAME},
                                         owner_references=_progress_owner.get('ref')),
            data=body['data']))


# --- worker liveness --------------------------------------------------------
# Serially pinging 1024 workers with a 5s timeout was measured at a 192s median
# and 773s max in prod (155 unreachable x 5s). Fanning out on one event loop
# bounds it by the timeout itself.

async def _ping_all(pods):
    timeout = aiohttp.ClientTimeout(total=WORKER_PING_TIMEOUT_SECONDS)
    async with aiohttp.ClientSession(timeout=timeout) as session:
        async def one(pod, ip):
            url = f"http://{ip}:11626/info"
            try:
                async with session.get(url):
                    return pod, True
            except Exception:
                return pod, False
        return dict(await asyncio.gather(*(one(p, ip) for p, ip in pods)))


def ping_workers(pods):
    """pods: (name, ip) pairs.

    By IP, not DNS. The <pod>.<service>.<ns>.svc form needs a per-pod A record,
    which a headless Service only publishes for endpoints whose EndpointSlice
    carries a hostname -- and that comes from pod.spec.hostname, which a Job pod
    cannot set to its own generated name. Measured on ssc-test: every ping
    failed to resolve and workers_up sat at 0 for the whole run.
    """
    if not pods:
        return {}
    return asyncio.run(_ping_all(pods))


# --- worker log capture -----------------------------------------------------

def backstop_save_pod_log(pod_name, end, attempt):
    """Last-resort archive for a range the collector never streamed.

    The log-collector sidecar owns /logs in the normal case: it holds a
    follow=true stream from pod start, so nothing is lost when the node is
    reaped. This only covers the gap where a pod lived and died entirely while
    the collector was down -- detected by the absence of the state file the
    collector writes when it claims a range.

    Never writes over a claimed or existing archive: two writers appending to
    one gzip would interleave members and duplicate lines.
    """
    if os.path.exists(state_path(end, attempt)):
        return True          # collector has it (streaming or already finished)
    path = log_path(end, attempt)
    if os.path.exists(path):
        return True
    try:
        body = core_v1.read_namespaced_pod_log(pod_name, NAMESPACE, container='stellar-core')
    except ApiException as e:
        logger.warning("could not save log for range %s attempt %d (pod %s): %s",
                       end, attempt, pod_name, e.reason)
        return False
    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        tmp = path + '.tmp'
        with gzip.open(tmp, 'wt') as fh:
            fh.write(body)
        os.replace(tmp, path)   # never leave a half-written archive behind
        return True
    except OSError as e:
        logger.warning("could not write %s: %s", path, e)
        return False


def log_path(end, attempt):
    """Canonical archive name, written by the log-collector sidecar.

    Deliberately carries no ok/failed suffix: which ranges failed is recorded in
    the progress ConfigMap, and encoding it here would mean two components
    disagreeing about a filename.
    """
    return os.path.join(LOG_DIR, f"range-{end}-a{attempt}.log.gz")


def state_path(end, attempt):
    return os.path.join(LOG_DIR, f"range-{end}-a{attempt}.state")


def outcome_path(end, attempt):
    return os.path.join(LOG_DIR, f"range-{end}-a{attempt}.outcome")


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
    # Kubelet can reject a pod before any container runs -- observed on
    # ssc-test: reason=VolumeAttachmentLimitExceeded, "Node has reached its
    # volume attachment limit, rejecting pod". There is no exit code and no
    # DisruptionTarget, so without this it falls through to 'failed' and a
    # transient admission rejection kills the whole run.
    if pod.status.reason == 'Evicted' and 'ephemeral' in (pod.status.message or ''):
        # Measured on ssc-test: the kubelet sets no DisruptionTarget for a
        # limit eviction, and stellar-core drains on the eviction SIGTERM and
        # exits 3 -- so the Job condition matches the generic non-zero rule and
        # reads as a plain catchup failure, which gets no retry at all.
        # status.message is the only discriminator and only the pod carries it.
        return {'outcome': 'ephemeral', 'exitCode': None, 'reason': pod.status.message}
    if pod.status.reason in ('VolumeAttachmentLimitExceeded', 'OutOfcpu', 'OutOfmemory',
                             'OutOfpods', 'UnexpectedAdmissionError', 'NodeAffinity',
                             'Shutdown', 'Evicted'):
        return {'outcome': 'rejected', 'exitCode': None, 'reason': pod.status.reason}
    if pod.status.reason == 'DeadlineExceeded':
        # The deadline lives on the PodSpec, so it is the kubelet that fires it
        # and the pod that carries the reason -- the Job just sees a non-zero
        # exit through its podFailurePolicy. Without this the drain-to-exit-3
        # matches the generic rule and reads as a plain catchup failure.
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
    path = outcome_path(end, attempt)
    if os.path.exists(path):
        return
    data = classify(pod)
    data['pod'] = pod.metadata.name
    # The only place a failed attempt's duration is ever available: the pod is
    # about to be reaped, and reconcile computes `seconds` solely on the success
    # path. Without it a resumed chain can only report its final leg.
    data['attemptSeconds'] = _pod_seconds(pod)
    try:
        tmp = path + '.tmp'
        with open(tmp, 'w') as fh:
            json.dump(data, fh)
        os.replace(tmp, path)
    except OSError as e:
        logger.warning("could not persist outcome for range %s: %s", end, e)


# The Job controller writes the exit code and pod name into the failure
# condition message, e.g.
#   "Container stellar-core for pod ns/kic-r400000-a1-xxxxx failed with exit
#    code 137 matching FailJob rule at index 1"
# Jobs are not bound to a node, so unlike the pod this survives consolidation.
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
            # No usable rule index. Measured on ssc-test (2026-07-28): a drained
            # stellar-core catches SIGTERM and exits 3 in ~7s, well inside the
            # 100s grace -- evictions do NOT produce 137. So a bare 137 is an OOM
            # with high confidence, and exit 3 without a DisruptionTarget
            # condition really is a catchup failure.
            outcome = 'oom' if code == 137 else 'failed'
        return {'outcome': outcome, 'exitCode': code,
                'pod': detail.group('pod') if detail else '',
                'source': 'job-condition'}
    return None


def read_outcome(end, attempt):
    try:
        with open(outcome_path(end, attempt)) as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


def _oom_count(end, attempt):
    """How many earlier attempts at this range were OOM-killed.

    Escalation must climb once per OOM, not once per attempt. On spot most
    retries are evictions -- measured on ssc-test 2026-07-30, 288 disruption
    retries against 7 OOM retries -- and a range disrupted three times then
    OOMing once would otherwise jump to base * 1.5^4, a 5x request for a single
    OOM. That inflation is fleet-wide and it is what exhausts the vCPU quota.
    """
    return sum(1 for n in range(1, int(attempt) + 1)
               if (read_outcome(end, n) or {}).get('outcome') == 'oom')


def verdict_path(end, attempt):
    return os.path.join(LOG_DIR, f"range-{end}-a{attempt}.verdict")


def save_verdict(end, attempt, outcome):
    """Persist the EFFECTIVE verdict for one attempt, so budgets can be tallied.

    The .outcome file is not enough on its own: it is classified from the pod,
    and a deadline kill reads as a plain exit-3 `failed` there -- only the Job's
    DeadlineExceeded condition says `timeout`. Reconcile resolves that conflict
    once, and this is where the answer is kept, on the same durable logs volume
    as everything else, so a monitor restart does not reset a range's budgets.
    """
    path = verdict_path(end, attempt)
    try:
        tmp = path + '.tmp'
        with open(tmp, 'w') as fh:
            fh.write(str(outcome))
        os.replace(tmp, path)
    except OSError as e:
        logger.warning("could not persist verdict for range %s attempt %s: %s",
                       end, attempt, e)


def _verdict_of(end, attempt):
    try:
        with open(verdict_path(end, attempt)) as fh:
            return fh.read().strip() or None
    except OSError:
        # Pre-fix runs, or an attempt whose verdict write lost the volume:
        # the pod-derived classification is the next best thing.
        return (read_outcome(end, attempt) or {}).get('outcome')


def _cause_count(end, attempt, causes):
    """How many of attempts 1..N at this range failed for one of `causes`.

    Budgets are per cause, not per attempt. One shared attempt index meant
    cluster churn -- which has its own deliberately large budget -- drained the
    small budgets belonging to the causes that say something about the range: a
    range evicted MAX_ATTEMPTS times had an effective OOM and disk budget of
    zero, was condemned on its first real OOM without ever being escalated, and
    took the whole mission with it.
    """
    return sum(1 for n in range(1, int(attempt) + 1)
               if _verdict_of(end, n) in causes)


def mem_for_attempt(attempt, base=None):
    """Memory limit after N OOMs, capped at MEM_ESCALATION_CAP.

    `base` is what attempt 1 actually ran with. It matters when a profile sized
    the range: escalating a 209Mi profiled range off the configured 24000Mi
    limit jumps straight to 36000Mi, a 172x overshoot that throws away the whole
    packing win on the first OOM.
    """
    base_q = _quantity_bytes(base or LIM_MEM)
    want = int(base_q * (MEM_BUMP_FACTOR ** max(0, attempt - 1)))
    cap = _quantity_bytes(MEM_ESCALATION_CAP)
    return _bytes_to_quantity(min(want, cap))


_UNITS = {'Ki': 1024, 'Mi': 1024**2, 'Gi': 1024**3, 'Ti': 1024**4,
          'K': 1000, 'M': 1000**2, 'G': 1000**3, 'T': 1000**4}


def _quantity_bytes(q):
    for suffix, mult in sorted(_UNITS.items(), key=lambda kv: -len(kv[0])):
        if q.endswith(suffix):
            return int(float(q[:-len(suffix)]) * mult)
    return int(float(q))


def _bytes_to_quantity(n):
    return f"{max(1, n // (1024 ** 2))}Mi"




# --- tx_apply ---------------------------------------------------------------

# medida prints the sum in scientific notation once it exceeds 1e6 ms, which is
# every range that applies a real transaction load. The old [0-9.]+ pattern
# matched "1.30722" then demanded "ms" and hit "e+06ms" instead, so tx_apply was
# silently missing for 25% of ranges -- 91-99% of everything above ledger 35M,
# exactly the expensive end.
_SUM_RE = re.compile(r"sum\s*=\s*([0-9.]+(?:[eE][+-]?[0-9]+)?)ms")


def metrics_path(end, attempt):
    return os.path.join(LOG_DIR, f"range-{end}-a{attempt}.metrics")


# A PVC's size is not a scheduling dimension -- growing it buys no packing, so
# it is not profiled; the only volume ceiling that matters is the ~26
# attachments per node. Ephemeral storage IS a scheduling dimension, so it is,
# but only in ephemeral mode and only on on-demand nodes -- see the collector.
# Any field may be absent and the consumer falls back to its default.
PEAK_FIELDS = ('peakAnonBytes', 'peakRssBytes', 'peakWorkingSetBytes',
               'peakEphemeralBytes')


def peaks_for_range(end, attempt=1):
    """Highest peak any attempt at this range reached, per axis.

    Not just the successful attempt. In pvc mode a pod that dies once replay has
    started leaves /data behind, and the next attempt resumes at LCL+1 with
    RESUME=true -- skipping the archive download and the bucket apply, which is
    where peak memory actually happens. Its peak describes the tail of the range,
    not the range, so profiling the winner alone under-reports by the whole
    download-vs-replay gap. On spot, where eviction is routine and resume is the
    entire point of durable /data, that would make the run unprofileable.

    Attempts that hit a ceiling are counted too. A pod OOM-killed at 8Gi really
    did allocate ~8Gi and wanted more, so its peak is a lower bound on demand,
    not an artifact of the limit -- and it is the attempt most worth keeping,
    because download concurrency scales with available cpu and a pod that
    bursted on an idle node can peak above the one that eventually succeeded.
    Sizing off the quieter attempt would OOM the range again. There is no false
    ratchet: a pod given 8Gi that only touches 1Gi records 1Gi.

    Advisory: used to size a LATER run's requests, never to decide anything
    about this one. Any field may be absent.
    """
    out = {}
    for n in _peak_attempts(end, attempt):
        try:
            with open(metrics_path(end, n)) as fh:
                data = json.load(fh)
        except (OSError, ValueError):
            continue
        for k in PEAK_FIELDS:
            v = data.get(k)
            if v is not None and v > out.get(k, 0):
                out[k] = v
    return out


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


def _hit_a_ceiling(end, attempt):
    """Was this attempt killed at one of its own resource limits?"""
    return (read_outcome(end, attempt) or {}).get('outcome') in ('oom', 'ephemeral')


def _peak_attempts(end, attempt):
    """Attempts whose peaks describe this range: the resumed chain, plus any
    attempt that died at a limit, wherever it sits.

    A ceiling-hit peak is evidence about the range no matter which pass
    produced it -- the process really did allocate that much and want more, so
    it is a lower bound on demand and the next run must size above it. That is
    the whole self-correcting loop: a range that OOMs at L records L, and
    L * PROFILE_MARGIN + PROFILE_CACHE_HEADROOM clears it next time.

    Without this the fresh-start rule silently drops it. Measured on ssc-test
    2026-07-30: an OOM during replay resumes (RESUME accepted, 224 of 252) and
    stays in the chain, but an OOM during download does not (25 of 252) -- and
    a run at higher cpu is download-bound, so the loop would go quiet exactly
    when it is most needed.

    Peaks only. tx_apply and seconds are summed, and a fresh start redoes work
    the dropped attempt already did, so including it there would double-count.
    """
    chain = set(_resumed_chain(end, attempt))
    return sorted(chain | {n for n in range(1, int(attempt) + 1)
                           if n not in chain and _hit_a_ceiling(end, n)})


def _resumed_chain(end, attempt):
    """Attempts describing one continuous pass over the range, oldest first.

    Stops at the last attempt that ran new-db: that one covered the whole range
    on its own, so nothing before it is part of the same pass.
    """
    first = int(attempt)
    while first > 1 and _attempt_resumed(end, first):
        first -= 1
    return range(first, int(attempt) + 1)


def _attempt_resumed(end, attempt):
    """Did this attempt pick up at LCL+1 rather than run new-db?

    Recorded by the collector from the worker's own "RESUME: ..." line, which is
    the only place that knows -- it depends on what was left on /data, not on
    storage mode or attempt number.
    """
    try:
        with open(metrics_path(end, attempt)) as fh:
            return bool(json.load(fh).get('resumed'))
    except (OSError, ValueError):
        return False


def tx_apply_for_range(end, attempt=1, pod_name=None):
    """Total 'ledger.transaction.apply' seconds for the whole range.

    Summed across the resumed chain, not read from the winning attempt alone.
    medida's total is per-process, so a pod that resumes at LCL+1 reports only
    the transactions it replayed -- on a range that was interrupted mid-replay
    that is the tail, not the range.

    Slightly over-counts: replay restarts at the checkpoint boundary containing
    LCL, so up to 64 ledgers can be applied twice. Against a 16320-ledger range
    that is <=0.4%, but it is a fixed ledger cost rather than a percentage, so
    it grows as ranges shrink.
    """
    total = None
    for n in _resumed_chain(end, attempt):
        # pod_name only ever names the LAST attempt's pod, so the archive/pod
        # fallbacks are offered to that one alone; earlier legs come from the
        # .metrics the collector already wrote.
        leg = _tx_apply_for_attempt(end, n, pod_name if n == int(attempt) else None)
        if leg is not None:
            total = leg if total is None else total + leg
    return total


def seconds_for_range(end, attempt=1, final=None):
    """Compute time for the whole range, summed across the resumed chain.

    `final` is the winning attempt's own duration, which reconcile has in hand
    from the pod. Earlier legs come from their .outcome, written when the
    monitor classified the failure and still had the pod.

    This is compute, not elapsed: the gaps between attempts -- scheduling, image
    pull, a node coming up -- are not in it. wallSeconds covers those.
    """
    total = None
    for n in _resumed_chain(end, attempt):
        if n == int(attempt):
            leg = final
        else:
            # .outcome is authoritative -- the pod's own terminated timestamps.
            # It is absent whenever the pod was reaped before the monitor could
            # classify it, which is every spot eviction, so fall back to the
            # collector's stream-lifetime figure rather than losing the leg.
            leg = (read_outcome(end, n) or {}).get('attemptSeconds')
            if leg is None:
                try:
                    with open(metrics_path(end, n)) as fh:
                        leg = json.load(fh).get('attemptSeconds')
                except (OSError, ValueError):
                    leg = None
        if leg is not None:
            total = leg if total is None else total + leg
    return total


def _tx_apply_for_attempt(end, attempt=1, pod_name=None):
    """Final 'ledger.transaction.apply' sum for ONE attempt, in seconds.

    stellar-core prints the medida block once at exit (we pass --metric), so
    this is the exact total for that process rather than a sample.

    Three sources, cheapest and most durable first:

      .metrics   the collector parsed it out of the live stream. Survives both
                 pod reaping and saveSuccessLogs=false.
      .log.gz    the collector's archive, if it was kept.
      pod log    only if a pod object still exists. Racing Karpenter, so this
                 is a fallback, never the plan.
    """
    try:
        with open(metrics_path(end, attempt)) as fh:
            value = json.load(fh).get('txApplySeconds')
        if value is not None:
            return float(value)
    except (OSError, ValueError, TypeError):
        pass
    raw = None
    candidate = log_path(end, attempt)
    if os.path.exists(candidate):
        try:
            with gzip.open(candidate, 'rt') as fh:
                raw = fh.read()
        except (OSError, EOFError, zlib.error):
            # A corrupt archive costs THIS RANGE its metric, never the pass.
            # EOFError is a truncated member -- the collector appending right
            # now, or one that was killed mid-append -- and is not an OSError,
            # so it used to escape the per-range work and abort the whole
            # reconcile: no recording, no reap, no dispatch for any of the
            # ~4000 ranges, repeating for as long as the torn bytes sat there.
            raw = None
    if raw is None:
        if pod_name is None:
            return None
        try:
            raw = core_v1.read_namespaced_pod_log(pod_name, NAMESPACE, tail_lines=400)
        except ApiException:
            return None
    lines = raw.splitlines()
    for i, line in enumerate(lines):
        if "metric 'ledger.transaction.apply'" not in line:
            continue
        for follow in lines[i + 1:i + 16]:
            m = _SUM_RE.search(follow)
            if m:
                return float(m.group(1)) / 1000.0
    return None


# --- job construction -------------------------------------------------------

# Resume decision, run before catchup. Only skip new-db when the DB on /data
# belongs to THIS range and replay had already started. Bucket apply uses
# createWithoutLoading() -- an unconditional INSERT that assumes a fresh DB --
# so a crash during that phase must start over. "Ledger close complete" is the
# cheap discriminator: bucket apply never closes a ledger.
#
# The LCL is read from stellar-core's own log on /data, NOT from the database:
# core 27 dropped the ledgerheaders table, so the old SQL probe silently
# returned empty and every interruption fell back to new-db.
RESUME_SCRIPT = r'''set -e
KEY="%(key)s"
TARGET=%(target)d
COUNT=%(count)d
MARK=/data/.job-key
RESUME=false
LCL=""
if [ -f "$MARK" ] && [ "$(cat "$MARK" 2>/dev/null)" = "$KEY" ]; then
  # Ask core for its own LCL. It reads storestate.lastclosedledgerheader through
  # its own accessor, so this survives both a schema change (v27 dropped
  # ledgerheaders, which is what silently disabled resume before) and any log
  # level above INFO. Safe here specifically: core has not started, so nothing
  # holds /data/buckets/stellar-core.lock. Core logs to the console alongside
  # the JSON, hence grepping rather than parsing.
  # One "num" key in the whole document and it is the ledger's -- verified
  # against 27.1.1 output on ssc-test 2026-07-30. Do NOT window this with
  # `grep -A<n> '"ledger":'`: bucketlist puts ~40 lines of hashes between the
  # key and "num", so a small window silently yields nothing and the probe
  # degrades to the log fallback without saying so.
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
  # Already at the target: a1 finished the replay and was evicted before it
  # could exit 0. Re-running catchup here applies nothing and stellar-core exits
  # 2, identically on every retry, so the range burns its whole budget and the
  # mission aborts the run over work that was actually done. Measured on
  # ssc-test 2026-07-30: range 16752063 killed a 2096-worker run that was 61
  # percent complete, exactly this way.
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
    cm = core_v1.read_namespaced_config_map(f"{RUN_NAME}-stellar-core-config", NAMESPACE)
    return [client.V1OwnerReference(api_version='v1', kind='ConfigMap',
                                    name=cm.metadata.name, uid=cm.metadata.uid,
                                    block_owner_deletion=True)]


def release_pvc(end):
    """Drop a completed range's volume.

    The PVC exists so an interrupted range resumes at L+1; once the range has
    succeeded there is nothing left to resume and the volume is dead weight.
    They are owner-referenced to the release, so without this they all survive
    until `helm uninstall` -- measured on ssc-test, 2032 bound PVCs and 79 TiB
    of gp3 provisioned a third of the way through a 3982-range run, heading for
    ~156 TiB and 3982 volumes against the account's volume ceiling.

    Best-effort: a failure here costs disk, never correctness, and the range is
    already recorded complete.
    """
    if STORAGE_MODE != 'pvc':
        return
    name = f"{RUN_NAME}-data-r{end}"
    try:
        core_v1.delete_namespaced_persistent_volume_claim(name, NAMESPACE)
        metric_pvc_released.inc()
    except ApiException as e:
        if e.status != 404:
            logger.warning("could not release PVC for completed range %s: %s", end, e)


def done_path(end, attempt):
    return os.path.join(LOG_DIR, f"range-{end}-a{attempt}.done")


def _attempt_finalized(end, attempt):
    """Has the collector written everything it will for this attempt?

    It writes this file last, after .metrics. Anything inferred instead -- peaks
    being present, tx_apply being readable -- is a guess: tx_apply falls back to
    the archive so it is available long before the collector finishes, and an
    attempt can legitimately finalize with no peaks at all.
    """
    return os.path.exists(done_path(end, attempt))


def _has_peaks(record):
    return any(record.get(k) is not None for k in PEAK_FIELDS)


def _reap_if_complete(end, attempt, record):
    """Delete a succeeded range's Job once nothing more can be learned from it.

    Deleting reaps the pod, and .metrics is the only place peaks live, so a
    reap before the collector finalizes makes the gap permanent -- that is
    exactly how a whole run's profile came back with txApply on every range and
    peaks on none. JOB_TTL_SECONDS still reclaims anything the collector never
    gets to, so a pod reaped before it could be read costs a late Job, not a
    stuck one.
    """
    if not _attempt_finalized(end, attempt):
        return
    reap_range_jobs(end)


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
        jobs = batch_v1.list_namespaced_job(
            NAMESPACE,
            label_selector=f"{LABEL_RUN}={RUN_NAME},{LABEL_RANGE}={end}").items
    except ApiException as e:
        logger.warning("could not list jobs for completed range %s: %s", end, e)
        return
    for j in jobs:
        try:
            batch_v1.delete_namespaced_job(j.metadata.name, NAMESPACE,
                                           propagation_policy='Background')
            metric_jobs_reaped.inc()
        except ApiException as e:
            if e.status != 404:
                logger.warning("could not delete finished job %s for range %s: %s",
                               j.metadata.name, end, e)


def delete_job(end, attempt):
    """Drop a finished Job once nothing more is owed by it.

    reconcile() lists every Job and Pod on each pass, so a finished Job is not
    free: it inflates two LIST calls for as long as it lingers. At 2048-4096
    parallelism with a real OOM or spot-eviction rate that is hundreds of dead
    objects per hour of run, and the apiserver pressure shows up as truncated
    list responses long before anything else complains.

    Callers must have persisted whatever they need first -- the logs, .outcome
    and .metrics all live on the monitor's volume by then, so the Job and its
    pod carry no information once the range is recorded. Best-effort: on
    failure JOB_TTL_SECONDS still reclaims it.
    """
    try:
        batch_v1.delete_namespaced_job(job_name(end, attempt), NAMESPACE,
                                       propagation_policy='Background')
        metric_jobs_reaped.inc()
    except ApiException as e:
        if e.status != 404:
            logger.warning("could not delete finished job for range %s attempt %d: %s",
                           end, attempt, e)


def ensure_pvc(end, owner):
    name = f"{RUN_NAME}-data-r{end}"
    try:
        core_v1.read_namespaced_persistent_volume_claim(name, NAMESPACE)
        return name
    except ApiException as e:
        if e.status != 404:
            raise
    spec = client.V1PersistentVolumeClaimSpec(
        access_modes=['ReadWriteOnce'],
        resources=client.V1VolumeResourceRequirements(requests={'storage': STORAGE_SIZE}))
    if STORAGE_CLASS:
        spec.storage_class_name = STORAGE_CLASS
    core_v1.create_namespaced_persistent_volume_claim(NAMESPACE, client.V1PersistentVolumeClaim(
        metadata=client.V1ObjectMeta(name=name, owner_references=owner,
                                     labels={LABEL_RUN: RUN_NAME, LABEL_RANGE: str(end)}),
        spec=spec))
    return name


def eph_for_attempt(attempt):
    """Ephemeral-storage size for attempt N, escalating after an eviction."""
    base_q = _quantity_bytes(LIM_EPHEMERAL)
    want = int(base_q * (EPH_BUMP_FACTOR ** max(0, attempt - 1)))
    return _bytes_to_quantity(min(want, _quantity_bytes(EPH_ESCALATION_CAP)))


def load_profile():
    """Per-range measurements from an earlier run, keyed by range end.

    Absent, unreadable or malformed all mean the same thing: size from the
    configured defaults. A profile is an optimisation, never a prerequisite.
    """
    if not PROFILE_PATH:
        return []
    try:
        with open(PROFILE_PATH) as fh:
            doc = json.load(fh)
    except (OSError, ValueError) as e:
        logger.warning("range profile %s unreadable (%s); using configured requests",
                       PROFILE_PATH, e)
        return []
    mode = doc.get('storageMode')
    cross_mode = bool(mode) and mode != STORAGE_MODE
    if cross_mode:
        # cpu and memory carry across modes -- they measure the same work. Disk
        # does not: a pvc run puts /data on the volume, so it never measures
        # node-local usage, and an ephemeral run's figure says nothing about a
        # pvc one. Keep the transferable axes and let disk fall back to the
        # configured default.
        logger.warning("range profile is for storageMode=%s but this run is %s; "
                       "using its cpu and memory, defaulting ephemeral storage",
                       mode, STORAGE_MODE)
    out = []
    for end, rec in (doc.get('ranges') or {}).items():
        try:
            end = int(end)
        except (TypeError, ValueError):
            continue
        if cross_mode:
            rec = {k: v for k, v in rec.items() if k != 'peakEphemeralBytes'}
        out.append((end, rec))
    out.sort()
    logger.info("loaded range profile: %d ranges from %s", len(out), PROFILE_PATH)
    return out


PROFILE = None


def profile_for(end):
    """Measurements to size this range from, or None to use the defaults.

    Exact end, else the nearest measured end ABOVE it. Cost rises with ledger
    position -- the bucket set only grows -- so a lower neighbour under-reports,
    and under-provisioning costs an eviction while over-provisioning only costs
    packing. Past the top of the profile there is nothing safe to extrapolate
    from, so fall back to the configured defaults.
    """
    if not PROFILE:
        return None
    end = int(end)
    idx = bisect.bisect_left(PROFILE, (end,))
    if idx < len(PROFILE) and PROFILE[idx][0] == end:
        return PROFILE[idx][1]
    return PROFILE[idx][1] if idx < len(PROFILE) else None


def _cpu_millis(q):
    return int(float(q[:-1])) if str(q).endswith('m') else int(float(q) * 1000)



def _sized(value, margin, cap):
    """A measured peak turned into a request: margin applied, never above cap."""
    want = int(value * margin)
    return _bytes_to_quantity(min(want, _quantity_bytes(cap)))


def _profile_overrides(end, escalated):
    """Request overrides for this range from the profile, or {} for none.

    Escalated retries opt out: an escalation is a measurement of THIS run and
    outranks anything an earlier one saw.
    """
    if escalated or end is None:
        return {}
    prof = profile_for(end)
    if not prof:
        return {}
    out = {}
    # peakAnonBytes is kubelet's rssBytes, sampled by the collector on its own
    # poll; peakRssBytes is the same quantity via a 30s Prometheus scrape. Prefer
    # the finer one and fall back, so a profile captured before the collector
    # tracked anon still sizes exactly as it used to.
    rss = prof.get('peakAnonBytes') or prof.get('peakRssBytes')
    if rss:
        want = int(rss * PROFILE_MARGIN) + _quantity_bytes(PROFILE_CACHE_HEADROOM)
        out['memory'] = _bytes_to_quantity(min(want, _quantity_bytes(PROFILE_MAX_MEM)))
    disk = prof.get('peakEphemeralBytes')
    if disk and LIM_EPHEMERAL:
        out['ephemeral-storage'] = _sized(disk, PROFILE_MARGIN, LIM_EPHEMERAL)
    return out


def _resources(mem=None, eph=None, end=None):
    # Before mem is defaulted below -- reading it afterwards can never see None,
    # which silently disabled profile sizing entirely.
    overrides = _profile_overrides(end, escalated=(mem is not None or eph is not None))
    mem = mem or LIM_MEM
    # Raise the request alongside the limit on an escalated retry: a pod that
    # OOMed at the old limit will not fit where it was scheduled before.
    req_mem = REQ_MEM if mem == LIM_MEM else mem
    req = {'cpu': REQ_CPU, 'memory': req_mem}
    lim = {'cpu': LIM_CPU, 'memory': mem}

    # Only meaningful in ephemeral mode. In PVC mode a large request makes disk
    # the binding dimension and halves workers-per-node for no reason.
    if REQ_EPHEMERAL:
        # Raise the request with the limit: ephemeral-storage is a scheduling
        # dimension, so a pod that outgrew its limit will not fit where it was
        # placed before.
        req['ephemeral-storage'] = eph or REQ_EPHEMERAL
    else:
        # pvc mode: /data is not on the node disk, so an ephemeral override
        # would size a dimension this run does not use.
        overrides.pop('ephemeral-storage', None)
    if LIM_EPHEMERAL:
        lim['ephemeral-storage'] = eph or LIM_EPHEMERAL

    # No cpu limit on any worker unless one is configured explicitly. Packing is
    # driven by the request; a limit only throttles a pod that could otherwise
    # use idle cores, and throttling changes what the range measures -- less cpu
    # means less download concurrency means a lower peak, so a throttled attempt
    # records a figure an unthrottled one cannot reproduce.
    #
    # This used to be applied only when _profile_overrides returned something,
    # which silently excluded two populations: unmeasured ranges, and escalated
    # retries (escalated returns {} as well). Measured on ssc-test 2026-07-30,
    # 214 a1 and 256 a2 pods were capped at cpu 2 while their peers ran free --
    # and for the retries that meant more memory and less cpu at the same time,
    # right after an OOM.
    if PROFILE_CPU_LIMIT:
        lim['cpu'] = PROFILE_CPU_LIMIT
    else:
        lim.pop('cpu', None)
    if overrides:
        # Memory and disk match request to limit: those are the dimensions worth
        # pinning, since exceeding either kills the pod outright.
        #
        # CPU is deliberately not matched. Its limit stays where it is
        # configured and only the request follows the measurement, so a range
        # packs by what it actually uses while keeping headroom to burst. That
        # leaves the pod Burstable rather than Guaranteed -- Kubernetes needs
        # all three to match -- which is the intended trade.
        for key, value in overrides.items():
            req[key] = lim[key] = value
    # Unmeasured range: the configured defaults, requests below limits, exactly
    # as before -- a range with no profile entry must behave as if there were no
    # profile at all.
    return client.V1ResourceRequirements(requests=req, limits=lim)


def volume_spread_constraints():
    """Keep PVC-mounting workers under the per-node EBS attachment limit.

    Only in pvc mode: in ephemeral mode /data is an emptyDir, no volume is
    attached, and spreading would just cost density.
    """
    if STORAGE_MODE != 'pvc' or MAX_VOLUMES_PER_NODE <= 0:
        return None
    min_domains = max(1, -(-PARALLELISM // MAX_VOLUMES_PER_NODE))   # ceil
    return [client.V1TopologySpreadConstraint(
        max_skew=MAX_VOLUMES_PER_NODE,
        min_domains=min_domains,
        topology_key='kubernetes.io/hostname',
        when_unsatisfiable='DoNotSchedule',
        label_selector=client.V1LabelSelector(match_labels={LABEL_RUN: RUN_NAME}))]


def pod_labels(end, attempt):
    """Labels on the worker POD, which are not the Job's.

    LABEL_ATTEMPT has to be here as well: the collector reads it off the pod to
    decide which range-<end>-a<n>.* files this attempt owns, and its default is
    "1". Measured on ssc-test 2026-07-30 -- with the label only on the Job, all
    2246 metrics files were a1 while 475 a2 pods were running, so every retry
    overwrote the first attempt's peaks instead of being maxed against them,
    peaks_for_range(end, 2) found nothing, and those Jobs were never reaped.
    """
    labels = {LABEL_RUN: RUN_NAME, LABEL_RANGE: str(end),
              LABEL_ATTEMPT: str(attempt)}
    if EMIT_MISSION_LABEL and MISSION:
        labels['mission'] = MISSION
    return labels


def build_job(end, count, attempt, owner, mem=None, eph=None):
    key = job_key(end, count)
    script = RESUME_SCRIPT % {'key': key, 'target': end, 'count': count}

    if STORAGE_MODE == 'pvc':
        data_vol = client.V1Volume(name='data', persistent_volume_claim=(
            client.V1PersistentVolumeClaimVolumeSource(claim_name=ensure_pvc(end, owner))))
    else:
        data_vol = client.V1Volume(name='data', empty_dir=client.V1EmptyDirVolumeSource())

    env = [client.V1EnvVar(name='ASAN_OPTIONS', value=ASAN_OPTIONS)] if ASAN_OPTIONS else []

    affinity = None
    if NODE_LABEL_KEY:
        affinity = client.V1Affinity(node_affinity=client.V1NodeAffinity(
            required_during_scheduling_ignored_during_execution=client.V1NodeSelector(
                node_selector_terms=[client.V1NodeSelectorTerm(match_expressions=[
                    client.V1NodeSelectorRequirement(key=NODE_LABEL_KEY, operator='In',
                                                     values=[NODE_LABEL_VALUE])])])))
    # Taint value must be absent: the mission emits {key, effect} with no value,
    # and the default Equal operator does not match "" against "true".
    tolerations = [client.V1Toleration(key=TOLERATE_TAINT, effect='NoSchedule')] if TOLERATE_TAINT else None

    container = client.V1Container(
        name='stellar-core', image=CORE_IMAGE,
        command=['/bin/sh', '-c', script], env=env, resources=_resources(mem, eph, end),
        ports=[client.V1ContainerPort(container_port=11626, name='http')],
        volume_mounts=[client.V1VolumeMount(name='data', mount_path='/data'),
                       client.V1VolumeMount(name='config', mount_path='/config')])

    return client.V1Job(
        metadata=client.V1ObjectMeta(
            name=job_name(end, attempt), owner_references=owner,
            labels={LABEL_RUN: RUN_NAME, LABEL_RANGE: str(end),
                    LABEL_ATTEMPT: str(attempt)}),
        spec=client.V1JobSpec(
            # The monitor owns retries, not the Job controller. With
            # backoffLimit>0 the controller would replace the pod on its own
            # schedule, so we could not classify disruption vs genuine catchup
            # failure, could not count evictions, and could not guarantee the
            # log is archived before the next attempt starts. 0 means the Job
            # fails once and stays put for inspection; reconcile() decides
            # whether to dispatch attempt N+1.
            #
            # A podFailurePolicy would be inert here -- with backoffLimit 0 every
            # pod failure already fails the Job, so Count and FailJob collapse to
            # the same outcome. Classification is done by reading the pod's
            # DisruptionTarget condition instead.
            backoff_limit=0,
            pod_failure_policy=client.V1PodFailurePolicy(
                rules=[r for _, r in _failure_rules()]),
            ttl_seconds_after_finished=JOB_TTL_SECONDS,
            template=client.V1PodTemplateSpec(
                metadata=client.V1ObjectMeta(labels=pod_labels(end, attempt)),
                spec=client.V1PodSpec(
                    # On the POD, not the JobSpec. JobSpec.activeDeadlineSeconds
                    # runs from the Job's startTime, so every second the pod
                    # spends Pending -- waiting for Karpenter, pulling the image
                    # -- is charged against a budget that is meant to bound how
                    # long the range RUNS. During a node-class outage this run
                    # sat ~15 minutes Pending and ranges died as "timeouts"
                    # having barely executed; a timeout gets
                    # MAX_TIMEOUT_ATTEMPTS, so two stalls condemn a range and
                    # fail the mission. The pod-level field starts at container
                    # start, which is the thing being bounded.
                    active_deadline_seconds=ATTEMPT_DEADLINE_SECONDS or None,
                    # IRSA for the S3 history mirror. Without it workers fall
                    # back to the public archive, which throttles at 1024.
                    service_account_name=WORKER_SERVICE_ACCOUNT or None,
                    # Keeps PVC-mounting workers under the per-node EBS
                    # attachment cap; inert at realistic CPU-bound density.
                    topology_spread_constraints=volume_spread_constraints(),
                    # Never, so a failed container is not restarted in place:
                    # the pod stays terminal and inspectable for classification
                    # and for the backstop log read.
                    restart_policy='Never',
                    termination_grace_period_seconds=WORKER_GRACE_SECONDS,
                    affinity=affinity, tolerations=tolerations,
                    containers=[container],
                    volumes=[data_vol, client.V1Volume(
                        name='config', config_map=client.V1ConfigMapVolumeSource(
                            name=f"{RUN_NAME}-stellar-core-config"))]))))


# --- reconcile --------------------------------------------------------------

def sync_counters(progress, counted):
    """Drive the counters from persisted state instead of from events.

    Two reasons not to .inc() as things happen:

    * a terminally-failed range stays the newest Job for its range, so an
      event-driven inc fires again on every reconcile until teardown
    * the process resets to zero on restart, while the underlying record
      (attempts in the progress ConfigMap, .outcome files on the PVC) survives

    Computing the true total and incrementing by the delta is monotonic,
    idempotent, and self-heals after a restart: the counter starts at 0 and the
    first sync walks it up to the recorded total.
    """
    retries = 0
    for rec in list(progress.get('completed', {}).values()) + list(progress.get('failed', {}).values()):
        retries += max(0, int(rec.get('attempts', 1)) - 1)

    oom = evicted = 0
    try:
        for name in os.listdir(LOG_DIR):
            if not name.endswith('.outcome'):
                continue
            try:
                with open(os.path.join(LOG_DIR, name)) as fh:
                    o = json.load(fh).get('outcome')
            except (OSError, ValueError):
                continue
            if o == 'oom':
                oom += 1
            elif o == 'disrupted':
                evicted += 1
    except OSError:
        pass

    for key, total, metric in (('retries', retries, metric_retries),
                               ('oom', oom, metric_oom_retries),
                               ('evicted', evicted, metric_evictions)):
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
        # `is not None`, not truthiness: a range with sum = 0ms records
        # txApply 0.0, which is a real observation and must not be dropped
        # silently. Same for a sub-second duration.
        for field, metric in (('seconds', metric_full_duration),
                              ('wallSeconds', metric_wall_duration),
                              ('txApply', metric_tx_apply_duration)):
            if (end, field) in replayed:
                continue
            value = rec.get(field)
            if value is None:
                continue
            replayed.add((end, field))
            metric.observe(value)


def pods_by_job():
    """One list per reconcile, indexed by Job name.

    This used to be a LIST per completed job, so a busy cycle at 1024 workers
    issued dozens of round trips for one pod each.
    """
    out = {}
    for p in core_v1.list_namespaced_pod(
            NAMESPACE, label_selector=f"{LABEL_RUN}={RUN_NAME}").items:
        jn = (p.metadata.labels or {}).get('batch.kubernetes.io/job-name')
        if jn:
            out.setdefault(jn, p)
    return out


def was_disrupted(pod):
    for cond in (pod.status.conditions or []):
        if cond.type == 'DisruptionTarget' and cond.status == 'True':
            return True
    return False


def reconcile(state):
    ranges = generate_ranges()
    by_end = {str(end): count for end, count in ranges}
    progress = load_progress()
    completed = progress.setdefault('completed', {})
    failed = progress.setdefault('failed', {})

    jobs = batch_v1.list_namespaced_job(
        NAMESPACE, label_selector=f"{LABEL_RUN}={RUN_NAME}").items
    job_pods = pods_by_job()

    live = {}           # range-end -> (attempt, job)
    for j in jobs:
        end = (j.metadata.labels or {}).get(LABEL_RANGE)
        attempt = int((j.metadata.labels or {}).get(LABEL_ATTEMPT, 1))
        prev = live.get(end)
        if prev is None or attempt >= prev[0]:
            live[end] = (attempt, j)

    in_progress = []
    for end, (attempt, j) in list(live.items()):
        st = j.status
        if st.succeeded:
            # Record BEFORE the Job's TTL can reclaim it: the per-attempt
            # `seconds` below is the pod's own start -> finish, and Karpenter
            # removes the pod ~1 min after the node empties. tx_apply no longer
            # depends on this window -- the collector persists it from the
            # stream.
            if end not in completed:
                pod = job_pods.get(j.metadata.name)
                # Job.startTime is the FIRST attempt, so it spans retries. The
                # successful pod's own start -> container finish is what
                # worker.sh used to report, and is the number comparable across
                # the redis cutover.
                seconds = _pod_seconds(pod) if pod is not None else None
                wall = None
                if st.start_time and st.completion_time:
                    wall = (st.completion_time - st.start_time).total_seconds()
                # Chain total, not this leg alone: a range that resumed spent
                # real time in the attempts before the winner. Falls back to the
                # single leg, then to wall, when nothing durable survived.
                seconds = seconds_for_range(end, attempt, seconds) or seconds
                if seconds is None:
                    seconds = wall   # pod already gone; wall is the only figure left
                # Not gated on `pod`: the collector's .metrics/.log.gz are
                # written from the live stream and outlive the pod, so a reaped
                # node must not cost us the metric.
                tx = tx_apply_for_range(end, attempt,
                                        pod.metadata.name if pod else None)
                if pod is not None and SAVE_SUCCESS_LOGS:
                    backstop_save_pod_log(pod.metadata.name, end, attempt)
                if tx is None:
                    logger.warning("could not read tx_apply for range %s (pod gone?); "
                                   "metric will be missing for this range", end)
                completed[end] = {'seconds': seconds, 'wallSeconds': wall,
                                  'txApply': tx, 'attempts': attempt}
                # Ledger count travels with the record: the logarithmic
                # generator varies it per range, so it cannot be recomputed
                # from config alone when the profile is read back.
                if by_end.get(end) is not None:
                    completed[end]['count'] = by_end[end]
                completed[end].update(peaks_for_range(end, attempt))
                # Durably recorded first: if this process dies between the two,
                # the range is still complete and simply keeps its volume.
                save_progress(progress)
                release_pvc(end)
                # Only once the record is complete. `tx is None` means the
                # collector had not flushed this range's .metrics yet, and the
                # pod is the only place left to read it from -- deleting the
                # Job would reap the pod and make that gap permanent. Leave
                # those to JOB_TTL_SECONDS.
                _reap_if_complete(end, attempt, completed[end])
            elif (not _has_peaks(completed[end])
                  or completed[end].get('txApply') is None
                  or not _attempt_finalized(end, attempt)):
                # Backfill. The record is written the moment the Job flips to
                # succeeded, which is usually before the collector has finalized
                # -- and peaks_for_range has no fallback, unlike tx_apply, which
                # reads the archive. Measured on ssc-test: 356 of 356 completed
                # ranges carried txApply and 0 carried peakAnonBytes, while 1936
                # .metrics files on the same volume held it. Retry while the Job
                # is still here; delete_job below is what ends the chances.
                late = peaks_for_range(end, attempt)
                if completed[end].get('txApply') is None:
                    # Same one-shot race as the peaks, and the same fix. The
                    # collector writes txApplySeconds into .metrics when it
                    # finalizes, which can land after reconcile recorded the
                    # range. Measured in the sandbox edge suite 2026-07-30:
                    # progress.json carried txApply=null while the durable
                    # .metrics file held txApplySeconds=0.000486848.
                    late_tx = tx_apply_for_range(end, attempt)
                    if late_tx is not None:
                        late = dict(late or {})
                        late['txApply'] = late_tx
                if late:
                    completed[end].update(late)
                    save_progress(progress)
                    logger.info("range %s: measurements arrived late, backfilled %s",
                                end, sorted(late))
                _reap_if_complete(end, attempt, completed[end])
        elif st.failed:
            # Completion is terminal for the range, so a Failed Job for a range
            # that is already recorded is garbage -- never an input to the retry
            # decision. Without this, a losing attempt that outlived the winner
            # gets re-classified (disrupted, unknown, ...) and the range is
            # dispatched all over again, against a PVC that was already
            # released, i.e. a full replay from genesis of work already paid
            # for. Sweep the leftover and move on.
            if end in completed:
                logger.info("range %s already recorded complete; discarding "
                            "leftover Job for attempt %d", end, attempt)
                reap_range_jobs(end)
                continue
            pod = job_pods.get(j.metadata.name)
            if pod is not None:
                record_outcome(end, attempt, pod)
                backstop_save_pod_log(pod.metadata.name, end, attempt)
            # Written by the log collector while the pod still existed; reading
            # the pod here would miss anything Karpenter already reaped.
            # 1. pod-derived verdict, recorded by the collector while it lived
            # 2. Job condition -- survives node consolidation, less precise
            # 3. unknown -- retry rather than condemn the run
            verdict = read_outcome(end, attempt) or classify_from_job(j)
            # ...with one exception. A deadline kill sends SIGTERM, stellar-core
            # drains and exits 3, and the pod-derived verdict therefore reads
            # `failed` -- which outranks the Job's DeadlineExceeded and condemns
            # a range that merely ran long. Only the Job knows the deadline
            # fired, so on that condition the Job wins. Measured in the sandbox
            # edge suite 2026-07-30: whichever of the two won the race decided
            # whether the range was retried or condemned.
            #
            # Ranked, not unconditional. The Job wins only where the pod has
            # nothing more specific to say -- an exit-3 drain, a rejection, no
            # surviving classification. Where the pod named the mechanism
            # (OOMKilled, DisruptionTarget, an ephemeral eviction) the pod wins,
            # because "ran too long" is also true of all of those and picking it
            # loses both the remediation and the correct retry budget.
            from_job = classify_from_job(j)
            if (from_job and from_job.get('outcome') == 'timeout'
                    and (verdict or {}).get('outcome') not in POD_AUTHORITATIVE_OUTCOMES):
                verdict = from_job
            if verdict is None:
                verdict = {'outcome': 'unknown', 'exitCode': None}
            elif verdict.get('source') == 'job-condition':
                logger.info("range %s attempt %d classified from Job condition "
                            "(exit %s); pod was already gone",
                            end, attempt, verdict.get('exitCode'))

            # Durable before anything reads a tally -- _oom_count and the
            # budget check below both count this attempt.
            save_verdict(end, attempt, verdict['outcome'])

            retry_mem = retry_eph = None
            if verdict['outcome'] == 'timeout':
                reason = (f"exceeded the {ATTEMPT_DEADLINE_SECONDS}s attempt deadline "
                          "(stuck retrying the history archive?)")
            elif verdict['outcome'] == 'rejected':
                reason = f"rejected by the node before starting ({verdict.get('reason', '?')})"
            elif verdict['outcome'] == 'disrupted':
                reason = "lost to node disruption"
            elif verdict['outcome'] == 'oom':
                base = (_profile_overrides(end, escalated=False) or {}).get('memory')
                # Rungs climbed = OOMs seen, not attempts made. This attempt's
                # own outcome is already on disk, so the count includes it.
                retry_mem = mem_for_attempt(_oom_count(end, attempt) + 1, base)
                reason = f"OOM-killed at memory limit {mem_for_attempt(attempt, base)}"
            elif verdict['outcome'] == 'ephemeral':
                retry_eph = eph_for_attempt(attempt + 1)
                reason = (f"evicted for exceeding its {eph_for_attempt(attempt)} "
                          f"ephemeral-storage limit")
            elif verdict['outcome'] == 'unknown':
                # The pod was gone before anything classified it -- almost always
                # because this process was down while the node was reaped. An
                # unclassified failure is NOT evidence of a bad ledger range, and
                # condemning the run on it would let a monitor restart fail a
                # 10-hour job. Retry; a genuinely broken range will exhaust its
                # attempts and fail with evidence.
                reason = "failed with no surviving classification (monitor restart?)"
            elif verdict.get('exitCode') == CATCHUP_INCOMPLETE_EXIT:
                # Exit 3 means "did not complete" and covers BOTH a corrupt
                # archive AND any interruption -- stellar-core catches SIGTERM,
                # drains and exits 3 in ~7s. Nothing in the exit code separates
                # them; only a DisruptionTarget condition does, and that is gone
                # the moment the pod is.
                #
                # Condemning on it made every graceful kill fatal, and a
                # condemned range aborts the whole mission. Measured in the
                # sandbox edge suite 2026-07-30: a pod deleted mid-replay, a pod
                # deleted mid-download, and an attempt-deadline kill were all
                # classified `failed` at attempt 1 and never retried -- the
                # resume path was unreachable through any of them.
                #
                # Retry on the ordinary range budget. A genuinely broken range
                # exhausts MAX_ATTEMPTS and fails with evidence; an interrupted
                # one succeeds, usually by resuming at LCL+1.
                reason = (f"exited {CATCHUP_INCOMPLETE_EXIT} (did not complete -- "
                          "corrupt archive or interruption, indistinguishable)")
            else:
                reason = None   # genuine catchup failure: do not retry

            # Four budgets, by whose fault the attempt was: a hang is usually
            # persistent and gets the lowest, a range that is genuinely broken
            # gets the middle one, and anything the cluster did to us gets the
            # highest.
            #
            # Each is spent by ITS OWN cause, never by the global attempt index.
            # Sharing one counter meant the cap was chosen by the latest verdict
            # and then compared against every retry the range had ever had: a
            # range that survived five spot evictions (legal, budget 20) reached
            # attempt 6, and its first genuine OOM was compared 6 >= 5 and
            # condemned -- never retried for an OOM, never escalated, and a
            # condemned range fails the mission. On spot, where evictions are
            # routine, that made the OOM and disk budgets effectively zero.
            if verdict['outcome'] == 'timeout':
                cap = MAX_TIMEOUT_ATTEMPTS
                spent = _cause_count(end, attempt, ('timeout',))
            elif verdict['outcome'] == 'ephemeral':
                cap = MAX_EPHEMERAL_ATTEMPTS
                spent = _cause_count(end, attempt, ('ephemeral',))
            elif verdict['outcome'] in ENVIRONMENTAL_OUTCOMES:
                cap = MAX_DISRUPTION_ATTEMPTS
                spent = _cause_count(end, attempt, ENVIRONMENTAL_OUTCOMES)
            else:
                # The range's own budget: an OOM and a "did not complete" are
                # both statements about this ledger range, so they share it.
                cap = MAX_ATTEMPTS_PER_RANGE
                spent = _cause_count(end, attempt, ('oom', 'failed'))
            # This attempt's verdict is already on disk, so `spent` includes it:
            # the Nth failure of a cause is the one that exhausts a budget of N,
            # exactly as `attempt < cap` behaved for a single-cause range.
            if reason is not None and spent < cap:
                if verdict['outcome'] == 'oom':
                    logger.error(
                        "!!! OOM RETRY !!! range %s was OOM-killed on attempt %d/%d; retrying with "
                        "memory limit %s -- RAISE THE CONFIGURED MEMORY LIMIT, this run is only "
                        "surviving by escalating at runtime", end, attempt, MAX_ATTEMPTS_PER_RANGE, retry_mem)
                elif verdict['outcome'] == 'ephemeral':
                    metric_eph_retries.inc()
                    logger.error(
                        "!!! DISK RETRY !!! range %s %s on attempt %d/%d; retrying with "
                        "ephemeral-storage %s -- RAISE THE CONFIGURED EPHEMERAL STORAGE, this "
                        "run is only surviving by escalating at runtime",
                        end, reason, attempt, cap, retry_eph)
                else:
                    logger.warning("range %s %s on attempt %d/%d; retrying",
                                   end, reason, attempt, MAX_ATTEMPTS_PER_RANGE)
                try:
                    batch_v1.create_namespaced_job(NAMESPACE, build_job(
                        int(end), by_end[end], attempt + 1, state['owner'], retry_mem, retry_eph))
                except ApiException as e:
                    if e.status != 409:
                        raise
                # After the successor exists, never before. If the create above
                # had failed with the predecessor already gone, the range would
                # have no live Job at all and the next pass would redispatch it
                # at attempt 1 -- losing the escalated memory that is the whole
                # point of the retry. live[] keys on the highest attempt, so the
                # two coexisting for one pass is already handled.
                #
                # Gated like the success path: deleting the Job reaps the pod,
                # and backstop_save_pod_log stands down for any range the
                # collector has claimed, so there is no second reader. Waiting
                # for .metrics means the collector has finalized this attempt --
                # its peaks, its tx_apply and its duration are all durable.
                # JOB_TTL_SECONDS reaps it if the collector never gets there.
                # Same marker the success path waits for. Peaks were a proxy:
                # an attempt that legitimately has none would never be reaped,
                # and one whose peaks landed early could be reaped while the
                # collector was still reading its log.
                if _attempt_finalized(end, attempt):
                    delete_job(end, attempt)
                in_progress.append(job_key(int(end), by_end[end]))
                continue
            if reason is not None:
                logger.error("range %s exhausted %d attempts (%s)", end, cap, reason)
            else:
                # The zero-retry path used to log nothing at all: the range just
                # appeared under failed{} and the mission aborted with no line
                # explaining why. Say it plainly.
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

    # Monotonic progress is invariant in a healthy run. A decrease means the
    # durable record or the Jobs were tampered with; redoing hours of work
    # silently is worse than stopping.
    if len(completed) < state['max_completed']:
        logger.error("PROGRESS WENT BACKWARDS: completed %d -> %d. Refusing to dispatch. "
                     "The progress ConfigMap or the Jobs were deleted underneath this run.",
                     state['max_completed'], len(completed))
        state['halted'] = True
    state['max_completed'] = max(state['max_completed'], len(completed))

    # Dispatch, heaviest range first (index 0 is the tip), up to PARALLELISM.
    #
    # A condemned range does NOT stop dispatch. It used to, which deadlocked the
    # driver: the mission waits for `remaining == 0 and in_progress == []`
    # (MissionHistoryPubnetParallelCatchupV2.fs), and a frozen dispatch leaves
    # `remaining` pinned at however many ranges were never sent, forever. The
    # mission still fails on a condemned range -- it reports once the run drains,
    # so the ranges that were already paid for are not thrown away.
    created = 0
    if not state['halted']:
        # No slots: a range's PVC is keyed by the range itself, so concurrency is
        # simply how many are in flight.
        capacity = PARALLELISM - len(in_progress)
        for end, count in ranges:
            if capacity <= 0:
                break
            key = str(end)
            if key in completed or key in failed or key in live:
                continue
            try:
                batch_v1.create_namespaced_job(NAMESPACE, build_job(
                    end, count, 1, state['owner']))
                created += 1
                capacity -= 1
                in_progress.append(job_key(end, count))
            except ApiException as e:
                if e.status != 409:   # AlreadyExists: name uniqueness is the mutex
                    raise

    observe_recorded(progress, state['replayed'])
    sync_counters(progress, state['counted'])
    return {
        'total': len(ranges),
        'completed': len(completed),
        'failed_ranges': [f"{job_key(int(k), by_end.get(k, 0))}|{v.get('pod', '')}"
                          for k, v in failed.items()],
        'in_progress': in_progress,
        'created': created,
        'remaining': len(ranges) - len(completed) - len(failed) - len(in_progress),
    }


def read_mission_start():
    """When this run first started, or None if not recorded yet.

    Its own ConfigMap key, not a field in progress.json: that document is keyed
    by ledger range, and anything else in it would be walked as if it were one.

    Read-only on purpose. Creating the ConfigMap here would race the owner
    reference, which is only known once reconcile has resolved it, and an
    ownerless progress ConfigMap survives `helm uninstall`.
    """
    try:
        cm = core_v1.read_namespaced_config_map(PROGRESS_CM, NAMESPACE)
        return float((cm.data or {})['started_at'])
    except (ApiException, KeyError, TypeError, ValueError):
        return None


def update_status_and_metrics():
    global status
    # None until reconcile has an owner reference to attach it to; until then
    # process start is correct anyway, because that IS the start of a new run.
    mission_start_time = read_mission_start() or time.time()
    check_storage_config()
    state = {'owner': None, 'replayed': set(), 'max_completed': 0, 'halted': False,
             'counted': {}}
    while True:
        try:
            reconcile_alive['ts'] = time.time()
            if state['owner'] is None:
                state['owner'] = owner_ref()
                _progress_owner['ref'] = state['owner']
                if read_mission_start() is None:
                    _patch_cm({'started_at': repr(mission_start_time)})

            r = reconcile(state)

            # Liveness of the workers that currently own a job -- idle slots are
            # deliberately not counted, matching the original metric.
            pods = [(p.metadata.name, p.status.pod_ip)
                    for p in core_v1.list_namespaced_pod(
                        NAMESPACE, label_selector=f"{LABEL_RUN}={RUN_NAME}",
                        field_selector='status.phase=Running',
                        # Served from the apiserver watch cache. Only safe here:
                        # a stale liveness sample is cosmetic, whereas stale
                        # dispatch state would re-run a range.
                        resource_version='0').items
                    if p.status.pod_ip]
            refresh_start = time.time()
            ping = ping_workers(pods)
            workers_refresh_duration = time.time() - refresh_start
            worker_statuses = [{'pod': p, 'status': 'running' if ok else 'down'}
                               for p, ok in ping.items()]
            workers_up = sum(1 for ok in ping.values() if ok)
            workers_down = len(ping) - workers_up

            mission_duration = time.time() - mission_start_time
            with status_lock:
                status = {
                    'num_remain': r['remaining'],
                    'queue_remain_count': r['remaining'],
                    'queue_succeeded_count': r['completed'],
                    'queue_failed_count': len(r['failed_ranges']),
                    'queue_in_progress_count': len(r['in_progress']),
                    'jobs_failed': r['failed_ranges'],
                    'jobs_in_progress': r['in_progress'],
                    'workers': worker_statuses,
                    'workers_up': workers_up,
                    'workers_down': workers_down,
                    'workers_refresh_duration': workers_refresh_duration,
                    'mission_duration': mission_duration,
                }
            metric_catchup_queues.labels(queue="remain").set(r['remaining'])
            metric_catchup_queues.labels(queue="succeeded").set(r['completed'])
            metric_catchup_queues.labels(queue="failed").set(len(r['failed_ranges']))
            metric_catchup_queues.labels(queue="in_progress").set(len(r['in_progress']))
            metric_workers.labels(status="up").set(workers_up)
            metric_workers.labels(status="down").set(workers_down)
            metric_refresh_duration.set(workers_refresh_duration)
            metric_mission_duration.set(mission_duration)
            logger.info("Status: %s", json.dumps(status))
            # Publish on change only -- a 10h run would otherwise issue ~3600
            # no-op ConfigMap writes.
            counts = (r['remaining'], r['completed'], len(r['failed_ranges']), len(r['in_progress']))
            if counts != state.get('last_counts'):
                state['last_counts'] = counts
                with status_lock:
                    save_status(status)

        except Exception as e:
            logger.exception("Error while reconciling: %s", str(e))

        time.sleep(RECONCILE_INTERVAL_SECONDS)


def run(server_class=HTTPServer, handler_class=RequestHandler):
    server_address = ('', 8080)
    httpd = server_class(server_address, handler_class)
    logger.info('Starting httpd server...')
    httpd.serve_forever()


if __name__ == '__main__':
    # Before any dispatch: the first Job built must already be sized from it.
    PROFILE = load_profile()

    # Not a logging thread despite the historical name -- this is the reconcile
    # loop: dispatch, progress record, metrics, status. Log capture and pod
    # classification live in the log-collector sidecar.
    reconcile_thread = threading.Thread(target=update_status_and_metrics)
    reconcile_thread.daemon = True
    reconcile_thread.start()

    # Separate thread: a blocking watch must not sit behind dispatch and the
    # liveness sweep, which is the whole point of it.
    run()
