"""How the collector reaches the apiserver and the kubelet.

The monitor uses the kubernetes client (see kube); the collector talks raw
aiohttp because it needs streaming log reads, so it carries its own credentials
and endpoints.
"""

import os
import ssl

SA = '/var/run/secrets/kubernetes.io/serviceaccount'
API = (f"https://{os.getenv('KUBERNETES_SERVICE_HOST', 'kubernetes.default')}"
       f":{os.getenv('KUBERNETES_SERVICE_PORT', '443')}")
# Read-only kubelet port. A seam for tests, which serve the payload on a loopback
# port rather than reaching a real node.
KUBELET_PORT = 10250


def token():
    # Projected service account tokens rotate, so this is re-read per request
    # rather than cached at startup.
    with open(os.path.join(SA, 'token')) as fh:
        return fh.read().strip()


def ssl_ctx():
    return ssl.create_default_context(cafile=os.path.join(SA, 'ca.crt'))
