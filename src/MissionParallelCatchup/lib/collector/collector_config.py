"""Settings for the log-collector sidecar, read from the environment once.

Every value the chart can tune lives here rather than beside the code that reads
it, so log_collector.py opens on its own logic. Named collector_config, not
config: runtime flattens lib/ into a single /app, so a second config.py would
silently replace the shared one.
"""

import os

# The worker container whose log is collected. Sidecars share the pod, so this
# is what keeps a peak or a log line from being attributed to the wrong one.
CONTAINER = os.getenv('WORKER_CONTAINER', 'stellar-core')
# Seconds between sweeps of the run's pod list, which is what discovers new pods
# and notices ones that went terminal.
POLL_SECONDS = float(os.getenv('COLLECTOR_POLL_SECONDS', 5))
# Poll cycles a stream gets to finalize itself after its pod leaves the pod list
# before it is cancelled outright. One cycle is usually enough; the margin is for
# a stream still finalizing: writing its .metrics and closing its archive.
VANISHED_GRACE_CYCLES = int(os.getenv('COLLECTOR_VANISHED_GRACE_CYCLES', 3))
# Peak memory comes from kubelet's /stats/summary (rssBytes, workingSetBytes),
# already fetched for ephemeral storage: ~10s cAdvisor housekeeping against a 30s
# scrape, and no dependence on Prometheus being up, reachable, or still retaining
# the window -- failures the old _promql helper all swallowed into "no peak".
# Peaks are held per pod and flushed on this much growth, so a restart loses at
# most that fraction of a range's high-water. cpu is not sampled: the request is
# fixed at REQ_CPU, so a measured value has nothing to size.
PEAK_FLUSH_RATIO = float(os.getenv('PEAK_FLUSH_RATIO', 1.05))
# Seconds between polls of one pod's log. Latency here is archive lag, not
# anything a decision waits on; 4096 pods at 10s is ~90 concurrent polls.
LOG_POLL_SECONDS = float(os.getenv('LOG_POLL_SECONDS', 10))
# Concurrent in-flight log polls, across all pods. This is the whole point of
# polling: it is independent of how many pods exist, where follow=true needed one
# held connection per pod forever.
MAX_CONCURRENT_POLLS = int(os.getenv('MAX_CONCURRENT_POLLS', 96))
# Most one poll may read before it stops, bounding a single response; the next
# poll picks up from the timestamp this one reached. Also the backstop against a
# blob that never terminates -- a progress meter emitting only carriage returns
# would otherwise grow the buffer until the sidecar OOMs.
MAX_POLL_CHARS = int(os.getenv('MAX_POLL_CHARS', 8388608))
# Longest a follow stream will hang on to a doomed pod. Spot gives 120s; past
# roughly double that the notice was withdrawn and the stream would otherwise be
# held for the life of the range. 0 disables the follow path entirely, which is
# only safe when prestopSleepSeconds is holding the pod open instead.
DOOMED_FOLLOW_SECONDS = float(os.getenv('DOOMED_FOLLOW_SECONDS', 300))
# Follows get their OWN budget rather than sharing the poll slots: a follow holds
# its slot for the whole drain, so a reclaim condemning more pods than there are
# slots would consume all of them and turn a partial node loss into a run-wide
# blackout. With a separate budget the worst case is that some condemned pods
# fall back to polling, which captured txApply on its own with no follow at all.
# Sized above any plausible simultaneous disruption -- a whole-AZ reclaim is not
# bounded by Karpenter's disruption budget -- and 256 x the old always-on
# design's 0.69 MiB per stream is 177 MiB against a 2048 MiB limit.
MAX_DOOMED_FOLLOWS = int(os.getenv('MAX_DOOMED_FOLLOWS', 256))
# Poll interval for a condemned pod, replacing LOG_POLL_SECONDS while it is
# doomed. preStop delays SIGTERM but leaves the gap between the medida block and
# the pod object being deleted at ~9s, which a blind 10s poll straddles -- it
# did, losing txApply even with a 60s preStop; polling that window every second
# cannot miss it. Costs no held connections, and sinceTime's 1s granularity makes
# anything below 1s a re-read of the same second.
DOOMED_POLL_SECONDS = float(os.getenv('DOOMED_POLL_SECONDS', 1))
# How long each watch connection lives before the apiserver closes it and we
# reconnect, bounded so a silently-dead stream self-heals; the reconnect resumes
# from the last resourceVersion, so nothing is missed across it. 0 disables the
# watch and leaves detection to the pod list.
WATCH_TIMEOUT_SECONDS = int(os.getenv('WATCH_TIMEOUT_SECONDS', 600))
# Pause before re-opening a watch that failed. Only covers hard errors: a clean
# timeout reconnects immediately.
WATCH_RETRY_SECONDS = float(os.getenv('WATCH_RETRY_SECONDS', 1))
# Failed polls tolerated after a pod goes terminal before we stop asking. Its log
# is not coming back and spinning holds a task and a poll slot for the rest of
# the run, but a couple of retries still absorb the transient 500s that arrive in
# bursts at ramp.
TERMINAL_POLL_ATTEMPTS = int(os.getenv('TERMINAL_POLL_ATTEMPTS', 3))

# Fields that only ever grow. write_metrics maxes these instead of overwriting,
# so a restarted poller starting its high-water at zero cannot lower one.
PEAK_KEYS = ('peakAnonBytes', 'peakWorkingSetBytes', 'peakEphemeralBytes')
# Phases whose log endpoint can actually answer. Pending has no container yet and
# Unknown means the node stopped reporting; the terminal phases are kept because
# that is where a pod's final output lives.
POLLABLE_PHASES = ('Running', 'Succeeded', 'Failed')
