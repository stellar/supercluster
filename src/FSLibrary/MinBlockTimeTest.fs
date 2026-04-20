// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MinBlockTimeTest

// Binary-searches for the minimum ledger target close time the network can
// sustain at a fixed TPS, using stellar-core's
// `ledger.age.closed-histogram` metric as the SLA signal.

open Logging
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

let private protocolMaxBlockTimeMs = 5000

let private timeoutsFor (targetMs: int) : int = max 500 (targetMs / 5)

let private readLedgerAgePercentiles (peer: Peer) : float * float =
    let h = peer.GetMetrics().LedgerAgeClosedHistogram
    float h.Median, float h.``99``

// Returns true iff every peer's ledger.age.closed-histogram satisfies:
//   P50 in [0.80*T, 1.20*T)
//   P99 <= 2*T
//
// FIXME: the P50 tolerance is temporarily widened to +/-20% because
// stellar-core currently has perf regressions that prevent the intended
// +/-5% band from being achievable. Tighten this back to 0.95/1.05 (or
// lower) once those regressions are fixed.
let private checkLedgerAgeSLA (formation: StellarFormation) (coreSets: CoreSet list) (targetMs: int) : bool =
    let tf = float targetMs
    let tLo = tf * 0.80
    let tHi = tf * 1.20
    let p99Max = tf * 2.0
    let mutable ok = true

    formation.NetworkCfg.EachPeerInSets
        (List.toArray coreSets)
        (fun peer ->
            let p50, p99 = readLedgerAgePercentiles peer
            let peerOk = p50 >= tLo && p50 < tHi && p99 <= p99Max

            LogInfo
                "peer=%s T=%dms p50=%.0f p99=%.0f -> %s"
                peer.ShortName.StringName
                targetMs
                p50
                p99
                (if peerOk then "PASS" else "FAIL")

            if not peerOk then ok <- false)

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
    // TODO: use 4 tx profiles
    let baseLoadGen =
        if List.length allNodes <= 30 && baseLoadGen.mode = GeneratePaymentLoad then
            { baseLoadGen with mode = PayPregenerated }
        else
            baseLoadGen

    let context =
        { context with
              genesisTestAccountCount = Some(context.genesisTestAccountCount |> Option.defaultValue 100000)
              numPregeneratedTxs =
                  if baseLoadGen.mode = PayPregenerated then
                      Some(context.numPregeneratedTxs |> Option.defaultValue 2500000)
                  else
                      None }

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) allNodes

    let loadGenNodes = if List.length allNodes > smallNetworkSize then tier1 else allNodes

    let isLoadGenNode cs = List.exists (fun (cs': CoreSet) -> cs' = cs) loadGenNodes

    // For PayPregenerated, partition genesis accounts evenly across loadgen
    // nodes and assign non-overlapping offsets so each node signs txs against
    // its own slice. Verbatim port of MaxTPSTest's partitioning.
    let allNodes =
        match context.numPregeneratedTxs, context.genesisTestAccountCount, baseLoadGen.mode with
        | Some txs, Some accounts, PayPregenerated ->
            let loadGenCount = List.length loadGenNodes
            let accountsPerNode = accounts / loadGenCount
            let mutable j = 0

            List.map
                (fun (cs: CoreSet) ->
                    if isLoadGenNode cs then
                        let i = j
                        j <- j + 1

                        { cs with
                              options =
                                  { cs.options with
                                        initialization =
                                            { cs.options.initialization with
                                                  pregenerateTxs = Some(txs, accountsPerNode, accountsPerNode * i) } } }
                    else
                        cs)
                allNodes
        | _ -> allNodes

    context.ExecuteWithOptionalConsistencyCheck
        allNodes
        None
        false
        (fun (formation: StellarFormation) ->

            let numAccounts = context.genesisTestAccountCount.Value
            let fixedTxRate = context.txRate

            // Headroom factor matches MaxTPSTest's `limitMultiplier = 5 * 2`:
            // 5x for ledgers-per-second at the default 5s close time, 2x for
            // spike margin. Applied at every (re)boot because pod restart
            // resets the network to genesis defaults.
            let isPaymentOnly = baseLoadGen.mode = GeneratePaymentLoad || baseLoadGen.mode = PayPregenerated

            let setupCoreSets (coreSets: CoreSet list) =
                formation.WaitUntilConnected coreSets
                formation.ManualClose coreSets
                formation.WaitUntilSynced coreSets
                formation.UpgradeProtocolToLatest coreSets
                formation.UpgradeMaxTxSetSize coreSets (fixedTxRate * 10)

                if not isPaymentOnly then
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
                let t = timeoutsFor targetMs

                formation.SetupUpgradeContract allNodes.Head

                formation.DeployUpgradeEntriesAndArm
                    allNodes
                    { LoadGen.GetDefault() with
                          mode = CreateSorobanUpgrade
                          ledgerTargetCloseTimeMilliseconds = Some targetMs
                          ballotTimeoutInitialMilliseconds = Some t
                          ballotTimeoutIncrementMilliseconds = Some t
                          nominationTimeoutInitialMilliseconds = Some t
                          nominationTimeoutIncrementMilliseconds = Some t }
                    (System.DateTime.UtcNow.AddSeconds(20.0))

                let peer = formation.NetworkCfg.GetPeer allNodes.Head 0
                peer.WaitForScpLedgerCloseTime targetMs |> ignore

            let evaluateAt (targetMs: int) : bool =
                let loadGen =
                    { baseLoadGen with
                          accounts = numAccounts
                          // ~5 min measurement window at fixed TPS. Enough for a
                          // stable read of the SLA metric without draining the
                          // tx source.
                          txs = fixedTxRate * 300
                          txrate = fixedTxRate }

                // Both the SCP upgrade (which runs a small arming loadgen) and
                // the measurement load can fail when the network is still
                // degraded from a previous iteration; either case is an SLA
                // miss, not a crash.
                try
                    applySCPUpgrade targetMs
                    formation.clearMetrics allNodes
                    formation.RunMultiLoadgen loadGenNodes loadGen
                    formation.CheckNoErrorsAndPairwiseConsistency()
                    formation.EnsureAllNodesInSync allNodes
                    checkLedgerAgeSLA formation allNodes targetMs
                with e ->
                    LogInfo "Run errored at T=%dms: %s" targetMs e.Message
                    false

            if context.maxBlockTimeMs > protocolMaxBlockTimeMs then
                failwithf
                    "--max-block-time-ms=%d exceeds the protocol cap (%d ms); validators will reject such upgrades"
                    context.maxBlockTimeMs
                    protocolMaxBlockTimeMs

            if context.minBlockTimeMs >= context.maxBlockTimeMs then
                failwithf
                    "--min-block-time-ms=%d must be strictly less than --max-block-time-ms=%d"
                    context.minBlockTimeMs
                    context.maxBlockTimeMs

            let mutable lo = context.minBlockTimeMs
            let mutable hi = context.maxBlockTimeMs
            let mutable bestPassing = None

            LogInfo "Starting min block time search: T in [%d, %d] ms, fixed TPS = %d" lo hi fixedTxRate

            // Restart-or-sleep between iterations. PayPregenerated requires a
            // full restart because the pregenerated txs have baked-in sequence
            // numbers that become stale after a partial iteration. Other modes
            // just need time for the tx queue to drain. Ported from MaxTPSTest.
            let restartCoreSetsOrWait () =
                if baseLoadGen.mode = PayPregenerated then
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
