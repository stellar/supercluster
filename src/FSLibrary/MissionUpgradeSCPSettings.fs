// Copyright 2025 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionUpgradeSCPSettings

open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP
open StellarNetworkData
open System.Threading
open Logging

let upgradeSCPSettings (context: MissionContext) =
    let context =
        { context with
              coreResources = SimulatePubnetTier1PerfResources
              installNetworkDelay = Some(context.installNetworkDelay |> Option.defaultValue true)
              numAccounts = 1000
              numTxs = 1000
              txRate = 50 }

    let fullCoreSet = StableApproximateTier1CoreSets context.image false

    let sdf =
        List.find (fun (cs: CoreSet) -> cs.name.StringName = "stellar" || cs.name.StringName = "sdf") fullCoreSet

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) fullCoreSet

    // This tests the following SCP timings and upgrades:
    // Protocol 22 sanity check: make sure block times are 5 seconds
    // Upgrade to protocol 23: block times stay the same after upgrade
    // Upgrade to minimum SCP timing configuration: check for 4 second block times
    context.Execute
        fullCoreSet
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilConnected fullCoreSet
            formation.ManualClose tier1
            formation.WaitUntilSynced fullCoreSet

            let peer = formation.NetworkCfg.GetPeer sdf 0

            // Helper function to measure average block close time
            let measureBlockTime (expectedSeconds: float) =
                let beforeLedgerNum = peer.GetLedgerNum()
                let beforeCloseTime = peer.GetInfo().Ledger.CloseTime

                // Generate some load so we're not just closing empty ledgers
                formation.RunLoadgen sdf context.GeneratePaymentLoad

                let afterLedgerNum = peer.GetLedgerNum()
                let afterCloseTime = peer.GetInfo().Ledger.CloseTime
                let ledgersClosed = afterLedgerNum - beforeLedgerNum
                let timeElapsed = afterCloseTime - beforeCloseTime
                let avgCloseTime = float timeElapsed / float ledgersClosed

                LogInfo
                    "%d ledgers closed in %d seconds, average close time: %.2f seconds"
                    ledgersClosed
                    timeElapsed
                    avgCloseTime

                // Verify close time is approximately as expected
                if avgCloseTime < expectedSeconds - 0.25 || avgCloseTime > expectedSeconds + 0.5 then
                    failwithf "Expected ~%.0f second close time, but got %.2f seconds" expectedSeconds avgCloseTime

            // Upgrade to protocol 22 and verify 5-second block times
            LogInfo "Upgrading to protocol 22 and verifying 5-second block times"
            let currentProtocol = peer.GetLedgerProtocolVersion()

            if currentProtocol <> 22 then
                formation.UpgradeProtocol tier1 22
                peer.WaitForProtocol 22

            formation.UpgradeMaxTxSetSize tier1 100000
            peer.WaitForMaxTxSetSize 100000
            formation.WaitUntilSynced fullCoreSet

            // Create accounts for payment load later
            formation.RunLoadgen sdf context.GenerateAccountCreationLoad

            measureBlockTime 5.0

            // Upgrade to protocol 23 and check that block times are still 5 seconds
            LogInfo "Upgrading to protocol 23 and verifying 5-second block times"
            formation.UpgradeProtocol tier1 23
            peer.WaitForProtocol 23
            measureBlockTime 5.0

            // Upgrade to minimum SCP timing configuration and check for 4-second block times
            LogInfo "Deploying SCP timing configuration upgrade to 4-second block times"
            formation.UpgradeToMinimumSCPConfig tier1

            LogInfo "Verifying 4-second block times after SCP timing upgrade"
            measureBlockTime 4.0

            formation.CheckNoErrorsAndPairwiseConsistency())
