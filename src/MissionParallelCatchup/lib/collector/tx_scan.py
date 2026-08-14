"""Reading the tx-apply total out of a worker's log.

One reader, so the window constants live here rather than in a module shared
with a second one -- the monitor's archive re-read is gone; it takes the value
from the collector's .metrics.

stellar-core prints the block once, just before exit, so the collector scans it
off the read that finds the pod terminal. Scanning there rather than in
job_monitor is what makes the metric independent of pod lifetime: by the time
the Job is seen to succeed the node may be reaped, and with saveSuccessLogs=false
the archive is gone too.
"""

import re

# `sum = <number>ms`, where the number may be in exponent form. The old
# [0-9.]+ pattern matched "1.30722" then demanded "ms" and hit "e+06ms"
# instead, so tx_apply was silently missing for 25% of ranges -- 91-99% of
# everything above ledger 35M, exactly the expensive end. 698 completed ranges
# lost the metric that way in a single run.
SUM_RE = re.compile(r"sum\s*=\s*([0-9.]+(?:[eE][+-]?[0-9]+)?)ms")

# A medida statistic: `<key> = <number>`. Anything else between the block header
# and its sum is another thread's output interleaved into the same log, and must
# not be charged against the search window.
METRIC_LINE = re.compile(r"[\w%.\-]+\s*=\s*[-+0-9.]")

# A different metric's header. Reaching one means the block we armed on has been
# passed, so a later `sum =` belongs to some other timer.
ANY_METRIC = re.compile(r"metric '")

# Statistics lines the sum may sit behind, and the hard line budget regardless.
# Measured 2026-08-04: a /info response pushed `sum` 91 lines down, and charging
# those lines made both readers give up while the value sat in the archive.
WINDOW = 15
HARD_WINDOW = 400

TX_METRIC = "metric 'ledger.transaction.apply'"

class TxApplyScanner:
    """Pull the medida tx-apply total out of the stream as it goes past.

    stellar-core prints this block once, just before exit. Scanning here rather
    than re-reading the log later is what makes the metric independent of pod
    lifetime: by the time job_monitor sees the Job succeed the node may be
    reaped, and with saveSuccessLogs=false the archive is gone too.
    """

    # Shared with job_monitor's archive re-read rather than restated: a
    # divergence would hand the recovery path the same blind spot it exists to
    # cover. A /info liveness response interleaved into the block once put `sum`
    # 91 lines below the header, 76 lines past where both readers gave up.

    # Printed by RESUME_SCRIPT before stellar-core starts. The colon and space
    # are load-bearing: its counterpart "RESUME DECLINED:" means new-db ran and
    # this attempt did the whole range, and must not read as a resume.
    RESUME_MARK = 'RESUME: '

    def __init__(self):
        self.seconds = None
        self.resumed = False
        self._left = 0
        self._span = 0

    def feed(self, line):
        if self.RESUME_MARK in line:
            self.resumed = True
        if TX_METRIC in line:
            self._left = WINDOW
            self._span = HARD_WINDOW
            return
        if self._left <= 0:
            return
        m = SUM_RE.search(line)
        if m:
            self.seconds = float(m.group(1)) / 1000.0
            self._left = 0
            return
        self._span -= 1
        if self._span <= 0 or ANY_METRIC.search(line):
            # Ran out of rope, or another timer's block started -- either way our
            # sum is not coming.
            self._left = 0
            return
        if METRIC_LINE.search(line):
            # Only medida's own statistics count. Interleaved output from another
            # thread is noise between us and the sum, not evidence we have passed
            # it.
            self._left -= 1





