# Running minimum block time test

Supercluster provides two missions that measure the minimum ledger target close time (a.k.a. "block time") stellar-core can sustain at a fixed transaction rate while still meeting a latency SLA:

* `MinBlockTimeClassic` measures the minimum block time using exclusively classic payments.
* `MinBlockTimeMixed` measures the minimum block time using one explicit `MIXED_PREGEN_*` overlay-only loadgen mode: pre-generated classic payments plus the selected synthetic Soroban transaction type.

Other than the type of load generated, these two missions are identical. They both spin up a configurable network of stellar-core nodes, then search for the smallest `ledgerTargetCloseTimeMilliseconds` value that the network can sustain, using a binary search over the range `[--min-block-time-ms, --max-block-time-ms]`. For each candidate close time `T`, the missions upgrade the network's SCP timing settings to `T` (with proportionally scaled ballot and nomination timeouts), run ~5 minutes of load at the fixed transaction rate, then check the `ledger.age.closed-histogram` metric on every node against the SLA. If the SLA is met, the missions try again with a smaller `T`; otherwise, they try with a larger `T`.

The missions perform the binary search to find the minimum sustainable block time. Upon completion, the missions emit a log line of the form `Minimum sustainable block time: 4000 ms (fixed TPS 1000, image ...)`.

## SLA: pass/fail criteria

A candidate close time `T` is considered a **pass** if and only if, **on every node in the network**, the stellar-core `ledger.age.closed-histogram` metric satisfies **both** of the following:

* **P75** is in the range `[0.80·T, 1.20·T)` ms  *(temporary; see note below)*
* **P99** is `≤ 2·T` ms

> **FIXME (P75 tolerance):** the intended P75 band is `[0.95·T, 1.05·T)` (±5%), but stellar-core currently has performance regressions that prevent the stricter band from being achievable under load. The tolerance has been temporarily widened to ±20% so the test can exercise the rest of the pipeline; tighten it back to ±5% (or narrower) once those regressions are fixed.

The node keeps this histogram over a sliding 5-minute window, so the metric is read during the load: at consecutive 5-minute windows that end with the planned load, after a warm-up of the remainder, and **every window must pass on every node**. A 300 s load is one window at its end; a 960 s load (`--overlay-v2-optimized`) is a 60 s warm-up and three windows, read at 360, 660 and 960 s. A load that runs past its planned end keeps being read every 5 minutes, and once more when it ends unless the last read is under 30 s old, so every part of the load after the warm-up is judged. A read that fails fails the candidate.

If any node violates any of these bounds, `T` is considered a **fail** and the binary search raises its lower bound. The same is true if the load run itself errors (e.g., stellar-core's internal `loadgen-run-failed` counter increments, nodes fall out of sync, or peers report inconsistent ledger hashes) — in that case the mission treats the iteration as a fail and the search continues upward.

Overlay-only candidates (`MinBlockTimeMixed`) never apply their transactions, but stellar-core's load generator still completes only once every transaction it submitted has been included in a closed ledger, so a load generator failure (such as load left out of ledgers) fails the candidate. That needs stellar-core master, or a Rust-overlay image built from 2026-07-31 on (earlier ones never count the transactions as included, so every overlay-only candidate fails). These candidates are judged while still in overlay-only mode: in addition to the close-time SLA above, the consistency and sync checks must pass and, as a cross-check, at least 95% of the offered transactions must reach ledgers (`ledger.transaction.count`). Apply is not re-enabled; the nodes are restarted, discarding what is left in their queues, before the next candidate.

Candidate close times are always whole seconds: the search runs over the whole seconds in `[--min-block-time-ms, --max-block-time-ms]`, bounds included, so the default range evaluates `4000` ms and, only if that fails, `5000` ms. Equal bounds evaluate exactly that close time once, without rounding. If no candidate in the range satisfied the SLA, the mission fails with `"No block time in [lo, hi] ms satisfied the SLA at TPS N"`.

## Docker images with performance tests enabled

To run these missions, you'll need a stellar-core docker image with performance tests enabled. The simplest way to get one is to use an image from the [Dockerhub stellar/unsafe-stellar-core repo](https://hub.docker.com/r/stellar/unsafe-stellar-core/tags) with `perftests` in the name. Note that `unsafe` in this case means "unsafe to use in production", as these are development builds that haven't necessarily undergone the same testing procedure as release builds. Additionally, `perftests` builds contain test-only features (such as artificial load generation) that are incompatible with the production environment. **Do not run `perftest` builds in production. Running any `perftests` build in the production environment could corrupt your local node state.**

## Parameters

This section details various useful parameters that tweak the min block time tests. As with the max TPS tests, these settings can have a large impact on the measured value, so results are only meaningfully comparable when only a single parameter is changed between runs (such as comparing two different stellar-core builds).

Parameter settings also have a large impact on run time. Each iteration of the binary search includes a full measurement window plus a node restart (needed to refresh pregenerated transactions between iterations), so these missions typically take on the order of 30-60 minutes to complete, depending on how many iterations the search needs.

### Shared parameters

These parameters affect both `MinBlockTimeClassic` and `MinBlockTimeMixed` missions:

* `--tx-rate`: The fixed transaction rate (TPS) used for every iteration of the search. For `MinBlockTimeMixed`, this is used only when neither `--classic-tx-rate` nor `--soroban-tx-rate` is set, in which case the mission splits it evenly between classic and Soroban streams. The mission answers the question "what is the smallest block time the network can sustain at this TPS?" so choosing a TPS the network clearly cannot sustain (e.g., above the network's max TPS at default block time) will result in the mission failing with no block time satisfying the SLA.
* `--min-block-time-ms`: Binary search lower bound, in milliseconds. Defaults to `4000`.
* `--max-block-time-ms`: Binary search upper bound, in milliseconds. Defaults to `5000`, which is also the protocol's maximum allowed ledger target close time — setting this higher will cause the mission to fail at startup, since validators reject upgrades above the protocol cap. Must not be less than `--min-block-time-ms`; when the two are equal the mission evaluates exactly that close time once, with no search.
* `--num-pregenerated-txs`: Number of pre-generated signed classic transactions to create per loadgen node. `MinBlockTimeClassic` uses these on small networks (≤30 nodes) when it automatically switches classic payment load to `PayPregenerated`; `MinBlockTimeMixed` always uses them for the classic stream in its `MIXED_PREGEN_*` mode. Defaults to `2500000`
* `--pubnet-data`: Network topology to use. Defaults to a topology of tier 1 validators. See [Specifying network topologies](#specifying-network-topologies) for details on how to specify a custom topology.
* `--tier1-org-count`: Organizations (three validators each) in that default tier 1 topology, from `10` (the default) to `40`. Beyond 10, synthetic organizations are added in a fixed order (`x01`, `x02`, ...), spread over further cloud regions in North America, Europe, Asia, South America, Oceania, Africa and the Middle East; the simulated network delay between two validators grows with their distance, so larger counts also raise the network's latency floor.
* `--netdelay-image`: Helper image providing simulated network delay for latency simulation. SDF provides a public image on dockerhub at `stellar/sdf-netdelay`.

### Tuned defaults for the Rust-overlay (v2) image

`--overlay-v2-optimized` applies, in one flag, the settings that benchmarks of the experimental Rust-overlay stellar-core image use. Without it missions keep their standard configs, resources and limits, so runs against the stellar-core master image need nothing. The run log lists what it sets (`--overlay-v2-optimized: ...` lines). It sets:

* for `MinBlockTime*`: tx-set limits at 125% of the offered txs per ledger instead of 2x (every tx-set build on the Rust-overlay core pulls twice these limits from the mempool over IPC), 960 s of load per candidate (a 60 s warm-up, then three judged 5-minute windows) instead of 300 s, and e2e latency measured on the load-generating nodes, as with `--measure-e2e-latency`;
* for `MinBlockTime*`: core's 10 MiB tx-set byte budget (`TESTING_MAX_CLASSIC_BYTE_ALLOWANCE` + `TESTING_MAX_SOROBAN_BYTE_ALLOWANCE`) split in proportion to the classic and Soroban bytes the run offers, at least 1 MiB each, instead of core's 5 MiB each (5 MiB caps a Soroban phase at about 6900 SAC payments). A Soroban-only run gets 9 MiB for Soroban and a classic-only run 9 MiB for classic; the max-TPS missions keep their own splits;
* for the perf missions (`MinBlockTimeClassic`/`Mixed`, `MaxTPSClassic`/`Mixed`): in-memory BucketListDB, one stellar-core pod per worker node (as `--one-stellar-core-per-host`), no test-only tx meta (images older than v27.0.0 reject that key, so the flag needs a newer image), and validators with an 8 vCPU request, no CPU limit and 16 GiB memory, whose containers get `TOKIO_WORKER_THREADS=8` unless `--core-env` sets it;
* 8 dependent-tx clusters in the Soroban limit upgrades;
* bounded overlay-mesh waits wherever a mission waits for the overlay to connect: nodes must answer within 5 minutes of starting, and a mesh that stops growing for 60 s (or is still incomplete after 2 minutes) is redrawn by restarting all nodes, up to 3 attempts, so a wedged mesh fails the run in about 20 minutes at worst instead of hanging it.

### Additional options for mixed pre-generated classic and synthetic Soroban traffic

In addition to the parameters in the previous section, `MinBlockTimeMixed` supports:

* `--min-block-time-mixed-mode`: Exact stellar-core loadgen mode. Must be one of `mixed_pregen_sac_payment`, `mixed_pregen_oz_token_transfer`, or `mixed_pregen_soroswap_swap`. Defaults to `mixed_pregen_sac_payment`.
* `--classic-tx-rate`: Classic payment TPS for the pre-generated classic stream.
* `--soroban-tx-rate`: Soroban TPS for the selected synthetic Soroban stream.

The load runs on every validator of the load-generating organizations, each with its own slice of the genesis accounts, at an equal share of the offered rate for the whole load window.

If neither stream-specific TPS is set, `--tx-rate` is split evenly between classic and Soroban traffic. If either stream-specific TPS is set, any omitted stream defaults to `0`, and the fixed TPS for the mission is the sum of `--classic-tx-rate` and `--soroban-tx-rate`.

Before enabling overlay-only mode, the mission upgrades classic max tx set size from `--classic-tx-rate * 15`, and Soroban network limits from the selected transaction type's per-transaction resources multiplied by `--soroban-tx-rate * 15` (~15 seconds of throughput as leeway).

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

To benchmark the Rust-overlay image at a single 1 s close time and 5000 SAC TPS on a 57-validator (19-organization) topology:

```bash
dotnet run --project src/App/App.fsproj --configuration Release -- mission MinBlockTimeMixed --image=<rust-overlay-perftest-image> --netdelay-image=stellar/sdf-netdelay:latest --overlay-v2-optimized --tier1-org-count=19 --min-block-time-mixed-mode=mixed_pregen_sac_payment --classic-tx-rate=0 --soroban-tx-rate=5000 --min-block-time-ms=1000 --max-block-time-ms=1000 --num-pregenerated-txs=10000
```

To run the mixed overlay-only mode at 600 total TPS with Soroswap synthetic Soroban swaps:

```bash
dotnet run --project src/App/App.fsproj --configuration Release -- mission MinBlockTimeMixed --image=stellar/unsafe-stellar-core:<stellar-core-perftest> --netdelay-image=stellar/sdf-netdelay:latest --min-block-time-mixed-mode=mixed_pregen_soroswap_swap --classic-tx-rate=300 --soroban-tx-rate=300 --min-block-time-ms=4000 --max-block-time-ms=5000
```
