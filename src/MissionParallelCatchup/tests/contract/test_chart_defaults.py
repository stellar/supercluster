"""values.yaml against the os.getenv defaults of the code it configures.

The chart sets these env vars EXPLICITLY, so the rendered value always wins over
the Python fallback and a drift between them is silent. It has shipped twice:
the code default for the profile cache headroom was raised to 512Mi while the
chart still forced 0, and the chart quietly won -- reproducing the exact OOMs
the code change existed to fix (measured: ranges profiled at 190MiB rss got a
209MiB limit and 90 of them were OOMKilled within 90s of dispatch).

Rather than a hand-curated pair list -- which only covers the constants someone
remembered -- this compares EVERY env var the chart sets against the code
default of the constant that reads it. Deliberate divergences are listed below
with their reason, so a new one has to be argued for rather than merely added.
"""

import os
import re

import job_monitor as jm
import log_collector as lc

import _artifacts as art

# Rendered with a profile ConfigMap so the PROFILE_* block is present -- it is
# the block the chart/code split actually bit on.
SETS = ('monitor.profileConfigMap=p',)

MODULES = {
    art.MONITOR_CONTAINER: ('job_monitor', jm),
    art.COLLECTOR_CONTAINER: ('log_collector', lc),
}

# Env vars whose chart value is deliberately NOT the code default. Each one is
# either per-release, per-mission, or a run parameter the mission overrides; in
# every case the code fallback exists only so the module can be imported
# outside a cluster. Nothing here may be a tuning constant.
DELIBERATE = {
    'RUN_NAME': 'the helm release name; the code fallback only names a standalone run',
    'CORE_IMAGE': 'the image under test, supplied per mission run',
    'WORKER_SERVICE_ACCOUNT': 'derived from the release name for IRSA trust',
    'MISSION': 'the mission name, for the kube-state-metrics label',
    'PROFILE_PATH': 'the mounted path of an optional profile ConfigMap',
    'ASAN_OPTIONS': 'passed through to the worker; empty means "unset", not "default"',
    'LATEST_LEDGER_NUM': 'a demo value in the chart; the mission always sets the real tip',
    'PARALLELISM': 'worker.replicas -- the whole point of the knob is to differ per run',
    'ATTEMPT_DEADLINE_SECONDS': 'a backstop the chart turns on and the code leaves off',
    # StellarKubeSpecs.fs owns worker sizing, so the chart ships these empty on
    # purpose and the mission fills them in on every install.
    'REQ_CPU': 'left empty in the chart; StellarKubeSpecs.fs supplies it',
    'REQ_MEM': 'left empty in the chart; StellarKubeSpecs.fs supplies it',
    'LIM_CPU': 'left empty in the chart; StellarKubeSpecs.fs supplies it',
    'LIM_MEM': 'left empty in the chart; StellarKubeSpecs.fs supplies it',
}


def _same(chart_value, code_value):
    """Compare a rendered string against a typed Python default.

    Helm renders everything as a string, so `5` and `5.0` and `true` and `True`
    all have to compare equal -- the contract is about the VALUE, not about how
    YAML happened to spell it.
    """
    if isinstance(code_value, bool):
        return chart_value.lower() == str(code_value).lower()
    if isinstance(code_value, (int, float)):
        try:
            return float(chart_value) == float(code_value)
        except ValueError:
            return False
    return chart_value == ('' if code_value is None else str(code_value))


def _pairs():
    """(container, env, chart_value, constant, code_default) for each env set."""
    out = []
    for cname, container in art.containers(SETS).items():
        module_name, module = MODULES[cname]
        bindings = art.env_bindings(art.module_source(module))
        code = art.defaults(module_name)
        for env, chart_value in art.env_of(container).items():
            if chart_value is None:
                continue                      # valueFrom: the chart picks nothing
            constant = bindings.get(env)
            out.append((cname, env, chart_value, constant,
                        code.get(constant) if constant else None))
    return out


def test_every_env_the_chart_sets_is_read_by_the_container_that_gets_it():
    """A chart env var no module reads is a knob that does nothing.

    That is the same failure as a constant nothing reads: the values.yaml
    comment promises a protection, the rendered Deployment carries it, and
    turning it changes nothing at all.
    """
    orphans = [(c, e) for c, e, _, constant, _ in _pairs() if constant is None]
    assert not orphans, (
        "the chart sets env vars nothing reads: "
        + ", ".join(f"{e} on {c}" for c, e in orphans))


def test_no_pinned_default_is_a_constant_nothing_reads():
    """Every constant this file pins must be used past its own assignment.

    A contract test guarding a constant no code consults is worse than nothing:
    it certifies a protection that does not exist. MAX_LINE_CHARS was exactly
    that and was deleted rather than kept.
    """
    dead = []
    for cname, container in art.containers(SETS).items():
        module_name, module = MODULES[cname]
        source = art.module_source(module)
        bindings = art.env_bindings(source)
        for env in art.env_of(container):
            constant = bindings.get(env)
            if constant is None:
                continue
            uses = len(re.findall(rf"\b{constant}\b", source))
            if uses < 2:
                dead.append(f"{module_name}.{constant} (from {env})")
    assert not dead, f"assigned from the chart but never read: {dead}"


def test_the_chart_value_is_the_code_default():
    """Chart and code must agree wherever the chart is not deliberately different."""
    drift = []
    for cname, env, chart_value, constant, code_value in _pairs():
        if constant is None or env in DELIBERATE:
            continue
        if not _same(chart_value, code_value):
            drift.append(f"{env} on {cname}: chart {chart_value!r} != "
                         f"code {constant}={code_value!r}")
    assert not drift, (
        "the chart overrides the code default with a different value, silently:\n  "
        + "\n  ".join(drift))


def test_the_chart_enables_a_twelve_hour_attempt_backstop():
    env = art.env_of(art.containers()[art.MONITOR_CONTAINER])
    assert env['ATTEMPT_DEADLINE_SECONDS'] == '43200'


def test_each_deliberate_divergence_is_still_a_real_env_var():
    """Keeps the allowlist above honest.

    A renamed or dropped env var must not go on being excused here -- that is
    how an exemption written for one variable ends up covering its replacement.
    """
    known = {env for _, env, _, _, _ in _pairs()}
    stale = sorted(set(DELIBERATE) - known)
    assert not stale, f"DELIBERATE excuses env vars the chart no longer sets: {stale}"


def test_the_profile_block_only_renders_with_a_profile_configmap():
    """PROFILE_PATH must not be set without the volume that backs it.

    load_profile() treats a non-empty PROFILE_PATH as "there is a profile" and
    only an OSError sends it back to the configured requests. Setting the path
    with no ConfigMap mounted would make every run log an unreadable-profile
    warning for a profile nobody asked for.
    """
    without = art.env_of(art.containers()[art.MONITOR_CONTAINER])
    assert 'PROFILE_PATH' not in without
    with_cm = art.env_of(art.containers(SETS)[art.MONITOR_CONTAINER])
    assert with_cm['PROFILE_PATH']

    mounts = {m['mountPath']
              for m in art.containers(SETS)[art.MONITOR_CONTAINER]['volumeMounts']}
    assert os.path.dirname(with_cm['PROFILE_PATH']) in mounts, (
        f"PROFILE_PATH={with_cm['PROFILE_PATH']} is not on any mounted volume")


def test_the_peak_flush_ratio_is_a_threshold_and_not_a_pass_through():
    """At exactly 1.0 every sample flushes: one write per pod per poll.

    At 2048 pods that is the dominant cost of the sampler, and the ratio exists
    to avoid it -- so agreeing with the chart is not enough, it also has to be
    above 1. Nothing else pins this: the behaviour tests inject their own ratio.
    """
    ratio = art.defaults('log_collector')['PEAK_FLUSH_RATIO']
    assert ratio > 1.0, f"ratio {ratio} flushes on every sample"


def test_the_sizing_headroom_is_a_real_allowance_in_both_places():
    """margin and headroom bound each other; neither may be inert.

    A margin below 1.0 shrinks a measured peak, and a headroom of 0 was
    measured to OOM 90 small ranges within 90s of dispatch -- memory.max bounds
    anon PLUS page cache, so a purely multiplicative margin is meaningless at
    small rss (190MiB rss * 1.1 is 19MiB of slack).

    The exact figures are pinned by the test above, against the chart. This one
    says what they must remain true of, so retuning them stays possible and
    zeroing them does not.
    """
    code = art.defaults('job_monitor')
    assert code['PROFILE_MARGIN'] >= 1.0, "a margin below 1.0 sizes under the measured peak"
    headroom = jm._quantity_bytes(code['PROFILE_CACHE_HEADROOM'])
    assert headroom >= 256 * 1024 ** 2, (
        f"{code['PROFILE_CACHE_HEADROOM']} of fixed headroom is what OOMed 90 small ranges")
    assert code['PROFILE_RUNTIME_MEMORY_INSURANCE'] == '3Gi'
    # ...and the ceiling has to sit above the configured worker limit, or a
    # range needing more than that is pinned under its own measured peak.
    assert (jm._quantity_bytes(code['PROFILE_MAX_MEM'])
            > jm._quantity_bytes(code['LIM_MEM'])), (
        "the profile ceiling is at or below the worker limit, so a hungry range "
        "can never ask for what it measured")
