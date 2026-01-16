// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionPubnetNetworkLimitsBench

// The point of this mission is to simulate pubnet as closely as possible under
// a mix of classic and soroban load.

open MaxTPSTest
open PubnetData
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarNetworkData
open StellarSupercluster
open StellarCoreHTTP
open StellarCorePeer

let largeMultiplier = 100
// Note: SLP4-specific tx size limits, update as needed
let SLP_TX_SIZE_LIMIT = 132_096
let PRE_SLP_TX_SIZE_LEDGER_LIMIT = 133_120
let POST_SLP_TX_SIZE_LEDGER_LIMIT = PRE_SLP_TX_SIZE_LEDGER_LIMIT * 2

let upgradeSorobanTxLimits (context: MissionContext) (formation: StellarFormation) (coreSetList: CoreSet list) =
    formation.SetupUpgradeContract coreSetList.Head

    let instructions =
        Option.map (fun x -> (int64 x * int64 largeMultiplier)) (maxDistributionValue context.instructionsDistribution)

    let txBytes =
        Option.map ((*) (largeMultiplier * 1024)) (maxDistributionValue context.totalKiloBytesDistribution)

    let entries =
        Option.map ((*) largeMultiplier) (maxDistributionValue context.dataEntriesDistribution)

    let txSizeBytes = Some(SLP_TX_SIZE_LIMIT)
    let wasmBytes = Some(SLP_TX_SIZE_LIMIT)

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
              txMaxSizeBytes = txSizeBytes
              maxContractSizeBytes =
                  Option.map ((*) largeMultiplier) (maxDistributionValue context.wasmBytesDistribution)
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
let private ledgerSecondsMultiplier = 5 * 2

let upgradeSorobanLedgerLimits
    (context: MissionContext)
    (formation: StellarFormation)
    (coreSetList: CoreSet list)
    (txrate: int)
    =
    formation.SetupUpgradeContract coreSetList.Head

    // Make the multiplier for all limits exception txSize really large to only surge on tx size
    let allOtherLimitsMultiplier = txrate * ledgerSecondsMultiplier * largeMultiplier

    let instructions =
        Option.map
            (fun x -> (int64 x * int64 allOtherLimitsMultiplier))
            (maxDistributionValue context.instructionsDistribution)

    let txBytes =
        Option.map ((*) (allOtherLimitsMultiplier * 1024)) (maxDistributionValue context.totalKiloBytesDistribution)

    let entries =
        Option.map ((*) allOtherLimitsMultiplier) (maxDistributionValue context.dataEntriesDistribution)

    // Note: pass the desired network limit here. Multiplier is not needed in order to enforce the limit to test.
    let txSizeBytes = Some(POST_SLP_TX_SIZE_LEDGER_LIMIT)

    let wasmBytes = txSizeBytes

    formation.DeployUpgradeEntriesAndArm
        coreSetList
        { LoadGen.GetDefault() with
              mode = CreateSorobanUpgrade
              ledgerMaxInstructions = instructions
              ledgerMaxReadBytes = txBytes
              ledgerMaxWriteBytes = maxOption txBytes wasmBytes
              ledgerMaxTxCount = Some allOtherLimitsMultiplier
              ledgerMaxReadLedgerEntries = entries
              ledgerMaxWriteLedgerEntries = entries
              ledgerMaxTransactionsSizeBytes = maxOption txSizeBytes wasmBytes }
        (System.DateTime.UtcNow)

    let peer = formation.NetworkCfg.GetPeer coreSetList.Head 0
    peer.WaitForLedgerMaxTxCount allOtherLimitsMultiplier

let pubnetNetworkLimitsBench (baseContext: MissionContext) =
    let context =
        { baseContext with
              numAccounts = 30000
              coreResources = SimulatePubnetResources
              genesisTestAccountCount = Some 30000
              // As the goal of `SimulatePubnet` is to simulate a pubnet,
              // network delays are, in general, indispensable.
              // Therefore, unless explicitly told otherwise, we will use
              // network delays.
              installNetworkDelay = Some(baseContext.installNetworkDelay |> Option.defaultValue true)
              enableTailLogging = false
              byteCountDistribution = defaultListValue pubnetByteCounts baseContext.byteCountDistribution
              wasmBytesDistribution = defaultListValue pubnetWasmBytes baseContext.wasmBytesDistribution
              dataEntriesDistribution = defaultListValue pubnetDataEntries baseContext.dataEntriesDistribution
              totalKiloBytesDistribution = defaultListValue pubnetTotalKiloBytes baseContext.totalKiloBytesDistribution

              // Note: try different distributions here to benchmark different transactions profiles
              // e.g. [120_000, 1] for large txs only, or
              // [(40_000, 1); (120_000, 1)] for a mix of medium and large txs
              txSizeBytesDistribution =
                  match baseContext.txSizeBytesDistribution with
                  | [] -> failwith "PubnetNetworkLimitsBench mission requires tx size distribution to be set"
                  | _ -> baseContext.txSizeBytesDistribution

              instructionsDistribution = defaultListValue pubnetInstructions baseContext.instructionsDistribution

              // Unless otherwise specified, cap max connections at 65 for
              // performance.
              maxConnections = Some(baseContext.maxConnections |> Option.defaultValue 65)

              updateSorobanCosts = Some(true)

              // Transactions per second. Default is ~200 payment TPS and ~30 invoke load TPS.
              txRate = if baseContext.txRate > 200 then baseContext.txRate else 230

              skipLowFeeTxs = true }

    let fullCoreSet = FullPubnetCoreSets context true false

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) fullCoreSet

    let nonTier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 <> Some true) fullCoreSet

    let numLoadGenerators = 20
    let loadGenerators = List.take (min numLoadGenerators (List.length nonTier1)) nonTier1


    let loadGen =
        { LoadGen.GetDefault() with
              mode = MixedClassicSoroban
              offset = 0
              maxfeerate = None
              skiplowfeetxs = context.skipLowFeeTxs
              accounts = context.numAccounts
              wasms = context.numWasms
              instances = context.numInstances
              txrate = context.txRate

              // ~10 minutes of load
              txs = context.txRate * 60 * 10

              // Weights: 200 TPS and (txRate - 200) TPS
              payWeight = Some(200 * 5)
              sorobanInvokeWeight = Some((context.txRate - 200) * 5)
              sorobanUploadWeight = Some(0)

              // Require all Soroban transactions to succeed.
              minSorobanPercentSuccess = Some(100) }

    context.Execute
        fullCoreSet
        None
        (fun (formation: StellarFormation) ->
            // Setup overlay connections first before manually closing
            // ledger, which kick off consensus
            formation.WaitUntilConnected fullCoreSet
            formation.ManualClose tier1

            // Wait until the whole network is synced before proceeding,
            // to fail asap in case of a misconfiguration
            formation.WaitUntilSynced fullCoreSet

            // Setup
            formation.UpgradeProtocolToLatest tier1
            formation.UpgradeMaxTxSetSize tier1 (context.txRate * 10)

            upgradeSorobanLedgerLimits context formation tier1 context.txRate
            upgradeSorobanTxLimits context formation tier1

            for lg in loadGenerators do
                // Run setup on nodes one at a time
                formation.RunLoadgen lg context.SetupSorobanInvoke

            formation.RunMultiLoadgen loadGenerators loadGen
            formation.EnsureAllNodesInSync fullCoreSet)
