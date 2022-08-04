// Copyright 2022 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionProtocolUpgradeWithLoad

open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP

let protocolUpgradeWithLoad (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  dumpDatabase = false }

    let context =
        { context with
              numAccounts = 200
              numTxs = 50000
              txRate = 1000
              skipLowFeeTxs = true }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            let networkCfg = formation.NetworkCfg
            let peer = networkCfg.GetPeer coreSet 0
            let latestProtocol = peer.GetSupportedProtocolVersion()

            formation.UpgradeProtocol [ coreSet ] (latestProtocol - 1)
            formation.UpgradeMaxTxSetSize [ coreSet ] 1000
            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad

            formation.ScheduleProtocolUpgrade [ coreSet ] latestProtocol (System.DateTime.Now.AddSeconds(20.0))
            formation.RunLoadgen coreSet context.GeneratePaymentLoad)
