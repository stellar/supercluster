# Getting started

  - Find or make a Kubernetes cluster. The easiest approach currently is probably [k3s](k3s.md) on an ubuntu system, but it should work on a variety of other kubernetes distributions.
    - If you'd prefer to run Supercluster on AWS, the easiest way is to follow our [guide to creating a cluster on EKS](eks.md).

  - Make sure you have enabled at least the `dns` and `ingress` components on the Kubernetes cluster, and that the `ingress` controller is [nginx-ingress](https://kubernetes.github.io/ingress-nginx/).

  - Download and install the **x86** version of [dotnet 8.0 or later](https://dotnet.microsoft.com/download).

  - Build with `dotnet build`. You might need to run `dotnet restore` first to install package dependencies. If you installed a version of dotnet newer that 8.x, then you may be asked to install the [dotnet 8.x runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

  - To run new parallel catchup mission `MissionHistoryPubnetParallelCatchupV2`, it requires [Helm](https://helm.sh/docs/intro/install/) to be installed on the machine.

  - Run with `dotnet run --project src/App/App.fsproj --configuration Release -- mission SimplePayment --image=<stellar-core-docker-image>`
