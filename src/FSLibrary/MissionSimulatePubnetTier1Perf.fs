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

    let allNodes =
        if context.pubnetData.IsSome then
            FullPubnetCoreSets context true false
        else
            StableApproximateTier1CoreSets
                context.image
                (if context.flatQuorum.IsSome then context.flatQuorum.Value else false)

    let tier1 =
        allNodes
        |> List.filter (fun (cs: CoreSet) -> List.contains cs.name.StringName tier1OrgNames)

    let sdf = List.find (fun (cs: CoreSet) -> cs.name.StringName = "sdf") tier1

    context.ExecuteWithOptionalConsistencyCheck
        allNodes
        None
        false
        (fun (formation: StellarFormation) ->

            let numAccounts = 10000

            let setupCoreSets (coreSets: CoreSet list) (rate: int) =
                // Setup overlay connections first before manually closing
                // ledger, which kick off consensus
                formation.WaitUntilConnected coreSets
                formation.ManualClose coreSets

                // Wait until the whole network is synced before proceeding,
                // to fail asap in case of a misconfiguration
                formation.WaitUntilSynced coreSets
                formation.UpgradeProtocolToLatest coreSets
                // Set max tx size to 10x the rate -- at 5x we overflow the transaction queue too often.
                formation.UpgradeMaxTxSetSize coreSets (10 * rate)
                formation.RunLoadgen sdf { context.GenerateAccountCreationLoad with accounts = numAccounts }

            let restartCoreSets (coreSets: CoreSet list) =
                for set in allNodes do
                    formation.Stop set.name

                for set in allNodes do
                    formation.Start set.name

            let getMiddle (low: int) (high: int) = low + (high - low) / 2

            let binarySearchWithThreshold (low: int) (high: int) (threshold: int) =

                let mutable lowerBound = low
                let mutable upperBound = high
                let mutable shouldRestart = false
                let mutable finalTxRate = None

                setupCoreSets allNodes (getMiddle lowerBound upperBound)

                while upperBound - lowerBound > threshold do
                    let middle = getMiddle lowerBound upperBound

                    if shouldRestart then
                        // Stop and start the network, to make sure next iteration of the test is not impacted by previous
                        restartCoreSets allNodes
                        setupCoreSets allNodes middle

                    formation.clearMetrics allNodes

                    try
                        LogInfo "Run started at tx rate %i" middle

                        let loadGen =
                            { mode = GeneratePaymentLoad
                              accounts = numAccounts
                              // Roughly 15 min of load
                              txs = middle * 1000
                              spikesize = context.spikeSize
                              spikeinterval = context.spikeInterval
                              txrate = middle
                              offset = 0
                              batchsize = 100 }

                        formation.RunMultiLoadgen tier1 loadGen
                        formation.CheckNoErrorsAndPairwiseConsistency()
                        formation.EnsureAllNodesInSync allNodes

                        // Increase the tx rate
                        lowerBound <- middle
                        finalTxRate <- Some middle
                        LogInfo "Run succeeded at tx rate %i" middle
                        shouldRestart <- false

                    with _ ->
                        LogInfo "Run failed at tx rate %i" middle
                        upperBound <- middle
                        shouldRestart <- true

                if finalTxRate.IsSome then
                    LogInfo "Found max tx rate %i" finalTxRate.Value

                    if context.maxTxRate - finalTxRate.Value <= threshold then
                        LogInfo "Tx rate reached the upper bound, increase max-tx-rate for more accurate results"
                else
                    failwith (sprintf "No successful runs at tx rate %i or higher" lowerBound)

                finalTxRate.Value

            let mutable results = []
            // As the runs take a while, set a threshold of 10, so we get a reasonbale approximation
            let threshold = 10
            let numRuns = 3

            for run in 1 .. numRuns do
                LogInfo "Starting max TPS run %i" run
                let resultRate = binarySearchWithThreshold context.txRate context.maxTxRate threshold
                results <- List.append results [ resultRate ]
                if run < numRuns then restartCoreSets allNodes

            LogInfo
                "Final tx rate averaged to %i over %i runs for image %s"
                (results |> List.map float |> List.average |> int)
                numRuns
                context.image)
