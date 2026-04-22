// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionMinBlockTimeMixed

// Mirror of MissionMaxTPSMixed for the minimum-block-time search: mixed
// classic + Soroban workload at a fixed TPS, binary-searches for the smallest
// ledger target close time that still keeps ledger.age.closed-histogram within SLA.

open MinBlockTimeTest
open PubnetData
open StellarMissionContext
open StellarCoreHTTP

let minBlockTimeMixed (baseContext: MissionContext) =
    let context =
        { baseContext with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(baseContext.installNetworkDelay |> Option.defaultValue true)
              enableTailLogging = false
              wasmBytesDistribution = defaultListValue pubnetWasmBytes baseContext.wasmBytesDistribution
              dataEntriesDistribution = defaultListValue pubnetDataEntries baseContext.dataEntriesDistribution
              totalKiloBytesDistribution = defaultListValue pubnetTotalKiloBytes baseContext.totalKiloBytesDistribution
              txSizeBytesDistribution = defaultListValue pubnetTxSizeBytes baseContext.txSizeBytesDistribution
              instructionsDistribution = defaultListValue pubnetInstructions baseContext.instructionsDistribution }

    let baseLoadGen =
        { LoadGen.GetDefault() with
              mode = MixedClassicSoroban
              spikesize = context.spikeSize
              spikeinterval = context.spikeInterval
              offset = 0
              maxfeerate = None
              skiplowfeetxs = false

              wasms = context.numWasms
              instances = context.numInstances

              payWeight = Some(baseContext.payWeight |> Option.defaultValue 50)
              sorobanUploadWeight = Some(baseContext.sorobanUploadWeight |> Option.defaultValue 5)
              sorobanInvokeWeight = Some(baseContext.sorobanInvokeWeight |> Option.defaultValue 45)

              minSorobanPercentSuccess = Some(baseContext.minSorobanPercentSuccess |> Option.defaultValue 60) }

    let invokeSetupCfg = { baseLoadGen with mode = SorobanInvokeSetup }

    minBlockTimeTest context baseLoadGen (Some invokeSetupCfg)
