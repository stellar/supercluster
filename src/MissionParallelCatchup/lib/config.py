"""What both processes must agree on: the run's identity, the shared volume,
and the verdict vocabulary.

Nothing tunable lives here. The monitor's knobs are in monitor_config and the
collector's in collector_config -- a setting only one process reads does not
belong in the module the other one also imports.
"""
import os

# --- identity ---------------------------------------------------------------
NAMESPACE = os.getenv('NAMESPACE', 'default')
RUN_NAME = os.getenv('RUN_NAME', 'parallel-catchup')
LOG_DIR = os.getenv('LOG_DIR', '/logs')

LABEL_RUN = 'catchup.stellar.org/run'
LABEL_RANGE = 'catchup.stellar.org/range-end'
LABEL_ATTEMPT = 'catchup.stellar.org/attempt'

# Shared because it decides what the collector may read: in pvc mode /data
# outlives the pod, in ephemeral mode it does not.
STORAGE_MODE = os.getenv('STORAGE_MODE', 'pvc')          # pvc | ephemeral

# The collector writes one of these into .outcome; a name on one side and not
# the other is an attempt nobody can classify.
ATTEMPT_OUTCOMES = ('disrupted', 'oom', 'ephemeral', 'timeout',
                    'rejected', 'unknown', 'failed', 'fetch-fault')
