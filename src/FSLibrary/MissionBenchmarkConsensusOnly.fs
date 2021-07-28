// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionBenchmarkConsensusOnly

open StellarCoreHTTP
open StellarCoreSet
open StellarMissionContext
open StellarPerformanceReporter
open StellarFormation
open StellarStatefulSets
open StellarSupercluster


let benchmarkConsensusOnly (context: MissionContext) =
    let context =
        { context with
              // = 2000 usec 100% of the time
              simulateApplyDuration = Some(context.simulateApplyDuration |> Option.defaultValue (seq { 2000 }))
              simulateApplyWeight = Some(context.simulateApplyWeight |> Option.defaultValue (seq { 100 })) }

    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = context.numNodes
                  accelerateTime = false
                  localHistory = false
                  maxSlotsToRemember = 24
                  syncStartupDelay = Some(30)
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  dumpDatabase = false }

    context.ExecuteWithPerformanceReporter
        [ coreSet ]
        None
        (fun (formation: StellarFormation) (performanceReporter: PerformanceReporter) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSize [ coreSet ] 1000000

            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad

            performanceReporter.RecordPerformanceMetrics
                context.GeneratePaymentLoad
                (fun _ -> formation.RunLoadgen coreSet context.GeneratePaymentLoad))
