// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionLoadGeneration

open StellarCoreSet
open StellarMissionContext
open StellarSupercluster

let loadGeneration (context : MissionContext) =
    let coreSet = MakeLiveCoreSet "core" CoreSetOptions.Default
    context.Execute [coreSet] None (fun (formation: ClusterFormation) ->
        formation.WaitUntilSynced [coreSet]
        formation.UpgradeProtocolToLatest [coreSet]
        formation.UpgradeMaxTxSize [coreSet] 100000

        formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
        formation.RunLoadgen coreSet context.GeneratePaymentLoad
    )
