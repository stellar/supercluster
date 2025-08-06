// Copyright 2025 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionUpgradeTxClusters

open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP
open StellarNetworkData
open Logging

let upgradeTxClusters (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecksAndEvents
                  emptyDirType = DiskBackedEmptyDir
                  updateSorobanCosts = Some(true) }

    let context =
        { context with
              numAccounts = 100
              numTxs = 1500
              txRate = 15
              coreResources = MediumTestResources
              genesisTestAccountCount = Some 100
              wasmBytesDistribution = [ (3000, 1) ]
              dataEntriesDistribution = [ (0, 1) ]
              totalKiloBytesDistribution = [ (1, 1) ]
              txSizeBytesDistribution = [ (512, 1) ]
              instructionsDistribution = [ (5000000, 1) ] }

    // This tests upgrading ledgerMaxDependentTxClusters:
    // 1. Start with default settings
    // 2. Increase ledgerMaxDependentTxClusters to a higher value
    // 3. Run soroban_invoke loadgen. This should last until the end of the test.
    // 4. Decrease ledgerMaxDependentTxClusters to a lower value using a different core node
    // 5. Wait for step 3 to complete.
    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSetSize [ coreSet ] 100000

            formation.UpgradeSorobanLedgerLimitsWithMultiplier [ coreSet ] 1000
            formation.UpgradeSorobanTxLimitsWithMultiplier [ coreSet ] 100

            formation.DeployUpgradeEntriesAndArm
                [ coreSet ]
                { LoadGen.GetDefault() with
                      mode = CreateSorobanUpgrade
                      ledgerMaxInstructions = Some(50000000) // 10 transactions per thread
                      txMaxInstructions = Some(50000000)
                      ledgerMaxDependentTxClusters = Some(8) }
                (System.DateTime.UtcNow.AddSeconds(20.0))

            let peer0 = formation.NetworkCfg.GetPeer coreSet 0
            peer0.WaitForMaxDependentTxClusters 8

            // Don't use peer 0. This will let us run soroban_invoke on peer 1
            // and then do the settings upgrade on peer 0.
            let peer1 = formation.NetworkCfg.GetPeer coreSet 1
            LogInfo "Loadgen: %s" (peer1.GenerateLoad context.SetupSorobanInvoke)
            peer1.WaitForLoadGenComplete context.SetupSorobanInvoke
            // 1500 txs at a rate of 15 txs/sec should take ~100 seconds, so we'll have enough time to do both settings upgrades
            LogInfo "Loadgen: %s" (peer1.GenerateLoad context.GenerateSorobanInvokeLoad)

            formation.SetupUpgradeContract coreSet

            formation.DeployUpgradeEntriesAndArm
                [ coreSet ]
                { LoadGen.GetDefault() with
                      mode = CreateSorobanUpgrade
                      ledgerMaxDependentTxClusters = Some(4) }
                (System.DateTime.UtcNow.AddSeconds(20.0))

            peer0.WaitForMaxDependentTxClusters 4

            peer1.WaitForLoadGenComplete context.GenerateSorobanInvokeLoad)
