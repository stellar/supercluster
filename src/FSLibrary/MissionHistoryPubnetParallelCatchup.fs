// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetParallelCatchup

open Logging
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open StellarJobExec
open StellarSupercluster
open MissionCatchupHelpers

let historyPubnetParallelCatchup (context: MissionContext) =
    let context = { context with coreResources = ParallelCatchupResources }

    let checkpointsPerJob = 250
    let ledgersPerCheckpoint = 64
    let latestLedgerNum = GetLatestPubnetLedgerNumber()
    let ledgersPerJob = checkpointsPerJob * ledgersPerCheckpoint
    let startingLedger = max context.pubnetParallelCatchupStartingLedger 0
    let parallelism = 128
    let overlapCheckpoints = 5
    let overlapLedgers = overlapCheckpoints * ledgersPerCheckpoint

    let jobArr = getCatchupRanges ledgersPerJob startingLedger latestLedgerNum overlapLedgers

    LogInfo
        "Running %d jobs (%d-way parallel) of %d checkpoints each, to catch up to ledger %d starting from %d"
        jobArr.Length
        parallelism
        checkpointsPerJob
        latestLedgerNum
        startingLedger

    LogInfo "Printing catchup ranges"

    for row in jobArr do
        LogInfo "%A" row


    let opts =
        { PubnetCoreSetOptions context.image with
              localHistory = false
              invariantChecks = AllInvariantsExceptBucketConsistencyChecks
              initialization = CoreSetInitialization.OnlyNewDb }

    let mutable jobQueue = Array.toList (Array.rev jobArr)

    context.ExecuteJobs
        (Some(opts))
        (Some(SDFMainNet))
        (fun (formation: StellarFormation) ->
            (formation.RunParallelJobs
                parallelism
                (fun _ ->
                    (match jobQueue with
                     | [] -> None
                     | head :: tail ->
                         jobQueue <- tail
                         Some head))
                context.image)
            |> formation.CheckAllJobsSucceeded)
