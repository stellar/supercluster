"""RACE #5 -- attempt budgets are spent from one shared counter.

Every retry bumps the same attempt index. The cap is then picked from whatever
the LATEST verdict happened to be, and the global index is compared against it.
So cluster churn (spot evictions, admission rejections, monitor restarts) --
which has its own, deliberately large budget -- silently drains the small
budgets belonging to the causes that actually say something about the range.

A range evicted five times arrives at attempt 6. Its FIRST genuine OOM is then
compared 6 >= MAX_ATTEMPTS(5) and condemned, having never once been retried for
an OOM and never once had its memory escalated. A condemned range fails the
whole mission.

Everything below is observed state: which Jobs exist, what resources they were
created with, and what landed in progress.json's failed{}. No source text.
"""

import config
import units
import sizing
import job_monitor as jm
import records


# --- helpers (local to this file on purpose) --------------------------------

def dispatch(cluster, end=300):
    """Get `end` to attempt 1, running, with nothing else in the way."""
    cluster.reconcile()
    assert cluster.attempt_of(end) == 1
    return end


def hit(cluster, end, state, times=1):
    """Fail the range's newest attempt `times` times in a row with `state`.

    One reconcile per failure, which is what the real loop does: the monitor
    sees the failed Job, decides retry-or-condemn, and (if retrying) creates
    the successor before the next pass.
    """
    for _ in range(times):
        n = cluster.attempt_of(end)
        cluster.advance(end, state)
        if state in ('incomplete', 'unexplained'):
            # exit 3 is decided from the archive, and not until .done exists.
            cluster.finalize(end, n, archive=(
                'fetch_fault' if state == 'incomplete'
                else 'bare'))
        cluster.reconcile()


def job_exists(cluster, end, attempt):
    return jm.job_name(int(end), attempt) in cluster.jobs()


def mem_of(cluster, end, attempt):
    job = cluster.k8s.job(jm.job_name(int(end), attempt))
    return job.spec.template.spec.containers[0].resources


def condemned(cluster, end):
    return str(end) in cluster.failed()


# --- the race ---------------------------------------------------------------

def test_five_evictions_do_not_burn_the_whole_oom_budget(cluster):
    """5 spot evictions (budget 20) must leave the OOM budget (5) untouched.

    Under the bug the range is on attempt 6 when its first OOM lands, 6 >= 5,
    and it is condemned without ever being retried for the OOM -- so its memory
    is never escalated and the mission fails on a range that is merely unlucky.
    """
    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=5)
    # Five evictions are legal on the disruption budget: the range is alive.
    assert not condemned(cluster, end)
    assert cluster.attempt_of(end) == 6

    hit(cluster, end, 'oom')

    assert not condemned(cluster, end), (
        "first OOM after eviction churn condemned the range: the eviction "
        f"retries spent the OOM budget. failed={cluster.failed()}")
    assert job_exists(cluster, end, 7), (
        f"no attempt 7 was dispatched; live jobs are {cluster.jobs()}")

    # And the whole point of an OOM retry: more memory. One OOM = one rung.
    res = mem_of(cluster, end, 7)
    assert res.requests['memory'] == '13824Mi'


def test_evictions_do_not_burn_the_disk_budget(cluster, monkeypatch):
    """Same shape, ephemeral-storage budget (4). Disk evictions repeat until
    the range gets more disk, so losing that budget to churn is terminal."""
    monkeypatch.setattr(config, 'LIM_EPHEMERAL', '40Gi')
    monkeypatch.setattr(config, 'REQ_EPHEMERAL', '40Gi')

    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=5)
    assert cluster.attempt_of(end) == 6

    hit(cluster, end, 'ephemeral')

    assert not condemned(cluster, end), (
        "first disk eviction after eviction churn condemned the range; "
        f"failed={cluster.failed()}")
    assert job_exists(cluster, end, 7)
    # One eviction = one rung: 40Gi * EPH_BUMP_FACTOR. Pinned to the exact rung
    # rather than "more than 40Gi", so indexing the ladder on `attempt` instead
    # of on evictions fails here -- after this much churn that would ask for the
    # 6th rung (capped at 200Gi), five times the disk for one eviction.
    grown = mem_of(cluster, end, 7).limits['ephemeral-storage']
    assert grown == sizing.eph_for_attempt(2) == '61440Mi', grown


def test_evictions_do_not_burn_the_range_budget_for_exit_3(cluster):
    """exit 3 ("did not complete") rides the ordinary range budget of 5.

    A range evicted five times gets zero exit-3 retries -- and exit 3 is the
    outcome an interrupted-then-resumable range produces, so the retry that was
    denied is the one that would have succeeded.
    """
    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=5)
    assert cluster.attempt_of(end) == 6

    hit(cluster, end, 'incomplete')

    assert not condemned(cluster, end), (
        f"first exit-3 after eviction churn condemned the range; "
        f"failed={cluster.failed()}")
    assert job_exists(cluster, end, 7)


def test_memory_ladder_follows_ooms_not_evictions(cluster, monkeypatch):
    """Interleaved churn: each OOM must climb exactly one rung, and the second
    OOM must still be inside the budget even though the attempt index is 8."""
    # The shipped 48Gi ceiling clamps rung 2 to the same figure rung 8 would
    # give, which would make the ladder assertion below prove nothing. Raise it
    # so the rung is observable; the budget behaviour under test is unaffected.
    monkeypatch.setattr(config, 'MEM_ESCALATION_CAP', '128Gi')
    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=3)     # attempts 1-3, now on 4
    hit(cluster, end, 'oom')                    # OOM #1 on attempt 4 -> a5
    assert job_exists(cluster, end, 5)
    assert mem_of(cluster, end, 5).requests['memory'] == '13824Mi'

    hit(cluster, end, 'disrupted', times=2)     # attempts 5-6, now on 7
    hit(cluster, end, 'oom')                    # OOM #2 on attempt 7 -> a8

    assert not condemned(cluster, end), f"failed={cluster.failed()}"
    assert job_exists(cluster, end, 8)
    # 24000Mi * 1.5^2 -- two OOMs, six evictions, two rungs.
    assert mem_of(cluster, end, 8).requests['memory'] == '20736Mi'


# --- the caps must still bind (a fix that just removes them is not a fix) ----

def test_the_oom_budget_still_binds(cluster):
    """Five real OOMs in a row exhaust the OOM budget and condemn the range."""
    end = dispatch(cluster)
    hit(cluster, end, 'oom', times=5)

    assert condemned(cluster, end), (
        f"five consecutive OOMs were not condemned; jobs={cluster.jobs()}")
    assert cluster.failed()[str(end)]['outcome'] == 'oom'
    assert not job_exists(cluster, end, 6), (
        f"a 6th OOM attempt was dispatched past the budget: {cluster.jobs()}")


def test_one_timeout_condemns_the_range(cluster):
    """A timeout is terminal -- it has no budget to bind.

    The deadline exists only for a range wedged on an unreachable archive, and
    retrying that just spends another 12h to learn the same thing. This used to
    allow 2 attempts; the assertion is that a SECOND one is never dispatched.
    """
    end = dispatch(cluster)
    hit(cluster, end, 'timeout')

    assert condemned(cluster, end), (
        f"the first timeout did not condemn the range; jobs={cluster.jobs()}")
    assert cluster.failed()[str(end)]['outcome'] == 'timeout'
    assert not job_exists(cluster, end, 2), (
        f"a second attempt was dispatched after a terminal timeout: {cluster.jobs()}")


def test_an_archive_without_the_done_marker_does_not_promote_a_fetch_fault(cluster):
    """The collector appends as the pod runs, so a present archive is not a
    finished one.

    Promoting on a partial archive would read a fetch-fault anchor that a later
    line still explains away, and hand the range the fetch-fault budget on
    incomplete evidence. The .done marker is what says the collector is finished
    with this attempt, so the decision waits for it.
    """
    end = dispatch(cluster)
    attempt = cluster.attempt_of(end)
    cluster.advance(end, 'incomplete')
    cluster.archive(end, attempt, 'fetch_fault')   # archive, but no .done
    cluster.reconcile()

    assert not condemned(cluster, end), f"failed={cluster.failed()}"
    assert not job_exists(cluster, end, attempt + 1), (
        "a successor was dispatched off a half-written archive: "
        f"{cluster.jobs()}")
    assert records._verdict_of(str(end), attempt) != 'fetch-fault', \
        "the verdict was promoted before the collector finished"


def test_a_cause_with_no_budget_entry_gets_no_retries(cluster):
    """The table is the whole policy: absent means condemned on sight.

    Not reachable through reconcile today -- every outcome that gets as far as
    the retry gate is in the table, and the rest return CONDEMN before the cap
    is read. This pins the default so a new outcome added to classify() cannot
    quietly inherit retries nobody chose for it.
    """
    assert jm.budget_for({'outcome': 'a-brand-new-thing'}, 300, 1) == (0, 0)
    for terminal in ('timeout', 'unknown'):
        assert terminal not in config.ATTEMPT_BUDGETS
        assert jm.budget_for({'outcome': terminal}, 300, 1)[1] == 0


def test_a_fetch_fault_does_not_spend_the_oom_budget(cluster):
    """An unreachable archive is the cluster's problem, not the range's.

    Before this, an exit-3 fetch fault was recorded as `failed` and the range
    budget counted ('oom', 'failed') -- so one unreachable S3 mirror permanently
    cost the range one of its five memory escalations.
    """
    end = dispatch(cluster)
    hit(cluster, end, 'incomplete')          # exit 3, fetch fault in the archive
    assert job_exists(cluster, end, 2), f"the fetch fault was not retried: {cluster.jobs()}"

    # The OOM ladder still has its whole budget.
    hit(cluster, end, 'oom', times=config.ATTEMPT_BUDGETS['oom'] - 1)
    assert not condemned(cluster, end), (
        "the fetch fault spent an OOM attempt; "
        f"failed={cluster.failed()} jobs={cluster.jobs()}")
    hit(cluster, end, 'oom')
    assert condemned(cluster, end), (
        f"the OOM budget did not bind after {config.ATTEMPT_BUDGETS['oom']} OOMs")
    assert cluster.failed()[str(end)]['outcome'] == 'oom'


def test_the_disk_budget_still_binds(cluster, monkeypatch):
    """Disk evictions are counted against their OWN budget, and it binds.

    The gap this closes: with the ephemeral arm of budget_for removed, an
    eviction falls through to the range budget, whose counter looks only at
    ('oom', 'failed'). For a purely disk-evicted range that count is always 0, so
    it would be retried forever -- and every other budget test still passed.
    """
    monkeypatch.setattr(config, 'LIM_EPHEMERAL', '40Gi')
    monkeypatch.setattr(config, 'REQ_EPHEMERAL', '40Gi')
    end = dispatch(cluster)
    hit(cluster, end, 'ephemeral', times=config.ATTEMPT_BUDGETS['ephemeral'])

    assert condemned(cluster, end), (
        f"{config.ATTEMPT_BUDGETS['ephemeral']} disk evictions were not condemned; "
        f"jobs={cluster.jobs()}")
    assert cluster.failed()[str(end)]['outcome'] == 'ephemeral'
    assert not job_exists(cluster, end, config.ATTEMPT_BUDGETS['ephemeral'] + 1), (
        f"an attempt was dispatched past the disk budget: {cluster.jobs()}")


def test_an_eviction_with_no_configured_disk_limit_does_not_wedge_reconcile(cluster):
    """LIM_EPHEMERAL is empty by default and the chart ships it empty.

    A pod with no ephemeral-storage limit can still be evicted under node disk
    pressure. eph_for_attempt used to parse the empty string and raise, and
    reconcile's caller swallows exceptions -- so one such eviction killed every
    later pass at the same range: no dispatch, no completions, for the rest of
    the run.
    """
    assert config.LIM_EPHEMERAL == '', "this test is about the unset default"
    end = dispatch(cluster)
    hit(cluster, end, 'ephemeral')

    assert not condemned(cluster, end), f"failed={cluster.failed()}"
    assert job_exists(cluster, end, 2), (
        f"the eviction was not retried; jobs={cluster.jobs()}")
    # And the pass still completes for everything else.
    assert cluster.reconcile()['completed'] == 0


def test_the_disk_budget_is_smaller_than_the_range_budget(cluster):
    """Pins that the two caps are actually different.

    Both tests above pass if disk silently borrows the range budget, as long as
    the caps happen to match. They must not: escalating disk 5 times is a 7.6x
    request.
    """
    # The MAX_* constants, not ATTEMPT_BUDGETS: the cluster fixture patches the
    # map, so asserting on it here would pin the fixture and let production ship
    # any ordering it liked.
    assert config.MAX_EPHEMERAL_ATTEMPTS < config.MAX_OOM_ATTEMPTS
    assert config.MAX_EPHEMERAL_ATTEMPTS < config.MAX_DISRUPTION_ATTEMPTS, (
        "an eviction is the range's own problem, not the cluster's")


def test_the_disruption_budget_still_binds(cluster, monkeypatch):
    """The environmental budget is effectively unlimited, but it is still a gate.

    Driven at a small cap rather than the configured one: what matters is that
    the gate fires at N, and looping to the real value would make this test do a
    thousand reconcile passes. test_attempt_budgets_are_ordered_by_whose_fault
    pins the production number.
    """
    monkeypatch.setitem(config.ATTEMPT_BUDGETS, 'disrupted', 6)
    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=6)

    assert condemned(cluster, end), (
        f"6 evictions were not condemned; jobs={cluster.jobs()}")
    assert not job_exists(cluster, end, 7)


def test_a_condemned_range_is_decided_once_and_then_cleaned_up(cluster, caplog):
    """A condemned Job is not deleted by anything else until JOB_TTL_SECONDS.

    It therefore stays the newest Job for its range, so every later pass
    re-derives the same verdict and re-logs the same condemnation -- measured on
    the 2026-07-30 run as 15 identical lines over 9 minutes, ending only when the
    TTL removed the Job. The reap waits for the collector's marker because
    deleting the Job reaps the pod.
    """
    import logging
    end = dispatch(cluster)
    hit(cluster, end, 'condemned')
    assert condemned(cluster, end), f"failed={cluster.failed()}"

    # Before the collector finishes: the Job stays, and nothing is re-decided.
    caplog.clear()
    with caplog.at_level(logging.ERROR):
        cluster.reconcile()
    assert 'RANGE CONDEMNED' not in caplog.text, \
        "the condemnation was logged again on a later pass"
    assert cluster.jobs(), "the Job was reaped before the collector finalized it"

    attempt = cluster.attempt_of(end)
    job = jm.job_name(end, attempt)
    cluster.finalize(end, attempt)
    cluster.reconcile()
    assert job not in cluster.jobs(), \
        f"the condemned Job was not reaped: {cluster.jobs()}"
    assert not [v for v in cluster.pvcs() if str(end) in v], \
        f"the condemned range kept its volume: {cluster.pvcs()}"
    # The other ranges are untouched.
    assert len(cluster.jobs()) == 2, cluster.jobs()
    # Still condemned, and still the reason the mission fails.
    assert condemned(cluster, end)


def test_a_genuine_catchup_failure_is_still_never_retried(cluster):
    """exit 1 is condemned on attempt 1 regardless of any tally."""
    end = dispatch(cluster)
    hit(cluster, end, 'condemned')

    assert condemned(cluster, end)
    assert cluster.failed()[str(end)]['attempts'] == 1
    assert not job_exists(cluster, end, 2)


def test_a_terminated_pod_with_no_exit_code_is_condemned(cluster):
    """No exit code is no evidence, and the run stops rather than guess.

    This reverses an earlier choice, so the cost stays on the record: on the r5
    run 2026-07-30 range 59018943 was condemned exactly this way and failed a
    mission that was otherwise 554 for 554. The policy now is that only a node
    disruption -- which proves the cluster took the pod away mid-run -- earns a
    retry without evidence. Anything the monitor cannot explain fails the run,
    because a run that reports success on a range nobody verified is worse.
    """
    end = dispatch(cluster)
    hit(cluster, end, 'no_exit_code')

    assert condemned(cluster, end), (
        f"a pod reaped before classification was retried on no evidence; "
        f"jobs={cluster.jobs()}")
    assert not job_exists(cluster, end, 2), (
        f"attempt 2 was dispatched with nothing explaining attempt 1: {cluster.jobs()}")


def test_an_unclassified_failure_is_condemned(cluster):
    """Same rule for a pod that vanished before anything classified it."""
    end = dispatch(cluster)
    hit(cluster, end, 'unknown')

    assert condemned(cluster, end), f"jobs={cluster.jobs()}"
    assert cluster.failed()[str(end)]['outcome'] == 'unknown'
    assert not job_exists(cluster, end, 2)


def test_a_disruption_is_the_only_thing_retried_without_evidence(cluster):
    """The counterpart: a disruption proves the range itself was fine."""
    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=8)

    assert not condemned(cluster, end), (
        f"eight spot evictions condemned a healthy range; failed={cluster.failed()}")
    assert job_exists(cluster, end, 9)


def test_a_real_catchup_failure_is_still_condemned(cluster):
    """The guard above must not swallow the case it is next to: an exit code of
    1 IS evidence, and a range that produces one still fails the mission."""
    end = dispatch(cluster)
    hit(cluster, end, 'condemned')

    assert condemned(cluster, end), (
        "exit 1 is a genuine catchup failure and must not be retried")


# --- exit 3: retried only when the archive explains it -------------------------

def test_exit_3_with_a_fetch_fault_is_retried(cluster):
    """The one exit-3 observed in production: a pod that could not reach STS.

    Every aws s3 cp failed before touching S3, stellar-core reported it as a
    stale archive, and the retry succeeded on another node in 32 seconds.
    """
    end = dispatch(cluster)
    cluster.advance(end, 'incomplete')
    cluster.finalize(end, 1, archive='fetch_fault')
    cluster.reconcile()

    assert not condemned(cluster, end), f"failed={cluster.failed()}"
    assert job_exists(cluster, end, 2)


def test_exit_3_with_nothing_to_explain_it_is_condemned(cluster):
    """A give-up line with no fetch cascade in front of it earns no retry.

    Conservative by choice: the archive survives on the volume, so an
    unrecognised cause is read off the failed run and added to the marker lists
    rather than guessed at now.
    """
    end = dispatch(cluster)
    cluster.advance(end, 'unexplained')
    cluster.finalize(end, 1, archive='bare')
    cluster.reconcile()

    assert condemned(cluster, end), f"jobs={cluster.jobs()}"
    assert not job_exists(cluster, end, 2)
    assert cluster.failed()[str(end)]['attempts'] == 1


def test_exit_3_waits_for_the_collector_before_deciding(cluster):
    """The archive is the evidence, so the decision cannot precede .done."""
    end = dispatch(cluster)
    cluster.advance(end, 'incomplete')          # no finalize: nothing to read yet

    cluster.reconcile()

    assert not condemned(cluster, end), "condemned before the evidence existed"
    assert not job_exists(cluster, end, 2), "retried before the evidence existed"

    cluster.finalize(end, 1, archive='fetch_fault')
    cluster.reconcile()
    assert job_exists(cluster, end, 2), "still not retried once finalized"


def test_a_permanently_missing_object_beats_an_earlier_transient_error(cluster):
    """A 404 is not transient, and it wins over a connect error further back.

    Both markers are in the window on purpose: the nearest cause to the anchor is
    the one that killed the attempt, so a recovered connect error earlier in the
    same window must not earn a retry.
    """
    end = dispatch(cluster)
    cluster.advance(end, 'incomplete')
    cluster.finalize(end, 1, archive=(
        # recovered earlier -- must NOT decide the outcome
        'fatal error: Could not connect to the endpoint URL: '
        '"https://sts.us-east-1.amazonaws.com/"\n'
        '2026-01-01T00:00:00.000 GAJSL [History INFO] Selected archive core_live_002\n'
        # the cause that actually terminated it
        'fatal error: An error occurred (404) when calling the HeadObject '
        'operation: Key does not exist\n'
        '2026-01-01T00:00:00.000 GAJSL [History WARNING] Could not download file: '
        'archive core_live_003 maybe missing file history/00/00/00/history-0.json\n'
        '2026-01-01T00:00:00.000 GAJSL [History ERROR] Missing HAS for ledger 1: '
        'maybe stale archive core_live_003\n'
        '2026-01-01T00:00:00.000 GAJSL [History WARNING] Catchup failed\n'))
    cluster.reconcile()

    assert condemned(cluster, end), f"a 404 was treated as transient; jobs={cluster.jobs()}"


def test_a_recovered_fetch_fault_does_not_earn_a_retry_for_a_later_failure(cluster):
    """The anchor must be the cause of THIS give-up, not an earlier recovered one.

    stellar-core retries a failed fetch, so a range can log the whole cascade
    several times and carry on -- 10 of the 11 in the one production exit-3 were
    retries that recovered. Crediting any of them would retry a range that later
    died of something else entirely.
    """
    end = dispatch(cluster)
    cluster.advance(end, 'incomplete')
    cluster.finalize(end, 1, archive=(
        # a fetch fault that stellar-core recovered from
        'fatal error: Could not connect to the endpoint URL: '
        '"https://sts.us-east-1.amazonaws.com/"\n'
        '2026-01-01T00:00:00.000 GAJSL [History WARNING] Could not download file: '
        'archive core_live_003 maybe missing file history/00/00/00/history-0.json\n'
        '2026-01-01T00:00:00.000 GAJSL [History ERROR] Missing HAS for ledger 1: '
        'maybe stale archive core_live_003\n'
        # ...then it got the file and went on to replay
        + ''.join('2026-01-01T00:00:0%d.000 GAJSL [Ledger INFO] '
                  'Ledger close complete: %d\n' % (i % 10, 100 + i)
                  for i in range(20))
        # ...and died of something the archive does not explain
        + '2026-01-01T00:00:00.000 GAJSL [History WARNING] Catchup failed\n'))
    cluster.reconcile()

    assert condemned(cluster, end), (
        f"a recovered fetch fault 20 lines earlier earned a retry; "
        f"jobs={cluster.jobs()}")


def test_the_real_production_exit_3_is_retried_end_to_end(cluster):
    """The whole path, on output stellar-core actually produced.

    Every other test here feeds hand-written archive text, so the pattern and the
    fixture agree by construction -- they cannot falsify each other. This is the
    verbatim archive of the one exit-3 in the 2026-08-04 run: a pod whose
    aws s3 cp could not reach STS, which gave up after 35 minutes and whose
    retry fetched the same object from the same bucket in 32 seconds.
    """
    import gzip
    import pathlib
    real = (pathlib.Path(__file__).resolve().parent.parent
            / 'data' / 'real-sts-fault-exit3.log.gz')
    with gzip.open(real, 'rt', errors='replace') as fh:
        archive = fh.read()

    end = dispatch(cluster)
    cluster.advance(end, 'incomplete')
    cluster.finalize(end, 1, archive=archive)
    cluster.reconcile()

    assert not condemned(cluster, end), (
        f"the real STS-fault exit-3 was condemned; failed={cluster.failed()}")
    assert job_exists(cluster, end, 2), "no retry was dispatched"
