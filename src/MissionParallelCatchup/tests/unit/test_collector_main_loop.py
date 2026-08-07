"""log_collector.main(): which pods get a stream, and when one is let go.

The loop is small and every decision in it has cost a run something: a stream
re-opened every cycle re-read a whole log per pod per POLL_SECONDS; a pod that
left the pod list without ever being observed terminal kept its stream retrying
until the run ended; a sampler placed after the per-pod branches only ever fired
on the cycle a stream opened, when the range had written almost nothing.

Driven by running the real `main()` with `list_pods`, `sample_kubelet` and
`poll_pod` replaced -- those three are the loop's entire outside world -- and
cancelling it once the scenario has played out. Nothing here reads source text:
the previous version of these tests sliced the loop body out with a regex, which
matched the wrong block twice and had to be re-anchored by hand.
"""

import asyncio
import os

import pytest

import config
import log_collector as lc


def pod(name, phase='Running', end='300', attempt='1', node='node-1', ip=None):
    # hostIP is what the sampler reads: it talks to the kubelet directly rather
    # than through the apiserver's node proxy.
    return {'metadata': {'name': name,
                         'labels': {config.LABEL_RUN: config.RUN_NAME,
                                    config.LABEL_RANGE: end,
                                    config.LABEL_ATTEMPT: attempt}},
            'spec': {'nodeName': node},
            'status': {'phase': phase, 'hostIP': ip or f"10.0.0.{abs(hash(node)) % 200 + 1}"}}


class Loop:
    """One scripted run of main(): a pod list per cycle, and what happened.

    Once the script is exhausted the last cycle repeats, so the loop settles
    into a steady state rather than starting to error -- an exception out of
    list_pods is swallowed by main() and would only add noise.

    `poller` picks what the fake stream does: 'wait' blocks on the pod's _wake
    Event and then returns (a normal stream, ended by the pod going away or
    terminal), 'return' finishes immediately (a stream that died early), and
    'hang' ignores the wake and never finishes at all.
    """

    def __init__(self, cycles, poller='wait'):
        self.cycles = list(cycles)
        self.passes = 0
        self.poller = poller
        self.order = []          # 'list' / 'sample' / 'open:<pod>' in sequence
        self.opened = []
        self.sampled = []
        self.done_seen = {}

    async def list_pods(self, session):
        self.order.append('list')
        pods = self.cycles[min(self.passes, len(self.cycles) - 1)]
        self.passes += 1
        return list(pods)

    async def sample_kubelet(self, session, nodes):
        self.order.append('sample')
        self.sampled.append(set(nodes))

    def poll_pod(self, session, name, end, attempt, done, done_ok):
        self.order.append(f"open:{name}")
        self.opened.append((name, end, attempt))

        async def run():
            if self.poller == 'return':
                return
            ev = lc._wake.setdefault(name, asyncio.Event())
            await ev.wait()
            self.done_seen[name] = done(name)
            if self.poller == 'hang':
                await asyncio.Event().wait()      # never finishes

        return run()


@pytest.fixture
def loop_env(tmp_path, monkeypatch):
    monkeypatch.setattr(config, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(lc, 'token', lambda: 'tok')
    monkeypatch.setattr(lc, 'ssl_ctx', lambda: None)
    monkeypatch.setattr(lc, 'POLL_SECONDS', 0.01)
    monkeypatch.setattr(lc, 'VANISHED_GRACE_CYCLES', 3)
    for name in ('_eph_peak', '_anon_peak', '_ws_peak', '_peak_flushed',
                 '_streaming', '_pod_secs', '_wake'):
        monkeypatch.setattr(lc, name, {})
    return monkeypatch


def run_loop(monkeypatch, cycles, poller='wait', extra=2):
    """Run main() over a scripted sequence of pod lists, then stop it."""
    loop = Loop(cycles, poller)
    monkeypatch.setattr(lc, 'list_pods', loop.list_pods)
    monkeypatch.setattr(lc, 'sample_kubelet', loop.sample_kubelet)
    monkeypatch.setattr(lc, 'poll_pod', loop.poll_pod)
    asyncio.run(_drive(loop, extra))
    return loop


async def _drive(loop, extra, want_survivors=False):
    task = asyncio.create_task(lc.main())
    want = len(loop.cycles) + extra
    for _ in range(600):
        await asyncio.sleep(0.005)
        if loop.passes >= want:
            break
    # Stream tasks only. The condemnation watch is also long-lived by design --
    # it is supposed to outlive every poller -- so counting it here would read
    # as a wedged stream that never gave its slot back.
    survivors = [t for t in asyncio.all_tasks()
                 if t is not asyncio.current_task() and t is not task
                 and not t.done()
                 and 'watch_condemnations' not in repr(t.get_coro())]
    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass
    return survivors if want_survivors else None


# --- the sampler ---------------------------------------------------------------

def test_the_sampler_never_delays_opening_a_stream(loop_env):
    """The sampler is a serial sweep of every node's kubelet, and on spot a dead
    one costs the whole connect timeout. Measured at 900 workers it stretched a
    cycle to 925s, and ahead of the per-pod branches that delay applied to every
    stream: five -a2 legs died with no reader, one after 184.7s. Opening a stream
    is time-critical, so it goes first and the sampler takes the wait."""
    loop = run_loop(loop_env, [[pod('w-1')], [pod('w-1')], [pod('w-1')]])

    assert loop.order[:3] == ['list', 'open:w-1', 'sample']
    # Still once per cycle, and still off the same listing rather than its own.
    assert loop.order.count('sample') == loop.order.count('list')


def test_the_sampler_stays_outside_the_per_pod_loop(loop_env):
    """It has to run every cycle, not once per stream. Those branches end in
    `continue` for a pod already streaming, so a sampler placed among them fires
    only on the cycle a stream opens -- when the range has written almost nothing
    and its peak is meaningless."""
    loop = run_loop(loop_env, [[pod('w-1')]] * 4)

    # One sample per listing even though only the first cycle opens anything.
    assert loop.order.count('sample') == loop.order.count('list')
    assert loop.order.count('open:w-1') == 1


def test_the_sampler_runs_every_cycle_not_once_per_stream(loop_env):
    loop = run_loop(loop_env, [[pod('w-1')]] * 4)

    assert len(loop.sampled) >= 3, loop.order
    assert loop.opened == [('w-1', '300', '1')], "the stream was re-opened"


def test_the_sampler_is_not_gated_on_storage_mode(loop_env):
    """It was, back when it only sampled disk. Memory is sized in both modes,
    so gating here left every pvc run with no anon peak at all."""
    loop_env.setattr(config, 'STORAGE_MODE', 'pvc')
    loop = run_loop(loop_env, [[pod('w-1')], [pod('w-1')]])

    assert loop.sampled and loop.sampled[0] == {pod('w-1')['status']['hostIP']}


def test_only_running_pods_are_handed_to_the_sampler(loop_env):
    """kubelet has no live stats for a pod that has not started or has exited,
    and every extra node in the set is another /stats/summary GET."""
    loop = run_loop(loop_env, [[pod('w-1', phase='Pending', node='node-a', ip='10.0.0.1'),
                                pod('w-2', phase='Running', node='node-b', ip='10.0.0.2')]] * 2)

    assert loop.sampled[0] == {'10.0.0.2'}


# --- which pods get a stream ---------------------------------------------------

def test_a_pending_pod_is_not_polled_until_it_can_answer(loop_env):
    """Its container has not started, so the log endpoint answers 400 and the
    poll is wasted -- 60 of 88 failures immediately after the polling switch."""
    loop = run_loop(loop_env, [[pod('w-1', phase='Pending')],
                               [pod('w-1', phase='Pending')],
                               [pod('w-1', phase='Running')],
                               [pod('w-1', phase='Running')]])

    assert loop.opened == [('w-1', '300', '1')]
    # ...and not until the third cycle, the first one it could have answered.
    assert loop.order[:6] == ['list', 'sample', 'list', 'sample',
                              'list', 'open:w-1']


def test_a_terminal_pod_is_still_polled(loop_env):
    """That is where a pod's final output lives; a stream that skipped it
    would lose the medida block of every range that finished quickly."""
    loop = run_loop(loop_env, [[pod('w-1', phase='Succeeded')]] * 2)

    assert loop.opened == [('w-1', '300', '1')]


def test_a_pod_with_no_range_label_is_not_ours(loop_env):
    stray = pod('other-1')
    del stray['metadata']['labels'][config.LABEL_RANGE]
    loop = run_loop(loop_env, [[stray]] * 2)

    assert loop.opened == []


def test_a_finished_stream_is_not_reopened_once_its_pod_is_terminal(loop_env):
    """A completed task is deleted from `tasks`, so without a record of it the
    next cycle re-creates the stream and re-reads the whole log -- every cycle,
    per pod, for the rest of the run."""
    loop = run_loop(loop_env, [[pod('w-1', phase='Succeeded')]] * 5)

    assert loop.opened == [('w-1', '300', '1')], \
        f"stream re-opened {len(loop.opened)} times"


def test_a_stream_that_died_while_its_pod_still_runs_is_reopened(loop_env):
    """The other half of the same guard. A task that ended while the pod is
    still Running died early, and re-opening the stream is how that recovers --
    barring it would abandon a live range."""
    loop = run_loop(loop_env, [[pod('w-1', phase='Running')]] * 5,
                    poller='return')

    assert len(loop.opened) >= 2, "an early-dying stream was never retried"


# --- a pod that leaves the list -----------------------------------------------

def test_a_vanished_pod_is_marked_terminal_and_wakes_its_poller(loop_env):
    """`terminal` is only written for pods in the pod list, so a pod that goes
    away without ever being seen terminal -- reaped node, eviction, or the
    monitor deleting its finished Job -- keeps done() False forever. Gone is
    terminal, and the wake is what stops the poller sleeping out its interval
    before it takes the 404 and writes the .done the monitor waits on."""
    loop = run_loop(loop_env, [[pod('w-1')], [pod('w-1')], [], [], []])

    assert loop.done_seen.get('w-1') is True, \
        "the poller was never woken, or woke to done() == False"


def test_a_pod_going_terminal_wakes_its_poller_without_waiting_a_cycle(loop_env):
    """The delay that matters is between the container exiting and the last
    read: sleeping blind hands that window to a spot reclaim, which deletes the
    pod and takes the final lines with it."""
    loop = run_loop(loop_env, [[pod('w-1', phase='Running')],
                               [pod('w-1', phase='Succeeded')],
                               [pod('w-1', phase='Succeeded')]])

    assert loop.done_seen.get('w-1') is True


def test_a_stream_that_will_not_finish_is_cancelled_after_the_grace(loop_env):
    """Marking a vanished pod terminal is not enough on its own: a stream
    wedged inside a connection attempt never reaches its own done() check, and
    that is exactly the state that starves every other stream of a poll slot."""
    loop = Loop([[pod('w-1')], [pod('w-1')], [], [], [], [], []], poller='hang')
    loop_env.setattr(lc, 'list_pods', loop.list_pods)
    loop_env.setattr(lc, 'sample_kubelet', loop.sample_kubelet)
    loop_env.setattr(lc, 'poll_pod', loop.poll_pod)

    alive = asyncio.run(_drive(loop, extra=3, want_survivors=True))

    assert alive == [], "a wedged stream outlived its grace and held its slot"
    assert os.path.exists(lc.done_path('300', '1')), \
        "forced cancellation skipped finalization and never licensed cleanup"


def test_the_grace_is_more_than_one_cycle():
    """A stream still finalizing -- writing .metrics, closing its archive --
    must not be cancelled out from under its own write."""
    assert lc.VANISHED_GRACE_CYCLES >= 2, \
        (f"a grace of {lc.VANISHED_GRACE_CYCLES} cycle(s) can cancel a stream "
         "in the middle of finalizing itself")
