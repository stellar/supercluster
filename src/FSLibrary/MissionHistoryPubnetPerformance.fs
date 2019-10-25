// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetPerformance

open Logging
open System
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open StellarJobExec

let historyPubnetPerformance (context : MissionContext) =
    let opts = { PubnetCoreSetOptions with
                     localHistory = false
                     initialization = { PubnetCoreSetOptions.initialization with
                                            newHist = false
                                            forceScp = false } }

    context.ExecuteJobs (Some(opts)) (Some(SDFMainNet))
        begin
        fun (formation: StellarFormation) ->

            (formation.RunSingleJobWithTimeout context.destination
                 (Some(TimeSpan.FromMinutes(10.0)))
                 [| "catchup"; "20000000/0" |])
            |> formation.CheckAllJobsSucceeded

            (formation.RunSingleJobWithTimeout context.destination
                 (Some(TimeSpan.FromHours(4.0)))
                 [| "catchup"; "20050000/50000" |])
            |> formation.CheckAllJobsSucceeded

        end
