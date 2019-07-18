// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionDatabaseInplaceUpgrade

open MissionHelpers
open StellarCoreCfg
open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext

let databaseInplaceUpgrade (context : MissionContext) =
    let newImage = GetOrDefault context.image CfgVal.stellarCoreImageName
    let oldImage = GetOrDefault context.oldImage CfgVal.stellarCoreImageName

    let version = obtainVersion context oldImage

    let quorumSet = Some(["core"])
    let beforeUpgradeCoreSet = MakeCoreSet 
                                 "before-upgrade"
                                 1 1
                                 { CoreSetOptions.Default with
                                     quorumSet = quorumSet
                                     image = Some(oldImage)
                                     persistentVolume = Some("upgrade") }
    let coreSet = MakeCoreSet "core" 3 3 { CoreSetOptions.Default with quorumSet = quorumSet; image = Some(newImage) }
    let afterUpgradeCoreSet = MakeCoreSet 
                                 "after-upgrade"
                                 0 1
                                 { CoreSetOptions.Default with
                                     quorumSet = quorumSet
                                     image = Some(newImage)
                                     persistentVolume = Some("upgrade")
                                     initialization = { newDb = false
                                                        newHist = false
                                                        initialCatchup = false
                                                        forceScp = false } }

    context.Execute [beforeUpgradeCoreSet; coreSet; afterUpgradeCoreSet] None (fun f ->
      f.WaitUntilSynced [beforeUpgradeCoreSet; coreSet]

      let upgrades = { DefaultUpgradeParameters with protocolVersion = version }
      f.NetworkCfg.EachPeer(fun p ->
          p.SetUpgrades(upgrades)
      )

      f.RunLoadgen beforeUpgradeCoreSet context.GenerateAccountCreationLoad
      f.RunLoadgen beforeUpgradeCoreSet context.GeneratePaymentLoad
      f.RunLoadgen coreSet context.GenerateAccountCreationLoad
      f.RunLoadgen coreSet context.GeneratePaymentLoad

      f.ChangeCount beforeUpgradeCoreSet.name 0
      f.ChangeCount afterUpgradeCoreSet.name 1

      f.RunLoadgen afterUpgradeCoreSet context.GenerateAccountCreationLoad
      f.RunLoadgen afterUpgradeCoreSet context.GeneratePaymentLoad
      f.RunLoadgen coreSet context.GenerateAccountCreationLoad
      f.RunLoadgen coreSet context.GeneratePaymentLoad
    )
