// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionValidatorSetup

open StellarCoreSet
open StellarCorePeer
open StellarMissionContext
open StellarSupercluster
open StellarFormation
open StellarStatefulSets
open StellarCoreHTTP

// This mission creates a network with validator configurations that closely
// mirror how tier 1 validators are actually configured. It uses both Postgres
// and has history archives enabled. This is a smoke test that spins up the
// network, runs for ~70 ledgers with load generation, verifies history
// publishing works, then spins it down.
let validatorSetup (context: MissionContext) =
    let opts =
        { CoreSetOptions.GetDefault context.image with
              dbType = Postgres
              // Use disk-backed storage like production validators
              emptyDirType = DiskBackedEmptyDir
              localHistory = true
              accelerateTime = true
              // Enable maintenance like production validators
              performMaintenance = true }

    let coreSet = MakeLiveCoreSet "core" opts

    let context =
        { context with
              numAccounts = 200
              genesisTestAccountCount = Some(200)
              numTxs = 500
              txRate = 10 }

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]

            // Run load generation to ensure blocks aren't empty
            formation.RunLoadgen coreSet context.GeneratePaymentLoad

            // Wait for a few more ledgers to ensure publishing happens
            let peer = formation.NetworkCfg.GetPeer coreSet 0
            peer.WaitForFewLedgers 20

            if (peer.GetMetrics().HistoryPublishSuccess.Count = 0) then
                failwith "history.publish.success is 0, expected > 0")
