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

import job_monitor as jm


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
        cluster.advance(end, state)
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
    assert res.requests['memory'] == '13824Mi'


def test_evictions_do_not_burn_the_disk_budget(cluster, monkeypatch):
    """Same shape, ephemeral-storage budget (4). Disk evictions repeat until
    the range gets more disk, so losing that budget to churn is terminal."""
    monkeypatch.setattr(jm, 'LIM_EPHEMERAL', '40Gi')
    monkeypatch.setattr(jm, 'REQ_EPHEMERAL', '40Gi')

    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=5)
    assert cluster.attempt_of(end) == 6

    hit(cluster, end, 'ephemeral')

    assert not condemned(cluster, end), (
        "first disk eviction after eviction churn condemned the range; "
        f"failed={cluster.failed()}")
    assert job_exists(cluster, end, 7)
    # Retried with MORE disk than it just outgrew, not the same 40Gi.
    grown = mem_of(cluster, end, 7).limits['ephemeral-storage']
    assert jm._quantity_bytes(grown) > jm._quantity_bytes('40Gi'), grown


def test_evictions_do_not_burn_the_oom_budget(cluster):
    """The OOM budget is small, so churn would eat it almost immediately.

    Retargeted from the timeout budget, which no longer exists: a deadline hit
    is terminal now. The invariant is the same one -- a cause with a large
    deliberate budget must not spend a small one belonging to a different cause.
    """
    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=3)
    assert cluster.attempt_of(end) == 4

    hit(cluster, end, 'oom')

    assert not condemned(cluster, end), (
        f"first OOM after 3 evictions condemned the range; "
        f"failed={cluster.failed()}")
    assert job_exists(cluster, end, 5)


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
    monkeypatch.setattr(jm, 'MEM_ESCALATION_CAP', '128Gi')
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


def test_the_disruption_budget_still_binds(cluster):
    """Twenty evictions really is the end of the road for a range."""
    end = dispatch(cluster)
    hit(cluster, end, 'disrupted', times=jm.MAX_DISRUPTION_ATTEMPTS)

    assert condemned(cluster, end), (
        f"{jm.MAX_DISRUPTION_ATTEMPTS} evictions were not condemned; "
        f"jobs={cluster.jobs()}")
    assert not job_exists(cluster, end, jm.MAX_DISRUPTION_ATTEMPTS + 1)


def test_a_genuine_catchup_failure_is_still_never_retried(cluster):
    """exit 1 is condemned on attempt 1 regardless of any tally."""
    end = dispatch(cluster)
    hit(cluster, end, 'condemned')

    assert condemned(cluster, end)
    assert cluster.failed()[str(end)]['attempts'] == 1
    assert not job_exists(cluster, end, 2)


def test_a_terminated_pod_with_no_exit_code_is_not_condemned(cluster):
    """A container that terminated without a populated exit code says nothing
    about the ledger range, and must not be treated as a catchup failure.

    Observed on the r5 run 2026-07-30: range 59018943 was condemned on attempt 1
    with `outcome=failed exitCode=None`, failing a mission that was otherwise
    554 for 554. The collector's classify() fell through every branch that
    needs an exit code and labelled the leftover case `failed` -- the one
    outcome that gets no retry at all.
    """
    end = dispatch(cluster)
    hit(cluster, end, 'no_exit_code')

    assert not condemned(cluster, end), (
        "a pod reaped before classification condemned the range and failed the "
        f"mission on no evidence. failed={cluster.failed()}")
    assert job_exists(cluster, end, 2), (
        f"no attempt 2 was dispatched; live jobs are {cluster.jobs()}")


def test_a_real_catchup_failure_is_still_condemned(cluster):
    """The guard above must not swallow the case it is next to: an exit code of
    1 IS evidence, and a range that produces one still fails the mission."""
    end = dispatch(cluster)
    hit(cluster, end, 'condemned')

    assert condemned(cluster, end), (
        "exit 1 is a genuine catchup failure and must not be retried")
