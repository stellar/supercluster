"""RACE #2: a txApply that arrives after the range is first recorded is
backfilled into progress.json but never reaches the Prometheus histogram.

The interleaving under test is the ordinary one at 1024 workers: the Job flips
to succeeded and reconcile records the range before the log-collector sidecar
has flushed that attempt's .metrics, so the first record carries txApply=None.
A later pass backfills the real value into progress.json. The histogram is
supposed to be a replay of the recorded ranges, so once progress.json says
txApply=1.25 the histogram must have counted 1.25 -- exactly once.

Every assertion here is on observed state: the durable progress record on the
fake logs volume, and the samples the Prometheus client actually exports.
Nothing reads job_monitor's source.
"""

import job_monitor as jm


# --- reading the exported metric --------------------------------------------
#
# The histograms are module-level and share the global REGISTRY, so absolute
# values leak across tests in one process. Every assertion below is therefore a
# delta taken inside a single test. This reads the exported samples -- the same
# numbers /metrics would serve -- not any private attribute.

def _hist(metric):
    """(count, sum) of a label-less Histogram, from its exported samples."""
    count = total = 0.0
    for family in metric.collect():
        for s in family.samples:
            if s.name.endswith('_count'):
                count = s.value
            elif s.name.endswith('_sum'):
                total = s.value
    return count, total


def _delta(before, after):
    return after[0] - before[0], round(after[1] - before[1], 9)


def _succeed_without_metrics(cluster, end):
    """Job succeeds, collector has not written anything for it yet.

    No .metrics, no .log.gz, and the fake pod log is empty, so all three of
    tx_apply_for_range's sources come up dry -- which is exactly the state the
    monitor is in when it records the range in the same second the Job flips.
    """
    cluster.reconcile()
    cluster.advance(end, 'succeeded')
    cluster.reconcile()


def test_late_txapply_reaches_the_histogram_not_just_progress_json(cluster):
    """The bug, stated as the disagreement it causes.

    progress.json ends up saying txApply is known for the range while the
    histogram never counted it, so the artifact and /metrics describe different
    runs.
    """
    _succeed_without_metrics(cluster, 300)

    # Precondition: recorded, but with no txApply yet. If this ever stops
    # holding the test below is not exercising the race any more.
    assert cluster.completed()['300']['txApply'] is None

    before = _hist(jm.metric_tx_apply_duration)

    # The collector finishes and flushes the attempt's measurements.
    cluster.finalize(300, 1, tx_apply=1.25)
    cluster.reconcile()

    # The durable artifact now claims the value is known...
    assert cluster.progress()['completed']['300']['txApply'] == 1.25

    # ...so the histogram must have counted that same value.
    count, total = _delta(before, _hist(jm.metric_tx_apply_duration))
    assert (count, total) == (1.0, 1.25), (
        "progress.json carries txApply=1.25 for range 300 but the histogram "
        f"observed count+{count} sum+{total}: the backfilled value can never "
        "be counted, so /metrics under-reports every range whose .metrics "
        "landed after the range was first recorded")


def test_backfilled_txapply_is_counted_once_not_on_every_later_pass(cluster):
    """The other half of the contract: exactly once, not once per pass.

    A fix that simply stops skipping the range would re-observe the value on
    every subsequent reconcile, which at a 10s loop inflates the histogram
    without bound.
    """
    _succeed_without_metrics(cluster, 300)
    before = _hist(jm.metric_tx_apply_duration)

    cluster.finalize(300, 1, tx_apply=1.25)
    for _ in range(4):
        cluster.reconcile()

    assert cluster.progress()['completed']['300']['txApply'] == 1.25
    count, total = _delta(before, _hist(jm.metric_tx_apply_duration))
    assert (count, total) == (1.0, 1.25), (
        f"range 300's txApply was observed {count} times across four passes; "
        "the histogram must count each recorded range exactly once")


def test_durations_recorded_up_front_are_not_recounted_while_txapply_is_late(cluster):
    """seconds/wallSeconds are known on the first record and must stay at one.

    This is the failure mode of the tempting one-line fix (move the guard
    inside the txApply branch): the range then stays unmarked for as many
    passes as the collector takes, and every one of those passes re-observes
    the durations it already counted. The two histograms would drift apart in
    opposite directions.
    """
    _succeed_without_metrics(cluster, 300)

    rec = cluster.completed()['300']
    assert rec['seconds'] is not None and rec['wallSeconds'] is not None
    seconds, wall = rec['seconds'], rec['wallSeconds']

    before_full = _hist(jm.metric_full_duration)
    before_wall = _hist(jm.metric_wall_duration)

    # Three passes with the collector still silent, then it finally lands.
    for _ in range(3):
        cluster.reconcile()
    cluster.finalize(300, 1, tx_apply=0.5)
    cluster.reconcile()
    cluster.reconcile()

    assert cluster.progress()['completed']['300']['txApply'] == 0.5

    assert _delta(before_full, _hist(jm.metric_full_duration)) == (0.0, 0.0), (
        "the full-duration histogram re-observed range 300's already-counted "
        f"{seconds}s while waiting for its txApply")
    assert _delta(before_wall, _hist(jm.metric_wall_duration)) == (0.0, 0.0), (
        "the wall-duration histogram re-observed range 300's already-counted "
        f"{wall}s while waiting for its txApply")


def test_txapply_present_on_first_sight_is_still_counted_exactly_once(cluster):
    """Baseline: the non-racing order must keep working.

    Collector finalizes before the monitor ever sees the Job, so txApply is
    known at first record. One observation, and no second one later.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=2.5)

    before = _hist(jm.metric_tx_apply_duration)
    cluster.reconcile()
    cluster.reconcile()
    cluster.reconcile()

    assert cluster.progress()['completed']['300']['txApply'] == 2.5
    assert _delta(before, _hist(jm.metric_tx_apply_duration)) == (1.0, 2.5)


def test_two_ranges_landing_their_metrics_at_different_times_both_count(cluster):
    """The population-level consequence, at the smallest scale that shows it.

    One range's .metrics is ready on the first pass and the other's is not.
    Both end up in progress.json with a txApply, so the histogram must contain
    both -- not just the one that happened to win the race.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.advance(200, 'succeeded')
    # 300's collector is quick; 200's is not.
    cluster.finalize(300, 1, tx_apply=1.0)

    before = _hist(jm.metric_tx_apply_duration)
    cluster.reconcile()

    assert cluster.completed()['200']['txApply'] is None

    cluster.finalize(200, 1, tx_apply=3.0)
    cluster.reconcile()

    recorded = {k: v['txApply'] for k, v in cluster.completed().items()}
    assert recorded == {'300': 1.0, '200': 3.0}

    count, total = _delta(before, _hist(jm.metric_tx_apply_duration))
    assert (count, total) == (2.0, 4.0), (
        f"progress.json holds txApply for {sorted(recorded)} but the histogram "
        f"counted {count} of them (sum {total}); only the range whose .metrics "
        "was ready on the first pass was observed")
