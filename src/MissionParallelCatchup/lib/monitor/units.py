"""Kubernetes quantity strings to bytes and back.

Pure string arithmetic -- no config, no cluster. The monitor compares profile
figures (bytes) against chart and pool values (quantity strings) constantly, and
doing it inline is how a Gi/Mi mix-up becomes a sizing bug.
"""

_UNITS = {'Ki': 1024, 'Mi': 1024**2, 'Gi': 1024**3, 'Ti': 1024**4,
          'K': 1000, 'M': 1000**2, 'G': 1000**3, 'T': 1000**4}


def gib(q):
    try:
        return quantity_bytes(q) / (1024 ** 3)
    except Exception:
        return None


def quantity_bytes(q):
    for suffix, mult in sorted(_UNITS.items(), key=lambda kv: -len(kv[0])):
        if q.endswith(suffix):
            return int(float(q[:-len(suffix)]) * mult)
    return int(float(q))


def bytes_to_quantity(n):
    return f"{max(1, n // (1024 ** 2))}Mi"
