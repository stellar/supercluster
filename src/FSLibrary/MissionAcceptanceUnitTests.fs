// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionAcceptanceUnitTests

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarJobExec

let acceptanceUnitTests (context : MissionContext) =
    context.ExecuteJobs None None
        (fun formation ->
         formation.RunSingleJob context.destination [| "test"; "[acceptance]~[.]" |] context.image
         |> formation.CheckAllJobsSucceeded)
