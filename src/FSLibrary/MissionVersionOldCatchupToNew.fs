// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionVersionMixOldCatchupToNew

open MissionCatchupHelpers
open StellarCoreCfg
open StellarMissionContext
open StellarFormation

let versionMixOldCatchupToNew (context : MissionContext) =
    let context = context.WithNominalLoad
    let newImage = GetOrDefault context.image CfgVal.stellarCoreImageName
    let oldImage = GetOrDefault context.oldImage CfgVal.stellarCoreImageName

    let catchupOptions = { generatorImage = newImage; catchupImage = oldImage; versionImage = oldImage }
    let catchupSets = MakeCatchupSets catchupOptions
    let sets = catchupSets.AllSetList()
    context.Execute sets None (fun (formation: StellarFormation) ->
        formation.WaitUntilSynced sets
        doCatchup context formation catchupSets
    )
