"""The collector sidecar restarts independently of the monitor.

log_collector holds every peak it measures in module-level dicts, keyed by pod
name, and writes them to the shared volume. Those dicts do not survive an
OOM-kill of the sidecar, but the files on the volume do -- and the pod they
describe keeps running. So every durable write has to assume the process that
made the previous one is gone and that whatever is already on disk was measured
by someone who saw more than this process did.

Two failures of that contract were found by hand before: a restart reset a
range's duration clock so attemptSeconds recorded 0.2s, and a newest-wins write
LOWERED an already-recorded peak. Both are pinned here.

Everything is asserted against the bytes on the shared volume, read back either
with json.load or -- better -- through job_monitor's own readers, which are the
real consumer. Nothing here reads the collector's source.

No reconcile: the volume is the entire interface between the two processes, so
these drive log_collector's file-writing entry points directly.
"""

import asyncio
import gzip
import json
import os

import pytest

import job_monitor as jm
import log_collector as lc

GIB = 1073741824


# -- the shared volume, and a collector with no memory of anything ------------

@pytest.fixture
def vol(tmp_path, monkeypatch):
    """A shared /logs both processes agree on, and cleared collector state.

    Every module-level dict log_collector keeps is replaced, not emptied: they
    are process state, and a test that inherited another test's pod entries
    would be measuring the wrong process.
    """
    log_dir = tmp_path / 'logs'
    log_dir.mkdir()
    monkeypatch.setattr(lc, 'LOG_DIR', str(log_dir))
    # The monitor reads the same directory off its own module global.
    monkeypatch.setattr(jm, 'LOG_DIR', str(log_dir))
    restart(monkeypatch)
    monkeypatch.setattr(lc, '_pod_secs', {})
    monkeypatch.setattr(lc, '_wake', {})
    monkeypatch.setattr(lc, 'token', lambda: 'test-token')
    return log_dir


def restart(monkeypatch):
    """Wipe exactly what an OOM-kill of the sidecar wipes: its memory.

    The volume is untouched, which is the whole point -- a restarted collector
    starts every high-water at zero while the file on disk still holds the real
    one.
    """
    for name in ('_eph_peak', '_anon_peak', '_ws_peak', '_peak_flushed', '_streaming'):
        if hasattr(lc, name):
            monkeypatch.setattr(lc, name, {})
    # Added by the ephemeral-flush fix; absent on builds without it.
    if hasattr(lc, '_eph_flushed'):
        monkeypatch.setattr(lc, '_eph_flushed', {})


def metrics(end, attempt=1):
    """What the monitor would find in .metrics, or None if there is no file."""
    try:
        with open(jm.metrics_path(str(end), attempt)) as fh:
            return json.load(fh)
    except OSError:
        return None


def run(coro):
    return asyncio.run(coro)


# -- a kubelet /stats/summary that says whatever the test needs ---------------

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


class FakeSession:
    """Stands in for the aiohttp session sample_kubelet fetches through."""

    def __init__(self, payload):
        self.payload = payload
        self.urls = []

    def get(self, url, **kwargs):
        self.urls.append(url)
        return _Resp(self.payload)


def summary(pod, rss=None, ws=None, eph=None, container=None):
    """One node's stats/summary, shaped the way kubelet shapes it."""
    entry = {'podRef': {'name': pod}, 'containers': []}
    if eph is not None:
        entry['ephemeral-storage'] = {'usedBytes': eph}
    mem = {}
    if rss is not None:
        mem['rssBytes'] = rss
    if ws is not None:
        mem['workingSetBytes'] = ws
    entry['containers'].append({'name': container or lc.CONTAINER, 'memory': mem})
    return {'pods': [entry]}


def sample(pod, **kw):
    """One real sample_kubelet pass over one node."""
    run(lc.sample_kubelet(FakeSession(summary(pod, **kw)), ['node-1']))


def finalize(pod, end, attempt=1, succeeded=False, started=None, tx=None):
    """One real finalize() for an attempt, as its poller would call it."""
    return run(lc.finalize(None, pod, str(end), attempt,
                           tx if tx is not None else lc.TxApplyScanner(),
                           lambda p: succeeded, started))


# -- a peak may never go backwards -------------------------------------------

@pytest.mark.parametrize('key', lc.PEAK_KEYS)
def test_a_later_lower_write_cannot_lower_a_recorded_peak(vol, key):
    """Every field in PEAK_KEYS, not just the one that was reported.

    This is the restarted-poller case reduced to its file operation: the second
    write is a fresh process's first flush, and it is smaller because that
    process started counting at zero.
    """
    lc.write_metrics('300', 1, {key: 8 * GIB})
    lc.write_metrics('300', 1, {key: 1 * GIB})

    assert metrics(300)[key] == 8 * GIB


@pytest.mark.parametrize('key', lc.PEAK_KEYS)
def test_a_later_higher_write_still_raises_the_peak(vol, key):
    """The guard must not be a write-once latch: growth is the normal case."""
    lc.write_metrics('300', 1, {key: 1 * GIB})
    lc.write_metrics('300', 1, {key: 8 * GIB})

    assert metrics(300)[key] == 8 * GIB


def test_a_write_that_omits_a_peak_leaves_it_alone(vol):
    """finalize writes only the axes it has. The rest are already on disk."""
    lc.write_metrics('300', 1, {'peakAnonBytes': 5 * GIB,
                                'peakEphemeralBytes': 30 * GIB})
    lc.write_metrics('300', 1, {'txApplySeconds': 12.5})

    stored = metrics(300)
    assert stored['peakAnonBytes'] == 5 * GIB
    assert stored['peakEphemeralBytes'] == 30 * GIB
    assert stored['txApplySeconds'] == 12.5


def test_resumed_true_is_monotonic_across_restarted_writers(vol):
    lc.write_metrics('300', 2, {'resumed': True})
    lc.write_metrics('300', 2, {'resumed': False, 'attemptSeconds': 10.0})

    assert metrics(300, 2)['resumed'] is True


def test_finalize_recovers_resume_after_the_scanner_is_recreated(vol):
    """The first poll saw RESUME, then its scanner vanished before finalize."""
    path = lc.base('300', 2) + '.log.gz'
    with gzip.open(path, 'wt') as fh:
        fh.write('RESUME: local state reached ledger 250; skipping new-db\n')

    finalize('w-300-a2', 300, attempt=2, tx=lc.TxApplyScanner())

    assert metrics(300, 2)['resumed'] is True


def test_finalize_recovers_txapply_after_the_scanner_is_recreated(vol, monkeypatch):
    """The first poll saw the final medida block, then its scanner vanished."""
    monkeypatch.setattr(lc, 'SAVE_SUCCESS_LOGS', False)
    path = lc.base('300', 2) + '.log.gz'
    with gzip.open(path, 'wt') as fh:
        fh.write('RESUME: local state reached ledger 250; skipping new-db\n')
        fh.write("metric 'ledger.transaction.apply'\n")
        fh.write('  count = 123\n')
        fh.write('  sum = 4200.0ms\n')

    finalize('w-300-a2', 300, attempt=2, succeeded=True,
             tx=lc.TxApplyScanner(recreated=True))

    assert metrics(300, 2)['resumed'] is True
    assert metrics(300, 2)['txApplySeconds'] == 4.2
    assert not os.path.exists(path), \
        "the test must prove recovery happened before success-log discard"


def test_finalize_does_not_promote_resume_declined(vol):
    path = lc.base('300', 2) + '.log.gz'
    with gzip.open(path, 'wt') as fh:
        fh.write('RESUME DECLINED: no usable local state; running new-db\n')

    finalize('w-300-a2', 300, attempt=2, tx=lc.TxApplyScanner())

    assert (metrics(300, 2) or {}).get('resumed') is not True


def test_peaks_from_different_writes_accumulate_into_one_record(vol):
    """Each axis is flushed by whoever measured it; the file is the union."""
    lc.write_metrics('300', 1, {'peakAnonBytes': 5 * GIB})
    lc.write_metrics('300', 1, {'peakWorkingSetBytes': 9 * GIB})
    lc.write_metrics('300', 1, {'peakEphemeralBytes': 30 * GIB})

    assert metrics(300) == {'peakAnonBytes': 5 * GIB,
                            'peakWorkingSetBytes': 9 * GIB,
                            'peakEphemeralBytes': 30 * GIB}


# -- mid-flight flushes, and what a restart may lose --------------------------

def test_a_midflight_anon_flush_survives_a_collector_restart(vol, monkeypatch):
    """The pinned bug, driven through the real sampler.

    A long range peaks early (download and bucket-apply), the sidecar is
    OOM-killed, and the replacement watches only the quiet replay tail. What
    the range gets sized on next run must still be the high-water.
    """
    lc._streaming['w-300'] = ('300', '1')
    sample('w-300', rss=6 * GIB)
    assert metrics(300)['peakAnonBytes'] == 6 * GIB, "flush never reached the volume"

    restart(monkeypatch)
    lc._streaming['w-300'] = ('300', '1')
    sample('w-300', rss=1 * GIB)
    finalize('w-300', 300)

    assert metrics(300)['peakAnonBytes'] == 6 * GIB
    # And the consumer agrees: this is the figure that sizes the next run.
    assert jm.peaks_for_range('300', 1)['peakAnonBytes'] == 6 * GIB


def test_a_midflight_ephemeral_flush_survives_a_collector_restart(vol, monkeypatch):
    """peakEphemeralBytes sizes an ephemeral-storage request, and a request
    that comes back too small is an eviction, not a slow range.

    Disk use is not monotonic -- stellar-core drops its download staging once
    buckets are applied -- so a replacement sidecar re-measuring the same pod
    does not recover the earlier high-water. It has to already be on the volume.
    """
    monkeypatch.setattr(lc, 'STORAGE_MODE', 'ephemeral')
    lc._streaming['w-300'] = ('300', '1')
    sample('w-300', rss=1 * GIB, eph=34 * GIB)

    restart(monkeypatch)
    monkeypatch.setattr(lc, 'STORAGE_MODE', 'ephemeral')
    lc._streaming['w-300'] = ('300', '1')
    sample('w-300', rss=1 * GIB, eph=4 * GIB)
    finalize('w-300', 300)

    assert metrics(300)['peakEphemeralBytes'] == 34 * GIB
    assert jm.peaks_for_range('300', 1)['peakEphemeralBytes'] == 34 * GIB


def test_pvc_mode_records_no_ephemeral_peak_at_all(vol, monkeypatch):
    """In pvc mode the range's data sits on the volume, not on node disk, so
    there is no ephemeral-storage request to size and the figure would be
    noise. Sampling it is gated on the mode; flushing it must be too."""
    monkeypatch.setattr(lc, 'STORAGE_MODE', 'pvc')
    lc._streaming['w-300'] = ('300', '1')
    sample('w-300', rss=1 * GIB, eph=34 * GIB)
    finalize('w-300', 300)

    assert 'peakEphemeralBytes' not in (metrics(300) or {})


def test_a_flush_with_no_stream_registered_writes_nothing(vol, monkeypatch):
    """_streaming is repopulated when a poller opens. A sample that lands on a
    pod with no poller yet has nowhere to write and must not guess a file."""
    monkeypatch.setattr(lc, 'STORAGE_MODE', 'ephemeral')
    sample('w-300', rss=6 * GIB, eph=34 * GIB)

    assert os.listdir(vol) == []


def test_finalize_cannot_lower_a_peak_the_volume_already_holds(vol, monkeypatch):
    """The restart case at the level of finalize itself.

    Whatever is in the replacement process's dicts is a partial observation;
    the file was written by a process that saw more.
    """
    lc.write_metrics('300', 1, {'peakAnonBytes': 6 * GIB,
                                'peakWorkingSetBytes': 11 * GIB,
                                'peakEphemeralBytes': 34 * GIB})
    lc._anon_peak['w-300'] = 1 * GIB
    lc._ws_peak['w-300'] = 2 * GIB
    lc._eph_peak['w-300'] = 3 * GIB

    finalize('w-300', 300)

    stored = metrics(300)
    assert stored['peakAnonBytes'] == 6 * GIB
    assert stored['peakWorkingSetBytes'] == 11 * GIB
    assert stored['peakEphemeralBytes'] == 34 * GIB


def test_the_flush_ratio_does_not_hold_back_the_first_measurement(vol):
    """A restarted sampler has flushed nothing, so its first sample must land
    on the volume immediately -- otherwise a pod that peaks once and then dies
    contributes nothing at all."""
    lc._streaming['w-300'] = ('300', '1')
    sample('w-300', rss=3 * GIB)

    assert metrics(300)['peakAnonBytes'] == 3 * GIB


def test_a_flush_goes_to_the_attempt_that_is_streaming(vol):
    """Peaks are keyed by (range, attempt); a retry must not inherit them."""
    lc._streaming['w-300-a2'] = ('300', '2')
    sample('w-300-a2', rss=7 * GIB)

    assert metrics(300, 2)['peakAnonBytes'] == 7 * GIB
    assert metrics(300, 1) is None


# -- .done is a promise about .metrics ----------------------------------------

def test_done_never_appears_beside_a_half_written_metrics_file(vol, monkeypatch):
    """The monitor reaps the Job -- and with it the pod -- the moment .done
    exists. If .metrics can be observed mid-write, that reap makes a torn
    record permanent."""
    lc.write_metrics('300', 1, {'peakAnonBytes': 6 * GIB, 'txApplySeconds': 30.0})

    real_dump = lc.json.dump
    seen = {}

    def dump_then_die(obj, fh, *a, **kw):
        # A write that dies with the file open: the failure mode the .tmp +
        # rename is there for.
        fh.write(json.dumps(obj)[:12])
        seen['torn'] = True
        raise OSError(28, 'No space left on device')

    monkeypatch.setattr(lc.json, 'dump', dump_then_die)
    lc._anon_peak['w-300'] = 9 * GIB
    finalize('w-300', 300)
    monkeypatch.setattr(lc.json, 'dump', real_dump)

    assert seen['torn'], "the interrupted write never happened"
    # The old record is intact and parseable -- not truncated, not empty.
    assert metrics(300) == {'peakAnonBytes': 6 * GIB, 'txApplySeconds': 30.0}
    # .done still lands: the collector really will write nothing more for this
    # attempt, and withholding it only strands the Job until its TTL.
    assert os.path.exists(jm.done_path('300', 1))
    # What the monitor actually reads is a complete record, not a torn one.
    assert jm.peaks_for_range('300', 1) == {'peakAnonBytes': 6 * GIB}


def test_done_lands_after_the_metrics_it_promises(vol):
    """Ordering, observed by mtime rather than by reading the source."""
    lc._anon_peak['w-300'] = 6 * GIB
    finalize('w-300', 300)

    assert (os.stat(jm.done_path('300', 1)).st_mtime_ns
            >= os.stat(jm.metrics_path('300', 1)).st_mtime_ns)
    assert metrics(300)['peakAnonBytes'] == 6 * GIB


def test_a_truncated_metrics_file_does_not_poison_the_next_write(vol):
    """Whatever tore the previous record, the next flush must still produce a
    file the monitor can read -- and must not raise inside the sampler."""
    with open(jm.metrics_path('300', 1), 'w') as fh:
        fh.write('{"peakAnonBytes": 644245')

    lc.write_metrics('300', 1, {'peakAnonBytes': 5 * GIB})

    assert metrics(300) == {'peakAnonBytes': 5 * GIB}
    assert jm.peaks_for_range('300', 1) == {'peakAnonBytes': 5 * GIB}


def test_an_attempt_with_nothing_to_report_still_finalizes(vol):
    """No peaks, no duration, no txApply -- a pod rejected before its container
    ran. .done has to land anyway or the monitor waits out JOB_TTL_SECONDS on a
    Job that will never learn anything."""
    finalize('w-300', 300)

    assert metrics(300) is None
    assert jm._attempt_finalized('300', 1)


def test_marking_done_twice_is_harmless(vol):
    lc._mark_done('300', 1)
    lc._mark_done('300', 1)

    assert os.path.exists(jm.done_path('300', 1))
    assert jm._attempt_finalized('300', 1)


# -- finalizing the same attempt twice ----------------------------------------

def test_finalizing_the_same_attempt_twice_keeps_its_measurements(vol, monkeypatch):
    """A restarted collector re-opens a poller for a pod that is still there
    and still terminal, and finalizes it a second time. The second pass
    measured nothing -- sample_kubelet only samples Running pods -- so it must
    add nothing and take nothing away."""
    lc._pod_secs['w-300'] = 3600.4
    lc._anon_peak['w-300'] = 6 * GIB
    lc._eph_peak['w-300'] = 34 * GIB
    tx = lc.TxApplyScanner()
    tx.seconds = 120.0
    finalize('w-300', 300, tx=tx)
    first = metrics(300)
    assert first['attemptSeconds'] == 3600.4
    assert first['attemptSecondsExact'] is True

    restart(monkeypatch)
    # The main loop re-reads the pod's own timestamps every cycle it sees it
    # terminal, so the second poller gets the same exact figure.
    lc._pod_secs['w-300'] = 3600.4
    finalize('w-300', 300)

    assert metrics(300) == first


def test_a_second_finalize_without_pod_timestamps_keeps_the_real_duration(vol,
                                                                          monkeypatch):
    """attemptSeconds is a fixed quantity measured two ways, and both are lower
    bounds: the pod's own start->finish is exact, while the poller's watch time
    covers only the part of the attempt this process was alive for. A second
    finalize that has lost the pod object -- 404 on the log endpoint, node
    already reaped -- may only ever offer the worse of the two, so it must not
    replace the better one.

    This is the same fabricated near-zero duration that was found by hand,
    reached from the reopen path rather than from a cold start.
    """
    lc._pod_secs['w-300'] = 3600.4
    finalize('w-300', 300)
    assert metrics(300)['attemptSeconds'] == 3600.4

    restart(monkeypatch)
    # No _pod_secs: this poller never saw the pod terminal, it just took a 404.
    # `started` is when IT attached, which is a moment ago.
    finalize('w-300', 300, started=_moments_ago())

    assert metrics(300)['attemptSeconds'] == 3600.4


def _moments_ago():
    """A `started` stamp on the same monotonic clock finalize reads."""
    async def now():
        return asyncio.get_event_loop().time()
    return run(now())


def test_a_cold_poller_on_an_already_terminal_pod_reports_no_duration(vol):
    """The other half of the pinned duration bug: with no pod timestamps and
    no start of its own, the collector reports nothing rather than a
    fabricated near-zero. The monitor's own figure is authoritative."""
    lc._anon_peak['w-300'] = 6 * GIB
    finalize('w-300', 300, started=None)

    stored = metrics(300)
    assert 'attemptSeconds' not in stored
    assert stored['peakAnonBytes'] == 6 * GIB
    # ...and the monitor is left free to supply the real one.
    assert jm.seconds_for_range('300', 1, final=3600.4) == 3600.4


def test_a_poller_that_watched_the_whole_attempt_still_reports_its_duration(vol):
    """The fallback is not disabled, only outranked."""
    started = _moments_ago() - 42.0
    finalize('w-300', 300, started=started)

    stored = metrics(300)
    assert stored['attemptSeconds'] == pytest.approx(42.0, abs=1.0)
    assert stored['attemptSecondsExact'] is False
    assert jm.seconds_for_range('300', 1) is None


def test_the_duration_the_collector_records_is_the_pods_not_the_pollers(vol):
    """_pod_secs is the pod's own start->finish and always wins."""
    lc._pod_secs['w-300'] = 3600.4
    finalize('w-300', 300, started=_moments_ago() - 5.0)

    assert metrics(300)['attemptSeconds'] == 3600.4
    assert metrics(300)['attemptSecondsExact'] is True


# -- .outcome is written once, by whoever got there first ---------------------

def _pod(name, phase='Failed', exit_code=None, reason=None, message=None,
         disrupted=False):
    status = {'phase': phase}
    if reason:
        status['reason'] = reason
    if message:
        status['message'] = message
    if disrupted:
        status['conditions'] = [{'type': 'DisruptionTarget', 'status': 'True'}]
    if exit_code is not None:
        status['containerStatuses'] = [
            {'name': lc.CONTAINER, 'state': {'terminated': {'exitCode': exit_code}}}]
    return {'metadata': {'name': name, 'labels': {}}, 'status': status}


def test_an_existing_outcome_is_not_overwritten_by_a_later_pod(vol):
    """Two pods can carry the same range-end and attempt labels -- a Job that
    replaces its pod, or a stale pod list after a restart. The first verdict is
    the one taken while the evidence was fresh; a later, different pod must not
    silently rewrite it."""
    lc.record_outcome(_pod('w-300-first', disrupted=True), '300', 1)
    first = jm.read_outcome('300', 1)

    lc.record_outcome(_pod('w-300-second', exit_code=1), '300', 1)

    assert jm.read_outcome('300', 1) == first
    assert first['outcome'] == 'disrupted'
    assert first['pod'] == 'w-300-first'


def test_an_outcome_written_by_the_monitor_is_not_re_classified(vol, monkeypatch):
    """Both processes write this file and both read it. The collector must
    treat the monitor's verdict as final, including the fields only the monitor
    records -- attemptSeconds for a failed leg lives nowhere else."""
    with open(jm.outcome_path('300', 1), 'w') as fh:
        json.dump({'outcome': 'ephemeral', 'exitCode': None, 'pod': 'w-300',
                   'attemptSeconds': 1800.0}, fh)

    lc.record_outcome(_pod('w-300', exit_code=3), '300', 1)

    assert jm.read_outcome('300', 1)['outcome'] == 'ephemeral'
    assert jm.read_outcome('300', 1)['attemptSeconds'] == 1800.0


def test_a_recorded_outcome_is_a_complete_file_or_no_file(vol, monkeypatch):
    """Same rename discipline as .metrics: the monitor branches its whole retry
    policy on this file, so a torn read would have to be a crash or a wrong
    verdict."""
    def dump_then_die(obj, fh, *a, **kw):
        fh.write(json.dumps(obj)[:9])
        raise OSError(28, 'No space left on device')

    monkeypatch.setattr(lc.json, 'dump', dump_then_die)
    lc.record_outcome(_pod('w-300', exit_code=1), '300', 1)

    assert jm.read_outcome('300', 1) is None
    assert not os.path.exists(jm.outcome_path('300', 1))


def test_an_ephemeral_eviction_is_classified_from_the_pod_message(vol):
    """The exit code cannot tell this apart from a catchup failure, and only
    the pod carries the discriminator -- so if the collector misses it while
    the pod exists, it is gone."""
    lc.record_outcome(
        _pod('w-300', exit_code=3, reason='Evicted',
             message='Pod ephemeral local storage usage exceeds the total limit '
                     'of containers 40Gi'),
        '300', 1)

    assert jm.read_outcome('300', 1)['outcome'] == 'ephemeral'


# -- the resume state file ----------------------------------------------------

def test_state_survives_a_restart_and_untimestamped_junk_never_becomes_it(vol):
    """The resume point is read back by a process that did not write it, so a
    poisoned value is permanent: sinceTime=unableZ is a 400 on every later
    request for that pod, forever."""
    lc.write_state('300', 1, '2026-07-30T10:15:30.123456789Z')
    assert lc.read_state('300', 1) == '2026-07-30T10:15:30.123456789Z'

    lc.write_state('300', 1, 'unable')
    assert lc.read_state('300', 1) is None


def test_an_empty_state_claim_is_not_a_resume_point(vol):
    """poll_pod writes '' to claim the range against job_monitor's backstop.
    That is a claim, not a timestamp, and must never be sent as sinceTime."""
    lc.write_state('300', 1, '')

    assert lc.read_state('300', 1) is None
    assert os.path.exists(lc.base('300', 1) + '.state')


def test_discarding_a_successful_range_keeps_its_measurements(vol):
    """saveSuccessLogs=false deletes the archive. .metrics is the only place
    txApply and the peaks survive a reaped pod, so it has to stay."""
    lc.write_metrics('300', 1, {'peakAnonBytes': 6 * GIB, 'txApplySeconds': 30.0})
    with open(lc.base('300', 1) + '.log.gz', 'wb') as fh:
        fh.write(b'\x1f\x8b')
    lc.write_state('300', 1, '2026-07-30T10:15:30Z')

    lc.discard('300', 1)

    assert not os.path.exists(lc.base('300', 1) + '.log.gz')
    assert metrics(300) == {'peakAnonBytes': 6 * GIB, 'txApplySeconds': 30.0}


def test_a_successful_range_discards_its_archive_inside_finalize(vol, monkeypatch):
    monkeypatch.setattr(lc, 'SAVE_SUCCESS_LOGS', False)
    with open(lc.base('300', 1) + '.log.gz', 'wb') as fh:
        fh.write(b'\x1f\x8b')
    lc._anon_peak['w-300'] = 6 * GIB

    finalize('w-300', 300, succeeded=True)

    assert not os.path.exists(lc.base('300', 1) + '.log.gz')
    assert metrics(300)['peakAnonBytes'] == 6 * GIB
    assert os.path.exists(jm.done_path('300', 1))
