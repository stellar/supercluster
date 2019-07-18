// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionCatchupHelpers

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarSupercluster

let catchupSets generatorImage catchupImage =
    let generatorOptions = { CoreSetOptions.Default with quorumSet = Some(["generator"]); accelerateTime = true; image = Some(generatorImage) }
    let genratorSet = MakeCoreSet "generator" 1 1 generatorOptions

    let newNodeOptions = { CoreSetOptions.Default with quorumSet = Some(["generator"]); accelerateTime = true; image = Some(catchupImage) }
    let minimal1Options =  { newNodeOptions with catchupMode = CatchupRecent(0) }
    let minimal1Set = MakeCoreSet "minimal1" 0 1 minimal1Options

    let complete1Options =  { newNodeOptions with catchupMode = CatchupComplete }
    let complete1Set = MakeCoreSet "complete1" 0 1 complete1Options

    let recent1Options =  { newNodeOptions with catchupMode = CatchupRecent(75) }
    let recent1Set = MakeCoreSet "recent1" 0 1 recent1Options

    // minimal => minimal cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let minimal2Options = { minimal1Options with quorumSet = Some(["minimal1"]); image = Some(catchupImage) }
    let minimal2Set = MakeCoreSet "minimal2" 0 1 minimal2Options

    // complete => complete cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let complete2Options = { complete1Options with quorumSet = Some(["complete1"]); image = Some(catchupImage) }
    let complete2Set = MakeCoreSet "complete2" 0 1 complete2Options

    // recent => recent cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let recent2Options = { recent1Options with quorumSet = Some(["recent1"]); image = Some(catchupImage) }
    let recent2Set = MakeCoreSet "recent2" 0 1 recent2Options

    // complete => minimal cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let minimal3Options = { minimal1Options with quorumSet = Some(["complete1"]); image = Some(catchupImage) }
    let minimal3Set = MakeCoreSet "minimal3" 0 1 minimal3Options

    // complete => recent cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let recent3Options = { recent1Options with quorumSet = Some(["complete1"]); image = Some(catchupImage) }
    let recent3Set = MakeCoreSet "recent3" 0 1 recent3Options

    let deferedSets = [minimal1Set
                       complete1Set
                       recent1Set
                       minimal2Set
                       complete2Set
                       recent2Set
                       minimal3Set
                       recent3Set ]

    (genratorSet, deferedSets)

let doCatchup (context: MissionContext) (f: ClusterFormation) generatorSet deferedSets version =
    f.RunLoadgen generatorSet context.GenerateAccountCreationLoad
    f.RunLoadgen generatorSet context.GeneratePaymentLoad

    let generatorPeer = f.NetworkCfg.GetPeer generatorSet 0
    let upgrades = { DefaultUpgradeParameters with
                       maxTxSize = Some(1000000);
                       protocolVersion = version }
    generatorPeer.SetUpgrades(upgrades) // upgrade protocol
    generatorPeer.WaitForNextLedger()
    generatorPeer.SetUpgrades(upgrades) // upgrade maxTxSize properly

    generatorPeer.WaitForLedgerNum 32

    for set in deferedSets do
        f.ChangeCount set.name 1
        let peer = f.NetworkCfg.GetPeer set 0
        peer.WaitForLedgerNum 40
