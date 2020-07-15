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

let simulatePubnet (context : MissionContext) =
    let fullCoreSet = FullPubnetCoreSets context.image

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
        // Wait for network setup first, to fail as soon as
        // possible in case of a misconfiguration
        formation.WaitUntilSynced fullCoreSet
        formation.InstallNetworkDelays fullCoreSet
        formation.UpgradeProtocolToLatest tier1
        formation.UpgradeMaxTxSize tier1 1000000

        formation.RunLoadgen sdf context.GenerateAccountCreationLoad
        formation.RunLoadgen sdf context.GeneratePaymentLoad
    )
