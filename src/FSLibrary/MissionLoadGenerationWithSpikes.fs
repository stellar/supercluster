// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionLoadGenerationWithSpikes

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP

let loadGenerationWithSpikes (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecksAndEvents
                  dumpDatabase = false }

    let context =
        { context with
              coreResources = MediumTestResources
              numAccounts = 2000
              numTxs = 20000
              txRate = 20
              spikeSize = 2000
              spikeInterval = 30
              genesisTestAccountCount = Some 2000 }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSetSize [ coreSet ] 100000

            formation.RunLoadgen coreSet context.GeneratePaymentLoad)
