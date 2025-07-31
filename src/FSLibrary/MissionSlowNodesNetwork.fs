// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionSlowNodesNetwork

open StellarCoreSet
open StellarMissionContext
open StellarFormation
open StellarStatefulSets
open StellarSupercluster
open StellarCoreHTTP
open StellarNetworkData

let slowNodesNetwork (context: MissionContext) =
    // Add pessimistic 100ms latency delay to all nodes to further stress the network
    let context =
        { context.WithNominalLoad with
              installNetworkDelay = Some(true)
              flatNetworkDelay = Some(100)
              numAccounts = 20000
              numTxs = 40000
              coreResources = MediumTestResources
              genesisTestAccountCount = Some 20000 }

    let coreSet =
        MakeLiveCoreSet
            "fast-core"
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = 7
                  nodeLocs = Some([ Ashburn; Brussels; Beauharnois; Bengaluru; Chennai; Clifton; Columbus ])
                  accelerateTime = false
                  dumpDatabase = false }

    let slowCoreSet =
        MakeLiveCoreSet
            "slow-core"
            { CoreSetOptions.GetDefault context.image with
                  nodeCount = 3
                  nodeLocs = Some([ CouncilBluffs; Falkenstein; Frankfurt ])
                  accelerateTime = false
                  // 2000 usec delay per grain-of-work unit to simulate a slow node
                  // (See ARTIFICIALLY_SLEEP_MAIN_THREAD_FOR_TESTING config option for details)
                  addArtificialDelayUsec = Some(2000)
                  dumpDatabase = false }

    context.Execute
        [ coreSet; slowCoreSet ]
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced [ coreSet
                                        slowCoreSet ]

            formation.UpgradeProtocolToLatest [ coreSet
                                                slowCoreSet ]

            formation.UpgradeMaxTxSetSize [ coreSet; slowCoreSet ] 100000

            formation.RunLoadgen coreSet { context.GeneratePaymentLoad with txrate = 100 })
