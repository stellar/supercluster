// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionDatabaseInplaceUpgrade

open StellarCoreCfg
open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarSupercluster

let databaseInplaceUpgrade (context : MissionContext) =
    let context = context.WithNominalLoad
    let newImage = GetOrDefault context.image CfgVal.stellarCoreImageName
    let oldImage = GetOrDefault context.oldImage CfgVal.stellarCoreImageName

    let quorumSet = Some(["core"])
    let beforeUpgradeCoreSet = MakeLiveCoreSet 
                                 "before-upgrade"
                                 { CoreSetOptions.Default with
                                     nodeCount = 1
                                     quorumSet = quorumSet
                                     image = Some(oldImage)
                                     persistentVolume = Some("upgrade") }
    let coreSet = MakeLiveCoreSet "core" { CoreSetOptions.Default with quorumSet = quorumSet; image = Some(newImage) }
    let afterUpgradeCoreSet = MakeDeferredCoreSet 
                                 "after-upgrade"
                                 { CoreSetOptions.Default with
                                     nodeCount = 1
                                     quorumSet = quorumSet
                                     image = Some(newImage)
                                     persistentVolume = Some("upgrade")
                                     initialization = { newDb = false
                                                        newHist = false
                                                        initialCatchup = false
                                                        forceScp = false } }

    context.Execute [beforeUpgradeCoreSet; coreSet; afterUpgradeCoreSet] None (fun (formation: ClusterFormation) ->
      formation.WaitUntilSynced [beforeUpgradeCoreSet; coreSet]

      let peer = formation.NetworkCfg.GetPeer beforeUpgradeCoreSet 0
      let version = peer.GetProtocolVersion()
      formation.UpgradeProtocol [coreSet] version

      formation.RunLoadgen beforeUpgradeCoreSet context.GenerateAccountCreationLoad
      formation.RunLoadgen beforeUpgradeCoreSet context.GeneratePaymentLoad
      formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
      formation.RunLoadgen coreSet context.GeneratePaymentLoad

      formation.Stop beforeUpgradeCoreSet.name
      formation.Start afterUpgradeCoreSet.name

      formation.RunLoadgen afterUpgradeCoreSet context.GenerateAccountCreationLoad
      formation.RunLoadgen afterUpgradeCoreSet context.GeneratePaymentLoad
      formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
      formation.RunLoadgen coreSet context.GeneratePaymentLoad
    )
