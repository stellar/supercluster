// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MaxTPSTest

// This module provides a configurable max TPS test to use in missions

open Logging
open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarFormation
open StellarMissionContext
open StellarNetworkData
open StellarStatefulSets
open StellarSupercluster

// Get the maximum value from a distribution. Returns `None` if `distribution`
// is the empty list
let maxDistributionValue (distribution: (int * int) list) =
    match distribution with
    | [] -> None
    | _ -> Some(fst (List.maxBy fst distribution))

// Get the max of two optional values.
let maxOption (x: int option) (y: int option) =
    match x, y with
    | Some x, Some y -> Some(max x y)
    | Some x, None -> Some x
    | None, Some y -> Some y
    | None, None -> None

// Upgrade max tx size to 2x the maximum possible from the distributions in
// `context`
let upgradeSorobanTxLimits (context: MissionContext) (formation: StellarFormation) (coreSetList: CoreSet list) =
    formation.SetupUpgradeContract coreSetList.Head

    let multiplier = 2

    let instructions =
        Option.map (fun x -> (int64 x * int64 multiplier)) (maxDistributionValue context.instructionsDistribution)

    let txBytes =
        Option.map ((*) (multiplier * 1024)) (maxDistributionValue context.totalKiloBytesDistribution)

    let entries =
        Option.map ((*) multiplier) (maxDistributionValue context.dataEntriesDistribution)

    let txSizeBytes =
        Option.map ((*) multiplier) (maxDistributionValue context.txSizeBytesDistribution)

    let wasmBytes = Option.map ((*) multiplier) (maxDistributionValue context.wasmBytesDistribution)

    formation.DeployUpgradeEntriesAndArm
        coreSetList
        { LoadGen.GetDefault() with
              mode = CreateSorobanUpgrade
              txMaxInstructions = instructions
              txMaxReadBytes = txBytes
              txMaxWriteBytes = maxOption txBytes wasmBytes
              txMaxReadLedgerEntries = entries
              txMaxWriteLedgerEntries = entries
              txMaxFootprintSize = entries
              txMaxSizeBytes = maxOption txSizeBytes wasmBytes
              maxContractSizeBytes = Option.map ((*) multiplier) (maxDistributionValue context.wasmBytesDistribution)
              // Memory limit must be reasonably high
              txMemoryLimit = Some 200000000 }
        (System.DateTime.UtcNow)

    let peer = formation.NetworkCfg.GetPeer coreSetList.Head 0

    match instructions with
    | Some instructions -> peer.WaitForTxMaxInstructions instructions
    | None ->
        // Wait a little
        peer.WaitForFewLedgers 3

// Multiplier to use when increasing ledger limits. Expected
// ledger close time (5 seconds) multiplied by some factor to add headroom (2x)
let private limitMultiplier = 5 * 2

let private smallNetworkSize = 10

let upgradeSorobanLedgerLimits
    (context: MissionContext)
    (formation: StellarFormation)
    (coreSetList: CoreSet list)
    (txrate: int)
    =
    formation.SetupUpgradeContract coreSetList.Head

    // Multiply txrate by limitMultiplier to get multiplier for ledger limits
    let multiplier = txrate * limitMultiplier

    let instructions =
        Option.map (fun x -> (int64 x * int64 multiplier)) (maxDistributionValue context.instructionsDistribution)

    let txBytes =
        Option.map ((*) (multiplier * 1024)) (maxDistributionValue context.totalKiloBytesDistribution)

    let entries =
        Option.map ((*) multiplier) (maxDistributionValue context.dataEntriesDistribution)

    let txSizeBytes =
        Option.map ((*) multiplier) (maxDistributionValue context.txSizeBytesDistribution)

    let wasmBytes = Option.map ((*) multiplier) (maxDistributionValue context.wasmBytesDistribution)

    formation.DeployUpgradeEntriesAndArm
        coreSetList
        { LoadGen.GetDefault() with
              mode = CreateSorobanUpgrade
              ledgerMaxInstructions = instructions
              ledgerMaxReadBytes = txBytes
              ledgerMaxWriteBytes = maxOption txBytes wasmBytes
              ledgerMaxTxCount = Some multiplier
              ledgerMaxReadLedgerEntries = entries
              ledgerMaxWriteLedgerEntries = entries
              ledgerMaxTransactionsSizeBytes = maxOption txSizeBytes wasmBytes }
        (System.DateTime.UtcNow)

    let peer = formation.NetworkCfg.GetPeer coreSetList.Head 0
    peer.WaitForLedgerMaxTxCount multiplier


let maxTPSTest (context: MissionContext) (baseLoadGen: LoadGen) (setupCfg: LoadGen option) =
    let allNodes =
        if context.pubnetData.IsSome then
            FullPubnetCoreSets context true false
        else
            StableApproximateTier1CoreSets
                context.image
                (if context.flatQuorum.IsSome then context.flatQuorum.Value else false)

    // PayPregenerated requires node restart between failed iterations to ensure validity of the pregenerated transactions
    // However, large-scale simulation restarts can be slow, so for now only use the new mode on small networks
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

    let sdf =
        List.find (fun (cs: CoreSet) -> cs.name.StringName = "stellar" || cs.name.StringName = "sdf") allNodes

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) allNodes

    // On smaller networks, run loadgen on all nodes to better balance the overhead of load generation
    let loadGenNodes = if List.length allNodes > smallNetworkSize then tier1 else allNodes
    let isLoadGenNode cs = List.exists (fun (cs': CoreSet) -> cs' = cs) loadGenNodes

    // Assign pre-generated transaction information to each load generator node.
    // Specifically, partition all availabe accounts evenly across nodes,
    // and assign appropriate offsets to prevent conflicts.
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

            let upgradeMaxTxSetSize (coreSets: CoreSet list) (rate: int) =
                // Max tx size to avoid overflowing the transaction queue
                let size = rate * limitMultiplier
                formation.UpgradeMaxTxSetSize coreSets size

            let setupCoreSets (coreSets: CoreSet list) =
                // Setup overlay connections first before manually closing
                // ledger, which kick off consensus
                formation.WaitUntilConnected coreSets
                formation.ManualClose coreSets

                // Wait until the whole network is synced before proceeding,
                // to fail asap in case of a misconfiguration
                formation.WaitUntilSynced coreSets
                formation.UpgradeProtocolToLatest coreSets

            let restartCoreSetsOrWait (coreSets: CoreSet list) =
                if baseLoadGen.mode = PayPregenerated then
                    // Stop all nodes in parallel
                    allNodes
                    |> List.map (fun set -> async { formation.Stop set.name })
                    |> Async.Parallel
                    |> Async.RunSynchronously
                    |> ignore

                    // Start all nodes in parallel
                    allNodes
                    |> List.map (fun set -> async { formation.Start set.name })
                    |> Async.Parallel
                    |> Async.RunSynchronously
                    |> ignore
                else
                    System.Threading.Thread.Sleep(5 * 60 * 1000)

            setupCoreSets allNodes

            // Perform setup (if requested)
            match setupCfg with
            | Some cfg ->
                // Run setup on nodes one at a time. Doing too many at once with
                // `RunMultiLoadgen` can cause them to fail.
                for cs in tier1 do
                    formation.RunLoadgen cs { cfg with accounts = numAccounts; minSorobanPercentSuccess = Some 100 }
            | None -> ()

            let getMiddle (low: int) (high: int) = low + (high - low) / 2

            let binarySearchWithThreshold (low: int) (high: int) (threshold: int) =

                let mutable lowerBound = low
                let mutable upperBound = high
                let mutable shouldRestartOrWait = false
                let mutable finalTxRate = None

                while upperBound - lowerBound > threshold do
                    let middle = getMiddle lowerBound upperBound

                    if shouldRestartOrWait then
                        restartCoreSetsOrWait allNodes
                        setupCoreSets allNodes

                    formation.clearMetrics allNodes
                    upgradeMaxTxSetSize allNodes middle

                    if baseLoadGen.mode <> PayPregenerated && baseLoadGen.mode <> GeneratePaymentLoad then
                        upgradeSorobanLedgerLimits context formation allNodes middle
                        upgradeSorobanTxLimits context formation allNodes

                    try
                        LogInfo "Run started at tx rate %i" middle

                        let loadGen =
                            { baseLoadGen with
                                  accounts = numAccounts
                                  // Roughly 15 min of load
                                  txs = middle * 1000
                                  txrate = middle }

                        formation.RunMultiLoadgen loadGenNodes loadGen
                        formation.CheckNoErrorsAndPairwiseConsistency()
                        formation.EnsureAllNodesInSync allNodes

                        // Increase the tx rate
                        lowerBound <- middle
                        finalTxRate <- Some middle
                        LogInfo "Run succeeded at tx rate %i" middle
                        shouldRestartOrWait <- false

                    with e ->
                        LogInfo "Run failed at tx rate %i: %s" middle e.Message
                        upperBound <- middle
                        shouldRestartOrWait <- true

                if finalTxRate.IsSome then
                    LogInfo "Found max tx rate %i" finalTxRate.Value

                    if context.maxTxRate - finalTxRate.Value <= threshold then
                        LogInfo "Tx rate reached the upper bound, increase max-tx-rate for more accurate results"
                else
                    failwith (sprintf "No successful runs at tx rate %i or higher" lowerBound)

                finalTxRate.Value

            // As the runs take a while, set a threshold of 10, so we get a
            // reasonable approximation
            let threshold = 10

            LogInfo "Starting max TPS run"
            let resultRate = binarySearchWithThreshold context.txRate context.maxTxRate threshold

            LogInfo "Final tx rate: %i for image %s" resultRate context.image)
