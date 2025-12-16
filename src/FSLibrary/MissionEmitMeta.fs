// Copyright 2021 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionEmitMeta

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarSupercluster
open StellarStatefulSets
open StellarCoreHTTP

let runEmitMeta (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  dumpDatabase = false
                  updateSorobanCosts = Some(true) }

    let coreSetWithCaptiveCore =
        MakeLiveCoreSet
            "in-memory-mode"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecksAndEvents
                  nodeCount = 1
                  emitMeta = true
                  validate = false
                  localHistory = false
                  quorumSet = CoreSetQuorum(CoreSetName "core")
                  updateSorobanCosts = Some(true) }

    let context =
        { context.WithSmallLoadgenOptions with
              numAccounts = 100
              numTxs = 100
              txRate = 20
              genesisTestAccountCount = Some 100 }

    context.Execute
        [ coreSet; coreSetWithCaptiveCore ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet
                                        coreSetWithCaptiveCore ]

            formation.UpgradeProtocolToLatest [ coreSet
                                                coreSetWithCaptiveCore ]

            formation.UpgradeMaxTxSetSize [ coreSet; coreSetWithCaptiveCore ] 100000

            formation.RunLoadgen coreSet context.GeneratePaymentLoad
            formation.UpgradeSorobanLedgerLimitsWithMultiplier [ coreSet ] 100
            formation.RunLoadgen coreSet { context.GenerateSorobanUploadLoad with txrate = 1 })
