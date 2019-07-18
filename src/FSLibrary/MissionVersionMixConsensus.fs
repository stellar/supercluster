// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionVersionMixConsensus

open MissionHelpers
open StellarCoreCfg
open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarTransaction

let versionMixConsensus (context : MissionContext) =
    let newImage = GetOrDefault context.image CfgVal.stellarCoreImageName
    let oldImage = GetOrDefault context.oldImage CfgVal.stellarCoreImageName

    let version = obtainVersion context oldImage

    let oldCoreSet = MakeCoreSet "old-core" 2 2 { CoreSetOptions.Default with image = Some(oldImage) }
    let newCoreSet = MakeCoreSet "new-core" 2 2 { CoreSetOptions.Default with image = Some(newImage) }
    context.Execute [oldCoreSet; newCoreSet] None (fun f ->
        f.WaitUntilSynced [oldCoreSet; newCoreSet]

        let upgrades = { DefaultUpgradeParameters with protocolVersion = version }
        f.NetworkCfg.EachPeer(fun p ->
            p.SetUpgrades(upgrades)
        )

        f.CreateAccount oldCoreSet UserAlice
        f.CreateAccount oldCoreSet UserBob
        f.Pay oldCoreSet UserAlice UserBob
        f.Pay newCoreSet UserAlice UserBob
    )
