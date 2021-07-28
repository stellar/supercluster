// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryTestnetRecentCatchup

open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open StellarStatefulSets
open StellarSupercluster

let historyTestnetRecentCatchup (context: MissionContext) =
    let context = { context with coreResources = NonParallelCatchupResources }

    let set =
        { TestnetCoreSetOptions context.image with
              nodeCount = 1
              catchupMode = CatchupRecent(1001) }

    let coreSet = MakeLiveCoreSet "core" set

    context.Execute
        [ coreSet ]
        (Some(SDFTestNet))
        (fun (formation: StellarFormation) -> formation.WaitUntilSynced [ coreSet ])
