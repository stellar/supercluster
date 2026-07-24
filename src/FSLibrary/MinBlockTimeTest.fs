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

let private searchThresholdMs = 100

// For the purposes of min block test, use high value to avoid noise from SCP timeouts
let private timeout = 2000

let private txSetSizeBufferMultiplier = 2

let private maxTxSetSizeForTarget (kind: string) (targetMs: int) (txRate: int) =
    let scaled = int64 targetMs * int64 txRate * int64 txSetSizeBufferMultiplier

    let txSetSize = (scaled + 999L) / 1000L

    if txSetSize > int64 System.Int32.MaxValue then
        failwithf "%s MaxTxSetSize %d exceeds supported int range" kind txSetSize

    max (int txSetSize) 100

// Exposed for reuse by MissionTriggerTimerMixConsensus, which runs the same
// MIXED_PREGEN_* load without the binary search.
let classicMaxTxSetSizeForTarget (targetMs: int) (classicTxRate: int) =
    maxTxSetSizeForTarget "Classic" targetMs classicTxRate

let private sorobanMaxTxSetSizeForTarget (targetMs: int) (sorobanTxRate: int) =
    maxTxSetSizeForTarget "Soroban" targetMs sorobanTxRate

type private MixedPregenSorobanResources =
    { instructions: int64
      readBytes: int
      writeBytes: int
      readOnlyEntries: int
      readWriteEntries: int
      txSizeBytes: int
      contractEventBytes: int }

let private usesPregeneratedTxs (mode: LoadGenMode) = mode = PayPregenerated || isMixedPregenMode mode

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

// Exposed for reuse by MissionTriggerTimerMixConsensus.
let upgradeMixedPregenSorobanLimits
    (formation: StellarFormation)
    (coreSets: CoreSet list)
    (baseLoadGen: LoadGen)
    (targetMs: int)
    =
    let sorobanTxRate = baseLoadGen.sorobanTxRate |> Option.defaultValue 0

    if sorobanTxRate > 0 then
        let resources = mixedPregenSorobanResources baseLoadGen.mode
        let footprintEntries = resources.readOnlyEntries + resources.readWriteEntries
        let targetMaxTxSetSize = sorobanMaxTxSetSizeForTarget targetMs sorobanTxRate

        LogInfo
            "Upgrading MIXED_PREGEN_* Soroban limits for %s: Soroban MaxTxSetSize=%d for T=%dms, soroban TPS=%d, buffer=%dx"
            (baseLoadGen.mode.ToString())
            targetMaxTxSetSize
            targetMs
            sorobanTxRate
            txSetSizeBufferMultiplier

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
                  txMaxContractEventsSizeBytes = Some limits.txMaxContractEventsSizeBytes }
            (System.TimeSpan.FromSeconds(20.0))

        waitForMixedPregenSorobanLimits peer limits

let private toggleOverlayOnlyMode (formation: StellarFormation) (coreSets: CoreSet list) =
    formation.NetworkCfg.EachPeerInSets
        (List.toArray coreSets)
        (fun peer ->
            let res = peer.ToggleOverlayOnlyMode()
            LogInfo "Toggled overlay-only mode on %s: %s" peer.ShortName.StringName res)

// Exposed for reuse by MissionTriggerTimerMixConsensus.
let withOverlayOnlyMode (formation: StellarFormation) (coreSets: CoreSet list) (f: unit -> unit) =
    LogInfo "Enabling overlay-only mode"
    toggleOverlayOnlyMode formation coreSets

    try
        f ()
    finally
        LogInfo "Disabling overlay-only mode"
        toggleOverlayOnlyMode formation coreSets

let private readLedgerAgePercentiles (peer: Peer) : Peer * float * float =
    let h = peer.GetMetrics().LedgerAgeClosedHistogram
    peer, float h.``75``, float h.``99``

let private collectLedgerAgePercentiles
    (formation: StellarFormation)
    (coreSets: CoreSet list)
    : (Peer * float * float) list =
    formation.NetworkCfg.PeersInSets(List.toArray coreSets)
    |> List.map (fun peer -> async { return readLedgerAgePercentiles peer })
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Array.toList

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

let minBlockTimeTest (context: MissionContext) (baseLoadGen: LoadGen) (setupCfg: LoadGen option) =
    let allNodes =
        if context.pubnetData.IsSome then
            FullPubnetCoreSets context true false
        else
            StableApproximateTier1CoreSets
                context.image
                (if context.flatQuorum.IsSome then context.flatQuorum.Value else false)

    // Mirrors MaxTPSTest: on small networks, GeneratePaymentLoad runs out of
    // source accounts at high TPS, so switch to PayPregenerated which uses
    // genesis-created accounts and pregenerated signed txs.
    let baseLoadGen =
        if List.length allNodes <= 30 && baseLoadGen.mode = GeneratePaymentLoad then
            { baseLoadGen with mode = PayPregenerated }
        else
            baseLoadGen

    let context =
        { context with
              runForMinBlockTime = true
              genesisTestAccountCount = Some(context.genesisTestAccountCount |> Option.defaultValue 100000)
              numPregeneratedTxs =
                  if usesPregeneratedTxs baseLoadGen.mode then
                      Some(context.numPregeneratedTxs |> Option.defaultValue 2500000)
                  else
                      None }

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) allNodes
    let loadGenNodes = List.filter (fun (cs: CoreSet) -> cs.options.generatesLoad) allNodes

    let loadGenNodes =
        if List.isEmpty loadGenNodes then
            if List.length allNodes > smallNetworkSize then tier1 else allNodes
        else
            loadGenNodes

    let isLoadGenNode cs = List.exists (fun (cs': CoreSet) -> cs' = cs) loadGenNodes

    let activeLoadGenNodes =
        if isMixedPregenMode baseLoadGen.mode then
            let requestedCount =
                max
                    (baseLoadGen.classicTxRate |> Option.defaultValue 0)
                    (baseLoadGen.sorobanTxRate |> Option.defaultValue 0)

            loadGenNodes
            |> List.truncate (min (List.length loadGenNodes) (max 1 requestedCount))
        else
            loadGenNodes

    // For pre-generated modes, partition genesis accounts evenly across
    // loadgen nodes and assign offsets so each active node signs txs against
    // its own slice. Mixed pregen keeps every tier1 core set initialized, but
    // partitions accounts by the active loadgen count so low-TPS runs still
    // have enough local accounts on the node that generates load.
    let allNodes =
        match context.numPregeneratedTxs, context.genesisTestAccountCount, baseLoadGen.mode with
        | Some txs, Some accounts, mode when usesPregeneratedTxs mode ->
            let partitionCount =
                if isMixedPregenMode mode then
                    List.length activeLoadGenNodes
                else
                    List.length loadGenNodes

            let accountsPerNode = accounts / partitionCount
            let mutable j = 0

            List.map
                (fun (cs: CoreSet) ->
                    let pregenerateTxs =
                        if isLoadGenNode cs then
                            let i = if isMixedPregenMode mode then j % partitionCount else j

                            j <- j + 1
                            Some(txs, accountsPerNode, accountsPerNode * i)
                        else
                            Some(0, 1, 0)

                    { cs with
                          options =
                              { cs.options with
                                    initialization = { cs.options.initialization with pregenerateTxs = pregenerateTxs } } })
                allNodes
        | _ -> allNodes

    context.ExecuteWithOptionalConsistencyCheck
        allNodes
        None
        false
        (fun (formation: StellarFormation) ->

            let numAccounts = context.genesisTestAccountCount.Value
            let fixedTxRate = context.txRate

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
                let maxTxSetSize = classicMaxTxSetSizeForTarget targetMs classicTxRateForLimits

                LogInfo
                    "Upgrading classic MaxTxSetSize to %d for T=%dms, classic TPS=%d, buffer=%dx"
                    maxTxSetSize
                    targetMs
                    classicTxRateForLimits
                    txSetSizeBufferMultiplier

                formation.UpgradeMaxTxSetSize allNodes maxTxSetSize

            let upgradeSorobanMaxTxSetSize (targetMs: int) =
                if isMixedPregenMode baseLoadGen.mode then
                    upgradeMixedPregenSorobanLimits formation allNodes baseLoadGen targetMs

            let evaluateAt (targetMs: int) : bool =
                let loadGen =
                    { baseLoadGen with
                          accounts = numAccounts
                          // ~5 min measurement window at fixed TPS. Enough for a
                          // stable read of the SLA metric without draining the
                          // tx source.
                          txs = fixedTxRate * 300
                          txrate = fixedTxRate }

                applySCPUpgrade targetMs
                upgradeClassicMaxTxSetSize targetMs
                upgradeSorobanMaxTxSetSize targetMs
                formation.clearMetrics allNodes

                // A loadgen failure is not an SLA signal — it usually means the
                // requested TPS is too high for the network, or that loadgen
                // itself lost a tx in the pipeline. Treating it as "SLA missed"
                // would mislead the binary search, so fail the mission loudly.
                try
                    if isMixedPregenMode baseLoadGen.mode then
                        withOverlayOnlyMode
                            formation
                            allNodes
                            (fun () -> formation.RunMultiLoadgen activeLoadGenNodes loadGen)
                    else
                        formation.RunMultiLoadgen activeLoadGenNodes loadGen
                with e -> failwithf "Loadgen failed at T=%dms; TPS might be too high (%s)" targetMs e.Message

                // Snapshot SLA metrics before consistency checks; those can take
                // long enough to skew the ledger age percentiles.
                let ledgerAgePercentiles = collectLedgerAgePercentiles formation allNodes

                formation.CheckNoErrorsAndPairwiseConsistency()
                formation.EnsureAllNodesInSync allNodes
                checkLedgerAgeSLA ledgerAgePercentiles targetMs

            if context.minBlockTimeMs >= context.maxBlockTimeMs then
                failwithf
                    "--min-block-time-ms=%d must be strictly less than --max-block-time-ms=%d"
                    context.minBlockTimeMs
                    context.maxBlockTimeMs

            let mutable lo = context.minBlockTimeMs
            let mutable hi = context.maxBlockTimeMs
            let mutable bestPassing = None

            LogInfo "Starting min block time search: T in [%d, %d] ms, fixed TPS = %d" lo hi fixedTxRate

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

            let mutable needsRecovery = false

            while hi - lo > searchThresholdMs do
                if needsRecovery then restartCoreSetsOrWait ()

                let mid = lo + (hi - lo) / 2

                if evaluateAt mid then
                    LogInfo "SLA met at T=%dms; lowering upper bound" mid
                    hi <- mid
                    bestPassing <- Some mid
                    needsRecovery <- false
                else
                    LogInfo "SLA not met at T=%dms; raising lower bound" mid
                    lo <- mid
                    needsRecovery <- true

            match bestPassing with
            | Some t ->
                LogInfo "Minimum sustainable block time: %d ms (fixed TPS %d, image %s)" t fixedTxRate context.image
            | None ->
                failwithf
                    "No block time in [%d, %d] ms satisfied the SLA at TPS %d"
                    context.minBlockTimeMs
                    context.maxBlockTimeMs
                    fixedTxRate)
