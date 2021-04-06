# Notes on k3s

There is a "self-contained" (single-binary, single-node) kubernetes distribution called [k3s](http://k3s.io).

Supercluster can run on k3s, and this is a relatively easy way to test it or run it locally.

Follow these steps:

  - Install k3s: `curl -sfL https://get.k3s.io | sh -s - --write-kubeconfig-mode 644`

  - Raise the sysctl limits on your ARP cache. This is necessary to run larger supercluster
    missions. Put something like this in your `/etc/sysctl.conf` and then run `sysctl -p`
    (as root):
```
net.ipv4.neigh.default.gc_thresh1 = 80000
net.ipv4.neigh.default.gc_thresh2 = 90000
net.ipv4.neigh.default.gc_thresh3 = 100000

net.ipv6.neigh.default.gc_thresh1 = 80000
net.ipv6.neigh.default.gc_thresh2 = 90000
net.ipv6.neigh.default.gc_thresh3 = 100000
```

  - Set your kubeconfig to the k3s one: `export KUBECONFIG=/etc/rancher/k3s/k3s.yaml`

  - Build supercluster normally (see [getting-started.md](getting-started.md))

  - Run supercluster with these additional arguments: `--kubeconfig $KUBECONFIG --namespace default --ingress-class traefik --ingress-internal-domain local --ingress-external-host localhost --uneven-sched`

