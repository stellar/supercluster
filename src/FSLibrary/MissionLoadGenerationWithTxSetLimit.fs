// Copyright 2022 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionLoadGenerationWithTxSetLimit

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP

let loadGenerationWithTxSetLimit (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecksAndEvents
                  dumpDatabase = false
                  updateSorobanCosts = Some(true) }

    let context =
        { context.WithSmallLoadgenOptions with
              coreResources = MediumTestResources
              numAccounts = 20000
              numTxs = 50000
              txRate = 1000
              skipLowFeeTxs = true }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSetSize [ coreSet ] 1000

            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
            formation.RunLoadgen coreSet context.GeneratePaymentLoad
            formation.UpgradeSorobanLedgerLimitsWithMultiplier [ coreSet ] 100
            formation.RunLoadgen coreSet { context.GenerateSorobanUploadLoad with txrate = 1; txs = 200 })
