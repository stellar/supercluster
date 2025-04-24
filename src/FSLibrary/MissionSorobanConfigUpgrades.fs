// Copyright 2023 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanConfigUpgrades

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP
open StellarCorePeer
open StellarDataDump

let LastVersionBeforeSoroban = 19

let sorobanConfigUpgrades (context: MissionContext) =

    let quorumSet = CoreSetQuorum(CoreSetName("core"))

    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecksAndEvents
                  emptyDirType = DiskBackedEmptyDir
                  quorumSet = quorumSet
                  updateSorobanCosts = Some(true)
                  nodeCount = 5 }

    let context =
        { context.WithSmallLoadgenOptions with
              numAccounts = 100
              numTxs = 100
              txRate = 1
              coreResources = MediumTestResources }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            let peer = formation.NetworkCfg.GetPeer coreSet 0

            // Upgrade to protocol before Soroban
            let latestVersion = peer.GetSupportedProtocolVersion()
            formation.UpgradeProtocol [ coreSet ] LastVersionBeforeSoroban
            formation.UpgradeMaxTxSetSize [ coreSet ] 100000
            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad

            // Upgrade to latest protocol
            formation.UpgradeProtocolToLatest [ coreSet ]

            // will wait until loadgen is done and contract is uploaded
            formation.UpgradeSorobanLedgerLimitsWithMultiplier [ coreSet ] 100

            // kill-switch
            formation.UpgradeSorobanMaxTxSetSize [ coreSet ] 0

            try
                formation.RunLoadgen coreSet context.GenerateSorobanUploadLoad
            with _ -> ()

            if peer.IsLoadGenComplete() <> Failure then
                failwith "Loadgen should have failed with max tx set size 0!"

            formation.clearMetrics [ coreSet ]

            formation.UpgradeSorobanMaxTxSetSize [ coreSet ] 100
            formation.RunLoadgen coreSet context.GenerateSorobanUploadLoad

            // Slightly increase the limit while generating load
            formation.DeployUpgradeEntriesAndArm
                [ coreSet ]
                { LoadGen.GetDefault() with
                      mode = CreateSorobanUpgrade
                      txMaxSizeBytes = Some(150000)
                      ledgerMaxTransactionsSizeBytes = Some(150000 * 100)
                      maxContractSizeBytes = Some(100000)
                      txMaxWriteBytes = Some(150000 * 2)
                      ledgerMaxWriteBytes = Some(150000 * 2 * 100) }
                (System.DateTime.UtcNow.AddSeconds(20.0))

            formation.RunLoadgen coreSet context.GenerateSorobanUploadLoad
            peer.WaitForMaxTxSize 150000

            // Further increase the limit while generating load
            formation.DeployUpgradeEntriesAndArm
                [ coreSet ]
                { LoadGen.GetDefault() with
                      mode = CreateSorobanUpgrade
                      txMaxSizeBytes = Some(500000)
                      ledgerMaxTransactionsSizeBytes = Some(500000 * 100)
                      maxContractSizeBytes = Some(400000)
                      txMaxWriteBytes = Some(500000 * 2)
                      ledgerMaxWriteBytes = Some(500000 * 2 * 100) }
                (System.DateTime.UtcNow.AddSeconds(20.0))

            formation.RunLoadgen coreSet context.GenerateSorobanUploadLoad
            peer.WaitForMaxTxSize 500000

            // Decrease max tx size to be below classic limit, everything should still work as expected
            formation.DeployUpgradeEntriesAndArm
                [ coreSet ]
                { LoadGen.GetDefault() with
                      mode = CreateSorobanUpgrade
                      txMaxSizeBytes = Some(50000)
                      maxContractSizeBytes = Some(10000)
                      txMaxWriteBytes = Some(50000 * 2) }
                (System.DateTime.UtcNow.AddSeconds(20.0))

            let peer = formation.NetworkCfg.GetPeer coreSet 0
            let startingTxCount = peer.GetMetrics().LedgerTransactionApply.Count

            // There is a race condition where previously valid TXs in the TX queue may become invalid
            // once the tx size limit is reduced. Allow 5% of failures to account for this race.
            formation.RunLoadgen coreSet { context.GenerateSorobanUploadLoad with skiplowfeetxs = true }
            let finalTxCount = peer.GetMetrics().LedgerTransactionApply.Count

            if (float (finalTxCount - startingTxCount)) / (float context.numTxs) < 0.95 then
                failwith "Loadgen failed!"

            peer.WaitForMaxTxSize 50000)
