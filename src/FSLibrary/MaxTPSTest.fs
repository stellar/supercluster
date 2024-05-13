// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MaxTPSTest

// This module provides a configurable max TPS test to use in missions

open Logging
open StellarCoreHTTP
open StellarCoreSet
open StellarFormation
open StellarMissionContext
open StellarNetworkData
open StellarStatefulSets
open StellarSupercluster

let maxTPSTest
    (context: MissionContext)
    (baseLoadGen: LoadGen)
    (setupCfg: LoadGen option)
    (increaseSorobanLimits: bool)
    =
    let allNodes =
        if context.pubnetData.IsSome then
            FullPubnetCoreSets context true false
        else
            StableApproximateTier1CoreSets
                context.image
                (if context.flatQuorum.IsSome then context.flatQuorum.Value else false)

    let sdf =
        List.find (fun (cs: CoreSet) -> cs.name.StringName = "stellar" || cs.name.StringName = "sdf") allNodes

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) allNodes

    context.ExecuteWithOptionalConsistencyCheck
        allNodes
        None
        false
        (fun (formation: StellarFormation) ->

            let numAccounts = 30000

            let upgradeMaxTxSetSize (coreSets: CoreSet list) (rate: int) =
                // Set max tx size to 10x the rate -- at 5x we overflow the transaction queue too often.
                formation.UpgradeMaxTxSetSize coreSets (10 * rate)

            // Setup overlay connections first before manually closing
            // ledger, which kick off consensus
            formation.WaitUntilConnected allNodes
            formation.ManualClose allNodes

            // Wait until the whole network is synced before proceeding,
            // to fail asap in case of a misconfiguration
            formation.WaitUntilSynced allNodes
            formation.UpgradeProtocolToLatest allNodes
            upgradeMaxTxSetSize allNodes 10000
            formation.RunLoadgen sdf { context.GenerateAccountCreationLoad with accounts = numAccounts }

            // Upgrade limits (if requested)
            if increaseSorobanLimits then
                formation.UpgradeSorobanLedgerLimitsWithMultiplier allNodes 100000
                formation.UpgradeSorobanTxLimitsWithMultiplier allNodes 1000

            // Perform setup (if requested)
            match setupCfg with
            | Some cfg -> formation.RunLoadgen sdf cfg
            | None -> ()

            let wait () = System.Threading.Thread.Sleep(5 * 60 * 1000)

            let getMiddle (low: int) (high: int) = low + (high - low) / 2

            let binarySearchWithThreshold (low: int) (high: int) (threshold: int) =

                let mutable lowerBound = low
                let mutable upperBound = high
                let mutable shouldWait = false
                let mutable finalTxRate = None

                while upperBound - lowerBound > threshold do
                    let middle = getMiddle lowerBound upperBound

                    if shouldWait then wait ()

                    formation.clearMetrics allNodes
                    upgradeMaxTxSetSize allNodes middle

                    try
                        LogInfo "Run started at tx rate %i" middle

                        let loadGen =
                            { baseLoadGen with
                                  accounts = numAccounts
                                  txs = middle * 1000
                                  txrate = middle }

                        formation.RunMultiLoadgen tier1 loadGen
                        formation.CheckNoErrorsAndPairwiseConsistency()
                        formation.EnsureAllNodesInSync allNodes

                        // Increase the tx rate
                        lowerBound <- middle
                        finalTxRate <- Some middle
                        LogInfo "Run succeeded at tx rate %i" middle
                        shouldWait <- false

                    with _ ->
                        LogInfo "Run failed at tx rate %i" middle
                        upperBound <- middle
                        shouldWait <- true

                if finalTxRate.IsSome then
                    LogInfo "Found max tx rate %i" finalTxRate.Value

                    if context.maxTxRate - finalTxRate.Value <= threshold then
                        LogInfo "Tx rate reached the upper bound, increase max-tx-rate for more accurate results"
                else
                    failwith (sprintf "No successful runs at tx rate %i or higher" lowerBound)

                finalTxRate.Value

            let mutable results = []
            // As the runs take a while, set a threshold of 10, so we get a
            // reasonable approximation
            let threshold = 10
            let numRuns = if context.numRuns.IsSome then context.numRuns.Value else 3

            for run in 1 .. numRuns do
                LogInfo "Starting max TPS run %i out of %i" run numRuns
                let resultRate = binarySearchWithThreshold context.txRate context.maxTxRate threshold
                results <- List.append results [ resultRate ]
                if run < numRuns then wait ()

            LogInfo
                "Final tx rate averaged to %i over %i runs for image %s"
                (results |> List.map float |> List.average |> int)
                numRuns
                context.image)
