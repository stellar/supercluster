// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryGenerateAndCatchup

open MissionCatchupHelpers
open StellarCoreCfg
open StellarMissionContext

let historyGenerateAndCatchup (context : MissionContext) =
    let image = CfgVal.stellarCoreImageName
    let (generatorSet, deferedSets) = catchupSets image image
    let sets = List.append [generatorSet] deferedSets
    context.Execute sets None (fun f ->
        f.WaitUntilSynced sets
        doCatchup context f generatorSet deferedSets (Some(11))
    )
