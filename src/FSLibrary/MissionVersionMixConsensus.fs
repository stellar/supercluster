// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionVersionMixConsensus

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarTransaction
open StellarFormation
open StellarStatefulSets
open StellarDataDump
open StellarSupercluster

let versionMixConsensus (context: MissionContext) =
    let context = context.WithNominalLoad
    let newImage = context.image
    let oldImage = GetOrDefault context.oldImage context.image

    let beforeSet =
        MakeLiveCoreSet
            "before"
            { CoreSetOptions.GetDefault oldImage with
                  nodeCount = 2
                  quorumSet = CoreSetQuorum(CoreSetName "before") }

    let fetchFromPeer = Some(CoreSetName("before"), 0)

    let newCoreSet =
        MakeDeferredCoreSet
            "new-core"
            { CoreSetOptions.GetDefault newImage with
                  nodeCount = 2
                  historyNodes = Some([])
                  quorumSet = CoreSetQuorumList([ CoreSetName "new-core"; CoreSetName "old-core" ])
                  initialization =
                      { newDb = false
                        newHist = false
                        initialCatchup = false
                        waitForConsensus = false
                        fetchDBFromPeer = fetchFromPeer } }

    let oldCoreSet =
        MakeDeferredCoreSet
            "old-core"
            { CoreSetOptions.GetDefault oldImage with
                  nodeCount = 2
                  historyNodes = Some([])
                  quorumSet = CoreSetQuorumList([ CoreSetName "new-core"; CoreSetName "old-core" ])
                  initialization =
                      { newDb = false
                        newHist = false
                        initialCatchup = false
                        waitForConsensus = false
                        fetchDBFromPeer = fetchFromPeer } }

    context.Execute
        [ beforeSet; newCoreSet; oldCoreSet ]
        None
        (fun (formation: StellarFormation) ->
            // Upgrade the network to the old protocol
            formation.WaitUntilSynced [ beforeSet ]
            let peer = formation.NetworkCfg.GetPeer beforeSet 0
            let version = peer.GetSupportedProtocolVersion()
            formation.UpgradeProtocol [ beforeSet ] version

            // Close a few ledgers, then save the network state
            peer.WaitForFewLedgers 2
            formation.BackupDatabaseToHistory peer

            // Start nodes with old and new image, and wait for everyone
            // to get in sync
            formation.Start newCoreSet.name
            formation.Start oldCoreSet.name

            let newCoreSetLive = formation.NetworkCfg.FindCoreSet newCoreSet.name
            let oldCoreSetLive = formation.NetworkCfg.FindCoreSet oldCoreSet.name

            formation.WaitUntilSynced [ oldCoreSetLive
                                        newCoreSetLive ]

            formation.Stop beforeSet.name

            formation.RunLoadgen oldCoreSetLive context.GenerateAccountCreationLoad
            formation.RunLoadgen oldCoreSetLive context.GeneratePaymentLoad

            let otherPeer = formation.NetworkCfg.GetPeer oldCoreSet 0
            otherPeer.WaitForFewLedgers 20)
