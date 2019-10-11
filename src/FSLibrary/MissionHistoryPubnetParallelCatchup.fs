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
open StellarSupercluster
open System


let historyPubnetParallelCatchup (context : MissionContext) =
    let checkpointsPerJob = 3000
    let maxLedgers = 26256029
    let ledgersPerCheckpoint = 64
    let ledgersPerJob = checkpointsPerJob * ledgersPerCheckpoint
    let numJobs = (maxLedgers / ledgersPerJob) + 1
    let jobArr = Array.init numJobs (fun i -> [| "catchup"; sprintf "%d/%d" ((i+1)*ledgersPerJob) ledgersPerJob |])
    let opts = { PubnetCoreSet with
                     initialization = { PubnetCoreSet.initialization with
                                            newHist = false;
                                            forceScp = false } }
    context.ExecuteJobs opts (Some(SDFMainNet))
        begin
        fun (formation: ClusterFormation) ->
            (formation.RunParallelJobsInRandomOrder 4 jobArr) |> ignore
        end
