// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryGenerateAndCatchup

open MissionCatchupHelpers
open StellarCoreCfg
open StellarCorePeer
open StellarCoreHTTP
open StellarMissionContext
open StellarSupercluster

let historyGenerateAndCatchup (context : MissionContext) =
    let image = CfgVal.stellarCoreImageName
    let catchupOptions = { generatorImage = image; catchupImage = image; versionImage = image }
    let catchupSets = MakeCatchupSets catchupOptions
    let sets = catchupSets.AllSetList()

    context.Execute sets None (fun (formation: ClusterFormation) ->
        formation.WaitUntilSynced sets
        doCatchup context formation catchupSets
    )
