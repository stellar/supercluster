// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionBenchmarkIncreaseTxRate

open StellarCoreHTTP
open StellarCoreSet
open StellarMissionContext
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

    context.Execute
        [ coreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet ]
            formation.UpgradeProtocolToLatest [ coreSet ]
            formation.UpgradeMaxTxSetSize [ coreSet ] 1000000

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
                      batchsize = 100
                      maxfeerate = None
                      skiplowfeetxs = false }

                formation.RunLoadgen coreSet loadGen)
