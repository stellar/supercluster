// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionVersionMixNewCatchupToOld

open MissionCatchupHelpers
open StellarCoreCfg
open StellarMissionContext
open StellarSupercluster

let versionMixNewCatchupToOld (context : MissionContext) =
    let newImage = GetOrDefault context.image CfgVal.stellarCoreImageName
    let oldImage = GetOrDefault context.oldImage CfgVal.stellarCoreImageName

    let catchupOptions = { generatorImage = oldImage; catchupImage = newImage; versionImage = oldImage }
    let catchupSets = MakeCatchupSets catchupOptions
    let sets = catchupSets.AllSetList()
    context.Execute sets None (fun (formation: ClusterFormation) ->
        formation.WaitUntilSynced sets
        doCatchup context formation catchupSets
    )
