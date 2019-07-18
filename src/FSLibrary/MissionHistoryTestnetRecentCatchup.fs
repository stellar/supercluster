// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryTestnetRecentCatchup

open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData

let historyTestnetRecentCatchup (context : MissionContext) =
    let set = { TestnetCoreSet with catchupMode = CatchupRecent(1001) }
    let coreSet = MakeCoreSet "core" 1 1 set
    context.Execute [coreSet] (Some(SDFTestNet)) (fun f ->
        f.WaitUntilSynced [coreSet]
    )
