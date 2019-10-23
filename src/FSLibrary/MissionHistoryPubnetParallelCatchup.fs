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

let historyPubnetParallelCatchup (context : MissionContext) =
    let checkpointsPerJob = 1000
    let ledgersPerCheckpoint = 64
    let ledgersPerJob = checkpointsPerJob * ledgersPerCheckpoint  // 64,000
    let totalLedgers = GetLatestPubnetLedgerNumber()              // ~25 million ish
    let numJobs = (totalLedgers / ledgersPerJob)                  // 400 ish
    let parallelism = 32
    let overlapCheckpoints = 5
    let overlapLedgers = overlapCheckpoints * ledgersPerCheckpoint

    LogInfo "Running %d jobs (%d-way parallel) of %d checkpoints each, to catch up to ledger %d"
            numJobs parallelism checkpointsPerJob totalLedgers

    let catchupRangeStr i = sprintf "%d/%d" ((i+1) * ledgersPerJob) (ledgersPerJob + overlapLedgers)
    let jobArr = Array.init numJobs (fun i -> [| "catchup"; catchupRangeStr i|])
    let opts = { PubnetCoreSetOptions with
                     localHistory = false
                     initialization = { PubnetCoreSetOptions.initialization with
                                            newHist = false
                                            forceScp = false } }
    context.ExecuteJobs (Some(opts)) (Some(SDFMainNet))
        begin
        fun (formation: StellarFormation) ->
            (formation.RunParallelJobsInRandomOrder parallelism context.destination jobArr)
            |> formation.CheckAllJobsSucceeded
        end
