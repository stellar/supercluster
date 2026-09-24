// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MinBlockTimeTest

// Binary-searches for the minimum ledger target close time the network can
// sustain at a fixed TPS, using stellar-core's
// `ledger.age.closed-histogram` metric as the SLA signal.

open FSharp.Data
open Logging
open PollRetry
open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarFormation
open StellarMissionContext
open StellarNetworkData
open StellarStatefulSets
open StellarSupercluster

let private smallNetworkSize = 10

// Candidate close times are always whole seconds: sub-second values hit a
// known rounding issue in close-time handling, so the search must never
// propose one. The candidates are the whole seconds in [minMs, maxMs], bounds
// included, so the default [4000, 5000] range evaluates 4000 and, only if that
// fails, 5000. Exposed for unit tests.
let wholeSecondCandidates (minMs: int) (maxMs: int) : int list =
    let first = max 1000 (((minMs + 999) / 1000) * 1000)
    let last = (maxMs / 1000) * 1000
    [ first .. 1000 .. last ]

// The close times a run evaluates. Equal bounds name one explicit target, such
// as a milestone latency, which is evaluated exactly once as given, in
// milliseconds and without whole-second rounding; otherwise the whole seconds
// in the range. Exposed for unit tests.
let closeTimeCandidates (minMs: int) (maxMs: int) : int list =
    if minMs = maxMs then [ minMs ] else wholeSecondCandidates minMs maxMs

// Binary search over ascending candidates for the smallest one that passes,
// assuming every candidate above a passing one passes too. Returns None when
// none passes. Exposed for unit tests.
let searchMinPassing (candidates: int list) (passes: int -> bool) : int option =
    let arr = Array.ofList candidates
    let mutable failIdx = -1
    let mutable passIdx = arr.Length

    while passIdx - failIdx > 1 do
        let mid = (failIdx + passIdx) / 2

        if passes arr.[mid] then passIdx <- mid else failIdx <- mid

    if passIdx < arr.Length then Some arr.[passIdx] else None

// For the purposes of min block test, use high value to avoid noise from SCP timeouts
let private timeout = 2000

let private txSetSizeBufferMultiplier = 2

// Buffer as a percentage of the offered txs per ledger (200 = the historical
// 2x). Core pulls 2x the sum of the classic and Soroban limits from the
// mempool on every nomination, so this also sizes the IPC reply.
let private maxTxSetSizeForTargetPct (kind: string) (targetMs: int) (txRate: int) (bufferPct: int) =
    let scaled = int64 targetMs * int64 txRate * int64 bufferPct

    let txSetSize = (scaled + 99_999L) / 100_000L

    if txSetSize > int64 System.Int32.MaxValue then
        failwithf "%s MaxTxSetSize %d exceeds supported int range" kind txSetSize

    max (int txSetSize) 100

let private maxTxSetSizeForTarget (kind: string) (targetMs: int) (txRate: int) =
    maxTxSetSizeForTargetPct kind targetMs txRate (txSetSizeBufferMultiplier * 100)

// Exposed for reuse by MissionTriggerTimerMixConsensus, which runs the same
// MIXED_PREGEN_* load without the binary search.
let classicMaxTxSetSizeForTarget (targetMs: int) (classicTxRate: int) =
    maxTxSetSizeForTarget "Classic" targetMs classicTxRate

let classicMaxTxSetSizeForTargetPct (targetMs: int) (classicTxRate: int) (bufferPct: int) =
    maxTxSetSizeForTargetPct "Classic" targetMs classicTxRate bufferPct

let private sorobanMaxTxSetSizeForTargetPct (targetMs: int) (sorobanTxRate: int) (bufferPct: int) =
    maxTxSetSizeForTargetPct "Soroban" targetMs sorobanTxRate bufferPct

type private MixedPregenSorobanResources =
    { instructions: int64
      readBytes: int
      writeBytes: int
      readOnlyEntries: int
      readWriteEntries: int
      txSizeBytes: int
      contractEventBytes: int }

let private usesPregeneratedTxs (mode: LoadGenMode) = mode = PayPregenerated || isMixedPregenMode mode

// Approximate size of a classic payment: envelope, one payment operation and
// one signature (about 196 bytes of XDR).
let private classicPaymentTxBytes = 200L

let private mixedPregenSorobanResources (mode: LoadGenMode) =
    match mode with
    | MixedPregenSACPayment ->
        { instructions = 250000L
          readBytes = 800
          writeBytes = 800
          readOnlyEntries = 2
          readWriteEntries = 2
          txSizeBytes = 1000
          contractEventBytes = 200 }
    | MixedPregenOZTokenTransfer ->
        { instructions = 5000000L
          readBytes = 5000
          writeBytes = 5000
          readOnlyEntries = 2
          readWriteEntries = 2
          txSizeBytes = 1000
          contractEventBytes = 200 }
    | MixedPregenSoroswapSwap ->
        { instructions = 5000000L
          readBytes = 5000
          writeBytes = 5000
          readOnlyEntries = 5
          readWriteEntries = 5
          txSizeBytes = 2000
          contractEventBytes = 200 }
    | _ -> failwithf "Mode %s is not a MIXED_PREGEN_* mode" (mode.ToString())

let private scaleLedgerLimit (name: string) (value: int) (ledgerMaxTxCount: int) =
    let scaled = int64 value * int64 ledgerMaxTxCount

    if scaled > int64 System.Int32.MaxValue then
        failwithf "Scaled %s limit %d exceeds supported int range" name scaled

    int scaled

type private MixedPregenSorobanLimits =
    { ledgerMaxInstructions: int64
      ledgerMaxReadBytes: int
      ledgerMaxWriteBytes: int
      ledgerMaxReadEntries: int
      ledgerMaxWriteEntries: int
      ledgerMaxTxCount: int
      ledgerMaxTransactionsSizeBytes: int
      txMaxInstructions: int64
      txMaxReadBytes: int
      txMaxWriteBytes: int
      txMaxReadEntries: int
      txMaxWriteEntries: int
      txMaxFootprintSize: int option
      txMaxSizeBytes: int
      txMaxContractEventsSizeBytes: int }

let private waitForMixedPregenSorobanLimits (peer: Peer) (limits: MixedPregenSorobanLimits) =
    RetryUntilTrue
        (fun _ ->
            let info = peer.GetSorobanInfo()

            let txMaxFootprintOk =
                match limits.txMaxFootprintSize with
                | Some expected ->
                    match info.Tx.JsonValue.TryGetProperty("max_footprint_size") with
                    | Some value -> value.AsInteger() = expected
                    | None -> false
                | None -> true

            info.Ledger.MaxInstructions = limits.ledgerMaxInstructions
            && info.Ledger.MaxReadBytes = limits.ledgerMaxReadBytes
            && info.Ledger.MaxWriteBytes = limits.ledgerMaxWriteBytes
            && info.Ledger.MaxReadLedgerEntries = limits.ledgerMaxReadEntries
            && info.Ledger.MaxWriteLedgerEntries = limits.ledgerMaxWriteEntries
            && info.Ledger.MaxTxCount = limits.ledgerMaxTxCount
            && info.Ledger.MaxTxSizeBytes = limits.ledgerMaxTransactionsSizeBytes
            && info.Tx.MaxInstructions = limits.txMaxInstructions
            && info.Tx.MaxReadBytes = limits.txMaxReadBytes
            && info.Tx.MaxWriteBytes = limits.txMaxWriteBytes
            && info.Tx.MaxReadLedgerEntries = limits.txMaxReadEntries
            && info.Tx.MaxWriteLedgerEntries = limits.txMaxWriteEntries
            && txMaxFootprintOk
            && info.Tx.MaxSizeBytes = limits.txMaxSizeBytes
            && info.Tx.MaxContractEventsSizeBytes = limits.txMaxContractEventsSizeBytes)
        (fun _ -> LogInfo "Waiting for MIXED_PREGEN_* Soroban limits on %s" peer.ShortName.StringName)

let upgradeMixedPregenSorobanLimitsWith
    (formation: StellarFormation)
    (coreSets: CoreSet list)
    (baseLoadGen: LoadGen)
    (targetMs: int)
    (bufferPct: int)
    (dependentTxClusters: int option)
    =
    let sorobanTxRate = baseLoadGen.sorobanTxRate |> Option.defaultValue 0

    if sorobanTxRate > 0 then
        let resources = mixedPregenSorobanResources baseLoadGen.mode
        let footprintEntries = resources.readOnlyEntries + resources.readWriteEntries
        let targetMaxTxSetSize = sorobanMaxTxSetSizeForTargetPct targetMs sorobanTxRate bufferPct

        LogInfo
            "Upgrading MIXED_PREGEN_* Soroban limits for %s: Soroban MaxTxSetSize=%d for T=%dms, soroban TPS=%d, buffer=%d%%"
            (baseLoadGen.mode.ToString())
            targetMaxTxSetSize
            targetMs
            sorobanTxRate
            bufferPct

        formation.UpgradeSorobanMaxTxSetSize coreSets targetMaxTxSetSize
        formation.SetupUpgradeContract coreSets.Head
        let peer = formation.NetworkCfg.GetPeer coreSets.Head 0

        let limits =
            { ledgerMaxInstructions =
                  max (peer.GetLedgerMaxInstructions()) (resources.instructions * int64 targetMaxTxSetSize)
              ledgerMaxReadBytes =
                  max
                      (peer.GetLedgerReadBytes())
                      (scaleLedgerLimit "ledger read bytes" resources.readBytes targetMaxTxSetSize)
              ledgerMaxWriteBytes =
                  max
                      (peer.GetLedgerWriteBytes())
                      (scaleLedgerLimit "ledger write bytes" resources.writeBytes targetMaxTxSetSize)
              ledgerMaxReadEntries =
                  max
                      (peer.GetLedgerReadEntries())
                      (scaleLedgerLimit "ledger read entries" footprintEntries targetMaxTxSetSize)
              ledgerMaxWriteEntries =
                  max
                      (peer.GetLedgerWriteEntries())
                      (scaleLedgerLimit "ledger write entries" resources.readWriteEntries targetMaxTxSetSize)
              ledgerMaxTxCount = max (peer.GetLedgerMaxTxCount()) targetMaxTxSetSize
              ledgerMaxTransactionsSizeBytes =
                  max
                      (peer.GetLedgerMaxTransactionsSizeBytes())
                      (scaleLedgerLimit "ledger tx bytes" resources.txSizeBytes targetMaxTxSetSize)
              txMaxInstructions = max (peer.GetTxMaxInstructions()) resources.instructions
              txMaxReadBytes = max (peer.GetTxReadBytes()) resources.readBytes
              txMaxWriteBytes = max (peer.GetTxWriteBytes()) resources.writeBytes
              txMaxReadEntries = max (peer.GetTxReadEntries()) footprintEntries
              txMaxWriteEntries = max (peer.GetTxWriteEntries()) resources.readWriteEntries
              txMaxFootprintSize = peer.GetTxMaxFootprintSize() |> Option.map (max footprintEntries)
              txMaxSizeBytes = max (peer.GetMaxTxSize()) resources.txSizeBytes
              txMaxContractEventsSizeBytes = max (peer.GetTxMaxContractEventsSize()) resources.contractEventBytes }

        formation.DeployUpgradeEntriesAndArmAfter
            coreSets
            { LoadGen.GetDefault() with
                  mode = CreateSorobanUpgrade
                  ledgerMaxInstructions = Some limits.ledgerMaxInstructions
                  ledgerMaxReadBytes = Some limits.ledgerMaxReadBytes
                  ledgerMaxWriteBytes = Some limits.ledgerMaxWriteBytes
                  ledgerMaxReadLedgerEntries = Some limits.ledgerMaxReadEntries
                  ledgerMaxWriteLedgerEntries = Some limits.ledgerMaxWriteEntries
                  ledgerMaxTxCount = Some limits.ledgerMaxTxCount
                  ledgerMaxTransactionsSizeBytes = Some limits.ledgerMaxTransactionsSizeBytes
                  txMaxInstructions = Some limits.txMaxInstructions
                  txMaxReadBytes = Some limits.txMaxReadBytes
                  txMaxWriteBytes = Some limits.txMaxWriteBytes
                  txMaxReadLedgerEntries = Some limits.txMaxReadEntries
                  txMaxWriteLedgerEntries = Some limits.txMaxWriteEntries
                  txMaxFootprintSize = limits.txMaxFootprintSize
                  txMaxSizeBytes = Some limits.txMaxSizeBytes
                  txMaxContractEventsSizeBytes = Some limits.txMaxContractEventsSizeBytes
                  // The network default is a single dependent-tx cluster, which
                  // serializes Soroban execution and holds a ledger to one
                  // cluster's instructions; --overlay-v2-optimized allows 8.
                  ledgerMaxDependentTxClusters = dependentTxClusters }
            (System.TimeSpan.FromSeconds(20.0))

        waitForMixedPregenSorobanLimits peer limits

        match dependentTxClusters with
        | Some n -> peer.WaitForMaxDependentTxClusters n
        | None -> ()

// Exposed for reuse by MissionTriggerTimerMixConsensus (historical 2x buffer).
let upgradeMixedPregenSorobanLimits
    (formation: StellarFormation)
    (coreSets: CoreSet list)
    (baseLoadGen: LoadGen)
    (targetMs: int)
    =
    upgradeMixedPregenSorobanLimitsWith formation coreSets baseLoadGen targetMs (txSetSizeBufferMultiplier * 100) None

// Exposed for reuse by MissionTriggerTimerMixConsensus.
let toggleOverlayOnlyMode (formation: StellarFormation) (coreSets: CoreSet list) =
    formation.NetworkCfg.EachPeerInSets
        (List.toArray coreSets)
        (fun peer ->
            let res = peer.ToggleOverlayOnlyMode()
            LogInfo "Toggled overlay-only mode on %s: %s" peer.ShortName.StringName res)

let private readLedgerAgePercentiles (peer: Peer) : Peer * float * float =
    let h = peer.GetMetrics().LedgerAgeClosedHistogram
    peer, float h.``75``, float h.``99``

// Transactions included in closed ledgers since the candidate cleared the
// metrics. ledger.transaction.count is Updated by LedgerManagerImpl even in
// overlay-only mode (it is marked before the apply-skip branch), so its sum
// counts every transaction that made it into a tx set with apply disabled.
let private readLedgerTxsIncluded (peer: Peer) : Peer * float =
    peer, float (peer.GetMetrics().LedgerTransactionCount.Sum)

// Whether every node reports the same count of transactions in its ledgers,
// covering the `offered` load. Exposed for unit tests.
let inclusionAgrees (offered: int) (included: ('node * float) list) : bool =
    match included |> List.map snd |> List.distinct with
    | [ txs ] -> txs >= float offered
    | _ -> false

// Every node's included-transaction count after a completed load, re-read for
// up to two close times until they agree (inclusionAgrees): a node that had
// not yet closed the last loaded ledger when first read catches up. After a
// completed load only empty ledgers close, so waiting cannot hide a mismatch.
let private collectLedgerTxsIncluded
    (formation: StellarFormation)
    (coreSets: CoreSet list)
    (offered: int)
    (targetMs: int)
    : (Peer * float) list =
    let readAll () =
        formation.NetworkCfg.PeersInSets(List.toArray coreSets)
        |> List.map (fun peer -> async { return readLedgerTxsIncluded peer })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList

    let clock = System.Diagnostics.Stopwatch.StartNew()
    let mutable included = readAll ()

    while not (inclusionAgrees offered included)
          && clock.ElapsedMilliseconds < 2L * int64 targetMs do
        System.Threading.Thread.Sleep 1000
        included <- readAll ()

    included

// Cross-check for overlay-only candidates after a completed load: every node
// must report the same count of transactions in its ledgers, covering the
// offered load. Loadgen completes only once every transaction its node
// submitted has been included in a closed ledger, and nodes that close the
// same ledgers count the same transactions, so a load the network carried
// agrees; this confirms it from the ledgers' own counts, which
// checkLedgerAgeSLA does not look at (closing near-empty ledgers on schedule
// passes it trivially). Comparing totals over the window, rather than txs per
// ledger against TPS x T, is not skewed by the idle ledgers around the load or
// by long ledgers carrying more. A node still behind after
// collectLedgerTxsIncluded's re-reads, or a shortfall on every node, fails the
// candidate. Returns why, or None.
let private inclusionFailure (included: (Peer * float) list) (targetMs: int) (offered: int) : string option =
    if List.isEmpty included then
        None
    else
        match included |> List.map snd |> List.distinct with
        | [ txs ] when txs >= float offered ->
            LogInfo
                "Inclusion at T=%dms: every node's ledgers hold %.0f transactions for the %d offered -> PASS"
                targetMs
                txs
                offered

            None
        | [ txs ] ->
            LogError
                "Every node's ledgers hold %.0f of the %d offered transactions at T=%dms, although loadgen reported all of them included."
                txs
                offered
                targetMs

            Some(sprintf "every node's ledgers hold %.0f of the %d offered transactions" txs offered)
        | _ ->
            LogError
                "Nodes disagree on how many transactions their ledgers hold at T=%dms (%s; %d offered): a node fell behind, forked or did not close every ledger itself."
                targetMs
                (included
                 |> List.map (fun (peer, txs) -> sprintf "%s: %.0f" peer.ShortName.StringName txs)
                 |> String.concat ", ")
                offered

            Some "nodes disagree on how many transactions their ledgers hold"

let private collectLedgerAgePercentiles
    (formation: StellarFormation)
    (coreSets: CoreSet list)
    : (Peer * float * float) list =
    formation.NetworkCfg.PeersInSets(List.toArray coreSets)
    |> List.map (fun peer -> async { return readLedgerAgePercentiles peer })
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Array.toList

let private logE2eLatencyMetrics (formation: StellarFormation) (coreSets: CoreSet list) : unit =
    let e2eLatencyMetrics : (string * (Metrics.Metrics -> Metrics.GenericCounter option)) list =
        [ "min", (fun m -> m.LoadgenTxLatencyRunMinMs)
          "mean", (fun m -> m.LoadgenTxLatencyRunMeanMs)
          "p50", (fun m -> m.LoadgenTxLatencyRunP50Ms)
          "p75", (fun m -> m.LoadgenTxLatencyRunP75Ms)
          "p99", (fun m -> m.LoadgenTxLatencyRunP99Ms)
          "max", (fun m -> m.LoadgenTxLatencyRunMaxMs) ]

    for peer in formation.NetworkCfg.PeersInSets(List.toArray coreSets) do
        let metrics = peer.GetMetrics()

        let summary =
            e2eLatencyMetrics
            |> List.map
                (fun (name, get) ->
                    let value =
                        get metrics
                        |> Option.map (fun c -> string c.Count)
                        |> Option.defaultValue "none"

                    sprintf "%s=%s" name value)
            |> String.concat " "

        LogInfo "TX e2e latency: peer=%s loadgen-tx-latency-run-ms: %s" peer.ShortName.StringName summary

// Returns true iff every peer's ledger.age.closed-histogram satisfies:
//   P75 in [0.80*T, 1.20*T)
//   P99 <= 2*T
//
// Evaluate a pre-collected snapshot. Core keeps closing ledgers after loadgen
// exits, so delaying metric collection skews SLA reads.
//
// FIXME: the P75 tolerance is temporarily widened to +/-20% because
// stellar-core currently has perf regressions that prevent the intended
// +/-5% band from being achievable. Tighten this back to 0.95/1.05 (or
// lower) once those regressions are fixed.
let private checkLedgerAgeSLA (percentiles: (Peer * float * float) list) (targetMs: int) : bool =
    let tf = float targetMs
    let tLo = tf * 0.80
    let tHi = tf * 1.20
    let p99Max = tf * 2.0
    let mutable ok = true

    LogInfo
        "SLA thresholds at T=%dms: every peer's ledger.age.closed-histogram P75 in [%.0f, %.0f) ms and P99 <= %.0f ms (the P75 band is the temporarily widened +/-20%%; the target is a close-time target, not a P99 guarantee)"
        targetMs
        tLo
        tHi
        p99Max

    let formatDeviation value =
        let deviation = (value - tf) / tf * 100.0

        if deviation >= 0.0 then
            sprintf "+%.1f%%" deviation
        else
            sprintf "%.1f%%" deviation

    for peer, p75, p99 in percentiles do
        let peerOk = p75 >= tLo && p75 < tHi && p99 <= p99Max

        LogInfo
            "peer=%s T=%dms p75=%.0f p99=%.0f -> %s"
            peer.ShortName.StringName
            targetMs
            p75
            p99
            (if peerOk then "PASS" else "FAIL")

        if not peerOk then ok <- false

    if not percentiles.IsEmpty then
        let avgP75 = percentiles |> List.averageBy (fun (_, p75, _) -> p75)
        let avgP99 = percentiles |> List.averageBy (fun (_, _, p99) -> p99)

        LogInfo
            "all peers T=%dms avg-p75=%.0f (%s vs target) avg-p99=%.0f (%s vs target)"
            targetMs
            avgP75
            (formatDeviation avgP75)
            avgP99
            (formatDeviation avgP99)

    ok

// The load-generating core sets a MIXED_PREGEN_* run uses. Its load runs on
// every validator of these sets, so they hold at most one validator per
// requested TPS (every generator gets a non-zero share), and always at least
// one set. Exposed for unit tests.
let activeLoadGenCoreSets (requestedTps: int) (loadGenNodes: CoreSet list) : CoreSet list =
    let fitting =
        loadGenNodes
        |> List.scan (fun total (cs: CoreSet) -> total + cs.options.nodeCount) 0
        |> List.tail
        |> List.takeWhile (fun total -> total <= max 1 requestedTps)
        |> List.length

    List.truncate (max 1 fitting) loadGenNodes

// Marks the core sets in `active` as generating load, as --loadgen-keys does
// for pubnet topologies, so per-node settings keyed on it (the e2e latency
// metric of --measure-e2e-latency) land on the nodes that submit. Matches by
// name, since `sets` may carry other option changes. Exposed for unit tests.
let markLoadGenerators (active: CoreSet list) (sets: CoreSet list) : CoreSet list =
    let names = active |> List.map (fun cs -> cs.name) |> Set.ofList

    sets
    |> List.map
        (fun (cs: CoreSet) ->
            if names.Contains cs.name then
                { cs with options = { cs.options with generatesLoad = true } }
            else
                cs)

let minBlockTimeTest (context: MissionContext) (baseLoadGen: LoadGen) (setupCfg: LoadGen option) =
    // --overlay-v2-optimized: see MissionContext.describeOverlayV2.
    let v2 = context.overlayV2Optimized

    let allNodes =
        if context.pubnetData.IsSome then
            FullPubnetCoreSets context true false
        else
            StableApproximateTier1CoreSetsWithExtraOrgs
                context.image
                (if context.flatQuorum.IsSome then context.flatQuorum.Value else false)
                context.tier1OrgsToAdd

    // Mirrors MaxTPSTest: on small networks, GeneratePaymentLoad runs out of
    // source accounts at high TPS, so switch to PayPregenerated which uses
    // genesis-created accounts and pregenerated signed txs.
    let baseLoadGen =
        if List.length allNodes <= 30 && baseLoadGen.mode = GeneratePaymentLoad then
            { baseLoadGen with mode = PayPregenerated }
        else
            baseLoadGen

    // Classic and Soroban transaction bytes offered per second, by which
    // --overlay-v2-optimized splits the tx-set byte allowance. Only the ratio
    // matters: both phases grow alike with the close time.
    let offeredTxBytesPerSec =
        if isMixedPregenMode baseLoadGen.mode then
            let classicTps =
                match baseLoadGen.classicTxRate, context.minBlockTimeMixedClassicTxRate with
                | Some rate, _ -> rate
                | None, Some rate -> rate
                | None, None -> context.txRate

            let sorobanTps = baseLoadGen.sorobanTxRate |> Option.defaultValue 0
            let sorobanTxBytes = int64 (mixedPregenSorobanResources baseLoadGen.mode).txSizeBytes
            int64 classicTps * classicPaymentTxBytes, int64 sorobanTps * sorobanTxBytes
        elif baseLoadGen.mode = GeneratePaymentLoad || baseLoadGen.mode = PayPregenerated then
            int64 context.txRate * classicPaymentTxBytes, 0L
        else
            // The other modes offer Soroban load only.
            0L, int64 context.txRate

    let context =
        { context with
              runForMinBlockTime = true
              genesisTestAccountCount = Some(context.genesisTestAccountCount |> Option.defaultValue 100000)
              offeredTxBytesPerSec = Some offeredTxBytesPerSec
              // --overlay-v2-optimized always measures e2e latency.
              measureE2eLatency = context.measureE2eLatency || v2
              numPregeneratedTxs =
                  if usesPregeneratedTxs baseLoadGen.mode then
                      Some(context.numPregeneratedTxs |> Option.defaultValue 2500000)
                  else
                      None }

    match MissionContext.txSetByteAllowances context with
    | Some allowances when context.overlayV2Optimized ->
        LogInfo
            "Tx-set byte allowances: %s, split by the offered bytes (classic %d B/s, Soroban %d B/s)"
            (MissionContext.describeTxSetByteAllowances allowances)
            (fst offeredTxBytesPerSec)
            (snd offeredTxBytesPerSec)
    | _ -> ()

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) allNodes
    let loadGenNodes = List.filter (fun (cs: CoreSet) -> cs.options.generatesLoad) allNodes

    let loadGenNodes =
        if List.isEmpty loadGenNodes then
            if List.length allNodes > smallNetworkSize then tier1 else allNodes
        else
            loadGenNodes

    // MIXED_PREGEN_* load runs on every validator of the active core sets,
    // each with its own account slice; other modes run it on node 0 of each
    // load-generating core set.
    let everyValidator = isMixedPregenMode baseLoadGen.mode

    let activeLoadGenNodes =
        if everyValidator then
            let requestedCount =
                max
                    (baseLoadGen.classicTxRate |> Option.defaultValue 0)
                    (baseLoadGen.sorobanTxRate |> Option.defaultValue 0)

            activeLoadGenCoreSets requestedCount loadGenNodes
        else
            loadGenNodes

    let isActiveLoadGenNode cs = List.exists (fun (cs': CoreSet) -> cs' = cs) activeLoadGenNodes

    let context = { context with pregenerateTxsPerValidator = everyValidator }

    // For pre-generated modes, partition genesis accounts evenly across the
    // active loadgen nodes and assign offsets so each signs txs against its own
    // slice; the other core sets pregenerate nothing, so low-TPS mixed runs
    // still have enough local accounts on the nodes that generate load. With
    // load on every validator, each core set's slice covers all of its
    // validators (StellarKubeSpecs.PregenerationOptionsForPeer splits it per
    // pod).
    let allNodes =
        match context.numPregeneratedTxs, context.genesisTestAccountCount, baseLoadGen.mode with
        | Some txs, Some accounts, mode when usesPregeneratedTxs mode ->
            let generators (cs: CoreSet) = if everyValidator then cs.options.nodeCount else 1
            let partitionCount = List.sumBy generators activeLoadGenNodes
            let accountsPerNode = accounts / partitionCount
            let mutable j = 0

            List.map
                (fun (cs: CoreSet) ->
                    let pregenerateTxs =
                        if isActiveLoadGenNode cs then
                            let i = j
                            j <- j + generators cs
                            Some(txs, accountsPerNode, accountsPerNode * i)
                        else
                            Some(0, 1, 0)

                    { cs with
                          options =
                              { cs.options with
                                    initialization = { cs.options.initialization with pregenerateTxs = pregenerateTxs } } })
                allNodes
        | _ -> allNodes

    let allNodes = markLoadGenerators activeLoadGenNodes allNodes

    context.ExecuteWithOptionalConsistencyCheck
        allNodes
        None
        false
        (fun (formation: StellarFormation) ->

            let numAccounts = context.genesisTestAccountCount.Value
            let fixedTxRate = context.txRate
            let bufferPct = MissionContext.txSetSizeBufferPct context
            let loadDurationSec = MissionContext.minBlockTimeLoadDurationSec context

            let classicTxRateForLimits =
                match baseLoadGen.classicTxRate, context.minBlockTimeMixedClassicTxRate with
                | Some rate, _ -> rate
                | None, Some rate -> rate
                | None, None -> fixedTxRate

            if classicTxRateForLimits < 0 then
                failwith "--classic-tx-rate must be non-negative"

            let isPaymentOnly = baseLoadGen.mode = GeneratePaymentLoad || baseLoadGen.mode = PayPregenerated

            let setupCoreSets (coreSets: CoreSet list) =
                formation.WaitUntilConnected coreSets
                formation.ManualClose coreSets
                formation.WaitUntilSynced coreSets
                formation.UpgradeProtocolToLatest coreSets

                if not (isMixedPregenMode baseLoadGen.mode) && not isPaymentOnly then
                    MaxTPSTest.upgradeSorobanLedgerLimits context formation coreSets fixedTxRate
                    MaxTPSTest.upgradeSorobanTxLimits context formation coreSets

            setupCoreSets allNodes

            // One-time Soroban-invoke setup (mixed variant passes a config).
            match setupCfg with
            | Some cfg ->
                for cs in tier1 do
                    formation.RunLoadgen cs { cfg with accounts = numAccounts; minSorobanPercentSuccess = Some 100 }
            | None -> ()

            // Apply an SCP-timing upgrade for target T; waits for the peer
            // to observe the new ledger_close_time_ms before returning.
            let applySCPUpgrade (targetMs: int) =
                formation.SetupUpgradeContract allNodes.Head

                formation.DeployUpgradeEntriesAndArm
                    allNodes
                    { LoadGen.GetDefault() with
                          mode = CreateSorobanUpgrade
                          ledgerTargetCloseTimeMilliseconds = Some targetMs
                          ballotTimeoutInitialMilliseconds = Some timeout
                          ballotTimeoutIncrementMilliseconds = Some timeout
                          nominationTimeoutInitialMilliseconds = Some timeout
                          nominationTimeoutIncrementMilliseconds = Some timeout }
                    (System.DateTime.UtcNow.AddSeconds(20.0))

                let peer = formation.NetworkCfg.GetPeer allNodes.Head 0
                peer.WaitForScpLedgerCloseTime targetMs |> ignore

            let upgradeClassicMaxTxSetSize (targetMs: int) =
                let maxTxSetSize = classicMaxTxSetSizeForTargetPct targetMs classicTxRateForLimits bufferPct

                LogInfo
                    "Upgrading classic MaxTxSetSize to %d for T=%dms, classic TPS=%d, buffer=%d%%"
                    maxTxSetSize
                    targetMs
                    classicTxRateForLimits
                    bufferPct

                formation.UpgradeMaxTxSetSize allNodes maxTxSetSize

            let upgradeSorobanMaxTxSetSize (targetMs: int) =
                if isMixedPregenMode baseLoadGen.mode then
                    upgradeMixedPregenSorobanLimitsWith
                        formation
                        allNodes
                        baseLoadGen
                        targetMs
                        bufferPct
                        (if v2 then Some MaxTPSTest.sorobanDependentTxClusters else None)

            let evaluateAt (targetMs: int) : bool =
                let loadGen =
                    { baseLoadGen with
                          accounts = numAccounts
                          // Measurement window at fixed TPS: ~5 min, enough for a
                          // stable read of the SLA metric without draining the tx
                          // source, or longer under --overlay-v2-optimized.
                          txs = fixedTxRate * loadDurationSec
                          txrate = fixedTxRate }

                applySCPUpgrade targetMs
                upgradeClassicMaxTxSetSize targetMs
                upgradeSorobanMaxTxSetSize targetMs
                formation.clearMetrics allNodes

                // MIXED_PREGEN_* candidates run in overlay-only mode. Core skips
                // apply, but loadgen still completes only once every transaction
                // its node submitted has been included in a closed ledger (core
                // counts inclusion in this mode). Apply is never re-enabled: the
                // restart before the next candidate discards whatever is left in
                // the nodes' queues rather than making the network apply it at
                // once, which previously pushed nodes out of sync minutes after
                // the window. Everything below is therefore read in-mode.
                let overlayOnly = isMixedPregenMode baseLoadGen.mode

                if overlayOnly then toggleOverlayOnlyMode formation allNodes

                // Per doc/measuring-minimum-block-time.md, each failure below (a
                // loadgen failure, such as application lagging the offered load
                // or a node dropping out mid-window, nodes disagreeing on the
                // transactions in their ledgers, or nodes out of sync or
                // inconsistent) fails the candidate, not the mission: the search
                // raises its lower bound and continues. Killing the mission would
                // let one bad window discard the whole search.
                let loadgenFailure =
                    try
                        formation.RunMultiLoadgen activeLoadGenNodes loadGen
                        None
                    with e ->
                        LogWarn "Loadgen failed at T=%dms: %s" targetMs e.Message
                        Some(sprintf "load generation failed: %s" e.Message)

                // Snapshot the window's metrics before the health checks: core
                // keeps closing ledgers after loadgen exits, and the checks can
                // take long enough to skew the ledger age percentiles.
                //
                // Metrics that cannot be read after a completed load leave the
                // candidate unmeasured, a harness or network problem rather than
                // a verdict, so the mission aborts instead of letting the search
                // move on as if it had failed. After a failed load the candidate
                // has already failed, usually because a node dropped out, which
                // also makes its metrics unreadable, so that is only logged.
                let snapshot =
                    try
                        let percentiles = collectLedgerAgePercentiles formation allNodes

                        // Only a completed load is cross-checked: a failed one has
                        // already failed the candidate.
                        let included =
                            if overlayOnly && loadgenFailure.IsNone then
                                collectLedgerTxsIncluded formation allNodes loadGen.txs targetMs
                            else
                                []

                        if context.measureE2eLatency then
                            logE2eLatencyMetrics formation activeLoadGenNodes

                        Some(percentiles, included)
                    with
                    | e when loadgenFailure.IsNone ->
                        failwithf
                            "Could not read metrics at T=%dms after a completed load, so the candidate cannot be judged: %s"
                            targetMs
                            e.Message
                    | e ->
                        LogWarn "Could not read metrics at T=%dms after the failed load: %s" targetMs e.Message
                        None

                let healthFailure =
                    try
                        formation.CheckNoErrorsAndPairwiseConsistency()
                        formation.EnsureAllNodesInSync allNodes
                        None
                    with e ->
                        LogWarn "Health check failed at T=%dms: %s" targetMs e.Message
                        Some(sprintf "health check: %s" e.Message)

                // The close-time SLA is logged even when the candidate already
                // failed, so every window reports where its close times landed.
                let slaOk, missingInclusion =
                    match snapshot with
                    | Some (percentiles, included) ->
                        checkLedgerAgeSLA percentiles targetMs, inclusionFailure included targetMs loadGen.txs
                    | None -> false, None

                let failure = List.tryPick id [ loadgenFailure; missingInclusion; healthFailure ]

                let pass = failure.IsNone && slaOk

                LogInfo
                    "Candidate T=%dms at %d TPS: %s%s"
                    targetMs
                    fixedTxRate
                    (if pass then "PASS" else "FAIL")
                    (match failure with
                     | Some reason -> sprintf " (%s)" reason
                     | None when not slaOk -> " (close-time SLA not met)"
                     | None -> "")

                pass

            if context.minBlockTimeMs > context.maxBlockTimeMs then
                failwithf
                    "--min-block-time-ms=%d must not exceed --max-block-time-ms=%d"
                    context.minBlockTimeMs
                    context.maxBlockTimeMs

            let candidates = closeTimeCandidates context.minBlockTimeMs context.maxBlockTimeMs

            if List.isEmpty candidates then
                failwithf
                    "No whole-second close time in [%d, %d] ms: --min-block-time-ms and --max-block-time-ms must include at least one whole second"
                    context.minBlockTimeMs
                    context.maxBlockTimeMs

            LogInfo
                "Starting min block time search: T in [%d, %d] ms (candidates: %s), fixed TPS = %d"
                context.minBlockTimeMs
                context.maxBlockTimeMs
                (candidates |> List.map string |> String.concat ", ")
                fixedTxRate

            // Restart-or-sleep between iterations. Pre-generated modes require
            // a full restart because the pregenerated txs have baked-in
            // sequence numbers that become stale after a partial iteration.
            // Other modes just need time for the tx queue to drain.
            let restartCoreSetsOrWait () =
                if usesPregeneratedTxs baseLoadGen.mode then
                    LogInfo "Restarting all nodes to refresh pregenerated txs"

                    allNodes
                    |> List.map (fun set -> async { formation.Stop set.name })
                    |> Async.Parallel
                    |> Async.RunSynchronously
                    |> ignore

                    allNodes
                    |> List.map (fun set -> async { formation.Start set.name })
                    |> Async.Parallel
                    |> Async.RunSynchronously
                    |> ignore

                    setupCoreSets allNodes
                else
                    LogInfo "Waiting 5 min for network to recover"
                    System.Threading.Thread.Sleep(5 * 60 * 1000)
                    formation.EnsureAllNodesInSync allNodes

            // A ref cell rather than a mutable: evaluateCandidate is a closure,
            // and F# closures cannot capture mutable locals.
            let needsRecovery = ref false

            // One search step: recover from the previous candidate if needed,
            // then evaluate T.
            let evaluateCandidate (t: int) : bool =
                if needsRecovery.Value then restartCoreSetsOrWait ()

                if evaluateAt t then
                    LogInfo "SLA met at T=%dms; lowering upper bound" t
                    // Overlay-only runs never apply their transactions, so the
                    // nodes' state and queues are not fit for the next
                    // iteration: leftovers starve its upgrade-contract loadgen,
                    // which then fails hard. Restart after a pass as well, not
                    // just after a failure.
                    needsRecovery.Value <- isMixedPregenMode baseLoadGen.mode
                    true
                else
                    LogInfo "SLA not met at T=%dms; raising lower bound" t
                    needsRecovery.Value <- true
                    false

            let bestPassing = searchMinPassing candidates evaluateCandidate

            match bestPassing with
            | Some t ->
                LogInfo "Minimum sustainable block time: %d ms (fixed TPS %d, image %s)" t fixedTxRate context.image
            | None ->
                failwithf
                    "No block time in [%d, %d] ms satisfied the SLA at TPS %d"
                    context.minBlockTimeMs
                    context.maxBlockTimeMs
                    fixedTxRate)
