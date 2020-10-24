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


type KubeOption(kubeConfig: string,
                namespaceProperty: string option) =
    [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
             Required = false, Default = "~/.kube/config")>]
    member self.KubeConfig = kubeConfig

    [<Option("namespace", HelpText="Namespace to use, overriding kubeconfig.",
             Required = false)>]
    member self.NamespaceProperty = namespaceProperty


type CommonOptions(kubeConfig: string,
                   numNodes: int,
                   logDebugPartitions: seq<string>,
                   logTracePartitions: seq<string>,
                   containerMaxCpuMili: int,
                   containerMaxMemMebi: int,
                   numConcurrentMissions: int,
                   namespaceProperty: string option,
                   ingressDomain: string,
                   exportToPrometheus: bool,
                   probeTimeout: int,
                   image: string,
                   apiRateLimit: int) =

    [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
             Required = false, Default = "~/.kube/config")>]
    member self.KubeConfig = kubeConfig

    [<Option('n', "num-nodes", HelpText="Number of nodes in config",
             Required = false, Default = 5)>]
    member self.NumNodes = numNodes

    [<Option("debug", HelpText="Log-partitions to set to 'debug' level")>]
    member self.LogDebugPartitions = logDebugPartitions

    [<Option("trace", HelpText="Log-partitions to set to 'trace' level")>]
    member self.LogTracePartitions = logTracePartitions

    [<Option("container-max-cpu-mili", HelpText="Maximum per-container CPU (in mili-CPUs)",
             Required = false, Default = 0)>]
    member self.ContainerMaxCpuMili = containerMaxCpuMili

    [<Option("container-max-mem-mebi", HelpText="Maximum per-container memory (in MB)",
             Required = false, Default = 0)>]
    member self.ContainerMaxMemMebi = containerMaxMemMebi

    [<Option("num-concurrent-missions", HelpText="Number of missions being run concurrently (including this one)",
             Required = false, Default = 1)>]
    member self.NumConcurrentMissions = numConcurrentMissions



    [<Option("namespace", HelpText="Namespace to use, overriding kubeconfig.",
             Required = false)>]
    member self.NamespaceProperty = namespaceProperty

    [<Option("ingress-domain", HelpText="Domain in which to configure ingress host",
             Required = false, Default = "local")>]
    member self.IngressDomain = ingressDomain

    [<Option("export-to-prometheus", HelpText="Whether to export core metrics to prometheus")>]
    member self.ExportToPrometheus : bool = exportToPrometheus

    [<Option("probe-timeout", HelpText="Timeout for liveness probe",
             Required = false, Default = 5)>]
    member self.ProbeTimeout = probeTimeout

    [<Option('i', "image", HelpText="Stellar-core image to use",
             Required = false, Default = "stellar/stellar-core")>]
    member self.Image = image

    [<Option("api-rate-limit", HelpText="Limit of kubernetes API requests per second to make",
             Required = false, Default = 10)>]
    member self.ApiRateLimit = apiRateLimit

[<Verb("setup", HelpText="Set up a new stellar-core cluster")>]
type SetupOptions(kubeConfig: string,
                  numNodes: int,
                  logDebugPartitions: seq<string>,
                  logTracePartitions: seq<string>,
                  containerMaxCpuMili: int,
                  containerMaxMemMebi: int,
                  numConcurrentMissions: int,
                  namespaceProperty: string option,
                  ingressDomain: string,
                  exportToPrometheus: bool,
                  probeTimeout: int,
                  image: string,
                  apiRateLimit: int) =
    inherit CommonOptions(kubeConfig,
                          numNodes,
                          logDebugPartitions,
                          logTracePartitions,
                          containerMaxCpuMili,
                          containerMaxMemMebi,
                          numConcurrentMissions,
                          namespaceProperty,
                          ingressDomain,
                          exportToPrometheus,
                          probeTimeout,
                          image,
                          apiRateLimit)


[<Verb("clean", HelpText="Clean all resources in a namespace")>]
type CleanOptions(kubeConfig: string,
                  numNodes: int,
                  logDebugPartitions: seq<string>,
                  logTracePartitions: seq<string>,
                  containerMaxCpuMili: int,
                  containerMaxMemMebi: int,
                  numConcurrentMissions: int,
                  namespaceProperty: string option,
                  ingressDomain: string,
                  exportToPrometheus: bool,
                  probeTimeout: int,
                  image: string,
                  apiRateLimit: int) =
    inherit CommonOptions(kubeConfig,
                          numNodes,
                          logDebugPartitions,
                          logTracePartitions,
                          containerMaxCpuMili,
                          containerMaxMemMebi,
                          numConcurrentMissions,
                          namespaceProperty,
                          ingressDomain,
                          exportToPrometheus,
                          probeTimeout,
                          image,
                          apiRateLimit)


[<Verb("loadgen", HelpText="Run a load generation test")>]
type LoadgenOptions(kubeConfig: string,
                    numNodes: int,
                    logDebugPartitions: seq<string>,
                    logTracePartitions: seq<string>,
                    containerMaxCpuMili: int,
                    containerMaxMemMebi: int,
                    numConcurrentMissions: int,
                    namespaceProperty: string option,
                    ingressDomain: string,
                    exportToPrometheus: bool,
                    probeTimeout: int,
                    image: string,
                    apiRateLimit: int) =
    inherit CommonOptions(kubeConfig,
                          numNodes,
                          logDebugPartitions,
                          logTracePartitions,
                          containerMaxCpuMili,
                          containerMaxMemMebi,
                          numConcurrentMissions,
                          namespaceProperty,
                          ingressDomain,
                          exportToPrometheus,
                          probeTimeout,
                          image,
                          apiRateLimit)


[<Verb("mission", HelpText="Run one or more named missions")>]
type MissionOptions(kubeConfig: string,
                    numNodes: int,
                    logDebugPartitions: seq<string>,
                    logTracePartitions: seq<string>,
                    containerMaxCpuMili: int,
                    containerMaxMemMebi: int,
                    numConcurrentMissions: int,
                    namespaceProperty: string option,
                    ingressDomain: string,
                    exportToPrometheus: bool,
                    probeTimeout: int,
                    missions: string seq,
                    destination: string,
                    image: string,
                    oldImage: string option,
                    txRate: int,
                    maxTxRate: int,
                    numAccounts: int,
                    numTxs: int,
                    spikeSize: int,
                    spikeInterval: int,
                    keepData: bool,
                    apiRateLimit: int) =

    [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
             Required = false, Default = "~/.kube/config")>]
    member self.KubeConfig = kubeConfig

    [<Option('n', "num-nodes", HelpText="Number of nodes in config",
             Required = false, Default = 5)>]
    member self.NumNodes = numNodes

    [<Option("debug", HelpText="Log-partitions to set to 'debug' level")>]
    member self.LogDebugPartitions = logDebugPartitions

    [<Option("trace", HelpText="Log-partitions to set to 'trace' level")>]
    member self.LogTracePartitions = logTracePartitions

    [<Option("container-max-cpu-mili", HelpText="Maximum per-container CPU (in mili-CPUs)",
             Required = false, Default = 0)>]
    member self.ContainerMaxCpuMili = containerMaxCpuMili

    [<Option("container-max-mem-mebi", HelpText="Maximum per-container memory (in MB)",
             Required = false, Default = 0)>]
    member self.ContainerMaxMemMebi = containerMaxMemMebi

    [<Option("num-concurrent-missions", HelpText="Number of missions being run concurrently (including this one)",
             Required = false, Default = 1)>]
    member self.NumConcurrentMissions = numConcurrentMissions

    [<Option("namespace", HelpText="Namespace to use, overriding kubeconfig.",
             Required = false)>]
    member self.NamespaceProperty = namespaceProperty

    [<Option("ingress-domain", HelpText="Domain in which to configure ingress host",
             Required = false, Default = "local")>]
    member self.IngressDomain = ingressDomain

    [<Option("export-to-prometheus", HelpText="Whether to export core metrics to prometheus")>]
    member self.ExportToPrometheus : bool = exportToPrometheus

    [<Option("probe-timeout", HelpText="Timeout for liveness probe",
             Required = false, Default = 5)>]
    member self.ProbeTimeout = probeTimeout

    [<Value(0, Required = true)>]
    member self.Missions = missions

    [<Option('d', "destination", HelpText="Output directory for logs and sql dumps",
             Required = false, Default = "destination")>]
    member self.Destination = destination

    [<Option('i', "image", HelpText="Stellar-core image to use",
             Required = false, Default = "stellar/stellar-core")>]
    member self.Image = image

    [<Option('o', "old-image", HelpText="Stellar-core image to use as old-image",
             Required = false)>]
    member self.OldImage = oldImage

    [<Option("tx-rate", HelpText="Transaction rate for benchamarks and load generation tests",
             Required = false, Default = 100)>]
    member self.TxRate = txRate

    [<Option("max-tx-rate", HelpText="Maximum transaction rate for benchamarks and load generation tests",
             Required = false, Default = 300)>]
    member self.MaxTxRate = maxTxRate

    [<Option("num-accounts", HelpText="Number of accounts for benchamarks and load generation tests",
             Required = false, Default = 100000)>]
    member self.NumAccounts = numAccounts

    [<Option("num-txs", HelpText="Number of transactions for benchamarks and load generation tests",
             Required = false, Default = 100000)>]
    member self.NumTxs = numTxs

    [<Option("spike-size", HelpText="Number of transactions per spike for benchamarks and load generation tests",
             Required = false, Default = 100000)>]
    member self.SpikeSize = spikeSize

    [<Option("spike-interval", HelpText="A spike will occur every spikeInterval seconds for benchamarks and load generation tests",
             Required = false, Default = 0)>]
    member self.SpikeInterval = spikeInterval

    [<Option("keep-data", HelpText="Keeps namespaces and persistent volumes after mission fails",
             Required = false, Default = false)>]
    member self.KeepData = keepData

    [<Option("api-rate-limit", HelpText="Limit of kubernetes API requests per second to make",
             Required = false, Default = 10)>]
    member self.ApiRateLimit = apiRateLimit



[<Verb("poll", HelpText="Poll a running stellar-core cluster for status")>]
type PollOptions(kubeConfig: string, namespaceProperty: string option) =
    inherit KubeOption(kubeConfig, namespaceProperty)


[<EntryPoint>]
let main argv =

  let logToConsoleOnly _ =
      Log.Logger <- LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console()
                    .CreateLogger()

  let logToConsoleAndFile (file:string) =
      Log.Logger <- LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console()
                    .WriteTo.File(file)
                    .CreateLogger()

  AuxClass.CheckCSharpWorksToo()
  let result = CommandLine.Parser.Default.ParseArguments<SetupOptions,
                                                         CleanOptions,
                                                         LoadgenOptions,
                                                         MissionOptions,
                                                         PollOptions>(argv)
  match result with

  | :? Parsed<obj> as command ->
    match command.Value with

    | :? SetupOptions as setup ->
      let _ = logToConsoleOnly()
      let (kube, ns) = ConnectToCluster setup.KubeConfig setup.NamespaceProperty
      let mutable nq = kube.GetQuotas ns
      if setup.ContainerMaxCpuMili > 0
      then
          nq <- { nq with ContainerMaxCpuMili = setup.ContainerMaxCpuMili }
      if setup.ContainerMaxMemMebi > 0
      then
          nq <- { nq with ContainerMaxMemMebi = setup.ContainerMaxMemMebi }
      let coreSet = MakeLiveCoreSet "core" { CoreSetOptions.GetDefault setup.Image with nodeCount = setup.NumNodes }
      let ll = { LogDebugPartitions = List.ofSeq setup.LogDebugPartitions
                 LogTracePartitions = List.ofSeq setup.LogTracePartitions }
      let ctx = {
          kube = kube
          destination = DefaultDestination
          image = setup.Image
          oldImage = None
          txRate = 100
          maxTxRate = 300
          numAccounts = 100000
          numTxs = 100000
          spikeSize = 100000
          spikeInterval = 0
          numNodes = setup.NumNodes
          namespaceProperty = setup.NamespaceProperty.Value
          quotas = nq
          logLevels = ll
          ingressDomain = setup.IngressDomain
          exportToPrometheus = setup.ExportToPrometheus
          probeTimeout = setup.ProbeTimeout
          coreResources = SmallTestResources
          keepData = false
          apiRateLimit = setup.ApiRateLimit
      }
      let nCfg = MakeNetworkCfg ctx [coreSet] None
      use formation = kube.MakeFormation nCfg
      formation.ReportStatus()
      0

    | :? CleanOptions as clean ->
      let _ = logToConsoleOnly()
      let (kube, ns) = ConnectToCluster clean.KubeConfig clean.NamespaceProperty
      let mutable nq = { kube.GetQuotas ns with NumConcurrentMissions = clean.NumConcurrentMissions }
      if clean.ContainerMaxCpuMili > 0
      then
          nq <- { nq with ContainerMaxCpuMili = clean.ContainerMaxCpuMili }
      if clean.ContainerMaxMemMebi > 0
      then
          nq <- { nq with ContainerMaxMemMebi = clean.ContainerMaxMemMebi }
      let ll = { LogDebugPartitions = List.ofSeq clean.LogDebugPartitions
                 LogTracePartitions = List.ofSeq clean.LogTracePartitions }
      let ctx = {
          kube = kube
          destination = DefaultDestination
          image = clean.Image
          oldImage = None
          txRate = 100
          maxTxRate = 300
          numAccounts = 100000
          numTxs = 100000
          spikeSize = 100000
          spikeInterval = 0
          numNodes = clean.NumNodes
          namespaceProperty = clean.NamespaceProperty.Value
          quotas = nq
          logLevels = ll
          ingressDomain = clean.IngressDomain
          exportToPrometheus = clean.ExportToPrometheus
          probeTimeout = clean.ProbeTimeout
          coreResources = SmallTestResources
          keepData = false
          apiRateLimit = clean.ApiRateLimit
      }
      let nCfg = MakeNetworkCfg ctx [] None
      use formation = kube.MakeEmptyFormation nCfg
      formation.CleanNamespace()
      0

    | :? LoadgenOptions as loadgen ->
      let _ = logToConsoleOnly()
      let (kube, ns) = ConnectToCluster loadgen.KubeConfig loadgen.NamespaceProperty
      let mutable nq = { kube.GetQuotas ns with NumConcurrentMissions = loadgen.NumConcurrentMissions }
      if loadgen.ContainerMaxCpuMili > 0
      then
          nq <- { nq with ContainerMaxCpuMili = loadgen.ContainerMaxCpuMili }
      if loadgen.ContainerMaxMemMebi > 0
      then
          nq <- { nq with ContainerMaxMemMebi = loadgen.ContainerMaxMemMebi }
      let coreSet = MakeLiveCoreSet "core" { CoreSetOptions.GetDefault loadgen.Image with nodeCount = loadgen.NumNodes }
      let ll = { LogDebugPartitions = List.ofSeq loadgen.LogDebugPartitions
                 LogTracePartitions = List.ofSeq loadgen.LogTracePartitions }
      let ctx = {
          kube = kube
          destination = DefaultDestination
          image = loadgen.Image
          oldImage = None
          txRate = 100
          maxTxRate = 300
          numAccounts = 100000
          numTxs = 100000
          spikeSize = 100000
          spikeInterval = 0
          numNodes = loadgen.NumNodes
          namespaceProperty = loadgen.NamespaceProperty.Value
          quotas = nq
          logLevels = ll
          ingressDomain = loadgen.IngressDomain
          exportToPrometheus = loadgen.ExportToPrometheus
          probeTimeout = loadgen.ProbeTimeout
          coreResources = SmallTestResources
          keepData = false
          apiRateLimit = loadgen.ApiRateLimit
      }
      let nCfg = MakeNetworkCfg ctx [coreSet] None
      use formation = kube.MakeFormation nCfg
      formation.RunLoadgenAndCheckNoErrors coreSet
      formation.ReportStatus()
      0

    | :? MissionOptions as mission ->
      let _ = logToConsoleAndFile (sprintf "%s/stellar-supercluster.log"
                                           mission.Destination)
      let ll = { LogDebugPartitions = List.ofSeq mission.LogDebugPartitions
                 LogTracePartitions = List.ofSeq mission.LogTracePartitions }
      match Seq.tryFind (allMissions.ContainsKey >> not) mission.Missions with
        | Some e ->
            begin
                LogError "Unknown mission: %s" e
                LogError "Available missions:"
                Map.iter (fun k _ -> LogError "        %s" k) allMissions
                1
            end
        | None ->
            begin
                LogInfo "-----------------------------------"
                LogInfo "Connecting to Kubernetes cluster"
                LogInfo "-----------------------------------"
                let (kube, ns) = ConnectToCluster mission.KubeConfig mission.NamespaceProperty
                let mutable nq = { kube.GetQuotas ns with NumConcurrentMissions = mission.NumConcurrentMissions }
                if mission.ContainerMaxCpuMili > 0
                then
                    nq <- { nq with ContainerMaxCpuMili = mission.ContainerMaxCpuMili }
                if mission.ContainerMaxMemMebi > 0
                then
                    nq <- { nq with ContainerMaxMemMebi = mission.ContainerMaxMemMebi }
                let destination = Destination(mission.Destination)

                let heartbeatHandler _ =
                        try
                            let ranges = kube.ListNamespacedLimitRange(namespaceParameter=ns)
                            if isNull ranges
                            then failwith "Connection issue!" 
                            else DumpPodInfo kube ns
                        with
                        | x ->
                            LogError "Connection issue!"
                            reraise()
                        
                // Poll cluster every minute to make sure we don't have any issues
                let timer = new System.Threading.Timer(TimerCallback(heartbeatHandler), null, 1000, 60000);

                for m in mission.Missions do
                    LogInfo "-----------------------------------"
                    LogInfo "Starting mission: %s" m
                    LogInfo "-----------------------------------"
                    try
                        let missionContext = { MissionContext.kube = kube
                                               destination = destination
                                               image = mission.Image
                                               oldImage = mission.OldImage
                                               txRate = mission.TxRate
                                               maxTxRate = mission.MaxTxRate
                                               numAccounts = mission.NumAccounts
                                               numTxs = mission.NumTxs
                                               spikeSize = mission.SpikeSize
                                               spikeInterval = mission.SpikeInterval
                                               numNodes = mission.NumNodes
                                               namespaceProperty = ns
                                               quotas = nq
                                               logLevels = ll
                                               ingressDomain = mission.IngressDomain
                                               exportToPrometheus = mission.ExportToPrometheus
                                               probeTimeout = mission.ProbeTimeout
                                               coreResources = SmallTestResources
                                               keepData = mission.KeepData
                                               apiRateLimit = mission.ApiRateLimit }
                        allMissions.[m] missionContext
                    with
                    // The exception handling below is required even if we weren't logging.
                    // This looks ridiculous but it's how you coax the .NET runtime
                    // to actually run the IDisposable Dispose methods on uncaught
                    // exceptions. Sigh.
                    | x -> 
                        LogError "Exception thrown. Message = %s - StackTrace =%s" x.Message x.StackTrace
                        reraise()
                    LogInfo "-----------------------------------"
                    LogInfo "Finished mission: %s" m
                    LogInfo "-----------------------------------"

                timer.Dispose()            
                0
            end

    | :? PollOptions as poll ->
      let _ = logToConsoleOnly()
      let (kube, ns) = ConnectToCluster poll.KubeConfig poll.NamespaceProperty
      let nq = kube.GetQuotas ns
      PollCluster kube ns
      0

    | _ -> 1

  | _ -> 1
