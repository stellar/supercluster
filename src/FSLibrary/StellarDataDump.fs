// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarDataDump

open k8s
open k8s.Models

open StellarCoreCfg
open StellarCorePeer
open StellarSupercluster
open System.IO
open StellarDestination
open System
open System.Threading
open Microsoft.Rest.Serialization

type ClusterFormation with
    member self.DumpPeerCommandLogs (destination : Destination) (command:string) (p:Peer) =
        let ns = self.NetworkCfg.NamespaceProperty
        let name = self.NetworkCfg.PeerShortName p.coreSet p.peerNum

        try
            let stream = self.Kube.ReadNamespacedPodLog(name = name,
                                                        namespaceParameter = ns,
                                                        container = sprintf "stellar-core-%s" command)
            destination.WriteStream ns (sprintf "%s-%s.log" name command) stream
            stream.Close()
        with
        | x -> ()

    member self.DumpPeerLogs (destination : Destination) (p:Peer) =
        self.DumpPeerCommandLogs destination "new-db" p
        self.DumpPeerCommandLogs destination "new-hist" p
        self.DumpPeerCommandLogs destination "catchup" p
        self.DumpPeerCommandLogs destination "force-scp" p
        self.DumpPeerCommandLogs destination "run" p

    member self.DumpPeerDatabase (destination : Destination) (p:Peer) =
        try
            let ns = self.NetworkCfg.NamespaceProperty
            let name = self.NetworkCfg.PeerShortName p.coreSet p.peerNum

            let muxedStream = self.Kube.MuxedStreamNamespacedPodExecAsync(
                                name = name,
                                ``namespace`` = ns,
                                command = [|"sqlite3"; CfgVal.databasePath; ".dump" |],
                                container = "sqlite",
                                tty = false,
                                cancellationToken = CancellationToken()).GetAwaiter().GetResult()
            let stdOut = muxedStream.GetStream(
                           Nullable<ChannelIndex>(ChannelIndex.StdOut),
                           Nullable<ChannelIndex>())
            let error = muxedStream.GetStream(
                           Nullable<ChannelIndex>(ChannelIndex.Error),
                           Nullable<ChannelIndex>())
            let errorReader = new StreamReader(error)

            muxedStream.Start()
            destination.WriteStream ns (sprintf "%s.sql" name) stdOut
            let errors = errorReader.ReadToEndAsync().GetAwaiter().GetResult()
            let returnMessage = SafeJsonConvert.DeserializeObject<V1Status>(errors);
            Kubernetes.GetExitCodeOrThrow(returnMessage) |> ignore
        with
        | x -> ()  

    member self.DumpPeerData (destination : Destination) (p:Peer) =
        self.DumpPeerLogs destination p
        self.DumpPeerDatabase destination p

    member self.DumpData (destination : Destination) =
        self.NetworkCfg.EachPeer (self.DumpPeerData destination)
