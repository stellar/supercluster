// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarDataDump

open k8s
open k8s.Models

open Logging
open StellarCoreCfg
open StellarCorePeer
open StellarFormation
open System.IO
open StellarDestination
open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Rest.Serialization
open StellarCoreSet

type StellarFormation with

    member self.DumpLogs (destination:Destination) (podName:PodName) (containerName:string) =
        let ns = self.NetworkCfg.NamespaceProperty
        try
            let stream = self.Kube.ReadNamespacedPodLog(name = podName.StringName,
                                                        namespaceParameter = ns,
                                                        container = containerName)
            destination.WriteStream ns (sprintf "%s-%s.log" podName.StringName containerName) stream
            stream.Close()
        with
        | x -> ()

    member self.DumpJobLogs (destination:Destination) (jobName:string) =
        let ns = self.NetworkCfg.NamespaceProperty
        for pod in self.Kube.ListNamespacedPod(namespaceParameter = ns,
                                               labelSelector="job-name=" + jobName).Items do
            let podName = pod.Metadata.Name
            for container in pod.Spec.Containers do
                let containerName = container.Name
                self.DumpLogs destination (PodName podName) containerName

    member self.DumpPeerCommandLogs (destination:Destination) (command:string) (p:Peer) =
        let podName = self.NetworkCfg.PodName p.coreSet p.peerNum
        let containerName = CfgVal.stellarCoreContainerName command
        self.DumpLogs destination podName containerName

    member self.DumpPeerLogs (destination : Destination) (p:Peer) =
        self.DumpPeerCommandLogs destination "new-db" p
        self.DumpPeerCommandLogs destination "new-hist" p
        self.DumpPeerCommandLogs destination "catchup" p
        self.DumpPeerCommandLogs destination "force-scp" p
        self.DumpPeerCommandLogs destination "run" p

    member self.BackupDatabaseToHistory (p:Peer) =
        let ns = self.NetworkCfg.NamespaceProperty
        let name = self.NetworkCfg.PodName p.coreSet p.peerNum
        let task = self.Kube.NamespacedPodExecAsync(name = name.StringName,
                                                    ``namespace`` = ns,
                                                    command = [|"sqlite3"; CfgVal.databasePath;
                                                                sprintf ".backup '%s'" CfgVal.databaseBackupPath |],
                                                    container = "stellar-core-run",
                                                    tty = false,
                                                    action = ExecAsyncCallback(fun _ _ _ -> Task.CompletedTask),
                                                    cancellationToken = CancellationToken())
        if task.GetAwaiter().GetResult() <> 0
        then failwith "Failed to back up database"

        let task2 = self.Kube.NamespacedPodExecAsync(name = name.StringName,
                                                     ``namespace`` = ns,
                                                     command = [|"tar"; "cf"; CfgVal.bucketsBackupPath;
                                                                 "-C"; CfgVal.dataVolumePath; CfgVal.bucketsDir |],
                                                     container = "stellar-core-run",
                                                     tty = false,
                                                     action = ExecAsyncCallback(fun _ _ _ -> Task.CompletedTask),
                                                     cancellationToken = CancellationToken())
        if task2.GetAwaiter().GetResult() <> 0
        then failwith "Failed to back up buckets"

    member self.DumpPeerDatabase (destination : Destination) (p:Peer) =
        try
            let ns = self.NetworkCfg.NamespaceProperty
            let name = self.NetworkCfg.PodName p.coreSet p.peerNum

            let muxedStream = self.Kube.MuxedStreamNamespacedPodExecAsync(
                                name = name.StringName,
                                ``namespace`` = ns,
                                command = [|"sqlite3"; CfgVal.databasePath; ".dump" |],
                                container = "stellar-core-run",
                                tty = false,
                                cancellationToken = CancellationToken()).GetAwaiter().GetResult()
            let stdOut = muxedStream.GetStream(
                           Nullable<ChannelIndex>(ChannelIndex.StdOut),
                           Nullable<ChannelIndex>())
            let error = muxedStream.GetStream(
                           Nullable<ChannelIndex>(ChannelIndex.Error),
                           Nullable<ChannelIndex>())
            let errorReader = new StreamReader(error)

            LogInfo "Dumping SQL database of peer %s" name.StringName
            muxedStream.Start()
            destination.WriteStream ns (sprintf "%s.sql" name.StringName) stdOut
            let errors = errorReader.ReadToEndAsync().GetAwaiter().GetResult()
            let returnMessage = SafeJsonConvert.DeserializeObject<V1Status>(errors);
            Kubernetes.GetExitCodeOrThrow(returnMessage) |> ignore
        with
        | x -> ()

    member self.DumpPeerData (destination : Destination) (p:Peer) =
        self.DumpPeerLogs destination p
        if p.coreSet.options.dumpDatabase
        then self.DumpPeerDatabase destination p

    member self.DumpJobData (destination : Destination) =
        for i in self.AllJobNums do
            self.DumpJobLogs destination (self.NetworkCfg.JobName i)

    member self.DumpData (destination : Destination) =
        self.NetworkCfg.EachPeer (self.DumpPeerData destination)
