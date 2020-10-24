// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryTestnetParallelCatchup

open Logging
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open StellarJobExec
open StellarSupercluster

let historyTestnetParallelCatchup (context : MissionContext) =
    let checkpointsPerJob = 256
    let ledgersPerCheckpoint = 64
    let ledgersPerJob = checkpointsPerJob * ledgersPerCheckpoint
    let totalLedgers = GetLatestTestnetLedgerNumber()
    let numJobs = (totalLedgers / ledgersPerJob)
    let parallelism = 128
    let overlapCheckpoints = 5
    let overlapLedgers = overlapCheckpoints * ledgersPerCheckpoint

    LogInfo "Running %d jobs (%d-way parallel) of %d checkpoints each, to catch up to ledger %d"
            numJobs parallelism checkpointsPerJob totalLedgers

    let catchupRangeStr i = sprintf "%d/%d" ((i+1) * ledgersPerJob) (ledgersPerJob + overlapLedgers)
    let jobArr = Array.init numJobs (fun i -> [| "catchup"; catchupRangeStr i|])
    let opts = { TestnetCoreSetOptions context.image with
                     localHistory = false
                     initialization = CoreSetInitialization.OnlyNewDb }
    context.ExecuteJobs (Some(opts)) (Some(SDFTestNet))
        begin
        fun (formation: StellarFormation) ->
            (formation.RunParallelJobsInRandomOrder parallelism context.destination jobArr context.image)
            |> formation.CheckAllJobsSucceeded
        end
