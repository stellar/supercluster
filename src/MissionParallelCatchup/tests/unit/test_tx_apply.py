"""Reading 'ledger.transaction.apply' out of stellar-core's medida block.

Two readers of one format the mission does not control: the collector's
streaming scanner, and the monitor's after-the-fact archive/pod reader. Both
are pinned against real captures so a stellar-core change fails here rather
than silently dropping the metric for a whole run.
"""

import gzip
import json
import os

import pytest

import kube
import records
import medida
import attempts
import job_monitor as jm
import log_collector as lc


# stellar-core 27.1.1 catchup pod, --metric 'ledger.transaction.apply'. Kept
# whole: `sum` is 10 lines below the header against a 15-line scan window.
MEDIDA_BLOCK = """2026-07-28T18:39:49.350 GAJSL [default INFO] metric 'ledger.transaction.apply':
2026-07-28T18:39:49.350 GAJSL [default INFO]            count = 20
2026-07-28T18:39:49.350 GAJSL [default INFO]        mean rate = 0.22136 calls/s
2026-07-28T18:39:49.350 GAJSL [default INFO]    1-minute rate = 0.113149 calls/s
2026-07-28T18:39:49.350 GAJSL [default INFO]    5-minute rate = 0.175948 calls/s
2026-07-28T18:39:49.350 GAJSL [default INFO]   15-minute rate = 0.191421 calls/s
2026-07-28T18:39:49.350 GAJSL [default INFO]              min = 0.295417ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              max = 0.639873ms
2026-07-28T18:39:49.350 GAJSL [default INFO]             mean = 0.417143ms
2026-07-28T18:39:49.350 GAJSL [default INFO]           stddev = 0.108677ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              sum = 8.34285ms
2026-07-28T18:39:49.350 GAJSL [default INFO]           median = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              75% = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              95% = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              98% = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              99% = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]            99.9% = 0ms"""

TX_APPLY_SECONDS = 0.00834285

# Real block from range-40010367-a1 on ssc-test. medida switches to scientific
# notation past 1e6 ms, which is every range with a real transaction load.
MEDIDA_BIG = """2026-07-29T20:11:16.931 GAJSL [default INFO] metric 'ledger.transaction.apply':
2026-07-29T20:11:16.931 GAJSL [default INFO]            count = 3231886
2026-07-29T20:11:16.931 GAJSL [default INFO]        mean rate = 812.4 calls/s
2026-07-29T20:11:16.931 GAJSL [default INFO]    1-minute rate = 790.1 calls/s
2026-07-29T20:11:16.931 GAJSL [default INFO]    5-minute rate = 801.3 calls/s
2026-07-29T20:11:16.931 GAJSL [default INFO]   15-minute rate = 799.0 calls/s
2026-07-29T20:11:16.931 GAJSL [default INFO]              min = 0.101ms
2026-07-29T20:11:16.931 GAJSL [default INFO]              max = 41.2ms
2026-07-29T20:11:16.931 GAJSL [default INFO]             mean = 0.404ms
2026-07-29T20:11:16.931 GAJSL [default INFO]           stddev = 0.612ms
2026-07-29T20:11:16.931 GAJSL [default INFO]              sum = 1.30722e+06ms"""

BIG_SECONDS = 1307.22


def scan(text):
    s = lc.TxApplyScanner()
    for line in text.splitlines():
        s.feed(line)
    return s


# --- the streaming scanner ----------------------------------------------------

@pytest.mark.parametrize('block,want', [(MEDIDA_BLOCK, TX_APPLY_SECONDS),
                                        (MEDIDA_BIG, BIG_SECONDS)])
def test_the_scanner_reads_the_sum_out_of_the_block(block, want):
    # Scientific notation was a silent 25% loss -- 91-99% of everything above
    # ledger 35M -- because the old regex matched "1.30722" then required "ms"
    # and found "e+06ms". The metric block was in the archive the whole time.
    assert scan(block).seconds == pytest.approx(want)


def test_scanner_resumes_a_block_split_across_a_reconnect():
    # One scanner spans the poller's reconnect loop, so a drop mid-block must
    # not lose the header already seen.
    head, tail = MEDIDA_BLOCK.splitlines()[:4], MEDIDA_BLOCK.splitlines()[4:]
    s = lc.TxApplyScanner()
    for line in head:
        s.feed(line)
    assert s.seconds is None
    for line in tail:
        s.feed(line)
    assert s.seconds == pytest.approx(TX_APPLY_SECONDS)


def test_scanner_ignores_sum_from_another_metric():
    s = scan("metric 'ledger.ledger.close':\n              sum = 999999.0ms")
    assert s.seconds is None


def test_scanner_gives_up_past_its_window_of_STATISTICS():
    # The window bounds how many medida statistics may sit between the header
    # and the sum, so a release that adds percentiles is caught here rather than
    # silently dropping tx_apply.
    s = lc.TxApplyScanner()
    s.feed("metric 'ledger.transaction.apply':")
    for i in range(lc.TxApplyScanner.WINDOW + 5):
        s.feed(f"              {i}% = 1.5ms")
    s.feed("              sum = 12.5555ms")
    assert s.seconds is None


def test_interleaved_output_does_not_spend_the_window():
    # Measured on ssc-test 2026-08-04: a /info liveness response landed inside
    # the block and pushed `sum` 91 lines below the header. Charging those lines
    # made the scanner give up 76 lines short while the value sat in the archive
    # -- one leg in 233, and job_monitor's re-read used the same span so its
    # recovery path missed it too.
    s = lc.TxApplyScanner()
    s.feed("metric 'ledger.transaction.apply':")
    s.feed("            count = 7641690")
    for line in ('{', '   "info" : {', '      "build" : "stellar-core 27.1.1",',
                 '      "ledger" : {', '         "age" : 109870542,',
                 '         "baseFee" : 100,', '         "bucketlist" : [',
                 '            {', '               "curr" : "2a2cfe82",',
                 '               "snap" : "c12100ab"', '            },') * 8:
        s.feed(line)
    s.feed("              sum = 1.9501e+06ms")
    assert s.seconds == pytest.approx(1950.1)


def test_a_block_whose_sum_never_arrives_cannot_claim_a_later_one():
    # Without a hard bound, skipping non-statistic lines would leave the scanner
    # armed forever and let it read some other timer's sum as tx_apply.
    s = lc.TxApplyScanner()
    s.feed("metric 'ledger.transaction.apply':")
    for _ in range(lc.TxApplyScanner.HARD_WINDOW + 10):
        s.feed('   "noise" : 1,')
    s.feed("              sum = 12.5555ms")
    assert s.seconds is None


def test_a_different_metric_block_ends_the_search():
    s = lc.TxApplyScanner()
    s.feed("metric 'ledger.transaction.apply':")
    s.feed("metric 'ledger.close':")
    s.feed("              sum = 12.5555ms")
    assert s.seconds is None, "that sum belongs to ledger.close"


def test_rate_and_mean_lines_are_not_read_as_sum():
    for line in MEDIDA_BLOCK.splitlines():
        if 'rate =' in line or 'mean =' in line:
            assert medida.SUM_RE.search(line) is None


def test_sum_stays_inside_the_scan_window():
    lines = MEDIDA_BLOCK.splitlines()
    header = next(i for i, l in enumerate(lines) if 'ledger.transaction.apply' in l)
    offset = next(i for i, l in enumerate(lines) if medida.SUM_RE.search(l)) - header
    assert offset == 10, f"medida layout moved: sum is now {offset} lines below the header"
    assert offset <= lc.TxApplyScanner.WINDOW


def test_resumed_is_read_from_the_workers_own_line():
    # "RESUME DECLINED" must not count as a resume -- it means the opposite, and
    # the colon in RESUME_MARK is what separates the two.
    s = lc.TxApplyScanner()
    s.feed("RESUME DECLINED: k last close was 'none'; bucket phase incomplete, starting fresh")
    assert s.resumed is False, "a declined resume was read as a resume"
    s.feed("RESUME: k reached ledger 31005951, replay had started; skipping new-db")
    assert s.resumed is True


def test_resumed_is_bookkeeping_and_never_becomes_a_measurement():
    # peaks_for_range needs it to tell a resumed tail from a complete pass; the
    # profile must not see it as an axis.
    assert 'resumed' not in attempts.PEAK_FIELDS


# --- the monitor's own reader -------------------------------------------------


def test_tx_apply_comes_from_the_collectors_record_and_nowhere_else(logdir, monkeypatch):
    """The monitor no longer parses stellar-core output for this at all.

    An archive sitting beside the record is not a second source: the collector
    re-reads it with its own scanner at finalization, so a reader here would
    repeat that work over the same bytes and could not disagree.
    """
    class ExplodingPodLog:
        def read_namespaced_pod_log(self, *_a, **_kw):
            raise AssertionError("the monitor must not read pod logs for txApply")
    monkeypatch.setattr(kube, 'core_v1', ExplodingPodLog())

    _metrics(4000, 1, {'txApplySeconds': 99.0})
    assert attempts._tx_apply_for_attempt(4000, 1) == 99.0

    os.remove(records.metrics_path(4000, 1))
    with gzip.open(records.log_path(4000, 1), 'wt') as fh:
        fh.write(MEDIDA_BIG)
    assert attempts._tx_apply_for_attempt(4000, 1) is None, \
        "an archive is the collector's to parse, not the monitor's"


def test_tx_apply_survives_a_reaped_pod(logdir):
    # The record outlives the pod, and is now the only thing the monitor reads.
    _metrics(4000, 1, {'txApplySeconds': 12.5})
    assert attempts.tx_apply_for_range(4000, 1) == 12.5


def test_a_range_with_no_measurement_anywhere_reports_nothing(logdir):
    assert attempts._tx_apply_for_attempt(4000, 1) is None
    assert attempts.tx_apply_for_range(4000, 1) is None


def test_a_corrupt_archive_costs_this_range_its_metric_never_the_pass(logdir):
    # EOFError from a truncated gzip member is not an OSError, so it used to
    # escape the per-range work and abort the whole reconcile: no recording, no
    # reap, no dispatch for any of ~4000 ranges, for as long as the torn bytes
    # sat there.
    with open(records.log_path(4000, 1), 'wb') as fh:
        fh.write(gzip.compress(MEDIDA_BLOCK.encode())[:40])
    assert attempts._tx_apply_for_attempt(4000, 1) is None


# --- summing a resumed chain --------------------------------------------------

def _metrics(end, attempt, values):
    """Write one attempt's .metrics, the way the collector leaves it."""
    with open(records.metrics_path(end, attempt), 'w') as fh:
        json.dump(values, fh)


def test_tx_apply_sums_the_whole_resumed_chain(logdir):
    # medida's total is per-process, so a pod that resumes at LCL+1 reports only
    # the transactions it replayed -- the tail, not the range.
    _metrics(4000, 1, {'txApplySeconds': 10.0})
    _metrics(4000, 2, {'txApplySeconds': 5.0, 'resumed': True})
    assert attempts.tx_apply_for_range(4000, 2) == 15.0


def test_a_fresh_start_drops_the_earlier_legs_from_the_total(logdir):
    # No RESUME line means new-db ran and this attempt redid the whole range;
    # adding the interrupted attempt's figure would double-count the same work.
    _metrics(4000, 1, {'txApplySeconds': 10.0})
    _metrics(4000, 2, {'txApplySeconds': 5.0})
    assert attempts.tx_apply_for_range(4000, 2) == 5.0


def test_a_missing_predecessor_leg_makes_the_chain_total_absent(logdir):
    # a1 has no record at all; a2 resumed from it and has none either. The sum
    # of what survived is a lower bound, not the range's total.
    _metrics(4000, 2, {'resumed': True, 'txApplySeconds': 3.0})
    assert attempts.tx_apply_for_range(4000, 2) is None, \
        "a chain missing a leg must report nothing, not the legs it has"
