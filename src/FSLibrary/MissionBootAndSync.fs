// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionBootAndSync

open StellarCoreSet
open StellarMissionContext
open StellarSupercluster
open StellarFormation
open StellarStatefulSets

let bootAndSync (context: MissionContext) =
    let coreSet = MakeLiveCoreSet "core" (CoreSetOptions.GetDefault context.image)

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ])
