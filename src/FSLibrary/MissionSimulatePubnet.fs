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
open StellarNetworkDelays
open StellarSupercluster
open StellarCoreHTTP

let simulatePubnet (context : MissionContext) =
    let context = { context with coreResources = SimulatePubnetResources }
    let fullCoreSet = FullPubnetCoreSets context.image true

    let findCoreSetWithHomeDomain (domain:string) : CoreSet =
        List.find (fun (cs:CoreSet) -> cs.name.StringName = domain) fullCoreSet

    let sdf = findCoreSetWithHomeDomain "www-stellar-org"
    let lobstr = findCoreSetWithHomeDomain "lobstr-co"
    let keybase = findCoreSetWithHomeDomain "keybase-io"
    let satoshipay = findCoreSetWithHomeDomain "satoshipay-io"
    let wirex = findCoreSetWithHomeDomain "wirexapp-com"
    let blockdaemon = findCoreSetWithHomeDomain "stellar-blockdaemon-com"
    let coinqvest = findCoreSetWithHomeDomain "coinqvest-com"

    let tier1 = [sdf; lobstr; keybase; satoshipay; wirex; blockdaemon; coinqvest]

    context.Execute fullCoreSet None (fun (formation: StellarFormation) ->
        // Setup overlay connections first before manually closing
        // ledger, which kick off consensus
        formation.WaitUntilConnected fullCoreSet 16
        formation.ManualClose tier1

        // Wait until the whole network is synced before proceeding,
        // to fail asap in case of a misconfiguration
        formation.WaitUntilSynced fullCoreSet
        formation.InstallNetworkDelays fullCoreSet
        formation.UpgradeProtocolToLatest tier1
        formation.UpgradeMaxTxSize tier1 1000000

        formation.RunLoadgen sdf context.GenerateAccountCreationLoad
        formation.RunLoadgen sdf context.GeneratePaymentLoad
    )
