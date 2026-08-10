# Jenkinsfile options for ParallelCatchupV2

What changed: the chart no longer knows that spot and on-demand exist.
`values-ondemand.yaml` is deleted, `monitor.capacityType` is gone, and the
monitor no longer reads `karpenter.sh/capacity-type`. Everything that used to be
derived is now passed in, so **Jenkins is the only place that knows which
capacity a run targets**.

Nothing has a safe default any more. A run that omits these gets *no* capacity
constraint and the chart's built-in (spot) claim maps.

## What every pooled run must pass

Three things, and both capacities pass all three:

| | spot | on-demand |
|---|---|---|
| capacity label | `--require-node-labels-pc-v2 catchup-capacity:spot` | `--require-node-labels-pc-v2 catchup-capacity:od` |
| cpu claims | `--pubnet-parallel-catchup-pool-cpu "<spot cpu map>"` | `--pubnet-parallel-catchup-pool-cpu "<od cpu map>"` |
| memory claims | `--pubnet-parallel-catchup-pool-mem "<spot mem map>"` | `--pubnet-parallel-catchup-pool-mem "<od mem map>"` |

Plus the ones that already existed and are unchanged:

```
--pubnet-parallel-catchup-pool-prefix catchup
--pubnet-parallel-catchup-storage-mode pvc|ephemeral
```

### The maps

Copy these verbatim. They are the values that were in `values.yaml` and
`values-ondemand.yaml` before the change.

**spot** — pools were doubled on 2026-08-04, so a claim is half a node and two
pods share it:

```
poolCpu: subdwarf:0.85,dwarf:0.85,subgiant:1.85,giant:1.85,supergiant:1.85,hypergiant:1.85,supernova:3.80,protostar:1.85,nebula:1.80
poolMem: subdwarf:1280Mi,dwarf:1280Mi,subgiant:2816Mi,giant:6656Mi,supergiant:14336Mi,hypergiant:29696Mi,supernova:60416Mi,protostar:29696Mi,nebula:9216Mi
```

**on-demand** — pools kept their original sizes, so a claim is the whole node
and one pod gets it:

```
poolCpu: subdwarf:0.45,dwarf:0.45,subgiant:1.40,giant:1.40,supergiant:1.40,hypergiant:3.35,supernova:7.20,protostar:1.40,nebula:3.35
poolMem: subdwarf:576Mi,dwarf:576Mi,subgiant:2048Mi,giant:5248Mi,supergiant:12416Mi,hypergiant:27328Mi,supernova:57216Mi,protostar:27328Mi,nebula:12416Mi
```

Do **not** cross them. On-demand claims are sized against *allocatable*; the
spot claims are the on-demand node's *nameplate*, and nameplate is not
allocatable. Shipping spot claims to an on-demand pool makes every tier
unschedulable and Karpenter provisions nothing — pods sit Pending, which reads
as slow provisioning rather than as a sizing bug. That is what happened on
2026-08-07 and it cost a day to spot.

## The pairing that used to be automatic

Storage mode and capacity are now fully independent. Nothing derives one from
the other and nothing will object if they disagree.

The pairing every production run wants:

```
pvc       + catchup-capacity:spot
ephemeral + catchup-capacity:od
```

Why it matters: `pvc` keeps `/data` across pods, so an evicted range resumes at
LCL+1 — that is what makes spot survivable. `ephemeral` puts `/data` on the node
and has no resume, so a spot reclaim costs the whole range from scratch.

They were split on purpose, so a test run can pair `ephemeral` with `spot`
deliberately to measure what a reclaim actually costs. Production should not.

## Example

```
dotnet run --project src/App/App.fsproj -- mission HistoryPubnetParallelCatchupV2 \
  --image <core image> \
  --namespace stellar-supercluster \
  --pubnet-parallel-catchup-pool-prefix catchup \
  --pubnet-parallel-catchup-storage-mode pvc \
  --require-node-labels-pc-v2 catchup-capacity:spot \
  --pubnet-parallel-catchup-pool-cpu "subdwarf:0.85,dwarf:0.85,subgiant:1.85,giant:1.85,supergiant:1.85,hypergiant:1.85,supernova:3.80,protostar:1.85,nebula:1.80" \
  --pubnet-parallel-catchup-pool-mem "subdwarf:1280Mi,dwarf:1280Mi,subgiant:2816Mi,giant:6656Mi,supergiant:14336Mi,hypergiant:29696Mi,supernova:60416Mi,protostar:29696Mi,nebula:9216Mi" \
  --pubnet-parallel-catchup-profile <profile url or path> \
  --pubnet-parallel-catchup-num-workers 1024 \
  --pubnet-parallel-catchup-ledgers-per-job 16000 \
  --destination ./logs
```

## Failure modes, and which are loud

**Loud** — a missing tier in either map fails `POST /start` with a 400 and a
reason, so the driver stops immediately:

```
POOL_CPU has no entry for nebula; a pooled range routed there would keep
the flat request and share its node
```

The check covers every ladder tier plus `protostar` (ranges newer than the
profile) and `nebula` (runs with no profile at all). Both are routed to, so both
need entries. `subdwarf` is dormant — it can never be selected — but it still
needs a map entry.

**Silent** — these produce a run that finishes and costs more than it should:

- *capacity label omitted.* Both capacities of a tier carry the same
  `purpose=catchup-<tier>` label, so nothing separates them. A spot run without
  `catchup-capacity:spot` can land on on-demand nodes and bill on-demand rates.
- *wrong map for the capacity.* Covered above. Not silent on on-demand (nothing
  schedules) but silent the other way: on-demand claims on spot pools pack one
  pod where two fit, halving throughput per node.
- *tier name typo in a map.* Reads as a missing tier, so `/start` catches it —
  the one typo class that is loud.

## Ordering note

If Jenkins passes several `--require-node-labels-pc-v2` entries, order does not
matter — they all become literal requirements. The mission reserves
`worker.requireNodeLabels[0]` for its own pool-routing label and starts the
caller's entries at index 1. Before this change they both wrote index 0 and the
routing label was overwritten, which put ranges on arbitrary tiers.

## Image

The chart pins `stellajuna/ssc-jm:2026-08-10a`, a personal Docker Hub repo.
**This is a dev pin and must be replaced before ssc-eks.**
