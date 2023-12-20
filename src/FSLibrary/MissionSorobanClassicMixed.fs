// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanClassicMixed

open StellarCoreSet
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
              numTxs = 10000
              coreResources = SimulatePubnetResources context.networkSizeLimit
              installNetworkDelay = Some(context.installNetworkDelay |> Option.defaultValue true)

              // This spike configuration was derived from some pubnet data.
              // Most ledgers are expected to have roughly 60 * 5 = 300 ops,
              // and 1 in 13 ledgers are expected to have roughly 60 * 5 + 700 = 1000 txs.
              // We expect that a transaction contains 1.65 ops on average.
              // * txRate = (60 op / s) / (1.65 op / tx) = 36 tx / s.
              // * spikeSize = 700 op / (1.65 op / tx) = 424 tx.
              txRate = 36
              spikeSize = 424
              spikeInterval = 65
              skipLowFeeTxs = true }

    let fullCoreSet = FullPubnetCoreSets context true true

    let sdf =
        List.find (fun (cs: CoreSet) -> cs.name.StringName = "stellar" || cs.name.StringName = "sdf") fullCoreSet

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) fullCoreSet

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
            formation.UpgradeProtocolToLatest tier1
            formation.UpgradeMaxTxSetSize tier1 1000000

            formation.RunLoadgen sdf { context.GenerateAccountCreationLoad with accounts = context.numAccounts * 2 }

            // Upgrade to Phase 1 limits
            formation.SetupUpgradeContract sdf

            formation.DeployUpgradeEntriesAndArm
                sdf
                fullCoreSet
                (LoadGen.GetSorobanPhase1Upgrade())
                (System.DateTime.UtcNow.AddSeconds(20.0))

            formation.RunLoadgenWithPeer
                0
                sdf
                { context.SetupSorobanInvoke with
                      instances = Some(100)
                      txrate = 1
                      spikesize = 0
                      spikeinterval = 0 }

            let sorobanLoad =
                async {
                    formation.RunLoadgenWithPeer
                        0
                        sdf
                        { context.GenerateSorobanInvokeLoad with
                              // Assume 1-2 large TXs per ledger, so each tx can have up to 1/2 ledger limits
                              dataEntriesHigh = Some(20 / 2)
                              ioKiloBytesHigh = Some(64 / 2)
                              txSizeBytesHigh = Some(71_680 / 2)
                              instructionsHigh = Some(100_000_000L / 2L)
                              instances = Some(100)
                              offset = context.numAccounts
                              // Soroban is expected to receive less traffic, scale everything down
                              txrate = 6
                              txs = 100
                              spikesize = 50 }

                }

            let classicLoad = async { formation.RunLoadgenWithPeer 1 sdf context.GeneratePaymentLoad }

            // Generate soroban load from peer 0 and classic load from peer 1 in parallel
            [ sorobanLoad; classicLoad ]
            |> Async.Parallel
            |> Async.RunSynchronously
            |> ignore

            formation.EnsureAllNodesInSync fullCoreSet)
