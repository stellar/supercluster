"""sample_kubelet: what one /stats/summary payload is allowed to become.

The sampler is the only source of every memory figure the profile uses. It runs
against a payload this mission does not control, on pods that may be seconds
old, in a process that can be restarted mid-range -- so most of what it does is
refuse to record something.

Driven through the real `log_collector.sample_kubelet` with a fake session, and
asserted on the module's own peak dicts and on the bytes that reach the volume.
An earlier generation of these tests exec'd the function body out of the source
with a hand-built namespace; the point of that was to survive an unimportable
module, and the module imports.
"""

import asyncio

import pytest

import job_monitor as jm
import log_collector as lc

MIB = 1024 ** 2


@pytest.fixture
def sampler(tmp_path, monkeypatch):
    """A collector with no memory, writing to a disposable volume."""
    monkeypatch.setattr(lc, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(jm, 'LOG_DIR', str(tmp_path))
    monkeypatch.setattr(lc, 'token', lambda: 'tok')
    monkeypatch.setattr(lc, 'STORAGE_MODE', 'ephemeral')
    for name in ('_eph_peak', '_anon_peak', '_ws_peak', '_peak_flushed',
                 '_streaming', '_pod_secs', '_wake'):
        monkeypatch.setattr(lc, name, {})
    return tmp_path


class _Resp:
    def __init__(self, payload):
        self._payload = payload

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False

    def raise_for_status(self):
        pass

    async def json(self):
        return self._payload


class _Session:
    def __init__(self, payload):
        self.payload = payload

    def get(self, url, **kw):
        return _Resp(self.payload)


def container(name=None, rss=None, ws=None):
    mem = {}
    if rss is not None:
        mem['rssBytes'] = rss
    if ws is not None:
        mem['workingSetBytes'] = ws
    return {'name': name or lc.CONTAINER, 'memory': mem}


def payload(pod, containers, eph=None):
    entry = {'podRef': {'name': pod}, 'containers': containers}
    if eph is not None:
        entry['ephemeral-storage'] = {'usedBytes': eph}
    return {'pods': [entry]}


def sample(doc):
    asyncio.run(lc.sample_kubelet(_Session(doc), ['node-1']))


# --- a peak is a high-water mark, not the latest reading ---------------------

def test_a_later_lower_sample_never_lowers_the_peak(sampler):
    """Catching the spike is the whole point, and download-phase anon
    oscillates: the sampler is what turns a series of readings into one
    number, so last-wins here defeats every consumer downstream."""
    sample(payload('w-1', [container(rss=900 * MIB)], eph=5))
    sample(payload('w-1', [container(rss=400 * MIB)], eph=2))

    assert lc._anon_peak == {'w-1': 900 * MIB}
    assert lc._eph_peak == {'w-1': 5}


def test_a_higher_sample_still_raises_it(sampler):
    sample(payload('w-1', [container(rss=400 * MIB)], eph=2))
    sample(payload('w-1', [container(rss=900 * MIB)], eph=5))

    assert lc._anon_peak == {'w-1': 900 * MIB}
    assert lc._eph_peak == {'w-1': 5}


# --- what must not be recorded -----------------------------------------------

def test_a_container_without_stats_yet_is_skipped_not_zeroed(sampler):
    """rssBytes is absent for the first seconds of a container's life, before
    cAdvisor has stats for it. Recording 0 would poison the peak for a range
    that is about to be measured properly, and raising would kill the sampler
    for every other pod on the node."""
    sample(payload('w-1', [container(rss=None)], eph=7))

    assert lc._anon_peak == {}, "a missing rssBytes was recorded anyway"
    assert lc._eph_peak == {'w-1': 7}, "the disk axis stopped being sampled"


def test_only_the_worker_container_is_measured(sampler):
    """Sidecars share the pod. Summing across containers, or letting the last
    one win, would size the range from whichever one kubelet listed last."""
    sample(payload('w-1', [container(name='istio-proxy', rss=900 * MIB)]))

    assert lc._anon_peak == {}


def test_the_worker_is_found_however_kubelet_orders_the_containers(sampler):
    sample(payload('w-1', [container(name='istio-proxy', rss=900 * MIB),
                           container(rss=222 * MIB)]))

    assert lc._anon_peak == {'w-1': 222 * MIB}


def test_a_pod_with_no_name_is_skipped_rather_than_keyed_on_none(sampler):
    asyncio.run(lc.sample_kubelet(_Session({'pods': [{'podRef': {},
                                                      'containers': []}]}),
                                 ['node-1']))

    assert lc._anon_peak == {} and lc._eph_peak == {}


def test_an_unreachable_kubelet_costs_the_sample_not_the_sampler(sampler):
    """The axis going quiet must not look like "this range used nothing"; the
    next node in the list still has to be visited."""
    class _Boom:
        def get(self, url, **kw):
            raise OSError('connection refused')

    asyncio.run(lc.sample_kubelet(_Boom(), ['node-1']))     # must not raise

    assert lc._anon_peak == {}


# --- flushing to the volume ---------------------------------------------------

def _writes(monkeypatch):
    seen = []
    real = lc.write_metrics

    def spy(end, attempt, values):
        seen.append((end, attempt, dict(values)))
        return real(end, attempt, values)

    monkeypatch.setattr(lc, 'write_metrics', spy)
    return seen


def test_a_peak_that_barely_grows_is_not_reflushed(sampler, monkeypatch):
    """One write per sample per pod, at 2048 pods, would be the dominant cost
    of the sampler. Only growth past PEAK_FLUSH_RATIO earns a write."""
    seen = _writes(monkeypatch)
    lc._streaming['w-1'] = ('300', '1')

    sample(payload('w-1', [container(rss=900 * MIB)]))
    sample(payload('w-1', [container(rss=910 * MIB)]))
    assert len(seen) == 1, f"a 1.1% rise triggered a second flush: {seen}"

    sample(payload('w-1', [container(rss=2000 * MIB)]))
    assert len(seen) == 2, "a 2.2x rise did not flush"
    assert seen[-1][2] == {'peakAnonBytes': 2000 * MIB}


def test_an_in_flight_peak_reaches_the_volume_before_the_stream_ends(sampler):
    """Prometheus computed max_over_time server-side and needed no state. A
    local high-water dict does: without the flush, a collector restart resets a
    range's peak to whatever it is using at that moment, which under-reports and
    sizes the next run too small."""
    lc._streaming['w-1'] = ('300', '1')
    sample(payload('w-1', [container(rss=900 * MIB)]))

    assert jm.peaks_for_range('300', 1) == {'peakAnonBytes': 900 * MIB}


def test_a_peak_sampled_before_stream_registration_is_flushed_on_open(sampler):
    """main samples first, then opens new pollers; a restart between those steps
    must not make that first high-water process-memory-only."""
    sample(payload('w-1', [container(rss=900 * MIB, ws=1200 * MIB)]))
    assert jm.peaks_for_range('300', 1) == {}

    lc._register_stream('w-1', '300', '1')

    assert jm.peaks_for_range('300', 1) == {
        'peakAnonBytes': 900 * MIB,
        'peakWorkingSetBytes': 1200 * MIB,
    }


def test_the_disk_axis_stays_mode_gated(sampler, monkeypatch):
    """ephemeral-storage is meaningless in pvc mode: /data is on the volume,
    not on the node."""
    monkeypatch.setattr(lc, 'STORAGE_MODE', 'pvc')
    lc._streaming['w-1'] = ('300', '1')

    sample(payload('w-1', [container(rss=900 * MIB)], eph=34 * 1024 ** 3))

    assert lc._eph_peak == {}
    assert lc._anon_peak == {'w-1': 900 * MIB}, \
        "memory sizing is not mode-specific and must be sampled in both"


# --- working set: sampled, recorded, never used to size anything -------------

def test_working_set_is_sampled_alongside_anon(sampler):
    """It is what kubelet ranks node-pressure evictions on, so it explains an
    eviction that rss cannot. Measured on ssc-test for one 420-ledger range:
    working set read 3.61 / 7.48 / 13.49 GiB under 4Gi / 8Gi / 24000Mi limits
    while rss held flat at ~2.4 GiB -- which is exactly why it is a diagnostic
    and never a request."""
    sample(payload('w-1', [container(rss=900 * MIB, ws=4096 * MIB)]))

    assert lc._ws_peak == {'w-1': 4096 * MIB}
    assert lc._anon_peak == {'w-1': 900 * MIB}


def test_finalize_records_the_working_set_peak(sampler):
    """Sampling it is useless if finalize drops it on the floor."""
    sample(payload('w-1', [container(rss=900 * MIB, ws=4096 * MIB)]))
    asyncio.run(lc.finalize(None, 'w-1', '300', 1, lc.TxApplyScanner(),
                            lambda p: True))

    stored = jm.peaks_for_range('300', 1)
    assert stored['peakWorkingSetBytes'] == 4096 * MIB
    assert stored['peakAnonBytes'] == 900 * MIB


# --- resume is bookkeeping finalize has to carry ------------------------------

def test_finalize_records_that_an_attempt_resumed(sampler):
    """Without this in .metrics, peaks_for_range cannot tell a resumed tail
    from a complete pass, and every resumed range is profiled off its tail."""
    tx = lc.TxApplyScanner()
    tx.feed("RESUME: 300/16320 reached ledger 299, replay had started")
    asyncio.run(lc.finalize(None, 'w-1', '300', 1, tx, lambda p: True))

    assert jm._attempt_resumed('300', 1) is True


def test_a_fresh_attempt_is_never_marked_resumed(sampler):
    asyncio.run(lc.finalize(None, 'w-1', '300', 1, lc.TxApplyScanner(),
                            lambda p: True))

    assert jm._attempt_resumed('300', 1) is False
