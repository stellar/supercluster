"""Configuration and run state for the parallel catchup job monitor.

Read through the module, never copied out of it:

    import config
    ... config.REQ_CPU ...

`from config import REQ_CPU` binds a COPY. A test's monkeypatch and the startup
assignment of config.PROFILE both rebind the attribute on this module, and a
copy taken at import time never sees either -- silently, with the test passing
against the default. A module object is a singleton, so reading through it is
what makes those visible everywhere.
"""
import os

# =============================================================================
# 1. stellar-core workload
# =============================================================================
CORE_IMAGE = os.getenv('CORE_IMAGE')

ASAN_OPTIONS = os.getenv('ASAN_OPTIONS', '')

# Which ledger ranges to run. These are pure inputs to the range generator:
# dispatch recomputes the whole list every reconcile, so a restart must
# reproduce it exactly.


# Both generators emit tip-first, which front-loads the most expensive ranges:
# the bucket set only grows with ledger position. 'oldest-first' reverses that,
# so a profiling run measures the cheap early ranges before it can be
# interrupted, and the expensive tip ranges last.
RANGE_ORDER = 'tip-first'  # from /start: tip-first | oldest-first | longest-first

VALID_RANGE_ORDERS = ('tip-first', 'oldest-first', 'longest-first')

STARTING_LEDGER = 0  # from /start

LATEST_LEDGER_NUM = 0  # from /start

LEDGERS_PER_JOB = 16000  # from /start

OVERLAP_LEDGERS = 320  # from /start


# =============================================================================
# 2. Kubernetes objects this monitor creates
# =============================================================================
NAMESPACE = os.getenv('NAMESPACE', 'default')

RUN_NAME = os.getenv('RUN_NAME', 'parallel-catchup')


LABEL_RUN = 'catchup.stellar.org/run'

LABEL_RANGE = 'catchup.stellar.org/range-end'

LABEL_ATTEMPT = 'catchup.stellar.org/attempt'

# Workers need IRSA to read the S3 history mirror. Without it they silently fall
# back to the public archive, which throttles at 1024 and kills the run with
# curl 22 -> catchup exit 3. The name matches the old StatefulSet's so existing
# IRSA trust policies keep matching.
WORKER_SERVICE_ACCOUNT = os.getenv('WORKER_SERVICE_ACCOUNT', '')

# Pod resources. Requests only: workers are given no cpu limit and no memory
# limit at all.
#
# CPU because a limit only throttles a pod that could otherwise use idle cores,
# and throttling changes what the range measures -- less cpu means less download
# concurrency means a lower peak, so a throttled attempt records a figure an
# unthrottled one cannot reproduce.
#
# Memory because a limit is a hard cap on anon PLUS page cache, and sizing it
# per-range from a profile got it wrong in the one direction that has no alarm
# on it. Measured 2026-07-31, range 39210943: sized at 1729Mi from a neighbour,
# genuinely needed 1620Mi of anon, which left ~110Mi for cache. It never OOMed
# -- it thrashed. 544k major page faults, 0.22 cores used on a node it had
# entirely to itself, 0.95 ledgers/s against a neighbour norm of 3.3, and it
# held 1092 idle slots open for three hours at the end of the run.
#
# Without a limit the request still does the real work: it places the pod and
# it sets eviction order under node pressure. What goes away is the cliff.
REQ_CPU = os.getenv('REQ_CPU', '1250m')

REQ_MEM = os.getenv('REQ_MEM', '9Gi')

# Range profile from an earlier run: tightens per-range requests so more
# workers fit per node. Requests only -- limits stay as configured, so the
# failure semantics and the OOM/disk escalation ladders are unchanged.
PROFILE_PATH = os.getenv('PROFILE_PATH', '')
# The run document /start delivers, kept so a restart resumes the same run.
RUN_PATH = ''

PROFILE_MARGIN = float(os.getenv('PROFILE_MARGIN', 1.15))

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

# Extra allowance scaled by the range's measured runtime. Long ranges keep more
# page cache and allocator slack live at once; 0 disables the allowance.
PROFILE_RUNTIME_MEMORY_INSURANCE = os.getenv('PROFILE_RUNTIME_MEMORY_INSURANCE', '3Gi')

# Ephemeral-storage gets the same two allowances as memory, for the same
# reasons. Measured on the 2026-08-01 on-demand run: peak 37.76Gi against a
# flat 40Gi limit -- 6% of headroom on a path that has never once fired in a
# real run, so a range 6% worse than the worst seen would be evicted 137 with
# no diagnostic pointing at disk.
#
# Flat allowance added to every range's measured peak. Covers the container
# image, logs and the sqlite WAL, none of which scale with the range.
PROFILE_EPHEMERAL_HEADROOM = os.getenv('PROFILE_EPHEMERAL_HEADROOM', '2Gi')

# Runtime-weighted allowance on top. Disk tracks runtime closely (pearson 0.920
# across 3985 ranges: runtime decile 0 uses 0.1Gi, decile 9 uses 24.7Gi), so
# the ranges that need the margin are exactly the ranges this gives it to.
PROFILE_RUNTIME_EPHEMERAL_INSURANCE = os.getenv('PROFILE_RUNTIME_EPHEMERAL_INSURANCE', '8Gi')

# Ceiling for profile-derived disk. Deliberately ABOVE LIM_EPHEMERAL: that flat
# limit is what an UNMEASURED range gets, and capping a measured range at it
# would throw away the measurement -- the worst observed range wants 43Gi after
# margin alone.
PROFILE_MAX_EPHEMERAL = os.getenv('PROFILE_MAX_EPHEMERAL', '64Gi')

REQ_EPHEMERAL = os.getenv('REQ_EPHEMERAL', '')

LIM_EPHEMERAL = os.getenv('LIM_EPHEMERAL', '')

# Placement. The taint toleration is emitted as {key, effect} with no value:
# the default Equal operator does not match "" against "true".
NODE_LABEL_KEY = os.getenv('NODE_LABEL_KEY', '')

NODE_LABEL_VALUE = os.getenv('NODE_LABEL_VALUE', '')

# Further labels a node must carry, "key:value" comma separated, ANDed with the
# one above. Unlike that one these are literal -- the pair above is pool-routed,
# its value replaced per range with <prefix>-<tier>.
#
# This is where a run pins itself to one capacity of a tier. Both capacities
# carry the same tier label value, so nothing else separates them, and the
# pairing matters: ephemeral has no resume, so a reclaim costs the whole range.
# A plain label rather than karpenter.sh/capacity-type, because the pools
# publish their own and the monitor has no business knowing who provisioned the
# node.
REQUIRE_NODE_LABELS = os.getenv('REQUIRE_NODE_LABELS', '')


def label_pairs(raw):
    """[(key, value)] from "k:v,k:v". Entries without a value are dropped: a
    key alone would require the label be exactly "", which no node carries, and
    a pod pinned to nothing sits Pending in a way that reads as slow
    provisioning rather than as misconfiguration."""
    out = []
    for item in (raw or '').split(','):
        key, _, value = item.strip().partition(':')
        if key and value:
            out.append((key, value))
    return out

AVOID_NODE_LABEL_KEY = os.getenv('AVOID_NODE_LABEL_KEY', '')

AVOID_NODE_LABEL_VALUE = os.getenv('AVOID_NODE_LABEL_VALUE', '')

TOLERATE_TAINT = os.getenv('TOLERATE_TAINT', '')

# Worker /data. pvc keeps it across pods, so an evicted range resumes at L+1 --
# that is what makes spot viable. ephemeral puts it on the node disk: denser
# packing, no resume, and REQ_EPHEMERAL must be sized to hold the catchup DB.
# One PVC per range, not per concurrency slot: measured on ssc-test, 300 jobs
# with a PVC each cost no more wall-clock than 300 jobs reusing 40.
STORAGE_MODE = os.getenv('STORAGE_MODE', 'pvc')                # pvc | ephemeral

STORAGE_CLASS = os.getenv('STORAGE_CLASS', '')

# 60Gi to match the tier nodes' ephemeral allowance. peakEphemeralBytes tops
# out at 37.8Gi across the whole 2026-08-01 profile, so this covers every
# range measured, with headroom for the tip to keep growing.
STORAGE_SIZE = os.getenv('STORAGE_SIZE', '60Gi')

# Job/pod lifetimes.
# SIGTERM -> SIGKILL budget. stellar-core exits ~7s after SIGTERM (measured), so
# this is slack rather than a target.
WORKER_GRACE_SECONDS = int(os.getenv('GRACE_SECONDS', 100))

# Seconds to stall inside preStop before the container is signalled. 0 disables.
#
# Sized to cover the collector's DETECTION LAG, which is the specific hole it
# fills. The collector notices DisruptionTarget on its pod-list cycle and only
# then drops that pod to 1s polling; if SIGTERM lands inside that blind window
# the poller is still on its lazy LOG_POLL_SECONDS cadence. Measured on
# ssc-test: a 60s preStop with 10s polling and no disruption detection still
# lost txApply, while 1s polling with no preStop at all captured it. So this is
# not what saves the metric -- it is what makes sure the detection has happened
# before the kill.
#
# 20s, not COLLECTOR_POLL_SECONDS. That constant is the SLEEP between cycles,
# not the cycle: each one also lists every pod and sweeps kubelet
# /stats/summary on every node, which at 768 workers over ~250 nodes is
# unmeasured and plausibly another 5-15s. The margin is
# (preStop + pod-object linger) - (detection + one 1s poll), and with the
# linger measured at 7.8s it goes NEGATIVE at a 12s cycle if this is 5s. Above
# the true cycle time the margin plateaus at +6.8s, so overshooting is free
# while undershooting silently loses the metric.
#
# A spot reclaim gives ~120s of notice and does not need this at all; an
# eviction-API kill or a fast drain signals immediately and does.
#
# Do NOT try to SIGTERM the process from inside the hook and hold the pod open
# afterwards: measured, the pod object survived 10.2s that way versus 69s for a
# plain sleep, because a container dies with its PID 1 and the kubelet does not
# defer deleting the object until the hook returns.
#
# Costs nothing on a healthy exit -- preStop does not run when the container
# exits on its own, only when the kubelet is tearing it down. At ~810 evictions
# a run, 5s each is about 1.1 pod-hours.
#
# Must stay comfortably under WORKER_GRACE_SECONDS: the hook and the SIGTERM
# drain share that one budget, and a hook still running when it expires is
# SIGKILLed, which loses exactly the output this exists to save.
WORKER_PRESTOP_SLEEP_SECONDS = int(os.getenv('PRESTOP_SLEEP_SECONDS', 5))

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
#
# Flat, deliberately -- NOT scaled by the range's profiled runtime. That was
# tried and removed. A deadline has to bound a range's WORST case, but a profile
# only offers a neighbour's TYPICAL case, and the two are far apart here:
# runtimes span 190x (p25 771s, max 5.9h), range keys are anchored to the
# network tip so a profile from an earlier run matches ZERO keys exactly and
# every lookup lands on a neighbour, and ~2% of those neighbours are 3-38x
# cheaper than their surroundings. Backtested honestly across that grid offset
# (run4 profile -> r5 actuals, 3983 ranges): a 2x factor falsely kills 134
# ranges, 4x kills 46, 6x kills 21. Flat 12h kills none.
#
# The asymmetry decides it. A false kill loses a range, and a timeout is
# terminal, so it fails the mission. A genuine wedge holds ONE slot out of
# 1092-1500 for 12h -- around 0.1% of a run's capacity. Never trade a certain
# catastrophe against a rounding error.
#
# 12h is a safe bound, not a good detector: it takes half a day to catch
# something provably dead in 4 minutes. The right signal is ledger-close
# progress, not elapsed time -- a wedged core closes zero ledgers while still
# logging, so `.state` (last log line) cannot see it and a new
# lastLedgerCloseAt would. Left undone on purpose; it needs a threshold above
# the initial bucket-apply phase, which legitimately closes nothing for ~20min
# on the longest ranges.
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
# Attempts each failure cause gets before the range is condemned. The whole
# retry policy, in one table.
#
# Every budget is spent by ITS OWN cause: an OOM never consumes the disk budget
# and a spot eviction never consumes either. A cause that is NOT here is
# condemned the first time it happens -- a timeout, a real catchup failure, an
# exit 3 with nothing in its archive, and anything nothing could classify.
#
#   disrupted  the cluster took the pod away mid-run, which proves the range
#              itself was fine. Effectively unlimited: on spot a healthy range
#              is legitimately evicted dozens of times, and 100 is far past any
#              rate a real run has produced while still terminating.
#   rejected   the kubelet refused the pod before any container ran (attachment
#              limits, admission churn). The range never started, so a retry
#              cannot mask anything about it.
#   fetch-fault  an exit 3 whose archive named a failed history fetch. An
#              unreachable mirror is the cluster's problem, not the range's. A
#              plain `failed` has no entry: a real catchup failure, and an exit 3
#              with nothing in its archive, are both condemned on sight.
#   oom        each retry escalates the memory request one rung.
#   ephemeral  each retry escalates the disk limit one rung. Smallest, because
#              an eviction repeats identically until the range gets more disk.
#
# The MAX_* names below exist so the chart can tune each one; ATTEMPT_BUDGETS is
# what the code reads, so tests patch the map rather than the constants.
MAX_DISRUPTION_ATTEMPTS = int(os.getenv('MAX_DISRUPTION_ATTEMPTS', 100))
MAX_REJECTED_ATTEMPTS = int(os.getenv('MAX_REJECTED_ATTEMPTS', 100))
MAX_FETCH_FAULT_ATTEMPTS = int(os.getenv('MAX_FETCH_FAULT_ATTEMPTS', 20))
MAX_OOM_ATTEMPTS = int(os.getenv('MAX_OOM_ATTEMPTS', 5))
MAX_EPHEMERAL_ATTEMPTS = int(os.getenv('MAX_EPHEMERAL_ATTEMPTS', 4))

ATTEMPT_BUDGETS = {
    'disrupted': MAX_DISRUPTION_ATTEMPTS,
    'rejected': MAX_REJECTED_ATTEMPTS,
    'fetch-fault': MAX_FETCH_FAULT_ATTEMPTS,
    'oom': MAX_OOM_ATTEMPTS,
    'ephemeral': MAX_EPHEMERAL_ATTEMPTS,
}

EPH_BUMP_FACTOR = float(os.getenv('EPH_BUMP_FACTOR', 1.5))

EPH_ESCALATION_CAP = os.getenv('EPH_ESCALATION_CAP', '200Gi')

ATTEMPT_OUTCOMES = ('disrupted', 'oom', 'ephemeral', 'timeout',
                    'rejected', 'unknown', 'failed', 'fetch-fault')

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

# Worker responsiveness is cosmetic and sampled independently from reconcile.
# Thirty seconds and three failures restore the old ~90-second down threshold,
# while a five-second request budget gives a busy admin endpoint substantially
# more room than the old one-shot two-second probe.

LIVENESS_PROBE_TIMEOUT_SECONDS = os.getenv('LIVENESS_PROBE_TIMEOUT_SECONDS', '5')


LIVENESS_MAX_CONCURRENCY = os.getenv('LIVENESS_MAX_CONCURRENCY', '32')
# Wall-clock bound on one sweep. The reconcile loop waits for it, so this
# is the most a fleet of unreachable workers can delay dispatch.
LIVENESS_SWEEP_SECONDS = os.getenv('LIVENESS_SWEEP_SECONDS', '15')

# Shared with the log-collector sidecar, which owns writes here: it streams each
# worker's log and records the .outcome verdict while the pod still exists.
LOG_DIR = os.getenv('LOG_DIR', '/logs')

SAVE_SUCCESS_LOGS = os.getenv('SAVE_SUCCESS_LOGS', 'true').lower() == 'true'

# The authoritative copy of the progress record lives on the logs PVC, not in
# the ConfigMap. A ConfigMap is capped at 1 MiB and this record is ~172 bytes
# per completed range, so it dies at ~6100 ranges -- reachable simply by halving
# ledgersPerJob. Measured mid-run on ssc-test: 348KB at 2024 completed ranges,
# which projects to ~65% of the cap at 3982 -- close enough that the next
# slicing change would have hit it. Worse, every completion rewrote the whole
# document through the API server, so a full run meant thousands of
# escalating-size etcd writes.
#
# The ConfigMap is still written, because the mission driver reads it without
# exec'ing into the pod, but it is now a best-effort mirror: if it fails, the
# run carries on from the file.
PROGRESS_FILE = os.path.join(LOG_DIR, 'progress.json')

PROFILE = None

# --- pool tiers -------------------------------------------------------------
#
# A range picks a NODEPOOL by its measured memory, and gets that pool's node to
# itself. This replaces the cpu ladder, which tuned a dimension that turned out
# not to be the binding one.
#
# Why memory and not cpu. Measured 2026-08-03 on one range across four instance
# shapes, isolated, no memory limit:
#
#     2 -> 4 cores   replay +2.8%   bucket-apply 1.37x
#     4 -> 8 cores   replay +1.5%   bucket-apply 1.18x
#     AMD vs Intel   replay  +16%   bucket-apply 1.35x
#
# Replay is ~93% of a job and is flat in core count from 2 upward -- it draws
# ~1.05 cores whatever it is given. So a cpu REQUEST never bought throughput.
# What it bought was neighbours-per-node, and memory is what actually fails: a
# range whose working set does not fit gets OOMKilled, not slowed down.
#
# Cuts are `node_usable / 1.60`, covering the p99 of run-to-run growth in the
# same range's peakAnonBytes (18,073 observations across five profiles: p50 0.97,
# p90 1.28, p99 1.60, max 2.83). Validated the hard way: range 63080767 measured
# 13.75Gi was placed on nodes with 14.1/14.3Gi allocatable -- a 1.03x margin --
# and OOMKilled on BOTH during bucket-apply, before closing a ledger.
# subdwarf's cut is 0 on purpose: nothing can satisfy `gib < 0`, so the tier is
# defined and provisionable but never routed to. Kept rather than deleted so the
# bottom of the ladder is there to experiment with; c8a.medium (1.42Gi
# allocatable) cannot hold a range the profile actually contains.
POOL_TIERS = os.getenv(
    'POOL_TIERS',
    '0:subdwarf,0.79:dwarf,1.61:subgiant,3.87:giant,8.85:supergiant,18.38:hypergiant,:supernova')

# Prepended to the tier name to form the node label value, e.g. catchup-dwarf.
# Empty disables pool routing entirely and every worker keeps the single global
# NODE_LABEL_VALUE, which is exactly today's behaviour.
POOL_PREFIX = os.getenv('POOL_PREFIX', '')

# Where a range goes when the profile has no entry for it (past the profile's
# top, i.e. the newest ledgers) and when there is no profile at all.
POOL_UNPROFILED = os.getenv('POOL_UNPROFILED', 'protostar')

POOL_NO_PROFILE = os.getenv('POOL_NO_PROFILE', 'nebula')

# cpu request per tier. NOT a demand estimate -- a claim token. Isolation is the
# point: freeing a node of its 3 neighbours raised throughput 29-92% while cpu
# draw FELL, so the contended resource is memory bandwidth and shared cache, not
# compute. Kept at or below the SMALLEST node in the tier so the low-weight
# fallback rungs stay schedulable (dwarf can land on a 1-vCPU c8a.medium).
#
# Memory, not cpu, is what actually enforces the isolation -- see _pool_memory.
# Half the node for most tiers. hypergiant and supernova are sized to the
# SMALLEST shape in their pool instead: x8i.large is r8a.xlarge with half the
# cores and the same 32 GiB, x8i.xlarge is r8a.2xlarge with half the cores and
# the same 64 GiB, so preferring them buys identical RAM for half the spot
# quota. A half-the-node 2.00/4.00 claim does not fit an x8i node once the 215m
# of daemonsets is counted, which is why those pools won no nodes at all on
# 2026-08-03. Below half, cpu no longer isolates the pod on the larger fallback
# shapes -- memory does, and it holds because every type within a tier carries
# the same RAM.
POOL_CPU = os.getenv(
    'POOL_CPU',
    'subdwarf:0.85,dwarf:0.85,subgiant:1.85,giant:1.85,supergiant:1.85,hypergiant:1.85,supernova:3.80,protostar:1.85,nebula:1.80')


# Rungs that never run, whatever the vCPU comparison says. Empty by default: with
# the spot pools doubled, promotion lands a range on a bigger SHARED node, and
# sharing is what the rung is really buying. Measured on ssc-test 2026-08-04 on
# one range, two pods to a node: on an 8-vCPU node a co-tenant cost 1.02x per pod
# (r8id.2xlarge, 3.78 and 3.91 lps), on a 4-vCPU node it cost 1.58x. Two pods on
# 8 cores leave 4 each, which the workload does not use; two on 4 cores leave 2,
# which is the floor.
#
# Caveat worth keeping in view: the bump fires on peakWorkingSetBytes, and working
# set does not predict throughput. Same x8i.xlarge box, same range, same time,
# only cgroup memory.max differing: 28 GiB ran 1.83 lps and 56 GiB ran 1.70. So
# this promotes for a reason that is not the reason it helps -- it reaches the
# right nodes via the wrong signal, and will promote ranges that gain nothing.
# Sizing the rung on peakAnonBytes, or widening the tier->instance map directly,
# would target those nodes deliberately.
#
# This is now the ONLY thing standing between a working set and a promotion, so
# a rung that should not be taken has to be named here -- nothing is inferred.
#
# hypergiant->supernova is denied on both capacity types. Its cost rose once the
# x8i pools were removed on 2026-08-04: supernova's only spot shapes are now
# 4xlarges, so the rung moves a range from 8 vCPU to 16 rather than the 8-vCPU
# x8i.2xlarge it used to reach. Simulated over the 2026-08-03 run it saved
# exactly 0 minutes on its own, because the longest job was a supergiant this
# rung cannot reach. It pays only in company -- supergiant->hypergiant alone is
# worth 8 min, this alone 0, the pair 27 -- and that pairing is not on offer
# while its cost is 8->16 vCPU.
#
# dwarf->subgiant is the same doubling at the bottom of the ladder, 2->4 vCPU on
# spot and 1->2 on on-demand.
POOL_BLOCK_RUNGS = os.getenv('POOL_BLOCK_RUNGS', 'dwarf->subgiant,hypergiant->supernova')

# Memory request for a pooled range is the TIER'S CUT, not the range's own
# measurement, and that is deliberate two ways.
#
# It guarantees one pod per node without depending on the cpu token: a tier's
# node is cut*1.60 of usable memory, so two pods asking cut apiece need 2*cut,
# which always exceeds 1.60*cut. The cpu claim cannot do this alone because a
# tier spans node sizes (dwarf reaches a 1-vCPU c8a.medium and a 2-vCPU
# t3a.small), so no single cpu value both schedules on the small one and fills
# the large one.
#
# And the request no longer needs a safety margin. PROFILE_MARGIN, cache
# headroom and runtime insurance all existed to keep a pod under its own LIMIT;
# with no memory limit and the node to itself, a pod may use everything the node
# has. The margin moved into the node size -- which is where it can actually be
# enforced, since the kubelet kills on node pressure, not on request.
# Per-tier memory request: exactly 50% of the tier node's NAMEPLATE capacity.
#
# 50% is what isolates. Two pods asking half the nameplate need the whole node,
# which always exceeds allocatable -- so a second pod can never fit, on every
# tier, without depending on how the kubelet happens to reserve.
#
# Verified against measured nodes rather than assumed: a c8a.medium reports
# 1892Mi capacity, 1449Mi allocatable, and carries 154Mi of daemonsets, leaving
# 1295Mi -- so the 1024Mi request schedules with room, and 2048Mi of two pods
# cannot. The same holds up the ladder.
#
# t3a.micro is absent on purpose: 413Mi allocatable cannot host a pod at all on
# this cluster, so subdwarf shares dwarf's node type and is emptied by its cut.
POOL_MEM = os.getenv(
    'POOL_MEM',
    'subdwarf:1280Mi,dwarf:1280Mi,subgiant:2816Mi,giant:6656Mi,supergiant:14336Mi,hypergiant:29696Mi,supernova:60416Mi,protostar:29696Mi,nebula:9216Mi')

_SORTED_SECONDS = None


# Sized for the dispatch burst rather than a steady LIST rate: ~1024 Jobs + PVCs
# go out at once at the head of a wave.
CONNECTION_POOL = int(os.getenv('CONNECTION_POOL', '64'))

# Left as strings on purpose. Coercing at import made a bad value a boot crash,
# and a process that cannot start cannot report why -- the driver just polled a
# pod that never answered and timed out 600s later with "not reachable".
# validate_config coerces and rebinds these when /start delivers the run, so a
# bad value comes back as a 400 carrying the reason.
