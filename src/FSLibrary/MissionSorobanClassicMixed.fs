// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanClassicMixed

open StellarCoreSet
open StellarCorePeer
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarNetworkData
open StellarCoreHTTP

let sorobanClassicMixed (context: MissionContext) =

    let context =
        { context with
              numAccounts = 2000
              numTxs = 2000
              txRate = 20
              skipLowFeeTxs = true }

    let sorobanCoreSet =
        MakeLiveCoreSet
            "soroban"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  dumpDatabase = false }

    let classicCoreSet =
        MakeLiveCoreSet
            "classic"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  dumpDatabase = false }

    let allNodes = [ sorobanCoreSet; classicCoreSet ]

    context.Execute
        allNodes
        None
        (fun (formation: StellarFormation) ->
            // Wait until the whole network is synced before proceeding,
            // to fail asap in case of a misconfiguration
            formation.WaitUntilSynced allNodes
            formation.UpgradeProtocolToLatest allNodes
            formation.UpgradeMaxTxSetSize allNodes 1000000

            formation.RunLoadgen
                sorobanCoreSet
                { context.GenerateAccountCreationLoad with accounts = context.numAccounts * 2 }

            // Upgrade to Phase 1 limits
            formation.SetupUpgradeContract sorobanCoreSet

            let limits = LoadGen.GetSorobanPhase1Upgrade()
            formation.DeployUpgradeEntriesAndArm allNodes limits (System.DateTime.UtcNow)

            // Wait until upgrade has been applied
            let peer = formation.NetworkCfg.GetPeer sorobanCoreSet 0
            peer.WaitForMaxTxSize limits.txMaxSizeBytes.Value

            formation.RunLoadgen sorobanCoreSet { context.SetupSorobanInvoke with instances = Some(10); txrate = 1 }

            let sorobanLoad =
                { context.GenerateSorobanInvokeLoad with
                      // Assume 1-2 large TXs per ledger, so each tx can have up to 1/2 ledger limits
                      dataEntriesHigh = Some(limits.txMaxWriteLedgerEntries.Value / 2)
                      ioKiloBytesHigh = Some((limits.txMaxWriteBytes.Value / 1024) / 2)
                      txSizeBytesHigh = Some(limits.txMaxSizeBytes.Value / 2)
                      instructionsHigh = Some(limits.txMaxInstructions.Value / 2L)
                      instances = Some(10)
                      offset = context.numAccounts
                      // Soroban is expected to receive less traffic, scale everything down
                      txrate = context.txRate / 10
                      txs = context.numTxs / 10 }


            formation.RunMultiLoadgen allNodes [ sorobanLoad; context.GeneratePaymentLoad ]
            formation.EnsureAllNodesInSync allNodes)
