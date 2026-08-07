"""Turning a measurement into the pod's requests and limits.

_profile_overrides() decides what the profile is allowed to say; _resources()
decides what actually lands on the container. Both are called here rather than
read, because the first version of the sizing gate read `mem is None` AFTER mem
had been defaulted, so it was never true and profile sizing was silently dead
while a source-text assertion still passed.
"""

import pytest

import config
import units
import sizing
import attempts
import job_monitor as jm


PROFILE_RANGES = [
    (1000, {'peakAnonBytes': 1_000_000_000, 'peakWorkingSetBytes': 9_000_000_000,
            'peakEphemeralBytes': 2_000_000_000}),
    (2000, {'peakAnonBytes': 3_000_000_000, 'peakWorkingSetBytes': 13_000_000_000,
            'peakEphemeralBytes': 4_000_000_000}),
]

MI = 1024 ** 2


@pytest.fixture
def shaped(monkeypatch):
    """The worker's configured shape, plus a loaded profile."""
    def configure(ranges=PROFILE_RANGES, margin=1.1, req_mem='9Gi',
                  req_eph='35Gi', lim_eph='40Gi', max_mem='32Gi',
                  headroom='512Mi', runtime_insurance='3Gi',
                  eph_headroom='2Gi', eph_insurance='8Gi', max_eph='64Gi'):
        monkeypatch.setattr(config, 'PROFILE', sorted(ranges))
        monkeypatch.setattr(config, '_SORTED_SECONDS', None)
        monkeypatch.setattr(config, 'PROFILE_MARGIN', margin)
        monkeypatch.setattr(config, 'PROFILE_MAX_MEM', max_mem)
        monkeypatch.setattr(config, 'PROFILE_CACHE_HEADROOM', headroom)
        monkeypatch.setattr(config, 'PROFILE_RUNTIME_MEMORY_INSURANCE', runtime_insurance)
        monkeypatch.setattr(config, 'PROFILE_EPHEMERAL_HEADROOM', eph_headroom)
        monkeypatch.setattr(config, 'PROFILE_RUNTIME_EPHEMERAL_INSURANCE', eph_insurance)
        monkeypatch.setattr(config, 'PROFILE_MAX_EPHEMERAL', max_eph)
        monkeypatch.setattr(config, 'REQ_CPU', '1800m')
        monkeypatch.setattr(config, 'REQ_MEM', req_mem)
        monkeypatch.setattr(config, 'REQ_EPHEMERAL', req_eph)
        monkeypatch.setattr(config, 'LIM_EPHEMERAL', lim_eph)
    return configure


# --- what the profile is allowed to say --------------------------------------

def test_profile_sizes_a_first_attempt(shaped):
    shaped()
    out = sizing._profile_overrides(2000, escalated=False)
    assert out['memory'] == '3659Mi'          # 3 GB rss * 1.1 + 512Mi
    # 3.8Gi measured * 1.1 margin + 2Gi flat headroom; this range is short
    # enough that its runtime-weighted share rounds to nothing.
    assert out['ephemeral-storage'] == '6244Mi'
    # cpu is no longer profiled: REQ_CPU is fixed, so there is nothing to size,
    # and a measured cpu value only makes packing non-uniform.
    assert 'cpu' not in out


def test_profile_does_not_override_an_escalated_retry(shaped):
    # An escalation is a measurement of THIS run and outranks an earlier one.
    shaped()
    assert sizing._profile_overrides(2000, escalated=True) == {}


def test_profile_gives_nothing_past_its_high_water_mark(shaped):
    shaped()
    assert sizing._profile_overrides(99999, escalated=False) == {}
    assert sizing._profile_overrides(None, escalated=False) == {}


def test_profile_memory_is_capped_at_its_own_ceiling_not_the_configured_request(shaped):
    # A range measured above the configured request must be able to ask for more,
    # or it packs as though it were small and lands somewhere it cannot fit. The
    # ceiling is what bounds it, and the OOM ladder can still climb past that.
    shaped(ranges=[(1, {'peakAnonBytes': 500_000_000_000})],
           req_mem='9Gi', max_mem='32Gi')
    assert sizing._profile_overrides(1, escalated=False)['memory'] == '32768Mi'


def test_profile_memory_can_exceed_the_configured_request(shaped):
    # 28 GB peak against a 9Gi configured request: the profile must raise it.
    shaped(ranges=[(1, {'peakAnonBytes': 28_000_000_000})],
           req_mem='9Gi', max_mem='32Gi')
    got = sizing._profile_overrides(1, escalated=False)['memory']
    assert units.quantity_bytes(got) > units.quantity_bytes('9Gi')


def test_memory_is_sized_from_rss_never_from_working_set(shaped):
    # Working set is whatever limit it was measured under -- the kernel grows
    # page cache to fill it. Measured on ssc-test, one 420-ledger range:
    #   limit 4Gi     -> ws  3.61 GiB, rss 2.43 GiB, 775s
    #   limit 8Gi     -> ws  7.48 GiB, rss 2.41 GiB, 746s
    #   limit 24000Mi -> ws 13.49 GiB, rss 2.28 GiB, 773s
    # rss is flat and wall-clock is flat, so sizing from ws would reserve 5x the
    # real demand for no gain. It is still recorded -- kubelet ranks
    # node-pressure evictions on it, so it explains an eviction rss cannot.
    shaped(ranges=[(1, {'peakWorkingSetBytes': 13_000_000_000})])
    assert 'memory' not in sizing._profile_overrides(1, escalated=False), \
        "an older artifact without rss must fall back, not guess from working set"
    assert 'peakWorkingSetBytes' in attempts.PEAK_FIELDS


def test_small_ranges_get_absolute_slack_not_just_a_percentage(shaped):
    # memory.max bounds anon PLUS page cache. At 190 MiB rss a 1.1x margin is
    # 19 MiB of slack -- measured on ssc-test, 90 ranges OOMKilled within 90s of
    # dispatch. The fixed headroom is what makes small ranges survivable.
    shaped(ranges=[(1, {'peakAnonBytes': 190 * MI})])
    got = units.quantity_bytes(sizing._profile_overrides(1, escalated=False)['memory'])
    slack = (got - 190 * MI) / MI
    assert slack > 400, f"only {slack:.0f}MiB of slack above rss"


@pytest.mark.parametrize('peak_mi', [648, 1467, 222])   # live: median, largest, smallest anon
def test_the_sizing_formula_is_peak_times_margin_plus_headroom(shaped, peak_mi):
    shaped(ranges=[(1, {'peakAnonBytes': peak_mi * MI})], margin=1.15,
           headroom='512Mi', max_mem='32Gi')
    got = sizing._profile_overrides(1, escalated=False)['memory']
    assert got == f"{int(peak_mi * MI * 1.15) // MI + 512}Mi"


def test_runtime_insurance_is_weighted_by_the_longest_profiled_range(shaped):
    shaped(ranges=[
        (1, {'peakAnonBytes': 1024 * MI, 'seconds': 100}),
        (2, {'peakAnonBytes': 1024 * MI, 'seconds': 400}),
    ], margin=1.15, headroom='512Mi', runtime_insurance='3Gi')

    short = units.quantity_bytes(sizing._profile_overrides(1, escalated=False)['memory'])
    longest = units.quantity_bytes(sizing._profile_overrides(2, escalated=False)['memory'])
    base = int(1024 * MI * 1.15) + 512 * MI
    assert short == (base + 768 * MI) // MI * MI
    assert longest == (base + 3 * 1024 * MI) // MI * MI


@pytest.mark.parametrize('seconds', [None, 0, -1, 'bad', float('nan'), float('inf')])
def test_invalid_or_nonpositive_runtime_adds_no_insurance(shaped, seconds):
    shaped(ranges=[(1, {'peakAnonBytes': 1024 * MI, 'seconds': seconds})],
           margin=1.15, headroom='512Mi', runtime_insurance='3Gi')
    got = units.quantity_bytes(sizing._profile_overrides(1, escalated=False)['memory'])
    assert got == (int(1024 * MI * 1.15) + 512 * MI) // MI * MI


def test_zero_runtime_insurance_disables_it_and_the_cap_still_applies_last(shaped):
    ranges = [(1, {'peakAnonBytes': 1024 * MI, 'seconds': 100})]
    shaped(ranges=ranges, margin=1.15, headroom='512Mi',
           runtime_insurance='0', max_mem='2Gi')
    without = sizing._profile_overrides(1, escalated=False)['memory']
    assert without == f"{int(1024 * MI * 1.15) // MI + 512}Mi"

    shaped(ranges=ranges, margin=1.15, headroom='512Mi',
           runtime_insurance='3Gi', max_mem='2Gi')
    assert sizing._profile_overrides(1, escalated=False)['memory'] == '2048Mi'


# --- what lands on the container ---------------------------------------------

def test_a_measured_range_requests_its_measurement_and_limits_only_disk(shaped):
    # The profile moves requests. Disk is the one dimension still limited, and
    # its limit is matched so a range measured to need more is allowed to use it.
    shaped()
    r = jm._resources(end=2000)
    assert r.requests['memory'] == '3659Mi'
    assert r.requests['ephemeral-storage'] == r.limits['ephemeral-storage'] == '6244Mi'
    # The configured request, not a measured one -- a profiled range now packs
    # at exactly the same cpu as an unprofiled one.
    assert r.requests['cpu'] == '1800m'
    assert set(r.limits) == {'ephemeral-storage'}, \
        f"a worker may only ever be limited on disk, got {sorted(r.limits)}"


def test_an_unmeasured_range_keeps_the_configured_requests(shaped):
    # No profile entry must behave exactly as if there were no profile at all.
    shaped()
    r = jm._resources(end=99999)
    assert r.requests['memory'] == '9Gi'
    assert 'memory' not in r.limits
    assert r.requests['ephemeral-storage'] == '35Gi'
    assert r.limits['ephemeral-storage'] == '40Gi'


def test_an_escalated_retry_keeps_its_own_size(shaped):
    # The escalation already chose the size; the profile must not overwrite it.
    # It lands on the request, which is the whole mechanism now: a bigger request
    # places the pod where the memory is actually free, and raises the bar before
    # the kubelet picks it as an eviction victim.
    shaped()
    r = jm._resources(mem='36000Mi', end=2000)
    assert r.requests['memory'] == '36000Mi'
    assert 'memory' not in r.limits, "an escalated retry must not be capped either"
    assert r.requests['cpu'] == '1800m', "cpu must fall back to the configured request"


def test_ephemeral_escalation_raises_request_and_limit_together(shaped):
    # ephemeral-storage is a scheduling dimension: a pod that outgrew its limit
    # will not fit where it was placed before unless the request moves too.
    shaped()
    r = jm._resources(eph='60Gi', end=2000)
    assert r.requests['ephemeral-storage'] == r.limits['ephemeral-storage'] == '60Gi'


def test_no_worker_gets_a_cpu_or_memory_limit(shaped):
    # _profile_overrides returns {} for BOTH "no profile entry" and "escalated
    # attempt". Treating them the same handed an OOM retry more memory while
    # capping it at LIM_CPU, when the attempt that just failed ran unlimited.
    # Measured on ssc-test 2026-07-30: 256 of 679 a2 pods were capped at cpu 2.
    # Less cpu means less download concurrency means a lower peak, so the retry
    # succeeds at a figure the next run cannot reproduce unthrottled.
    #
    # At a 2-core limit every range pegs 2.0 anyway, so the measured peak would
    # be a ceiling and the profile could never learn real demand. Packing is
    # driven by the request, which every worker still carries.
    shaped()
    measured = jm._resources(end=2000)
    escalated = jm._resources(mem='9000Mi', end=2000)
    unmeasured = jm._resources(end=999999999)
    for r, why in ((measured, 'measured'), (escalated, 'escalated retry'),
                   (unmeasured, 'unprofiled')):
        assert 'cpu' not in r.limits, f"{why} range was throttled: {r.limits}"
        assert 'memory' not in r.limits, f"{why} range was capped: {r.limits}"
        assert r.requests['cpu'] == '1800m', why



def test_pvc_mode_takes_no_ephemeral_request_or_override(shaped):
    # /data is not on the node disk there, so sizing it would be meaningless --
    # and a large request would make disk the binding dimension and halve
    # workers-per-node for no reason.
    shaped(req_eph='')
    r = jm._resources(end=2000)
    assert 'ephemeral-storage' not in r.requests


def test_disk_gets_a_flat_headroom_and_a_runtime_weighted_share(shaped):
    """Disk is sized like memory: measured peak, plus a floor, plus insurance.

    The 2026-08-01 ephemeral run peaked at 37.76Gi against a flat 40Gi limit --
    6% of margin, on a detection-and-escalation path that has never fired on
    real data. Margin alone does not fix that: it scales the measurement, so the
    ranges closest to the limit get the least absolute headroom.

    Disk earns the runtime weighting the same way memory does -- measured across
    3985 ranges, peak disk tracks runtime at pearson 0.920 (runtime decile 0
    uses 0.1Gi, decile 9 uses 24.7Gi), so the weighting lands the allowance on
    exactly the ranges that need it.
    """
    shaped(eph_headroom='2Gi', eph_insurance='8Gi')
    short = sizing._profile_overrides(2000, escalated=False)['ephemeral-storage']

    # same range, no allowances at all -> margin only
    shaped(eph_headroom='0', eph_insurance='0')
    bare = sizing._profile_overrides(2000, escalated=False)['ephemeral-storage']

    assert units.quantity_bytes(short) > units.quantity_bytes(bare)
    assert units.quantity_bytes(short) - units.quantity_bytes(bare) >= 2 * 1024 ** 3


def test_a_measured_range_may_exceed_the_flat_unprofiled_disk_limit(shaped):
    """PROFILE_MAX_EPHEMERAL is above LIM_EPHEMERAL on purpose.

    LIM_EPHEMERAL is what an UNMEASURED range gets. Capping a measured range at
    it would discard the measurement -- the worst range observed wants ~43Gi
    after margin alone, which the flat 40Gi limit would silently clip back to
    the value that was already too tight.
    """
    shaped(lim_eph='40Gi', max_eph='64Gi', eph_headroom='2Gi', eph_insurance='8Gi')
    out = sizing._profile_overrides(2000, escalated=False)['ephemeral-storage']
    assert units.quantity_bytes(out) > 0
    # and the cap still binds when it should
    shaped(lim_eph='40Gi', max_eph='1Gi', eph_headroom='2Gi', eph_insurance='8Gi')
    capped = sizing._profile_overrides(2000, escalated=False)['ephemeral-storage']
    assert units.quantity_bytes(capped) == 1024 ** 3
