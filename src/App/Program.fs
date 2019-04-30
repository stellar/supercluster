// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

open CSLibrary
open CommandLine
open Serilog
open Serilog.Sinks

open StellarNetworkCfg
open StellarSupercluster

[<Verb("setup", HelpText="Set up a new stellar-core cluster")>]
type SetupOptions = {

  [<Option('k', "kubeconfig", HelpText = "Kubernetes config file",
           Required = false, Default = "~/.kube/config")>]
  kubeconfig : string

  [<Option('n', "num-nodes", HelpText="Number of nodes in config",
           Required = false, Default = 5)>]
  numNodes : int
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
  let result = CommandLine.Parser.Default.ParseArguments<SetupOptions, PollOptions>(argv)
  match result with

  | :? Parsed<obj> as command ->
    match command.Value with

    | :? SetupOptions as setup ->
      let kube = ConnectToCluster setup.kubeconfig
      let nCfg = MakeNetworkCfg setup.numNodes
      RunCluster kube nCfg
      0

    | :? PollOptions as poll ->
      let kube = ConnectToCluster poll.kubeconfig
      PollCluster kube
      0

    | _ -> 1

  | _ -> 1
