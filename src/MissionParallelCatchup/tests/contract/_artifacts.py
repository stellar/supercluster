"""The artifacts a contract test compares, loaded once per session.

A contract test pins agreement across a boundary that behaviour cannot reach
from inside Python: the helm chart against the code that reads its env vars,
the RBAC Role against the API calls the code makes, the F# mission driver
against the chart and the monitor it drives, and captured output from
Kubernetes and stellar-core against the parsers that decode it.

Reading files as text is the point here. The rule that keeps it honest: assert
the INVARIANT, never one spelling of correct code. If a test can only be
satisfied by the exact call that happens to be there today, it will go red over
a correct fix -- which has already happened twice in this suite.
"""

import functools
import json
import os
import re
import shutil
import subprocess
import sys

import pytest
import yaml

HERE = os.path.dirname(os.path.abspath(__file__))
MODULE_DIR = os.path.dirname(os.path.dirname(HERE))          # src/MissionParallelCatchup
SRC_ROOT = os.path.dirname(MODULE_DIR)                       # src
CHART = os.path.join(MODULE_DIR, 'parallel_catchup_helm')
FSHARP_PATH = os.path.join(SRC_ROOT, 'FSLibrary',
                           'MissionHistoryPubnetParallelCatchupV2.fs')

# Container names in the monitor Deployment. The collector is a separate
# container with its own env block, so a variable the monitor has is not
# automatically one the collector has -- STORAGE_MODE was missing there once and
# the ephemeral sampler silently did nothing.
MONITOR_CONTAINER = 'job-monitor'
COLLECTOR_CONTAINER = 'log-collector'


@functools.lru_cache(maxsize=None)
def text(path):
    with open(path) as fh:
        return fh.read()


def module_source(module):
    """The on-disk source of an imported module."""
    return text(module.__file__)


def fsharp():
    return text(FSHARP_PATH)


def values_yaml():
    return text(os.path.join(CHART, 'values.yaml'))


def job_monitor_template():
    return text(os.path.join(CHART, 'templates', 'job_monitor.yaml'))


# --- rendering ---------------------------------------------------------------

# The mission always sends this; the chart has no usable default for it.
_BASE_SET = ('worker.stellar_core_image=x',)


@functools.lru_cache(maxsize=None)
def render(sets=(), release='t'):
    """`helm template`, as the mission installs it. `sets` must be a tuple."""
    if not shutil.which('helm'):
        pytest.skip('helm not installed')
    args = ['helm', 'template', release, CHART]
    for s in _BASE_SET + tuple(sets):
        args += ['--set', s]
    r = subprocess.run(args, capture_output=True, text=True)
    assert r.returncode == 0, f"helm template failed:\n{r.stderr}"
    return r.stdout


@functools.lru_cache(maxsize=None)
def docs(sets=(), release='t'):
    return tuple(d for d in yaml.safe_load_all(render(sets, release)) if d)


def of_kind(kind, sets=(), release='t'):
    return [d for d in docs(sets, release) if d.get('kind') == kind]


def monitor_deployment(sets=(), release='t'):
    found = of_kind('Deployment', sets, release)
    assert len(found) == 1, f"expected one Deployment, got {len(found)}"
    return found[0]


def containers(sets=(), release='t'):
    """{name: container} for the monitor Deployment's pod spec."""
    spec = monitor_deployment(sets, release)['spec']['template']['spec']
    return {c['name']: c for c in spec['containers']}


def env_of(container):
    """{NAME: value} for the env entries that carry a literal value.

    valueFrom entries (NAMESPACE, from the downward API) are reported with a
    value of None: they are set, but the chart does not choose the value.
    """
    return {e['name']: e.get('value') for e in (container.get('env') or [])}


def role_rules(sets=(), release='t'):
    found = of_kind('Role', sets, release)
    assert len(found) == 1, f"expected one Role, got {len(found)}"
    return found[0]['rules']


def granted(sets=(), release='t'):
    """{(apiGroup, resource): {verbs}} the monitor's ServiceAccount holds."""
    out = {}
    for rule in role_rules(sets, release):
        for group in rule['apiGroups']:
            for resource in rule['resources']:
                out.setdefault((group, resource), set()).update(rule['verbs'])
    return out


# --- the code's own defaults, read without ambient env -----------------------

_PROBE = """
import json, sys
sys.path.insert(0, {module_dir!r})
import {module} as m
out = {{}}
for k, v in vars(m).items():
    if k.isupper() and isinstance(v, (int, float, str, bool, type(None))):
        out[k] = v
print('<<<' + json.dumps(out) + '>>>')
"""


@functools.lru_cache(maxsize=None)
def defaults(module_name, env_pairs=()):
    """Module-level UPPERCASE constants as they are with NO env set.

    Read out of a subprocess with a scrubbed environment rather than off the
    imported module: the values a test process happens to import depend on
    whatever env the developer is running under, and the whole point here is to
    compare the chart against the built-in fallback.

    `env_pairs` is a tuple of (name, value) for the few constants that are
    derived from an env var at import -- PROGRESS_CM off RUN_NAME, say -- where
    the derivation is what a test needs to see.
    """
    src = _PROBE.format(module_dir=MODULE_DIR, module=module_name)
    env = {'PATH': os.environ.get('PATH', ''), 'HOME': os.environ.get('HOME', '')}
    env.update(dict(env_pairs))
    r = subprocess.run([sys.executable, '-c', src], capture_output=True,
                       text=True, env=env, cwd=MODULE_DIR)
    assert r.returncode == 0, f"could not import {module_name} cleanly:\n{r.stderr}"
    body = r.stdout[r.stdout.index('<<<') + 3:r.stdout.rindex('>>>')]
    return json.loads(body)


_GETENV = re.compile(r"^(\w+)\s*=\s*[^\n]*os\.getenv\(\s*'([A-Z_]+)'", re.M)


def env_bindings(source):
    """{ENV_VAR: module_constant} for every `X = ... os.getenv('ENV'...)`.

    An env var can be read into more than one name -- LOG_DIR feeds both the
    exported LOG_DIR and a module-private copy used to place the monitor's own
    log file. The exported constant is the one the rest of the module and these
    tests can see, so it wins.
    """
    out = {}
    for name, env in _GETENV.findall(source):
        if env not in out or (name.isupper() and not out[env].isupper()):
            out[env] = name
    return out


def reads_env(source):
    return set(re.findall(r"os\.getenv\(\s*'([A-Z_]+)'", source))
