// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetParallelCatchup

open k8s
open k8s.Models

open Logging
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open System
open Logging


let historyPubnetParallelCatchup (context : MissionContext) =
    let checkpointsPerJob = 1000
    let totalLedgers = GetLatestPubnetLedgerNumber()
    let ledgersPerCheckpoint = 64
    let ledgersPerJob = checkpointsPerJob * ledgersPerCheckpoint
    let numJobs = (totalLedgers / ledgersPerJob) + 1
    let parallelism = 32
    LogInfo "Running %d jobs (%d-way parallel) of %d checkpoints each, to catch up to ledger %d"
            numJobs parallelism checkpointsPerJob totalLedgers
    let jobArr = Array.init numJobs (fun i -> [| "catchup"; sprintf "%d/%d" ((i+1)*ledgersPerJob) ledgersPerJob |])
    let opts = { PubnetCoreSet with
                     localHistory = false
                     initialization = { PubnetCoreSet.initialization with
                                            newHist = false
                                            forceScp = false } }
    context.ExecuteJobs opts (Some(SDFMainNet))
        begin
        fun (formation: StellarFormation) ->
            (formation.RunParallelJobsInRandomOrder parallelism jobArr) |> ignore
        end
