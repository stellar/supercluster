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
    | MaxTPSClassicResources
    | ParallelCatchupResources
    | NonParallelCatchupResources
    | UpgradeResources

type MissionContext =
    { kube: Kubernetes
      kubeCfg: string
      destination: Destination
      missionName: string
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
      gatewayName: string
      gatewayNamespace: string
      routeInternalDomain: string
      routeExternalHost: string option
      routeExternalPort: int
      exportToPrometheus: bool
      probeTimeout: int
      coreResources: CoreResources
      keepData: bool
      unevenSched: bool
      // --one-stellar-core-per-host: this run's stellar-core StatefulSet pods
      // (validators and watchers) carry a *required* pod anti-affinity against
      // each other on kubernetes.io/hostname and request their limits, so each
      // gets a worker node sized to what it may use. The placement is checked
      // whenever pods start, and the run fails fast if they cannot be scheduled.
      // The soft topology spread alone lets the scheduler pack them when few
      // nodes are available.
      oneStellarCorePerHost: bool
      // When set, this run requires exclusive use of its nodes: its pods will
      // not be scheduled onto a node hosting another run's stellar-core pods,
      // and no other run's stellar-core pods will be scheduled onto its nodes
      // while it is alive. This only covers pods carrying the app=stellar-core
      // label (i.e. pods built from NetworkCfg pod templates); it does not
      // repel other workloads such as parallel-catchup-v2 helm pods, which are
      // instead kept on their own tainted nodes. Used for performance-sensitive
      // missions (Max TPS, Min Block Time) whose measurements would be
      // corrupted by co-tenant workloads.
      dedicatedNodes: bool
      requireNodeLabels: ((string * string option) list)
      avoidNodeLabels: ((string * string option) list)
      tolerateNodeTaints: ((string * string option) list)
      apiRateLimit: int
      httpProxyReplicas: int
      pubnetData: string option
      measureE2eLatency: bool
      flatQuorum: bool option
      tier1Keys: string option
      loadgenKeys: string option
      maxConnections: int option
      fullyConnectTier1: bool
      byteCountDistribution: ((int * int) list)
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
      enableBackgroundSigValidation: bool
      enableParallelApply: bool
      enableInMemoryBuckets: bool
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
      numPregeneratedTxs: int option
      networkSizeLimit: int
      pubnetParallelCatchupStartingLedger: int
      pubnetParallelCatchupEndLedger: int option
      pubnetParallelCatchupLedgersPerJob: int
      pubnetParallelCatchupNumWorkers: int
      genesisTestAccountCount: int option

      asanOptions: string option

      // Extra environment for every stellar-core container (and the overlay
      // process it spawns), as (NAME, VALUE) pairs from --core-env.
      coreEnv: (string * string) list

      // Tail logging can cause the pubnet simulation missions like SorobanLoadGeneration
      // and SimulatePubnet to fail on the heartbeat handler due to what looks like a
      // server disconnection. Our solution for now is to just disable tail logging on
      // those missions.
      enableTailLogging: bool
      catchupSkipKnownResultsForTesting: bool option
      checkEventsAreConsistentWithEntryDiffs: bool option
      updateSorobanCosts: bool option
      enableRelaxedAutoQsetConfig: bool
      jobMonitorExternalHost: string option
      txBatchMaxSize: int option
      runForMaxTps: string option
      requireNodeLabelsPcV2: ((string * string option) list)
      avoidNodeLabelsPcV2: ((string * string option) list)
      tolerateNodeTaintsPcV2: ((string * string option) list)
      serviceAccountAnnotationsPcV2: ((string * string) list)
      s3HistoryMirrorOverridePcV2: string option
      s3HistoryMirrorRegionPcV2: string
      benchmarkInfrastructure: bool option
      benchmarkInfrastructureOnly: bool option
      benchmarkDurationSeconds: int option
      enableTcpTuning: bool
      minBlockTimeMs: int
      maxBlockTimeMs: int
      minBlockTimeMixedMode: string
      minBlockTimeMixedClassicTxRate: int option
      minBlockTimeMixedSorobanTxRate: int option
      // --tier1-org-count: organizations in StableApproximateTier1CoreSets
      // (None = its 10; up to 40 adds StellarNetworkData.tier1ExtraOrgs).
      tier1OrgCount: int option
      runForMinBlockTime: bool
      forceOldStyleTriggerTimerPct: int
      uniformDrift: int list
      bimodalDrift: int list
      driftPct: int
      ledgerCloseTimeMs: int option
      forceOldStyleTriggerTimer: bool option }

module MissionContext =
    /// Parse repeatable --core-env NAME=VALUE entries. Rejects blank or malformed entries, names that are not
    /// environment variable names, and duplicate names, so a typo cannot silently drop a setting.
    let parseCoreEnv (entries: seq<string>) : (string * string) list =
        let parsed =
            entries
            |> Seq.map
                (fun e ->
                    match e.IndexOf('=') with
                    | i when
                        i > 0
                        && System.Text.RegularExpressions.Regex.IsMatch(
                            e.Substring(0, i),
                            @"\A[A-Za-z_][A-Za-z0-9_]*\z"
                        ) -> (e.Substring(0, i), e.Substring(i + 1))
                    | _ ->
                        failwithf
                            "--core-env expects NAME=VALUE with NAME made of letters, digits and underscores, not starting with a digit; got '%s'"
                            e)
            |> List.ofSeq

        let names = parsed |> List.map fst

        if List.length names <> (names |> List.distinct |> List.length) then
            failwithf "--core-env has duplicate names: %A" names

        parsed
