// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionBenchmarkIncreaseTxRate

open StellarCoreHTTP
open StellarCoreSet
open StellarMissionContext
open StellarPerformanceReporter
open StellarFormation
open StellarStatefulSets
open StellarSupercluster


let benchmarkIncreaseTxRate (context: MissionContext) =
    let coreSet =
        MakeLiveCoreSet
            "core"
            { CoreSetOptions.GetDefault context.image with
                  invariantChecks = AllInvariantsExceptBucketConsistencyChecks
                  nodeCount = context.numNodes
                  accelerateTime = false }

    context.ExecuteWithPerformanceReporter
        [ coreSet ]
        None
        (fun (formation: StellarFormation) (performanceReporter: PerformanceReporter) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSize [ coreSet ] 1000000

            formation.RunLoadgen coreSet context.GenerateAccountCreationLoad

            for txRate in context.txRate .. (10) .. context.maxTxRate do
                let loadGen =
                    { mode = GeneratePaymentLoad
                      accounts = context.numAccounts
                      txs = context.numTxs
                      spikesize = context.spikeSize
                      spikeinterval = context.spikeInterval
                      txrate = txRate
                      offset = 0
                      batchsize = 100 }

                performanceReporter.RecordPerformanceMetrics loadGen (fun _ -> formation.RunLoadgen coreSet loadGen))
