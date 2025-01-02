// Copyright 2023 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanInvokeHostLoad

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP

let sorobanInvokeHostLoad (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  emptyDirType = DiskBackedEmptyDir
                  updateSorobanCosts = Some(true) }

    let context =
        { context.WithMediumLoadgenOptions with
              numAccounts = 100
              numTxs = 100
              txRate = 1
              coreResources = MediumTestResources }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSetSize [ coreSet ] 100000

            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
            formation.UpgradeSorobanLedgerLimitsWithMultiplier [ coreSet ] 1000
            formation.UpgradeSorobanTxLimitsWithMultiplier [ coreSet ] 100
            formation.RunLoadgen coreSet context.SetupSorobanInvoke
            formation.RunLoadgen coreSet context.GenerateSorobanInvokeLoad)
