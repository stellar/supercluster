"""Every knob, and the validation a run is admitted through.

Read through the module -- `config.PARALLELISM`, never `from config import
PARALLELISM`. /start rebinds several of these after validating them, and an
imported name binds a copy that never sees the rebind.

Numbers that arrive from the chart stay strings until validate() coerces them.
Coercing at import made a bad value a boot crash, and a process that cannot
start cannot report why.
"""
import logging
import os

logger = logging.getLogger('job_monitor')


def _int(name, default):
    """A chart-env integer, typed here rather than at /start.

    The loop idles on its interval while waiting for /start, so a string there
    crash-loops the pod before it can be told anything. A bad value falls back
    loudly rather than killing the boot; /start-delivered values get a 400.
    """
    raw = os.getenv(name)
    if raw is None:
        return default
    try:
        return int(raw)
    except (TypeError, ValueError):
        logger.error("%s=%r is not an integer; using %d", name, raw, default)
        return default


# --- identity, shared with the collector ------------------------------------
NAMESPACE = os.getenv('NAMESPACE', 'default')
RUN_NAME = os.getenv('RUN_NAME', 'parallel-catchup')
LOG_DIR = os.getenv('LOG_DIR', '/logs')

LABEL_RUN = 'catchup.stellar.org/run'
LABEL_RANGE = 'catchup.stellar.org/range-end'
LABEL_ATTEMPT = 'catchup.stellar.org/attempt'

# The collector writes one of these into .outcome; a name on one side and not
# the other is an attempt nobody can classify.
ATTEMPT_OUTCOMES = ('disrupted', 'oom', 'ephemeral', 'timeout',
                    'rejected', 'unknown', 'failed', 'fetch-fault')

# Collector-only, and here because both processes import THIS module as
# `config` when they run from one flat directory.
SAVE_SUCCESS_LOGS = os.getenv('SAVE_SUCCESS_LOGS', 'true').lower() == 'true'

# --- the run ----------------------------------------------------------------
PARALLELISM = _int('PARALLELISM', 3)
RECONCILE_INTERVAL_SECONDS = _int('LOGGING_INTERVAL_SECONDS', 10)
HTTP_PORT = _int('HTTP_PORT', 8080)

# Delivered by POST /start, replayed from run.json on restart.
STARTING_LEDGER = '0'
LATEST_LEDGER_NUM = '0'
LEDGERS_PER_JOB = '16000'
OVERLAP_LEDGERS = '320'
RANGE_ORDER = 'tip-first'
PROFILE = []                       # sorted [(end, record)]
_SORTED_SECONDS = None             # cache, invalidated whenever PROFILE is set

RANGE_ORDERS = ('tip-first', 'oldest-first', 'longest-first')

# --- the worker pod ---------------------------------------------------------
CORE_IMAGE = os.getenv('CORE_IMAGE', 'stellar/stellar-core:latest')
ASAN_OPTIONS = os.getenv('ASAN_OPTIONS', '')
WORKER_SERVICE_ACCOUNT = os.getenv('WORKER_SERVICE_ACCOUNT', '')
WORKER_GRACE_SECONDS = int(os.getenv('WORKER_GRACE_SECONDS', '150'))
# Holds the pod open after SIGTERM so the collector can drain the last of its
# log. Must fit inside the grace period; a preStop the kubelet kills mid-sleep
# buys nothing and logs FailedPreStopHook on every evicted pod.
WORKER_PRESTOP_SLEEP_SECONDS = int(os.getenv('WORKER_PRESTOP_SLEEP_SECONDS', '5'))

STORAGE_MODE = os.getenv('STORAGE_MODE', 'pvc')          # pvc | ephemeral
STORAGE_CLASS = os.getenv('STORAGE_CLASS', '')
STORAGE_SIZE = os.getenv('STORAGE_SIZE', '60Gi')

JOB_TTL_SECONDS = int(os.getenv('JOB_TTL_SECONDS', '3600'))
# 0 disables. A timeout is terminal, so a tight bound trades a certain
# catastrophe against a rounding error: one wedged range holds one slot.
ATTEMPT_DEADLINE_SECONDS = int(os.getenv('ATTEMPT_DEADLINE_SECONDS', '0'))

# --- unpooled / unprofiled sizing -------------------------------------------
# The packing unit for a run with no profile: 4 workers per r8*.2xlarge
# (cpu-bound) or 3 per m8*.2xlarge (memory-bound). The price of not measuring.
REQ_CPU = os.getenv('REQ_CPU', '1800m')
REQ_MEM = os.getenv('REQ_MEM', '9Gi')
REQ_EPHEMERAL = os.getenv('REQ_EPHEMERAL', '')
LIM_EPHEMERAL = os.getenv('LIM_EPHEMERAL', '')

# --- profile-derived sizing (unpooled) --------------------------------------
PROFILE_MARGIN = float(os.getenv('PROFILE_MARGIN', '1.15'))
# Flat, because a multiplicative margin is nothing at small rss: 1.15x of
# 190Mi is 19Mi of slack, and 90 ranges OOMKilled inside 90s on it.
PROFILE_CACHE_HEADROOM = os.getenv('PROFILE_CACHE_HEADROOM', '512Mi')
PROFILE_RUNTIME_MEMORY_INSURANCE = os.getenv('PROFILE_RUNTIME_MEMORY_INSURANCE', '3Gi')
PROFILE_MAX_MEM = os.getenv('PROFILE_MAX_MEM', '32Gi')
# Image, logs, sqlite WAL: none of it scales with the range.
PROFILE_EPHEMERAL_HEADROOM = os.getenv('PROFILE_EPHEMERAL_HEADROOM', '2Gi')
# Disk tracks runtime closely (pearson 0.920 over 3985 ranges).
PROFILE_RUNTIME_EPHEMERAL_INSURANCE = os.getenv('PROFILE_RUNTIME_EPHEMERAL_INSURANCE', '8Gi')
# Above the flat limit on purpose: that limit is what an UNMEASURED range gets.
PROFILE_MAX_EPHEMERAL = os.getenv('PROFILE_MAX_EPHEMERAL', '64Gi')

# --- escalation -------------------------------------------------------------
MEM_BUMP_FACTOR = float(os.getenv('MEM_BUMP_FACTOR', '1.5'))
MEM_ESCALATION_CAP = os.getenv('MAX_MEM', '48Gi')
EPH_BUMP_FACTOR = float(os.getenv('EPH_BUMP_FACTOR', '1.5'))
EPH_ESCALATION_CAP = os.getenv('MAX_EPHEMERAL', '200Gi')

# --- pools ------------------------------------------------------------------
# Empty disables routing entirely; every worker keeps NODE_LABEL_VALUE.
POOL_PREFIX = os.getenv('POOL_PREFIX', '')
POOL_TIERS = os.getenv(
    'POOL_TIERS',
    '0:subdwarf,0.79:dwarf,1.61:subgiant,3.87:giant,8.85:supergiant,'
    '18.38:hypergiant,:supernova')
# Fits once in the tier's on-demand shape, twice in its (one size larger) spot
# shape. Not 50% of either -- see the requirements.
POOL_MEM = os.getenv(
    'POOL_MEM',
    'subdwarf:1280Mi,dwarf:1280Mi,subgiant:2816Mi,giant:6656Mi,'
    'supergiant:14336Mi,hypergiant:29696Mi,supernova:60416Mi,'
    'protostar:29696Mi,nebula:9216Mi')
POOL_CPU = os.getenv(
    'POOL_CPU',
    'subdwarf:0.85,dwarf:0.85,subgiant:1.85,giant:1.85,supergiant:1.85,'
    'hypergiant:1.85,supernova:3.80,protostar:1.85,nebula:1.80')
POOL_BLOCK_RUNGS = os.getenv('POOL_BLOCK_RUNGS', 'dwarf->subgiant,hypergiant->supernova')
POOL_UNPROFILED = os.getenv('POOL_UNPROFILED', 'protostar')
POOL_NO_PROFILE = os.getenv('POOL_NO_PROFILE', 'nebula')

# --- placement --------------------------------------------------------------
NODE_LABEL_KEY = os.getenv('NODE_LABEL_KEY', '')
NODE_LABEL_VALUE = os.getenv('NODE_LABEL_VALUE', '')
REQUIRE_NODE_LABELS = os.getenv('REQUIRE_NODE_LABELS', '')
AVOID_NODE_LABEL_KEY = os.getenv('AVOID_NODE_LABEL_KEY', '')
AVOID_NODE_LABEL_VALUE = os.getenv('AVOID_NODE_LABEL_VALUE', '')
TOLERATE_TAINT = os.getenv('TOLERATE_TAINT', '')

# --- retry budgets ----------------------------------------------------------
# The whole retry policy. A cause that is NOT here is condemned the first time
# it happens. Every budget is spent by its own cause: one shared attempt index
# let evictions drain the OOM budget to zero.
ATTEMPT_BUDGETS = {
    'disrupted': int(os.getenv('MAX_DISRUPTION_ATTEMPTS', '100')),
    'rejected': int(os.getenv('MAX_REJECTED_ATTEMPTS', '100')),
    'fetch-fault': int(os.getenv('MAX_FETCH_FAULT_ATTEMPTS', '20')),
    'oom': int(os.getenv('MAX_OOM_ATTEMPTS', '5')),
    'ephemeral': int(os.getenv('MAX_EPHEMERAL_ATTEMPTS', '4')),
    'unknown': int(os.getenv('MAX_UNKNOWN_ATTEMPTS', '2')),
}

# --- liveness ---------------------------------------------------------------
LIVENESS_MAX_CONCURRENCY = int(os.getenv('LIVENESS_MAX_CONCURRENCY', '64'))
LIVENESS_PROBE_TIMEOUT_SECONDS = float(os.getenv('LIVENESS_PROBE_TIMEOUT_SECONDS', '2'))
# Under the reconcile interval on purpose: the pass awaits this sweep, so a
# deadline above the interval lets an unreachable fleet stretch dispatch and
# reaping behind a probe that only feeds a dashboard.
LIVENESS_SWEEP_SECONDS = float(os.getenv('LIVENESS_SWEEP_SECONDS', '5'))

# Sized for the dispatch burst rather than a steady rate: a wave head creates
# ~1024 Jobs and PVCs at once.
APISERVER_CONCURRENCY = int(os.getenv('APISERVER_CONCURRENCY', '64'))


def label_pairs(raw):
    """[(key, value)] from "k:v,k:v". A key with no value is dropped -- it would
    require the label be exactly "", which no node carries, and the pod sits
    Pending in a way that reads as slow provisioning."""
    out = []
    for item in (raw or '').split(','):
        key, _, value = item.strip().partition(':')
        if key and value:
            out.append((key, value))
    return out


def set_profile(profile):
    global PROFILE, _SORTED_SECONDS
    PROFILE = profile
    _SORTED_SECONDS = None


def validate():
    """Coerce and re-bind everything a run depends on. Raises ValueError.

    Called from /start, which is the first moment the configuration is
    complete, so a misconfigured run is answered with a 400 rather than
    crash-looping a pod the driver can only time out on.
    """
    global STARTING_LEDGER, LATEST_LEDGER_NUM, LEDGERS_PER_JOB, OVERLAP_LEDGERS

    def positive(name, value, floor=1):
        try:
            n = int(value)
        except (TypeError, ValueError):
            raise ValueError(f"{name} is not an integer: {value!r}")
        if n < floor:
            raise ValueError(f"{name} must be >= {floor}, got {n}")
        return n

    STARTING_LEDGER = positive('STARTING_LEDGER', STARTING_LEDGER, floor=0)
    LATEST_LEDGER_NUM = positive('LATEST_LEDGER_NUM', LATEST_LEDGER_NUM)
    LEDGERS_PER_JOB = positive('LEDGERS_PER_JOB', LEDGERS_PER_JOB)
    OVERLAP_LEDGERS = positive('OVERLAP_LEDGERS', OVERLAP_LEDGERS, floor=0)

    if LATEST_LEDGER_NUM < STARTING_LEDGER:
        raise ValueError(f"LATEST_LEDGER_NUM {LATEST_LEDGER_NUM} is below "
                         f"STARTING_LEDGER {STARTING_LEDGER}")
    if RANGE_ORDER not in RANGE_ORDERS:
        raise ValueError(f"RANGE_ORDER must be one of {RANGE_ORDERS}, "
                         f"got {RANGE_ORDER!r}")
    if STORAGE_MODE not in ('pvc', 'ephemeral'):
        raise ValueError(f"STORAGE_MODE must be pvc or ephemeral, "
                         f"got {STORAGE_MODE!r}")
    if RANGE_ORDER == 'longest-first' and not PROFILE:
        # With no profile every range ties, the sort is a no-op, and dispatch
        # silently falls back to tip-first while the operator believes
        # otherwise.
        raise ValueError(
            "RANGE_ORDER=longest-first requires a profile: it orders ranges by "
            "measured seconds. POST a profile, or set another order.")
