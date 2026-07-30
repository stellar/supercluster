"""Node affinity and tolerations that build_job puts on a worker pod.

The mission exposes three node-targeting flags. Two of them survived the
rewrite from a StatefulSet template to API-created Jobs; avoidNodeLabels did
not, and the gap was silent -- the driver sent the value, values.yaml declared
it, and no template read it, so a run started with --pubnet-parallel-catchup-
avoid-node-labels scheduled workers onto exactly the nodes it named.
"""

import importlib

import pytest

import job_monitor as jm


def _match_expressions(monkeypatch, **env):
    """build_job's node-affinity expressions under a given env.

    Takes the `cluster` fixture because build_job calls ensure_pvc, which is a
    real API call -- the fixture is what puts the fake cluster behind it.
    """
    for k in ('NODE_LABEL_KEY', 'NODE_LABEL_VALUE',
              'AVOID_NODE_LABEL_KEY', 'AVOID_NODE_LABEL_VALUE'):
        monkeypatch.setattr(jm, k, env.get(k, ''))
    job = jm.build_job(300, 420, 1, None)
    aff = job.spec.template.spec.affinity
    if aff is None:
        return None
    terms = aff.node_affinity.required_during_scheduling_ignored_during_execution
    return terms.node_selector_terms[0].match_expressions


def test_no_targeting_leaves_the_pod_unconstrained(cluster, monkeypatch):
    assert _match_expressions(monkeypatch) is None


def test_require_alone_pins_the_pod_to_the_label(cluster, monkeypatch):
    exprs = _match_expressions(monkeypatch,
                               NODE_LABEL_KEY='purpose', NODE_LABEL_VALUE='catchup-spot')
    assert [(e.key, e.operator, e.values) for e in exprs] == [
        ('purpose', 'In', ['catchup-spot'])]


def test_avoid_alone_keeps_the_pod_off_the_label(cluster, monkeypatch):
    exprs = _match_expressions(monkeypatch,
                               AVOID_NODE_LABEL_KEY='purpose',
                               AVOID_NODE_LABEL_VALUE='catchup-od')
    assert [(e.key, e.operator, e.values) for e in exprs] == [
        ('purpose', 'NotIn', ['catchup-od'])]


def test_avoid_without_a_value_means_the_label_must_be_absent(cluster, monkeypatch):
    # NotIn [""] would only exclude the empty value, which is not what "avoid
    # this label" means; the mission sends operator DoesNotExist for this case.
    exprs = _match_expressions(monkeypatch, AVOID_NODE_LABEL_KEY='reserved')
    assert [(e.key, e.operator) for e in exprs] == [('reserved', 'DoesNotExist')]
    assert not exprs[0].values


def test_require_and_avoid_share_one_term_so_they_are_anded(cluster, monkeypatch):
    # Expressions inside a term are ANDed; separate terms are ORed. Split across
    # two terms, a pod that failed the require would still match on the avoid.
    exprs = _match_expressions(monkeypatch,
                               NODE_LABEL_KEY='purpose', NODE_LABEL_VALUE='catchup-spot',
                               AVOID_NODE_LABEL_KEY='reserved')
    assert [(e.key, e.operator) for e in exprs] == [
        ('purpose', 'In'), ('reserved', 'DoesNotExist')]
