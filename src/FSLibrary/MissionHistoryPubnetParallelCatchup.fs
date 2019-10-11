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
    context.ExecuteJobs PubnetCoreSet (Some(SDFMainNet))
        begin
        fun (formation: ClusterFormation) ->
        let j = formation.StartJobForCmds [|
                                           [| "new-db" |];
                                           [| "offline-info" |]
                                           [| "catchup"; "current/1000" |]
                                           |]
        let (handle, ok) = formation.WatchJob j
        let i = System.Threading.WaitHandle.WaitAny(Array.ofList [handle])
        if !ok
        then LogInfo "Job %d passed" i
        else LogInfo "Job %d failed" i
        end
