// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMissionContext

open k8s
open StellarDestination

let GetOrDefault optional def =
    match optional with
    | Some(x) -> x
    | _ -> def

type LogLevels =
    { LogDebugPartitions: string list
      LogTracePartitions: string list }

type CoreResources =
    SmallTestResources
    | AcceptanceTestResources
    | SimulatePubnetResources
    | ParallelCatchupResources

type MissionContext =
    { kube : Kubernetes
      destination : Destination
      image : string
      oldImage : string option
      txRate : int
      maxTxRate : int
      numAccounts : int
      numTxs : int
      spikeSize : int
      spikeInterval : int
      numNodes : int
      namespaceProperty : string
      logLevels: LogLevels
      ingressDomain : string
      exportToPrometheus : bool
      probeTimeout : int
      coreResources : CoreResources
      keepData : bool
      apiRateLimit: int }
