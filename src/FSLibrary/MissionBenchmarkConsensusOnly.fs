// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionBenchmarkConsensusOnly

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarPerformanceReporter
open StellarFormation

let benchmarkConsensusOnly (context : MissionContext) =
    let coreSet = MakeLiveCoreSet "core" { CoreSetOptions.Default with nodeCount = context.numNodes; accelerateTime = false; localHistory = false; simulateApplyUsec = 2000; maxSlotsToRemember = 24; }
    context.ExecuteWithPerformanceReporter [coreSet] None (fun (formation: StellarFormation) (performanceReporter: PerformanceReporter) ->
        formation.WaitUntilSynced [coreSet]
        formation.UpgradeProtocolToLatest [coreSet]
        formation.UpgradeMaxTxSize [coreSet] 1000000

        formation.RunLoadgen coreSet context.GenerateAccountCreationLoad
        performanceReporter.RecordPerformanceMetrics context.GeneratePaymentLoad (fun _ ->
            formation.RunLoadgen coreSet context.GeneratePaymentLoad
        )
    )
