# Stellar Supercluster

Stellar Supercluster (SSC) is package for automated integration testing of [stellar-core](https://github.com/stellar/stellar-core). It works by running multiple containerized core nodes in self-contained simulated networks, and feeding them traffic (and/or invoking their internal load-generation testing subsystem). It is a second-generation tool, replacing the functionality of an older and now-retired package called Stellar Core commander (SCC).

## Why a new tool

SSC Has the following differences from SCC:

  - SCC used local processes and docker daemons. SSC uses Kubernetes for greater scalability, automation and co-tenancy among users. See [doc/kubernetes.md](doc/kubernetes.md) for some notes on Kubernetes.

  - SCC was written in Ruby and was fairly slow, fragile and typo-prone. SSC is written in F# for greater compile-time error checking, performance and IDE support. See [doc/fsharp.md](doc/fsharp.md) for some notes on F#.

SSC has been driving day-to-day integration testing and simulation experiments at [SDF](https://stellar.org) since late 2019, and is capable of testing much larger and much more complex scenarios than SCC was, while being easier to maintain and more robust to errors.

There is also [a presentation from early in SSC's development](doc/Stellar-Supercluster.pdf) which gives some more details.

## Getting started

See [doc/getting-started.md](doc/getting-started.md) for brief instructions on how to use it.

## Contributions and support

Contributions are welcome, and support for non-SDF users will be provided on a best-effort basis.

SSC is primarily written for SDF's own testing needs, however, and generalizing it beyond that use-case may be out of scope. For any substantial changes, please file an issue to discuss before getting too far into development.

## License

[Apache 2.0](COPYING)
