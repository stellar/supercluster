// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSimulatePubnet

// The point of this mission is to simulate pubnet as closely as possible,
// for evaluating the likely effect of a change to core when deployed.

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarNetworkData
open StellarSupercluster
open StellarCoreHTTP

let simulatePubnet (context : MissionContext) =
    let context = { context with coreResources = SimulatePubnetResources }
    let networkSizeLimit = 100
    let fullCoreSet = FullPubnetCoreSets context.image true networkSizeLimit
    let sdf = List.find (fun (cs:CoreSet) -> cs.name.StringName = "www-stellar-org") fullCoreSet
    let tier1 = List.filter (fun (cs:CoreSet) -> cs.options.tier1 = Some true) fullCoreSet

    context.Execute fullCoreSet None (fun (formation: StellarFormation) ->
        // Setup overlay connections first before manually closing
        // ledger, which kick off consensus
        formation.WaitUntilConnected fullCoreSet
        formation.ManualClose tier1

        // Wait until the whole network is synced before proceeding,
        // to fail asap in case of a misconfiguration
        formation.WaitUntilSynced fullCoreSet
        formation.UpgradeProtocolToLatest tier1
        formation.UpgradeMaxTxSize tier1 1000000

        formation.RunLoadgen sdf context.GenerateAccountCreationLoad
        formation.RunLoadgen sdf context.GeneratePaymentLoad
    )
