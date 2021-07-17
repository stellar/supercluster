// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionProtocolUpgradeTestnet

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open StellarStatefulSets
open StellarSupercluster

let protocolUpgradeTestnet (context: MissionContext) =
    let context = { context with coreResources = UpgradeResources }

    let set =
        { CoreSetOptions.GetDefault context.image with
              nodeCount = 1
              quorumSet = CoreSetQuorum(CoreSetName "core")
              historyNodes = Some([])
              historyGetCommands = TestnetGetCommands
              catchupMode = CatchupRecent(0)
              initialization = { CoreSetInitialization.Default with initialCatchup = true } }

    let coreSet = MakeLiveCoreSet "core" set

    context.Execute
        [ coreSet ]
        (Some(SDFTestNet))
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]

            let peer = formation.NetworkCfg.GetPeer coreSet 0
            peer.WaitForFewLedgers(3)
            peer.UpgradeProtocolToLatest System.DateTime.UtcNow
            peer.WaitForFewLedgers(3)

            formation.CheckUsesLatestProtocolVersion())
