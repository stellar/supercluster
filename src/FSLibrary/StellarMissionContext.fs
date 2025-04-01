// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMissionContext

open k8s
open StellarDestination

let GetOrDefault optional def =
    match optional with
    | Some (x) -> x
    | _ -> def

// If `list` is empty, return `value`. Otherwise, return `list`.
let defaultListValue value list =
    match list with
    | [] -> value
    | _ -> list


type LogLevels = { LogDebugPartitions: string list; LogTracePartitions: string list }

type CoreResources =
    | SmallTestResources
    | MediumTestResources
    | AcceptanceTestResources
    | SimulatePubnetResources
    | SimulatePubnetTier1PerfResources
    | ParallelCatchupResources
    | NonParallelCatchupResources
    | UpgradeResources

type MissionContext =
    { kube: Kubernetes
      kubeCfg: string
      destination: Destination
      image: string
      oldImage: string option
      netdelayImage: string
      postgresImage: string
      nginxImage: string
      prometheusExporterImage: string
      txRate: int
      maxTxRate: int
      numAccounts: int
      numTxs: int
      spikeSize: int
      spikeInterval: int
      numWasms: int option
      numInstances: int option
      maxFeeRate: int option
      skipLowFeeTxs: bool
      numNodes: int
      namespaceProperty: string
      logLevels: LogLevels
      ingressClass: string
      ingressInternalDomain: string
      ingressExternalHost: string option
      ingressExternalPort: int
      exportToPrometheus: bool
      probeTimeout: int
      coreResources: CoreResources
      keepData: bool
      unevenSched: bool
      requireNodeLabels: ((string * string option) list)
      avoidNodeLabels: ((string * string option) list)
      tolerateNodeTaints: ((string * string option) list)
      apiRateLimit: int
      pubnetData: string option
      flatQuorum: bool option
      tier1Keys: string option
      maxConnections: int option
      fullyConnectTier1: bool
      opCountDistribution: string option
      wasmBytesDistribution: ((int * int) list)
      dataEntriesDistribution: ((int * int) list)
      totalKiloBytesDistribution: ((int * int) list)
      txSizeBytesDistribution: ((int * int) list)
      instructionsDistribution: ((int * int) list)
      payWeight: int option
      sorobanUploadWeight: int option
      sorobanInvokeWeight: int option
      minSorobanPercentSuccess: int option
      installNetworkDelay: bool option
      flatNetworkDelay: int option
      simulateApplyDuration: seq<int> option
      simulateApplyWeight: seq<int> option
      peerReadingCapacity: int option
      enableBackggroundOverlay: bool
      peerFloodCapacity: int option
      peerFloodCapacityBytes: int option
      sleepMainThread: int option
      flowControlSendMoreBatchSize: int option
      flowControlSendMoreBatchSizeBytes: int option
      outboundByteLimit: int option
      tier1OrgsToAdd: int
      nonTier1NodesToAdd: int
      randomSeed: int
      tag: string option
      numRuns: int option
      numPregeneratedTxs: int option
      networkSizeLimit: int
      pubnetParallelCatchupStartingLedger: int
      pubnetParallelCatchupEndLedger: int option
      pubnetParallelCatchupNumWorkers: int
      genesisTestAccountCount: int option

      // Tail logging can cause the pubnet simulation missions like SorobanLoadGeneration
      // and SimulatePubnet to fail on the heartbeat handler due to what looks like a
      // server disconnection. Our solution for now is to just disable tail logging on
      // those missions.
      enableTailLogging: bool
      catchupSkipKnownResultsForTesting: bool option
      updateSorobanCosts: bool option
      txBatchMaxSize: int option }
