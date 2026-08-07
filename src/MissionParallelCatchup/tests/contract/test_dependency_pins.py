"""The dependency pins, in the three places they are written and the one that runs.

The image installs them, and the dev path installs them again at container start
from the chart -- the same list, typed twice more. A divergence there means the
sourceConfigMap run and the built image are different programs.

The test environment counts too: the suite ran against kubernetes 36.0.3 for
months while the image pinned ~=35.0, so every contract test that builds a real
V1* model was checking the wrong major. That is invisible until a model differs.
"""

import os
import re

import _artifacts as art

DOCKERFILE = os.path.join(art.MODULE_DIR, 'Dockerfile.jobmonitor')

# name -> the specifier both artifacts must agree on.
_SPEC = re.compile(r"'([a-z0-9-]+)(~=[0-9.]+)'")


def _dockerfile_pins():
    text = open(DOCKERFILE).read()
    install = text[text.index('RUN pip install'):]
    install = install[:install.index('\nCOPY')]
    return dict(_SPEC.findall(install))


def _chart_pins():
    """Every `pip install` the chart renders, one dict per occurrence."""
    text = art.text(os.path.join(art.CHART, 'templates', 'job_monitor.yaml'))
    out = []
    for line in re.findall(r'pip install --no-cache-dir -q (.+?)&&', text, re.S):
        out.append(dict(_SPEC.findall(line)))
    return out


def test_the_chart_installs_exactly_what_the_image_pins():
    image = _dockerfile_pins()
    assert image, "no pins found in the Dockerfile -- the parser is stale"
    for n, chart in enumerate(_chart_pins()):
        assert chart == image, (
            f"chart pip install #{n + 1} differs from the image: "
            f"chart={chart} image={image}")


def test_both_containers_install_the_same_list():
    lists = _chart_pins()
    assert len(lists) == 2, f"expected one pip install per container, found {len(lists)}"
    assert lists[0] == lists[1], f"the two containers install different deps: {lists}"


def test_the_test_environment_satisfies_the_pins():
    """What the suite imports must be what the image would install.

    Not a style check: the contract tests construct real V1* models, so a major
    the image never installs makes those assertions about a client that does not
    ship.
    """
    import importlib.metadata as md
    drift = []
    for name, spec in _dockerfile_pins().items():
        try:
            installed = md.version(name)
        except md.PackageNotFoundError:
            continue                      # not needed to run the suite
        pinned = spec[2:].split('.')[0]
        if installed.split('.')[0] != pinned:
            drift.append(f"{name}: pinned {spec}, test env has {installed}")
    assert not drift, "the suite is running against a different major:\n  " + "\n  ".join(drift)


def test_the_client_ships_the_async_api_the_pin_was_raised_for():
    """36 was chosen over 35 for kubernetes.aio, which 35 does not contain."""
    import kubernetes.aio  # noqa: F401
    from kubernetes.aio import client
    assert hasattr(client, 'V1PodFailurePolicyRule'), \
        "the async client must carry the same models the sync one does"
