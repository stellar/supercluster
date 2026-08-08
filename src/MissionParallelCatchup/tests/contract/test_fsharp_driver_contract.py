"""MissionHistoryPubnetParallelCatchupV2.fs against the chart and the Python.

The F# driver is the only caller. It installs the chart with a pile of --set
overrides, polls the monitor through a ConfigMap, execs into the monitor pod to
collect logs and to read progress.json, and writes the range-profile artifact
that a LATER run's monitor reads back. Nothing in that loop is type-checked
across the language boundary: a --set key the chart does not know is accepted by
helm and does nothing, a JSON field the driver forgets to project is simply
absent, and a ConfigMap key it looks up under the wrong name reads as "the
monitor has not published yet".

Every failure in that list is silent, and several have happened.

The F# is read as text -- there is no dotnet in this suite -- but each test
drives the extracted value through the real chart or the real Python, so what is
pinned is the agreement and not the F#'s spelling of it.
"""

import json
import os
import re

import pytest

import config
import units
import profiles
import records
import sizing
import job_monitor as jm
import log_collector as lc

import _artifacts as art

FS = art.fsharp()


def fs_extract(pattern, flags=re.S):
    m = re.search(pattern, FS, flags)
    assert m, f"not found in the F# driver: {pattern}"
    return m


# --- the --set keys the driver sends -----------------------------------------

_SET_KEY = re.compile(
    r'(?:worker|monitor|range|service_account)(?:\.[A-Za-z0-9_]+|\[%d\]|\[0\])+(?==)')


def set_keys():
    """Every chart value path the driver overrides, indices stripped.

    An indexed path is truncated at the array: `worker.requireNodeLabels[0].key`
    is the chart's `worker.requireNodeLabels` list, whose element shape is
    checked by rendering it below rather than by looking it up in values.yaml.
    """
    out = {}
    for raw in set(_SET_KEY.findall(FS)):
        out.setdefault(raw.split('[')[0], set()).add(raw)
    return out


def test_the_driver_really_does_configure_the_chart():
    """Guards the extraction: a regex that stopped matching would pass silently."""
    keys = set_keys()
    assert len(keys) >= 15, f"only found {sorted(keys)}; the --set scan has gone blind"
    # Sentinels that must keep flowing through --set. The ledger range moved to
    # POST /start, so it is deliberately not one of them any more.
    assert 'worker.stellar_core_image' in keys and 'worker.replicas' in keys


def test_every_helm_command_uses_the_mission_namespace():
    """KUBECONFIG chooses a cluster, but its current namespace is unrelated.

    The Kubernetes client always uses context.namespaceProperty. Every Helm
    operation must use that same namespace explicitly or install into the
    kubeconfig default, poll sandbox through the client, and wait forever for a
    monitor that exists in another namespace.
    """
    # Split on the call itself rather than matching one array literal: the install
    # builds its argv with Array.concat so it can add a second --values for
    # on-demand, and a `[| "helm" ... |]` pattern silently stopped seeing it.
    calls = [seg for seg in re.split(r'\bRunShellCommand\b', FS)[1:]
             if re.match(r'[\s(]*(?:Array\.concat\s*\[\s*)?\[\|\s*"helm"', seg)]
    assert len(calls) == 4, (
        f"expected install, get-values and two cleanup commands; found {len(calls)}")
    blocks = calls
    for block in blocks:
        verb = re.search(r'"(install|get|upgrade|uninstall)"', block)
        assert verb, f"could not identify Helm command in {block!r}"
        # F# array elements separate with a newline or a semicolon; both appear
        assert re.search(
            r'"--namespace"\s*;?\s*context\.namespaceProperty', block), (
            f"helm {verb.group(1)} does not target the mission namespace: {block!r}")




def test_every_value_the_driver_sets_is_one_the_chart_knows():
    """`helm --set` on an unknown path is accepted and ignored.

    A rename in values.yaml, or a typo here, produces a run that installs
    cleanly and quietly uses the default for whatever the driver meant to
    override -- the wrong image, the wrong ledger range, the wrong storage mode.
    """
    values = _values_tree()
    templates = _template_text()
    unknown = [k for k in sorted(set_keys())
               if not _in_values(values, k) and f".Values.{k}" not in templates]
    assert not unknown, (
        "the driver overrides chart values that do not exist; helm accepts them "
        f"and does nothing: {unknown}")


def test_every_value_the_driver_sets_reaches_a_template():
    """Declared in values.yaml is not the same as consumed.

    A key that exists but is read by nothing renders a perfectly valid manifest
    with the setting missing.
    """
    templates = _template_text()
    inert = [k for k in sorted(set_keys()) if f".Values.{k}" not in templates]
    assert not inert, f"declared in values.yaml but read by no template: {inert}"


def _values_tree():
    import yaml
    return yaml.safe_load(art.values_yaml())


def _template_text():
    tdir = os.path.join(art.CHART, 'templates')
    parts = [art.text(os.path.join(tdir, n)) for n in sorted(os.listdir(tdir))]
    parts.append(art.text(os.path.join(art.CHART, 'files', 'stellar-core.cfg')))
    return "\n".join(parts)


def _in_values(tree, path):
    node = tree
    for part in path.split('.'):
        if not isinstance(node, dict) or part not in node:
            return False
        node = node[part]
    return True


# --- the indexed shapes only the mission sends -------------------------------

def test_the_service_account_annotations_the_driver_sends_render_as_a_map():
    """metadata.annotations must be a map; the driver sends an indexed array.

    Passing it straight through toYaml produced a list and failed the whole
    install with "cannot unmarshal array into ... map[string]string". The --set
    strings below are built from the driver's own sprintf format, so a change to
    the shape it emits is caught here rather than at install time.
    """
    fmt = fs_extract(r'let serviceAccountAnnotationsToHelmIndexed.*?sprintf\s+"([^"]+)"').group(1)
    sets = tuple(_fill(fmt, 0, 'eks.amazonaws.com/role-arn', 'arn:aws:iam::1:role/r').split(','))
    for sa in art.of_kind('ServiceAccount', sets):
        annotations = sa['metadata'].get('annotations')
        assert isinstance(annotations, dict), f"{sa['metadata']['name']}: {annotations!r}"
        assert annotations['eks.amazonaws.com/role-arn'] == 'arn:aws:iam::1:role/r'


def test_the_chart_still_renders_with_no_annotations_at_all():
    """A hand-run install passes none, and the mission passes none by default."""
    for sa in art.of_kind('ServiceAccount'):
        assert not sa['metadata'].get('annotations')


def test_the_node_selector_the_driver_sends_reaches_the_monitor():
    """The driver emits structured {key, operator, values} like every other
    supercluster mission; a hand-run helm install more naturally passes
    "key:value" strings. Both shapes have to arrive as the same env pair."""
    body = fs_extract(r'let requireNodeLabelToHelmIndexed(.*?)\nlet ').group(1)
    for fragment in ('worker.requireNodeLabels[%d].key', 'operator=In', '.values[0]='):
        assert fragment in body, f"the driver no longer emits {fragment!r}"
    structured = ('worker.requireNodeLabels[0].key=purpose',
                  'worker.requireNodeLabels[0].operator=In',
                  'worker.requireNodeLabels[0].values[0]=catchup8-spot')
    env = art.env_of(art.containers(structured)[art.MONITOR_CONTAINER])
    assert (env['NODE_LABEL_KEY'], env['NODE_LABEL_VALUE']) == ('purpose', 'catchup8-spot')

    plain = ('worker.requireNodeLabels[0]=purpose:catchup8-spot',)
    env = art.env_of(art.containers(plain)[art.MONITOR_CONTAINER])
    assert (env['NODE_LABEL_KEY'], env['NODE_LABEL_VALUE']) == ('purpose', 'catchup8-spot')


def test_the_taint_the_driver_sends_reaches_the_monitor():
    """The driver defaults the effect to NoSchedule and sends no value.

    The monitor builds a Toleration with the default Equal operator, which does
    not match "" against "true" -- so the value must stay absent on both sides.
    """
    fmt = fs_extract(r'let tolerateTaintToHelmIndexed.*?sprintf\s+"([^"]+)"').group(1)
    assert '.effect=' in fmt and '.value' not in fmt
    sets = ('worker.tolerateNodeTaints[0].key=catchup8-spot',
            'worker.tolerateNodeTaints[0].effect=NoSchedule')
    env = art.env_of(art.containers(sets)[art.MONITOR_CONTAINER])
    assert env['TOLERATE_TAINT'] == 'catchup8-spot'


def _fill(fmt, index, *values):
    """Apply an F# sprintf format with %d indices and %s values."""
    out, values = fmt.replace('\\"', '"'), list(values)
    out = out.replace('%d', str(index))
    for value in values:
        out = out.replace('%s', value, 1)
    return out


# --- the worker command line the driver builds -------------------------------

def test_the_driver_disables_the_aws_progress_meter():
    """--no-progress is load-bearing, not cosmetic.

    The AWS CLI draws its transfer meter with carriage returns and no newline,
    so a 628 MiB bucket download arrives as one multi-megabyte "line". Measured
    on ssc-test 2026-07-30 at 2096 workers: aiohttp aborts a line over 512 KiB,
    so every large download killed its own collector stream, the reconnect hit
    the same wall, and every retry pod was starved of a stream. The collector
    now reads in chunks and splits on \\r too (see test_cross_process_files),
    but the cure is not emitting the spam -- it was also the bulk of every large
    range's archive.
    """
    flags = fs_extract(r'sprintf "aws s3 cp ([^"]*)--region %s"').group(1)
    assert '--no-progress' in flags, f"aws s3 cp flags: {flags!r}"


def test_the_history_get_command_lands_in_the_config_the_worker_mounts():
    """The S3 mirror override is a per-archive `get` command in stellar-core.cfg.

    Without it the workers fall back to the public archive, which throttles at
    1024 -- silently, as a very slow run rather than an error.
    """
    template = fs_extract(r'setOptions\.Add\(sprintf "(worker\.historyGetCommandCore00%d)=').group(1)
    for index in (1, 2, 3):
        key = template.replace('%d', str(index))
        assert f".Values.{key}" in art.text(
            os.path.join(art.CHART, 'files', 'stellar-core.cfg')), \
            f"{key} is set by the driver but never reaches stellar-core.cfg"


# --- the ConfigMap the driver polls ------------------------------------------





def test_every_status_field_the_driver_reads_is_one_the_monitor_sets():
    """The driver's loop terminates on num_remain and queue_in_progress_count.

    A field it reads that the monitor never sets throws inside the polling loop,
    which the driver treats as fatal: cleanup, uninstall, mission failed -- with
    the run's work discarded.
    """
    read = set(re.findall(r'status\.(?:\[|Value<\w+>\()"(\w+)"', FS))
    assert read, "the status parse has changed shape -- update this test"
    missing = sorted(read - set(jm.status))
    assert not missing, f"the driver reads status fields the monitor never sets: {missing}"


def test_the_driver_can_find_the_pod_name_in_a_failed_range_entry(cluster):
    """jobs_failed entries are "<range key>|<pod>", split on '|' by the driver.

    It uses element 1 as a pod name to dump logs from. An entry with no
    separator makes that a silent no-op; an entry with the halves swapped makes
    it request a pod named after a ledger range.
    """
    cluster.reconcile()
    cluster.advance(300, 'condemned')
    result = cluster.reconcile()

    assert result['failed_ranges'], "no range was condemned; the fixture changed"
    entry = result['failed_ranges'][0]
    parts = entry.split('|')
    assert len(parts) == 2, f"the driver's split('|')[1] cannot work on {entry!r}"
    assert parts[1].startswith(f"{cluster.run_name}-r300-a"), (
        f"element 1 is {parts[1]!r}, which is not a pod name")
    assert '/' in parts[0], f"element 0 should be the <end>/<count> range key: {parts[0]!r}"


# --- the exec paths the driver uses at teardown ------------------------------

def test_the_driver_execs_into_a_container_that_exists():
    """A wrong container name fails the exec, and the failure is caught and
    logged as a warning -- so the run finishes with no collected logs and no
    range profile."""
    names = set(art.containers())
    for name in set(re.findall(r'containerName = "([\w-]+)"', FS)):
        assert name in names, f"the driver execs into {name!r}; the pod has {sorted(names)}"


def test_the_driver_reads_the_progress_file_where_the_monitor_writes_it():
    """`cat /logs/progress.json`, hard-coded on the driver side."""
    path = fs_extract(r'command = \[\| "cat"; "([^"]+)" \|\]').group(1)
    assert path == config.PROGRESS_FILE, (
        f"the driver cats {path}; the monitor writes {config.PROGRESS_FILE}")






def test_the_driver_finds_the_monitor_pod_by_the_labels_the_chart_sets():
    """Two releases share a namespace on a test cluster routinely.

    A selector missing the release label would exec into the other run's monitor
    -- and read its progress record.
    """
    selector = fs_extract(r'labelSelector = sprintf "([^"]+)"').group(1)
    labels = art.monitor_deployment(release='pc-abc')['spec']['template']['metadata']['labels']
    for clause in selector.split(','):
        key, _, value = clause.partition('=')
        assert key in labels, f"the driver selects on {key!r}; the pod has {sorted(labels)}"
        if '%s' not in value:
            assert labels[key] == value
        else:
            assert labels[key] == 'pc-abc'


# --- the range-profile artifact: written by F#, read by Python next run ------

def fs_profile_fields():
    body = fs_extract(r'let rangeProfileFields =(.*?)\n\n').group(1)
    return set(re.findall(r'"(\w+)"', body))


def fs_document_keys():
    return set(re.findall(r'doc\.\["(\w+)"\]\s*<-', FS))


# Recorded but deliberately not carried into the artifact: they are Prometheus
# metrics, and nothing in the next run sizes or orders from them -- wallSeconds
# alone was 349 KB of a 963 KB artifact. Listed rather than inferred so that
# dropping a field the artifact DOES need still fails the test below.
NOT_PROFILED = {'wallSeconds', 'txApply'}


def test_the_artifact_carries_every_measurement_the_record_holds(cluster):
    """Two lists that must agree, in one direction each.

    Anchored on a record the real monitor wrote, so neither side can drift by
    editing a constant. A field the driver projects but the record never holds
    lands in the artifact as null; a field the record holds and the driver drops
    is lost from the artifact -- peakAnonBytes was exactly that, carried for 0%
    of ranges while the volume copy had it for 99%. The only permitted asymmetry
    is NOT_PROFILED, enumerated above.
    """
    cluster.reconcile()
    cluster.advance(300, 'succeeded')
    cluster.finalize(300, 1, tx_apply=1.5,
                     peaks={'peakAnonBytes': 7, 'peakWorkingSetBytes': 9,
                            'peakEphemeralBytes': 11})
    cluster.reconcile()

    measured = set(cluster.completed()['300']) - {'attempts', 'count'}
    assert measured, "the monitor recorded no measurements at all"
    assert not fs_profile_fields() - measured, (
        f"the driver projects {sorted(fs_profile_fields() - measured)}, which no "
        "completion record carries")
    assert measured - fs_profile_fields() == NOT_PROFILED, (
        "the artifact drops "
        f"{sorted(measured - fs_profile_fields() - NOT_PROFILED)} "
        "without that being a deliberate choice recorded in NOT_PROFILED")


def test_every_field_the_sizing_consumer_reads_is_in_the_artifact():
    """Derived from _profile_overrides, so a new sizing input fails here first."""
    consumed = set(re.findall(r"prof\.get\('(\w+)'\)", art.module_source(sizing)))
    assert consumed, "the sizing consumer no longer reads named fields"
    missing = sorted(consumed - fs_profile_fields())
    assert not missing, f"the profile is sized from {missing}, which the artifact drops"


def test_every_document_key_the_monitor_reads_is_one_the_driver_writes():
    """storageMode decides whether the disk axis is usable; ranges is the data."""
    read = set(re.findall(r"doc\.get\('(\w+)'\)", art.module_source(profiles)))
    assert read, "load_profile no longer reads named document keys"
    missing = sorted(read - fs_document_keys())
    assert not missing, f"load_profile reads {missing}, which the driver never writes"


def _artifact(storage_mode='pvc', ranges=None):
    """A profile document in the exact shape the driver writes."""
    doc = {'schema': 1, 'generated': '2026-07-30T00:00:00.0000000Z',
           'release': 'parallel-catchup-abc', 'storageMode': storage_mode,
           'ledgersPerRange': 16320, 'ranges': ranges or {}}
    assert set(doc) == fs_document_keys(), (
        f"this stand-in has drifted from the driver: {set(doc) ^ fs_document_keys()}")
    return doc


def test_an_artifact_from_a_previous_run_loads_and_sizes_the_next_one(tmp_path, monkeypatch):
    """The whole point of the artifact, end to end across the language boundary.

    Values are the driver's own projection of a completed range: keyed by range
    end as a STRING (JSON object keys always are), with count alongside the
    measurements.
    """
    path = tmp_path / 'profile.json'
    path.write_text(json.dumps(_artifact(ranges={
        '16752063': {'peakAnonBytes': 2 * 1024 ** 3, 'peakWorkingSetBytes': 13 * 1024 ** 3,
                     'seconds': 1200.0, 'count': 16320}})))

    monkeypatch.setattr(config, 'PROFILE_PATH', str(path))
    monkeypatch.setattr(config, 'STORAGE_MODE', 'pvc')
    monkeypatch.setattr(config, 'PROFILE', profiles.load_profile())
    assert config.PROFILE, "the driver's artifact did not load at all"

    sized = sizing._profile_overrides(16752063, escalated=False)
    assert 'memory' in sized, "a measured range was not sized from the artifact"
    assert (units.quantity_bytes(sized['memory']) > 2 * 1024 ** 3), \
        "the request came out below the measured peak"


def test_a_cross_mode_artifact_keeps_memory_and_drops_the_disk_axis(tmp_path, monkeypatch):
    """storageMode is in the document because the axes are not interchangeable.

    cpu and memory measure the same work in either mode. Disk does not: a pvc
    run puts /data on the volume and never measures node-local usage at all, so
    an ephemeral run's figure says nothing about it.
    """
    path = tmp_path / 'profile.json'
    path.write_text(json.dumps(_artifact(storage_mode='ephemeral', ranges={
        '16752063': {'peakAnonBytes': 2 * 1024 ** 3,
                     'peakEphemeralBytes': 30 * 1024 ** 3, 'count': 16320}})))

    monkeypatch.setattr(config, 'PROFILE_PATH', str(path))
    monkeypatch.setattr(config, 'STORAGE_MODE', 'pvc')
    monkeypatch.setattr(config, 'LIM_EPHEMERAL', '40Gi')
    monkeypatch.setattr(config, 'PROFILE', profiles.load_profile())

    sized = sizing._profile_overrides(16752063, escalated=False)
    assert 'memory' in sized, "a cross-mode profile was rejected outright"
    assert 'ephemeral-storage' not in sized, "a pvc run was sized from ephemeral-mode disk"


def test_an_empty_artifact_is_never_written_and_never_fatal(tmp_path, monkeypatch):
    """An empty profile is worse than none: it looks complete.

    The usual cause is readProgressRecord falling back to the ConfigMap mirror,
    which has every profiling field stripped. Both sides guard it -- the driver
    writes nothing, and the monitor treats a profile with no usable range as no
    profile -- because either half alone leaves the next run sizing itself from
    empty data instead of from its configured requests.
    """
    assert re.search(r'if ranges\.Count = 0 then None', FS), \
        "the driver no longer suppresses an empty profile"

    path = tmp_path / 'profile.json'
    path.write_text(json.dumps(_artifact(ranges={})))
    monkeypatch.setattr(config, 'PROFILE_PATH', str(path))
    monkeypatch.setattr(config, 'STORAGE_MODE', 'pvc')
    monkeypatch.setattr(config, 'PROFILE', profiles.load_profile())
    assert config.PROFILE == [], "an empty profile loaded as if it held something"
    assert sizing._profile_overrides(16752063, escalated=False) == {}

    # ...and an artifact that never arrived at all is the same, not an error.
    monkeypatch.setattr(config, 'PROFILE_PATH', str(tmp_path / 'absent.json'))
    assert profiles.load_profile() == []


def test_every_helm_and_kubectl_call_is_namespaced():
    """A namespace the mission was told to use must reach the shell too.

    helm and kubectl default to the kubeconfig's current context, while the
    mission's own Kubernetes client honours context.namespaceProperty. Without
    an explicit --namespace those disagree, and a run targeted at one namespace
    installs into another. Measured 2026-07-30: a mission run with
    `--namespace sandbox` put a job-monitor Deployment and four Jobs into the
    production namespace beside a live 2096-worker run.
    """
    fs = art.text(art.FSHARP_PATH)
    import re
    # Every RunShellCommand array invoking helm or kubectl must carry the flag.
    calls = re.findall(r'RunShellCommand \[\|\s*"(?:helm|kubectl)".*?\|\]', fs, re.S)
    assert calls, "no helm/kubectl shell calls found -- did the driver change shape?"
    missing = [c.split('\n')[0] for c in calls if '"--namespace"' not in c]
    assert not missing, f"shell calls without --namespace: {missing}"


def test_the_driver_pulls_from_the_directory_the_collector_writes_into():
    """The puller and the collector must agree on where artifacts live.

    Replaces the two tar-shape tests: there is no archive any more, so what
    matters is that the monitor serves LOG_DIR and the driver asks for the
    manifest of it. A mismatch would fetch an empty list and report success
    on nothing collected.
    """
    fs = open(art.FSHARP_PATH).read()
    assert '"/logs"' in fs, "the driver no longer requests the manifest"
    assert '"/logs/" + name' in fs, "the driver no longer fetches artifacts by name"
    # The monitor serves them out of the volume the collector writes to.
    import http_server
    assert 'config.LOG_DIR' in art.module_source(http_server)
