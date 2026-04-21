# Running minimum block time test

Supercluster provides two missions that measure the minimum ledger target close time (a.k.a. "block time") stellar-core can sustain at a fixed transaction rate while still meeting a latency SLA:

* `MinBlockTimeClassic` measures the minimum block time using exclusively classic payments.
* `MinBlockTimeMixed` measures the minimum block time using a configurable mix of soroban invoke, soroban upload, and classic pay transactions.

Other than the type of load generated, these two missions are identical. They both spin up a configurable network of stellar-core nodes, then search for the smallest `ledgerTargetCloseTimeMilliseconds` value that the network can sustain, using a binary search over the range `[--min-block-time-ms, --max-block-time-ms]`. For each candidate close time `T`, the missions upgrade the network's SCP timing settings to `T` (with proportionally scaled ballot and nomination timeouts), run ~5 minutes of load at the fixed transaction rate, then check the `ledger.age.closed-histogram` metric on every node against the SLA. If the SLA is met, the missions try again with a smaller `T`; otherwise, they try with a larger `T`.

The missions perform the binary search to find the minimum sustainable block time. Upon completion, the missions emit a log line of the form `Minimum sustainable block time: 4500 ms (fixed TPS 1000, image ...)`.

## SLA: pass/fail criteria

A candidate close time `T` is considered a **pass** if and only if, **on every node in the network**, the stellar-core `ledger.age.closed-histogram` metric satisfies **both** of the following:

* **P50** (median) is in the range `[0.80·T, 1.20·T)` ms  *(temporary; see note below)*
* **P99** is `≤ 2·T` ms

> **FIXME (P50 tolerance):** the intended P50 band is `[0.95·T, 1.05·T)` (±5%), but stellar-core currently has performance regressions that prevent the stricter band from being achievable under load. The tolerance has been temporarily widened to ±20% so the test can exercise the rest of the pipeline; tighten it back to ±5% (or narrower) once those regressions are fixed.

If any node violates any of these bounds, `T` is considered a **fail** and the binary search raises its lower bound. The same is true if the load run itself errors (e.g., stellar-core's internal `loadgen-run-failed` counter increments, nodes fall out of sync, or peers report inconsistent ledger hashes) — in that case the mission treats the iteration as a fail and the search continues upward.

The search terminates when the upper and lower bounds are within 100 ms of each other. If no candidate in the range satisfied the SLA, the mission fails with `"No block time in [lo, hi] ms satisfied the SLA at TPS N"`.

## Docker images with performance tests enabled

To run these missions, you'll need a stellar-core docker image with performance tests enabled. The simplest way to get one is to use an image from the [Dockerhub stellar/unsafe-stellar-core repo](https://hub.docker.com/r/stellar/unsafe-stellar-core/tags) with `perftests` in the name. Note that `unsafe` in this case means "unsafe to use in production", as these are development builds that haven't necessarily undergone the same testing procedure as release builds. Additionally, `perftests` builds contain test-only features (such as artificial load generation) that are incompatible with the production environment. **Do not run `perftest` builds in production. Running any `perftests` build in the production environment could corrupt your local node state.**

## Parameters

This section details various useful parameters that tweak the min block time tests. As with the max TPS tests, these settings can have a large impact on the measured value, so results are only meaningfully comparable when only a single parameter is changed between runs (such as comparing two different stellar-core builds).

Parameter settings also have a large impact on run time. Each iteration of the binary search includes a full measurement window plus a node restart (needed to refresh pregenerated transactions between iterations), so these missions typically take on the order of 30-60 minutes to complete, depending on how many iterations the search needs.

### Shared parameters

These parameters affect both `MinBlockTimeClassic` and `MinBlockTimeMixed` missions:

* `--tx-rate`: The fixed transaction rate (TPS) used for every iteration of the search. The mission answers the question "what is the smallest block time the network can sustain at this TPS?" so choosing a TPS the network clearly cannot sustain (e.g., above the network's max TPS at default block time) will result in the mission failing with no block time satisfying the SLA.
* `--min-block-time-ms`: Binary search lower bound, in milliseconds. Defaults to `4000`.
* `--max-block-time-ms`: Binary search upper bound, in milliseconds. Defaults to `5000`, which is also the protocol's maximum allowed ledger target close time — setting this higher will cause the mission to fail at startup, since validators reject upgrades above the protocol cap. Must be strictly greater than `--min-block-time-ms`.
* `--num-pregenerated-txs`: On small networks (≤30 nodes), the mission automatically switches classic payment load to `PayPregenerated` mode, which requires pregenerating signed transactions at each node. Defaults to `2500000`; lower this if you hit pod boot / liveness-probe issues while pregeneration runs.
* `--pubnet-data`: Network topology to use. Defaults to a topology of tier 1 validators. See [Specifying network topologies](#specifying-network-topologies) for details on how to specify a custom topology.
* `--netdelay-image`: Helper image providing simulated network delay for latency simulation. SDF provides a public image on dockerhub at `stellar/sdf-netdelay`.

### Additional options for mixed soroban and classic traffic

In addition to the parameters in the previous section, `MinBlockTimeMixed` supports the following options to adjust the distribution of various transaction types:

* `--pay-weight`: Weight of pay transactions. Defaults to 50.
* `--soroban-upload-weight`: Weight of soroban upload transactions. Defaults to 5.
* `--soroban-invoke-weight`: Weight of soroban invoke transactions. Defaults to 45.

That is, the default distribution produces 50% pay transactions, 5% soroban upload transactions, and 45% soroban invoke transactions.

#### Specifying soroban transaction distributions

As with `MaxTPSMixed`, `MinBlockTimeMixed` also allows customization of various low-level transaction details, and defaults to distributions based on real-world data. See the [Specifying soroban transaction distributions](measuring-transaction-throughput.md#specifying-soroban-transaction-distributions) section of the max TPS documentation for the available parameters (`--wasm-bytes` / `--wasm-bytes-weights`, `--data-entries` / `--data-entries-weights`, `--total-kilobytes` / `--total-kilobytes-weights`, `--tx-size-bytes` / `--tx-size-bytes-weights`, `--instructions` / `--instructions-weights`).

### More parameters

The above parameters are sufficient to run the minimum block time missions, but Supercluster contains many more parameters to configure its behavior. To see them all, run

```bash
$ dotnet run --project src/App/App.fsproj --configuration Release -- mission --help
```

## How the SCP timing upgrade is applied

For each candidate close time `T`, the mission deploys a config-settings upgrade that sets:

* `ledgerTargetCloseTimeMilliseconds = T`
* `ballotTimeoutInitialMilliseconds   = max(500, T / 5)`
* `ballotTimeoutIncrementMilliseconds = max(500, T / 5)`
* `nominationTimeoutInitialMilliseconds   = max(500, T / 5)`
* `nominationTimeoutIncrementMilliseconds = max(500, T / 5)`

The SCP timeouts are scaled with `T` so that nomination and ballot rounds continue to fit inside a single ledger window even when `T` is reduced.

The mission uses the same `SetupUpgradeContract` + `DeployUpgradeEntriesAndArm` path that `MissionUpgradeSCPSettings` and `MaxTPSTest` use; after arming the upgrade, the mission waits (via `WaitForScpLedgerCloseTime`) for the target value to be reflected in `/sorobaninfo` before starting the measurement window.

## Specifying network topologies

See the [Specifying network topologies](measuring-transaction-throughput.md#specifying-network-topologies) section of the max TPS documentation. The format and semantics are identical.

## Example command

To run a mission that searches for the minimum block time at 1000 TPS, with the search lower bound at 4 seconds:

```bash
dotnet run --project src/App/App.fsproj --configuration Release -- mission MinBlockTimeClassic --image=stellar/unsafe-stellar-core:<stellar-core-perftest-build> --netdelay-image=stellar/sdf-netdelay:latest --tx-rate=1000 --min-block-time-ms=4000 --max-block-time-ms=5000
```
