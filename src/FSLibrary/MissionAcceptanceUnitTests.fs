// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionAcceptanceUnitTests

open StellarCoreSet
open StellarMissionContext
open StellarSupercluster
open StellarJobExec

let acceptanceUnitTests (context: MissionContext) =
    let context = { context with coreResources = AcceptanceTestResources }

    let opts =
        { CoreSetOptions.GetDefault context.image with
              dbType = Postgres
              localHistory = false
              nodeCount = 1
              initialization = CoreSetInitialization.NoInitCmds }

    context.ExecuteJobs
        (Some(opts))
        None
        (fun formation ->
            formation.RunSingleJob [| "test"; "[acceptance]~[.]" |] context.image false
            |> formation.CheckAllJobsSucceeded)
