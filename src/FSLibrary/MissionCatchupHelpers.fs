// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionCatchupHelpers

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open Logging

type CatchupMissionOptions = { generatorImage: string; catchupImage: string; versionImage: string }

type CatchupSets =
    { generatorSet: CoreSet
      deferredSetList: CoreSet list
      versionSet: CoreSet }

    member self.AllSetList() =
        List.concat [ [ self.generatorSet ]
                      self.deferredSetList
                      [ self.versionSet ] ]

let MakeRecentCatchupSet (options: CatchupMissionOptions) =
    let generatorOptions =
        { CoreSetOptions.GetDefault options.generatorImage with
              nodeCount = 1
              quorumSet = CoreSetQuorum(CoreSetName "generator")
              accelerateTime = true }

    let generatorSet = MakeLiveCoreSet "generator" generatorOptions

    let recentOptions =
        { CoreSetOptions.GetDefault options.catchupImage with
              catchupMode = CatchupRecent(5)
              nodeCount = 1
              quorumSet = CoreSetQuorum(CoreSetName "generator")
              accelerateTime = true
              initialization = CoreSetInitialization.DefaultNoForceSCP }

    let recentSet = MakeDeferredCoreSet "recent1" recentOptions

    let versionOptions =
        { CoreSetOptions.GetDefault options.versionImage with
              nodeCount = 1
              quorumSet = CoreSetQuorum(CoreSetName "version")
              accelerateTime = true }

    let versionSet = MakeLiveCoreSet "version" versionOptions

    { generatorSet = generatorSet
      deferredSetList = [ recentSet ]
      versionSet = versionSet }

let MakeCatchupSets (options: CatchupMissionOptions) =
    let generatorOptions =
        { CoreSetOptions.GetDefault options.generatorImage with
              nodeCount = 1
              quorumSet = CoreSetQuorum(CoreSetName "generator")
              accelerateTime = true }

    let generatorSet = MakeLiveCoreSet "generator" generatorOptions

    let newNodeOptions =
        { CoreSetOptions.GetDefault options.catchupImage with
              nodeCount = 1
              quorumSet = CoreSetQuorum(CoreSetName "generator")
              accelerateTime = true
              initialization = CoreSetInitialization.DefaultNoForceSCP }

    let minimal1Options = { newNodeOptions with catchupMode = CatchupRecent(0) }
    let minimal1Set = MakeDeferredCoreSet "minimal1" minimal1Options

    let complete1Options = { newNodeOptions with catchupMode = CatchupComplete }
    let complete1Set = MakeDeferredCoreSet "complete1" complete1Options

    let recent1Options = { newNodeOptions with catchupMode = CatchupRecent(75) }
    let recent1Set = MakeDeferredCoreSet "recent1" recent1Options

    // minimal => minimal cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let minimal2Options =
        { minimal1Options with
              quorumSet = CoreSetQuorum(CoreSetName "minimal1")
              image = options.catchupImage }

    let minimal2Set = MakeDeferredCoreSet "minimal2" minimal2Options

    // complete => complete cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let complete2Options =
        { complete1Options with
              quorumSet = CoreSetQuorum(CoreSetName "complete1")
              image = options.catchupImage }

    let complete2Set = MakeDeferredCoreSet "complete2" complete2Options

    // recent => recent cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let recent2Options =
        { recent1Options with
              quorumSet = CoreSetQuorum(CoreSetName "recent1")
              image = options.catchupImage }

    let recent2Set = MakeDeferredCoreSet "recent2" recent2Options

    // complete => minimal cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let minimal3Options =
        { minimal1Options with
              quorumSet = CoreSetQuorum(CoreSetName "complete1")
              image = options.catchupImage }

    let minimal3Set = MakeDeferredCoreSet "minimal3" minimal3Options

    // complete => recent cascade step: make sure that you can catch up to a node that was, itself, created by catchup.
    let recent3Options =
        { recent1Options with
              quorumSet = CoreSetQuorum(CoreSetName "complete1")
              image = options.catchupImage }

    let recent3Set = MakeDeferredCoreSet "recent3" recent3Options

    let versionOptions =
        { CoreSetOptions.GetDefault options.versionImage with
              nodeCount = 1
              quorumSet = CoreSetQuorum(CoreSetName "version")
              accelerateTime = true }

    let versionSet = MakeLiveCoreSet "version" versionOptions

    { generatorSet = generatorSet
      deferredSetList =
          [ minimal1Set
            complete1Set
            recent1Set
            minimal2Set
            complete2Set
            recent2Set
            minimal3Set
            recent3Set ]
      versionSet = versionSet }

let doCatchupForVersion
    (context: MissionContext)
    (formation: StellarFormation)
    (catchupSets: CatchupSets)
    (version: int)
    =
    formation.Stop(CoreSetName "version")

    LogInfo "Upgrading generator peer to protocol version %d " version

    let generatorPeer = formation.NetworkCfg.GetPeer catchupSets.generatorSet 0
    generatorPeer.UpgradeProtocol version System.DateTime.UtcNow

    if context.numTxs > 100 then
        generatorPeer.UpgradeMaxTxSize 1000000 System.DateTime.UtcNow

    formation.RunLoadgen catchupSets.generatorSet context.GenerateAccountCreationLoad
    formation.RunLoadgen catchupSets.generatorSet context.GeneratePaymentLoad
    generatorPeer.WaitForFewLedgers(5)

    for set in catchupSets.deferredSetList do
        formation.Start set.name
        let peer = formation.NetworkCfg.GetPeer set 0
        peer.WaitForFewLedgers(5)

let doCatchup (context: MissionContext) (formation: StellarFormation) (catchupSets: CatchupSets) =
    let versionPeer = formation.NetworkCfg.GetPeer catchupSets.versionSet 0
    let version = versionPeer.GetSupportedProtocolVersion()
    doCatchupForVersion context formation catchupSets version


let getCatchupRanges (ledgersPerJob: int) (startingLedger: int) (latestLedgerNum: int) (overlapLedgers: int) =
    // we require some overlap
    assert (overlapLedgers > 0)

    let mutable endRange = latestLedgerNum
    let mutable ranges = [||]

    while endRange > startingLedger do
        let range = sprintf "%d/%d" (endRange) (ledgersPerJob + overlapLedgers)
        ranges <- Array.append [| [| "catchup"; range |] |] ranges
        endRange <- endRange - ledgersPerJob

    ranges
