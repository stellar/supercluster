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
open StellarPersistentVolume
open StellarSupercluster


[<Verb("setup", HelpText="Set up a new stellar-core cluster")>]
type SetupOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string

  [<Option('n', "num-nodes", HelpText="Number of nodes in config",
           Required = false, Default = 5)>]
  numNodes : int

  [<Option("quota-limit-cpu", HelpText="Total quota limit for CPU (in vCPUs)",
           Required = false, Default = 100)>]
  quotaLimitCPU : int

  [<Option("quota-limit-mem-mb", HelpText="Total quota limit for memory (in MB)",
           Required = false, Default = 3200)>]
  quotaLimitMemoryMB : int

  [<Option("namespace", HelpText="Namespace to use.",
           Required = false, Default = "stellar-supercluster")>]
  namespaceProperty : string

  [<Option("ingress-domain", HelpText="Domain in which to configure ingress host",
           Required = false, Default = "local")>]
  ingressDomain : string

  [<Option("probe-timeout", HelpText="Timeout for liveness probe",
           Required = false, Default = 1)>]
  probeTimeout : int
}

[<Verb("loadgen", HelpText="Run a load generation test")>]
type LoadgenOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string

  [<Option('n', "num-nodes", HelpText="Number of nodes in config",
           Required = false, Default = 5)>]
  numNodes : int

  [<Option("quota-limit-cpu", HelpText="Total quota limit for CPU (in vCPUs)",
           Required = false, Default = 100)>]
  quotaLimitCPU : int

  [<Option("quota-limit-mem-mb", HelpText="Total quota limit for memory (in MB)",
           Required = false, Default = 3200)>]
  quotaLimitMemoryMB : int

  [<Option("namespace", HelpText="Namespace to use.",
           Required = false, Default = "stellar-supercluster")>]
  namespaceProperty : string

  [<Option("ingress-domain", HelpText="Domain in which to configure ingress host",
           Required = false, Default = "local")>]
  ingressDomain : string

  [<Option("probe-timeout", HelpText="Timeout for liveness probe",
           Required = false, Default = 1)>]
  probeTimeout : int
}

[<Verb("mission", HelpText="Run one or more named missions")>]
type MissionOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string

  [<Value(0, Required = true)>]
  missions : string seq

  [<Option("quota-limit-cpu", HelpText="Total quota limit for CPU (in vCPUs)",
           Required = false, Default = 100)>]
  quotaLimitCPU : int

  [<Option("quota-limit-mem-mb", HelpText="Total quota limit for memory (in MB)",
           Required = false, Default = 3200)>]
  quotaLimitMemoryMB : int

  [<Option("ingress-domain", HelpText="Domain in which to configure ingress host",
           Required = false, Default = "local")>]
  ingressDomain : string

  [<Option('d', "destination", HelpText="Output directory for logs and sql dumps",
           Required = false, Default = "destination")>]
  destination : string

  [<Option("persistent-volume-root", HelpText="Root for persistent volumes - some missions require this for data exchange between pods",
           Required = false, Default = "/tmp")>]
  persistentVolumeRoot : string

  [<Option('i', "image", HelpText="Stellar-core image to use",
           Required = false)>]
  image : string option

  [<Option('o', "old-image", HelpText="Stellar-core image to use as old-image",
           Required = false)>]
  oldImage : string option

  [<Option("tx-rate", HelpText="Transaction rate for benchamarks and load generation tests",
           Required = false, Default = 100)>]
  txRate : int

  [<Option("max-tx-rate", HelpText="Maximum transaction rate for benchamarks and load generation tests",
           Required = false, Default = 300)>]
  maxTxRate : int

  [<Option("num-accounts", HelpText="Number of accounts for benchamarks and load generation tests",
           Required = false, Default = 100000)>]
  numAccounts : int

  [<Option("num-txs", HelpText="Number of transactions for benchamarks and load generation tests",
           Required = false, Default = 100000)>]
  numTxs : int

  [<Option("num-nodes", HelpText="Number of nodes for benchmarks and load generation tests",
           Required = false, Default = 3)>]
  numNodes : int

  [<Option("namespace", HelpText="Namespace to use.",
           Required = false, Default = "stellar-supercluster")>]
  namespaceProperty : string

  [<Option("keep-data", HelpText="Keeps namespaces nad persistent volumes after mission fails",
           Required = false, Default = false)>]
  keepData : bool

  [<Option("probe-timeout", HelpText="Timeout for liveness probe",
           Required = false, Default = 1)>]
  probeTimeout : int
}


[<Verb("poll", HelpText="Poll a running stellar-core cluster for status")>]
type PollOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string

  [<Option("ingress-domain", HelpText="Domain in which to configure ingress host",
           Required = false, Default = "local")>]
  ingressDomain : string
}


[<EntryPoint>]
let main argv =

  Log.Logger <- LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console().CreateLogger()

  AuxClass.CheckCSharpWorksToo()
  let result = CommandLine.Parser.Default.ParseArguments<SetupOptions,
                                                         LoadgenOptions,
                                                         MissionOptions,
                                                         PollOptions>(argv)
  match result with

  | :? Parsed<obj> as command ->
    match command.Value with

    | :? SetupOptions as setup ->
      let kube = ConnectToCluster setup.kubeconfig
      let coreSet = MakeLiveCoreSet "core" { CoreSetOptions.Default with nodeCount = setup.numNodes }
      let nCfg = MakeNetworkCfg [coreSet]
                                setup.namespaceProperty
                                setup.quotaLimitCPU
                                setup.quotaLimitMemoryMB
                                setup.ingressDomain None
      use formation = kube.MakeFormation nCfg None false setup.probeTimeout
      formation.ReportStatus()
      0

    | :? LoadgenOptions as loadgen ->
      let kube = ConnectToCluster loadgen.kubeconfig
      let coreSet = MakeLiveCoreSet "core" { CoreSetOptions.Default with nodeCount = loadgen.numNodes }
      let nCfg = MakeNetworkCfg [coreSet]
                                loadgen.namespaceProperty
                                loadgen.quotaLimitCPU
                                loadgen.quotaLimitMemoryMB
                                loadgen.ingressDomain None
      use formation = kube.MakeFormation nCfg None false loadgen.probeTimeout
      formation.RunLoadgenAndCheckNoErrors coreSet
      formation.ReportStatus()
      0

    | :? MissionOptions as mission ->
      match Seq.tryFind (allMissions.ContainsKey >> not) mission.missions with
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
                let kube = ConnectToCluster mission.kubeconfig
                let destination = Destination(mission.destination)
                let persistentVolume = PersistentVolume(mission.persistentVolumeRoot)

                try
                    for m in mission.missions do
                        LogInfo "-----------------------------------"
                        LogInfo "Starting mission: %s" m
                        LogInfo "-----------------------------------"
                        try
                            let missionContext = { MissionContext.kube = kube
                                                   destination = destination
                                                   image = mission.image
                                                   oldImage = mission.oldImage
                                                   txRate = mission.txRate
                                                   maxTxRate = mission.maxTxRate
                                                   numAccounts = mission.numAccounts
                                                   numTxs = mission.numTxs
                                                   numNodes = mission.numNodes
                                                   quotaLimitCPU = mission.quotaLimitCPU
                                                   quotaLimitMemoryMB = mission.quotaLimitMemoryMB
                                                   ingressDomain = mission.ingressDomain
                                                   persistentVolume = persistentVolume
                                                   namespaceProperty = mission.namespaceProperty
                                                   keepData = mission.keepData
                                                   probeTimeout = mission.probeTimeout }
                            allMissions.[m] missionContext
                        with
                        // This looks ridiculous but it's how you coax the .NET runtime
                        // to actually run the IDisposable Dispose methods on uncaught
                        // exceptions. Sigh.
                        | x -> reraise()
                        LogInfo "-----------------------------------"
                        LogInfo "Finished mission: %s" m
                        LogInfo "-----------------------------------"
                    finally
                        persistentVolume.Cleanup()
                0
            end

    | :? PollOptions as poll ->
      let kube = ConnectToCluster poll.kubeconfig
      PollCluster kube
      0

    | _ -> 1

  | _ -> 1
