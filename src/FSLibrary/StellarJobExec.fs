// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarJobExec

open k8s
open k8s.Models

open Logging
open StellarNetworkCfg
open StellarDestination
open StellarDataDump
open StellarFormation
open StellarKubeSpecs
open StellarNamespaceContent
open System
open System.Threading
open Microsoft.Rest


type JobStatusTable() =
    let mutable running = Map.empty
    let mutable finished = Map.empty
    let mutable pendingFinished = []

    member self.NoteRunning (name:string) (j:V1Job) =
        Monitor.Enter self
        assert (not (running.ContainsKey(name)))
        assert (not (finished.ContainsKey(name)))
        running <- running.Add(name, j)
        Monitor.Exit self

    member self.NoteFinished (name:string) (ok:bool) =
        Monitor.Enter self
        assert running.ContainsKey(name)
        assert (not (finished.ContainsKey(name)))
        pendingFinished <- (Map.find name running, ok) :: pendingFinished
        running <- running.Remove(name)
        finished <- finished.Add(name, ok)
        Monitor.PulseAll self
        Monitor.Exit self

    member self.IsFinished (name:string) : bool =
        Monitor.Enter self
        let b = finished.ContainsKey name
        Monitor.Exit self
        b

    member self.WaitForNextFinishWithTimeout(timeout:TimeSpan) : bool =
        Monitor.Enter self
        let n = finished.Count
        let mutable signalled = true
        while n = finished.Count && signalled do
          signalled <- Monitor.Wait(self, timeout)
        Monitor.Exit self
        signalled

    member self.WaitForNextFinish() : (V1Job * bool) =
        Monitor.Enter self
        while pendingFinished.IsEmpty do
          Monitor.Wait self |> ignore
        let res = pendingFinished.Head
        pendingFinished <- pendingFinished.Tail
        Monitor.Exit self
        res

    member self.NumRunning() : int =
        Monitor.Enter self
        let n = running.Count
        Monitor.Exit self
        n

    member self.GetFinishedTable() : Map<string,bool> =
        Monitor.Enter self
        let m = finished
        Monitor.Exit self
        m


type StellarFormation with

    member self.StartJob (j:V1Job) : V1Job =
        try
            let ns = self.NetworkCfg.NamespaceProperty
            self.sleepUntilNextRateLimitedApiCallTime()
            let j = self.Kube.CreateNamespacedJob(body=j,
                                                  namespaceParameter = ns)
            self.NamespaceContent.Add(j)
            j
        with
        | :? HttpOperationException as w ->
            LogError "err: %s" w.Message
            LogError "err: %s" w.Response.Content
            LogError "err: %s" w.Response.ReasonPhrase
            reraise()


    member self.WatchJob (j:V1Job) (jst:JobStatusTable) =
        let name = j.Metadata.Name
        let ns = j.Metadata.NamespaceProperty

        let checkStatus _ =
            self.sleepUntilNextRateLimitedApiCallTime()
            let js = self.Kube.ReadNamespacedJob(name=name, namespaceParameter=ns)
            let jobCompleted = js.Status.CompletionTime.HasValue
            let jobActive = js.Status.Active.GetValueOrDefault(0)
            let jobFailed = js.Status.Failed.GetValueOrDefault(0)
            if (jobCompleted && jobActive = 0) || jobFailed > 0
            then
                let jobSucceeded = js.Status.Succeeded.GetValueOrDefault(0)
                LogInfo "Finished job %s: %d fail / %d success"
                    name jobFailed jobSucceeded
                let ok = (jobFailed = 0) && (jobSucceeded = 1)
                jst.NoteFinished name ok

        // Unfortunately event "watches" time out and get disconnected,
        // and periodically also drop events, so we can neither rely on
        // a single event handler registration nor catching every change
        // event as they occur. Instead, we rely on fully re-reading the
        // state of the job every time the handler is (re)installed _or_
        // triggered by any event.
        let rec installHandler (firstWait:bool) =
            if firstWait
            then
                LogInfo "Waiting for job %s" name
                jst.NoteRunning name j
            else
                LogInfo "Continuing to wait for job %s" name
            checkStatus()
            if not (jst.IsFinished name)
            then
                let handler (ety:WatchEventType) (job:V1Job) =
                    if (not (jst.IsFinished name))
                    then checkStatus()
                let action = System.Action<WatchEventType, V1Job>(handler)
                let reinstall = System.Action((fun _ -> installHandler false))
                self.sleepUntilNextRateLimitedApiCallTime()
                self.Kube.WatchNamespacedJobAsync(name = name,
                                                  ``namespace`` = ns,
                                                  onEvent = action,
                                                  onClosed = reinstall) |> ignore
        installHandler true

    member self.RunSingleJob (destination:Destination)
                             (job:(string array))
                             (image:string)
                             (useConfigFile:bool) : Map<string,bool> =
        self.RunSingleJobWithTimeout destination None job image useConfigFile

    member self.RunSingleJobWithTimeout (destination:Destination)
                                        (timeout:TimeSpan option)
                                        (cmd:(string array))
                                        (image:string)
                                        (useConfigFile:bool) : Map<string,bool> =
        let startTime = DateTime.UtcNow
        let j = self.StartJobForCmd cmd image useConfigFile
        let name = j.Metadata.Name
        let jst = new JobStatusTable()
        self.WatchJob j jst
        let timeoutArg = match timeout with | None -> TimeSpan(0,0,0,0,-1) | Some x -> x
        let signalled = jst.WaitForNextFinishWithTimeout timeoutArg
        let endTime = DateTime.UtcNow
        if signalled
        then
            LogInfo "Job finished after %O (timeout %O): '%s'"
                (endTime - startTime) timeoutArg (String.Join(" ", cmd))
            jst.GetFinishedTable()
        else
            let err = (sprintf "Timeout while waiting %O for job '%s'"
                           timeoutArg (String.Join(" ", cmd)))
            LogError "%s" err
            failwith err

    member self.RunParallelJobsInRandomOrder (parallelism:int)
                                             (destination:Destination)
                                             (allJobs:((string array) array))
                                             (image:string) : Map<string,bool> =
        let jobArr = Array.copy allJobs
        let shuffle (arr:'a array) =
            let rng = System.Random()
            let rnd _ = rng.Next(arr.Length)
            let swap i j =
                let tmp = arr.[i]
                arr.[i] <- arr.[j]
                arr.[j] <- tmp
            Array.iteri (fun i _ -> swap i (rnd())) arr
        shuffle jobArr
        let mutable jobQueue = Array.toList jobArr
        self.RunParallelJobs parallelism destination
            (fun _ -> (match jobQueue with
                       | [] -> None
                       | head::tail -> jobQueue <- tail
                                       Some head)) image

    member self.RunParallelJobs (parallelism:int)
                                (destination:Destination)
                                (nextJob:(unit->(string array) option))
                                (image:string)
                                : Map<string,bool> =
        let jst = new JobStatusTable()

        let rec loop _ =
            if jst.NumRunning() < parallelism
            then addJob()
            else waitJob()

        and addJob _ =
            match nextJob() with
                 | None ->
                     if jst.NumRunning() = 0
                     then jst.GetFinishedTable()
                     else waitJob()
                 | Some(cmd) ->
                    let j = self.StartJobForCmd cmd image true
                    let name = j.Metadata.Name
                    self.WatchJob j jst
                    loop()

        and waitJob _ =
            let (j, ok) = jst.WaitForNextFinish()
            let name = j.Metadata.Name
            if ok
            then LogInfo "Job %s passed" name
            else LogInfo "Job %s failed" name
            self.FinishJob destination j
            loop()

        let oldConfig = self.NetworkCfg
        let c = parallelism * self.NetworkCfg.quotas.NumConcurrentMissions
        self.SetNetworkCfg { self.NetworkCfg with
                                 quotas = { self.NetworkCfg.quotas with
                                                NumConcurrentMissions = c } }
        let res = loop()
        self.SetNetworkCfg oldConfig
        res

    member self.CheckAllJobsSucceeded (jobs:Map<string,bool>) =
        let anyBad = ref false
        Map.iter (fun k v ->
                  if v
                  then LogInfo "Job %s passed" k
                  else (LogError "Job %s failed" k; anyBad := true)) jobs
        if !anyBad
        then failwith "One of more jobs failed"

    member self.AddDynamicPersistentVolumeClaimForJob (n:int) : unit =
        let pvc = self.NetworkCfg.ToDynamicPersistentVolumeClaimForJob n
        self.AddPersistentVolumeClaim pvc

    member self.StartJobForCmd (cmd:string array) (image:string) (useConfigFile:bool): V1Job =
        let jobNum = self.NextJobNum
        self.AddDynamicPersistentVolumeClaimForJob jobNum
        self.StartJob (self.NetworkCfg.GetJobFor jobNum cmd image useConfigFile)

    member self.FinishJob (destination:Destination) (j:V1Job) : unit =
        // We need to dump the job logs as we go and mop up the jobs
        // because the namespace we're running within has a limited
        // quota for number of jobs / pods available and a big parallel
        // catchup will exhaust that set.
        self.DumpJobLogs destination j.Metadata.Name

        // We mop up the PVCs here after each job finishes to avoid keeping
        // huge amounts of idle EBS storage allocated.
        let pvc = self.NetworkCfg.ToDynamicPersistentVolumeClaim j.Metadata.Name
        self.NamespaceContent.Del(pvc)

        self.NamespaceContent.Del(j)
