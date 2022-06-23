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
        keepData: bool,
        unevenSched: bool,
        requireNodeLabels: seq<string>,
        avoidNodeLabels: seq<string>,
        tolerateNodeTaints: seq<string>,
        apiRateLimit: int,
        pubnetData: string option,
        tier1Keys: string option,
        opCountDistribution: string option,
        installNetworkDelay: bool option,
        flatNetworkDelay: int option,
        peerReadingCapacity: int option,
        peerFloodCapacity: int option,
        sleepMainThread: int option,
        flowControlSendMoreBatchSize: int option,
        simulateApplyDuration: seq<string>,
        simulateApplyWeight: seq<string>,
        networkSizeLimit: int,
        tier1OrgsToAdd: int,
        nonTier1NodesToAdd: int,
        randomSeed: int,
        pubnetParallelCatchupStartingLedger: int,
        tag: string option
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

    [<Option("probe-timeout", HelpText = "Timeout for liveness probe", Required = false, Default = 5)>]
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

    [<Option("tier1-keys", HelpText = "JSON file containing list of 'tier-1' pubkeys from pubnet", Required = false)>]
    member self.Tier1Keys = tier1Keys

    [<Option("op-count-distribution",
             HelpText = "Operation count distribution for SimulatePubnet. See csv-type-samples/sample-loadgen-op-count-distribution.csv for the format",
             Required = false)>]
    member self.OpCountDistribution = opCountDistribution

    [<Option("install-network-delay",
             HelpText = "Installs network delay estimated from node locations",
             Required = false)>]
    member self.InstallNetworkDelay = installNetworkDelay

    [<Option("flat-network-delay",
             HelpText = "Constant value to set network delay to",
             Required = false)>]
    member self.FlatNetworkDelay = flatNetworkDelay

    [<Option("peer-reading-capacity",
             HelpText = "A config parameter that controls how many messages from a particular peer core can process simultaneously (See PEER_READING_CAPACITY)",
             Required = false)>]
    member self.PeerReadingCapacity = peerReadingCapacity

    [<Option("peer-flood-capacity",
             HelpText = "A config parameter that controls how many flood messages (tx or SCP) from a particular peer core can process simultaneously (See PEER_FLOOD_READING_CAPACITY)",
             Required = false)>]
    member self.PeerFloodCapacity = peerFloodCapacity

    [<Option("sleep-main-thread",
             HelpText = "Additional sleep to test behavior of slower nodes (See ARTIFICIALLY_SLEEP_MAIN_THREAD_FOR_TESTING)",
             Required = false)>]
    member self.SleepMainThread = sleepMainThread

    [<Option("flow-control-send-more-batch-size",
             HelpText = "Peer asks for more data every time it processes `FLOW_CONTROL_SEND_MORE_BATCH_SIZE` messages (See FLOW_CONTROL_SEND_MORE_BATCH_SIZE)",
             Required = false)>]
    member self.FlowControlSendMoreBatchSize = flowControlSendMoreBatchSize

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
             Default = 100)>]
    member self.NetworkSizeLimit = networkSizeLimit

    [<Option("pubnet-parallel-catchup-starting-ledger",
             HelpText = "starting ledger to run parallel catchup on",
             Required = false,
             Default = 0)>]
    member self.PubnetParallelCatchupStartingLedger = pubnetParallelCatchupStartingLedger

    [<Option("tag", HelpText = "optional name to tag the run with", Required = false)>]
    member self.Tag = tag


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
                  numNodes = 3
                  namespaceProperty = ns
                  logLevels = ll
                  ingressClass = "private"
                  ingressInternalDomain = "local"
                  ingressExternalHost = None
                  ingressExternalPort = 80
                  exportToPrometheus = false
                  probeTimeout = 5
                  coreResources = SmallTestResources
                  keepData = false
                  unevenSched = true
                  requireNodeLabels = []
                  avoidNodeLabels = []
                  tolerateNodeTaints = []
                  apiRateLimit = 30
                  pubnetData = None
                  tier1Keys = None
                  opCountDistribution = None
                  installNetworkDelay = None
                  flatNetworkDelay = None
                  simulateApplyDuration = None
                  simulateApplyWeight = None
                  peerFloodCapacity = None
                  peerReadingCapacity = None
                  sleepMainThread = None
                  flowControlSendMoreBatchSize = None
                  tier1OrgsToAdd = 0
                  nonTier1NodesToAdd = 0
                  randomSeed = 0
                  networkSizeLimit = 0
                  pubnetParallelCatchupStartingLedger = 0
                  tag = None }

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
                 LogInfo "Connecting to Kubernetes cluster"
                 LogInfo "-----------------------------------"

                 let (kube, ns) = ConnectToCluster mission.KubeConfig mission.NamespaceProperty
                 let destination = Destination(mission.Destination)

                 let heartbeatHandler _ =
                     try
                         let ranges = kube.ListNamespacedLimitRange(namespaceParameter = ns)
                         if isNull ranges then failwith "Connection issue!" else DumpPodInfo kube ns
                     with x ->
                         LogError "Connection issue!"
                         reraise ()

                 // Poll cluster every minute to make sure we don't have any issues
                 let timer = new System.Threading.Timer(TimerCallback(heartbeatHandler), null, 1000, 300000)

                 for m in mission.Missions do
                     LogInfo "-----------------------------------"
                     LogInfo "Starting mission: %s" m
                     LogInfo "-----------------------------------"

                     let processInputSeq s = if Seq.isEmpty s then None else Some((Seq.map int) s)

                     try
                         let missionContext =
                             { MissionContext.kube = kube
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
                               tier1Keys = mission.Tier1Keys
                               opCountDistribution = mission.OpCountDistribution
                               installNetworkDelay = mission.InstallNetworkDelay
                               flatNetworkDelay = mission.FlatNetworkDelay
                               simulateApplyDuration = processInputSeq mission.SimulateApplyDuration
                               simulateApplyWeight = processInputSeq mission.SimulateApplyWeight
                               peerReadingCapacity = mission.PeerReadingCapacity
                               peerFloodCapacity = mission.PeerFloodCapacity
                               sleepMainThread = mission.SleepMainThread
                               flowControlSendMoreBatchSize = mission.FlowControlSendMoreBatchSize
                               tier1OrgsToAdd = mission.Tier1OrgsToAdd
                               nonTier1NodesToAdd = mission.NonTier1NodesToAdd
                               networkSizeLimit = mission.NetworkSizeLimit
                               randomSeed = mission.RandomSeed
                               pubnetParallelCatchupStartingLedger = mission.PubnetParallelCatchupStartingLedger
                               tag = mission.Tag }

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
