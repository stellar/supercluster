// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionMinBlockTimeClassic

// Mirror of MissionMaxTPSClassic for the minimum-block-time search: fixes the
// TPS at --tx-rate and binary-searches for the smallest ledger target close
// time (in [--min-block-time-ms, --max-block-time-ms] ms) that still keeps
// ledger.age.closed-histogram within the currently enforced SLA checks (P75
// within a widened band around T, and P99 <= 2T).

open MinBlockTimeTest
open StellarMissionContext
open StellarCoreHTTP

let minBlockTimeClassic (context: MissionContext) =
    let context =
        { context with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(context.installNetworkDelay |> Option.defaultValue true)
              enableTailLogging = false }

    let baseLoadGen =
        { LoadGen.GetDefault() with
              mode = GeneratePaymentLoad
              spikesize = context.spikeSize
              spikeinterval = context.spikeInterval
              offset = 0
              maxfeerate = None
              skiplowfeetxs = false }

    minBlockTimeTest context baseLoadGen None
