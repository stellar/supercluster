# Gateway API and Ingress

This is a brief additional note about how Supercluster manages to
speak HTTP to stellar-core nodes in a cluster it's managing.


## Background: HTTP?

K8s somewhat annoyingly doesn't ship with any ability to get packets
into or out of a cluster, much less route or distribute incoming
HTTP requests.

Instead, it provides management APIs that allow configuring the
capability, and it expects 3rd party software (VPC management and HTTP
proxies) to be provided that k8s can operate on your behalf, via those
management APIs. So Supercluster asks k8s "hey can you open a port and
route incoming HTTP to a pod" and k8s turns around and launches a pod
with traefik or nginx or something and tries to configure it to accept
traffic it admits from the VPC boundary and route it onwards from
there.


## Old system: Ingress

Before mid 2026, Supercluster used "ingress" objects.

These configured the ingress-implementing HTTP proxy software (again
something like nginx or traefik) to route incoming HTTP requests to
pods. So an incoming request to, say, http://$ingressHost/peer-N/foo
would get routed to http://<peer-N>.cluster.internal.domain/foo by the
program. This was done by setting HTTP-proxy-program-specific
"annotations" on the ingress object, and this was fragile and often
broke: k8s gave us nearly no help configuring the proxies it was
managing. But it also, annoyingly, also _limited_ the set of places
the HTTP-proxy-programs could send things to. Rather than common
things like "addresses" or "ports", it required us to configure and use
things called `ExternalName` objects, which were more-or-less DNS
CNAME entries. Except they worked badly and caused massive DNS
amplification for some reason.

So the picture at the time looked like:

```
Client
  |
  v
[Ingress nginx]
  |   route: /peer-N/core or /peer-N/history
  v
[Service peer-N  (type=ExternalName)]
  |   DNS lookup #1: peer-N.<ns>.svc  -> CNAME
  v
[Pod DNS name from StatefulSet/headless service]
  |   DNS lookup #2: A/AAAA
  v
[Peer Pod IP]
  |
  v
[stellar-core or history container]
```

Anyway, we can now forget all that!


## New system: Gateway API

Mid 2026 we migrated to the newest-latest replacement for ingress,
which is called Gateway API. This has a bunch of new capabilities
in addition to standardizing the API for installing HTTP routing
rules. Unfortunately standardization comes at the expense of
expressivity: the newly standardized `HTTPRoute` management objects
have no support for pattern-based routing and have a limit of
16 routes per management object.

So instead of using per-target-pod `HTTPRoute`s, we now forward
traffic from the gateway to a new, manually configured pod of some
number of nginx proxy containers which, as they are configured through
non-k8s-abstracted normal nginx config language, we can configure
however we like. We configure them with pattern rules, just like we
configured nginx-as-an-ingress before.

Unfortunately this means we now have _two_ forwarding hops on our
incoming traffic path instead of one. The new picture looks like this:

```
Client
  |
  v
[Gateway traefik]
  |   (single HTTPRoute to internal proxy service)
  v
[Manually configured nginx proxies]
  |   DNS lookup: backend name (service or pod DNS)
  v
[Backend IP]
  |
  v
[stellar-core or history container]
```

This seems like it's doing more work, but in three specific ways it's
better:

  1. As mentioned above, the `HTTPRoute` interface is standardized
     so we won't need to update supercluster again if we're running
     on some other gateway implementation. All gateway implementations
     will work the same here, so that removes a degree of breakage
     and also non-portability between k8s cloud providers.

  2. The previous arrangement unfortunately caused quadratic DNS
     amplification: when we added an `ExternalName` and wired it
     into the ingress, the ingress would re-resolve all existing
     names (and actually about 6x as many as that, due to search
     path expansion). This was enough to break CoreDNS in big
     simulations.

  3. Because all HTTP traffic is now directly routed to the in-cluster
     proxy pod, it's a step towards allowing the supercluster CLI
     itself to startup _inside the same cluster_ rather than
     "calling it from outside". While no current scenarios make use
     of this capability, we're considering it as a deployment mode
     for the future.

