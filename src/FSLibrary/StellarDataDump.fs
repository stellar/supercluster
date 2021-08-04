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
open StellarShellCmd
open System.IO
open StellarDestination
open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Rest.Serialization
open StellarCoreSet

let logName (podOrJob: string) (cmd: string) : string = sprintf "%s-%s.log" podOrJob cmd

let prevLogName (podOrJob: string) (cmd: string) : string = sprintf "%s-%s-previous.log" podOrJob cmd

let tailLogName (podOrJob: string) (cmd: string) : string = sprintf "%s-%s-tail.log" podOrJob cmd

type StellarFormation with

    member self.LaunchLogTailingTask (podName: PodName) (containerName: string) =
        let ns = self.NetworkCfg.NamespaceProperty
        self.sleepUntilNextRateLimitedApiCallTime ()

        let task =
            async {
                try
                    let! stream =
                        self.Kube.ReadNamespacedPodLogAsync(
                            name = podName.StringName,
                            namespaceParameter = ns,
                            container = containerName,
                            follow = Nullable<bool>(true)
                        )
                        |> Async.AwaitTask

                    let filename = tailLogName podName.StringName containerName
                    do! self.Destination.WriteStreamAsync filename stream
                    stream.Close()
                with :? Microsoft.Rest.HttpOperationException as ex ->
                    LogError "HTTP Operation exception: %s " (ex.Response.Content.ToString())
            }

        Async.Start task

    member self.LaunchLogTailingTasksForPod(podName: PodName) =
        let pod =
            self.Kube.ReadNamespacedPodStatus(
                name = podName.StringName,
                namespaceParameter = self.NetworkCfg.NamespaceProperty
            )

        // NB: sometimes (for no clear reason) k8s returns an empty container-status list
        // which gives us nothing to work with here. Possibly it's a startup race, but
        // given that this is (a) rare and (b) only causes us to fail-to-tail a single
        // container's logs when it happens, we just swallow the error rather than trying
        // to compensate for it (eg. retrying). You don't get a tailed log in this case.
        let containerNames =
            if isNull pod.Status.ContainerStatuses then
                []
            else
                List.map (fun (c: V1ContainerStatus) -> c.Name) (List.ofSeq pod.Status.ContainerStatuses)

        for containerName in containerNames do
            self.LaunchLogTailingTask podName containerName

    member self.LaunchLogTailingTasksForCoreSet(cs: CoreSet) =
        for i = 0 to (cs.CurrentCount - 1) do
            let podName = self.NetworkCfg.PodName cs i
            self.LaunchLogTailingTasksForPod podName

    member self.LaunchLogTailingTasksForAllPeers() =
        self.NetworkCfg.MapAllPeers
            (fun (cs: CoreSet) (i: int) ->
                let podName = self.NetworkCfg.PodName cs i
                self.LaunchLogTailingTasksForPod podName)

    member self.DumpLogs (podName: PodName) (containerName: string) =
        let ns = self.NetworkCfg.NamespaceProperty

        try
            self.sleepUntilNextRateLimitedApiCallTime ()

            let stream =
                self.Kube.ReadNamespacedPodLog(
                    name = podName.StringName,
                    namespaceParameter = ns,
                    container = containerName
                )

            let filename = logName podName.StringName containerName
            self.Destination.WriteStream filename stream
            stream.Close()

            // remove any tail log if it exists, now that we have a final Log
            let tailfile = tailLogName podName.StringName containerName
            self.Destination.RemoveIfExists tailfile

            // dump previous container log if it exists
            self.sleepUntilNextRateLimitedApiCallTime ()

            let streamPrevious =
                self.Kube.ReadNamespacedPodLog(
                    name = podName.StringName,
                    namespaceParameter = ns,
                    container = containerName,
                    previous = System.Nullable<bool>(true)
                )

            let prevfile = prevLogName podName.StringName containerName
            self.Destination.WriteStream prevfile streamPrevious
            streamPrevious.Close()
        with x -> ()

    member self.DumpJobLogs(jobName: string) =
        let ns = self.NetworkCfg.NamespaceProperty
        self.sleepUntilNextRateLimitedApiCallTime ()

        let pods =
            self.Kube.ListNamespacedPod(namespaceParameter = ns, labelSelector = "job-name=" + jobName)

        for pod in pods.Items do
            let podName = pod.Metadata.Name

            for container in pod.Spec.Containers do
                let containerName = container.Name
                self.DumpLogs(PodName podName) containerName

    member self.DumpPeerCommandLogs (command: string) (p: Peer) =
        let podName = self.NetworkCfg.PodName p.coreSet p.peerNum
        let containerName = CfgVal.stellarCoreContainerName command
        self.DumpLogs podName containerName

    member self.DumpPeerLogs(p: Peer) =
        for containerCmd in StellarCoreCfg.CfgVal.allCoreContainerCmds do
            self.DumpPeerCommandLogs containerCmd p

    member self.BackupDatabaseToHistory(p: Peer) =
        let ns = self.NetworkCfg.NamespaceProperty
        let name = self.NetworkCfg.PodName p.coreSet p.peerNum
        self.sleepUntilNextRateLimitedApiCallTime ()
        let stop_cmd = ShCmd.OfStrs [| "killall5"; "-19" |]
        let cont_cmd = ShCmd.OfStrs [| "killall5"; "-18" |]

        let backup_sql_cmd =
            ShCmd.OfStrs [| "sqlite3"
                            CfgVal.databasePath
                            sprintf ".backup \"%s\"" CfgVal.databaseBackupPath |]

        let backup_bucket_cmd =
            ShCmd.OfStrs [| "tar"
                            "cf"
                            CfgVal.bucketsBackupPath
                            "-C"
                            CfgVal.dataVolumePath
                            CfgVal.bucketsDir |]

        let cmd =
            ShCmd.ShAnd [| stop_cmd
                           backup_sql_cmd
                           backup_bucket_cmd
                           cont_cmd |]

        let task =
            self.Kube.NamespacedPodExecAsync(
                name = name.StringName,
                ``namespace`` = ns,
                command = [| "/bin/sh"; "-x"; "-c"; cmd.ToString() |],
                container = "stellar-core-run",
                tty = false,
                action = ExecAsyncCallback(fun _ _ _ -> Task.CompletedTask),
                cancellationToken = CancellationToken()
            )

        if task.GetAwaiter().GetResult() <> 0 then
            failwith "Failed to back up database and buckets"

    member self.DumpPeerDatabase(p: Peer) =
        try
            let ns = self.NetworkCfg.NamespaceProperty
            let name = self.NetworkCfg.PodName p.coreSet p.peerNum

            self.sleepUntilNextRateLimitedApiCallTime ()

            let muxedStream =
                self
                    .Kube
                    .MuxedStreamNamespacedPodExecAsync(name = name.StringName,
                                                       ``namespace`` = ns,
                                                       command = [| "sqlite3"; CfgVal.databasePath; ".dump" |],
                                                       container = "stellar-core-run",
                                                       tty = false,
                                                       cancellationToken = CancellationToken())
                    .GetAwaiter()
                    .GetResult()

            let stdOut =
                muxedStream.GetStream(Nullable<ChannelIndex>(ChannelIndex.StdOut), Nullable<ChannelIndex>())

            let error =
                muxedStream.GetStream(Nullable<ChannelIndex>(ChannelIndex.Error), Nullable<ChannelIndex>())

            let errorReader = new StreamReader(error)

            LogInfo "Dumping SQL database of peer %s" name.StringName
            muxedStream.Start()
            self.Destination.WriteStream(sprintf "%s.sql" name.StringName) stdOut
            let errors = errorReader.ReadToEndAsync().GetAwaiter().GetResult()
            let returnMessage = SafeJsonConvert.DeserializeObject<V1Status>(errors)
            Kubernetes.GetExitCodeOrThrow(returnMessage) |> ignore
        with x -> ()

    member self.DumpPeerData(p: Peer) =
        self.DumpPeerLogs p
        if p.coreSet.options.dumpDatabase then self.DumpPeerDatabase p

    member self.DumpJobData() =
        for i in self.AllJobNums do
            self.DumpJobLogs(self.NetworkCfg.JobName i)

    member self.DumpData() = self.NetworkCfg.EachPeer(self.DumpPeerData)
