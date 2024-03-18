// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionMaxTPSMixed

// This module provides a max TPS test with a blend of classic and Soroban load.
// It uses the MaxTPSTest module to perform a binary search for the max TPS.

open MaxTPSTest
open StellarMissionContext
open StellarCoreHTTP

let maxTPSMixed (baseContext: MissionContext) =
    let context =
        { baseContext with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(baseContext.installNetworkDelay |> Option.defaultValue true)
              // No additional DB overhead unless specified (this will measure the in-memory SQLite DB only)
              simulateApplyDuration = Some(baseContext.simulateApplyDuration |> Option.defaultValue (seq { 0 }))
              simulateApplyWeight = Some(baseContext.simulateApplyWeight |> Option.defaultValue (seq { 100 }))
              enableTailLogging = false
              // Setup distributions based on testnet data
              wasmBytesDistribution = [ (8 * 1024, 132); (24 * 1024, 68); (40 * 1024, 92); (56 * 1024, 141) ]

              // NOTE: `dataEntriesDistribution` and
              // `totalKiloBytesDistribution` are skewed a bit so that in most
              // cases there are more kilobytes of I/O than data entries. This
              // is to avoid a problem in which loadgen rounds the size of each
              // data entry up (from 0kb to 1kb) and underestimates the
              // resources required for the write. This will be fixed as part of
              // stellar-core issue #4231.
              dataEntriesDistribution = [ (2, 380); (9, 42); (15, 5); (21, 2) ]
              totalKiloBytesDistribution = [ (3, 427); (5, 2) ]

              txSizeBytesDistribution = [ (200, 37); (400, 6); (600, 1); (800, 4); (1000, 1) ]
              instructionsDistribution = [ (12500000, 201); (37500000, 183); (62500000, 34); (87500000, 11) ] }

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

              // Require 80% of Soroban transactions to successfully apply by
              // default
              minSorobanPercentSuccess = Some(baseContext.minSorobanPercentSuccess |> Option.defaultValue 80) }

    let invokeSetupCfg = { baseLoadGen with mode = SorobanInvokeSetup }

    maxTPSTest context baseLoadGen (Some invokeSetupCfg) true
