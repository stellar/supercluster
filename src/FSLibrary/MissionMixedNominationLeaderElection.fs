// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

// This mission runs a network with a mix of nodes running the old and new
// nomination leader election algorithms.

module MissionMixedNominationLeaderElection

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarFormation
open StellarMissionContext
open StellarStatefulSets
open StellarSupercluster

let mixedNominationAlgorithm (oldCount: int) (context: MissionContext) =
    let oldNodeCount = oldCount
    let newNodeCount = 3 - oldCount

    let oldName = "core-old-leader-election"
    let newName = "core-new-leader-election"

    let oldCoreSet =
        MakeLiveCoreSet
            oldName
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = oldNodeCount
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  accelerateTime = false
                  dumpDatabase = false
                  forceOldStyleLeaderElection = true }

    let newCoreSet =
        MakeLiveCoreSet
            newName
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = newNodeCount
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  accelerateTime = false
                  dumpDatabase = false
                  requireAutoQset = true }

    let coreSets = [ oldCoreSet; newCoreSet ]

    context.Execute
        coreSets
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced coreSets

            let peer = formation.NetworkCfg.GetPeer oldCoreSet 0
            peer.WaitForFewLedgers(3)
            formation.UpgradeProtocolToLatest coreSets
            peer.WaitForLatestProtocol()
            peer.WaitForFewLedgers(60) // About 5 minutes

            // Check everything is still in sync
            formation.CheckNoErrorsAndPairwiseConsistency()
            formation.EnsureAllNodesInSync coreSets)

let mixedNominationLeaderElectionWithOldMajority (context: MissionContext) = mixedNominationAlgorithm 2 context

let mixedNominationLeaderElectionWithNewMajority (context: MissionContext) = mixedNominationAlgorithm 1 context
