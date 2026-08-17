"""The filenames both processes agree on, and the write that keeps them whole.

This is the entire cross-process contract. The collector writes these files
while a pod still exists and the monitor reads them back, so a disagreement
about a name is a measurement silently lost. Everything either side does with
the contents lives on its own side: record for the monitor, state_files for the
collector.
"""
import os

import config


def log_path(end, attempt):
    """Canonical archive name, written by the log-collector sidecar.

    Deliberately carries no ok/failed suffix: which ranges failed is recorded in
    the progress ConfigMap, and encoding it here would mean two components
    disagreeing about a filename.
    """
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.log.gz")


def state_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.state")


def outcome_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.outcome")


def metrics_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.metrics")


def done_path(end, attempt):
    return os.path.join(config.LOG_DIR, f"range-{end}-a{attempt}.done")


def write_atomic(path, body, opener=None):
    """Write `body` through tmp+rename so a reader never sees a partial file.

    Both processes write these files and both read them back: the collector
    writes while the monitor polls, so a torn .metrics or .outcome reads as
    corrupt and the measurement is lost, and a restarted monitor decides a
    range's remaining budget from .outcome and .verdict.
    """
    tmp = path + '.tmp'
    # `opener` resolves at call time, not as a default argument, so the write
    # seam stays patchable -- the tmp+rename discipline is only worth having if
    # a test can crash a write mid-flight and prove the real path is untouched.
    with (opener or open)(tmp, 'wt') as fh:
        fh.write(body)
    os.replace(tmp, path)


