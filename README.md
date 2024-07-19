# Stellar Supercluster

Stellar Supercluster (SSC) is package for automated integration testing of
[stellar-core](https://github.com/stellar/stellar-core). It works by running
multiple containerized core nodes in self-contained simulated networks, and
feeding them traffic (and/or invoking their internal load-generation testing
subsystem). It is a second-generation tool, replacing the functionality of an
older and now-retired package called Stellar Core commander (SCC).

## Why a new tool

SSC Has the following differences from SCC:

  - SCC used local processes and docker daemons. SSC uses Kubernetes for greater
    scalability, automation and co-tenancy among users. See
    [doc/kubernetes.md](doc/kubernetes.md) for some notes on Kubernetes.

  - SCC was written in Ruby and was fairly slow, fragile and typo-prone. SSC is
    written in F# for greater compile-time error checking, performance and IDE
    support. See [doc/fsharp.md](doc/fsharp.md) for some notes on F#.

SSC has been driving day-to-day integration testing and simulation experiments
at [SDF](https://stellar.org) since late 2019, and is capable of testing much
larger and much more complex scenarios than SCC was, while being easier to
maintain and more robust to errors.

## Getting started

See [doc/getting-started.md](doc/getting-started.md) for brief instructions on
how to use it.

## Contributions and support

See [doc/contributing.md](doc/contributing.md). Support for uses outside of SDF
will be provided on a best-effort basis.

## Supported Tests ("Missions")

See [doc/missions.md](doc/missions.md) for a list of supported tests.

## Measuring transaction throughput

See [doc/measuring-transaction-throughput.md](doc/measuring-transaction-throughput.md) for instructions on load testing using supercluster. 

## Theoretical max TPS

See [doc/theoretical-max-tps.md](doc/theoretical-max-tps.md) for a table of our theoretical max TPS results by release, as well as instructions on how to reproduce those numbers.

## License

[Apache 2.0](COPYING)
