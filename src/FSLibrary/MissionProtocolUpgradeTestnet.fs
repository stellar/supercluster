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

let protocolUpgradeTestnet (context : MissionContext) =
    let set = { CoreSetOptions.Default with
                  quorumSet = Some(["core"])
                  historyNodes = Some([])
                  historyGetCommands = TestnetGetCommands
                  catchupMode = CatchupRecent(0)
                  initialization = { CoreSetInitialization.Default with initialCatchup = true } }

    let coreSet = MakeCoreSet "core" 1 1 set
    context.Execute [coreSet] (Some(SDFTestNet)) (fun f ->
        f.WaitUntilSynced [coreSet]

        let peer = f.NetworkCfg.GetPeer coreSet 0
        peer.WaitForNextLedger()
        peer.WaitForNextLedger()
        peer.WaitForNextLedger()
        let upgrades = { DefaultUpgradeParameters with protocolVersion = Some(peer.GetProtocolVersion) }
        peer.SetUpgrades(upgrades)
        peer.WaitForNextLedger()
        peer.WaitForNextLedger()
        peer.WaitForNextLedger()

        f.CheckUsesLatestProtocolVersion
    )
