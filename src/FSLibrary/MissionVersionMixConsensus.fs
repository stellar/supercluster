// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionVersionMixConsensus

open StellarCoreCfg
open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarTransaction
open StellarFormation

let versionMixConsensus (context : MissionContext) =
    let newImage = context.image
    let oldImage = GetOrDefault context.oldImage context.image

    let oldCoreSet = MakeLiveCoreSet "old-core" { CoreSetOptions.GetDefault oldImage with
                                                      nodeCount = 4
                                                      quorumSet = AllPeersQuorum }
    let newCoreSet = MakeLiveCoreSet "new-core" { CoreSetOptions.GetDefault newImage with
                                                      nodeCount = 2
                                                      quorumSet = AllPeersQuorum }
    context.Execute [oldCoreSet; newCoreSet] None (fun (formation: StellarFormation) ->
        formation.WaitUntilSynced [oldCoreSet; newCoreSet]
        let peer = formation.NetworkCfg.GetPeer oldCoreSet 0
        let version = peer.GetSupportedProtocolVersion()
        formation.UpgradeProtocol [oldCoreSet; newCoreSet] version

        formation.CreateAccount oldCoreSet UserAlice
        formation.CreateAccount oldCoreSet UserBob
        formation.Pay oldCoreSet UserAlice UserBob
        formation.Pay newCoreSet UserAlice UserBob
    )
