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
              numTxs = 100
              txRate = 12
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
    // 3. Run loadgen to test the new settings
    // 4. Decrease ledgerMaxDependentTxClusters to a lower value
    // 5. Run loadgen again
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

            let peer = formation.NetworkCfg.GetPeer coreSet 0
            peer.WaitForMaxDependentTxClusters 8

            formation.RunLoadgen coreSet context.SetupSorobanInvoke
            formation.RunLoadgen coreSet context.GenerateSorobanInvokeLoad

            formation.SetupUpgradeContract coreSet

            formation.DeployUpgradeEntriesAndArm
                [ coreSet ]
                { LoadGen.GetDefault() with
                      mode = CreateSorobanUpgrade
                      ledgerMaxDependentTxClusters = Some(2) }
                (System.DateTime.UtcNow.AddSeconds(20.0))

            peer.WaitForMaxDependentTxClusters 2

            formation.RunLoadgen coreSet context.SetupSorobanInvoke
            formation.RunLoadgen coreSet context.GenerateSorobanInvokeLoad)
