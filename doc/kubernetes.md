# Kubernetes notes

Stellar Supercluster uses [Kubernetes](https://kubernetes.io/) as its sole container-execution system.

The Kubernetes [architecture docs directory](https://github.com/kubernetes/community/blob/master/contributors/design-proposals/architecture/) has a lot of helpful technical background. There are also a ton of books, but for basic orientation the O'Reilly [Kubernetes Up And Running](https://www.oreilly.com/library/view/kubernetes-up-and/9781491935668/) book is relatively short and readable.

A few notes here, for one-page orientation. Kubernetes is a very loosely coupled system with dozens of somewhat-orthogonal, sometimes competing or overlapping subsystems. They are broadly organized into "resources" and "controllers".

  - [Resources](https://github.com/kubernetes/community/blob/master/contributors/design-proposals/architecture/resource-management.md) are things that client API calls talk about. Every resource has two parts to it: a "spec" and a "state". The spec is what the client has _requested_ the resource be like, and the state is
what the resource is currently _actually_ like.

  - Controllers are autonomous "control loops" that watch resources and attempt to reconcile the state with the spec. When you make an API call altering (creating or writing) a resource's spec, a controller will typically wake up and do some work trying to make your spec come to pass. In doing so it may modify other resource specs, triggering other controllers, and so forth. With luck the system will converge to a satisfying new state.

We use two controllers beyond the defaults (a [DNS controller](https://kubernetes.io/docs/concepts/services-networking/dns-pod-service/) and an [ingress controller](https://kubernetes.io/docs/concepts/services-networking/ingress-controllers/)) and a moderate number of resources:

  - [Namespaces](https://kubernetes.io/docs/concepts/overview/working-with-objects/namespaces/). These are lightweight resources that just represent a point of name-qualification and logical containment for other resources. Deleting a namespace deletes everything that's _in_ that namespace.
  - [Containers](https://kubernetes.io/docs/concepts/containers/images/). These along with Volumes are the main components of Pods. They run a Docker / OCI container process in a sandbox along with its views of Volumes, an environment, a network, etc.
  - [ConfigMaps](https://kubernetes.io/docs/tasks/configure-pod-container/configure-pod-configmap/). These are abstract key-value sets of configuration data. The most useful thing you can do with them (once populated) is map them into a read-only Volume of a Pod, where they appear as a directory with the keys as files and the values as the contents of those files.
  - [Pods](https://kubernetes.io/docs/concepts/workloads/pods/pod-overview/). These are logical groupings of shared volumes and one or more containers that can see them (including a sequence of containers run in order during Pod initialization). A Pod is always scheduled on a single node and never moves once it's been instantiated.
  - [PodTemplates](https://kubernetes.io/docs/concepts/workloads/pods/pod-overview/#pod-templates). These are templates for StatefulSets to build Pods with sequentially-numbered names.
  - [StatefulSets](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/). These are descriptions of a set of Pods to instantiate from PodTemplates, with sequentially-numbered names and identities, and numerically-ordered scaling.
  - [Services](https://kubernetes.io/docs/concepts/services-networking/service/). These are abstract descriptions of a group of Pods collectively providing a service on a given Virtual IP address or other network abstraction. In our case we make both a single Service for a group of Pods, as well as separate per-Pod services that allow us to direct requests to specific Pods if we like.
  - [Ingresses](https://kubernetes.io/docs/concepts/services-networking/ingress/). These are externally-exposed HTTP servers that map public HTTP requests to Services through URL-path rewriting and forwarding the rewritten request to the Service.
