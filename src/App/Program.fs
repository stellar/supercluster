// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

open CSLibrary
open CommandLine
open Serilog
open Serilog.Sinks

open Logging
open StellarNetworkCfg
open StellarSupercluster
open StellarMission

[<Verb("setup", HelpText="Set up a new stellar-core cluster")>]
type SetupOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string

  [<Option('n', "num-nodes", HelpText="Number of nodes in config",
           Required = false, Default = 5)>]
  numNodes : int
}

[<Verb("loadgen", HelpText="Run a load generation test")>]
type LoadgenOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string

  [<Option('n', "num-nodes", HelpText="Number of nodes in config",
           Required = false, Default = 5)>]
  numNodes : int
}

[<Verb("mission", HelpText="Run one or more named missions")>]
type MissionOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string

  [<Value(0, Required = true)>]
  missions : string seq
}


[<Verb("poll", HelpText="Poll a running stellar-core cluster for status")>]
type PollOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string
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
      let nCfg = MakeNetworkCfg setup.numNodes
      use formation = kube.MakeFormation nCfg
      formation.ReportStatus()
      0

    | :? LoadgenOptions as loadgen ->
      let kube = ConnectToCluster loadgen.kubeconfig
      let nCfg = MakeNetworkCfg loadgen.numNodes
      use formation = kube.MakeFormation nCfg
      formation.RunLoadgenAndCheckNoErrors()
      formation.ReportStatus()
      0

    | :? MissionOptions as mission ->
      match Seq.tryFind (fun n -> not (allMissions.ContainsKey n)) mission.missions with
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
                for m in mission.missions do
                    LogInfo "-----------------------------------"
                    LogInfo "Starting mission: %s" m
                    LogInfo "-----------------------------------"
                    try
                        allMissions.[m](kube)
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
      let kube = ConnectToCluster poll.kubeconfig
      PollCluster kube
      0

    | _ -> 1

  | _ -> 1
