"""The single writer for one MissionParallelCatchup run.

Turns a ledger range into completed work using Kubernetes Jobs, and reports what
happened. One pass is: snapshot the cluster, derive each range's state, observe
the volume, act on the apiserver, commit the record.

EXACTLY ONE of these may run. No leader election exists anywhere, so a rolling
update that briefly overlaps two is a second writer of every Job, PVC and the
progress record -- replicas: 1, strategy: Recreate.
"""
import asyncio
import collections
import logging
import signal
import sys
import time

import cluster
import config
import dispatch
import liveness
import metrics
import monitor_config as mc
import policy
import record
import server
import sizing
import verdict

logging.basicConfig(level=logging.INFO, stream=sys.stdout,
                    format='%(asctime)s %(levelname)s %(message)s')
logger = logging.getLogger('job_monitor')


async def main():
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(sig, stop.set)

    state = State()
    state.resume()
    async with cluster.session():
        async with asyncio.TaskGroup() as tg:
            tg.create_task(server.serve(state, stop))
            tg.create_task(reconcile_loop(state, stop))
    logger.info("stopped")
    return 1 if state.progress['condemned'] else 0


async def reconcile_loop(state, stop):
    while not stop.is_set():
        if state.ranges:
            try:
                await reconcile(state)
            except Exception:
                # A pass is a projection; the next one rebuilds it. Dying here
                # leaves the run with no writer.
                logger.exception("reconcile pass failed")
        try:
            async with asyncio.timeout(mc.RECONCILE_INTERVAL_SECONDS):
                await stop.wait()
        except TimeoutError:
            pass
    stop.set()


async def reconcile(state):
    """One pass: look, decide, act, persist.

    The volume is touched in exactly two places, both threaded and both
    batched. Everything between them is decisions and apiserver calls.
    """
    jobs, pods = await cluster.snapshot()
    states = derive_all(state, jobs, pods)
    await asyncio.to_thread(observe, states)
    await act(states, state)
    await asyncio.to_thread(state.commit, states)
    await publish(states, state, pods)


# --- what is true about one range -------------------------------------------


class RangeState:
    """One range as of this pass. Cheap to build, thrown away at the end."""

    __slots__ = ('end', 'count', 'attempt', 'status', 'jobs', 'job', 'pod',
                 'verdict', 'completed_at', 'done', 'measured')

    def __init__(self, end, count, attempt=0, status='pending', jobs=(),
                 job=None, pod=None, verdict=None, completed_at=None):
        self.end = str(end)
        self.count = count
        self.attempt = attempt
        self.status = status
        self.jobs = jobs
        self.job = job
        self.pod = pod
        self.verdict = verdict
        self.completed_at = completed_at
        self.done = False          # set by observe()
        self.measured = None       # set by observe() for a completed range

    @property
    def holds_capacity(self):
        """Anything unfinished occupies a slot, a failed attempt included: its
        retry is already owed. Missions run longest-first, so releasing the slot
        would let short ranges dispatch ahead of a long range's retry."""
        return self.status in ('running', 'failed')


def derive_all(state, jobs, pods):
    return [derive(end, count, state.progress, jobs, pods)
            for end, count in state.ranges]


def derive(end, count, progress, jobs, pods):
    """One range from its newest Job, that Job's pod, and the record.

    The record answers for a range with no Job: reaped, condemned, or never
    started. Without it a reaped range reads as pending and is dispatched a
    second time.
    """
    range_jobs = jobs.get(str(end), ())
    job = max(range_jobs, key=_attempt_of, default=None)
    if job is None:
        # Terminal: re-deciding would re-count the verdict on every pass.
        if str(end) in progress['condemned']:
            return RangeState(end, count, status='condemned')
        if str(end) in progress['completed']:
            attempt = progress['completed'][str(end)].get('attempts', 1)
            return RangeState(end, count, attempt, 'completed')
        return RangeState(end, count)

    attempt = _attempt_of(job)
    names = [j.metadata.name for j in range_jobs]
    pod = pods.get(job.metadata.name)
    # succeeded and failed are POD COUNTS and there is no running field, so
    # running is whatever is neither.
    if job.status.succeeded:
        return RangeState(end, count, attempt, 'completed', names, job, pod,
                          completed_at=job.status.completion_time)
    if job.status.failed:
        # Classified by observe(); this phase reads nothing.
        return RangeState(end, count, attempt, 'failed', names, job, pod)
    return RangeState(end, count, attempt, 'running', names, job, pod)


def observe(states):
    """Every read a pass makes. Only a finished attempt has anything to say.

    A failed one is classified here because an exit-3 verdict decompresses a
    whole archive, and a completed one is aggregated here because that walks
    its .metrics -- both are reads, so both belong before any decision.
    """
    for st in states:
        if st.status not in ('failed', 'completed'):
            continue
        st.done = record.is_done(st.end, st.attempt)
        # Otherwise the slot is held for the rest of the run.
        if not st.done and st.status == 'failed' and st.pod is None:
            st.done = record.unclaimed(st.end, st.attempt)
        if not st.done:
            continue          # the collector is still writing this attempt
        if st.status == 'failed':
            st.verdict = verdict.effective(st.end, st.attempt, st.job)
        elif st.jobs:
            st.measured = aggregate(st)


def _attempt_of(job):
    return int((job.metadata.labels or {}).get(config.LABEL_ATTEMPT, 1))


# --- what a pass does about it ----------------------------------------------


async def act(states, state):
    """Retries first, then reaps, then new work into whatever slots are left.

    A retry needs no slot: its range already holds one.
    """
    work = []
    active = sum(1 for st in states if st.holds_capacity)

    for st in states:
        if st.status == 'failed':
            work.append(_settle(st, state))
        elif st.status == 'completed' and st.jobs and st.done:
            work.append(_reap(st, state))

    for st in states:
        if st.status == 'pending' and active < mc.PARALLELISM:
            active += 1
            work.append(_dispatch(st, state))

    for result in await asyncio.gather(*work, return_exceptions=True):
        if isinstance(result, Exception):
            logger.warning("action failed: %s", result)


async def _dispatch(st, state):
    created = await dispatch.create(int(st.end), st.count, 1)
    if created is not None:
        state.started.append((st.end, created.metadata.creation_timestamp))
    logger.info("range %s dispatched", st.end)


async def _settle(st, state):
    """Decide a failed attempt, once the collector has finished with it.

    Deferred until .done because the verdict can still change: an exit 3 that
    reads as a catchup failure is a retryable fetch fault if the archive says
    so, and that is the difference between a retry and a dead run.
    """
    if not st.done:
        return
    cause = st.verdict.get('outcome')
    spent = record.note_cause(state.progress, st.end, st.attempt, cause)
    ooms = spent.get('oom', 0)
    base = sizing.requests_for(int(st.end), ooms)[0]
    decision = policy.decide(int(st.end), st.verdict, spent,
                             base_memory=base.get('memory'),
                             base_ephemeral=base.get('ephemeral-storage'))
    if decision.action == policy.DEFER:
        return
    if decision.action == policy.CONDEMN:
        state.condemn(st, decision.reason)
        await cluster.delete_job(dispatch.job_name(int(st.end), st.attempt))
        return
    logger.info("range %s attempt %d -> %d (%s%s%s)", st.end, st.attempt,
                st.attempt + 1, decision.reason,
                f"; memory={decision.memory}" if decision.memory else '',
                f"; ephemeral={decision.ephemeral}" if decision.ephemeral else '')
    await dispatch.create(int(st.end), st.count, st.attempt + 1,
                          oom_count=ooms, memory=decision.memory,
                          ephemeral=decision.ephemeral)
    metrics.settled(state.progress, cause, end=st.end)
    # After the successor exists, never before: with the predecessor gone and
    # the create failed, the next pass redispatches at attempt 1 and loses the
    # escalated request. TTL reclaims it if this delete does not.
    await cluster.delete_job(dispatch.job_name(int(st.end), st.attempt))


async def _reap(st, state):
    """Delete a completed range's Jobs and release its volume.

    Only once .done: reaping deletes the pod, which is the last place peaks and
    terminated timestamps can be read.
    """
    await cluster.reap(st.end, st.jobs)


# --- what a pass reports ----------------------------------------------------


async def publish(states, state, pods):
    began = time.monotonic()
    counts = await liveness.publish(list(pods.values()))
    swept = time.monotonic() - began          # the sweep alone, nothing after it
    metrics.sync_counters(state.progress, state.applied)
    metrics.observe_completed(state.progress, state.replayed)
    metrics.publish_gauges(state.counts(states), counts, swept,
                           time.time() - state.mission_start)


# --- the run ----------------------------------------------------------------


class State:
    """Everything that outlives a pass. Small on purpose: the cluster and the
    volume are the sources, and this is what cannot be re-derived from them."""

    def __init__(self):
        self.ranges = []
        self.progress = {'completed': {}, 'condemned': {}, 'causes': {},
                         'counters': collections.Counter(),
                         'disruptedRanges': set()}
        self.started = []          # (end, createdAt) awaiting a commit
        self.applied = {}          # counter totals already pushed to prometheus
        self.replayed = set()      # (range, field) already observed
        self.mission_start = time.time()
        self._live = {}            # this pass's counts, for /status

    def resume(self):
        """Replay run.json through the same validation path.

        A restarted monitor must resume the run it inherited rather than wait
        for a /start delivered to its predecessor -- and the profile has to come
        back with it, since longest-first orders by it.
        """
        self.mission_start = record.mission_start()
        self.progress = record.load_progress()
        doc = record.load_run()
        if doc is None:
            return
        try:
            self.start(doc)
        except ValueError as e:
            logger.error("run.json no longer validates (%s); waiting for /start", e)

    def start(self, doc):
        spec = doc.get('range') or {}
        for key, name in (('order', 'RANGE_ORDER'),
                          ('startingLedger', 'STARTING_LEDGER'),
                          ('latestLedgerNum', 'LATEST_LEDGER_NUM'),
                          ('ledgersPerJob', 'LEDGERS_PER_JOB'),
                          ('overlapLedgers', 'OVERLAP_LEDGERS')):
            if key in spec:
                setattr(mc, name, spec[key])
        mc.set_profile(sizing.load_profile(doc.get('profile') or {}))
        mc.validate()
        self.ranges = dispatch.range_list()
        logger.info("run started: %d ranges, %s, profile of %d",
                    len(self.ranges), mc.RANGE_ORDER, len(mc.PROFILE))

    def condemn(self, st, reason):
        """Mark terminal; commit() persists it at the end of the pass.

        Idempotent because it must be: the record is all that keeps a condemned
        range terminal once its Job is gone, and counting the verdict twice
        would inflate the run's reasons forever. Dispatch does not halt --
        ending the run is the driver's call.
        """
        if st.end in self.progress['condemned']:
            return
        self.progress['condemned'][st.end] = {
            'end': int(st.end), 'count': st.count, 'attempts': st.attempt,
            'pod': (st.verdict or {}).get('pod') or '',
            'outcome': (st.verdict or {}).get('outcome'),
            'exitCode': (st.verdict or {}).get('exitCode'),
            'reason': reason}
        metrics.settled(self.progress, (st.verdict or {}).get('outcome'))
        logger.error("range %s condemned after %d attempts: %s",
                     st.end, st.attempt, reason)

    def commit(self, states):
        """Every write a pass makes, in one place and one record write.

        A completed range keeps being re-aggregated while its attempt files are
        readable, so measurements landing after the Job succeeded are picked up;
        once reaped the recorded entry stands.
        """
        for st in states:
            if st.measured is not None:
                self.progress['completed'][st.end] = st.measured
        for end, created in self.started:
            record.record_start(end, created)
        self.started.clear()
        record.save_progress(self.progress)

    def counts(self, states):
        """The four buckets, which must not overlap.

        `remaining` is work NOT YET DISPATCHED, not work outstanding: a range in
        flight is counted by `running` alone. Overlapping them makes a
        fully-dispatched run of four ranges report four remaining and four
        running, and every consumer that sums the buckets sees eight.
        """
        self._live = {
            'remaining': sum(1 for st in states if st.status == 'pending'),
            'running': sum(1 for st in states if st.holds_capacity),
            'completed': sum(1 for st in states if st.status == 'completed'),
            'condemned': sum(1 for st in states if st.status == 'condemned'),
        }
        return dict(self._live)

    def status(self):
        """What the driver decides on, in both shapes.

        `remaining` is 1 until the first real pass, or a driver polling a fresh
        monitor sees zero and tears the mission down. The legacy keys ship until
        no old driver is left: it reads the counts as ints (absent = 0 = done)
        and iterates jobs_failed (absent = null = throws).
        """
        live = self._live or {'remaining': 1, 'running': 0, 'completed': 0,
                              'condemned': 0}
        condemned = list(self.progress['condemned'].values())
        return dict(live,
                    condemned=condemned,
                    num_remain=live['remaining'],
                    queue_remain_count=live['remaining'],
                    queue_in_progress_count=live['running'],
                    queue_succeeded_count=live['completed'],
                    queue_failed_count=len(condemned),
                    jobs_failed=condemned)


def aggregate(st):
    """One completed range, from its whole attempt chain.

    Every field is advisory -- it sizes a LATER run -- so any may be absent, and
    absent must stay absent. A zero becomes a profile entry, and since a range
    resolves to the nearest measured end ABOVE it, one such entry captures every
    range beneath it and hides the real measurement.
    """
    # Read once per attempt; every field below comes out of this.
    seen = {n: record.read_metrics(st.end, n) for n in range(1, st.attempt + 1)}
    out = {'count': st.count, 'attempts': st.attempt}

    # Every attempt: max is monotone, and no excluded input would be wrong.
    peaks = {}
    for metrics_of in seen.values():
        for key, value in metrics_of.items():
            if key.startswith('peak') and value is not None:
                peaks[key] = max(peaks.get(key, value), value)
    out.update(peaks)

    # Resumed chain only: a fresh retry discarded its predecessor's work.
    chain = _resumed_chain(seen, st.attempt)
    for field, source in (('seconds', 'attemptSeconds'),
                          ('txApply', 'txApplySeconds')):
        legs = [seen[n].get(source) for n in chain]
        if legs and all(leg is not None for leg in legs):
            out[field] = sum(legs)

    wall = _wall_seconds(st)
    if wall is not None:
        out['wallSeconds'] = wall
    return out


def _resumed_chain(seen, attempt):
    """The winning attempt, plus every predecessor it continued from.

    `resumed` sits on the attempt that continued, so it names its own
    predecessor. A fresh start breaks the chain: an attempt that ran new-db
    discarded whatever came before it.
    """
    chain, n = [attempt], attempt
    while n > 1 and seen[n].get('resumed'):
        n -= 1
        chain.append(n)
    return sorted(chain)


def _wall_seconds(st):
    """Attempt 1's dispatch to the winning attempt's completion.

    The range's whole life: every retry, every gap, every wait for a node. Not
    the winning Job's own start, which measures one leg and understates exactly
    the mess this exists to capture -- which is why attempt 1's creation
    timestamp is persisted before its Job can be deleted.
    """
    started = record.started_at(st.end)
    if started is None or st.completed_at is None:
        return None
    return max(0.0, (st.completed_at - started).total_seconds())


if __name__ == '__main__':
    sys.exit(asyncio.run(main()))
