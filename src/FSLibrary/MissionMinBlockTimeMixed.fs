// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionMinBlockTimeMixed

// Minimum-block-time search for the new overlay-only mixed-pregen loadgen modes:
// pre-generated classic payments plus one explicit synthetic Soroban tx type.

open MinBlockTimeTest
open StellarMissionContext
open StellarCoreHTTP

let minBlockTimeMixed (baseContext: MissionContext) =
    let mode = parseMixedPregenMode baseContext.minBlockTimeMixedMode

    let classicTxRate, sorobanTxRate =
        match baseContext.minBlockTimeMixedClassicTxRate, baseContext.minBlockTimeMixedSorobanTxRate with
        | None, None ->
            let soroban = baseContext.txRate / 2
            baseContext.txRate - soroban, soroban
        | Some classic, None -> classic, 0
        | None, Some soroban -> 0, soroban
        | Some classic, Some soroban -> classic, soroban

    if classicTxRate < 0 || sorobanTxRate < 0 then
        failwith "--classic-tx-rate and --soroban-tx-rate must be non-negative"

    if classicTxRate = 0 && sorobanTxRate = 0 then
        failwith "At least one of --classic-tx-rate or --soroban-tx-rate must be non-zero"

    let context =
        { baseContext with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(baseContext.installNetworkDelay |> Option.defaultValue true)
              enableTailLogging = false
              txRate = classicTxRate + sorobanTxRate }

    let baseLoadGen =
        { LoadGen.GetDefault() with
              mode = mode
              spikesize = context.spikeSize
              spikeinterval = context.spikeInterval
              offset = 0
              maxfeerate = None
              skiplowfeetxs = false
              classicTxRate = Some classicTxRate
              sorobanTxRate = Some sorobanTxRate }

    minBlockTimeTest context baseLoadGen None
