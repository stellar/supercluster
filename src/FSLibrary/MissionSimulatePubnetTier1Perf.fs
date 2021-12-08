// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSimulatePubnetTier1Perf

// The point of this mission is to simulate the tier1 group of pubnet
// for purposes of repeatable / comparable performance evaluation.

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarNetworkData
open StellarSupercluster
open StellarCoreHTTP

let simulatePubnetTier1Perf (context: MissionContext) =
    let context = { context with coreResources = SimulatePubnetTier1PerfResources }

    let tier1 = StableApproximateTier1CoreSets context.image
    let sdf = List.find (fun (cs: CoreSet) -> cs.name.StringName = "sdf") tier1

    context.Execute
        tier1
        None
        (fun (formation: StellarFormation) ->
            // Setup overlay connections first before manually closing
            // ledger, which kick off consensus
            formation.WaitUntilConnected tier1
            formation.ManualClose tier1

            // Wait until the whole network is synced before proceeding,
            // to fail asap in case of a misconfiguration
            formation.WaitUntilSynced tier1
            formation.UpgradeProtocolToLatest tier1

            // Set max tx size to 10x the rate -- at 5x we overflow too often
            // and lose sync.
            formation.UpgradeMaxTxSize tier1 (10 * context.maxTxRate)

            formation.RunLoadgen sdf context.GenerateAccountCreationLoad
            formation.RunLoadgen sdf context.GeneratePaymentLoad
            formation.EnsureAllNodesInSync tier1)
