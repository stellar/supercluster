"""Reading the tx-apply total out of a worker's log.

The collector scans the live stream as it goes past and re-reads its own archive
at finalization when the stream missed the block -- stellar-core prints it once,
just before exit, so a stream that ends a beat early has no total. Scanning here
rather than in job_monitor is what makes the metric independent of pod lifetime:
by the time the Job is seen to succeed the node may be reaped, and with
saveSuccessLogs=false the archive is gone too.
"""

import gzip
import logging
import zlib

import medida
import records

logger = logging.getLogger('log_collector')

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
    WINDOW = medida.WINDOW
    HARD_WINDOW = medida.HARD_WINDOW

    # Printed by RESUME_SCRIPT before stellar-core starts. Its counterpart,
    # "RESUME DECLINED", means new-db ran and this attempt did the whole range,
    # so the colon is load-bearing -- it is what separates the two.
    RESUME_MARK = 'RESUME: '
    RESUME_DECLINED_MARK = 'RESUME DECLINED:'

    def __init__(self, recreated=False):
        self.seconds = None
        self.resumed = False
        self.resume_decided = False
        # A new poller starting from durable .state missed every earlier line.
        # Finalization must recover scanner-only facts from the archive.
        self.recreated = recreated
        self._left = 0
        self._span = 0

    def feed(self, line):
        if self.RESUME_MARK in line:
            self.resumed = True
            self.resume_decided = True
        elif self.RESUME_DECLINED_MARK in line:
            self.resume_decided = True
        if TX_METRIC in line:
            self._left = self.WINDOW
            self._span = self.HARD_WINDOW
            return
        if self._left <= 0:
            return
        m = medida.SUM_RE.search(line)
        if m:
            self.seconds = float(m.group(1)) / 1000.0
            self._left = 0
            return
        self._span -= 1
        if self._span <= 0 or medida.ANY_METRIC.search(line):
            # Ran out of rope, or another timer's block started -- either way our
            # sum is not coming.
            self._left = 0
            return
        if medida.METRIC_LINE.search(line):
            # Only medida's own statistics count. Interleaved output from another
            # thread is noise between us and the sum, not evidence we have passed
            # it.
            self._left -= 1



def scan_archive(end, attempt, need_tx=False):
    """Recover scanner state from complete gzip members already on disk."""
    path = records.log_path(end, attempt)
    scanner = TxApplyScanner()
    try:
        with gzip.open(path, 'rt', errors='replace') as fh:
            for line in fh:
                scanner.feed(line)
                # The resume decision is at process startup. Avoid decompressing
                # a multi-gigabyte worker log when that is all the caller needs.
                if scanner.resume_decided and not need_tx:
                    break
    except FileNotFoundError:
        return scanner
    except (EOFError, gzip.BadGzipFile, zlib.error) as e:
        # Keep facts found in complete prefix members. A torn final member cannot
        # invalidate an earlier RESUME line or complete medida block.
        logger.warning("could only partially recover scanner state from %s: %s", path, e)
    except OSError as e:
        logger.warning("could not open scanner archive %s: %s", path, e)
    return scanner


