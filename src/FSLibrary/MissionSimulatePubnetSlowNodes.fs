// Copyright 2023 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSimulatePubnetSlowNodes

// The point of this mission is to simulate pubnet as closely as possible,
// for evaluating the likely effect of a change to core when deployed.
// some nodes are artificially "slow"

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarNetworkData
open StellarSupercluster
open StellarCoreHTTP
open Logging

let simulatePubnetSlowNodes (context: MissionContext) =
    let context =
        { context with
              coreResources = SimulatePubnetResources context.networkSizeLimit
              simulateApplyDuration =
                  Some(
                      context.simulateApplyDuration
                      |> Option.defaultValue (simulateApplyDurationDefault)
                  )
              simulateApplyWeight =
                  Some(context.simulateApplyWeight |> Option.defaultValue (simulateApplyWeightDefault))
              installNetworkDelay = Some(context.installNetworkDelay |> Option.defaultValue true)
              numAccounts = 10000
              txRate = 200
              sleepMainThread = Some(context.sleepMainThread |> Option.defaultValue 50) }

    // One tier1 org is "slow", i.e. we add artificial delay to
    let delay = if context.sleepMainThread.IsSome then context.sleepMainThread.Value else 0
    let fullCoreSet = FullPubnetCoreSets context true true delay

    let sp = List.find (fun (cs: CoreSet) -> cs.name.StringName = "satoshipay") fullCoreSet

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) fullCoreSet

    let slowCoreSet =
        List.filter
            (fun (cs: CoreSet) -> cs.options.addArtificalDelay.IsSome && cs.options.addArtificalDelay.Value > 0)
            tier1

    assert (slowCoreSet.Length <> 0)

    List.map
        (fun (cs: CoreSet) -> LogInfo "Adding artificial delay %i to CoreSet %s" delay cs.name.StringName)
        slowCoreSet
    |> ignore

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
            formation.UpgradeMaxTxSetSize tier1 (context.txRate * 20)

            formation.RunLoadgen sp context.GenerateAccountCreationLoad
            formation.RunLoadgen sp context.GeneratePretendLoad
            formation.RunLoadgen sp { context.GenerateSorobanUploadLoad with txrate = 2; txs = 2000 })
