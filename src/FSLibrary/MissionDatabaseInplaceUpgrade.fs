// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionDatabaseInplaceUpgrade

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarDataDump
open StellarSupercluster

let databaseInplaceUpgrade (context: MissionContext) =
    let context = context.WithNominalLoad
    let newImage = context.image
    let oldImage = GetOrDefault context.oldImage context.image

    let quorumSet = CoreSetQuorum(CoreSetName("core"))

    let coreSet =
        MakeLiveCoreSet "core" { CoreSetOptions.GetDefault newImage with quorumSet = quorumSet }

    let beforeUpgradeCoreSet =
        MakeLiveCoreSet
            "before-upgrade"
            { CoreSetOptions.GetDefault oldImage with nodeCount = 1; quorumSet = quorumSet }

    let fetchFromPeer = Some(CoreSetName("before-upgrade"), 0)

    let afterUpgradeCoreSet =
        MakeDeferredCoreSet
            "after-upgrade"
            { CoreSetOptions.GetDefault newImage with
                  nodeCount = 1
                  quorumSet = quorumSet
                  initialization =
                      { newDb = false
                        newHist = false
                        initialCatchup = false
                        waitForConsensus = true
                        fetchDBFromPeer = fetchFromPeer } }

    context.Execute
        [ beforeUpgradeCoreSet; coreSet; afterUpgradeCoreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            let peer = formation.NetworkCfg.GetPeer beforeUpgradeCoreSet 0
            let version = peer.GetSupportedProtocolVersion()
            formation.UpgradeProtocol [ coreSet ] version

            formation.WaitUntilSynced [ beforeUpgradeCoreSet ]

            formation.RunLoadgen beforeUpgradeCoreSet context.GenerateAccountCreationLoad
            formation.RunLoadgen beforeUpgradeCoreSet context.GeneratePaymentLoad
            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
            formation.RunLoadgen coreSet context.GeneratePaymentLoad

            formation.BackupDatabaseToHistory peer
            formation.Start afterUpgradeCoreSet.name

            let afterUpgradeCoreSetLive = formation.NetworkCfg.FindCoreSet afterUpgradeCoreSet.name
            formation.WaitUntilSynced [ afterUpgradeCoreSetLive ]

            formation.RunLoadgen afterUpgradeCoreSet context.GenerateAccountCreationLoad
            formation.RunLoadgen afterUpgradeCoreSet context.GeneratePaymentLoad
            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
            formation.RunLoadgen coreSet context.GeneratePaymentLoad)
