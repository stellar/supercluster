"""Unit tests for the parallel-catchup job monitor and its log collector.

Covers the formats this mission does not control: the Job controller's
podFailurePolicy condition messages, and stellar-core's medida metric block.
Both are pinned from real captures so a Kubernetes or stellar-core change fails
here rather than silently degrading a run.

Sources are parsed rather than imported, so no cluster, kubernetes client or
aiohttp is required.

Run: python3 -m pytest test_job_monitor.py
"""

import json
import re

import pytest

SRC = open(__file__.replace('test_job_monitor.py', 'job_monitor.py')).read()
COLLECTOR_SRC = open(__file__.replace('test_job_monitor.py', 'log_collector.py')).read()


def _extract(pattern, src=None):
    m = re.search(pattern, src if src is not None else SRC, re.S | re.M)
    assert m, f"pattern not found: {pattern}"
    return m


JOB_MSG = re.compile(eval(_extract(r"_JOB_MSG = re\.compile\((r\"[^\"]+\")\)").group(1)))
JOB_RULE = re.compile(eval(_extract(r"_JOB_RULE = re\.compile\((r\"[^\"]+\")\)").group(1)))
SUM_RE = re.compile(eval(_extract(r"_SUM_RE = re\.compile\((r\"[^\"]+\")\)").group(1)))
RULE_ORDER = [x.strip().strip("'") for x in
              _extract(r"RULE_ORDER = \[([^\]]+)\]").group(1).split(',')]
RULE_OUTCOME = dict(enumerate(RULE_ORDER))

# RECONSTRUCTED 2026-07-30 after an over-broad test deletion removed the
# originals -- twice. Kept up here with the other module constants so a
# function-scoped deletion cannot reach them again. Shaped to what the code
# parses (_JOB_RULE reads "rule at index N", RULE_ORDER[2] is 'failed';
# classify() keys on the substring 'ephemeral' in status.message) but no longer
# verbatim captures. Re-pin from a real eviction on the next run.
EPH_EVICT_JOB_CONDITION = (
    "Container stellar-core for pod stellar-supercluster/"
    "parallel-catchup-r31005951-a1-x7k2p failed with exit code 3 "
    "matching FailJob rule at index 2")
EPH_EVICT_MESSAGE = (
    "Pod ephemeral local storage usage exceeds the total limit of containers 40Gi")


def classify(msg):
    """Mirrors classify_from_job: rule index wins, exit code is the fallback."""
    rule, detail = JOB_RULE.search(msg), JOB_MSG.search(msg)
    outcome = RULE_OUTCOME.get(int(rule.group('idx'))) if rule else None
    code = int(detail.group('code')) if detail else None
    if outcome is None and code is not None:
        outcome = 'oom' if code == 137 else 'failed'
    return outcome, code, (detail.group('pod') if detail else None)


def tx_apply_scanner():
    cls = _extract(r"^_TX_METRIC = .*?^(class TxApplyScanner:.*?)^def ",
                   COLLECTOR_SRC).group(1)
    ns = {'re': re}
    exec("\n".join([_extract(r"^_TX_METRIC = .*$", COLLECTOR_SRC).group(0),
                    _extract(r"^_SUM_RE = .*$", COLLECTOR_SRC).group(0),
                    cls]), ns)
    return ns['TxApplyScanner']


# --- captures ----------------------------------------------------------------

# EKS 1.34 Job condition messages. Only the wording is pinned; pod and
# container names are renamed for readability.
DISRUPTED = ("Pod sandbox/jterm-catchup-snfr2 has condition DisruptionTarget "
             "matching FailJob rule at index 0")
OOMKILLED = ("Container oom-container for pod sandbox/oom-test-job-qvq8b failed with "
             "exit code 137 matching FailJob rule at index 1")
NONZERO_EXIT = ("Container exit-1-container for pod sandbox/exit-1-job-wbhkq failed with "
                "exit code 1 matching FailJob rule at index 2")

# stellar-core 27.1.1 catchup pod, --metric 'ledger.transaction.apply'. Kept
# whole: `sum` is 10 lines below the header against a 15-line scan window.
MEDIDA_BLOCK = """2026-07-28T18:39:49.350 GAJSL [default INFO] metric 'ledger.transaction.apply':
2026-07-28T18:39:49.350 GAJSL [default INFO]            count = 20
2026-07-28T18:39:49.350 GAJSL [default INFO]        mean rate = 0.22136 calls/s
2026-07-28T18:39:49.350 GAJSL [default INFO]    1-minute rate = 0.113149 calls/s
2026-07-28T18:39:49.350 GAJSL [default INFO]    5-minute rate = 0.175948 calls/s
2026-07-28T18:39:49.350 GAJSL [default INFO]   15-minute rate = 0.191421 calls/s
2026-07-28T18:39:49.350 GAJSL [default INFO]              min = 0.295417ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              max = 0.639873ms
2026-07-28T18:39:49.350 GAJSL [default INFO]             mean = 0.417143ms
2026-07-28T18:39:49.350 GAJSL [default INFO]           stddev = 0.108677ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              sum = 8.34285ms
2026-07-28T18:39:49.350 GAJSL [default INFO]           median = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              75% = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              95% = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              98% = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]              99% = 0ms
2026-07-28T18:39:49.350 GAJSL [default INFO]            99.9% = 0ms"""

TX_APPLY_SECONDS = 0.00834285


# --- how a failed catchup attempt is classified ------------------------------

@pytest.mark.parametrize("msg,outcome,code,pod", [
    (DISRUPTED, 'disrupted', None, None),
    (OOMKILLED, 'oom', 137, 'oom-test-job-qvq8b'),
    (NONZERO_EXIT, 'failed', 1, 'exit-1-job-wbhkq'),
])
def test_job_condition_message(msg, outcome, code, pod):
    assert classify(msg) == (outcome, code, pod)


def test_rule_order_matches_the_rendered_policy():
    rendered = re.findall(r"\n        \('(\w+)', client\.V1PodFailurePolicyRule", SRC)
    assert rendered == RULE_ORDER


def test_eviction_is_told_apart_from_a_broken_range_by_the_condition():
    # stellar-core exits 3 both for a drain and for a corrupt bucket, so only
    # DisruptionTarget separates them -- hence rule 0 must be evaluated first.
    assert classify(DISRUPTED)[0] == 'disrupted'
    assert classify("Container c for pod ns/p failed with exit code 3")[0] == 'failed'


def test_bare_137_is_an_oom():
    assert classify("Container c for pod ns/p failed with exit code 137")[:2] == ('oom', 137)


def test_backoff_limit_message_stays_unclassified():
    assert classify("Job has reached the specified backoff limit") == (None, None, None)


def test_admission_rejection_is_not_a_catchup_failure():
    rejected = {'VolumeAttachmentLimitExceeded', 'OutOfcpu', 'OutOfmemory', 'OutOfpods',
                'UnexpectedAdmissionError', 'NodeAffinity', 'Shutdown', 'Evicted'}
    listed = set(re.findall(r"'(\w+)'", _extract(
        r"if pod\.status\.reason in \(([^)]+)\)").group(1)))
    assert rejected <= listed, f"missing from classify(): {rejected - listed}"


# --- ledger range generation -------------------------------------------------

def test_logarithmic_ranges_match_the_shell_generator():
    # Verbatim output of logarithmic_range_generator.sh with
    # floor=16000 overlap=320 start=0 latest=500000 parallelism=4, captured
    # before it was deleted. Chunk size halves toward the tip, so exact values
    # are pinned rather than a count.
    expected = "250000/62820 187500/62820 125000/62820 62500/62820 375001/31570 343751/31570 312501/31570 281251/31570 500000/16320 484000/16320 468000/16320 452000/14817".split()

    floor, overlap, start, latest, par = 16000, 320, 0, 500000, 4

    def seg(sl, el, ss):
        out = []
        while el > sl:
            lpj = min(el - sl, ss)
            out.append((el, lpj + overlap))
            el -= lpj
        return out

    out, s0, end = [], start, latest // 2
    chunk = (end - s0 + 1) // max(par, 1)
    while chunk > floor:
        out += seg(s0, end, chunk)
        s0 = end + 1
        chunk //= 2
        end = s0 + (chunk * par)
    out += seg(end + 1, latest, floor)

    assert [f"{e}/{c}" for e, c in out] == expected


# --- tx_apply, read from stellar-core's metric block -------------------------

def test_monitor_parses_tx_apply_sum():
    sums = [SUM_RE.search(l) for l in MEDIDA_BLOCK.splitlines()]
    got = [float(m.group(1)) / 1000.0 for m in sums if m]
    assert got == [pytest.approx(TX_APPLY_SECONDS)]


def test_collector_scanner_agrees_with_the_monitor():
    scanner = tx_apply_scanner()()
    for line in MEDIDA_BLOCK.splitlines():
        scanner.feed(line)
    assert scanner.seconds == pytest.approx(TX_APPLY_SECONDS)


def test_scanner_resumes_a_block_split_across_a_reconnect():
    # One scanner spans stream_pod's reconnect loop, so a drop mid-block must
    # not lose the header already seen.
    head, tail = MEDIDA_BLOCK.splitlines()[:4], MEDIDA_BLOCK.splitlines()[4:]
    scanner = tx_apply_scanner()()
    for line in head:
        scanner.feed(line)
    assert scanner.seconds is None
    for line in tail:
        scanner.feed(line)
    assert scanner.seconds == pytest.approx(TX_APPLY_SECONDS)


def test_scanner_ignores_sum_from_another_metric():
    scanner = tx_apply_scanner()()
    for line in ["metric 'ledger.ledger.close':", "              sum = 999999.0ms"]:
        scanner.feed(line)
    assert scanner.seconds is None


def test_scanner_gives_up_past_its_window():
    scanner = tx_apply_scanner()()
    scanner.feed("metric 'ledger.transaction.apply':")
    for _ in range(20):
        scanner.feed("[default INFO] unrelated chatter")
    scanner.feed("              sum = 12.5555ms")
    assert scanner.seconds is None


def test_rate_and_mean_lines_are_not_read_as_sum():
    for line in MEDIDA_BLOCK.splitlines():
        if 'rate =' in line or 'mean =' in line:
            assert SUM_RE.search(line) is None


def test_sum_stays_inside_the_scan_window():
    lines = MEDIDA_BLOCK.splitlines()
    header = next(i for i, l in enumerate(lines) if 'ledger.transaction.apply' in l)
    offset = next(i for i, l in enumerate(lines) if SUM_RE.search(l)) - header
    assert offset == 10, f"medida layout moved: sum is now {offset} lines below the header"
    assert offset <= tx_apply_scanner().WINDOW


def test_tx_apply_survives_a_reaped_pod():
    stmt = _extract(r"\n\s*tx = tx_apply_for_range\(.*?\n(?=\s*(?:if|completed|#))")
    assert not re.search(r"\)\s*if pod else None", stmt.group(0)), \
        "tx_apply must fall back to the collector's files when the pod is gone"
    assert re.search(r"tx_apply_for_range\(\s*end,\s*attempt", stmt.group(0))


def test_tx_apply_prefers_durable_sources_over_the_pod_api():
    # The per-attempt reader; tx_apply_for_range now sums these over the chain.
    fn = _extract(r"def _tx_apply_for_attempt\(.*?^def ").group(0)
    assert fn.index('metrics_path') < fn.index('log_path') < fn.index('read_namespaced_pod_log')


# --- contracts between job_monitor and log_collector -------------------------

def test_metrics_filename_agrees_across_both_processes():
    mon = _extract(r"def metrics_path\(end, attempt\):\s*return [^\n]*?f\"([^\"]+)\"")
    col = _extract(r"def base\(end, attempt\):\s*return [^\n]*?f\"([^\"]+)\"", COLLECTOR_SRC)
    assert mon.group(1) == col.group(1) + '.metrics'


def test_discarding_a_successful_archive_keeps_its_metrics():
    suffixes = _extract(r"def discard\(end, attempt\):.*?for suffix in \(([^)]*)\)",
                        COLLECTOR_SRC).group(1)
    assert '.log.gz' in suffixes
    assert '.metrics' not in suffixes


def test_metrics_are_written_before_the_archive_is_discarded():
    # Lives in finalize(), shared by the clean-exit and pod-gone paths.
    body = _extract(r"^(async def finalize\(.*?)(?=\n\nasync def )",
                    COLLECTOR_SRC).group(1)
    assert body.index('write_metrics') < body.index('discard(')


def test_worker_pod_spec_uses_every_helper():
    body = _extract(r"spec=client\.V1PodSpec\((.*?)containers=\[container\]").group(1)
    for field in ('service_account_name', 'topology_spread_constraints', 'restart_policy',
                  'termination_grace_period_seconds', 'affinity', 'tolerations'):
        assert field in body, f"{field} missing from the worker pod spec"
    for helper in ('pod_labels', 'volume_spread_constraints', 'ensure_pvc',
                   '_failure_rules', '_resources'):
        assert len(re.findall(rf"\b{helper}\(", SRC)) >= 2, \
            f"{helper}() is defined but never called"


def test_untimestamped_kubelet_text_never_becomes_a_resume_point():
    # A pod that has just been replaced returns plain text from the logs API
    # instead of log lines. Partitioning that on the first space yields "unable",
    # which as a resume point makes every later request sinceTime=unableZ -> 400
    # for the life of the range.
    ts_re = re.compile(eval(_extract(r"_TS_RE = re\.compile\((r\"[^\"]+\")\)",
                                     COLLECTOR_SRC).group(1)))
    kubelet = "unable to retrieve container logs for containerd://9f2c1a"
    assert ts_re.match(kubelet.partition(' ')[0]) is None
    for good in ("2026-07-28T20:29:27.927795721Z", "2026-07-28T20:29:27Z"):
        assert ts_re.match(good), good










def test_an_ephemeral_eviction_is_not_read_as_an_oom_or_a_disruption():
    # Measured end-to-end on ssc-test: the kubelet sets no DisruptionTarget,
    # and stellar-core drains and exits 3, so the Job condition is a plain
    # non-zero failure that would get no retry. status.message is the only
    # discriminator and only the pod carries it, so both classifiers must test
    # it before anything keyed on Evicted.
    assert 'index 2' in EPH_EVICT_JOB_CONDITION, "the Job matches the generic non-zero rule"
    for src in (COLLECTOR_SRC, SRC):
        body = _extract(r"def classify(?:_from_job)?\(pod\):(.*?)(?=\n\ndef )", src)
        body = body.group(1) if body else src
        eph = body.find("'ephemeral'")
        generic = body.find("'VolumeAttachmentLimitExceeded'")
        assert eph != -1, "no ephemeral-eviction branch"
        assert eph < generic, "the ephemeral branch must precede the generic Evicted branch"


def test_ephemeral_eviction_message_still_matches_what_we_test_for():
    # Both classifiers key on the substring 'ephemeral' in status.message.
    assert 'ephemeral' in EPH_EVICT_MESSAGE


def test_ephemeral_escalation_raises_request_and_limit_together():
    # ephemeral-storage is a scheduling dimension: a pod that outgrew its limit
    # will not fit where it was placed before unless the request moves too.
    fn = _extract(r"def _resources\(.*?^def ").group(0)
    assert fn.count('eph or') == 2, "both request and limit must take the escalated size"
    assert 'MAX_EPHEMERAL_ATTEMPTS' in SRC
    env = _extract(r"ENVIRONMENTAL_OUTCOMES = \(([^)]+)\)").group(1)
    assert 'ephemeral' not in env, "a deterministic failure must not get the 20-attempt budget"


# The collector is a separate container with its own env block, so a variable
# the monitor has is not automatically one the collector has. STORAGE_MODE was
# missing there and the sampler silently did nothing -- it defaults to 'pvc'.
COLLECTOR_ENV_WITH_DEFAULTS = {
    'KUBERNETES_SERVICE_HOST', 'KUBERNETES_SERVICE_PORT',   # injected by kubelet
    'LOGGING_LEVEL', 'PEAK_WS_WINDOW', 'PROMETHEUS_URL',
    'STATE_FLUSH_SECONDS', 'WORKER_CONTAINER',
}


def test_a_finished_stream_is_never_reopened():
    # A completed task is deleted from `tasks`, so without a record of it the
    # next poll re-creates the stream and re-reads the whole log -- every
    # cycle, per pod. Measured: the completion block ran every 10s per range.
    loop = _extract(r"while True:\n(.*?)await asyncio\.sleep\(POLL_SECONDS\)",
                    COLLECTOR_SRC).group(1)
    assert 'if name in streamed:' in loop
    # ...but only once the pod is terminal: a task that ended while the pod is
    # still running died early, and re-opening the stream is how that recovers.
    # Scoped to the per-pod branch: the vanished-pod reaper above it also
    # deletes tasks and adds to `streamed`, and slicing the whole loop would
    # match that block instead of this one.
    per_pod = loop[loop.index('for pod in pods:'):]
    guard = per_pod[per_pod.index('del tasks[name]'):per_pod.index('streamed.add(name)')]
    assert 'terminal.get(name)' in guard


def test_metrics_writes_merge_so_a_rewrite_cannot_drop_a_measurement():
    # The ephemeral peak is held in memory by the collector; a restart loses it.
    # If a later write clobbered the file, the peak already persisted would be
    # lost -- which is exactly what happened before this merge.
    fn = _extract(r"def write_metrics\(.*?(?=\ndef )", COLLECTOR_SRC).group(0)
    assert '{**prior, **values}' in fn, "existing fields must survive"
    # ...and peaks additionally take the max, see the monotonicity test below.
    assert 'PEAK_KEYS' in fn


def test_the_ephemeral_sampler_runs_every_poll_not_once_per_stream():
    # The per-pod branches all end in `continue` for pods already streaming, so
    # a sampler placed after them fires only on the cycle a stream opens --
    # when the range has written almost nothing. It must run before the loop.
    loop = _extract(r"while True:\n(.*?)await asyncio\.sleep\(POLL_SECONDS\)",
                    COLLECTOR_SRC).group(1)
    call = loop.index('sample_kubelet')
    for_pod = loop.index('for pod in pods:')
    assert call < for_pod, "sample_kubelet must run before the per-pod loop"
    assert loop.count('await list_pods(session)') == 1, \
        "one listing per cycle; the sampler must reuse it"


def test_every_env_the_collector_reads_is_set_on_the_collector_container():
    chart = open(__file__.replace(
        'test_job_monitor.py',
        'parallel_catchup_helm/templates/job_monitor.yaml')).read()
    collector = chart[chart.index('- name: log-collector'):]
    needed = set(re.findall(r"os\.getenv\('([A-Z_]+)'", COLLECTOR_SRC))
    missing = {v for v in needed - COLLECTOR_ENV_WITH_DEFAULTS
               if f"- name: {v}\n" not in collector}
    assert not missing, f"collector reads {sorted(missing)} but the chart never sets them"




def test_both_peaks_reach_the_progress_record():
    fields = _extract(r"PEAK_FIELDS = \(([^)]+)\)").group(1)
    for f in ('peakAnonBytes', 'peakWorkingSetBytes', 'peakEphemeralBytes'):
        assert f in fields, f


# --- range profile consumption -----------------------------------------------

def _profile_ns(ranges, mode='ephemeral', margin=1.1):
    """profile_for + _sized, exec'd out of job_monitor with a fixed profile."""
    ns = {'bisect': __import__('bisect'), 'logger': __import__('logging').getLogger('t')}
    for name in ('_quantity_bytes', '_bytes_to_quantity', 'profile_for',
                 '_cpu_millis', '_sized'):
        m = re.search(rf"^(def {name}\(.*?)(?=^\S|\Z)", SRC, re.S | re.M)
        exec(m.group(1), ns)
    ns['_UNITS'] = eval(_extract(r"_UNITS = (\{.*?\})").group(1))
    ns['PROFILE'] = sorted(ranges)
    ns['STORAGE_MODE'] = mode
    ns['PROFILE_MARGIN'] = margin
    return ns


PROFILE_RANGES = [
    (1000, {'peakRssBytes': 1_000_000_000, 'peakWorkingSetBytes': 9_000_000_000,
            'peakEphemeralBytes': 2_000_000_000, 'peakCpuCores': 0.5}),
    (2000, {'peakRssBytes': 3_000_000_000, 'peakWorkingSetBytes': 13_000_000_000,
            'peakEphemeralBytes': 4_000_000_000, 'peakCpuCores': 1.2}),
]


def test_profile_prefers_an_exact_end():
    ns = _profile_ns(PROFILE_RANGES)
    assert ns['profile_for'](2000)['peakRssBytes'] == 3_000_000_000


def test_profile_rounds_up_to_the_next_measured_end_never_down():
    # Cost rises with ledger position -- the bucket set only grows -- so a lower
    # neighbour under-reports, and under-provisioning costs an eviction while
    # over-provisioning only costs packing density.
    ns = _profile_ns(PROFILE_RANGES)
    assert ns['profile_for'](1500)['peakRssBytes'] == 3_000_000_000, \
        "1500 must size from 2000, not from 1000"


def test_profile_falls_back_to_defaults_past_its_high_water_mark():
    # An older profile has nothing above its own top, which is exactly where a
    # newer run's fresh ranges live. Extrapolating there would under-provision.
    ns = _profile_ns(PROFILE_RANGES)
    assert ns['profile_for'](9999) is None


def test_a_profile_from_the_other_storage_mode_is_rejected():
    # An ephemeral profile carries peakEphemeralBytes and a pvc one does not.
    fn = _extract(r"def load_profile\(.*?^PROFILE = None", SRC).group(0)
    assert "mode != STORAGE_MODE" in fn
    assert 'return []' in fn


def test_an_unreadable_profile_is_not_fatal():
    # It is an optimisation, never a prerequisite.
    fn = _extract(r"def load_profile\(.*?^PROFILE = None", SRC).group(0)
    assert '(OSError, ValueError)' in fn


def test_sizing_applies_the_margin_and_never_exceeds_the_limit():
    ns = _profile_ns(PROFILE_RANGES)
    # 1 GB * 1.1, well under the cap
    assert ns['_sized'](1_000_000_000, 1.1, '10Gi') == '1049Mi'
    # capped: a huge peak cannot produce a request above its own limit
    assert ns['_sized'](50_000_000_000, 1.1, '8Gi') == '8192Mi'


def _overrides_ns(ranges, lim_mem='24000Mi', lim_eph='40Gi', margin=1.1,
                  max_mem='32Gi'):
    ns = _profile_ns(ranges, margin=margin)
    ns['LIM_MEM'] = lim_mem
    ns['LIM_EPHEMERAL'] = lim_eph
    ns['LIM_CPU'] = '2'
    ns['REQ_CPU'] = '1800m'
    ns['PROFILE_CPU_LIMIT'] = ''
    ns['PROFILE_MAX_MEM'] = max_mem
    ns['PROFILE_CACHE_HEADROOM'] = '512Mi'
    m = re.search(r"^(def _profile_overrides\(.*?)(?=^\S|\Z)", SRC, re.S | re.M)
    exec(m.group(1), ns)
    return ns


def test_profile_sizes_a_first_attempt():
    # Executed, not grepped: the first version of this gate read `mem is None`
    # AFTER mem had been defaulted, so it was never true and profile sizing was
    # silently dead while a source-text assertion still passed.
    ns = _overrides_ns(PROFILE_RANGES)
    out = ns['_profile_overrides'](2000, escalated=False)
    assert out['memory'] == '3659Mi'          # 3 GB rss * 1.1 + 512Mi
    assert out['ephemeral-storage'] == '4196Mi'
    # cpu is no longer profiled: REQ_CPU is fixed, so there is nothing to size.
    assert 'cpu' not in out


def test_profile_does_not_override_an_escalated_retry():
    # An escalation is a measurement of THIS run and outranks an earlier one.
    ns = _overrides_ns(PROFILE_RANGES)
    assert ns['_profile_overrides'](2000, escalated=True) == {}


def test_profile_gives_nothing_past_its_high_water_mark():
    ns = _overrides_ns(PROFILE_RANGES)
    assert ns['_profile_overrides'](99999, escalated=False) == {}


def test_profile_memory_is_capped_at_its_own_ceiling_not_the_worker_limit():
    # A range needing more than the configured limit must be able to ask for it,
    # or it is pinned under its own measured peak and OOMs every attempt. The
    # ceiling is what bounds it, and the OOM ladder can still climb past that.
    ns = _overrides_ns([(1, {'peakRssBytes': 500_000_000_000})],
                       lim_mem='24000Mi', max_mem='32Gi')
    assert ns['_profile_overrides'](1, escalated=False)['memory'] == '32768Mi'


def test_profile_memory_can_exceed_the_configured_worker_limit():
    # 28 GB peak against a 24000Mi configured limit: the profile must raise it.
    ns = _overrides_ns([(1, {'peakRssBytes': 28_000_000_000})],
                       lim_mem='24000Mi', max_mem='32Gi')
    got = ns['_profile_overrides'](1, escalated=False)['memory']
    assert ns['_quantity_bytes'](got) > ns['_quantity_bytes']('24000Mi')


class _FakeRR:
    """Stand-in for client.V1ResourceRequirements, so _resources can be run."""

    def __init__(self, requests=None, limits=None):
        self.requests, self.limits = requests, limits


def _resources_ns(ranges, req_eph='35Gi', lim_eph='40Gi'):
    ns = _overrides_ns(ranges, lim_eph=lim_eph)
    ns.update(REQ_CPU='1800m', LIM_CPU='2', REQ_MEM='9Gi',
              REQ_EPHEMERAL=req_eph, client=type('c', (), {'V1ResourceRequirements': _FakeRR}))
    m = re.search(r"^(def _resources\(.*?)(?=^def )", SRC, re.S | re.M)
    exec(m.group(1), ns)
    return ns


def test_a_measured_range_matches_memory_and_disk_and_leaves_cpu_configured():
    # Memory and disk match request to limit -- exceeding either kills the pod.
    # CPU keeps its configured limit and only moves its request, so the range
    # packs by what it uses and can still burst.
    ns = _resources_ns(PROFILE_RANGES)
    r = ns['_resources'](end=2000)
    assert r.requests['memory'] == r.limits['memory'] == '3659Mi'
    assert r.requests['ephemeral-storage'] == r.limits['ephemeral-storage'] == '4196Mi'
    # The configured request, not a measured one -- a profiled range now packs
    # at exactly the same cpu as an unprofiled one.
    assert r.requests['cpu'] == '1800m'
    assert 'cpu' not in r.limits, "a measured range runs uncapped"



def test_an_unmeasured_range_keeps_the_mismatched_defaults():
    # No profile entry must behave exactly as if there were no profile at all.
    ns = _resources_ns(PROFILE_RANGES)
    r = ns['_resources'](end=99999)
    assert r.requests['memory'] == '9Gi' and r.limits['memory'] == '24000Mi'
    # cpu is the exception: no worker is throttled, measured or not. A limit
    # only stops a pod using idle cores, and it changes what the range measures.
    assert r.requests['cpu'] == '1800m'
    assert 'cpu' not in r.limits, "an unprofiled range must not be throttled"
    assert r.requests['ephemeral-storage'] == '35Gi'
    assert r.limits['ephemeral-storage'] == '40Gi'
    assert r.requests != r.limits


def test_an_escalated_retry_keeps_the_mismatched_defaults():
    # The escalation already chose the size; the profile must not overwrite it.
    ns = _resources_ns(PROFILE_RANGES)
    r = ns['_resources'](mem='36000Mi', end=2000)
    assert r.limits['memory'] == '36000Mi'
    assert r.requests['cpu'] == '1800m', "cpu must fall back to the configured request"


def test_no_range_is_cpu_throttled_but_every_range_has_a_request():
    # At a 2-core limit every range pegs 2.0, so the measured peak is a ceiling
    # and the profile can never learn real demand. Headroom above the request is
    # the whole point -- the request is still capped for packing.
    ns = _resources_ns(PROFILE_RANGES)
    measured = ns['_resources'](end=2000)
    unmeasured = ns['_resources'](end=99999)
    assert 'cpu' not in measured.limits, "measured ranges run uncapped"
    assert 'cpu' not in unmeasured.limits, "unmeasured ranges run uncapped too"
    # The request is still what bounds packing, on both paths.
    assert measured.requests['cpu'] and unmeasured.requests['cpu']
    assert ns['_cpu_millis'](measured.requests['cpu']) <= ns['_cpu_millis']('1800m')


def test_pvc_mode_takes_no_ephemeral_override():
    # /data is not on the node disk there, so sizing it would be meaningless.
    ns = _resources_ns(PROFILE_RANGES, req_eph='')
    r = ns['_resources'](end=2000)
    assert 'ephemeral-storage' not in r.requests


def test_the_override_is_computed_before_mem_is_defaulted():
    # Reading `mem is None` after mem has been defaulted can never be true,
    # which silently disabled profile sizing entirely once already.
    fn = _extract(r"def _resources\(.*?return client\.V1ResourceRequirements").group(0)
    assert fn.index('_profile_overrides') < fn.index('mem = mem or LIM_MEM')


# --- dispatch order + cross-mode profile reuse -------------------------------

def _ranges_ns(order='tip-first', generator='uniform', parallelism=4):
    ns = {}
    for n in ('_uniform_segment', '_ordered', 'generate_ranges'):
        m = re.search(rf"^(def {n}\(.*?)(?=^\S|\Z)", SRC, re.S | re.M)
        exec(m.group(1), ns)
    ns.update(RANGE_GENERATOR=generator, RANGE_ORDER=order,
              STARTING_LEDGER=39990000, LATEST_LEDGER_NUM=40000000,
              LEDGERS_PER_JOB=1000, LOGARITHMIC_FLOOR_LEDGERS=64000,
              PARALLELISM=parallelism, OVERLAP_LEDGERS=320)
    return ns


def test_generators_emit_tip_first_by_default():
    r = _ranges_ns()['generate_ranges']()
    assert r[0][0] > r[-1][0], "index 0 must be the tip"


def test_oldest_first_reverses_dispatch_without_dropping_ranges():
    # A profiling run wants the cheap early ranges measured first: the bucket
    # set only grows with ledger position, so tip-first front-loads the
    # expensive ones and an interrupted run profiles nothing cheap.
    tip = _ranges_ns('tip-first')['generate_ranges']()
    old = _ranges_ns('oldest-first')['generate_ranges']()
    assert old == list(reversed(tip))
    assert sorted(old) == sorted(tip), "reversing must not change the range set"


def test_a_cross_mode_profile_keeps_cpu_and_memory_but_drops_disk():
    # cpu and memory measure the same work in either mode. Disk does not: a pvc
    # run never measures node-local usage at all, so its absence must fall back
    # to the configured default rather than size the wrong dimension.
    fn = _extract(r"def load_profile\(.*?^PROFILE = None", SRC).group(0)
    assert 'cross_mode' in fn
    assert "k != 'peakEphemeralBytes'" in fn
    assert 'return []' not in fn.split('cross_mode = ')[1].split('out = []')[0], \
        "a cross-mode profile must degrade, not be rejected"


def test_memory_is_sized_from_rss_never_from_working_set():
    # Working set is whatever limit it was measured under -- the kernel grows
    # page cache to fill it. Measured on ssc-test, one 420-ledger range:
    #   limit 4Gi     -> ws  3.61 GiB, rss 2.43 GiB, 775s
    #   limit 8Gi     -> ws  7.48 GiB, rss 2.41 GiB, 746s
    #   limit 24000Mi -> ws 13.49 GiB, rss 2.28 GiB, 773s
    # rss is flat and wall-clock is flat, so sizing from ws would reserve 5x the
    # real demand for no gain.
    fn = _extract(r"def _profile_overrides\(.*?^def ").group(0)
    assert 'peakRssBytes' in fn
    assert 'peakWorkingSetBytes' not in fn, "working set must not drive sizing"


def test_a_profile_without_rss_leaves_memory_alone():
    # An older artifact predates peakRssBytes; it must fall back to the
    # configured default rather than guess from working set.
    ns = _overrides_ns([(1, {'peakWorkingSetBytes': 13_000_000_000})])
    assert 'memory' not in ns['_profile_overrides'](1, escalated=False)


def test_working_set_is_still_recorded_as_a_diagnostic():
    # It is what kubelet ranks node-pressure evictions on, so it explains an
    # eviction that rss cannot -- it just must not feed sizing.
    fields = _extract(r"PEAK_FIELDS = \(([^)]+)\)").group(1)
    for f in ('peakRssBytes', 'peakWorkingSetBytes'):
        assert f in fields, f


# --- the chart must render with the shapes the mission actually sends ---------

import shutil, subprocess

CHART = __file__.replace('test_job_monitor.py', 'parallel_catchup_helm')


def _helm(*extra):
    if not shutil.which('helm'):
        pytest.skip('helm not installed')
    r = subprocess.run(['helm', 'template', 't', CHART,
                        '--set', 'worker.stellar_core_image=x', *extra],
                       capture_output=True, text=True)
    assert r.returncode == 0, r.stderr
    return r.stdout


def test_chart_renders_the_service_account_annotations_the_mission_sends():
    # The mission sends service_account.annotations as an indexed array of
    # {key,value}; metadata.annotations must be a map. Rendering it straight
    # through toYaml produced a list and failed the whole install with
    # "cannot unmarshal array into ... map[string]string" -- which no
    # source-text assertion would have caught.
    out = _helm('--set', 'service_account.annotations[0].key=eks.amazonaws.com/role-arn',
                '--set', 'service_account.annotations[0].value=arn:aws:iam::1:role/r')
    assert 'eks.amazonaws.com/role-arn: "arn:aws:iam::1:role/r"' in out


def test_chart_renders_without_service_account_annotations():
    _helm()


def test_chart_renders_the_node_targeting_the_mission_sends():
    out = _helm('--set', 'worker.requireNodeLabels[0].key=purpose',
                '--set', 'worker.requireNodeLabels[0].operator=In',
                '--set', 'worker.requireNodeLabels[0].values[0]=catchup8-spot',
                '--set', 'worker.tolerateNodeTaints[0].key=catchup8-spot',
                '--set', 'worker.tolerateNodeTaints[0].effect=NoSchedule')
    assert 'catchup8-spot' in out


def test_small_ranges_get_absolute_slack_not_just_a_percentage():
    # memory.max bounds anon PLUS page cache. At 190 MiB rss a 1.1x margin is
    # 19 MiB of slack -- measured on ssc-test, 90 ranges OOMKilled within 90s of
    # dispatch. The fixed headroom is what makes small ranges survivable.
    ns = _overrides_ns([(1, {'peakRssBytes': 190 * 2**20})])
    got = ns['_quantity_bytes'](ns['_profile_overrides'](1, escalated=False)['memory'])
    slack = (got - 190 * 2**20) / 2**20
    assert slack > 400, f"only {slack:.0f}MiB of slack above rss"


def test_oom_escalation_starts_from_what_the_attempt_actually_had():
    # Escalating a 209Mi profiled range off the configured 24000Mi limit jumps
    # to 36000Mi -- a 172x overshoot that discards the packing win on first OOM.
    ns = {}
    for n in ('_quantity_bytes', '_bytes_to_quantity', 'mem_for_attempt'):
        m = re.search(rf"^(def {n}\(.*?)(?=^\S|\Z)", SRC, re.S | re.M)
        exec(m.group(1), ns)
    ns['_UNITS'] = eval(_extract(r"_UNITS = (\{.*?\})").group(1))
    ns.update(LIM_MEM='24000Mi', MEM_BUMP_FACTOR=1.5, MEM_ESCALATION_CAP='48Gi')
    assert ns['mem_for_attempt'](2, '702Mi') == '1053Mi'
    assert ns['mem_for_attempt'](2) == '36000Mi'      # unprofiled keeps old behaviour


def test_chart_defaults_match_the_code_defaults():
    # The chart sets these env vars explicitly, so its value WINS over the
    # os.getenv default. They drifted once -- code said 512Mi while the chart
    # still said 0 -- and the chart silently won, reproducing the OOMs the code
    # change was meant to fix.
    values = open(__file__.replace(
        'test_job_monitor.py', 'parallel_catchup_helm/values.yaml')).read()
    pairs = [('PROFILE_CACHE_HEADROOM', 'profileCacheHeadroom'),
             ('PROFILE_MAX_MEM', 'profileMaxMemory'),
             ('PROFILE_CPU_LIMIT', 'profileCpuLimit'),
             ('PROFILE_MARGIN', 'profileMargin')]
    for env, key in pairs:
        code = _extract(rf"{env} = .*?os\.getenv\('{env}',\s*'?\"?([^'\")]+)").group(1).strip()
        chart = _extract(rf"^\s*{key}:\s*\"?([^\"\n]+)", values).group(1).strip().strip('"')
        assert code == chart, f"{env}: code default {code!r} != chart {chart!r}"


def test_the_monitor_log_lands_where_the_mission_collects_it():
    # collectLogsFromPods tars /logs. The monitor used to write its own log to
    # /data, an emptyDir, so OOM-retry storms never reached the destination
    # directory and did not survive a monitor restart.
    blk = _extract(r"log_file_name = .*?log_file_path = [^\n]*").group(0)
    assert "os.getenv('LOG_DIR'" in blk
    col = _extract(r"def base\(end, attempt\):\s*return [^\n]*", COLLECTOR_SRC).group(0)
    assert 'LOG_DIR' in col, "collector and monitor must share the collected directory"


def test_a_completed_range_releases_its_volume():
    # PVCs are owner-referenced to the release, so nothing reclaimed them until
    # helm uninstall. Measured on ssc-test: 2032 bound PVCs / 79 TiB a third of
    # the way through a 3982-range run, heading for ~156 TiB and 3982 volumes.
    fn = _extract(r"def release_pvc\(.*?^def ").group(0)
    assert 'delete_namespaced_persistent_volume_claim' in fn
    assert "STORAGE_MODE != 'pvc'" in fn, "ephemeral mode has no PVC to release"
    assert 'e.status != 404' in fn, "already-gone must not be an error"


def test_the_volume_is_released_only_after_progress_is_saved():
    # If the process dies between the two, the range must still read as
    # complete -- keeping a volume is recoverable, losing the record is not.
    blk = _extract(r"completed\[end\]\.update\(peaks_for_range.*?release_pvc\(end\)").group(0)
    assert blk.index('save_progress') < blk.index('release_pvc')


def test_releasing_a_volume_never_fails_a_completed_range():
    fn = _extract(r"def release_pvc\(.*?^def ").group(0)
    assert 'raise' not in fn, "a disk cleanup failure must not condemn a finished range"


# --- progress durability -----------------------------------------------------

def test_progress_is_written_to_the_volume_before_the_configmap():
    # A ConfigMap caps at 1 MiB and the record is ~172 bytes per completed
    # range, so it dies around 6100 ranges -- reachable by halving
    # ledgersPerJob. Measured mid-run: 348KB at 2024 ranges, 65% of the cap
    # projected at 3982.
    fn = _extract(r"def save_progress\(.*?^def ").group(0)
    assert 'PROGRESS_FILE' in fn
    assert fn.index('os.replace') < fn.index('_patch_cm'), \
        "the durable write must land before the mirror"
    assert '.tmp' in fn, "a torn write would lose the whole record"


def test_a_configmap_mirror_failure_does_not_stop_the_run():
    # reconcile's loop swallows exceptions, so a 413 thrown here meant no
    # completion was ever recorded again and finished ranges were redispatched
    # forever -- silent, unbounded cost.
    fn = _extract(r"def save_progress\(.*?^def ").group(0)
    assert 'except ApiException' in fn
    assert 'raise' not in fn.split('_patch_cm')[1]


def test_progress_is_read_back_from_the_volume_first():
    fn = _extract(r"def load_progress\(.*?^def ").group(0)
    assert fn.index('PROGRESS_FILE') < fn.index('read_namespaced_config_map'), \
        "the file is authoritative; the ConfigMap is only a fallback"
    assert 'e.status == 404' in fn


# Real block from range-40010367-a1 on ssc-test. medida switches to scientific
# notation past 1e6 ms, which is every range with a real transaction load.
MEDIDA_BIG = """2026-07-29T20:11:16.931 GAJSL [default INFO] metric 'ledger.transaction.apply':
2026-07-29T20:11:16.931 GAJSL [default INFO]            count = 3231886
2026-07-29T20:11:16.931 GAJSL [default INFO]        mean rate = 812.4 calls/s
2026-07-29T20:11:16.931 GAJSL [default INFO]    1-minute rate = 790.1 calls/s
2026-07-29T20:11:16.931 GAJSL [default INFO]    5-minute rate = 801.3 calls/s
2026-07-29T20:11:16.931 GAJSL [default INFO]   15-minute rate = 799.0 calls/s
2026-07-29T20:11:16.931 GAJSL [default INFO]              min = 0.101ms
2026-07-29T20:11:16.931 GAJSL [default INFO]              max = 41.2ms
2026-07-29T20:11:16.931 GAJSL [default INFO]             mean = 0.404ms
2026-07-29T20:11:16.931 GAJSL [default INFO]           stddev = 0.612ms
2026-07-29T20:11:16.931 GAJSL [default INFO]              sum = 1.30722e+06ms"""


def test_scientific_notation_sum_is_parsed():
    # 25% of ranges recorded no tx_apply -- 91-99% of everything above ledger
    # 35M -- because the regex matched "1.30722" then required "ms" and found
    # "e+06ms". The metric block was in the archive the whole time.
    m = SUM_RE.search(MEDIDA_BIG)
    assert m, "scientific-notation sum must parse"
    assert float(m.group(1)) / 1000.0 == pytest.approx(1307.22)


def test_scanner_reads_a_scientific_notation_block():
    scanner = tx_apply_scanner()()
    for line in MEDIDA_BIG.splitlines():
        scanner.feed(line)
    assert scanner.seconds == pytest.approx(1307.22)


def test_plain_decimal_sums_still_parse():
    m = SUM_RE.search("              sum = 8.34285ms")
    assert float(m.group(1)) / 1000.0 == pytest.approx(TX_APPLY_SECONDS)


def test_the_chart_grants_the_pvc_delete_release_pvc_needs():
    # release_pvc calls delete_namespaced_persistent_volume_claim. The Role
    # granted only get/list/create, so every completion logged a 403 warning and
    # the volumes leaked -- 3982 of them, which crashed the EBS CSI controller.
    chart = open(__file__.replace(
        'test_job_monitor.py',
        'parallel_catchup_helm/templates/job_monitor.yaml')).read()
    blk = _extract(r'resources: \["persistentvolumeclaims"\]\s*\n\s*verbs: \[([^\]]+)\]', chart)
    verbs = {v.strip().strip('"') for v in blk.group(1).split(',')}
    assert 'delete' in verbs, f"release_pvc needs delete, Role has {sorted(verbs)}"


def test_the_configmap_mirror_carries_no_profiling_fields():
    # Profile data lives only on the volume. In the ConfigMap it is what pushes
    # a ~30-byte state record to ~172 bytes and the whole document toward the
    # 1 MiB cap at ~6100 ranges.
    ns = {}
    m = re.search(r"^(_PROFILE_ONLY_FIELDS = \(.*?\)\n\n\ndef _state_only\(.*?)(?=\ndef )",
                  SRC, re.S | re.M)
    assert m, "_state_only not found"
    exec(m.group(1), ns)
    prog = {'completed': {'100': {'attempts': 1, 'count': 16320, 'seconds': 700.0,
                                  'peakRssBytes': 123, 'peakCpuCores': 1.9,
                                  'txApply': 200.0, 'wallSeconds': 750.0}},
            'failed': {}}
    out = ns['_state_only'](prog)['completed']['100']
    assert out == {'attempts': 1, 'count': 16320}, out
    # and the untouched original still has everything for the volume copy
    assert 'peakRssBytes' in prog['completed']['100']


def test_the_volume_copy_keeps_the_profile():
    fn = _extract(r"def save_progress\(.*?^def ").group(0)
    assert 'json.dumps(progress' in fn, "the volume write must use the full record"
    assert '_state_only' in fn, "the ConfigMap write must be stripped"
    assert fn.index('os.replace') < fn.index('_state_only')


# --- finished-Job reaping -------------------------------------------------
# reconcile() LISTs every Job and Pod each pass, so a finished Job costs two
# list entries per pass until it is gone. At 2048-4096 parallelism the dead
# ones outnumbered the live ones within the hour under the old 3600s TTL.

def _delete_job_ns(delete_impl):
    """Exec delete_job against fakes. Nothing here needs a cluster."""
    class ApiException(Exception):
        def __init__(self, status):
            self.status = status
            super().__init__(f"status {status}")

    calls, warnings, reaped = [], [], []

    class FakeBatch:
        def delete_namespaced_job(self, name, namespace, **kw):
            calls.append((name, namespace, kw))
            exc = delete_impl(name)
            if exc is not None:
                raise exc

    ns = {
        'batch_v1': FakeBatch(),
        'NAMESPACE': 'stellar-supercluster',
        'job_name': lambda end, attempt: f"run-r{end}-a{attempt}",
        'metric_jobs_reaped': type('C', (), {'inc': lambda s: reaped.append(1)})(),
        'ApiException': ApiException,
        'logger': type('L', (), {'warning': lambda s, *a: warnings.append(a)})(),
    }
    exec(_extract(r"^(def delete_job\(.*?)(?=\ndef )").group(1), ns)
    return ns['delete_job'], calls, warnings, reaped, ApiException


def test_delete_job_reaps_the_pod_too():
    # Background propagation is what actually removes the pod. Orphan/default
    # would leave the pod behind and reap nothing that reconcile lists.
    delete_job, calls, _, reaped, _ = _delete_job_ns(lambda name: None)
    delete_job(30957951, 2)
    assert calls == [('run-r30957951-a2', 'stellar-supercluster',
                      {'propagation_policy': 'Background'})]
    assert len(reaped) == 1


def test_delete_job_is_best_effort():
    # A 404 is the normal race with the TTL controller, not an error. Any other
    # status must warn and keep going: losing a Job to a leaked object is a
    # disk/etcd cost, but raising here would abort a reconcile pass mid-run and
    # strand every other range in the same iteration.
    _, ApiExc = None, None
    for status, want_warn in ((404, False), (403, True), (500, True)):
        delete_job, _, warnings, reaped, ApiException = _delete_job_ns(
            lambda name, s=status: ApiException(s))
        delete_job(1, 1)   # must not raise
        assert bool(warnings) is want_warn, f"status {status}"
        assert reaped == [], "a failed delete must not count as reaped"


def test_the_chart_grants_the_job_delete_reconcile_needs():
    # Same failure the PVC Role had: verbs omitted delete, so every reap logged
    # a 403 and nothing was ever collected.
    chart = open(__file__.replace(
        'test_job_monitor.py',
        'parallel_catchup_helm/templates/job_monitor.yaml')).read()
    blk = _extract(r'resources: \["jobs"\]\s*\n\s*verbs: \[([^\]]+)\]', chart)
    verbs = {v.strip().strip('"') for v in blk.group(1).split(',')}
    assert 'delete' in verbs, f"delete_job needs delete, Role has {sorted(verbs)}"


def test_the_retry_creates_the_successor_before_deleting_the_predecessor():
    # Ordering is the whole safety argument: if the create fails with the
    # predecessor already deleted, the range has no live Job, reconcile sees an
    # undispatched range and redispatches at attempt 1 -- silently discarding
    # the escalated memory limit the retry existed to apply.
    body = _extract(r"(create_namespaced_job\(NAMESPACE, build_job\(\s*int\(end\), by_end\[end\], attempt \+ 1.*?)continue").group(1)
    assert 'delete_job(end, attempt)' in body, "retry path never reaps the old attempt"
    assert body.index('create_namespaced_job') < body.index('delete_job('), \
        "delete_job must come after the successor is created"


def test_a_success_whose_record_is_incomplete_keeps_its_job():
    # tx is read from the collector's .metrics, else the pod. Deleting the Job
    # reaps the pod, so reaping a success before the metrics land turns a
    # recoverable gap into a permanent one -- the same class of loss as the 698
    # ranges the tx_apply regex dropped.
    body = _extract(r"(release_pvc\(end\)\n.*?)(?=\s+elif st\.failed:)").group(1)
    assert '_reap_if_complete(end, attempt, completed[end])' in body, \
        "success path must gate the reap on the record being complete"
    fn = _extract(r"^(def _reap_if_complete\(.*?)(?=\ndef )").group(1)
    assert 'not _attempt_finalized(end, attempt)' in fn, \
        "the reap must wait for the collector's own done marker"


def test_the_chart_ttl_matches_the_code_default():
    # The TTL is now only a backstop, but a chart/code split is how the cache
    # headroom regression shipped: the code default was fixed and the chart
    # still forced the old value.
    chart = open(__file__.replace(
        'test_job_monitor.py', 'parallel_catchup_helm/values.yaml')).read()
    want = int(_extract(r"JOB_TTL_SECONDS = int\(os\.getenv\('JOB_TTL_SECONDS', (\d+)\)\)").group(1))
    got = int(_extract(r"jobTtlSeconds: (\d+)", chart).group(1))
    assert got == want, f"chart sets {got}, code defaults to {want}"


# --- peak anon from kubelet ----------------------------------------------
# Page cache expands to fill memory.max, so memory.peak ~= the limit for every
# pod and cannot be profiled (measured on ssc-test: a range needing 862 MiB of
# anon reported peak 12704 MiB under a 24000 MiB limit). Anon is the only
# limit-independent figure, and kubelet reports it per container for free in
# the payload the collector already fetches for ephemeral storage.

def _sample_ns(summary, container='stellar-core', streaming=True):
    """Exec sample_kubelet's per-pod body against one kubelet payload."""
    eph, anon, ws, flushed, streaming_ref, written, logged = {}, {}, {}, {}, {}, [], []

    class FakeResp:
        def __init__(self, d): self._d = d
        async def __aenter__(self): return self
        async def __aexit__(self, *a): return False
        def raise_for_status(self): pass
        async def json(self): return self._d

    class FakeSession:
        def get(self, url, headers=None): return FakeResp(summary)

    ns = {
        'API': 'https://k8s', 'CONTAINER': container,
        '_eph_peak': eph, '_anon_peak': anon, '_ws_peak': ws,
        '_peak_flushed': flushed, '_streaming': streaming_ref,
        'PEAK_FLUSH_RATIO': 1.05, 'STORAGE_MODE': 'ephemeral',
        'write_metrics': lambda e, a, v: written.append((e, a, v)),
        'token': lambda: 't',
        'logger': type('L', (), {'warning': lambda s, *a: None,
                                 'info': lambda s, *a: logged.append(a)})(),
    }
    if streaming:
        # The main loop records this when it opens a pod's stream; a peak flush
        # needs it to know which .metrics file the pod belongs to.
        for _p in summary.get('pods', []):
            streaming_ref[_p['podRef']['name']] = ('999', '1')
    exec(_extract(r"^(async def sample_kubelet\(.*?)(?=\n\nasync def )",
                  COLLECTOR_SRC).group(1), ns)
    import asyncio
    asyncio.run(ns['sample_kubelet'](FakeSession(), ['node-a']))
    _sample_ns.last = {'ws': ws, 'written': written, 'flushed': flushed}
    return eph, anon


def _payload(pod, rss, used=None, container='stellar-core'):
    mem = {} if rss is None else {'rssBytes': rss}
    return {'pods': [{'podRef': {'name': pod},
                      'ephemeral-storage': {} if used is None else {'usedBytes': used},
                      'containers': [{'name': container, 'memory': mem}]}]}


def test_kubelet_anon_is_tracked_as_a_high_water_mark():
    # A single low sample after a high one must not lower the peak: the whole
    # point is catching the spike, and download-phase anon oscillates.
    eph, anon = _sample_ns(_payload('p1', 900, used=5))
    assert anon == {'p1': 900} and eph == {'p1': 5}
    ns_hi = _payload('p1', 900)
    ns_hi['pods'][0]['containers'][0]['memory']['rssBytes'] = 400
    # re-run with a lower reading against a pre-seeded peak
    eph2, anon2 = _sample_ns({'pods': [
        _payload('p1', 900)['pods'][0], ns_hi['pods'][0]]})
    assert anon2['p1'] == 900, "a later, lower sample overwrote the peak"


def test_a_container_without_stats_yet_is_skipped_not_zeroed():
    # rssBytes is absent for the first seconds of a container's life. Recording
    # 0, or letting it raise, would either poison the peak or kill the sampler
    # for every other pod on the node.
    eph, anon = _sample_ns(_payload('p1', None, used=7))
    assert anon == {}, "missing rssBytes must not be recorded"
    assert eph == {'p1': 7}, "ephemeral must still be sampled"


def test_only_the_worker_container_is_measured():
    # Sidecars share the pod. Summing or last-wins across containers would size
    # the range from whichever one kubelet listed last.
    eph, anon = _sample_ns(_payload('p1', 900, container='istio-proxy'))
    assert anon == {}, "a non-worker container was measured"


def test_peak_anon_is_kept_from_every_attempt():
    # An OOM-killed pod's last sample is below its true peak by construction --
    # it died reaching past it. Feeding that into the profile would re-derive
    # the very limit that killed the range.
    # peaks_for_range takes the max across a resumed chain, so a partial
    # attempt can only raise the figure. Gating here is what hid the
    # download-phase peak of a range that resumed.
    body = _extract(r"(anon = _anon_peak\.pop\(pod, None\).*?)(?=\s+ws = _ws_peak)",
                    COLLECTOR_SRC).group(1)
    assert 'done_ok(pod)' not in body, "peakAnonBytes is still gated on success"


def test_peak_anon_reaches_the_profile():
    # peaks_for_range filters to PEAK_FIELDS, and the ConfigMap mirror strips
    # _PROFILE_ONLY_FIELDS. A new measurement absent from either is silently
    # dropped between the collector and the profile.
    for name in ('PEAK_FIELDS', '_PROFILE_ONLY_FIELDS'):
        blk = _extract(name + r" = \(([^)]+)\)").group(1)
        fields = {f.strip().strip("'") for f in blk.split(',') if f.strip()}
        assert 'peakAnonBytes' in fields, f"{name} drops peakAnonBytes"


def test_sizing_prefers_anon_and_falls_back_to_the_scraped_rss():
    # A profile captured before the collector tracked anon must keep sizing
    # exactly as it did, or every existing profile silently reverts to default.
    body = _extract(r"(rss = prof\.get\('peakAnonBytes'\).*?out\['memory'\])").group(1)
    assert "prof.get('peakAnonBytes') or prof.get('peakRssBytes')" in body


@pytest.mark.parametrize('peak,want_mi', [
    (648 * 1024**2, int(648 * 1.15) + 512),     # measured live: anon 648Mi
    (1467 * 1024**2, int(1467 * 1.15) + 512),   # the largest anon sampled
    (222 * 1024**2, int(222 * 1.15) + 512),     # the smallest
])
def test_the_sizing_formula_is_peak_times_115_plus_512mi(peak, want_mi):
    margin = float(_extract(r"PROFILE_MARGIN = float\(os\.getenv\('PROFILE_MARGIN', ([\d.]+)\)\)").group(1))
    head = _extract(r"PROFILE_CACHE_HEADROOM = os\.getenv\('PROFILE_CACHE_HEADROOM', '(\d+)Mi'\)").group(1)
    got_mi = int(peak * margin) // 1024**2 + int(head)
    assert (margin, int(head)) == (1.15, 512)
    assert got_mi == want_mi


def test_the_chart_matches_the_new_sizing_defaults():
    chart = open(__file__.replace(
        'test_job_monitor.py', 'parallel_catchup_helm/values.yaml')).read()
    assert _extract(r"profileMargin: ([\d.]+)", chart).group(1) == \
           _extract(r"PROFILE_MARGIN = float\(os\.getenv\('PROFILE_MARGIN', ([\d.]+)\)\)").group(1)
    assert _extract(r'profileCacheHeadroom: "(\d+Mi)"', chart).group(1) == \
           _extract(r"PROFILE_CACHE_HEADROOM = os\.getenv\('PROFILE_CACHE_HEADROOM', '(\d+Mi)'\)").group(1)


# --- zombie streams -------------------------------------------------------
# `done` reads terminal.get(pod, False) and terminal is only written for pods
# present in list_pods. A pod deleted while Running -- reaped node, eviction,
# or the monitor deleting a finished Job -- therefore never became terminal,
# and its stream retried every 30s for the rest of the run while holding one of
# MAX_CONCURRENT connection slots.

def test_a_vanished_pod_is_marked_terminal_so_its_stream_can_finish():
    body = _extract(r"(live = \{p\['metadata'\]\['name'\].*?)(?=\n\s+# Unconditional:)",
                    COLLECTOR_SRC).group(1)
    assert 'terminal[name] = True' in body, \
        "a vanished pod never becomes terminal, so done() stays False forever"
    assert 'n not in live' in body, "nothing detects a pod leaving the pod list"


def test_a_vanished_stream_is_cancelled_if_it_will_not_finish():
    # Marking terminal is not enough on its own: a stream blocked inside a
    # connection attempt never reaches its done() check, which is exactly the
    # state that starves every other stream.
    body = _extract(r"(live = \{p\['metadata'\]\['name'\].*?)(?=\n\s+# Unconditional:)",
                    COLLECTOR_SRC).group(1)
    assert 't.cancel()' in body and 'VANISHED_GRACE_CYCLES' in body, \
        "no backstop cancel for a stream that cannot finalize"
    assert 'del tasks[name]' in body, "cancelled task is never removed from tasks"


def test_the_grace_is_more_than_one_cycle():
    # A stream mid-fetch_peaks against a slow Prometheus must not be cancelled
    # out from under its own metrics write.
    n = int(_extract(r"VANISHED_GRACE_CYCLES = int\(os\.getenv\('COLLECTOR_VANISHED_GRACE_CYCLES', (\d+)\)\)",
                     COLLECTOR_SRC).group(1))
    assert n >= 2, f"grace of {n} cycle(s) can cancel a stream mid-finalize"



def test_both_exit_paths_share_one_finalize():
    # Two copies of the metrics/discard logic is how one path silently stops
    # writing peakAnonBytes while the other keeps working.
    # Three: clean exit, pod-gone 404, and an interrupted read on a pod that
    # has since gone terminal.
    # Three: pod gone (404), the pod was terminal before the poll that just
    # succeeded, and a terminal pod whose polls keep failing.
    assert len(re.findall(r"await finalize\(session, pod, end, attempt, tx, done_ok, started\)",
                          COLLECTOR_SRC)) == 3
    assert len(re.findall(r"write_metrics\(end, attempt, measured\)", COLLECTOR_SRC)) == 1



def _run_stream_pod(status, terminal):
    """Execute poll_pod against a fake apiserver. Returns finalize calls.

    Executed rather than pattern-matched: an earlier version of these tests
    asserted on an `except ClientResponseError` branch that raise_for_status
    could never reach, and passed against dead code.
    """
    import asyncio, tempfile, os as _os, gzip as _gzip
    calls = []

    class FakeResp:
        status = None
        async def __aenter__(self): return self
        async def __aexit__(self, *a): return False
        def raise_for_status(self):
            if self.status >= 400:
                raise OSError(f"HTTP {self.status}")
        @property
        def content(self):
            class C:
                async def iter_chunked(self, n):
                    if False:
                        yield b''
            return C()

    FakeResp.status = status      # class bodies cannot close over a local

    class FakeSession:
        def get(self, url, params=None, headers=None): return FakeResp()

    async def fake_finalize(session, pod, end, attempt, tx, done_ok, started=None):
        calls.append((pod, end, attempt))

    d = tempfile.mkdtemp()
    ns = {
        'asyncio': asyncio, 'gzip': _gzip, 're': re, 'os': _os,
        'API': 'https://k8s', 'NAMESPACE': 'ns', 'CONTAINER': 'stellar-core',
        'LOG_DIR': d, 'LOG_POLL_SECONDS': 0.05, 'MAX_POLL_CHARS': 1 << 20,
        'TERMINAL_POLL_ATTEMPTS': 3,
        '_poll_slots': asyncio.Semaphore(4), '_wake': {},
        'token': lambda: 't', 'finalize': fake_finalize,
        'base': lambda e, a: _os.path.join(d, f"range-{e}-a{a}"),
        'read_state': lambda e, a: None, 'write_state': lambda e, a, ts: None,
        '_TS_RE': re.compile(r"^\d{4}"),
        'TxApplyScanner': type('T', (), {'seconds': None, 'resumed': False,
                                         'feed': lambda s, l: None}),
        'logger': type('L', (), {'info': lambda s, *a: None,
                                 'warning': lambda s, *a: None})(),
    }
    exec(_extract(r"^(async def _poll_once\(.*?)(?=\n\nasync def )",
                  COLLECTOR_SRC).group(1), ns)
    exec(_extract(r"^(async def poll_pod\(.*?)(?=\n\nasync def )",
                  COLLECTOR_SRC).group(1), ns)
    coro = ns['poll_pod'](FakeSession(), 'pod-1', '999', '1',
                          lambda p: terminal, lambda p: False)
    asyncio.run(asyncio.wait_for(coro, timeout=2))
    return calls


def test_a_404_finalizes_what_was_already_streamed():
    # The pod object is gone, but the bytes already read still owe a tx_apply
    # and the peaks live in Prometheus, not on the pod.
    assert _run_stream_pod(404, terminal=False) == [('pod-1', '999', '1')]


def test_an_interrupted_read_on_a_terminal_pod_still_finalizes():
    # 500s were a burst at ramp. Returning bare here dropped the metrics for
    # every range whose last read happened to throw.
    assert _run_stream_pod(500, terminal=True) == [('pod-1', '999', '1')]


def test_an_interrupted_read_on_a_live_pod_does_not_finalize():
    # Still running: retry is correct, and finalizing now would write a
    # truncated peak and let the range look measured when it is not.
    import pytest as _pt
    with _pt.raises(Exception):
        _run_stream_pod(500, terminal=False)   # retries until the 2s timeout


# --- kubelet replaces Prometheus ------------------------------------------
# Every peak the profile uses now comes from the kubelet payload the collector
# already fetches. Prometheus was lossy for this: a 30s scrape against ~10s
# cAdvisor housekeeping, plus a hard dependency on Prometheus being up,
# reachable and still retaining the window -- and _promql swallowed all three
# failures into "no peak", so an outage produced a complete-looking, empty
# profile.

def test_the_collector_no_longer_reads_from_prometheus():
    # Comments stripped: one deliberately explains why the local high-water
    # dict exists where max_over_time did not need to.
    code = '\n'.join(l for l in COLLECTOR_SRC.splitlines()
                     if not l.lstrip().startswith('#'))
    for token in ('PROMETHEUS_URL', '_promql', 'fetch_peaks', 'max_over_time'):
        assert token not in code, f"{token} survived the kubelet switch"


def test_cpu_is_not_profiled():
    # REQ_CPU is fixed, so a measured cpu value has nothing to size and only
    # makes packing non-uniform.
    assert 'peakCpuCores' not in _extract(r"PEAK_FIELDS = \(([^)]+)\)").group(1)
    body = _extract(r"^(def _profile_overrides\(.*?)(?=\ndef )").group(1)
    assert "out['cpu']" not in body


def test_memory_is_sampled_in_both_storage_modes():
    # This was gated on ephemeral mode back when the sampler only did disk,
    # which left every pvc run with no anon peak at all.
    loop = _extract(r"while True:\n(.*?)await asyncio\.sleep\(POLL_SECONDS\)",
                    COLLECTOR_SRC).group(1)
    call = loop.index('sample_kubelet')
    gate = loop.rfind("STORAGE_MODE == 'ephemeral'", 0, call)
    assert gate == -1, "the kubelet sampler is still gated on storage mode"


def test_the_disk_axis_stays_mode_gated():
    # ephemeral-storage is meaningless in pvc mode: /data is not on the node.
    fn = _extract(r"^(async def sample_kubelet\(.*?)(?=\n\nasync def )",
                  COLLECTOR_SRC).group(1)
    used = fn.index("get('usedBytes')")
    assert "STORAGE_MODE == 'ephemeral'" in fn[used:used + 200]





def test_an_in_flight_peak_is_flushed_so_a_restart_cannot_lose_it():
    # Prometheus computed max_over_time server-side and needed no state. A local
    # high-water dict does: without a flush, a collector restart resets a range's
    # peak to whatever it is using at that moment, which under-reports and sizes
    # the next run too small. Executed, not pattern-matched -- `if False:` leaves
    # every identifier in place and passes a source-text check.
    _sample_ns(_payload('p1', 900, used=5))
    w = _sample_ns.last['written']
    assert w, "a first sample never flushed its peak"
    assert w[-1][2] == {'peakAnonBytes': 900}


def test_a_peak_that_barely_grows_is_not_reflushed():
    # One write per sample per pod, at 2048 pods, would be the dominant cost of
    # the sampler. Only growth past PEAK_FLUSH_RATIO earns a write.
    pods = [_payload('p1', 900)['pods'][0], _payload('p1', 910)['pods'][0]]
    _sample_ns({'pods': pods})
    assert len(_sample_ns.last['written']) == 1, "a 1.1% rise triggered a second flush"

    pods = [_payload('p1', 900)['pods'][0], _payload('p1', 2000)['pods'][0]]
    _sample_ns({'pods': pods})
    assert len(_sample_ns.last['written']) == 2, "a 2.2x rise did not flush"


def test_working_set_is_sampled_recorded_but_never_sizes_anything():
    # It counts active page cache, which grows to fill the limit -- measured at
    # 3.61/7.48/13.49 GiB for one range under 4Gi/8Gi/24000Mi limits while rss
    # held at ~2.4 GiB. Useful as a diagnostic, never as a request.
    p = _payload('p1', 900, used=5)
    p['pods'][0]['containers'][0]['memory']['workingSetBytes'] = 4096
    _sample_ns(p)
    assert _sample_ns.last['ws'] == {'p1': 4096}, "working set is not sampled"
    assert 'peakWorkingSetBytes' in _extract(r"PEAK_FIELDS = \(([^)]+)\)").group(1)
    body = _extract(r"^(def _profile_overrides\(.*?)(?=\ndef )").group(1)
    assert 'peakWorkingSetBytes' not in body, "working set must not size a request"


def test_finalize_records_the_working_set_peak():
    # Sampling it is useless if finalize drops it on the floor.
    fn = _extract(r"^(async def finalize\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    written, ws = [], {'pod-1': 4096}
    ns = {
        '_anon_peak': {'pod-1': 900}, '_ws_peak': ws, '_eph_peak': {},
        '_peak_flushed': {}, '_streaming': {}, '_wake': {}, 'SAVE_SUCCESS_LOGS': True,
        'write_metrics': lambda e, a, v: written.append(v),
        'discard': lambda e, a: None, '_mark_done': lambda e, a: None,
        'logger': type('L', (), {'info': lambda s, *a: None})(),
    }
    exec(fn, ns)
    import asyncio
    tx = type('T', (), {'seconds': 1.5, 'resumed': False})()
    asyncio.run(ns['finalize'](None, 'pod-1', '999', '1', tx, lambda p: True))
    assert written and written[0].get('peakWorkingSetBytes') == 4096
    assert written[0].get('peakAnonBytes') == 900


def test_the_flush_ratio_default_is_above_one_and_matches_the_chart():
    # The behaviour tests inject their own ratio, so nothing else pins the
    # default. At exactly 1.0 every sample flushes: one write per pod per poll,
    # 2048 pods, which is the cost the ratio exists to avoid.
    got = float(_extract(
        r"PEAK_FLUSH_RATIO = float\(os\.getenv\('PEAK_FLUSH_RATIO', ([\d.]+)\)\)",
        COLLECTOR_SRC).group(1))
    assert got > 1.0, f"ratio {got} flushes on every sample"
    chart = open(__file__.replace(
        'test_job_monitor.py', 'parallel_catchup_helm/values.yaml')).read()
    assert float(_extract(r"peakFlushRatio: ([\d.]+)", chart).group(1)) == got


# --- peaks aggregate across attempts --------------------------------------
# In pvc mode a pod killed after replay starts leaves /data, and the next
# attempt resumes at LCL+1 with RESUME=true -- skipping the archive download and
# bucket apply, which is where peak memory happens. Profiling only the winning
# attempt therefore under-reports a resumed range by the whole download gap, and
# on spot (where eviction is routine and resume is the point of durable /data)
# that would make the run unprofileable.

def _peaks_ns(attempts):
    """Exec peaks_for_range over a temp dir. attempts: {n: (metrics, outcome)}.

    `resumed` lives in the metrics dict, as the collector writes it.
    """
    import tempfile, json as _json, os as _os
    d = tempfile.mkdtemp()
    for n, (metrics, outcome) in attempts.items():
        if metrics is not None:
            with open(_os.path.join(d, f"m-{n}"), 'w') as fh:
                fh.write(metrics if isinstance(metrics, str) else _json.dumps(metrics))
        if outcome is not None:
            with open(_os.path.join(d, f"o-{n}"), 'w') as fh:
                _json.dump(outcome, fh)
    ns = {
        'json': _json,
        'metrics_path': lambda e, n: _os.path.join(d, f"m-{n}"),
        'outcome_path': lambda e, n: _os.path.join(d, f"o-{n}"),
        'PEAK_FIELDS': ('peakAnonBytes', 'peakRssBytes', 'peakWorkingSetBytes',
                        'peakEphemeralBytes'),
    }
    for name in ('read_outcome', '_attempt_resumed', '_resumed_chain',
                 '_hit_a_ceiling', '_peak_attempts'):
        exec(_extract(r"^(def " + name + r"\(.*?)(?=\ndef )").group(1), ns)
    exec(_extract(r"^(def peaks_for_range\(.*?)(?=\ndef )").group(1), ns)
    return ns['peaks_for_range']


def test_a_resumed_range_keeps_the_peak_from_the_attempt_that_did_the_download():
    # a1 evicted mid-replay having already done the download; a2 resumes at
    # LCL+1 and only replays the tail. a2 alone would report 400MiB for a range
    # that really needs 2GiB.
    f = _peaks_ns({
        1: ({'peakAnonBytes': 2 * 1024**3}, {'outcome': 'disrupted'}),
        2: ({'peakAnonBytes': 400 * 1024**2, 'resumed': True}, None),
    })
    assert f(999, 2)['peakAnonBytes'] == 2 * 1024**3


def test_an_oom_killed_attempt_still_counts_toward_the_peak():
    # It really did allocate ~8Gi and wanted more, so that is a lower bound on
    # demand. Sizing off the quieter successful attempt instead would OOM the
    # range again; 8Gi * 1.15 + 512Mi clears the level it died at.
    f = _peaks_ns({
        1: ({'peakAnonBytes': 8 * 1024**3}, {'outcome': 'oom'}),
        2: ({'peakAnonBytes': 900 * 1024**2, 'resumed': True}, None),
    })
    assert f(999, 2)['peakAnonBytes'] == 8 * 1024**3


def test_an_oom_killed_attempt_still_counts_on_the_disk_axis():
    # It hit the memory ceiling, not the disk one, so its disk figure is real.
    f = _peaks_ns({
        1: ({'peakEphemeralBytes': 30 * 1024**3}, {'outcome': 'oom'}),
        2: ({'peakEphemeralBytes': 5 * 1024**3, 'resumed': True}, None),
    })
    assert f(999, 2)['peakEphemeralBytes'] == 30 * 1024**3


def test_a_disk_evicted_attempt_counts_on_every_axis():
    f = _peaks_ns({
        1: ({'peakEphemeralBytes': 40 * 1024**3,
             'peakAnonBytes': 3 * 1024**3}, {'outcome': 'ephemeral'}),
        2: ({'peakEphemeralBytes': 9 * 1024**3,
             'peakAnonBytes': 1 * 1024**3, 'resumed': True}, None),
    })
    out = f(999, 2)
    assert out['peakEphemeralBytes'] == 40 * 1024**3
    assert out['peakAnonBytes'] == 3 * 1024**3


def test_a_missing_or_malformed_metrics_file_is_tolerated():
    f = _peaks_ns({1: (None, None), 2: ("not json at all", None),
                   3: ({'peakAnonBytes': 5, 'resumed': True}, None)})
    assert f(999, 3) == {'peakAnonBytes': 5}
    assert _peaks_ns({})(999, 3) == {}


def test_an_absent_peak_never_reaches_the_profile_as_a_null():
    # The consumer falls back to a default on a missing field, so a null defeats it.
    f = _peaks_ns({1: ({'peakAnonBytes': None, 'peakRssBytes': 7}, None)})
    assert f(999, 1) == {'peakRssBytes': 7}


def test_spot_is_never_excluded_as_a_capacity_type():
    # Truncation is what invalidates a sample, not the node it ran on. Gating on
    # spot would blank the axis for an all-spot run, the run we most want.
    assert 'capacity-type' not in COLLECTOR_SRC
    assert 'capacity-type' not in SRC


def test_a_fresh_retry_supersedes_an_interrupted_one():
    # No RESUME line means new-db ran and this attempt did the whole range, so
    # its sample is complete. An earlier attempt that was merely interrupted
    # measured the same work and only adds noise.
    f = _peaks_ns({
        1: ({'peakAnonBytes': 8 * 1024**3}, {'outcome': 'disrupted'}),
        2: ({'peakAnonBytes': 900 * 1024**2}, None),      # no 'resumed'
    })
    assert f(999, 2)['peakAnonBytes'] == 900 * 1024**2


def test_the_chain_stops_at_the_last_fresh_start():
    # a1 interrupted then superseded by a fresh a2; a3 resumed from a2. Only
    # a2+a3 describe the same continuous pass over the range.
    f = _peaks_ns({
        1: ({'peakAnonBytes': 9 * 1024**3}, {'outcome': 'disrupted'}),
        2: ({'peakAnonBytes': 2 * 1024**3}, {'outcome': 'disrupted'}),
        3: ({'peakAnonBytes': 500 * 1024**2, 'resumed': True}, None),
    })
    assert f(999, 3)['peakAnonBytes'] == 2 * 1024**3


def test_resumed_is_read_from_the_workers_own_line():
    # "RESUME DECLINED" must not count as a resume -- it means the opposite.
    scanner_src = _extract(r"^(class TxApplyScanner:.*?)(?=\ndef )", COLLECTOR_SRC).group(1)
    assert "RESUME_MARK = 'RESUME: '" in scanner_src
    ns = {'_TX_METRIC': "metric 'ledger.transaction.apply'", '_SUM_RE': SUM_RE}
    exec(scanner_src, ns)
    s = ns['TxApplyScanner']()
    s.feed("RESUME DECLINED: k last close was 'none'; bucket phase incomplete, starting fresh")
    assert s.resumed is False, "a declined resume was read as a resume"
    s.feed("RESUME: k reached ledger 31005951, replay had started; skipping new-db")
    assert s.resumed is True


def test_resumed_never_reaches_the_profile_as_a_field():
    # It is bookkeeping for peaks_for_range, not a measurement.
    assert 'resumed' not in _extract(r"PEAK_FIELDS = \(([^)]+)\)").group(1)


def test_finalize_records_that_an_attempt_resumed():
    # Without this in .metrics, peaks_for_range cannot tell a resumed tail from
    # a complete pass, and every resumed range is profiled off its tail alone.
    fn = _extract(r"^(async def finalize\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    import asyncio

    def run(resumed):
        written = []
        ns = {'_anon_peak': {'p': 1}, '_ws_peak': {}, '_eph_peak': {},
              '_peak_flushed': {}, '_streaming': {}, '_wake': {}, 'SAVE_SUCCESS_LOGS': True,
              'write_metrics': lambda e, a, v: written.append(v),
              'discard': lambda e, a: None, '_mark_done': lambda e, a: None,
              'logger': type('L', (), {'info': lambda s, *a: None})()}
        exec(fn, ns)
        tx = type('T', (), {'seconds': None, 'resumed': resumed})()
        asyncio.run(ns['finalize'](None, 'p', '999', '1', tx, lambda p: True))
        return written[0]

    assert run(True).get('resumed') is True
    assert 'resumed' not in run(False), "a fresh attempt must not be marked resumed"


# --- timings aggregate across the resumed chain too ------------------------
# medida's total is per-process and a pod's duration is its own, so both are
# tail-only for a resumed range in exactly the way the peaks were.

def _chain_ns(attempts, extra=None):
    """Exec the chain helpers over a temp dir. attempts: {n: (metrics, outcome)}."""
    import tempfile, json as _json, os as _os
    d = tempfile.mkdtemp()
    for n, (metrics, outcome) in attempts.items():
        if metrics is not None:
            with open(_os.path.join(d, f"m-{n}"), 'w') as fh:
                _json.dump(metrics, fh)
        if outcome is not None:
            with open(_os.path.join(d, f"o-{n}"), 'w') as fh:
                _json.dump(outcome, fh)
    ns = {
        'json': _json,
        'metrics_path': lambda e, n: _os.path.join(d, f"m-{n}"),
        'outcome_path': lambda e, n: _os.path.join(d, f"o-{n}"),
    }
    for name in ('_attempt_resumed', '_resumed_chain', 'read_outcome',
                 'seconds_for_range'):
        ns[name] = None
    exec(_extract(r"^(def _attempt_resumed\(.*?)(?=\ndef )").group(1), ns)
    exec(_extract(r"^(def _resumed_chain\(.*?)(?=\ndef )").group(1), ns)
    exec(_extract(r"^(def read_outcome\(.*?)(?=\ndef )").group(1), ns)
    exec(_extract(r"^(def seconds_for_range\(.*?)(?=\ndef )").group(1), ns)
    ns.update(extra or {})
    return ns


def test_seconds_sums_the_whole_resumed_chain():
    # a1 ran 900s then was evicted mid-replay; a2 resumed and took 300s. The
    # range cost 1200s of compute, not 300.
    ns = _chain_ns({
        1: ({}, {'outcome': 'disrupted', 'attemptSeconds': 900.0}),
        2: ({'resumed': True}, None),
    })
    assert ns['seconds_for_range'](999, 2, 300.0) == 1200.0


def test_seconds_ignores_attempts_before_a_fresh_start():
    # a2 ran new-db and did the whole range itself, so a1's 900s is not part of
    # the same pass.
    ns = _chain_ns({
        1: ({}, {'outcome': 'oom', 'attemptSeconds': 900.0}),
        2: ({}, None),          # no 'resumed'
    })
    assert ns['seconds_for_range'](999, 2, 300.0) == 300.0


def test_seconds_survives_a_leg_with_no_recorded_duration():
    # An attempt whose pod vanished before it was classified has no
    # attemptSeconds. Better to under-report one leg than return nothing.
    ns = _chain_ns({
        1: ({}, {'outcome': 'disrupted'}),        # no attemptSeconds
        2: ({'resumed': True}, None),
    })
    assert ns['seconds_for_range'](999, 2, 300.0) == 300.0


def test_seconds_is_none_when_nothing_is_known():
    ns = _chain_ns({1: ({}, None)})
    assert ns['seconds_for_range'](999, 1, None) is None


def test_a_failed_attempts_duration_is_persisted_with_its_verdict():
    # The only moment it is available: reconcile computes `seconds` solely on
    # the success path, and the pod is about to be reaped.
    fn = _extract(r"^(def record_outcome\(.*?)(?=\ndef )").group(1)
    assert "data['attemptSeconds'] = _pod_seconds(pod)" in fn


def test_tx_apply_sums_the_chain_and_offers_fallbacks_to_the_last_leg_only():
    # pod_name names the winning attempt's pod; handing it to an earlier leg
    # would read the wrong pod's log.
    fn = _extract(r"^(def tx_apply_for_range\(.*?)(?=\ndef )").group(1)
    assert '_resumed_chain(end, attempt)' in fn
    assert 'pod_name if n == int(attempt) else None' in fn
    assert 'total + leg' in fn, "legs are summed, not maxed"


def test_an_unclassifiable_job_failure_is_retried_not_condemned():
    # BackoffLimitExceeded carries no rule index and no exit code, so classify()
    # honestly returns nothing. That must not read as "this range is bad": a
    # monitor restart while a node was reaped produces exactly this, and
    # condemning on it would fail a 10-hour job on no evidence.
    assert classify("Job has reached the specified backoff limit") == (None, None, None)
    env = set(re.findall(r"'(\w+)'", _extract(r"ENVIRONMENTAL_OUTCOMES = \(([^)]+)\)").group(1)))
    assert 'unknown' in env, "an unclassified failure must get the environmental budget"
    assert {'disrupted', 'rejected'} <= env, "cluster-caused outcomes share that budget"
    # ...and the environmental budget is the most generous of the three.
    body = _extract(r"(if verdict\['outcome'\] == 'timeout':\s*\n\s*cap = .*?)(?=\n\s+if reason)").group(1)
    assert 'ENVIRONMENTAL_OUTCOMES' in body and 'MAX_DISRUPTION_ATTEMPTS' in body


def test_only_a_genuine_catchup_failure_is_condemned():
    # `failed` is the one outcome with no retry reason. Everything else -- oom,
    # ephemeral, timeout, and all three environmental outcomes -- sets one.
    body = _extract(r"(if verdict\['outcome'\] == 'timeout':.*?reason = None[^\n]*)").group(1)
    assert body.rstrip().endswith("reason = None   # genuine catchup failure: do not retry"), \
        "a genuine catchup failure must be the only unretried outcome"
    # every other branch in that chain sets a reason, i.e. retries
    for outcome in ('rejected', 'disrupted', 'oom', 'ephemeral', 'unknown'):
        assert f"== '{outcome}'" in body, f"{outcome} left the retry chain"


# --- gaps found by a mutation sweep, 2026-07-30 ----------------------------
# Each of these guards a decision the design depends on, and each was mutable
# without breaking a single test before this block existed.

def test_the_job_controller_never_owns_retries():
    # backoffLimit 0 is load-bearing: above 0 the Job controller replaces the pod
    # on its own schedule, so we could not classify disruption vs catchup
    # failure, could not count evictions, and could not guarantee the log was
    # archived before the next attempt started.
    spec = _extract(r"spec=client\.V1JobSpec\((.*?)template=").group(1)
    assert re.search(r"backoff_limit\s*=\s*0\b", spec), "backoffLimit must be 0"
    assert re.search(r"ttl_seconds_after_finished\s*=\s*JOB_TTL_SECONDS", spec), \
        "finished Jobs need a TTL backstop even though reconcile deletes them"


def test_a_worker_pod_is_never_restarted_in_place():
    # restartPolicy OnFailure restarts the container inside the same pod, which
    # keeps the pod name and reuses the same resource limits -- so an OOM would
    # loop forever at the limit that killed it instead of escalating, and the
    # attempt counter would never advance.
    spec = _extract(r"spec=client\.V1PodSpec\((.*?)containers=\[container\]").group(1)
    assert "restart_policy='Never'" in spec


@pytest.mark.parametrize('attempt,want', [(1, 1.0), (2, 1.5), (3, 2.25), (4, 3.375)])
def test_the_memory_escalation_ladder_compounds(attempt, want):
    # 1.5x per attempt off what the attempt actually ran with. A factor of 1.0
    # would retry an OOM at the identical limit, forever.
    ns = {'os': __import__('os'), 're': re}
    for name in ('_quantity_bytes', '_bytes_to_quantity', 'mem_for_attempt'):
        exec(_extract(r"^(def " + name + r"\(.*?)(?=\ndef )").group(1), ns)
    ns['_UNITS'] = {'Ki': 1024, 'Mi': 1024**2, 'Gi': 1024**3, 'Ti': 1024**4,
                    'K': 1000, 'M': 1000**2, 'G': 1000**3, 'T': 1000**4}
    ns['MEM_BUMP_FACTOR'] = float(_extract(
        r"MEM_BUMP_FACTOR = float\(os\.getenv\('MEM_BUMP_FACTOR', ([\d.]+)\)\)").group(1))
    ns['MEM_ESCALATION_CAP'] = '48Gi'
    ns['LIM_MEM'] = '1000Mi'
    got = ns['mem_for_attempt'](attempt, '1000Mi')
    assert got == f"{int(1000 * want)}Mi", got


def test_the_escalation_ladder_is_capped():
    ns = {'os': __import__('os'), 're': re}
    for name in ('_quantity_bytes', '_bytes_to_quantity', 'mem_for_attempt'):
        exec(_extract(r"^(def " + name + r"\(.*?)(?=\ndef )").group(1), ns)
    ns['_UNITS'] = {'Ki': 1024, 'Mi': 1024**2, 'Gi': 1024**3, 'Ti': 1024**4,
                    'K': 1000, 'M': 1000**2, 'G': 1000**3, 'T': 1000**4}
    ns['MEM_BUMP_FACTOR'] = 1.5
    ns['MEM_ESCALATION_CAP'] = '4Gi'
    ns['LIM_MEM'] = '1000Mi'
    assert ns['mem_for_attempt'](20, '1000Mi') == '4096Mi', "cap not applied"


def test_progress_is_written_atomically():
    # The mission reads progress.json off the volume while the monitor is still
    # writing it. A partial file is unparseable JSON, which reads as "no
    # progress" -- and reconcile halts the run when progress goes backwards.
    fn = _extract(r"^(def save_progress\(.*?)(?=\ndef )").group(1)
    assert '.tmp' in fn and 'os.replace(' in fn, "progress.json is not written atomically"
    assert fn.index('.tmp') < fn.index('os.replace('), "replace must follow the temp write"


def test_the_log_stream_resumes_from_the_last_durable_timestamp():
    # Without sinceTime a reconnect re-reads the whole log from the start: one
    # full re-read per pod per reconnect, at 2096 pods.
    fn = _extract(r"^(async def _poll_once\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert "params['sinceTime']" in fn, "a poll does not resume from the last durable line"
    # ...and the second-granularity overlap it creates is removed per line.
    assert re.search(r"if last_ts and ts <= last_ts:\s*\n\s*continue", fn), \
        "the deliberate resume overlap is never deduped"


def test_every_durable_write_is_atomic():
    # Three writers put files on the shared volume while the mission and the
    # collector read them. A half-written .outcome or archive is unparseable,
    # and an unreadable outcome downgrades a classified failure to "unknown".
    for fn_name in ('save_progress', 'backstop_save_pod_log', 'record_outcome',
                    'write_metrics'):
        for src in (SRC, COLLECTOR_SRC):
            m = re.search(r"^(def " + fn_name + r"\(.*?)(?=\ndef )", src, re.S | re.M)
            if m:
                break
        assert m, f"{fn_name} not found"
        body = m.group(1)
        assert '.tmp' in body, f"{fn_name} does not write via a temp file"
        assert 'os.replace(' in body, f"{fn_name} does not rename atomically"
        assert body.index('.tmp') < body.index('os.replace('), \
            f"{fn_name} renames before it writes"


def test_attempt_budgets_are_ordered_by_whose_fault_the_failure_was():
    # A hang is usually persistent, so it gets the fewest tries. A genuinely
    # broken range gets the middle budget. Anything the cluster did to us gets
    # the most -- on spot, evictions are routine and must not condemn a range.
    def const(name, env):
        return int(_extract(name + r" = int\(os\.getenv\('" + env + r"', (\d+)\)\)").group(1))
    timeout = const('MAX_TIMEOUT_ATTEMPTS', 'MAX_TIMEOUT_ATTEMPTS')
    per_range = const('MAX_ATTEMPTS_PER_RANGE', 'MAX_ATTEMPTS')
    disruption = const('MAX_DISRUPTION_ATTEMPTS', 'MAX_DISRUPTION_ATTEMPTS')
    ephemeral = const('MAX_EPHEMERAL_ATTEMPTS', 'MAX_EPHEMERAL_ATTEMPTS')
    assert timeout < per_range < disruption, \
        f"budgets out of order: timeout={timeout} range={per_range} disruption={disruption}"
    assert per_range > 1, "a range that OOMs once could never escalate"
    assert ephemeral > 1, "a range evicted on disk once could never grow"
    assert disruption >= 10, "spot eviction would condemn ranges at this budget"


def test_the_collector_records_a_duration_the_monitor_cannot():
    # Measured on ssc-test 2026-07-30: 212 of 212 spot disruptions were
    # classified from the Job condition with the pod already reaped, so
    # record_outcome never ran and no .outcome carried attemptSeconds. Peaks
    # survived (the collector writes .metrics regardless) but the chain's time
    # total silently lost every evicted leg. This process watched the container
    # run, so it is the only observer left.
    fn = _extract(r"^(async def finalize\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert "measured['attemptSeconds']" in fn
    import asyncio
    written = []
    ns = {'asyncio': asyncio, '_anon_peak': {}, '_ws_peak': {}, '_eph_peak': {},
          '_peak_flushed': {}, '_streaming': {}, '_wake': {}, 'SAVE_SUCCESS_LOGS': True,
          'write_metrics': lambda e, a, v: written.append(v),
          'discard': lambda e, a: None, '_mark_done': lambda e, a: None,
          'logger': type('L', (), {'info': lambda s, *a: None})()}
    exec(fn, ns)
    tx = type('T', (), {'seconds': None, 'resumed': False})()
    async def go():
        now = asyncio.get_event_loop().time()
        await ns['finalize'](None, 'p', '999', '1', tx, lambda p: True, now - 42.0)
    asyncio.run(go())
    assert written and written[0]['attemptSeconds'] == pytest.approx(42.0, abs=1.0)


def test_seconds_falls_back_to_the_collectors_figure():
    # The authoritative .outcome is missing for every reaped pod. Without this
    # fallback the chain drops that leg entirely and under-reports the range.
    ns = _chain_ns({
        1: ({'attemptSeconds': 850.0}, None),          # no .outcome at all
        2: ({'resumed': True}, None),
    })
    assert ns['seconds_for_range'](999, 2, 300.0) == 1150.0


def test_the_authoritative_outcome_wins_over_the_collector_estimate():
    # .outcome comes from the pod's terminated timestamps; the collector's is a
    # stream-lifetime approximation that starts up to one poll late.
    ns = _chain_ns({
        1: ({'attemptSeconds': 850.0}, {'outcome': 'disrupted', 'attemptSeconds': 900.0}),
        2: ({'resumed': True}, None),
    })
    assert ns['seconds_for_range'](999, 2, 300.0) == 1200.0


# --- a worker must not be able to kill its own log stream ------------------
# Found live on the 2096-worker spot run: the AWS CLI draws its transfer meter
# with carriage returns and no newline, so a 628 MiB bucket download arrives as
# one multi-megabyte "line". aiohttp raises over 512 KiB, every large download
# killed its own stream, and the reconnect hit the same wall -- which starved
# every retry pod of a collector stream and left a2 metrics empty.

def test_the_stream_is_read_in_chunks_not_lines():
    fn = _extract(r"^(async def _poll_once\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert 'iter_chunked' in fn, "line-wise reads are bounded by aiohttp's 512KiB limit"
    assert 'async for raw in resp.content:' not in fn
    assert "'follow'" not in fn, "a poll must not follow"


def test_carriage_returns_split_lines_too():
    # The progress meter is \r-delimited. Without \r in the split it stays one
    # blob no matter how the bytes arrive.
    fn = _extract(r"^(async def _poll_once\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    m = re.search(r"re\.split\(r'\[([^\]]+)\]'", fn)
    assert m, "no line splitting found"
    assert '\\r' in m.group(1) and '\\n' in m.group(1), f"splits on {m.group(1)!r}"


def test_an_unterminated_blob_is_capped_not_buffered_forever():
    # A meter that never emits a newline would otherwise grow the buffer until
    # the collector OOMs -- 2096 streams doing it at once.
    fn = _extract(r"^(async def _poll_once\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert 'MAX_POLL_CHARS' in fn, "a single poll response is unbounded"
    cap = int(_extract(r"MAX_LINE_CHARS = int\(os\.getenv\('MAX_LINE_CHARS', (\d+)\)\)",
                       COLLECTOR_SRC).group(1))
    assert 1024 < cap < 524288, f"cap {cap} is outside a sane range"


def test_the_worker_disables_the_aws_progress_meter():
    # The real cure: never emit the \r spam. Also keeps it out of the archives,
    # where it was the bulk of every large range's log.
    fs = open(__file__.replace(
        'src/MissionParallelCatchup/test_job_monitor.py',
        'src/FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs')).read()
    m = re.search(r'sprintf "aws s3 cp ([^"]*)--region %s"', fs)
    assert m, "s3 GET command not found"
    assert '--no-progress' in m.group(1), f"aws s3 cp flags: {m.group(1)!r}"


def test_a_failed_attempt_is_not_reaped_before_the_collector_finalizes_it():
    # delete_job reaps the pod, and backstop_save_pod_log stands down for any
    # range the collector claimed -- so nothing else would ever read that log.
    # Under follow=true the collector already holds everything; under polling it
    # would lose the last interval. Gate it either way.
    body = _extract(r"(try:\s*\n\s*batch_v1\.create_namespaced_job.*?)continue").group(1)
    assert 'delete_job(end, attempt)' in body
    assert re.search(r"if _attempt_finalized\(end, attempt\):\s*\n\s*delete_job\(end, attempt\)", body), \
        "the retry-path reap is not gated on the collector's done marker"
    assert body.index('create_namespaced_job') < body.index('delete_job('), \
        "successor must exist before the predecessor is reaped"


def test_the_line_buffer_cap_is_charged_per_stream():
    # Measured on ssc-test at 2096 follow streams: 1444 MiB of a 2048 MiB limit,
    # memory.events max=2617. The cap is worst-case memory per live stream, so
    # 256 KiB would add 1 GiB at 4096 streams and OOM the sidecar on its own.
    cap = int(_extract(r"MAX_LINE_CHARS = int\(os\.getenv\('MAX_LINE_CHARS', (\d+)\)\)",
                       COLLECTOR_SRC).group(1))
    assert cap <= 65536, f"{cap} bytes x 4096 streams = {cap * 4096 // 2**20} MiB worst case"
    assert cap >= 8192, "below this a legitimate long line would be truncated"
    chart = open(__file__.replace(
        'test_job_monitor.py', 'parallel_catchup_helm/values.yaml')).read()
    assert int(_extract(r"maxLineChars: (\d+)", chart).group(1)) == cap


# --- polling replaces follow=true ------------------------------------------
# Measured on ssc-test at 2096 follow streams: 1444 MiB of a 2048 MiB limit,
# memory.events max=2617, 1.00 of 2 cpu, 1797 held connections. That scales
# with pod count, so 4096 exceeds both limits. Polling makes concurrency a
# tuning parameter instead.

def test_concurrency_is_independent_of_pod_count():
    # The whole point. Under follow=true the cap had to exceed parallelism or
    # pods starved silently -- 1200 against 2048 left 896 blocked forever.
    assert 'COLLECTOR_MAX_STREAMS' not in COLLECTOR_SRC
    chart = open(__file__.replace(
        'test_job_monitor.py', 'parallel_catchup_helm/templates/job_monitor.yaml')).read()
    assert 'COLLECTOR_MAX_STREAMS' not in chart
    assert 'worker.replicas' not in chart.split('MAX_CONCURRENT_POLLS')[1][:200], \
        "poll concurrency must not be derived from parallelism"


def test_polls_are_bounded_by_a_semaphore():
    fn = _extract(r"^(async def _poll_once\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert 'async with _poll_slots:' in fn, "polls are not bounded"
    # ...and the connector is sized for polls, not for one socket per pod.
    assert 'MAX_CONCURRENT_POLLS + 64' in COLLECTOR_SRC


def test_the_archive_is_not_held_open_between_polls():
    # A live gzip deflate buffer per stream is most of what put the sidecar at
    # 1444 MiB. Opening per poll means nothing is retained between them.
    fn = _extract(r"^(async def _poll_once\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert "gzip.open(base(end, attempt) + '.log.gz', 'at')" in fn
    loop = _extract(r"^(async def poll_pod\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert 'gzip.open' not in loop, "the archive is held across polls"


def test_terminal_is_read_before_the_poll_not_after():
    # A pod that exits mid-poll would otherwise have its final output dropped:
    # the poll that read it would not yet know the pod was terminal, and the
    # next check would come after finalize.
    loop = _extract(r"^(async def poll_pod\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert loop.index('was_terminal = done(pod)') < loop.index('await _poll_once('), \
        "terminal is sampled after the poll, which races a pod exiting mid-poll"


def test_a_dead_pod_is_not_polled_forever():
    # Its log is not coming back, and the task holds a poll slot for the rest of
    # the run. follow=true finalized here because it already held the bytes.
    loop = _extract(r"^(async def poll_pod\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert 'TERMINAL_POLL_ATTEMPTS' in loop
    n = int(_extract(r"TERMINAL_POLL_ATTEMPTS = int\(os\.getenv\('TERMINAL_POLL_ATTEMPTS', (\d+)\)\)",
                     COLLECTOR_SRC).group(1))
    assert n >= 2, "a single transient 500 would end the attempt"


def test_a_terminal_pod_whose_polls_keep_failing_still_finalizes():
    # Executed: the loop must exit, not spin. Was a real regression when polling
    # replaced streaming -- the suite caught it.
    assert _run_stream_pod(500, terminal=True) == [('pod-1', '999', '1')]


def test_poll_concurrency_default_is_modest():
    n = int(_extract(r"MAX_CONCURRENT_POLLS = int\(os\.getenv\('MAX_CONCURRENT_POLLS', (\d+)\)\)",
                     COLLECTOR_SRC).group(1))
    assert 16 <= n <= 256, f"{n} in-flight polls is not a sane default"


def test_a_pending_pod_is_not_polled_yet():
    # Its container has not started, so the log endpoint answers 400 and the
    # poll is wasted -- 60 of 88 failures immediately after the polling switch.
    loop = _extract(r"while True:\n(.*?)await asyncio\.sleep\(POLL_SECONDS\)",
                    COLLECTOR_SRC).group(1)
    per_pod = loop[loop.index('for pod in pods:'):]
    # From the attempt lookup to the stream open: the earlier
    # terminal[name] = phase in ('Succeeded', 'Failed') line is not this guard.
    guard = per_pod[per_pod.index('attempt = labels.get'):per_pod.index('_streaming[name]')]
    assert 'phase not in POLLABLE_PHASES' in guard, \
        "pollability is not decided by an allowlist"
    allowed = set(re.findall(r"'(\w+)'", _extract(
        r"POLLABLE_PHASES = \(([^)]+)\)", COLLECTOR_SRC).group(1)))
    # Terminal phases must stay in -- that is where a pod's final output lives.
    assert {'Running', 'Succeeded', 'Failed'} == allowed, allowed
    # Pending has no container; Unknown means the node stopped reporting.
    assert 'Pending' not in allowed and 'Unknown' not in allowed


def test_late_peaks_are_backfilled_into_a_completed_record():
    # The record is written the moment the Job flips to succeeded, usually
    # before the collector finalizes. peaks_for_range has no fallback the way
    # tx_apply does, so a one-shot read loses them: measured on ssc-test, 356 of
    # 356 completed ranges had txApply and 0 had peakAnonBytes, while 1936
    # .metrics files on the same volume held it.
    body = _extract(r"(elif not _has_peaks\(completed\[end\]\).*?)(?=\n\s+elif st\.failed:)").group(1)
    assert 'peaks_for_range(end, attempt)' in body, "no retry of the peak read"
    assert 'save_progress(progress)' in body, "a backfilled peak is never persisted"
    assert '_reap_if_complete' in body, "backfill never lets the Job go"
    assert '_attempt_finalized(end, attempt)' in body, \
        "backfill stops retrying before the collector has finished"


def test_the_reap_waits_for_the_collectors_done_marker():
    # Not inferred from peaks or tx_apply: tx_apply falls back to the archive so
    # it lands long before the collector finishes, and an attempt can finalize
    # with no peaks at all. Only the collector knows it is done.
    import tempfile, os as _os
    d = tempfile.mkdtemp()
    ns = {'os': _os, 'LOG_DIR': d, 'PEAK_FIELDS': ('peakAnonBytes',), 'reaped': []}
    ns['delete_job'] = lambda e, a: ns['reaped'].append((e, a))
    for name in ('done_path', '_attempt_finalized', '_has_peaks', '_reap_if_complete'):
        exec(_extract(r"^(def " + name + r"\(.*?)(?=\ndef )").group(1), ns)
    full = {'txApply': 5.0, 'peakAnonBytes': 99}
    ns['_reap_if_complete'](1, 1, full)
    assert ns['reaped'] == [], "reaped before the collector marked it done"
    open(_os.path.join(d, 'range-1-a1.done'), 'w').close()
    ns['_reap_if_complete'](1, 1, full)
    assert ns['reaped'] == [(1, 1)], ns['reaped']


def test_the_done_marker_is_written_after_everything_else():
    # It licenses the monitor to reap the pod, which is the only place peaks can
    # still be read from. Written before .metrics it would authorise exactly the
    # reap it exists to prevent.
    fn = _extract(r"^(async def finalize\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert '_mark_done(end, attempt)' in fn
    assert fn.index('write_metrics(end, attempt, measured)') < fn.index('_mark_done('), \
        "the done marker precedes the metrics it certifies"
    assert fn.rstrip().endswith('_mark_done(end, attempt)'), \
        "the done marker is not the last thing finalize does"


def test_the_done_marker_is_written_atomically_and_is_best_effort():
    fn = _extract(r"^(def _mark_done\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert '.tmp' in fn and 'os.replace(' in fn, "a half-written marker would be truthy"
    assert 'except OSError' in fn, "a failed marker must not kill the stream"
    import tempfile, os as _os
    d = tempfile.mkdtemp()
    ns = {'os': _os,
          'base': lambda e, a: _os.path.join(d, f"range-{e}-a{a}"),
          'logger': type('L', (), {'warning': lambda s, *a: None})()}
    exec(_extract(r"^(def done_path\(.*?)(?=\ndef )", COLLECTOR_SRC).group(1), ns)
    exec(fn, ns)
    ns['_mark_done'](77, 2)
    assert _os.path.exists(_os.path.join(d, 'range-77-a2.done'))
    assert not _os.path.exists(_os.path.join(d, 'range-77-a2.done.tmp'))


def test_both_sides_agree_on_the_marker_path():
    # Two processes, one volume, one filename. A mismatch would mean the monitor
    # never reaps and every Job waits out its TTL.
    c = _extract(r"^(def done_path\(.*?)(?=\ndef )", COLLECTOR_SRC).group(1)
    m = _extract(r"^(def done_path\(.*?)(?=\n\ndef )").group(1)
    assert ".done" in c and ".done" in m
    assert 'range-' in m and 'base(end, attempt)' in c


def test_a_terminal_pod_wakes_its_poller_immediately():
    # The delay that matters is between the container exiting and the last read.
    # Sleeping blind for LOG_POLL_SECONDS hands that window to a spot reclaim,
    # which deletes the pod and takes the final lines with it.
    loop = _extract(r"while True:\n(.*?)await asyncio\.sleep\(POLL_SECONDS\)",
                    COLLECTOR_SRC).group(1)
    # Structure, not just presence: mutating the guard to `if False:` leaves
    # the .set() line in place and sails past a substring check.
    assert re.search(r"terminal\[name\] = phase in [^\n]*\n\s*if terminal\[name\][^\n]*:\s*\n(?:\s*#[^\n]*\n)*\s*_wake\[name\]\.set\(\)", loop), \
        "a pod going terminal does not wake its poller"
    poller = _extract(r"^(async def poll_pod\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert 'asyncio.wait_for(' in poller and '_wake.setdefault' in poller, \
        "the poller still sleeps blind between polls"
    assert 'await asyncio.sleep(backoff)' not in poller


def test_the_wake_entry_is_dropped_when_the_attempt_finishes():
    # One entry per pod, and pods are per range per attempt -- 3979 ranges with
    # retries would otherwise accumulate for the life of the run.
    fn = _extract(r"^(async def finalize\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert '_wake.pop(pod, None)' in fn


def test_a_vanished_pod_also_wakes_its_poller():
    # Gone is terminal. Without the wake its poller sleeps out the interval
    # before taking the 404, delaying finalize and the .done the monitor needs
    # before it can reap the Job.
    loop = _extract(r"while True:\n(.*?)await asyncio\.sleep\(POLL_SECONDS\)",
                    COLLECTOR_SRC).group(1)
    blk = loop[loop.index('n not in live'):loop.index('if STORAGE_MODE') if 'if STORAGE_MODE' in loop else loop.index('for pod in pods:')]
    assert 'terminal[name] = True' in blk
    assert re.search(r"if name in _wake:\s*\n(?:\s*#[^\n]*\n)*\s*_wake\[name\]\.set\(\)", blk), \
        "a vanished pod never wakes its poller"


def test_both_reap_paths_wait_for_the_same_marker():
    # Success and retry must agree. Gating one on peaks and the other on the
    # marker means an attempt with no peaks is reaped on one path and left to
    # the TTL on the other.
    assert len(re.findall(r"_attempt_finalized\(end, attempt\)", SRC)) >= 2
    assert 'if peaks_for_range(end, attempt):' not in SRC, \
        "a reap still uses peaks as a proxy for the collector being done"


def test_a_ceiling_hit_survives_a_fresh_start():
    # An OOM peak is evidence about the range whichever pass produced it: the
    # process really did allocate that much and want more. Dropping it breaks
    # the self-correcting loop -- a range that OOMs at L must record L so that
    # L * margin + headroom clears it next run. Measured on ssc-30: an OOM in
    # replay resumes and stays in the chain (224 of 252), an OOM in download
    # does not (25 of 252), and a higher-cpu run is download-bound.
    f = _peaks_ns({
        1: ({'peakAnonBytes': 8 * 1024**3}, {'outcome': 'oom'}),
        2: ({'peakAnonBytes': 900 * 1024**2}, None),      # fresh start
    })
    assert f(999, 2)['peakAnonBytes'] == 8 * 1024**3


def test_a_disk_ceiling_hit_survives_a_fresh_start_too():
    f = _peaks_ns({
        1: ({'peakEphemeralBytes': 40 * 1024**3}, {'outcome': 'ephemeral'}),
        2: ({'peakEphemeralBytes': 9 * 1024**3}, None),
    })
    assert f(999, 2)['peakEphemeralBytes'] == 40 * 1024**3


def test_the_ceiling_exception_is_peaks_only():
    # tx_apply and seconds are summed, and a fresh start redoes the work the
    # dropped attempt already did, so counting it there would double-count.
    for fn_name in ('tx_apply_for_range', 'seconds_for_range'):
        fn = _extract(r"^(def " + fn_name + r"\(.*?)(?=\ndef )").group(1)
        assert '_resumed_chain(end, attempt)' in fn, f"{fn_name} lost the chain"
        assert '_peak_attempts' not in fn, f"{fn_name} would double-count redone work"
    peaks = _extract(r"^(def peaks_for_range\(.*?)(?=\ndef )").group(1)
    assert '_peak_attempts(end, attempt)' in peaks


def test_the_worker_pod_carries_its_attempt_number():
    # The collector reads LABEL_ATTEMPT off the POD, not the Job, and defaults
    # to "1". With the label only on the Job every attempt claimed the same
    # range-<end>-a1.* files: measured on ssc-test 2026-07-30, 2246 metrics
    # files all a1 while 475 a2 pods ran, so each retry overwrote the first
    # attempt's peak instead of being maxed against it -- destroying exactly
    # the OOM evidence the chain exists to keep.
    fn = _extract(r"^(def pod_labels\(.*?)(?=\ndef )").group(1)
    # The dict itself, not the docstring -- which names LABEL_ATTEMPT while
    # explaining why it must be there, and made an earlier version of this
    # assertion pass against a pod_labels that had dropped it.
    body = fn[fn.index('labels = {'):]
    assert re.search(r"LABEL_ATTEMPT: str\(attempt\)", body), \
        "the pod template omits the attempt label"
    assert re.match(r"def pod_labels\(end, attempt\)", fn), \
        "pod_labels does not take the attempt"
    assert 'metadata=client.V1ObjectMeta(labels=pod_labels(end, attempt))' in SRC
    # and the collector's default is what makes the omission silent
    assert re.search(r"labels\.get\(LABEL_ATTEMPT, '1'\)", COLLECTOR_SRC), \
        "collector no longer defaults the attempt -- update this test"


def test_pod_and_job_agree_on_the_attempt_label_key():
    # Two readers, one key. A mismatch reproduces the same silent collision.
    assert _extract(r"LABEL_ATTEMPT = '([^']+)'").group(1) == \
           _extract(r"LABEL_ATTEMPT = '([^']+)'", COLLECTOR_SRC).group(1)


def test_no_worker_gets_a_cpu_limit_unless_one_is_configured():
    # _profile_overrides returns {} for BOTH "no profile entry" and "escalated
    # attempt". Treating them the same handed an OOM retry more memory while
    # capping it at LIM_CPU, when the attempt that just failed ran unlimited.
    # Measured on ssc-test 2026-07-30: 256 of 679 a2 pods were capped at cpu 2.
    # Less cpu means less download concurrency means a lower peak, so the retry
    # succeeds at a figure the next run cannot reproduce unthrottled.
    ns = _resources_ns(PROFILE_RANGES)
    first = ns['_resources'](end=2000)
    retry = ns['_resources'](mem='9000Mi', end=2000)          # escalated
    assert 'cpu' not in first.limits, first.limits
    assert 'cpu' not in retry.limits, f"escalated retry was throttled: {retry.limits}"
    assert retry.requests['memory'] == retry.limits['memory'] == '9000Mi'
    # ...and an unmeasured range is not throttled either. A limit only stops a
    # pod using cores that are otherwise idle, and it changes what the range
    # measures. Packing is driven by the request.
    plain = ns['_resources'](end=999999999)
    assert 'cpu' not in plain.limits, f"unprofiled range was throttled: {plain.limits}"
    assert plain.requests.get('cpu') is not None, "the cpu request must remain"


def test_escalation_counts_ooms_not_attempts():
    # On spot most retries are evictions: 288 disruption retries against 7 OOM
    # retries on ssc-test 2026-07-30. Keying the exponent on the attempt index
    # meant a range disrupted three times then OOMing once jumped to
    # base * 1.5^4 -- a 5x request for one OOM, inflated fleet-wide.
    import tempfile, json as _json, os as _os
    d = tempfile.mkdtemp()
    ns = {'os': _os, 'json': _json,
          'outcome_path': lambda e, n: _os.path.join(d, f"o-{n}")}
    for name in ('read_outcome', '_oom_count'):
        exec(_extract(r"^(def " + name + r"\(.*?)(?=\ndef )").group(1), ns)
    for n, outcome in ((1, 'disrupted'), (2, 'disrupted'), (3, 'disrupted'), (4, 'oom')):
        _json.dump({'outcome': outcome}, open(ns['outcome_path'](9, n), 'w'))
    assert ns['_oom_count'](9, 4) == 1, "three evictions were counted as escalations"
    for n in (5, 6):
        _json.dump({'outcome': 'oom'}, open(ns['outcome_path'](9, n), 'w'))
    assert ns['_oom_count'](9, 6) == 3
    body = _extract(r"(base = \(_profile_overrides\(end, escalated=False\).*?retry_mem = [^\n]+)").group(1)
    assert '_oom_count(end, attempt) + 1' in body, "escalation still keys on the attempt index"


# --- an already-finished range must not be retried -------------------------
# Measured on ssc-test 2026-07-30: a1 replayed range 16752063 to its target
# ledger and was evicted before it could exit 0. a2 resumed, found LCL ==
# TARGET, ran catchup against a DB with nothing left to apply, and stellar-core
# exited 2 -- deterministically, every attempt. The range exhausted its budget
# and the mission aborted a 61%-complete 2096-worker run over work that had
# actually been done.

def _run_resume_script(lcl, target=16752063, count=16320, mark_matches=True):
    """Execute RESUME_SCRIPT's decision logic with a stubbed core."""
    import subprocess, tempfile, os as _os, re as _re
    src = _extract(r"RESUME_SCRIPT = r'''(.*?)'''").group(1)
    src = src % {'key': f"{target}/{count}", 'target': target, 'count': count}
    d = tempfile.mkdtemp()
    bindir = _os.path.join(d, 'bin'); _os.makedirs(bindir)
    # stub stellar-core: report `lcl` to offline-info, log what else is invoked
    stub = _os.path.join(bindir, 'stellar-core')
    with open(stub, 'w') as fh:
        fh.write('#!/bin/sh\n'
                 'for a in "$@"; do case "$a" in\n'
                 '  offline-info) ' +
                 (f'echo \'{{"info":{{"ledger":{{"num":{lcl},"hash":"x"}}}}}}\'; ' if lcl else 'echo "{}"; ') +
                 'exit 0;;\n'
                 '  new-db)  echo "RAN:new-db"  >> "$STUBLOG"; exit 0;;\n'
                 '  catchup) echo "RAN:catchup" >> "$STUBLOG"; exit 2;;\n'
                 'esac; done\nexit 0\n')
    _os.chmod(stub, 0o755)
    src = src.replace('/usr/bin/stellar-core', stub)
    _os.makedirs(_os.path.join(d, 'data'), exist_ok=True)
    src = src.replace('/data/', _os.path.join(d, 'data') + '/')
    src = src.replace('MARK=' + _os.path.join(d, 'data') + '/.job-key',
                      'MARK=' + _os.path.join(d, 'data') + '/.job-key')
    mark = _os.path.join(d, 'data', '.job-key')
    if mark_matches:
        open(mark, 'w').write(f"{target}/{count}")
    stublog = _os.path.join(d, 'stub.log')
    env = dict(_os.environ, STUBLOG=stublog)
    r = subprocess.run(['/bin/sh', '-c', src], capture_output=True, text=True, env=env, timeout=30)
    ran = open(stublog).read().split() if _os.path.exists(stublog) else []
    return r.returncode, r.stdout, ran


def test_a_range_already_at_its_target_exits_success_without_recatching():
    code, out, ran = _run_resume_script(lcl=16752063)
    assert 'ALREADY COMPLETE' in out, out
    assert code == 0, f"exit {code}; a finished range must not fail"
    assert 'RAN:catchup' not in ran, "re-ran catchup on a completed range -> exit 2"
    assert 'RAN:new-db' not in ran, "wiped a completed range"


def test_a_partially_replayed_range_still_resumes():
    code, out, ran = _run_resume_script(lcl=16752063 - 100)
    assert 'RESUME:' in out and 'ALREADY COMPLETE' not in out, out
    assert 'RAN:catchup' in ran and 'RAN:new-db' not in ran, ran


def test_a_range_that_never_started_replay_starts_fresh():
    code, out, ran = _run_resume_script(lcl=None)
    assert 'RESUME DECLINED' in out, out
    assert 'RAN:new-db' in ran and 'RAN:catchup' in ran, ran


def test_the_resume_script_survives_its_own_percent_formatting():
    # RESUME_SCRIPT is %-formatted with the range's key/target/count at dispatch.
    # A bare % anywhere in it -- including in a comment -- raises at runtime and
    # takes down every job dispatch. Nearly shipped exactly that: a comment
    # reading "61%-complete".
    src = _extract(r"RESUME_SCRIPT = r'''(.*?)'''").group(1)
    src % {'key': '123/456', 'target': 123, 'count': 456}      # must not raise
    # %% is a legitimate escape (printf '%%s'), so strip those pairs before
    # looking for a stray one.
    probe = src.replace('%%', '')
    stray = [m.start() for m in re.finditer(r"%(?!\()", probe)]
    assert not stray, f"bare % in RESUME_SCRIPT near {probe[max(0,stray[0]-40):stray[0]+20]!r}"


def test_the_lcl_probe_does_not_window_its_grep():
    # offline-info puts ~40 lines of bucketlist hashes between "ledger": and
    # "num", so `grep -A8 '"ledger":'` yields nothing and the probe degrades to
    # the log fallback silently -- shipped exactly that once. Verified against
    # 27.1.1 on ssc-test 2026-07-30: exactly one "num" key in the document, and
    # it is the ledger's (genesis reads 1).
    src = _extract(r"RESUME_SCRIPT = r'''(.*?)'''").group(1)
    probe = src[src.index('offline-info'):src.index('if [ -n "$LCL" ]')]
    assert not re.search(r"grep\s+-A\d+", probe), \
        "a line-windowed grep cannot reach \"num\" past the bucketlist"
    assert '"num"' in probe, "the probe no longer reads the ledger num"
    assert 'head -1' in probe, "unbounded match could pick up a later key"


def test_a_pod_already_finished_reports_no_duration():
    # `started` measures how long the COLLECTOR has watched, not how long the
    # container ran. A pod that was already terminal when its poller began --
    # finished while the collector was down, which happened across two restarts
    # on ssc-test 2026-07-30 -- would otherwise record ~0s next to a real peak:
    # 150 metrics files had a sub-5s duration with a >500MiB anon peak.
    poller = _extract(r"^(async def poll_pod\(.*?)(?=\n\nasync def )", COLLECTOR_SRC).group(1)
    assert 'first_pass' in poller, "nothing distinguishes the first poll"
    assert re.search(r"if first_pass and was_terminal:\s*\n(?:\s*#[^\n]*\n)*\s*started = None", poller), \
        "an already-finished pod still reports a fabricated duration"
    assert poller.index('was_terminal = done(pod)') < poller.index('first_pass = False')


def test_a_peak_on_disk_is_never_lowered_by_a_later_write():
    # Peaks are monotonic, but the merge overwrote. After a collector restart
    # the fresh poller's high-water starts at zero, so its first flush would
    # replace a higher pre-restart value with a lower one -- undersizing the
    # range next run, the one direction that costs an OOM.
    import tempfile, os as _os, json as _json
    d = tempfile.mkdtemp()
    ns = {'json': _json, 'os': _os,
          'base': lambda e, a: _os.path.join(d, f"r{e}-a{a}"),
          'PEAK_KEYS': ('peakAnonBytes', 'peakWorkingSetBytes', 'peakEphemeralBytes'),
          'logger': type('L', (), {'info': lambda s, *a: None,
                                   'warning': lambda s, *a: None})()}
    exec(_extract(r"^(def write_metrics\(.*?)(?=\ndef )", COLLECTOR_SRC).group(1), ns)
    w = ns['write_metrics']
    w(1, 1, {'peakAnonBytes': 3000, 'txApplySeconds': 12.0})
    w(1, 1, {'peakAnonBytes': 900})                    # restarted poller, lower
    got = _json.load(open(ns['base'](1, 1) + '.metrics'))
    assert got['peakAnonBytes'] == 3000, f"peak was lowered to {got['peakAnonBytes']}"
    assert got['txApplySeconds'] == 12.0, "an unrelated field was dropped"
    w(1, 1, {'peakAnonBytes': 5000})                   # a genuinely higher peak
    assert _json.load(open(ns['base'](1, 1) + '.metrics'))['peakAnonBytes'] == 5000
    # non-peak fields still take the newest value
    w(1, 1, {'txApplySeconds': 99.0})
    assert _json.load(open(ns['base'](1, 1) + '.metrics'))['txApplySeconds'] == 99.0
