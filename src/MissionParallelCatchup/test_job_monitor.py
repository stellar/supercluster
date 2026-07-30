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
    fn = _extract(r"def tx_apply_for_range\(.*?^def ").group(0)
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


# --- peak working set, for sizing a later run's requests --------------------
#
# Queried from Prometheus rather than read from the worker's cgroup. Measured on
# ssc-test: cgroup memory.peak reported 1.5GB for a process holding 0.3MB of
# anon memory, because it counts page cache -- and catchup reads GBs of buckets.
# Sampling inside the worker was the other option and is worse: it means dropping
# the `exec`, which is what keeps stellar-core at PID 1 and able to see SIGTERM.

def _collector_fn(*names):
    """exec the named pure functions out of log_collector.py."""
    src = ["import json"]
    for n in names:
        m = re.search(rf"^(def {n}\(.*?)(?=^\S|\Z)", COLLECTOR_SRC, re.S | re.M)
        assert m, f"{n} not found in log_collector.py"
        src.append(m.group(1))
    ns = {}
    exec("\n".join(src), ns)
    return tuple(ns[n] for n in names)








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
    assert '{**json.load(fh), **values}' in fn, "existing fields must survive"


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
                 '_cpu_millis', '_sized_cpu', '_sized'):
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
    ns['PROFILE_CPU_MARGIN'] = 1.0
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
    assert r.requests['cpu'] == '1800m' and r.limits['cpu'] == '2', \
        "an unprofiled range must keep the configured cpu limit"
    assert r.requests['ephemeral-storage'] == '35Gi'
    assert r.limits['ephemeral-storage'] == '40Gi'
    assert r.requests != r.limits


def test_an_escalated_retry_keeps_the_mismatched_defaults():
    # The escalation already chose the size; the profile must not overwrite it.
    ns = _resources_ns(PROFILE_RANGES)
    r = ns['_resources'](mem='36000Mi', end=2000)
    assert r.limits['memory'] == '36000Mi'
    assert r.requests['cpu'] == '1800m', "cpu must fall back to the configured request"


def test_the_raised_cpu_limit_is_what_lets_the_peak_grow():
    # At a 2-core limit every range pegs 2.0, so the measured peak is a ceiling
    # and the profile can never learn real demand. Headroom above the request is
    # the whole point -- the request is still capped for packing.
    ns = _resources_ns(PROFILE_RANGES)
    measured = ns['_resources'](end=2000)
    unmeasured = ns['_resources'](end=99999)
    assert 'cpu' not in measured.limits, "measured ranges run uncapped"
    assert unmeasured.limits['cpu'] == '2', "unprofiled keeps the configured cap"
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


def test_a_success_whose_metrics_are_missing_keeps_its_job():
    # tx is read from the collector's .metrics, else the pod. Deleting the Job
    # reaps the pod, so reaping a success before the metrics land turns a
    # recoverable gap into a permanent one -- the same class of loss as the 698
    # ranges the tx_apply regex dropped.
    body = _extract(r"(release_pvc\(end\)\n.*?)(?=\s+elif st\.failed:)").group(1)
    assert re.search(r"if tx is not None:\s*\n\s*delete_job\(end, attempt\)", body), \
        "success path must gate the reap on the metric having landed"


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
    assert len(re.findall(r"await finalize\(session, pod, end, attempt, tx, done_ok\)",
                          COLLECTOR_SRC)) == 3
    assert len(re.findall(r"write_metrics\(end, attempt, measured\)", COLLECTOR_SRC)) == 1



def _run_stream_pod(status, terminal):
    """Execute stream_pod against a fake apiserver. Returns finalize calls.

    Executed rather than pattern-matched: the previous version of this test
    asserted on a `except ClientResponseError` branch that raise_for_status
    could never reach, because an earlier `if resp.status == 404` returned
    first. It passed against dead code.
    """
    import asyncio, tempfile, types, os as _os
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
            async def it():
                if False: yield b''
            return it()

    FakeResp.status = status      # class bodies cannot close over a local

    class FakeSession:
        def get(self, url, params=None, headers=None): return FakeResp()

    async def fake_finalize(session, pod, end, attempt, tx, done_ok):
        calls.append((pod, end, attempt))

    d = tempfile.mkdtemp()
    ns = {
        'asyncio': asyncio, 'gzip': __import__('gzip'), 'os': _os,
        'API': 'https://k8s', 'NAMESPACE': 'ns', 'CONTAINER': 'stellar-core',
        'LOG_DIR': d, 'STATE_FLUSH_SECONDS': 10,
        'token': lambda: 't', 'finalize': fake_finalize,
        'base': lambda e, a: _os.path.join(d, f"range-{e}-a{a}"),
        'read_state': lambda e, a: None, 'write_state': lambda e, a, ts: None,
        '_TS_RE': re.compile(r"^\d{4}"),
        'TxApplyScanner': type('T', (), {'seconds': None, 'feed': lambda s, l: None}),
        'logger': type('L', (), {'info': lambda s, *a: None,
                                 'warning': lambda s, *a: None})(),
    }
    exec(_extract(r"^(async def stream_pod\(.*?)(?=\n\nasync def )",
                  COLLECTOR_SRC).group(1), ns)
    coro = ns['stream_pod'](FakeSession(), 'pod-1', '999', '1',
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
        '_peak_flushed': {}, '_streaming': {}, 'SAVE_SUCCESS_LOGS': True,
        'write_metrics': lambda e, a, v: written.append(v),
        'discard': lambda e, a: None,
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
    exec(_extract(r"^(def _attempt_resumed\(.*?)(?=\ndef )").group(1), ns)
    exec(_extract(r"^(def peaks_for_range\(.*?)(?=\ndef _attempt_resumed)").group(1), ns)
    ns['_attempt_resumed'] = ns['_attempt_resumed']
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


def test_a_fresh_retry_supersedes_everything_before_it():
    # No RESUME line means new-db ran and this attempt did the whole range, so
    # its sample is complete. The earlier attempt measured the same work and
    # only adds noise -- and in ephemeral mode, where resume can never fire,
    # this is every retry.
    f = _peaks_ns({
        1: ({'peakAnonBytes': 8 * 1024**3}, {'outcome': 'oom'}),
        2: ({'peakAnonBytes': 900 * 1024**2}, None),      # no 'resumed'
    })
    assert f(999, 2)['peakAnonBytes'] == 900 * 1024**2


def test_the_chain_stops_at_the_last_fresh_start():
    # a1 fresh (dropped), a2 fresh and evicted mid-replay, a3 resumed from it.
    # Only a2+a3 describe the same continuous pass over the range.
    f = _peaks_ns({
        1: ({'peakAnonBytes': 9 * 1024**3}, {'outcome': 'oom'}),
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
              '_peak_flushed': {}, '_streaming': {}, 'SAVE_SUCCESS_LOGS': True,
              'write_metrics': lambda e, a, v: written.append(v),
              'discard': lambda e, a: None,
              'logger': type('L', (), {'info': lambda s, *a: None})()}
        exec(fn, ns)
        tx = type('T', (), {'seconds': None, 'resumed': resumed})()
        asyncio.run(ns['finalize'](None, 'p', '999', '1', tx, lambda p: True))
        return written[0]

    assert run(True).get('resumed') is True
    assert 'resumed' not in run(False), "a fresh attempt must not be marked resumed"
