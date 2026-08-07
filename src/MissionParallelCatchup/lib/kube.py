"""Kubernetes API clients.

Read through the module, never copied out of it:

    import kube
    ... kube.core_v1.list_namespaced_pod(...) ...

`from kube import core_v1` binds a COPY, and the tests replace these attributes
with a fake cluster -- a copy taken at import time keeps talking to the real
apiserver, silently.
"""
import os

from kubernetes import client, config as kube_config

import config

# The env var is exactly what load_incluster_config() itself keys on, so in a pod
# this is the unconditional call it always was -- a missing token or CA still
# raises here and crash-loops the container rather than running blind. Outside a
# pod there is nothing to load and import stays pure; the tests replace the
# clients below.
IN_CLUSTER = bool(os.getenv('KUBERNETES_SERVICE_HOST'))
if IN_CLUSTER:
    kube_config.load_incluster_config()

# client-go's Python equivalent defaults are fine for a few LISTs per cycle, but
# dispatching ~1024 Jobs + PVCs at once needs headroom.
_cfg = client.Configuration.get_default_copy()
_cfg.connection_pool_maxsize = config.CONNECTION_POOL
client.Configuration.set_default(_cfg)

core_v1 = client.CoreV1Api()
batch_v1 = client.BatchV1Api()
