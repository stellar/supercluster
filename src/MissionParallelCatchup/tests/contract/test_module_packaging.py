"""The repo layout must flatten into the one directory the container runs from.

apps/ and lib/ are for reading the repo. At runtime there is a single flat /app:
the image COPYs both directories into it, and the dev path mounts a ConfigMap
built with --from-file, whose keys are basenames and cannot contain '/'. The
modules therefore import each other by bare name, and every failure guarded here
is silent in the suite and fatal in the cluster -- an import nothing ships
crash-loops the container, and two same-named files collide into one ConfigMap
key with no warning at all.
"""

import ast
import os
import re
import sys

import _artifacts as art

DOCKERFILE = os.path.join(art.MODULE_DIR, 'Dockerfile.jobmonitor')

# The two entrypoints of the image. Everything they import from this repo has to
# reach /app with them.
ENTRYPOINTS = ('job_monitor.py', 'log_collector.py')

# Modules that must be read through rather than copied out of, and the names it
# is never safe to bind: reassigned at startup or replaced by the tests.
READ_THROUGH = ('config', 'kube')

SOURCE_DIRS = (art.APPS_DIR, art.LIB_DIR)


def _py_files(directory):
    return [f for f in os.listdir(directory) if f.endswith('.py')]


def _local_modules():
    """Everything importable from the source directories, by the name used.

    Packages count: a subdirectory is exactly what the flatness check exists to
    catch, so it cannot be invisible here.
    """
    names = set()
    for d in SOURCE_DIRS:
        names |= {f[:-3] for f in _py_files(d) if not f.startswith('_')}
        names |= {e for e in os.listdir(d)
                  if os.path.isfile(os.path.join(d, e, '__init__.py'))}
    return names


def _path(name):
    for d in SOURCE_DIRS:
        candidate = os.path.join(d, name)
        if os.path.isfile(candidate):
            return candidate
    raise AssertionError(f"{name} is in neither apps/ nor lib/")


def _imports(path):
    """(module, names) for every import in `path`; names is empty for plain imports."""
    with open(path) as fh:
        tree = ast.parse(fh.read())
    out = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            out.extend((a.name, ()) for a in node.names)
        elif isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
            out.append((node.module, tuple(a.name for a in node.names)))
    return out


def _first_party(name):
    local = _local_modules()
    return {m for m, _ in _imports(_path(name)) if m in local}


def test_the_image_ships_every_module_the_entrypoints_import():
    text = open(DOCKERFILE).read()
    # A directory COPY ships everything in it; a file COPY ships just that file.
    copied = set()
    for target in re.findall(r'^COPY\s+\./(\S+)\s', text, re.M):
        if target.endswith('/'):
            copied |= set(_py_files(os.path.join(art.MODULE_DIR, target.rstrip('/'))))
        else:
            copied.add(os.path.basename(target))

    needed = set(ENTRYPOINTS)
    for entry in ENTRYPOINTS:
        needed |= {f"{m}.py" for m in _first_party(entry)}
    missing = sorted(needed - copied)
    assert not missing, f"imported but never COPYd into the image: {missing}"


def test_the_source_directories_flatten_without_a_collision():
    """Two same-named files would become one /app file and one ConfigMap key.

    Whichever COPY ran last wins in the image, and `--from-file` silently keeps
    one of the two -- so a duplicated basename is a module quietly replaced by
    another, not an error anyone sees.
    """
    seen = {}
    for d in SOURCE_DIRS:
        for f in _py_files(d):
            seen.setdefault(f, []).append(os.path.relpath(d, art.MODULE_DIR))
    clashes = {f: dirs for f, dirs in seen.items() if len(dirs) > 1}
    assert not clashes, f"same basename in more than one source directory: {clashes}"


def test_the_modules_import_each_other_by_bare_name():
    """No package-qualified import can survive the flattening.

    `from lib import config` resolves in the repo and fails in /app, where there
    is no lib/ -- and it fails at container startup, long after every test here
    has passed.
    """
    packages = {e for d in SOURCE_DIRS for e in os.listdir(d)
                if os.path.isfile(os.path.join(d, e, '__init__.py'))}
    packages |= {os.path.basename(d) for d in SOURCE_DIRS}
    offenders = []
    for d in SOURCE_DIRS:
        for f in _py_files(d):
            for module, _ in _imports(os.path.join(d, f)):
                if module.split('.')[0] in packages:
                    offenders.append(f"{f}: import {module}")
    assert not offenders, (
        "these do not resolve once apps/ and lib/ flatten into /app:\n  "
        + "\n  ".join(offenders))


def test_no_module_shadows_a_standard_library_name():
    """/app is sys.path[0], so a local name wins over the stdlib module.

    lib/profile.py was written and renamed to profiles.py for exactly this: it
    would have shadowed the stdlib profiler for every module in the process,
    including anything the kubernetes client imports. The failure is remote from
    the cause and appears only in the container.
    """
    stdlib = sys.stdlib_module_names
    clashes = sorted({f[:-3] for d in SOURCE_DIRS for f in _py_files(d)} & set(stdlib))
    assert not clashes, f"these shadow a stdlib module on a flat sys.path: {clashes}"


def test_nothing_copies_names_out_of_the_read_through_modules():
    """`from config import REQ_CPU` binds a copy, and the copy is silently stale.

    config.PROFILE is assigned at startup and the tests monkeypatch the rest;
    kube.core_v1/batch_v1 are replaced with a fake cluster. A name bound at
    import time sees none of it -- the default is used, and the test passes.
    """
    offenders = []
    for d in SOURCE_DIRS:
        for f in _py_files(d):
            for module, names in _imports(os.path.join(d, f)):
                if module in READ_THROUGH and names:
                    offenders.append(f"{f}: from {module} import {', '.join(names)}")
    assert not offenders, (
        "read these through the module (import config; config.X) instead:\n  "
        + "\n  ".join(offenders))
