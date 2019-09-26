// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionCatchupHelpers

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarSupercluster

type CatchupMissionOptions =
    { generatorImage : string
      catchupImage : string
      versionImage : string }

type CatchupSets =
    { generatorSet : CoreSet
      deferredSetList : CoreSet list
      versionSet : CoreSet }
     
    member self.AllSetList () = 
        List.concat [[self.generatorSet]; self.deferredSetList; [self.versionSet]]

let MakeCatchupSets (options: CatchupMissionOptions) =
    let generatorOptions = { CoreSetOptions.Default with nodeCount = 1; quorumSet = Some(["generator"]); accelerateTime = true; image = Some(options.generatorImage) }
    let generatorSet = MakeLiveCoreSet "generator" generatorOptions

    let newNodeOptions = { CoreSetOptions.Default with nodeCount = 1; quorumSet = Some(["generator"]); accelerateTime = true; image = Some(options.catchupImage) }
    let minimal1Options =  { newNodeOptions with catchupMode = CatchupRecent(0) }
    let minimal1Set = MakeDeferredCoreSet "minimal1" minimal1Options

    let complete1Options =  { newNodeOptions with catchupMode = CatchupComplete }
    let complete1Set = MakeDeferredCoreSet "complete1" complete1Options

    let recent1Options =  { newNodeOptions with catchupMode = CatchupRecent(75) }
    let recent1Set = MakeDeferredCoreSet "recent1" recent1Options

    // minimal => minimal cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let minimal2Options = { minimal1Options with quorumSet = Some(["minimal1"]); image = Some(options.catchupImage) }
    let minimal2Set = MakeDeferredCoreSet "minimal2" minimal2Options

    // complete => complete cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let complete2Options = { complete1Options with quorumSet = Some(["complete1"]); image = Some(options.catchupImage) }
    let complete2Set = MakeDeferredCoreSet "complete2" complete2Options

    // recent => recent cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let recent2Options = { recent1Options with quorumSet = Some(["recent1"]); image = Some(options.catchupImage) }
    let recent2Set = MakeDeferredCoreSet "recent2" recent2Options

    // complete => minimal cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let minimal3Options = { minimal1Options with quorumSet = Some(["complete1"]); image = Some(options.catchupImage) }
    let minimal3Set = MakeDeferredCoreSet "minimal3" minimal3Options

    // complete => recent cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let recent3Options = { recent1Options with quorumSet = Some(["complete1"]); image = Some(options.catchupImage) }
    let recent3Set = MakeDeferredCoreSet "recent3" recent3Options

    let versionOptions = { CoreSetOptions.Default with nodeCount = 1; quorumSet = Some(["version"]); accelerateTime = true; image = Some(options.versionImage) }
    let versionSet = MakeLiveCoreSet "version" versionOptions

    { generatorSet = generatorSet
      deferredSetList = [minimal1Set
                         complete1Set
                         recent1Set
                         minimal2Set
                         complete2Set
                         recent2Set
                         minimal3Set
                         recent3Set ]
      versionSet = versionSet}

let doCatchup (context: MissionContext) (formation: ClusterFormation) (catchupSets: CatchupSets) =
    let versionPeer = formation.NetworkCfg.GetPeer catchupSets.versionSet 0
    let version = versionPeer.GetProtocolVersion()

    let generatorPeer = formation.NetworkCfg.GetPeer catchupSets.generatorSet 0
    generatorPeer.UpgradeProtocol(version)
    if context.numTxs > 100
    then generatorPeer.UpgradeMaxTxSize(1000000)

    formation.RunLoadgen catchupSets.generatorSet context.GenerateAccountCreationLoad
    formation.RunLoadgen catchupSets.generatorSet context.GeneratePaymentLoad
    generatorPeer.WaitForLedgerNum 32

    for set in catchupSets.deferredSetList do
        formation.Start set.name
        let peer = formation.NetworkCfg.GetPeer set 0
        peer.WaitForLedgerNum 40
