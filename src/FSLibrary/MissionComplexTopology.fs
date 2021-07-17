// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionComplexTopology

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP

let complexTopology (context: MissionContext) =
    let context = context.WithNominalLoad

    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = 4
                  quorumSet = CoreSetQuorum(CoreSetName "core")
                  accelerateTime = true }

    let publicSet =
        MakeLiveCoreSet
            "public"
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = 2
                  quorumSet = CoreSetQuorum(CoreSetName "core")
                  accelerateTime = true
                  initialization = CoreSetInitialization.DefaultNoForceSCP
                  validate = false }

    let orgSet =
        MakeLiveCoreSet
            "org"
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = 1
                  quorumSet = CoreSetQuorum(CoreSetName "core")
                  peers = Some([ CoreSetName "public" ])
                  accelerateTime = true
                  initialization = CoreSetInitialization.DefaultNoForceSCP
                  validate = false }

    context.Execute
        [ coreSet; publicSet; orgSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet
                                        publicSet
                                        orgSet ]

            formation.UpgradeProtocolToLatest [ coreSet ]

            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
            formation.RunLoadgen coreSet context.GeneratePaymentLoad)
