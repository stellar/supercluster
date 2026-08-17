"""Resume points on the volume, read back at collector startup."""
import os
import re

import config

# A kubelet log timestamp, and the only thing that may become a resume point.
TS_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?$")


def hydrate_states():
    """Every attempt with a resume point on the volume: (end, attempt) -> ts.

    Read once at collector startup so a restart continues each attempt where it
    stopped rather than re-reading whole logs. A state file with no timestamp
    means "claimed, nothing durable yet" and hydrates as an empty resume point.
    """
    out = {}
    try:
        names = os.listdir(config.LOG_DIR)
    except OSError:
        return out
    for name in names:
        m = re.match(r'^range-(\d+)-a(\d+)\.state$', name)
        if not m:
            continue
        try:
            with open(os.path.join(config.LOG_DIR, name)) as fh:
                ts = fh.read().strip()
            out[(m.group(1), m.group(2))] = ts if TS_RE.match(ts) else ''
        except OSError:
            continue
    return out
