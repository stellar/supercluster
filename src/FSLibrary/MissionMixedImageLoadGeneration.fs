// Copyright 2022 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionMixedImageLoadGeneration

open stellar_dotnet_sdk
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP

let mixedImageLoadGeneration (oldImageNodeCount: int) (context: MissionContext) =
    let oldNodeCount = oldImageNodeCount
    let newNodeCount = 3 - oldImageNodeCount

    let newImage = context.image
    let oldImage = GetOrDefault context.oldImage newImage

    let oldName = "core-old"
    let newName = "core-new"

    // Allow 2/3 nodes consensus, so that one image version could fork the network
    // in case of bugs.
    let qSet =
        CoreSetQuorumListWithThreshold(([ CoreSetName oldName; CoreSetName newName ], 51))

    let oldCoreSet =
        MakeLiveCoreSet
            oldName
            { CoreSetOptions.GetDefault oldImage with
                  nodeCount = oldNodeCount
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  dumpDatabase = false
                  quorumSet = qSet }

    let newCoreSet =
        MakeLiveCoreSet
            newName
            { CoreSetOptions.GetDefault newImage with
                  nodeCount = newNodeCount
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  dumpDatabase = false
                  quorumSet = qSet }

    let context =
        { context with
              numAccounts = 200
              numTxs = 50000
              txRate = 1000
              skipLowFeeTxs = true }

    // Put the version with majority of nodes in front of the set to let it generate
    // the load and possibly leave the minority of nodes out of consensus in case of bugs.
    let coreSets =
        if oldNodeCount > newNodeCount then
            [ oldCoreSet; newCoreSet ]
        else
            [ newCoreSet; oldCoreSet ]

    context.Execute
        coreSets
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced coreSets
            formation.UpgradeProtocolToLatest coreSets
            formation.UpgradeMaxTxSetSize coreSets 1000

            formation.RunLoadgen oldCoreSet context.GenerateAccountCreationLoad
            formation.RunLoadgen oldCoreSet context.GeneratePaymentLoad)

let mixedImageLoadGenerationWithOldImageMajority (context: MissionContext) = mixedImageLoadGeneration 2 context

let mixedImageLoadGenerationWithNewImageMajority (context: MissionContext) = mixedImageLoadGeneration 1 context
