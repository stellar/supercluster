// Copyright 2023 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanInvokeHostLoad

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP
open StellarCorePeer

let sorobanInvokeHostLoad (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  dumpDatabase = false
                  emptyDirType = DiskBackedEmptyDir }

    let context =
        { context with
              numAccounts = 1000
              numTxs = 1000
              txRate = 5
              coreResources = MediumTestResources }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]

            let maxTxSetSize = 10000000
            formation.UpgradeMaxTxSetSize [ coreSet ] maxTxSetSize

            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
            formation.UpgradeSorobanLedgerLimitsWithMultiplier [ coreSet ] 3000
            formation.UpgradeSorobanTxLimitsWithMultiplier [ coreSet ] 100

            // Have a unique instance per TX, assume 6 seconds per ledger for wiggle room
            let txsPerLedger = context.txRate * 6
            let peer = formation.NetworkCfg.GetPeer coreSet 0

            formation.RunLoadgen coreSet { context.SetupSorobanInvoke with instances = Some(txsPerLedger) }

            formation.RunLoadgen
                coreSet
                { context.GenerateSorobanInvokeLoad with
                      instances = Some(txsPerLedger)
                      // Generate TXs up to max resources
                      dataEntriesHigh = Some(peer.GetTxWriteEntries())
                      ioKiloBytesHigh = Some(peer.GetTxWriteBytes() / 1024)
                      txSizeBytesHigh = Some(maxTxSetSize / txsPerLedger)
                      instructionsHigh = Some(peer.GetTxMaxInstructions()) })
