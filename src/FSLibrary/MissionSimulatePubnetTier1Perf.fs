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
            let mutable finalTxRate = None
            let mutable lowerBound = max context.txRate 400
            let mutable upperBound = context.maxTxRate
            // Ad the runs take a while, set a threshold of 10, so we get a reasonbale approximation
            let threshold = 10

            formation.UpgradeMaxTxSetSize tier1 (10 * upperBound)

            formation.RunLoadgen sdf context.GenerateAccountCreationLoad

            while upperBound - lowerBound > threshold do
                let middle = lowerBound + (upperBound - lowerBound) / 2

                try
                    LogInfo "Run started at tx rate %i" middle

                    let loadGen =
                        { mode = GeneratePaymentLoad
                          accounts = context.numAccounts
                          // Roughly 15 min of load
                          txs = middle * 1000
                          spikesize = context.spikeSize
                          spikeinterval = context.spikeInterval
                          txrate = middle
                          offset = 0
                          batchsize = 100 }

                    formation.RunMultiLoadgen tier1 loadGen
                    formation.EnsureAllNodesInSync tier1

                    // Increase the tx rate
                    lowerBound <- middle
                    finalTxRate <- Some middle
                    LogInfo "Run succeeded at tx rate %i" middle

                with _ ->
                    LogInfo "Run failed at tx rate %i" middle
                    upperBound <- middle
                    // After each failed iteration, let the mission stabilize a bit before running the next one
                    System.Threading.Thread.Sleep(120000)

                formation.clearMetrics tier1

            if finalTxRate.IsSome then
                LogInfo "Final tx rate %i" finalTxRate.Value
            else
                failwith (sprintf "No successful runs at tx rate %i or higher" lowerBound))
