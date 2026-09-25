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
    // Validators of the 8-vCPU perf benchmarks (MinBlockTimeClassic/Mixed,
    // MaxTPSClassic/Mixed) under --overlay-v2-optimized: no CFS CPU quota; see
    // StellarKubeSpecs.PerfBenchmarkCoreResourceRequirements.
    | PerfBenchmarkResources
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
      // --overlay-v2-optimized: defaults tuned for the experimental Rust-overlay
      // (v2) stellar-core image. Every setting it changes is listed in
      // MissionContext.describeOverlayV2; without it, missions keep upstream's
      // configs, resources and limits.
      overlayV2Optimized: bool
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
      // Emit DISABLE_TX_META_FOR_TESTING = true; set by the --overlay-v2-optimized
      // perf-mission defaults.
      disableTxMetaForTesting: bool
      // (classic, Soroban) transaction bytes the mission offers per second,
      // set by MinBlockTime* so --overlay-v2-optimized can split the tx-set
      // byte allowance by the offered load (MissionContext.txSetByteAllowances).
      offeredTxBytesPerSec: (int64 * int64) option
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
      // Set by MinBlockTimeTest when its load runs on every validator
      // (StellarKubeSpecs.LoadOnEveryValidator for the mission's actual load
      // mode): each validator then pregenerates transactions for its own
      // account slice (StellarKubeSpecs.PregenerationOptionsForPeer).
      pregenerateTxsPerValidator: bool
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

    /// The single decision for BUCKETLIST_DB_INDEX_PAGE_SIZE_EXPONENT=0 (in-memory BucketListDB), so the key is
    /// emitted at most once: --in-memory-buckets or an --overlay-v2-optimized perf mission's default (both set
    /// enableInMemoryBuckets), or --run-for-max-tps, whose high-throughput config has always included it.
    let inMemoryBuckets (ctx: MissionContext) : bool = ctx.enableInMemoryBuckets || ctx.runForMaxTps.IsSome

    /// Whether a core set's nodes run on postgres: its dbType says so, or a max-TPS mission, which uses postgres
    /// whatever the dbType (parallel apply is only supported on postgres). The one decision behind the DATABASE
    /// setting, the postgres sidecar and the pod's postgres setup steps, so they cannot disagree.
    let usesPostgres (ctx: MissionContext) (dbType: StellarCoreSet.DBType) : bool =
        dbType = StellarCoreSet.Postgres || ctx.runForMaxTps.IsSome

    /// What --overlay-v2-optimized sets on the perf-sensitive benchmark missions (MinBlockTimeClassic/Mixed
    /// and MaxTPSClassic/Mixed), applied on top of the command-line context like their dedicatedNodes = true:
    /// in-memory BucketListDB, no test-only tx meta and one stellar-core pod per worker node (as --one-stellar-core-per-host).
    /// Without the flag the context is returned unchanged.
    let withOverlayV2PerfDefaults (ctx: MissionContext) : MissionContext =
        if not ctx.overlayV2Optimized then
            ctx
        else
            { ctx with
                  enableInMemoryBuckets = true
                  disableTxMetaForTesting = true
                  oneStellarCorePerHost = true }

    /// Validator resources of the perf missions (MinBlockTimeClassic/Mixed, MaxTPSClassic/Mixed): PerfBenchmarkResources
    /// under --overlay-v2-optimized, otherwise the mission's own upstream resources.
    let perfMissionCoreResources (ctx: MissionContext) (upstream: CoreResources) : CoreResources =
        if ctx.overlayV2Optimized then PerfBenchmarkResources else upstream

    /// MinBlockTime* tx-set limits as a percentage of the offered txs per ledger: upstream's 2x, or 125% under
    /// --overlay-v2-optimized. Every tx-set build on the Rust-overlay core pulls twice these limits from the
    /// mempool, so the smaller buffer cuts that pull while a slow ledger's backlog can still drain.
    let txSetSizeBufferPct (ctx: MissionContext) : int = if ctx.overlayV2Optimized then 125 else 200

    let private mib = 1024 * 1024

    /// Splits core's 10 MiB tx-set byte budget (MAX_TX_SET_ALLOWANCE, which the two allowances together must not
    /// exceed) between the classic and Soroban phases in proportion to the bytes each is offered, with at least
    /// 1 MiB each for setup transactions, as (classic, soroban) bytes. Whenever the offered bytes per ledger fit
    /// in 10 MiB, each phase gets at least what it is offered. Exposed for unit tests.
    let splitTxSetByteAllowance (classicBytes: int64) (sorobanBytes: int64) : int * int =
        let total = int64 (10 * mib)
        let atLeast = int64 mib
        let offered = max 0L classicBytes + max 0L sorobanBytes

        let proportional = if offered = 0L then total / 2L else total * max 0L sorobanBytes / offered

        let soroban = proportional |> max atLeast |> min (total - atLeast)

        int (total - soroban), int soroban

    /// TESTING_MAX_{CLASSIC,SOROBAN}_BYTE_ALLOWANCE in bytes, as (classic, soroban), or None for core's defaults
    /// (5 MiB each; 5 MiB caps a Soroban phase at about 6900 SAC payments). The max-TPS modes keep their own
    /// splits; under --overlay-v2-optimized, a mission that declares its offered load (MinBlockTime*) gets
    /// splitTxSetByteAllowance of it. The one source for the node configs and the run log.
    let txSetByteAllowances (ctx: MissionContext) : (int * int) option =
        match ctx.runForMaxTps with
        | Some "classic" -> Some(9 * mib, 1 * mib)
        | Some "soroban" -> Some(1 * mib, 9 * mib)
        | Some "classic-prev-version" -> None
        | Some _ -> failwith "run-for-max-tps must be either classic, classic-prev-version, or soroban"
        | None when ctx.overlayV2Optimized ->
            ctx.offeredTxBytesPerSec
            |> Option.map (fun (classic, soroban) -> splitTxSetByteAllowance classic soroban)
        | None -> None

    /// A (classic, soroban) byte allowance pair for the run log.
    let describeTxSetByteAllowances (classic: int, soroban: int) : string =
        sprintf "classic %.1f MiB, Soroban %.1f MiB" (float classic / float mib) (float soroban / float mib)

    /// MinBlockTime* load per candidate, in seconds: upstream's 300, or 960 under --overlay-v2-optimized (a 60 s
    /// warm-up, then three 5-minute close-time windows; see MinBlockTimeTest.ledgerAgeReadSchedule).
    let minBlockTimeLoadDurationSec (ctx: MissionContext) : int = if ctx.overlayV2Optimized then 960 else 300

    /// Whether --measure-e2e-latency needs --loadgen-keys to find the load-generating nodes: MinBlockTime* missions
    /// mark their own (MinBlockTimeTest.markLoadGenerators); other missions only have the keys.
    let e2eLatencyNeedsLoadgenKeys (missions: string seq) : bool =
        missions |> Seq.exists (fun m -> not (m.StartsWith "MinBlockTime"))

    /// The settings --overlay-v2-optimized resolves for this run, one line each, for the run log. Empty without it.
    let describeOverlayV2 (ctx: MissionContext) : string list =
        if not ctx.overlayV2Optimized then
            []
        else
            let userEnv = ctx.coreEnv |> List.map fst |> Set.ofList

            [ sprintf "MinBlockTime* tx-set limits: %d%% of the offered txs per ledger" (txSetSizeBufferPct ctx)
              sprintf "MinBlockTime* load window: %d s per candidate" (minBlockTimeLoadDurationSec ctx)
              (match txSetByteAllowances ctx with
               | Some allowances -> "tx-set byte allowances: " + describeTxSetByteAllowances allowances
               | None when ctx.runForMaxTps.IsNone ->
                   "tx-set byte allowances: MinBlockTime* splits 10 MiB by its offered classic/Soroban bytes (at least 1 MiB each); others keep core's 5 MiB each"
               | None -> "tx-set byte allowances: core's defaults (5 MiB each)")
              "BucketListDB: in-memory (perf missions)"
              "test tx meta: disabled (perf missions)"
              "placement: one stellar-core pod per worker node, enforced (perf missions)"
              "perf validators: 8 vCPU request, no CPU limit, 16Gi memory"
              (if userEnv.Contains "TOKIO_WORKER_THREADS" then
                   "TOKIO_WORKER_THREADS: from --core-env"
               else
                   "TOKIO_WORKER_THREADS: 8 (perf validators)")
              "Soroban limits: 8 dependent-tx clusters"
              "overlay mesh: bounded wait whenever a mission waits for connections, restarting all nodes to redraw a wedged mesh"
              "e2e latency: measured on the MinBlockTime* load generators (as --measure-e2e-latency)" ]
