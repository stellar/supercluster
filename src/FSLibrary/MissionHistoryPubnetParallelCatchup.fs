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

let historyPubnetParallelCatchup (context : MissionContext) =
    let context = { context with coreResources = ParallelCatchupResources }

    let checkpointsPerJob = 250
    let ledgersPerCheckpoint = 64
    let latestLedgerNum = GetLatestPubnetLedgerNumber()
    let ledgersPerJob = checkpointsPerJob * ledgersPerCheckpoint
    let startingLedger  = max context.pubnetParallelCatchupStartingLedger ledgersPerJob
    let totalLedgers = latestLedgerNum - startingLedger
    let numJobs = max (totalLedgers / ledgersPerJob) 1
    let parallelism = 84
    let overlapCheckpoints = 5
    let overlapLedgers = overlapCheckpoints * ledgersPerCheckpoint

    LogInfo "Running %d jobs (%d-way parallel) of %d checkpoints each, to catch up to ledger %d starting from %d"
            numJobs parallelism checkpointsPerJob latestLedgerNum startingLedger

    let catchupRangeStr i = sprintf "%d/%d" ((i) * ledgersPerJob + startingLedger) (ledgersPerJob + overlapLedgers)
    let jobArr = Array.init numJobs (fun i -> [| "catchup"; catchupRangeStr i|])
    let opts = { PubnetCoreSetOptions context.image with
                     localHistory = false
                     initialization = CoreSetInitialization.OnlyNewDb }
    context.ExecuteJobs (Some(opts)) (Some(SDFMainNet))
        begin
        fun (formation: StellarFormation) ->
            (formation.RunParallelJobsInRandomOrder parallelism context.destination jobArr context.image)
            |> formation.CheckAllJobsSucceeded
        end
