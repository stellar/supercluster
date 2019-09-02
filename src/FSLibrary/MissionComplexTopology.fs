// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionComplexTopology

open StellarCoreSet
open StellarMissionContext
open StellarSupercluster

let complexTopology (context : MissionContext) =
    let coreSet = MakeLiveCoreSet "core"
                    { CoreSetOptions.Default with
                        nodeCount = 4
                        quorumSet = Some(["core"])
                        accelerateTime = true }
    let publicSet = MakeLiveCoreSet "public"
                      { CoreSetOptions.Default with
                          nodeCount = 2
                          quorumSet = Some(["core"])
                          accelerateTime = true
                          initialization = { CoreSetInitialization.Default with forceScp = false }
                          validate = false }
    let orgSet = MakeLiveCoreSet "org"
                   { CoreSetOptions.Default with
                       nodeCount = 1
                       quorumSet = Some(["core"])
                       peers = Some(["public"])
                       accelerateTime = true
                       initialization = { CoreSetInitialization.Default with forceScp = false }
                       validate = false }

    context.Execute [coreSet; publicSet; orgSet] None (fun (formation: ClusterFormation) ->
        formation.WaitUntilSynced [coreSet; publicSet; orgSet]
        formation.UpgradeProtocolToLatest [coreSet]
        formation.UpgradeMaxTxSize [coreSet] 100000

        formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
        formation.RunLoadgen coreSet context.GeneratePaymentLoad
    )
