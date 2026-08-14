"""Reading medida statistics out of a stellar-core log.

Both readers live here on purpose. The collector scans the live stream and the
monitor re-reads the finished archive, and the two must agree on how far a `sum`
may sit from its block header -- otherwise the recovery path inherits exactly the
blind spot it exists to cover.
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
