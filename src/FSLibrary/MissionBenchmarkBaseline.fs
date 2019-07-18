// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionBenchmarkBaseline

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext

let benchmarkBaseline (context : MissionContext) =
    let coreSet = MakeCoreSet "core" context.numNodes context.numNodes CoreSetOptions.Default
    context.ExecuteWithPerformanceReporter [coreSet] None (fun f pr ->
        f.WaitUntilSynced [coreSet]

        let upgrades = { DefaultUpgradeParameters with
                           maxTxSize = Some(1000000);
                           protocolVersion = Some(11) }
        f.NetworkCfg.EachPeer (fun p ->
            p.SetUpgrades(upgrades) // upgrade protocol
            p.WaitForNextLedger()
            p.SetUpgrades(upgrades) // upgrade maxTxSize properly
        )

        f.RunLoadgen coreSet context.GenerateAccountCreationLoad
        pr.RecordPerformanceMetrics "pay" context.numAccounts context.numTxs context.txRate 100 (fun _ ->
            f.RunLoadgen coreSet context.GeneratePaymentLoad
        )
    )
