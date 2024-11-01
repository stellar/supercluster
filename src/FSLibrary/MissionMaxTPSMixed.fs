// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionMaxTPSMixed

// This module provides a max TPS test with a blend of classic and Soroban load.
// It uses the MaxTPSTest module to perform a binary search for the max TPS.

open MaxTPSTest
open PubnetData
open StellarMissionContext
open StellarCoreHTTP

let maxTPSMixed (baseContext: MissionContext) =
    let context =
        { baseContext with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(baseContext.installNetworkDelay |> Option.defaultValue true)
              enableTailLogging = false
              // Setup distributions based on pubnet data
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

              // Blend settings. 50% classic, 5% upload, 45% invoke by default
              payWeight = Some(baseContext.payWeight |> Option.defaultValue 50)
              sorobanUploadWeight = Some(baseContext.sorobanUploadWeight |> Option.defaultValue 5)
              sorobanInvokeWeight = Some(baseContext.sorobanInvokeWeight |> Option.defaultValue 45)

              // Require a majority of Soroban transactions to succeed.
              minSorobanPercentSuccess = Some(baseContext.minSorobanPercentSuccess |> Option.defaultValue 60) }

    let invokeSetupCfg = { baseLoadGen with mode = SorobanInvokeSetup }

    maxTPSTest context baseLoadGen (Some invokeSetupCfg)
