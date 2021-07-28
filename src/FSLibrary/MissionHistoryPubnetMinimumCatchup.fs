// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetMinimumCatchup

open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open StellarStatefulSets
open StellarSupercluster

let historyPubnetMinimumCatchup (context: MissionContext) =
    let context = { context with coreResources = NonParallelCatchupResources }

    let set =
        { PubnetCoreSetOptions context.image with
              nodeCount = 1
              catchupMode = CatchupRecent(0)
              invariantChecks = AllInvariantsExceptBucketConsistencyChecks }

    let coreSet = MakeLiveCoreSet "core" set

    context.Execute
        [ coreSet ]
        (Some(SDFMainNet))
        (fun (formation: StellarFormation) -> formation.WaitUntilSynced [ coreSet ])
