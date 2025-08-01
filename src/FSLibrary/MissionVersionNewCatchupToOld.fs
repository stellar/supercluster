// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionVersionMixNewCatchupToOld

open MissionCatchupHelpers
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP

let versionMixNewCatchupToOld (context: MissionContext) =
    let context =
        { context.WithNominalLoad with
              genesisTestAccountCount = Some context.WithNominalLoad.numAccounts }

    let newImage = context.image
    let oldImage = GetOrDefault context.oldImage newImage

    let catchupOptions = { generatorImage = oldImage; catchupImage = newImage; versionImage = oldImage }
    let catchupSets = MakeCatchupSets catchupOptions
    let sets = catchupSets.AllSetList()

    context.Execute
        sets
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilAllLiveSynced()
            doCatchup context formation catchupSets)
