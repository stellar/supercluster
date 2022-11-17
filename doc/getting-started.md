# Getting started

  - Find or make a Kubernetes cluster. The easiest approach currently is probably [k3s](k3s.md) on an ubuntu system, but it should work on a variety of other kubernetes distributions.

  - Make sure you have enabled at least the `dns` and `ingress` components on the Kubernetes cluster, and that the `ingress` controller is [nginx-ingress](https://kubernetes.github.io/ingress-nginx/).

  - Download and install [dotnet 5.0 or later](https://dotnet.microsoft.com/download).

  - Build with `dotnet build`. You might need to run `dotnet restore` first to install package dependencies.

  - Run with `dotnet run --project src/App/App.fsproj --configuration Release -- mission SimplePayment --image=<stellar-core-docker-image>`
