"""Turning a measurement into the pod's requests and limits.

_profile_overrides() decides what the profile is allowed to say; _resources()
decides what actually lands on the container. Both are called here rather than
read, because the first version of the sizing gate read `mem is None` AFTER mem
had been defaulted, so it was never true and profile sizing was silently dead
while a source-text assertion still passed.
"""

import pytest

import job_monitor as jm


PROFILE_RANGES = [
    (1000, {'peakRssBytes': 1_000_000_000, 'peakWorkingSetBytes': 9_000_000_000,
            'peakEphemeralBytes': 2_000_000_000, 'peakCpuCores': 0.5}),
    (2000, {'peakRssBytes': 3_000_000_000, 'peakWorkingSetBytes': 13_000_000_000,
            'peakEphemeralBytes': 4_000_000_000, 'peakCpuCores': 1.2}),
]

MI = 1024 ** 2


@pytest.fixture
def sizing(monkeypatch):
    """The worker's configured shape, plus a loaded profile."""
    def configure(ranges=PROFILE_RANGES, margin=1.1, lim_mem='24000Mi',
                  req_eph='35Gi', lim_eph='40Gi', max_mem='32Gi',
                  headroom='512Mi', cpu_limit=''):
        monkeypatch.setattr(jm, 'PROFILE', sorted(ranges))
        monkeypatch.setattr(jm, 'PROFILE_MARGIN', margin)
        monkeypatch.setattr(jm, 'PROFILE_MAX_MEM', max_mem)
        monkeypatch.setattr(jm, 'PROFILE_CACHE_HEADROOM', headroom)
        monkeypatch.setattr(jm, 'PROFILE_CPU_LIMIT', cpu_limit)
        monkeypatch.setattr(jm, 'REQ_CPU', '1800m')
        monkeypatch.setattr(jm, 'LIM_CPU', '2')
        monkeypatch.setattr(jm, 'REQ_MEM', '9Gi')
        monkeypatch.setattr(jm, 'LIM_MEM', lim_mem)
        monkeypatch.setattr(jm, 'REQ_EPHEMERAL', req_eph)
        monkeypatch.setattr(jm, 'LIM_EPHEMERAL', lim_eph)
    return configure


# --- what the profile is allowed to say --------------------------------------

def test_profile_sizes_a_first_attempt(sizing):
    sizing()
    out = jm._profile_overrides(2000, escalated=False)
    assert out['memory'] == '3659Mi'          # 3 GB rss * 1.1 + 512Mi
    assert out['ephemeral-storage'] == '4196Mi'
    # cpu is no longer profiled: REQ_CPU is fixed, so there is nothing to size,
    # and a measured cpu value only makes packing non-uniform.
    assert 'cpu' not in out
    assert 'peakCpuCores' not in jm.PEAK_FIELDS


def test_profile_does_not_override_an_escalated_retry(sizing):
    # An escalation is a measurement of THIS run and outranks an earlier one.
    sizing()
    assert jm._profile_overrides(2000, escalated=True) == {}


def test_profile_gives_nothing_past_its_high_water_mark(sizing):
    sizing()
    assert jm._profile_overrides(99999, escalated=False) == {}
    assert jm._profile_overrides(None, escalated=False) == {}


def test_profile_memory_is_capped_at_its_own_ceiling_not_the_worker_limit(sizing):
    # A range needing more than the configured limit must be able to ask for it,
    # or it is pinned under its own measured peak and OOMs every attempt. The
    # ceiling is what bounds it, and the OOM ladder can still climb past that.
    sizing(ranges=[(1, {'peakRssBytes': 500_000_000_000})],
           lim_mem='24000Mi', max_mem='32Gi')
    assert jm._profile_overrides(1, escalated=False)['memory'] == '32768Mi'


def test_profile_memory_can_exceed_the_configured_worker_limit(sizing):
    # 28 GB peak against a 24000Mi configured limit: the profile must raise it.
    sizing(ranges=[(1, {'peakRssBytes': 28_000_000_000})],
           lim_mem='24000Mi', max_mem='32Gi')
    got = jm._profile_overrides(1, escalated=False)['memory']
    assert jm._quantity_bytes(got) > jm._quantity_bytes('24000Mi')


def test_memory_is_sized_from_rss_never_from_working_set(sizing):
    # Working set is whatever limit it was measured under -- the kernel grows
    # page cache to fill it. Measured on ssc-test, one 420-ledger range:
    #   limit 4Gi     -> ws  3.61 GiB, rss 2.43 GiB, 775s
    #   limit 8Gi     -> ws  7.48 GiB, rss 2.41 GiB, 746s
    #   limit 24000Mi -> ws 13.49 GiB, rss 2.28 GiB, 773s
    # rss is flat and wall-clock is flat, so sizing from ws would reserve 5x the
    # real demand for no gain. It is still recorded -- kubelet ranks
    # node-pressure evictions on it, so it explains an eviction rss cannot.
    sizing(ranges=[(1, {'peakWorkingSetBytes': 13_000_000_000})])
    assert 'memory' not in jm._profile_overrides(1, escalated=False), \
        "an older artifact without rss must fall back, not guess from working set"
    assert 'peakWorkingSetBytes' in jm.PEAK_FIELDS


def test_sizing_prefers_anon_and_falls_back_to_the_scraped_rss(sizing):
    # peakAnonBytes is kubelet's rssBytes on the collector's own poll;
    # peakRssBytes is the same quantity via a 30s Prometheus scrape. A profile
    # captured before the collector tracked anon must keep sizing exactly as it
    # did, or every existing profile silently reverts to default.
    sizing(ranges=[(1, {'peakRssBytes': 1_000_000_000})])
    scraped_only = jm._profile_overrides(1, escalated=False)['memory']
    # Both present: the finer figure wins, not the coarser one it sits beside.
    sizing(ranges=[(1, {'peakAnonBytes': 1_000_000_000,
                        'peakRssBytes': 3_000_000_000})])
    assert jm._profile_overrides(1, escalated=False)['memory'] == scraped_only


def test_small_ranges_get_absolute_slack_not_just_a_percentage(sizing):
    # memory.max bounds anon PLUS page cache. At 190 MiB rss a 1.1x margin is
    # 19 MiB of slack -- measured on ssc-test, 90 ranges OOMKilled within 90s of
    # dispatch. The fixed headroom is what makes small ranges survivable.
    sizing(ranges=[(1, {'peakRssBytes': 190 * MI})])
    got = jm._quantity_bytes(jm._profile_overrides(1, escalated=False)['memory'])
    slack = (got - 190 * MI) / MI
    assert slack > 400, f"only {slack:.0f}MiB of slack above rss"


@pytest.mark.parametrize('peak_mi', [648, 1467, 222])   # live: median, largest, smallest anon
def test_the_sizing_formula_is_peak_times_margin_plus_headroom(sizing, peak_mi):
    sizing(ranges=[(1, {'peakAnonBytes': peak_mi * MI})], margin=1.15,
           headroom='512Mi', max_mem='32Gi')
    got = jm._profile_overrides(1, escalated=False)['memory']
    assert got == f"{int(peak_mi * MI * 1.15) // MI + 512}Mi"


# --- what lands on the container ---------------------------------------------

def test_a_measured_range_matches_memory_and_disk_and_leaves_cpu_configured(sizing):
    # Memory and disk match request to limit -- exceeding either kills the pod.
    # CPU keeps its configured request and is left uncapped, so the range packs
    # by what it uses and can still burst.
    sizing()
    r = jm._resources(end=2000)
    assert r.requests['memory'] == r.limits['memory'] == '3659Mi'
    assert r.requests['ephemeral-storage'] == r.limits['ephemeral-storage'] == '4196Mi'
    # The configured request, not a measured one -- a profiled range now packs
    # at exactly the same cpu as an unprofiled one.
    assert r.requests['cpu'] == '1800m'
    assert 'cpu' not in r.limits, "a measured range runs uncapped"


def test_an_unmeasured_range_keeps_the_mismatched_defaults(sizing):
    # No profile entry must behave exactly as if there were no profile at all.
    sizing()
    r = jm._resources(end=99999)
    assert r.requests['memory'] == '9Gi' and r.limits['memory'] == '24000Mi'
    assert r.requests['ephemeral-storage'] == '35Gi'
    assert r.limits['ephemeral-storage'] == '40Gi'
    assert r.requests != r.limits


def test_an_escalated_retry_keeps_its_own_size_and_raises_the_request_with_it(sizing):
    # The escalation already chose the size; the profile must not overwrite it.
    # The request moves too: a pod that OOMed at the old limit will not fit
    # where it was scheduled before.
    sizing()
    r = jm._resources(mem='36000Mi', end=2000)
    assert r.requests['memory'] == r.limits['memory'] == '36000Mi'
    assert r.requests['cpu'] == '1800m', "cpu must fall back to the configured request"


def test_ephemeral_escalation_raises_request_and_limit_together(sizing):
    # ephemeral-storage is a scheduling dimension: a pod that outgrew its limit
    # will not fit where it was placed before unless the request moves too.
    sizing()
    r = jm._resources(eph='60Gi', end=2000)
    assert r.requests['ephemeral-storage'] == r.limits['ephemeral-storage'] == '60Gi'


def test_no_worker_gets_a_cpu_limit_unless_one_is_configured(sizing):
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
    sizing()
    measured = jm._resources(end=2000)
    escalated = jm._resources(mem='9000Mi', end=2000)
    unmeasured = jm._resources(end=999999999)
    for r, why in ((measured, 'measured'), (escalated, 'escalated retry'),
                   (unmeasured, 'unprofiled')):
        assert 'cpu' not in r.limits, f"{why} range was throttled: {r.limits}"
        assert r.requests['cpu'] == '1800m', why


def test_a_configured_cpu_limit_is_still_honoured(sizing):
    sizing(cpu_limit='3')
    assert jm._resources(end=2000).limits['cpu'] == '3'


def test_pvc_mode_takes_no_ephemeral_request_or_override(sizing):
    # /data is not on the node disk there, so sizing it would be meaningless --
    # and a large request would make disk the binding dimension and halve
    # workers-per-node for no reason.
    sizing(req_eph='')
    r = jm._resources(end=2000)
    assert 'ephemeral-storage' not in r.requests
