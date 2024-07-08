// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetParallelCatchupV2

open Logging
open StellarMissionContext

let historyPubnetParallelCatchupV2 (context: MissionContext) =
    LogInfo
        "Running parallel catchup v2 ..."