// Copyright 2023 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanLoadGeneration

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP


let sorobanLoadGeneration (context: MissionContext) =
    let rate = 5

    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptEvents
                  dumpDatabase = false }

    let context =
        { context.WithSmallLoadgenOptions with
              txRate = rate
              numAccounts = 10000
              numTxs = rate * 200
              skipLowFeeTxs = true
              maxFeeRate = Some 100000000
              updateSorobanCosts = Some(true)
              genesisTestAccountCount = Some 10000 }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            // Wait until the whole network is synced before proceeding,
            // to fail asap in case of a misconfiguration
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSetSize [ coreSet ] 1000000

            formation.UpgradeSorobanLedgerLimitsWithMultiplier [ coreSet ] 100
            formation.RunLoadgen coreSet context.GenerateSorobanUploadLoad)
