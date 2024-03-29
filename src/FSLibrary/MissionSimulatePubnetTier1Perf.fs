// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSimulatePubnetTier1Perf

// The point of this mission is to simulate the tier1 group of pubnet
// for purposes of repeatable / comparable performance evaluation.

open MaxTPSTest
open StellarMissionContext
open StellarCoreHTTP

let simulatePubnetTier1Perf (context: MissionContext) =
    let context =
        { context with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(context.installNetworkDelay |> Option.defaultValue true)
              // No additional DB overhead unless specified (this will measure the in-memory SQLite DB only)
              simulateApplyDuration = Some(context.simulateApplyDuration |> Option.defaultValue (seq { 0 }))
              simulateApplyWeight = Some(context.simulateApplyWeight |> Option.defaultValue (seq { 100 }))
              enableTailLogging = false }

    let baseLoadGen =
        { LoadGen.GetDefault() with
              mode = GeneratePaymentLoad
              spikesize = context.spikeSize
              spikeinterval = context.spikeInterval
              offset = 0
              maxfeerate = None
              skiplowfeetxs = false }

    maxTPSTest context baseLoadGen None false
