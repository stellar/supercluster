// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSimulatePubnetTier1Perf

// The point of this mission is to simulate the tier1 group of pubnet
// for purposes of repeatable / comparable performance evaluation.

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarNetworkData
open StellarSupercluster
open StellarCoreHTTP
open Logging

let simulatePubnetTier1Perf (context: MissionContext) =
    let context =
        { context with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(context.installNetworkDelay |> Option.defaultValue true)
              // Start with at least 300 tx/s
              txRate = max context.txRate 400
              // No additional DB overhead unless specified (this will measure the in-memory SQLite DB only)
              simulateApplyDuration = Some(context.simulateApplyDuration |> Option.defaultValue (seq { 0 }))
              simulateApplyWeight = Some(context.simulateApplyWeight |> Option.defaultValue (seq { 100 })) }

    let tier1 = StableApproximateTier1CoreSets context.image
    let sdf = List.find (fun (cs: CoreSet) -> cs.name.StringName = "sdf") tier1

    context.Execute
        tier1
        None
        (fun (formation: StellarFormation) ->
            // Setup overlay connections first before manually closing
            // ledger, which kick off consensus
            formation.WaitUntilConnected tier1
            formation.ManualClose tier1

            // Wait until the whole network is synced before proceeding,
            // to fail asap in case of a misconfiguration
            formation.WaitUntilSynced tier1
            formation.UpgradeProtocolToLatest tier1
            // Set max tx size to 10x the rate -- at 5x we overflow the transaction queue too often.
            formation.UpgradeMaxTxSetSize tier1 (10 * context.maxTxRate)

            formation.RunLoadgen sdf context.GenerateAccountCreationLoad

            let mutable finalTxRate = context.txRate

            try
                for txRate in context.txRate .. (20) .. context.maxTxRate do
                    finalTxRate <- txRate

                    let loadGen =
                        { mode = GeneratePaymentLoad
                          accounts = context.numAccounts
                          // Roughly 15 min of load
                          txs = txRate * 1000
                          spikesize = context.spikeSize
                          spikeinterval = context.spikeInterval
                          txrate = txRate
                          offset = 0
                          batchsize = 100 }

                    formation.RunMultiLoadgen tier1 loadGen
                    formation.EnsureAllNodesInSync tier1

                    // After each iteration, let the mission stabilize a bit before running the next one
                    System.Threading.Thread.Sleep(60000)

            with _ -> LogInfo "Simulation failed at tx rate %i" finalTxRate)
