// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSimulatePubnetMixedLoad

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

let simulatePubnetMixedLoad (baseContext: MissionContext) =
    let context =
        { baseContext with
              numAccounts = 30000
              coreResources = SimulatePubnetMixedLoadResources
              // As the goal of `SimulatePubnet` is to simulate a pubnet,
              // network delays are, in general, indispensable.
              // Therefore, unless explicitly told otherwise, we will use
              // network delays.
              installNetworkDelay = Some(baseContext.installNetworkDelay |> Option.defaultValue true)
              enableTailLogging = false
              wasmBytesDistribution = defaultListValue pubnetWasmBytes baseContext.wasmBytesDistribution
              dataEntriesDistribution = defaultListValue pubnetDataEntries baseContext.dataEntriesDistribution
              totalKiloBytesDistribution = defaultListValue pubnetTotalKiloBytes baseContext.totalKiloBytesDistribution

              txSizeBytesDistribution = defaultListValue pubnetTxSizeBytes baseContext.txSizeBytesDistribution
              instructionsDistribution = defaultListValue pubnetInstructions baseContext.instructionsDistribution

              // Unless otherwise specified, cap max connections at 65 for
              // performance.
              maxConnections = Some(baseContext.maxConnections |> Option.defaultValue 65)

              enableBackggroundOverlay = true }

    let fullCoreSet = FullPubnetCoreSets context true false

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) fullCoreSet

    let nonTier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 <> Some true) fullCoreSet

    // Each load generator will generate ~10 TPS, provided there are at least 20
    // non-tier1 nodes
    let numLoadGenerators = 20
    let loadGenerators = List.take (min numLoadGenerators (List.length nonTier1)) nonTier1

    // Transactions per second. ~1000 per ledger. 200 payment TPS and 2 invoke
    // TPS.
    let txrate = 202

    let loadGen =
        { LoadGen.GetDefault() with
              mode = MixedClassicSoroban
              offset = 0
              maxfeerate = None
              skiplowfeetxs = false
              accounts = context.numAccounts

              wasms = context.numWasms
              instances = context.numInstances

              // ~1000 transactions per ledger
              txrate = txrate

              // ~15 minutes of load
              txs = txrate * 60 * 15

              // Blend settings. 99% classic, 1% invoke by default
              payWeight = Some(baseContext.payWeight |> Option.defaultValue pubnetPayWeight)
              sorobanInvokeWeight = Some(baseContext.sorobanInvokeWeight |> Option.defaultValue pubnetInvokeWeight)
              sorobanUploadWeight = Some(baseContext.sorobanUploadWeight |> Option.defaultValue pubnetUploadWeight)

              // Require a majority of Soroban transactions to succeed.
              minSorobanPercentSuccess = Some(baseContext.minSorobanPercentSuccess |> Option.defaultValue 60) }

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
            formation.UpgradeMaxTxSetSize tier1 (txrate * 10)

            formation.RunLoadgen
                loadGenerators.Head
                { context.GenerateAccountCreationLoad with accounts = context.numAccounts }

            upgradeSorobanLedgerLimits context formation tier1 txrate
            upgradeSorobanTxLimits context formation tier1

            for lg in loadGenerators do
                // Run setup on nodes one at a time
                formation.RunLoadgen
                    lg
                    { loadGen with
                          mode = SorobanInvokeSetup
                          txrate = 2
                          minSorobanPercentSuccess = Some 100 }

            formation.RunMultiLoadgen loadGenerators loadGen
            formation.CheckNoErrorsAndPairwiseConsistency()
            formation.EnsureAllNodesInSync fullCoreSet)
