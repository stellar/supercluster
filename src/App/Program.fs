// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

open CSLibrary
open CommandLine
open Serilog

open Logging
open StellarCoreSet
open StellarDestination
open StellarNetworkCfg
open StellarMission
open StellarMissionContext
open StellarSupercluster


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
                   storageClass: string,
                   containerMaxCpuMili: int,
                   containerMaxMemMega: int,
                   namespaceQuotaLimCpuMili: int,
                   namespaceQuotaLimMemMega: int,
                   namespaceQuotaReqCpuMili: int,
                   namespaceQuotaReqMemMega: int,
                   numConcurrentMissions: int,
                   namespaceProperty: string option,
                   ingressDomain: string,
                   probeTimeout: int) =

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

    [<Option("storage-class", HelpText="Storage class name to use for dynamically provisioning persistent volume claims",
             Required = false, Default = "default")>]
    member self.StorageClass = storageClass

    // Getting quotas and limits right needs _seven_ arguments

    [<Option("container-max-cpu-mili", HelpText="Maximum per-container CPU (in mili-CPUs)",
             Required = false, Default = 4000)>]
    member self.ContainerMaxCpuMili = containerMaxCpuMili

    [<Option("container-max-mem-mega", HelpText="Maximum per-container memory (in MB)",
             Required = false, Default = 8000)>]
    member self.ContainerMaxMemMega = containerMaxMemMega

    [<Option("namespace-quota-lim-cpu-mili", HelpText="Namespace quota for CPU limit (in mili-CPUs)",
             Required = false, Default = 80000)>]
    member self.NamespaceQuotaLimCpuMili = namespaceQuotaLimCpuMili

    [<Option("namespace-quota-lim-mem-mega", HelpText="Namespace quota for memory limit (in MB)",
             Required = false, Default = 62000)>]
    member self.NamespaceQuotaLimMemMega = namespaceQuotaLimMemMega

    [<Option("namespace-quota-req-cpu-mili", HelpText="Namespace quota for CPU request (in mili-CPUs)",
             Required = false, Default = 10000)>]
    member self.NamespaceQuotaReqCpuMili = namespaceQuotaReqCpuMili

    [<Option("namespace-quota-req-mem-mega", HelpText="Namespace quota for memory request (in MB)",
             Required = false, Default = 22000)>]
    member self.NamespaceQuotaReqMemMega = namespaceQuotaReqMemMega

    [<Option("num-concurrent-missions", HelpText="Number of missions being run concurrently (including this one)",
             Required = false, Default = 1)>]
    member self.NumConcurrentMissions = numConcurrentMissions



    [<Option("namespace", HelpText="Namespace to use, overriding kubeconfig.",
             Required = false)>]
    member self.NamespaceProperty = namespaceProperty

    [<Option("ingress-domain", HelpText="Domain in which to configure ingress host",
             Required = false, Default = "local")>]
    member self.IngressDomain = ingressDomain

    [<Option("probe-timeout", HelpText="Timeout for liveness probe",
             Required = false, Default = 5)>]
    member self.ProbeTimeout = probeTimeout


[<Verb("setup", HelpText="Set up a new stellar-core cluster")>]
type SetupOptions(kubeConfig: string,
                  numNodes: int,
                  logDebugPartitions: seq<string>,
                  logTracePartitions: seq<string>,
                  storageClass: string,
                  containerMaxCpuMili: int,
                  containerMaxMemMega: int,
                  namespaceQuotaLimCpuMili: int,
                  namespaceQuotaLimMemMega: int,
                  namespaceQuotaReqCpuMili: int,
                  namespaceQuotaReqMemMega: int,
                  numConcurrentMissions: int,
                  namespaceProperty: string option,
                  ingressDomain: string,
                  probeTimeout: int) =
    inherit CommonOptions(kubeConfig,
                          numNodes,
                          logDebugPartitions,
                          logTracePartitions,
                          storageClass,
                          containerMaxCpuMili,
                          containerMaxMemMega,
                          namespaceQuotaLimCpuMili,
                          namespaceQuotaLimMemMega,
                          namespaceQuotaReqCpuMili,
                          namespaceQuotaReqMemMega,
                          numConcurrentMissions,
                          namespaceProperty,
                          ingressDomain,
                          probeTimeout)


[<Verb("clean", HelpText="Clean all resources in a namespace")>]
type CleanOptions(kubeConfig: string,
                  numNodes: int,
                  logDebugPartitions: seq<string>,
                  logTracePartitions: seq<string>,
                  storageClass: string,
                  containerMaxCpuMili: int,
                  containerMaxMemMega: int,
                  namespaceQuotaLimCpuMili: int,
                  namespaceQuotaLimMemMega: int,
                  namespaceQuotaReqCpuMili: int,
                  namespaceQuotaReqMemMega: int,
                  numConcurrentMissions: int,
                  namespaceProperty: string option,
                  ingressDomain: string,
                  probeTimeout: int) =
    inherit CommonOptions(kubeConfig,
                          numNodes,
                          logDebugPartitions,
                          logTracePartitions,
                          storageClass,
                          containerMaxCpuMili,
                          containerMaxMemMega,
                          namespaceQuotaLimCpuMili,
                          namespaceQuotaLimMemMega,
                          namespaceQuotaReqCpuMili,
                          namespaceQuotaReqMemMega,
                          numConcurrentMissions,
                          namespaceProperty,
                          ingressDomain,
                          probeTimeout)


[<Verb("loadgen", HelpText="Run a load generation test")>]
type LoadgenOptions(kubeConfig: string,
                    numNodes: int,
                    logDebugPartitions: seq<string>,
                    logTracePartitions: seq<string>,
                    storageClass: string,
                    containerMaxCpuMili: int,
                    containerMaxMemMega: int,
                    namespaceQuotaLimCpuMili: int,
                    namespaceQuotaLimMemMega: int,
                    namespaceQuotaReqCpuMili: int,
                    namespaceQuotaReqMemMega: int,
                    numConcurrentMissions: int,
                    namespaceProperty: string option,
                    ingressDomain: string,
                    probeTimeout: int) =
    inherit CommonOptions(kubeConfig,
                          numNodes,
                          logDebugPartitions,
                          logTracePartitions,
                          storageClass,
                          containerMaxCpuMili,
                          containerMaxMemMega,
                          namespaceQuotaLimCpuMili,
                          namespaceQuotaLimMemMega,
                          namespaceQuotaReqCpuMili,
                          namespaceQuotaReqMemMega,
                          numConcurrentMissions,
                          namespaceProperty,
                          ingressDomain,
                          probeTimeout)


[<Verb("mission", HelpText="Run one or more named missions")>]
type MissionOptions(kubeConfig: string,
                    numNodes: int,
                    logDebugPartitions: seq<string>,
                    logTracePartitions: seq<string>,
                    containerMaxCpuMili: int,
                    containerMaxMemMega: int,
                    namespaceQuotaLimCpuMili: int,
                    namespaceQuotaLimMemMega: int,
                    namespaceQuotaReqCpuMili: int,
                    namespaceQuotaReqMemMega: int,
                    numConcurrentMissions: int,
                    namespaceProperty: string option,
                    ingressDomain: string,
                    probeTimeout: int,
                    missions: string seq,
                    destination: string,
                    storageClass: string,
                    image: string option,
                    oldImage: string option,
                    txRate: int,
                    maxTxRate: int,
                    numAccounts: int,
                    numTxs: int,
                    keepData: bool) =

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

    // Getting quotas and limits right needs _seven_ arguments

    [<Option("container-max-cpu-mili", HelpText="Maximum per-container CPU (in mili-CPUs)",
             Required = false, Default = 4000)>]
    member self.ContainerMaxCpuMili = containerMaxCpuMili

    [<Option("container-max-mem-mega", HelpText="Maximum per-container memory (in MB)",
             Required = false, Default = 8000)>]
    member self.ContainerMaxMemMega = containerMaxMemMega

    [<Option("namespace-quota-lim-cpu-mili", HelpText="Namespace quota for CPU limit (in mili-CPUs)",
             Required = false, Default = 80000)>]
    member self.NamespaceQuotaLimCpuMili = namespaceQuotaLimCpuMili

    [<Option("namespace-quota-lim-mem-mega", HelpText="Namespace quota for memory limit (in MB)",
             Required = false, Default = 62000)>]
    member self.NamespaceQuotaLimMemMega = namespaceQuotaLimMemMega

    [<Option("namespace-quota-req-cpu-mili", HelpText="Namespace quota for CPU request (in mili-CPUs)",
             Required = false, Default = 10000)>]
    member self.NamespaceQuotaReqCpuMili = namespaceQuotaReqCpuMili

    [<Option("namespace-quota-req-mem-mega", HelpText="Namespace quota for memory request (in MB)",
             Required = false, Default = 22000)>]
    member self.NamespaceQuotaReqMemMega = namespaceQuotaReqMemMega

    [<Option("num-concurrent-missions", HelpText="Number of missions being run concurrently (including this one)",
             Required = false, Default = 1)>]
    member self.NumConcurrentMissions = numConcurrentMissions

    [<Option("namespace", HelpText="Namespace to use, overriding kubeconfig.",
             Required = false)>]
    member self.NamespaceProperty = namespaceProperty

    [<Option("ingress-domain", HelpText="Domain in which to configure ingress host",
             Required = false, Default = "local")>]
    member self.IngressDomain = ingressDomain

    [<Option("probe-timeout", HelpText="Timeout for liveness probe",
             Required = false, Default = 5)>]
    member self.ProbeTimeout = probeTimeout

    [<Value(0, Required = true)>]
    member self.Missions = missions

    [<Option('d', "destination", HelpText="Output directory for logs and sql dumps",
             Required = false, Default = "destination")>]
    member self.Destination = destination

    [<Option("storage-class", HelpText="Storage class name to use for dynamically provisioning persistent volume claims",
             Required = false, Default = "default")>]
    member self.StorageClass = storageClass

    [<Option('i', "image", HelpText="Stellar-core image to use",
             Required = false)>]
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

    [<Option("keep-data", HelpText="Keeps namespaces and persistent volumes after mission fails",
             Required = false, Default = false)>]
    member self.KeepData = keepData


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
      let coreSet = MakeLiveCoreSet "core" { CoreSetOptions.Default with nodeCount = setup.NumNodes }
      let ll = { LogDebugPartitions = List.ofSeq setup.LogDebugPartitions
                 LogTracePartitions = List.ofSeq setup.LogTracePartitions }
      let sc = setup.StorageClass
      let nq = MakeNetworkQuotas (setup.ContainerMaxCpuMili,
                                  setup.ContainerMaxMemMega,
                                  setup.NamespaceQuotaLimCpuMili,
                                  setup.NamespaceQuotaLimMemMega,
                                  setup.NamespaceQuotaReqCpuMili,
                                  setup.NamespaceQuotaReqMemMega,
                                  setup.NumConcurrentMissions)
      let nCfg = MakeNetworkCfg [coreSet] ns nq ll sc
                                setup.IngressDomain None
      use formation = kube.MakeFormation nCfg false setup.ProbeTimeout
      formation.ReportStatus()
      0

    | :? CleanOptions as clean ->
      let _ = logToConsoleOnly()
      let (kube, ns) = ConnectToCluster clean.KubeConfig clean.NamespaceProperty
      let ll = { LogDebugPartitions = List.ofSeq clean.LogDebugPartitions
                 LogTracePartitions = List.ofSeq clean.LogTracePartitions }
      let sc = clean.StorageClass
      let nq = MakeNetworkQuotas (clean.ContainerMaxCpuMili,
                                  clean.ContainerMaxMemMega,
                                  clean.NamespaceQuotaLimCpuMili,
                                  clean.NamespaceQuotaLimMemMega,
                                  clean.NamespaceQuotaReqCpuMili,
                                  clean.NamespaceQuotaReqMemMega,
                                  clean.NumConcurrentMissions)
      let nCfg = MakeNetworkCfg [] ns nq ll sc
                                clean.IngressDomain None
      use formation = kube.MakeEmptyFormation nCfg
      formation.CleanNamespace()
      0

    | :? LoadgenOptions as loadgen ->
      let _ = logToConsoleOnly()
      let (kube, ns) = ConnectToCluster loadgen.KubeConfig loadgen.NamespaceProperty
      let coreSet = MakeLiveCoreSet "core" { CoreSetOptions.Default with nodeCount = loadgen.NumNodes }
      let ll = { LogDebugPartitions = List.ofSeq loadgen.LogDebugPartitions
                 LogTracePartitions = List.ofSeq loadgen.LogTracePartitions }
      let sc = loadgen.StorageClass
      let nq = MakeNetworkQuotas (loadgen.ContainerMaxCpuMili,
                                  loadgen.ContainerMaxMemMega,
                                  loadgen.NamespaceQuotaLimCpuMili,
                                  loadgen.NamespaceQuotaLimMemMega,
                                  loadgen.NamespaceQuotaReqCpuMili,
                                  loadgen.NamespaceQuotaReqMemMega,
                                  loadgen.NumConcurrentMissions)
      let nCfg = MakeNetworkCfg [coreSet] ns nq ll sc
                                loadgen.IngressDomain None
      use formation = kube.MakeFormation nCfg false loadgen.ProbeTimeout
      formation.RunLoadgenAndCheckNoErrors coreSet
      formation.ReportStatus()
      0

    | :? MissionOptions as mission ->
      let _ = logToConsoleAndFile (sprintf "%s/stellar-supercluster.log"
                                           mission.Destination)
      let nq = MakeNetworkQuotas (mission.ContainerMaxCpuMili,
                                  mission.ContainerMaxMemMega,
                                  mission.NamespaceQuotaLimCpuMili,
                                  mission.NamespaceQuotaLimMemMega,
                                  mission.NamespaceQuotaReqCpuMili,
                                  mission.NamespaceQuotaReqMemMega,
                                  mission.NumConcurrentMissions)
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
                let destination = Destination(mission.Destination)

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
                                               numNodes = mission.NumNodes
                                               quotas = nq
                                               logLevels = ll
                                               ingressDomain = mission.IngressDomain
                                               storageClass = mission.StorageClass
                                               namespaceProperty = ns
                                               keepData = mission.KeepData
                                               probeTimeout = mission.ProbeTimeout }
                        allMissions.[m] missionContext
                    with
                    // This looks ridiculous but it's how you coax the .NET runtime
                    // to actually run the IDisposable Dispose methods on uncaught
                    // exceptions. Sigh.
                    | x -> reraise()
                    LogInfo "-----------------------------------"
                    LogInfo "Finished mission: %s" m
                    LogInfo "-----------------------------------"
                0
            end

    | :? PollOptions as poll ->
      let (kube, ns) = ConnectToCluster poll.KubeConfig poll.NamespaceProperty
      PollCluster kube
      0

    | _ -> 1

  | _ -> 1
