"""Logging setup shared by the monitor and the collector sidecar.

Both processes write to the same logs volume and both had their own copy of this
bootstrap, which is how they came to disagree about the directory.
"""
import logging
import os
import sys
import tempfile
from datetime import datetime, timezone


def get_logging_level():
    name_to_level = {
        'CRITICAL': logging.CRITICAL,
        'ERROR': logging.ERROR,
        'WARNING': logging.WARNING,
        'INFO': logging.INFO,
        'DEBUG': logging.DEBUG,
    }
    result = name_to_level.get(os.getenv('LOGGING_LEVEL', 'INFO'))
    return result if result is not None else logging.INFO


def log_dir():
    """The directory the log file goes in.

    On the logs PVC, not the monitor's emptyDir: /data dies with the pod, and the
    mission tars /logs -- so an OOM-retry storm, the loudest signal this thing
    produces, was visible only in `kubectl logs` and never reached the run's
    destination directory. Falls back to /data if LOG_DIR is not mounted.
    """
    chosen = os.getenv('LOG_DIR', '/logs')
    if not os.path.isdir(chosen):
        chosen = '/data'
    # Last resort, and unreachable in a pod: /data is the monitor's own emptyDir,
    # so one of the two above always exists there. Off-cluster neither does, and
    # a FileHandler on a missing directory made this module impossible to import
    # -- which is why nothing here was ever tested against a real reconcile().
    if not os.path.isdir(chosen):
        chosen = tempfile.gettempdir()
    return chosen


def build_logger(file_prefix, name=None, to_file=True):
    """Configure root logging and return the logger to use.

    `file_prefix` names the per-process log file; `name` is the logger name, and
    defaults to the root logger. `to_file=False` is stdout only, for a process
    that should not add a writer to the shared logs volume.
    """
    handlers = [logging.StreamHandler(sys.stdout)]
    if to_file:
        stamp = datetime.now(timezone.utc).strftime('%Y-%m-%d_%H-%M-%S')
        handlers.append(logging.FileHandler(
            os.path.join(log_dir(), f"{file_prefix}_{stamp}.log")))
    logging.basicConfig(level=get_logging_level(),
                        format='%(asctime)s - %(levelname)s - %(message)s',
                        handlers=handlers)
    return logging.getLogger(name)
