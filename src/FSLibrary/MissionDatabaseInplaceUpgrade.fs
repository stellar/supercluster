// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionDatabaseInplaceUpgrade

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarDataDump
open StellarSupercluster

let databaseInplaceUpgrade (context: MissionContext) =
    let context =
        { context.WithNominalLoad with
              genesisTestAccountCount = Some context.WithNominalLoad.numAccounts }

    let newImage = context.image
    let oldImage = GetOrDefault context.oldImage context.image

    let quorumSet = CoreSetQuorum(CoreSetName("core"))

    let coreSet =
        MakeLiveCoreSet "core" { CoreSetOptions.GetDefault newImage with quorumSet = quorumSet }

    let beforeUpgradeCoreSet =
        MakeLiveCoreSet
            "before-upgrade"
            { CoreSetOptions.GetDefault oldImage with
                  nodeCount = 1
                  quorumSet = quorumSet
                  // This core set is not in the quorum set, so it is not needed to
                  // bootstrap the network. Joining via consensus (rather than
                  // force-SCP) keeps it from falsely reporting "Synced!" at
                  // ledger 1 when pod scheduling delays let the core nodes get
                  // ahead of it.
                  initialization = CoreSetInitialization.DefaultNoForceSCP
                  // FIXME: Remove these options once the stable (old) image in
                  // CI supports skipping validator quality checks
                  skipHighCriticalValidatorChecks = false
                  quorumSetConfigType = RequireExplicitQset }

    let fetchFromPeer = Some(CoreSetName("before-upgrade"), 0)

    let afterUpgradeCoreSet =
        MakeDeferredCoreSet
            "after-upgrade"
            { CoreSetOptions.GetDefault newImage with
                  nodeCount = 1
                  quorumSet = quorumSet
                  localHistory = false
                  initialization =
                      { newDb = false
                        newHist = false
                        initialCatchup = false
                        waitForConsensus = true
                        fetchDBFromPeer = fetchFromPeer
                        pregenerateTxs = None } }

    context.Execute
        [ beforeUpgradeCoreSet; coreSet; afterUpgradeCoreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            let peer = formation.NetworkCfg.GetPeer beforeUpgradeCoreSet 0
            let version = peer.GetSupportedProtocolVersion()
            formation.UpgradeProtocol [ coreSet ] version

            formation.WaitUntilSynced [ beforeUpgradeCoreSet ]

            formation.RunLoadgen beforeUpgradeCoreSet context.GeneratePaymentLoad
            formation.RunLoadgen coreSet context.GeneratePaymentLoad

            formation.BackupDatabaseToHistory peer
            formation.Start afterUpgradeCoreSet.name

            let afterUpgradeCoreSetLive = formation.NetworkCfg.FindCoreSet afterUpgradeCoreSet.name
            formation.WaitUntilSynced [ afterUpgradeCoreSetLive ]

            // The upgraded node is a watcher outside the quorum, so the
            // validators keep closing ledgers while it catches up and it can
            // report "Synced!" while still many ledgers behind them. Running
            // loadgen against it in that state fails: loadgen verifies its
            // transactions against the local ledger, which stalls until the
            // node's next catchup round. Wait for it to reach the validators'
            // current ledger before generating load on it.
            let upgradedPeer = formation.NetworkCfg.GetPeer afterUpgradeCoreSetLive 0
            upgradedPeer.WaitUntilCaughtUpWith(formation.NetworkCfg.GetPeer coreSet 0)

            formation.RunLoadgen afterUpgradeCoreSet context.GeneratePaymentLoad
            formation.RunLoadgen coreSet context.GeneratePaymentLoad)
