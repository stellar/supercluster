// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryGenerateAndCatchup

open MissionCatchupHelpers
open StellarCoreHTTP
open StellarCorePeer
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster

let sorobanProtocolVersion = 20

let historyGenerateAndCatchup (context: MissionContext) =
    let context = context.WithNominalLoad
    let image = context.image
    let catchupOptions = { generatorImage = image; catchupImage = image; versionImage = image }
    let catchupSets = MakeRecentCatchupSet catchupOptions
    let sets = catchupSets.AllSetList()

    let mutable maxVersion = 0

    context.Execute
        sets
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilAllLiveSynced()

            let versionPeer = formation.NetworkCfg.GetPeer catchupSets.versionSet 0
            maxVersion <- versionPeer.GetSupportedProtocolVersion()

            doCatchup context formation catchupSets)

    for i in 0 .. maxVersion do
        let contextCopy = context.WithNominalLoad

        contextCopy.Execute
            sets
            None
            (fun (formation: StellarFormation) ->
                formation.WaitUntilAllLiveSynced()
                doCatchupForVersion contextCopy formation catchupSets i false)

    if maxVersion = sorobanProtocolVersion then
        let contextCopy = { context with numTxs = 1000; numAccounts = 100; txRate = 1 }

        contextCopy.Execute
            sets
            None
            (fun (formation: StellarFormation) ->
                formation.WaitUntilAllLiveSynced()
                doCatchupForVersion contextCopy formation catchupSets sorobanProtocolVersion true)
