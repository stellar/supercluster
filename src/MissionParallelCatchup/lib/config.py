"""What the monitor and the log-collector must agree on.

Both processes run from the same image and share one /logs volume, so these are
the names that have to mean the same thing in both: which run they belong to,
where its files are, and the vocabulary of an attempt's verdict. The monitor's
own settings live in monitor_config; the collector's in collector_config.

Read through the module, never copied out of it:

    import config
    ... config.LOG_DIR ...

`from config import LOG_DIR` binds a COPY. A test's monkeypatch rebinds the
attribute on this module, and a copy taken at import time never sees it --
silently, with the test passing against the default. A module object is a
singleton, so reading through it is what makes those visible everywhere.
"""
import os

# The run, and every object that belongs to it. The collector selects pods on
# LABEL_RUN=RUN_NAME, so a disagreement here has it watching a different run --
# or nothing at all.
NAMESPACE = os.getenv('NAMESPACE', 'default')

RUN_NAME = os.getenv('RUN_NAME', 'parallel-catchup')

LABEL_RUN = 'catchup.stellar.org/run'

LABEL_RANGE = 'catchup.stellar.org/range-end'

LABEL_ATTEMPT = 'catchup.stellar.org/attempt'

# The shared volume. The collector owns writes here -- it streams each worker's
# log and records the .outcome verdict while the pod still exists -- and the
# monitor reads them back during reconcile.
LOG_DIR = os.getenv('LOG_DIR', '/logs')

SAVE_SUCCESS_LOGS = os.getenv('SAVE_SUCCESS_LOGS', 'true').lower() == 'true'

# Worker /data. pvc keeps it across pods, so an evicted range resumes at L+1 --
# that is what makes spot viable. ephemeral puts it on the node disk: denser
# packing, no resume, and REQ_EPHEMERAL must be sized to hold the catchup DB.
# One PVC per range, not per concurrency slot: measured on ssc-test, 300 jobs
# with a PVC each cost no more wall-clock than 300 jobs reusing 40.
# Read by both: it also decides whether the collector samples ephemeral disk.
STORAGE_MODE = os.getenv('STORAGE_MODE', 'pvc')                # pvc | ephemeral

# The verdict vocabulary. The collector writes one of these into .outcome and
# the monitor charges it against a retry budget, so a name added on one side
# and not the other is an attempt nobody can classify.
ATTEMPT_OUTCOMES = ('disrupted', 'oom', 'ephemeral', 'timeout',
                    'rejected', 'unknown', 'failed', 'fetch-fault')
