"""stellar-core's medida metric block against the two parsers that read it.

txApply is the only per-range performance number this mission produces, and it
exists in exactly one place: the block stellar-core prints once, just before
exit, because we pass --metric 'ledger.transaction.apply'. Both processes parse
it -- the collector out of the live stream (the only reader guaranteed to see
the bytes, since the pod may be reaped and saveSuccessLogs may be off) and the
monitor out of the archive as a fallback. Two parsers, one format.

The blocks below are whole captures rather than the lines we care about: the
layout IS the contract. `sum` sits ten lines under the header, against a
fifteen-line scan window, so a medida release that adds five percentiles takes
the metric out silently.
"""

import gzip
import re

import pytest

import job_monitor as jm
import log_collector as lc


# stellar-core 27.1.1 catchup pod, --metric 'ledger.transaction.apply'.
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
# notation past 1e6 ms, which is every range with a real transaction load. The
# old [0-9.]+ pattern matched "1.30722", then demanded "ms" and hit "e+06ms"
# instead: 25% of ranges recorded no tx_apply -- 91-99% of everything above
# ledger 35M, exactly the expensive end -- while the block sat in the archive
# the whole time.
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

TX_APPLY_BIG_SECONDS = 1307.22


def scan(block):
    scanner = lc.TxApplyScanner()
    for line in block.splitlines():
        scanner.feed(line)
    return scanner


# --- the layout, which is what the scan window is sized against --------------

def test_the_sum_still_sits_inside_the_scan_window():
    """Ten lines below the header, against a fifteen-line window.

    Five more percentiles in a medida release and the metric disappears with no
    error anywhere. The margin is the thing to watch, so it is reported.
    """
    lines = MEDIDA_BLOCK.splitlines()
    header = next(i for i, l in enumerate(lines) if 'ledger.transaction.apply' in l)
    offset = next(i for i, l in enumerate(lines) if 'sum =' in l) - header
    assert offset == 10, f"medida layout moved: sum is now {offset} lines below the header"
    assert offset <= lc.TxApplyScanner.WINDOW, (
        f"the sum is {offset} lines down and the scanner looks {lc.TxApplyScanner.WINDOW}")


@pytest.mark.parametrize('gap', [1, 10, lc.TxApplyScanner.WINDOW,
                                 lc.TxApplyScanner.WINDOW + 5])
def test_both_readers_reach_exactly_as_far_past_the_header(gap, tmp_path, monkeypatch):
    """The collector scans the stream; the monitor scans the archive.

    Two separate implementations of "find the sum under this header". A reach
    that differed between them would make the metric depend on which reader got
    to it -- and the monitor's read is the one that happens when the collector
    was down for the pod's lifetime.

    Asserted by measuring both, at the boundary and past it, rather than by
    comparing two constants: the monitor is free to stop slicing a window and
    reuse the scanner outright, which is a better implementation of the same
    contract.
    """
    monkeypatch.setattr(jm, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(lc, 'LOG_DIR', str(tmp_path))
    block = ["metric 'ledger.transaction.apply':"]
    block += [f"           filler {i} = 0ms" for i in range(gap - 1)]
    block += ["              sum = 1500.0ms"]

    scanner = lc.TxApplyScanner()
    for line in block:
        scanner.feed(line)

    with gzip.open(jm.log_path('300', 1), 'wt') as fh:
        fh.write("\n".join(block) + "\n")
    from_archive = jm._tx_apply_for_attempt('300', 1)

    assert (scanner.seconds is None) == (from_archive is None), (
        f"at {gap} lines past the header the collector says {scanner.seconds} "
        f"and the monitor says {from_archive}")
    if from_archive is not None:
        assert from_archive == pytest.approx(scanner.seconds)


# --- the number itself --------------------------------------------------------

@pytest.mark.parametrize('block,seconds', [
    (MEDIDA_BLOCK, TX_APPLY_SECONDS),
    (MEDIDA_BIG, TX_APPLY_BIG_SECONDS),
])
def test_both_processes_read_the_same_total_out_of_one_block(block, seconds):
    """The collector's scanner and the monitor's regex must not disagree.

    They are separate implementations of the same read: a stream scanner with a
    window, and a whole-archive search. progress.json takes whichever one landed
    first, so a disagreement is a per-range coin flip.
    """
    assert scan(block).seconds == pytest.approx(seconds)
    m = jm._SUM_RE.search(block)
    assert m, "the monitor's regex does not match this block at all"
    assert float(m.group(1)) / 1000.0 == pytest.approx(seconds)


def test_scientific_notation_is_the_normal_case_not_the_edge_case():
    """Past 1e6 ms, which every range with real transaction load exceeds."""
    assert 'e+06' in MEDIDA_BIG
    assert scan(MEDIDA_BIG).seconds > scan(MEDIDA_BLOCK).seconds


def test_no_other_line_in_the_block_looks_like_the_sum():
    """min, max, mean, stddev and the percentiles are all "<name> = <n>ms".

    A pattern loose enough to take one of them would report a per-transaction
    latency as a whole-range total -- plausible, wrong, and unnoticeable.
    """
    for block in (MEDIDA_BLOCK, MEDIDA_BIG):
        matched = [l for l in block.splitlines() if jm._SUM_RE.search(l)]
        assert len(matched) == 1, f"matched {len(matched)} lines: {matched}"
        assert 'sum =' in matched[0]


def test_a_sum_from_another_metric_is_not_this_metric():
    """stellar-core prints many medida blocks; only one is ours."""
    scanner = lc.TxApplyScanner()
    for line in ["metric 'ledger.ledger.close':", "              sum = 999999.0ms"]:
        scanner.feed(line)
    assert scanner.seconds is None


def test_a_block_split_across_two_polls_still_resolves():
    """One scanner spans the whole poll loop for a pod.

    A poll boundary -- or a reconnect -- landing inside the block must not lose
    the header already seen, or the last four lines of a range's life are read
    with no idea what metric they belong to.
    """
    lines = MEDIDA_BLOCK.splitlines()
    scanner = lc.TxApplyScanner()
    for line in lines[:4]:
        scanner.feed(line)
    assert scanner.seconds is None
    for line in lines[4:]:
        scanner.feed(line)
    assert scanner.seconds == pytest.approx(TX_APPLY_SECONDS)


def test_the_metric_is_the_one_the_worker_is_told_to_print():
    """--metric on the worker command line and the string the scanner greps.

    stellar-core prints nothing at all without the flag, so a rename on either
    side is a run's worth of missing metrics with no error.
    """
    script = jm.RESUME_SCRIPT
    m = re.search(r"--metric '([^']+)'", script)
    assert m, "the worker no longer asks stellar-core for a metric"
    assert m.group(1) in lc._TX_METRIC, (
        f"the worker prints {m.group(1)!r}, the collector greps {lc._TX_METRIC!r}")
