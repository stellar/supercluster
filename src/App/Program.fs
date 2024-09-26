// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

open CSLibrary
open CommandLine
open Serilog
open k8s

open Logging
open StellarCoreSet
open StellarDestination
open StellarNetworkCfg
open StellarMission
open StellarMissionContext
open StellarSupercluster

open System.Threading


type KubeOption(kubeConfig: string, namespaceProperty: string option) =
    [<Option('k', "kubeconfig", HelpText = "Kubernetes config file", Required = false, Default = "~/.kube/config")>]
    member self.KubeConfig = kubeConfig

    [<Option("namespace", HelpText = "Namespace to use, overriding kubeconfig.", Required = false)>]
    member self.NamespaceProperty = namespaceProperty

[<Verb("poll", HelpText = "Poll a running stellar-core cluster for status")>]
type PollOptions(kubeConfig: string, namespaceProperty: string option) =
    inherit KubeOption(kubeConfig, namespaceProperty)

[<Verb("clean", HelpText = "Clean all resources in a namespace")>]
type CleanOptions(kubeConfig: string, namespaceProperty: string option) =
    inherit KubeOption(kubeConfig, namespaceProperty)

[<Verb("mission", HelpText = "Run one or more named missions")>]
type MissionOptions
    (
        kubeConfig: string,
        numNodes: int,
        logDebugPartitions: seq<string>,
        logTracePartitions: seq<string>,
        namespaceProperty: string option,
        ingressClass: string,
        ingressInternalDomain: string,
        ingressExternalHost: string option,
        ingressExternalPort: int,
        exportToPrometheus: bool,
        probeTimeout: int,
        missions: string seq,
        destination: string,
        image: string,
        oldImage: string option,
        netdelayImage: string,
        postgresImage: string,
        nginxImage: string,
        prometheusExporterImage: string,
        txRate: int,
        maxTxRate: int,
        numAccounts: int,
        numTxs: int,
        spikeSize: int,
        spikeInterval: int,
        numWasms: int option,
        numInstances: int option,
        keepData: bool,
        unevenSched: bool,
        requireNodeLabels: seq<string>,
        avoidNodeLabels: seq<string>,
        tolerateNodeTaints: seq<string>,
        apiRateLimit: int,
        pubnetData: string option,
        flatQuorum: bool option,
        tier1Keys: string option,
        opCountDistribution: string option,
        wasmBytesValues: seq<int>,
        wasmBytesWeights: seq<int>,
        dataEntriesValues: seq<int>,
        dataEntriesWeights: seq<int>,
        totalKiloBytesValues: seq<int>,
        totalKilobytesWeights: seq<int>,
        txSizeBytesValues: seq<int>,
        txSizeBytesWeights: seq<int>,
        instructionsValues: seq<int>,
        instructionsWeights: seq<int>,
        payWeight: int option,
        sorobanUploadWeight: int option,
        sorobanInvokeWeight: int option,
        minSorobanPercentSuccess: int option,
        installNetworkDelay: bool option,
        flatNetworkDelay: int option,
        peerReadingCapacity: int option,
        enableBackgroundOverlay: bool,
        peerFloodCapacity: int option,
        peerFloodCapacityBytes: int option,
        flowControlSendMoreBatchSizeBytes: int option,
        outboundByteLimit: int option,
        sleepMainThread: int option,
        flowControlSendMoreBatchSize: int option,
        simulateApplyDuration: seq<string>,
        simulateApplyWeight: seq<string>,
        networkSizeLimit: int,
        tier1OrgsToAdd: int,
        nonTier1NodesToAdd: int,
        randomSeed: int,
        pubnetParallelCatchupStartingLedger: int,
        pubnetParallelCatchupEndLedger: int option,
        pubnetParallelCatchupNumWorkers: int,
        tag: string option,
        numRuns: int option
    ) =

    [<Option('k', "kubeconfig", HelpText = "Kubernetes config file", Required = false, Default = "~/.kube/config")>]
    member self.KubeConfig = kubeConfig

    [<Option('n', "num-nodes", HelpText = "Number of nodes in config", Required = false, Default = 5)>]
    member self.NumNodes = numNodes

    [<Option("debug", HelpText = "Log-partitions to set to 'debug' level")>]
    member self.LogDebugPartitions = logDebugPartitions

    [<Option("trace", HelpText = "Log-partitions to set to 'trace' level")>]
    member self.LogTracePartitions = logTracePartitions

    [<Option("namespace", HelpText = "Namespace to use, overriding kubeconfig.", Required = false)>]
    member self.NamespaceProperty = namespaceProperty

    [<Option("ingress-class",
             HelpText = "Value for kubernetes.io/ingress.class, on ingress",
             Required = false,
             Default = "private")>]
    member self.IngressClass = ingressClass

    [<Option("ingress-internal-domain",
             HelpText = "Cluster-internal DNS domain in which to configure ingress",
             Required = false,
             Default = "local")>]
    member self.IngressInternalDomain = ingressInternalDomain

    [<Option("ingress-external-host",
             HelpText = "Cluster-external hostname to connect to for access to ingress",
             Required = false)>]
    member self.IngressExternalHost = ingressExternalHost

    [<Option("ingress-external-port",
             HelpText = "Cluster-external port to connect to for access to ingress",
             Required = false,
             Default = 80)>]
    member self.IngressExternalPort = ingressExternalPort

    [<Option("export-to-prometheus", HelpText = "Whether to export core metrics to prometheus")>]
    member self.ExportToPrometheus : bool = exportToPrometheus

    [<Option("probe-timeout", HelpText = "Timeout for liveness probe", Required = false, Default = 30)>]
    member self.ProbeTimeout = probeTimeout

    [<Value(0, Required = true)>]
    member self.Missions = missions

    [<Option('d',
             "destination",
             HelpText = "Output directory for logs and sql dumps",
             Required = false,
             Default = "destination")>]
    member self.Destination = destination

    [<Option('i', "image", HelpText = "Stellar-core image to use", Required = false, Default = "stellar/stellar-core")>]
    member self.Image = image

    [<Option('o', "old-image", HelpText = "Stellar-core image to use as old-image", Required = false)>]
    member self.OldImage = oldImage

    [<Option("netdelay-image",
             HelpText = "'Netdelay' utility image to use",
             Required = false,
             Default = "stellar/netdelay:latest")>]
    member self.netdelayImage = netdelayImage

    [<Option("postgres-image",
             HelpText = "Postgres server image to use",
             Required = false,
             Default = "index.docker.io/library/postgres:9.5.22")>]
    member self.postgresImage = postgresImage

    [<Option("nginx-image",
             HelpText = "Nginx server image to use",
             Required = false,
             Default = "index.docker.io/library/nginx:1.21.0")>]
    member self.nginxImage = nginxImage

    [<Option("prometheus-exporter-image",
             HelpText = "Stellar-core prometheus-exporter image to use",
             Required = false,
             Default = "stellar/stellar-core-prometheus-exporter:latest")>]
    member self.prometheusExporterImage = prometheusExporterImage

    [<Option("tx-rate",
             HelpText = "Transaction rate for benchmarks and load generation tests",
             Required = false,
             Default = 20)>]
    member self.TxRate = txRate

    [<Option("max-tx-rate",
             HelpText = "Maximum transaction rate for benchmarks and load generation tests",
             Required = false,
             Default = 1000)>]
    member self.MaxTxRate = maxTxRate

    [<Option("num-accounts",
             HelpText = "Number of accounts for benchmarks and load generation tests",
             Required = false,
             Default = 100000)>]
    member self.NumAccounts = numAccounts

    [<Option("num-txs",
             HelpText = "Number of transactions for benchmarks and load generation tests",
             Required = false,
             Default = 100000)>]
    member self.NumTxs = numTxs

    [<Option("spike-size",
             HelpText = "Number of transactions per spike for benchmarks and load generation tests",
             Required = false,
             Default = 100000)>]
    member self.SpikeSize = spikeSize

    [<Option("spike-interval",
             HelpText = "A spike will occur every spikeInterval seconds for benchmarks and load generation tests",
             Required = false,
             Default = 0)>]
    member self.SpikeInterval = spikeInterval

    [<Option("num-wasms", HelpText = "Number of wasms for benchmarks and load generation tests", Required = false)>]
    member self.NumWasms = numWasms

    [<Option("num-instances",
             HelpText = "Number of instances for benchmarks and load generation tests",
             Required = false)>]
    member self.NumInstances = numInstances

    [<Option("keep-data",
             HelpText = "Keeps namespaces and persistent volumes after mission fails",
             Required = false,
             Default = false)>]
    member self.KeepData = keepData

    [<Option("uneven-sched",
             HelpText = "Do not attempt to constrain scheduling evenly across worker nodes",
             Required = false,
             Default = false)>]
    member self.UnevenSched = unevenSched

    [<Option("require-node-labels", HelpText = "Only run on nodes with matching `key:value` labels", Required = false)>]
    member self.RequireNodeLabels = requireNodeLabels

    [<Option("avoid-node-labels", HelpText = "Do not run on nodes with matching `key:value` labels", Required = false)>]
    member self.AvoidNodeLabels = avoidNodeLabels

    [<Option("tolerate-node-taints", HelpText = "Allow running on nodes with matching taints", Required = false)>]
    member self.TolerateNodeTaints = tolerateNodeTaints

    [<Option("api-rate-limit",
             HelpText = "Limit of kubernetes API requests per second to make",
             Required = false,
             Default = 10)>]
    member self.ApiRateLimit = apiRateLimit

    [<Option("pubnet-data", HelpText = "JSON file containing pubnet connectivity graph data", Required = false)>]
    member self.PubnetData = pubnetData

    [<Option("flat-quorum", HelpText = "Use flat Tier1 quorum", Required = false)>]
    member self.FlatQuorum = flatQuorum

    [<Option("tier1-keys", HelpText = "JSON file containing list of 'tier-1' pubkeys from pubnet", Required = false)>]
    member self.Tier1Keys = tier1Keys

    [<Option("op-count-distribution",
             HelpText = "Operation count distribution for SimulatePubnet. See csv-type-samples/sample-loadgen-op-count-distribution.csv for the format",
             Required = false)>]
    member self.OpCountDistribution = opCountDistribution

    [<Option("wasm-bytes",
             HelpText = "A space-separated list of sizes of wasm blobs for SOROBAN_UPLOAD and MIX_CLASSIC_SOROBAN loadgen modes (See LOADGEN_WASM_BYTES_FOR_TESTING)",
             Required = false)>]
    member self.WasmBytesValues = wasmBytesValues

    [<Option("wasm-bytes-weights",
             HelpText = "A space-separated indicating how often to select a certain size when generating wasms (See LOADGEN_WASM_BYTES_DISTRIBUTION_FOR_TESTING)",
             Required = false)>]
    member self.WasmBytesWeights = wasmBytesWeights

    [<Option("data-entries",
             HelpText = "A space-separated list of number of data entries for SOROBAN_INVOKE and MIX_CLASSIC_SOROBAN loadgen modes (See LOADGEN_NUM_DATA_ENTRIES_FOR_TESTING)",
             Required = false)>]
    member self.DataEntriesValues = dataEntriesValues

    [<Option("data-entries-weights",
             HelpText = "A space-separated indicating how often to select a certain number of data entries when generating invoke transactions (See LOADGEN_NUM_DATA_ENTRIES_DISTRIBUTION_FOR_TESTING)",
             Required = false)>]
    member self.DataEntriesWeights = dataEntriesWeights

    [<Option("total-kilobytes",
             HelpText = "A space-separated list of kilobytes of IO for SOROBAN_INVOKE and MIX_CLASSIC_SOROBAN loadgen modes (See LOADGEN_IO_KILOBYTES_FOR_TESTING)",
             Required = false)>]
    member self.TotalKiloBytesValues = totalKiloBytesValues

    [<Option("total-kilobytes-weights",
             HelpText = "A space-separated indicating how often to select a certain number of kilobytes of IO when generating invoke transactions (See LOADGEN_IO_KILOBYTES_DISTRIBUTION_FOR_TESTING)",
             Required = false)>]
    member self.TotalKiloBytesWeights = totalKilobytesWeights

    [<Option("tx-size-bytes",
             HelpText = "A space-separated list of transaction sizes for SOROBAN_INVOKE and MIX_CLASSIC_SOROBAN loadgen modes (See LOADGEN_TX_SIZE_BYTES_FOR_TESTING)",
             Required = false)>]
    member self.TxSizeBytesValues = txSizeBytesValues

    [<Option("tx-size-bytes-weights",
             HelpText = "A space-separated indicating how often to select a certain transaction size when generating invoke transactions (See LOADGEN_TX_SIZE_BYTES_DISTRIBUTION_FOR_TESTING)",
             Required = false)>]
    member self.TxSizeBytesWeights = txSizeBytesWeights

    [<Option("instructions",
             HelpText = "A space-separated list of instruction counts for SOROBAN_INVOKE and MIX_CLASSIC_SOROBAN loadgen modes (See LOADGEN_TX_SIZE_BYTES_FOR_TESTING)",
             Required = false)>]
    member self.InstructionsValues = instructionsValues

    [<Option("instructions-weights",
             HelpText = "A space-separated indicating how often to select a certain instruction count when generating invoke transactions (See LOADGEN_TX_SIZE_BYTES_DISTRIBUTION_FOR_TESTING)",
             Required = false)>]
    member self.InstructionsWeights = instructionsWeights

    [<Option("pay-weight",
             HelpText = "Weight for classic transactions in MIX_CLASSIC_SOROBAN loadgen mode",
             Required = false)>]
    member self.PayWeight = payWeight

    [<Option("soroban-upload-weight",
             HelpText = "Weight for SOROBAN_UPLOAD transactions in MIX_CLASSIC_SOROBAN loadgen mode",
             Required = false)>]
    member self.SorobanUploadWeight = sorobanUploadWeight

    [<Option("soroban-invoke-weight",
             HelpText = "Weight for SOROBAN_INVOKE transactions in MIX_CLASSIC_SOROBAN loadgen mode",
             Required = false)>]
    member self.SorobanInvokeWeight = sorobanInvokeWeight

    [<Option("min-soroban-percent-success",
             HelpText = "Minimum percentage of soroban transactions that must succeed at apply time in loadgen",
             Required = false)>]
    member self.MinSorobanPercentSuccess = minSorobanPercentSuccess

    [<Option("install-network-delay",
             HelpText = "Installs network delay estimated from node locations",
             Required = false)>]
    member self.InstallNetworkDelay = installNetworkDelay

    [<Option("flat-network-delay", HelpText = "Constant value to set network delay to", Required = false)>]
    member self.FlatNetworkDelay = flatNetworkDelay

    [<Option("peer-reading-capacity",
             HelpText = "A config parameter that controls how many messages from a particular peer core can process simultaneously (See PEER_READING_CAPACITY)",
             Required = false)>]
    member self.PeerReadingCapacity = peerReadingCapacity

    [<Option("enable-background-overlay", HelpText = "background overlay")>]
    member self.EnableBackgroundOverlay : bool = enableBackgroundOverlay

    [<Option("peer-flood-capacity",
             HelpText = "A config parameter that controls how many flood messages (tx or SCP) from a particular peer core can process simultaneously (See PEER_FLOOD_READING_CAPACITY)",
             Required = false)>]
    member self.PeerFloodCapacity = peerFloodCapacity

    [<Option("peer-flood-capacity-bytes",
             HelpText = "A config parameter that controls how many bytes (tx or SCP) from a particular peer core can process simultaneously (See PEER_FLOOD_READING_CAPACITY_BYTES)",
             Required = false)>]
    member self.PeerFloodCapacityBytes = peerFloodCapacityBytes

    [<Option("sleep-main-thread",
             HelpText = "Additional sleep to test behavior of slower nodes (See ARTIFICIALLY_SLEEP_MAIN_THREAD_FOR_TESTING)",
             Required = false)>]
    member self.SleepMainThread = sleepMainThread

    [<Option("flow-control-send-more-batch-size",
             HelpText = "Peer asks for more data every time it processes `FLOW_CONTROL_SEND_MORE_BATCH_SIZE` messages (See FLOW_CONTROL_SEND_MORE_BATCH_SIZE)",
             Required = false)>]
    member self.FlowControlSendMoreBatchSize = flowControlSendMoreBatchSize

    [<Option("flow-control-send-more-batch-size-bytes",
             HelpText = "Peer asks for more data every time it processes `FLOW_CONTROL_SEND_MORE_BATCH_SIZE_BYTES` bytes (See FLOW_CONTROL_SEND_MORE_BATCH_SIZE_BYTES)",
             Required = false)>]
    member self.FlowControlSendMoreBatchSizeBytes = flowControlSendMoreBatchSizeBytes

    [<Option("outbound-byte-limit", HelpText = "Byte limit for outbound transaction queue", Required = false)>]
    member self.OutboundByteLimit = outboundByteLimit

    [<Option("simulate-apply-duration",
             HelpText = "A space-separated list of how much to sleep for in simulation (See OP_APPLY_SLEEP_TIME_DURATION_FOR_TESTING)",
             Required = false)>]
    member self.SimulateApplyDuration = simulateApplyDuration

    [<Option("simulate-apply-weight",
             HelpText = "A space-separated indicating how often to sleep for a specified amount (See OP_APPLY_SLEEP_TIME_WEIGHT_FOR_TESTING)",
             Required = false)>]
    member self.SimulateApplyWeight = simulateApplyWeight

    [<Option("tier-1-orgs-to-add",
             HelpText = "The number of tier-1 organizations to add while scaling the network in SimulatePubnet",
             Required = false)>]
    member self.Tier1OrgsToAdd = tier1OrgsToAdd

    [<Option("non-tier-1-nodes-to-add",
             HelpText = "The number of non-tier-1 nodes to add while scaling the network in SimulatePubnet",
             Required = false)>]
    member self.NonTier1NodesToAdd = nonTier1NodesToAdd

    [<Option("random-seed", HelpText = "The seed used for a random number generator.", Required = false)>]
    member self.RandomSeed = randomSeed

    [<Option("network-size-limit",
             HelpText = "The number of nodes to run in SimulatePubnet",
             Required = false,
             Default = 600)>]
    member self.NetworkSizeLimit = networkSizeLimit

    [<Option("pubnet-parallel-catchup-starting-ledger",
             HelpText = "starting ledger to run parallel catchup on",
             Required = false,
             Default = 0)>]
    member self.PubnetParallelCatchupStartingLedger = pubnetParallelCatchupStartingLedger

    [<Option("pubnet-parallel-catchup-end-ledger", HelpText = "end ledger to run parallel catchup on", Required = false)>]
    member self.PubnetParallelCatchupEndLedger = pubnetParallelCatchupEndLedger

    [<Option("pubnet-parallel-catchup-num-workers",
             HelpText = "number of workers to run parallel catchup with (only supported for V2)",
             Required = false,
             Default = 128)>]
    member self.PubnetParallelCatchupNumWorkers = pubnetParallelCatchupNumWorkers

    [<Option("tag", HelpText = "optional name to tag the run with", Required = false)>]
    member self.Tag = tag

    [<Option("num-runs",
             HelpText = "optional number of max TPS runs (more runs increase result accuracy)",
             Required = false)>]
    member self.NumRuns = numRuns

let splitLabel (lab: string) : (string * string option) =
    match lab.Split ':' with
    | [| x |] -> (x, None)
    | [| a; b |] -> (a, Some b)
    | _ -> failwith ("unexpected label '" + lab + "', need string of form 'key' or 'key:value'")

[<EntryPoint>]
let main argv =

    let logToConsoleOnly _ =
        Log.Logger <- LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger()

    let logToConsoleAndFile (file: string) =
        Log.Logger <-
            LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(file)
                .CreateLogger()

    AuxClass.CheckCSharpWorksToo()

    let result =
        CommandLine.Parser.Default.ParseArguments<PollOptions, CleanOptions, MissionOptions>(argv)

    match result with

    | :? (Parsed<obj>) as command ->
        match command.Value with

        | :? PollOptions as poll ->
            let _ = logToConsoleOnly ()
            let (kube, ns) = ConnectToCluster poll.KubeConfig poll.NamespaceProperty
            PollCluster kube ns
            0

        | :? CleanOptions as clean ->
            let _ = logToConsoleOnly ()
            let (kube, ns) = ConnectToCluster clean.KubeConfig clean.NamespaceProperty
            let ll = { LogDebugPartitions = []; LogTracePartitions = [] }

            let ctx =
                { kube = kube
                  kubeCfg = clean.KubeConfig
                  destination = Destination("destination")
                  image = "stellar/stellar-core"
                  oldImage = None
                  netdelayImage = ""
                  postgresImage = ""
                  nginxImage = ""
                  prometheusExporterImage = ""
                  txRate = 100
                  maxTxRate = 300
                  numAccounts = 100000
                  numTxs = 100000
                  spikeSize = 100000
                  spikeInterval = 0
                  numWasms = None
                  numInstances = None
                  maxFeeRate = Some(1000)
                  skipLowFeeTxs = false
                  numNodes = 3
                  namespaceProperty = ns
                  logLevels = ll
                  ingressClass = "private"
                  ingressInternalDomain = "local"
                  ingressExternalHost = None
                  ingressExternalPort = 80
                  exportToPrometheus = false
                  probeTimeout = 30
                  coreResources = SmallTestResources
                  keepData = false
                  unevenSched = true
                  requireNodeLabels = []
                  avoidNodeLabels = []
                  tolerateNodeTaints = []
                  apiRateLimit = 30
                  pubnetData = None
                  flatQuorum = None
                  tier1Keys = None
                  opCountDistribution = None
                  wasmBytesDistribution = []
                  dataEntriesDistribution = []
                  totalKiloBytesDistribution = []
                  txSizeBytesDistribution = []
                  instructionsDistribution = []
                  payWeight = None
                  sorobanUploadWeight = None
                  sorobanInvokeWeight = None
                  minSorobanPercentSuccess = None
                  installNetworkDelay = None
                  flatNetworkDelay = None
                  simulateApplyDuration = None
                  simulateApplyWeight = None
                  peerFloodCapacity = None
                  peerReadingCapacity = None
                  enableBackggroundOverlay = false
                  peerFloodCapacityBytes = None
                  outboundByteLimit = None
                  sleepMainThread = None
                  flowControlSendMoreBatchSize = None
                  flowControlSendMoreBatchSizeBytes = None
                  tier1OrgsToAdd = 0
                  nonTier1NodesToAdd = 0
                  randomSeed = 0
                  networkSizeLimit = 0
                  pubnetParallelCatchupStartingLedger = 0
                  pubnetParallelCatchupEndLedger = None
                  pubnetParallelCatchupNumWorkers = 128
                  tag = None
                  numRuns = None
                  enableTailLogging = true }

            let nCfg = MakeNetworkCfg ctx [] None
            use formation = kube.MakeEmptyFormation nCfg
            formation.CleanNamespace()
            0

        | :? MissionOptions as mission ->
            let _ = logToConsoleAndFile (sprintf "%s/stellar-supercluster.log" mission.Destination)

            let ll =
                { LogDebugPartitions = List.ofSeq mission.LogDebugPartitions
                  LogTracePartitions = List.ofSeq mission.LogTracePartitions }

            match Seq.tryFind (allMissions.ContainsKey >> not) mission.Missions with
            | Some e ->
                (LogError "Unknown mission: %s" e
                 LogError "Available missions:"
                 Map.iter (fun k _ -> LogError "        %s" k) allMissions
                 1)
            | None ->
                (LogInfo "-----------------------------------"
                 LogInfo "Supercluster command line: %s" (System.String.Join(" ", argv))
                 LogInfo "-----------------------------------"
                 LogInfo "Connecting to Kubernetes cluster: %s" mission.Destination
                 LogInfo "-----------------------------------"

                 let (kube, ns) = ConnectToCluster mission.KubeConfig mission.NamespaceProperty
                 let destination = Destination(mission.Destination)

                 let podLogger _ =
                     try
                         ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (mission.ApiRateLimit)
                         let ranges = kube.ListNamespacedLimitRange(namespaceParameter = ns)

                         if isNull ranges then
                             LogError "Heartbeat did not return any data."
                         else
                             DumpPodInfo kube mission.ApiRateLimit ns
                     with x -> LogError "Connection issue! Api call failed."

                 let timer = new System.Threading.Timer(TimerCallback(podLogger), null, 1000, 300000)

                 for m in mission.Missions do
                     LogInfo "-----------------------------------"
                     LogInfo "Starting mission: %s" m
                     LogInfo "-----------------------------------"

                     let processInputSeq s = if Seq.isEmpty s then None else Some((Seq.map int) s)

                     try
                         let missionContext =
                             { MissionContext.kube = kube
                               kubeCfg = mission.KubeConfig
                               destination = destination
                               image = mission.Image
                               oldImage = mission.OldImage
                               netdelayImage = mission.netdelayImage
                               postgresImage = mission.postgresImage
                               nginxImage = mission.nginxImage
                               prometheusExporterImage = mission.prometheusExporterImage
                               txRate = mission.TxRate
                               maxTxRate = mission.MaxTxRate
                               numAccounts = mission.NumAccounts
                               numTxs = mission.NumTxs
                               spikeSize = mission.SpikeSize
                               spikeInterval = mission.SpikeInterval
                               numWasms = mission.NumWasms
                               numInstances = mission.NumInstances
                               maxFeeRate = Some(1000)
                               skipLowFeeTxs = false
                               numNodes = mission.NumNodes
                               namespaceProperty = ns
                               logLevels = ll
                               ingressClass = mission.IngressClass
                               ingressInternalDomain = mission.IngressInternalDomain
                               ingressExternalHost = mission.IngressExternalHost
                               ingressExternalPort = mission.IngressExternalPort
                               exportToPrometheus = mission.ExportToPrometheus
                               probeTimeout = mission.ProbeTimeout
                               coreResources = SmallTestResources
                               keepData = mission.KeepData
                               unevenSched = mission.UnevenSched
                               requireNodeLabels = List.map splitLabel (List.ofSeq mission.RequireNodeLabels)
                               avoidNodeLabels = List.map splitLabel (List.ofSeq mission.AvoidNodeLabels)
                               tolerateNodeTaints = List.map splitLabel (List.ofSeq mission.TolerateNodeTaints)
                               apiRateLimit = mission.ApiRateLimit
                               pubnetData = mission.PubnetData
                               flatQuorum = mission.FlatQuorum
                               tier1Keys = mission.Tier1Keys
                               opCountDistribution = mission.OpCountDistribution
                               wasmBytesDistribution =
                                   List.zip (List.ofSeq mission.WasmBytesValues) (List.ofSeq mission.WasmBytesWeights)
                               dataEntriesDistribution =
                                   List.zip
                                       (List.ofSeq mission.DataEntriesValues)
                                       (List.ofSeq mission.DataEntriesWeights)
                               totalKiloBytesDistribution =
                                   List.zip
                                       (List.ofSeq mission.TotalKiloBytesValues)
                                       (List.ofSeq mission.TotalKiloBytesWeights)
                               txSizeBytesDistribution =
                                   List.zip
                                       (List.ofSeq mission.TxSizeBytesValues)
                                       (List.ofSeq mission.TxSizeBytesWeights)
                               instructionsDistribution =
                                   List.zip
                                       (List.ofSeq mission.InstructionsValues)
                                       (List.ofSeq mission.InstructionsWeights)
                               payWeight = mission.PayWeight
                               sorobanUploadWeight = mission.SorobanUploadWeight
                               sorobanInvokeWeight = mission.SorobanInvokeWeight
                               minSorobanPercentSuccess = mission.MinSorobanPercentSuccess
                               installNetworkDelay = mission.InstallNetworkDelay
                               flatNetworkDelay = mission.FlatNetworkDelay
                               simulateApplyDuration = processInputSeq mission.SimulateApplyDuration
                               simulateApplyWeight = processInputSeq mission.SimulateApplyWeight
                               peerReadingCapacity = mission.PeerReadingCapacity
                               enableBackggroundOverlay = mission.EnableBackgroundOverlay
                               peerFloodCapacity = mission.PeerFloodCapacity
                               peerFloodCapacityBytes = mission.PeerFloodCapacityBytes
                               outboundByteLimit = mission.OutboundByteLimit
                               sleepMainThread = mission.SleepMainThread
                               flowControlSendMoreBatchSize = mission.FlowControlSendMoreBatchSize
                               flowControlSendMoreBatchSizeBytes = mission.FlowControlSendMoreBatchSizeBytes
                               tier1OrgsToAdd = mission.Tier1OrgsToAdd
                               nonTier1NodesToAdd = mission.NonTier1NodesToAdd
                               networkSizeLimit = mission.NetworkSizeLimit
                               randomSeed = mission.RandomSeed
                               pubnetParallelCatchupStartingLedger = mission.PubnetParallelCatchupStartingLedger
                               pubnetParallelCatchupEndLedger = mission.PubnetParallelCatchupEndLedger
                               pubnetParallelCatchupNumWorkers = mission.PubnetParallelCatchupNumWorkers
                               tag = mission.Tag
                               numRuns = mission.NumRuns
                               enableTailLogging = true }

                         allMissions.[m] missionContext

                     // The exception handling below is required even if we weren't logging.
                     // This looks ridiculous but it's how you coax the .NET runtime
                     // to actually run the IDisposable Dispose methods on uncaught
                     // exceptions. Sigh.
                     with x ->
                         LogError "Exception thrown. Message = %s - StackTrace =%s" x.Message x.StackTrace
                         reraise ()

                     LogInfo "-----------------------------------"
                     LogInfo "Finished mission: %s" m
                     LogInfo "-----------------------------------"

                 timer.Dispose()
                 0)

        | _ -> 1

    | _ -> 1
