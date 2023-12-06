// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanClassicMixed

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP

let sorobanClassicMixed (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  dumpDatabase = false }

    let context =
        { context with
              numAccounts = 2000
              numTxs = 2000
              txRate = 20
              skipLowFeeTxs = true }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSetSize [ coreSet ] 100000

            formation.RunLoadgen coreSet { context.GenerateAccountCreationLoad with accounts = context.numAccounts * 2 }

            // Upgrade to Phase 1 limits
            formation.SetupUpgradeContract coreSet

            formation.DeployUpgradeEntriesAndArm
                coreSet
                (LoadGen.GetSorobanPhase1Upgrade())
                (System.DateTime.UtcNow.AddSeconds(20.0))

            formation.RunLoadgenWithPeer 0 coreSet { context.SetupSorobanInvoke with instances = Some(100) }

            let sorobanLoad =
                async {
                    formation.RunLoadgenWithPeer
                        0
                        coreSet
                        { context.GenerateSorobanInvokeLoad with
                              // Assume 1-2 large TXs per ledger, so each tx can have up to 1/2 ledger limits
                              dataEntriesHigh = Some(20 / 2)
                              ioKiloBytesHigh = Some(64 / 2)
                              txSizeBytesHigh = Some(71_680 / 2)
                              instructionsHigh = Some(100_000_000L / 2L)
                              instances = Some(100)
                              offset = context.numAccounts
                              txrate = context.txRate / 4
                              txs = context.numTxs / 4 // Our txrate is 1/4 of payment rate, so submit 1/4 of txs
                        }
                }

            let classicLoad = async { formation.RunLoadgenWithPeer 1 coreSet context.GeneratePaymentLoad }

            // Generate soroban load from peer 0 and classic load from peer 1 in parallel
            [ sorobanLoad; classicLoad ]
            |> Async.Parallel
            |> Async.RunSynchronously
            |> ignore

            formation.EnsureAllNodesInSync [ coreSet ])
