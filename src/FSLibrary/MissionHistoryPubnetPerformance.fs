// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetPerformance

open System
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open StellarJobExec
open StellarSupercluster

let historyPubnetPerformance (context: MissionContext) =
    let opts =
        { PubnetCoreSetOptions context.image with
              localHistory = false
              invariantChecks = AllInvariantsExceptBucketConsistencyChecks
              initialization = CoreSetInitialization.OnlyNewDb }

    context.ExecuteJobs
        (Some(opts))
        (Some(SDFMainNet))
        (fun (formation: StellarFormation) ->

            (formation.RunSingleJobWithTimeout
                (Some(TimeSpan.FromMinutes(10.0)))
                [| "catchup"; "20000000/0" |]
                context.image
                true)
            |> formation.CheckAllJobsSucceeded

            (formation.RunSingleJobWithTimeout
                (Some(TimeSpan.FromHours(4.0)))
                [| "catchup"; "20050000/50000" |]
                context.image
                true)
            |> formation.CheckAllJobsSucceeded)
