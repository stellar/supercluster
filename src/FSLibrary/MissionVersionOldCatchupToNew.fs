// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionVersionMixOldCatchupToNew

open MissionCatchupHelpers
open MissionHelpers
open StellarCoreCfg
open StellarMissionContext

let versionMixOldCatchupToNew (context : MissionContext) =
    let newImage = GetOrDefault context.image CfgVal.stellarCoreImageName
    let oldImage = GetOrDefault context.oldImage CfgVal.stellarCoreImageName

    let (generatorSet, deferedSets) = catchupSets newImage oldImage
    let sets = List.append [generatorSet] deferedSets
    let version = obtainVersion context oldImage
    context.Execute sets None (fun f ->
        f.WaitUntilSynced sets
        doCatchup context f generatorSet deferedSets version
    )
