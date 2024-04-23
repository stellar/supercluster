// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSorobanCatchupWithPrevAndCurr

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarDataDump
open StellarSupercluster

let sorobanCatchupWithPrevAndCurr (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  emptyDirType = DiskBackedEmptyDir }

    let quorumSet = CoreSetQuorum(CoreSetName("core"))

    // This coreset has catchup complete
    let afterUpgradeCoreSet =
        MakeDeferredCoreSet
            "after-upgrade"
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = 1
                  quorumSet = quorumSet
                  catchupMode = CatchupComplete }

    let context =
        { context.WithMediumLoadgenOptions with
              numAccounts = 100
              numTxs = 100
              txRate = 1
              coreResources = MediumTestResources }

    context.Execute
        [ coreSet; afterUpgradeCoreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]

            let supportedProtocol = (formation.NetworkCfg.GetPeer coreSet 0).GetSupportedProtocolVersion()

            // We want to test against a protocol boundary that has Soroban enabled on both sides,
            // so the image must be on the protocol version after Soroban was introduced.
            // This check can be removed once we're on v21 for good and not just on vnext.
            if supportedProtocol >= 21 then
                formation.UpgradeProtocol [ coreSet ] (supportedProtocol - 1)
                formation.UpgradeMaxTxSetSize [ coreSet ] 100000

                formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
                formation.UpgradeSorobanLedgerLimitsWithMultiplier [ coreSet ] 1000
                formation.UpgradeSorobanTxLimitsWithMultiplier [ coreSet ] 100
                formation.RunLoadgen coreSet context.SetupSorobanInvoke
                formation.RunLoadgen coreSet context.GenerateSorobanInvokeLoad

                formation.UpgradeProtocol [ coreSet ] (supportedProtocol)

                formation.RunLoadgen coreSet context.SetupSorobanInvoke
                formation.RunLoadgen coreSet context.GenerateSorobanInvokeLoad

                // Start the second coreset that will replay all ledgers
                formation.Start afterUpgradeCoreSet.name

                let afterUpgradeCoreSetLive = formation.NetworkCfg.FindCoreSet afterUpgradeCoreSet.name
                formation.WaitUntilSynced [ afterUpgradeCoreSetLive ]

                let peer = formation.NetworkCfg.GetPeer coreSet 0
                let ledgerNum = peer.GetLedgerNum()

                let catchupPeer = formation.NetworkCfg.GetPeer afterUpgradeCoreSetLive 0
                catchupPeer.WaitForLedgerNum(ledgerNum + 64))
