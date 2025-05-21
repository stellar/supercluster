# Running maximum transaction per second test

Supercluster provides two missions that measure the maximum number of Transactions Per Second (TPS) stellar-core can support under some set of parameters:
* `MaxTPSClassic` measures maximum TPS using exclusively classic payments
* `MaxTPSMixed` measures maximum TPS using a configurable mix of soroban invoke, soroban upload, and classic pay transactions.

Other than the type of load generated, these two missions are identical. They both spin up a configurable network of stellar-core nodes, then measure the maximum TPS of the network using a binary search over a configurable range of TPS values. For each tested TPS value, the missions generate roughly 15 minutes of load, then check whether the mission was successful. To be considered successful, a mission must close 5-second ledgers at a sustained TPS value. That is, generated transactions must end up in the ledger (cannot be dropped) and all nodes must remain in sync. If the mission determines a run was successful, it will try again with a higher target TPS value. Otherwise, it will retry with a lower target TPS value.

The missions perform the binary search to find the maximum sustainable TPS value. Upon completion, the missions emit a log line of the form, "Final tx rate: 1000 for image ...".

## Docker images with performance tests enabled

To run these missions, you'll need a stellar-core docker image with performance tests enabled. The simplest way to get one is to use an image from the [Dockerhub stellar/unsafe-stellar-core repo](https://hub.docker.com/r/stellar/unsafe-stellar-core/tags) with `perftests` in the name. Note that `unsafe` in this case means "unsafe to use in production", as these are development builds that haven't necessarily undergone the same testing procedure as release builds. Additionally, `perftests` builds contain test-only features (such as artificial load generation) that are incompatible with the production environment. **Do not run `perftest` builds in production. Running any `perftests` build in the production environment could corrupt your local node state.**

## Parameters

This section details various useful parameters that tweak the max TPS tests. Note that these settings can have a large impact on the measured TPS values, making the results only meaningfully comparable when only a single parameter is changed between runs (such as comparing two different stellar-core builds).

Additionally, parameter settings have a large impact on the run time of the missions. It is normal for these tests to take on the order of hours to complete. For example, runs using the parameter set Stellar Development Foundation (SDF) uses to check for performance changes between stellar-core releases takes around 7 hours to run.

### Shared parameters

These parameters affect both `MaxTPSClassic` and `MaxTPSMixed` missions:

* `--tx-rate`: Binary search lower bound. If a run fails to achieve at least this value it will fail with an error and you should rerun the mission with a lower value.
* `--max-tx-rate`: Binary search upper bound. If a run succeeds at this value you should rerun the mission with a higher value.
* `--pubnet-data`: Network topology to use. Defaults to a topology of tier 1 validators. See [Specifying network topologies](#specifying-network-topologies) for details on how to specify a custom topology.
* `--netdelay-image`: Helper image providing simulated network delay for latency simulation. SDF provides a public image on dockerhub at `stellar/sdf-netdelay`.
* `--run-for-max-tps`: Enables additional, potentially experimental, performance features in stellar-core.

### Additional options for mixed soroban and classic traffic

In addition to the parameters in the previous section, `MaxTPSMixed` supports the following options to adjust the distribution of various transaction types:

* `--pay-weight`: Weight of pay transactions. Defaults to 50.
* `--soroban-upload-weight`: Weight of soroban upload transactions. Defaults to 5.
* `--soroban-invoke-weight`: Weight of soroban invoke transactions. Defaults to 45.

That is, the default distribution produces 50% pay transactions, 5% soroban upload transactions, and 45% soroban invoke transactions.

#### Specifying soroban transaction distributions

In addition to allowing customization of the distribution of transaction types, `MaxTPSMixed` also allows customization of various low-level transaction details. `MaxTPSMixed` defaults to distributions for these parameters that are based on real-word data, and we recommend leaving them at their default values. However, you can set these parameters as follows.

For each parameter `x`, there are two options `--x` and `--x-distribution` that describe the shape of the distribution for `x`. Each of these options takes a space separated list of values where `x` holds the values that may be sampled and `x-distribution` holds the weights of each value.  The probability that `x[i]` is sampled is `x-distribution[i]/sum(x-distribution)` for each `i`.

The pairs of parameters and distributions are:

* `--wasm-bytes` / `--wasm-bytes-weights`: Sizes of wasm blobs used for soroban upload transactions.
* `--data-entries` / `--data-entries-weights`: Number of data entries for soroban invoke transactions.
* `--total-kilobytes` / `--total-kilobytes-weights`: Number of kilobytes of IO for soroban invoke transactions.
* `--tx-size-bytes` / `--tx-size-bytes-weights`: Sizes of soroban invoke transactions.
* `--instructions` / `--instructions-weights`: Instruction counts for soroban invoke transactions.

### More parameters

The above parameters are sufficient to run the maximum TPS missions, but Supercluster contains many more parameters to configure its behavior. To see them all, run

```bash
$ dotnet run --project src/App/App.fsproj --configuration Release -- mission --help
```

## Specifying network topologies

Supercluster supports running missions with artificially generated network topologies. Desired topology must be provided in a particular JSON format:
```
[
  {
    "publicKey": 0,
    "peers": [
      1
    ],
    "sb_homeDomain": "org1"
  },
  {
    "publicKey": 1,
    "peers": [
      0
    ],
    "sb_homeDomain": "org2"
  }
]
```
- “publicKey” is the ID of the node. It can be an actual public key string, or any unique identifier (in the example above numbers are used as IDs)
- “Peers“ is the list of other IDs that this node should connect to
- “sb_homeDomain” is the home domain of the organization this node belongs to
- To mark nodes as “Tier1”, sb_homeDomain must be any of the following: [“sdf”, “pn”, “lo”, “wx”, “cq”, “sp”, “bd”]. Note: right now the Tier1 orgs are hard-coded, since this is the only Tier1 configuration used currently, but in the future this functionality can be improved by allowing arbitrary nodes to specify whether they are Tier1.

## Example command

To run a mission with a custom topology that searches for a max TPS between 500 and 1500, your command would look roughly like:
```bash
dotnet run --project src/App/App.fsproj --configuration Release -- mission MaxTPSClassic --image=stellar/unsafe-stellar-core:<stellar-core-perftest-build> --netdelay-image=stellar/sdf-netdelay:latest --pubnet-data=generated-overlay-topology.json --tx-rate=500 --max-tx-rate=1500 --run-for-max-tps
```