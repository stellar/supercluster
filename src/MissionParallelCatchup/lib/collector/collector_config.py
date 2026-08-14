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
# Concurrent in-flight log polls, across all pods. This is the whole point of
# polling: it is independent of how many pods exist, where follow=true needed one
# held connection per pod forever.
MAX_CONCURRENT_POLLS = int(os.getenv('MAX_CONCURRENT_POLLS', 96))
# Most one poll may read before it stops, bounding a single response; the next
# poll picks up from the timestamp this one reached. Also the backstop against a
# blob that never terminates -- a progress meter emitting only carriage returns
# would otherwise grow the buffer until the sidecar OOMs.
MAX_POLL_CHARS = int(os.getenv('MAX_POLL_CHARS', 8388608))
# Concurrent kubelet sweeps. One request per node, and a dead node costs the
# connect timeout, so this is small next to the log-read budget.
MAX_CONCURRENT_SAMPLES = int(os.getenv('MAX_CONCURRENT_SAMPLES', 16))
# Longest a single log read may stall before it is abandoned. A cycle gathers
# every pod, so one wedged read would hold up the whole pass -- where the old
# task-per-pod design only stalled that pod.
READ_TIMEOUT_SECONDS = float(os.getenv('READ_TIMEOUT_SECONDS', 60))
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
# How long each watch connection lives before the apiserver closes it and we
# reconnect, bounded so a silently-dead stream self-heals; the reconnect resumes
# from the last resourceVersion, so nothing is missed across it. 0 disables the
# watch and leaves detection to the pod list.
WATCH_TIMEOUT_SECONDS = int(os.getenv('WATCH_TIMEOUT_SECONDS', 600))
# Pause before re-opening a watch that failed. Only covers hard errors: a clean
# timeout reconnects immediately.
WATCH_RETRY_SECONDS = float(os.getenv('WATCH_RETRY_SECONDS', 1))
# How far back a terminal read reaches past the resume point, so a medida block
# split across two reads is whole in one of them. The archive dedups the
# overlap, so this only widens what the scan sees.
TERMINAL_REREAD_SECONDS = int(os.getenv('TERMINAL_REREAD_SECONDS', 30))
