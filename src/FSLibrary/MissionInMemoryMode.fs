// Copyright 2021 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionInMemoryMode

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarSupercluster
open StellarStatefulSets
open StellarCoreHTTP

let runInMemoryMode (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet "core" { CoreSetOptions.GetDefault context.image with dumpDatabase = false }

    let coreSetWithCaptiveCore =
        MakeLiveCoreSet
            "in-memory-mode"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  nodeCount = 1
                  inMemoryMode = true
                  validate = false
                  localHistory = false
                  quorumSet = CoreSetQuorum(CoreSetName "core") }

    let context = { context with numAccounts = 100; numTxs = 100; txRate = 20 }

    context.Execute
        [ coreSet; coreSetWithCaptiveCore ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet
                                        coreSetWithCaptiveCore ]

            formation.UpgradeProtocolToLatest [ coreSet
                                                coreSetWithCaptiveCore ]

            formation.UpgradeMaxTxSize [ coreSet; coreSetWithCaptiveCore ] 100000

            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
            formation.RunLoadgen coreSet context.GeneratePaymentLoad)
