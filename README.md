# Stellar Supercluster

Stellar Supercluster (SSC) is an experimental package for automated management and testing of stellar-core networks. It aims to subsume and (if successful) replace the functionality of [stellar_core_commander](https://github.com/stellar/stellar_core_commander) (SCC), with the following motives / goals:

  1. Leverage [Kubernetes](https://kubernetes.io/). Since SCC was written, containerized application deployment has made significant progress standardizing abstractions for application lifecycle, naming, network management, health monitoring, configuration and logging, and made many of these facilities available via a standardized API: Kubernetes. We wish to experiment with leveraging this technology both to reduce reliance on our own home-grown code (which does relatively limited and idiosyncratic variants of these tasks) as well as make it easier for individual developers to run larger test clusters with higher levels of automation on their own, without requiring cluster-operator intervention in setting up test nodes for SCC to access via ssh and docker.

  2. Leverage static languages and APIs, specifically [F#](https://docs.microsoft.com/en-us/dotnet/fsharp/) and [C#](https://docs.microsoft.com/en-us/dotnet/csharp/). SCC is written in Ruby and is dynamically typed, and delegates to several other even more-weakly-typed / typo-agnostic components such as shell scripts and templating languages. Experience working with it (especially with the long cycle times of SCC tests) has been frustratingly fragile, and discouraged maintenance and extension to newer or bigger test scenarios. We wish to move as much of the logic as possible into a context where we have some reasonable static checks and can modularize, parametrize, refactor, extend and evolve the library with more confidence and speed. There are well-typed [.NET bindings to the Kubernetes API](https://github.com/kubernetes-client/csharp) and a [.NET port of the Stellar SDK](https://github.com/kubernetes-client/csharp), and the various .NET IDEs ([VSCode](https://code.visualstudio.com/), [Visual Studio](https://visualstudio.microsoft.com/), [JetBrains Rider](https://code.visualstudio.com/), even [Emacs](https://github.com/fsharp/emacs-fsharp-mode) and [Vim](https://github.com/fsharp/vim-fsharp)) provide instant static error feedback and code completion.


Early experimentation so far suggests that the combination of improved observability and standardized APIs for management provided by Kubernetes, and improved type-safety and static checking provided by the .NET development environment, provides gains in productivity when code-writing, refactoring and debugging.

## Caveats

  - This is still an experiment and it cannot do most of what SCC can do.

  - Kubernetes is quite complex and has a _lot_ of moving parts. We try to avoid using more of it than we have to; and we do _no_ configuration of it using YAML, only typed API calls. At present we use the following resource types:
    - Namespace
    - Volume
    - Container
    - ConfigMap
    - Pod
    - PodTemplate
    - StatefulSet
    - Service
    - Ingress

  - F# is a slightly uncommon language. It is however terse, expressive, strongly typed and well-supported by the open source [dotnet core](https://dotnet.microsoft.com/download) and [mono](https://www.mono-project.com/) toolchains on all platforms we develop on. Stellar Supercluster is also structured as an application assembly and two library sub-assemblies, one in F# and one in C#, such that we can add C# code easily in any case where that is preferable. The two libraries can see each other and interoperate directly; write in whichever you prefer.

## Getting started

  1. Find or make a Kubernetes cluster. The easiest approach currently is probably [microk8s](https://microk8s.io/) on an ubuntu system if you have one of those, but there are zillions of options, from [k3s](https://k3s.io/) on linux to [minikube](https://kubernetes.io/docs/setup/minikube/) on a mac or windows laptop to cloud-hosted versions on [Amazon](https://azure.microsoft.com/en-us/services/kubernetes-service/), [Google](https://cloud.google.com/kubernetes-engine/), [Azure](https://azure.microsoft.com/en-us/services/kubernetes-service/), [IBM](https://www.ibm.com/cloud/container-service) [Alibaba](https://www.alibabacloud.com/product/container-service), [DigitalOcean](https://www.digitalocean.com/products/kubernetes/), or [many others](https://kubernetes.io/docs/setup/pick-right-solution/#hosted-solutions). One way or another, get a kubeconfig to talk to the cluster and put it a file, say `~/.kube/your-cluster-config`.

  2. Make sure you have enabled at least the `dns` and `ingress` components on the Kubernetes cluster.

  3. Download and install [dotnet core](https://dotnet.microsoft.com/download).

  4. Build with `dotnet build`. You might need to run `dotnet restore` first to install package dependencies.

  5. Run with `dotnet run src/App/App.fsproj setup -n 5 -k ~/.kube/your-cluster-config`

  6. When it's running, try talking to the ingress port, it should map a URL route for each Pod in the new network. You may need to check with `kubectl` to find where the ingress was exposed.

~~~

$ kubectl --kubeconfig ~/.kube/your-cluster-config  describe ingress --all-namespaces
Name:             stellar-core-ingress
Namespace:        ssc-f4a1c9b0c3d0
Address:          127.0.0.1
Default backend:  default-http-backend:80 (<none>)
Rules:
  Host  Path  Backends
  ----  ----  --------
  *
        /ssc-f4a1c9b0c3d0/peer-0/(.*)   peer-0:11626 (<none>)
        /ssc-f4a1c9b0c3d0/peer-1/(.*)   peer-1:11626 (<none>)
        /ssc-f4a1c9b0c3d0/peer-2/(.*)   peer-2:11626 (<none>)
        /ssc-f4a1c9b0c3d0/peer-3/(.*)   peer-3:11626 (<none>)
        /ssc-f4a1c9b0c3d0/peer-4/(.*)   peer-4:11626 (<none>)
Annotations:
  nginx.ingress.kubernetes.io/rewrite-target:  /$1
Events:                                        <none>

$ curl -k -L http://127.0.0.1:80/ssc-f4a1c9b0c3d0/peer-0/info
{
   "info" : {
      "build" : "v11.0.0rc1-15-g236f831",
      "history_failure_rate" : "0",
      "ledger" : {
         "age" : 2,
         "baseFee" : 100,
         "baseReserve" : 100000000,
         "closeTime" : 1556088957,
         "hash" : "bef6e4f75fb1b907345dcc9404783644afe33ebf52f4b63b00da2523ea799673",
         "maxTxSetSize" : 100,
         "num" : 727,
         "version" : 0
      },
      "network" : "Private test network 'ssc-f4a1c9b0c3d0'",
      "peers" : {
         "authenticated_count" : 4,
         "pending_count" : 0
      },
      "protocol_version" : 11,
      "quorum" : {
         "727" : {
            "agree" : 5,
            "delayed" : 0,
            "disagree" : 0,
            "fail_at" : 2,
            "hash" : "c9c029",
            "missing" : 0,
            "phase" : "EXTERNALIZE",
            "validated" : true
         }
      },
      "startedOn" : "2019-04-24T05:54:44Z",
      "state" : "Synced!"
   }
}
~~~

## Reading the code


### If you are new to F#

F# is an ML-family language, similar in many ways to OCaml. If you have not used one before, they take a little getting used to, but have many very desirable capabilities. Some initial orientation:

  - If you've written a functional language before (especially one with typed), the cheatsheet (in [HTML](http://dungpa.github.io/fsharp-cheatsheet/) or [PDF](https://github.com/dungpa/fsharp-cheatsheet/raw/gh-pages/fsharp-cheatsheet.pdf)) may be adequate.

  - There's a long list of further [learning resources on fsharp.org](https://fsharp.org/learn.html), including many books and a tutorial site [fsharpforfunandprofit.com](https://fsharpforfunandprofit.com/).

  - A few brief points if you just want to try reading and see how it goes, here:

    - Everything is _immutable by default_. This is not true of the C# objects bound-to inside the .NET ecosystem or the Kubernetes objects they represent, of course; but inside F# you have to ask for mutability in order to get it, otherwise bindings (`let` variables, function arguments, fields in records, etc.) are immutable.

    - Everything has a _static type_, and most are inferred. Everything is typed, but you don't have to write most of the types. You _can_ however write them in many contexts -- by writing `binding:type` -- so put them in places where you want to cross-check a type, or leave one as documentation.

    - Relatively lightweight and fine-grained _"algebraic" type declarations_ are used much more ubiquitously in F# (and all MLs) than in other languages. The two main types are _records_ (declared with `type t = {a:ty; b:ty; ...}`, fields accessable with standard `x.a` dot-syntax) and _discriminated unions_ (declared with `type t = A(ty) | B(ty) | C(ty)`). The latter are really important and different _feeling_ than OO programming: any logic or domain of discourse that has 2 or more cases typically gives rise to a disjoint union to segregate the cases, rather than a class hierarchy; cases are then handled (separately and compiler-checked) with `match` expressions.

    - Everything is an _expression_, including conditionals, loops, declaration blocks. Everything nests and everything evaluates to some value. There's a trivial "unit" value denoted `()` that occurs in contexts that are a bit like `void` or empty argument lists in C, but slightly different. If you see a function taking or being called-with `()`, like `foo()`, the `()` symbol there is an actual value being (logically) passed, it just as a single trivial (0-byte) inhabitant. You can bind that `()` value to a variable, store it in a structure, etc.

    - Function application is by juxtaposition: `foo bar` calls the function `foo` with the argument `bar`.

    - Parentheses usually surround comma-expressions, which build tuples, and many functions take tuples as arguments, so many calls look a bit like parentheses are part of calls, like `foo(bar, baz)`. Especially method calls and calls mapped into F# from other .NET languages like C#. But that is not a mandatory aspect of calling a function: it's calling a function and passing a 2-tuple. It's kinda a silly pun, but it is confusing if you are expecting parentheses to be mandatory on function calls.

    - Functions can be bound, like values, using `let`. A `let` that takes a parameter is a function. For example, `let foo x = x + 1` bound `foo` to the function that adds 1 to its argument.

    - Other functions are attached to types and participate in .NET CLR standard receiver-based method dispatch, supporting self-polymorphism, overriding and inheritence, and so forth. These are called "methods" and are declared with `member` or `override` blocks attached to type declarations.

    - In F# the argument-tuple pun is extended to support _keyword_ arguments, supporting calls like `foo(a=b, c=d)`. These only work on method definitions, not let-bound functions.


### If you are new to Kubernetes

The Kubernetes [architecture docs directory](https://github.com/kubernetes/community/blob/master/contributors/design-proposals/architecture/) has a lot of helpful technical background. There are also a ton of books, but for basic orientation the O'Reilly [Kubernetes Up And Running](https://www.oreilly.com/library/view/kubernetes-up-and/9781491935668/) book is relatively short and readable.

A few notes here, for one-page orientation. Kubernetes is a very loosely coupled system with dozens of somewhat-orthogonal, sometimes competing or overlapping subsystems. They are broadly organized into "resources" and "controllers".

  - [Resources](https://github.com/kubernetes/community/blob/master/contributors/design-proposals/architecture/resource-management.md) are things that client API calls talk about. Every resource has two parts to it: a "spec" and a "state". The spec is what the client has _requested_ the resource be like, and the state is
what the resource is currently _actually_ like.

  - Controllers are autonomous "control loops" that watch resources and attempt to reconcile the state with the spec. When you make an API call altering (creating or writing) a resource's spec, a controller will typically wake up and do some work trying to make your spec come to pass. In doing so it may modify other resource specs, triggering other controllers, and so forth. With luck the system will converge to a satisfying new state.

We use two controllers beyond the defaults (a [DNS controller](https://kubernetes.io/docs/concepts/services-networking/dns-pod-service/) and an [ingress controller](https://kubernetes.io/docs/concepts/services-networking/ingress-controllers/)) and a moderate number of resources:

  - [Namespaces](https://kubernetes.io/docs/concepts/overview/working-with-objects/namespaces/). These are lightweight resources that just represent a point of name-qualification and logical containment for other resources. Deleting a namespace deletes everything that's _in_ that namespace.
  - [Volumes](https://kubernetes.io/docs/concepts/storage/volumes/). These can be a very wide variety of storage abstractions, but we only use two currently: [Empty Directory Volumes](https://kubernetes.io/docs/concepts/storage/volumes/#emptydir) that reside on a Pod and live only as long as the Pod, and [ConfigMap Volumes](https://kubernetes.io/docs/concepts/storage/volumes/#configmap).
  - [Containers](https://kubernetes.io/docs/concepts/containers/images/). These along with Volumes are the main components of Pods. They run a Docker / OCI container process in a sandbox along with its views of Volumes, an environment, a network, etc.
  - [ConfigMaps](https://kubernetes.io/docs/tasks/configure-pod-container/configure-pod-configmap/). These are abstract key-value sets of configuration data. The most useful thing you can do with them (once populated) is map them into a read-only Volume of a Pod, where they appear as a directory with the keys as files and the values as the contents of those files.
  - [Pods](https://kubernetes.io/docs/concepts/workloads/pods/pod-overview/). These are logical groupings of shared volumes and one or more containers that can see them (including a sequence of containers run in order during Pod initialization). A Pod is always scheduled on a single node and never moves once it's been instantiated.
  - [PodTemplates](https://kubernetes.io/docs/concepts/workloads/pods/pod-overview/#pod-templates). These are templates for StatefulSets to build Pods with sequentially-numbered names.
  - [StatefulSets](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/). These are descriptions of a set of Pods to instantiate from PodTemplates, with sequentially-numbered names and identities, and numerically-ordered scaling.
  - [Services](https://kubernetes.io/docs/concepts/services-networking/service/). These are abstract descriptions of a group of Pods collectively providing a service on a given Virtual IP address or other network abstraction. In our case we make both a single Service for a group of Pods, as well as separate per-Pod services that allow us to direct requests to specific Pods if we like.
  - [Ingresses](https://kubernetes.io/docs/concepts/services-networking/ingress/). These are externally-exposed HTTP servers that map public HTTP requests to Services through URL-path rewriting and forwarding the rewritten request to the Service.
