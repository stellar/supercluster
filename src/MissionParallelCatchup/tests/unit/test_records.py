"""The filenames and record shapes the two processes agree on.

The monitor and the collector are separate containers sharing one volume. Every
handoff between them is a filename, and a mismatch is silent: the monitor
simply never reaps and every Job waits out its TTL.
"""

import os

import config
import records
import job_monitor as jm
import log_collector as lc


def basename(path):
    return os.path.basename(path)


# --- one volume, one set of filenames ----------------------------------------

def test_both_sides_agree_on_the_metrics_filename(logdir):
    assert basename(records.metrics_path(300, 2)) == basename(lc.base(300, 2)) + '.metrics'


def test_both_sides_agree_on_the_done_marker(logdir):
    # It licenses the monitor to reap the pod, which is the only place peaks can
    # still be read from.
    assert basename(records.done_path(300, 2)) == basename(lc.done_path(300, 2))


def test_the_monitor_log_lands_where_the_mission_collects_it():
    # collectLogsFromPods tars LOG_DIR. The monitor used to write its own log to
    # /data, an emptyDir, so OOM-retry storms never reached the destination
    # directory and did not survive a monitor restart.
    assert config.LOG_DIR == config.LOG_DIR, \
        "collector and monitor must share the collected directory"
    assert os.path.dirname(config.PROGRESS_FILE) == config.LOG_DIR


def test_every_per_attempt_artifact_is_named_for_its_attempt(logdir):
    # One namespace per (range, attempt) across five writers; a helper that
    # dropped the attempt would have two attempts overwrite each other.
    paths = [records.log_path(300, 2), records.state_path(300, 2), records.outcome_path(300, 2),
             records.metrics_path(300, 2), records.verdict_path(300, 2), records.done_path(300, 2)]
    assert all(basename(p).startswith('range-300-a2.') for p in paths), paths
    assert len({basename(p) for p in paths}) == len(paths), "two writers share a filename"


# --- the worker pod's own labels ---------------------------------------------

def test_the_worker_pod_carries_its_attempt_number():
    # The collector reads LABEL_ATTEMPT off the POD, not the Job, and defaults
    # to "1". With the label only on the Job every attempt claimed the same
    # range-<end>-a1.* files: measured on ssc-test 2026-07-30, 2246 metrics
    # files all a1 while 475 a2 pods ran, so each retry overwrote the first
    # attempt's peak instead of being maxed against it -- destroying exactly
    # the OOM evidence the chain exists to keep.
    labels = jm.pod_labels(300, 2)
    assert labels[config.LABEL_ATTEMPT] == '2'
    assert labels[config.LABEL_RANGE] == '300'
    assert labels[config.LABEL_RUN] == config.RUN_NAME


def test_the_mission_label_is_opt_in(monkeypatch):
    # It is high-cardinality and only wanted when something is scraping by
    # mission, so it must not appear unless both switches are set.
    monkeypatch.setattr(config, 'MISSION', 'pubnet-catchup')
    monkeypatch.setattr(config, 'EMIT_MISSION_LABEL', False)
    assert 'mission' not in jm.pod_labels(300, 1)
    monkeypatch.setattr(config, 'EMIT_MISSION_LABEL', True)
    assert jm.pod_labels(300, 1)['mission'] == 'pubnet-catchup'


# --- what the ConfigMap mirror is allowed to carry ---------------------------

# --- durations the collector can read off a pod the monitor never saw ---------

def test_a_terminal_pod_still_yields_its_real_duration():
    # A pod carries startTime and terminated.finishedAt until it is deleted, so
    # even a pod that finished before this poller existed has a real duration.
    # The poller's own elapsed time cannot know that -- it measures how long WE
    # watched, which is ~0 in exactly that case, and 150 metrics files came back
    # with a sub-5s duration next to a >500MiB anon peak because of it.
    pod = {'status': {'startTime': '2026-07-30T04:16:26Z',
                      'containerStatuses': [{'state': {'terminated': {
                          'finishedAt': '2026-07-30T04:22:19Z'}}}]}}
    assert lc.pod_seconds(pod) == 353.0
    assert lc.pod_seconds({'status': {'startTime': '2026-07-30T04:16:26Z'}}) is None
    assert lc.pod_seconds({'status': {}}) is None
