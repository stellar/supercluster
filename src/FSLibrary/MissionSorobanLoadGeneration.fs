// Copyright 2023 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanLoadGeneration

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarNetworkData
open StellarSupercluster
open StellarCoreHTTP


let sorobanLoadGeneration (context: MissionContext) =
    let rate = 5

    let context =
        { context with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(context.installNetworkDelay |> Option.defaultValue true)
              txRate = rate
              numAccounts = 10000
              numTxs = rate * 2000
              skipLowFeeTxs = true
              maxFeeRate = Some 100000000 }

    let fullCoreSet = FullPubnetCoreSets context true true

    let sdf =
        List.find (fun (cs: CoreSet) -> cs.name.StringName = "stellar" || cs.name.StringName = "sdf") fullCoreSet

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) fullCoreSet

    context.Execute
        fullCoreSet
        None
        (fun (formation: StellarFormation) ->
            // Setup overlay connections first before manually closing
            // ledger, which kick off consensus
            formation.WaitUntilConnected fullCoreSet
            formation.ManualClose tier1

            // Wait until the whole network is synced before proceeding,
            // to fail asap in case of a misconfiguration
            formation.WaitUntilSynced fullCoreSet
            formation.UpgradeProtocolToLatest tier1
            formation.UpgradeMaxTxSetSize tier1 1000000

            formation.RunLoadgen sdf context.GenerateAccountCreationLoad
            formation.UpgradeSorobanLedgerLimitsWithMultiplier tier1 100
            formation.RunLoadgen sdf context.GenerateSorobanUploadLoad
            formation.EnsureAllNodesInSync fullCoreSet)
