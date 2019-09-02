// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSimplePayment

open StellarCoreSet
open StellarMissionContext
open StellarTransaction
open StellarSupercluster

let simplePayment (context : MissionContext) =
    let coreSet = MakeLiveCoreSet "core" CoreSetOptions.Default
    context.Execute [coreSet] None (fun (formation: ClusterFormation) ->
        formation.WaitUntilSynced [coreSet]
        formation.UpgradeProtocolToLatest [coreSet]

        formation.CreateAccount coreSet UserAlice
        formation.CreateAccount coreSet UserBob
        formation.Pay coreSet UserAlice UserBob
    )
