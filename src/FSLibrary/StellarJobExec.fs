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
open System
open System.Threading
open Microsoft.Rest


type JobStatusTable() =
    let mutable running = Map.empty
    let mutable finished = Map.empty
    let mutable pendingFinished = []
    let mutable pendingObsoleteTasks = []

    member self.NoteRunning (name:string) (j:V1Job) (task:Tasks.Task<Watcher<V1Job>>) =
        lock self
            begin fun _ ->
                // We bounce tasks off the pendingObsoleteTasks list because
                // NoteRunning is called from the onClosed callback when the
                // task hasn't actually finished running and we can't Dispose
                // of it yet. We _can_ call Dispose on tasks from the previous
                // call to NoteRunning; we then queue up the current task for
                // Disposal on the _next_ call.
                for (_, t:Tasks.Task<Watcher<V1Job>>) in pendingObsoleteTasks do
                        if t <> null && t.IsCompleted
                        then t.Dispose()
                    done
                GC.Collect();
                GC.WaitForPendingFinalizers();
                pendingObsoleteTasks <- []
                match Map.tryFind name running with
                    | None -> ()
                    | Some((_, t)) ->
                        pendingObsoleteTasks <- (name, t) :: pendingObsoleteTasks
                        running <- Map.remove name running

                assert (not (running.ContainsKey(name)))
                assert (not (finished.ContainsKey(name)))
                running <- running.Add(name, (j, task))
                Interlocked.MemoryBarrier()
            end

    member self.NoteFinished (name:string) (ok:bool) =
        lock self
            begin fun _ ->
                if (not (running.ContainsKey(name))) then ()
                else
                assert (not (finished.ContainsKey(name)))
                let (job, task) = Map.find name running
                pendingObsoleteTasks <- (name, task) :: pendingObsoleteTasks
                pendingFinished <- (job, ok) :: pendingFinished
                running <- running.Remove(name)
                finished <- finished.Add(name, ok)
                LogInfo "Awakening any waiters"
                Interlocked.MemoryBarrier();
                Monitor.PulseAll self
            end

    member self.IsFinished (name:string) : bool =
        lock self (fun _ -> finished.ContainsKey name)

    member self.WaitForNextFinishWithTimeout(timeout:TimeSpan) : bool =
        lock self
            begin fun _ ->
                let mutable signalled = true
                while pendingFinished.IsEmpty && signalled do
                  signalled <- Monitor.Wait(self, timeout)
                if not pendingFinished.IsEmpty
                then pendingFinished <- pendingFinished.Tail
                Interlocked.MemoryBarrier()
                signalled
            end

    member self.WaitForNextFinish (checkJobs) (timeout:TimeSpan) : (V1Job * bool) =
        lock self
            begin fun _ ->
                while pendingFinished.IsEmpty do
                    LogInfo "Waiting for next finish"
                    Monitor.Wait(self, timeout) |> ignore
                    LogInfo "Woke from waiting for next finish" 
                    checkJobs()
                let res = pendingFinished.Head
                pendingFinished <- pendingFinished.Tail
                Interlocked.MemoryBarrier()
                res
            end

    member self.HavePendingFinished() : bool =
        lock self (fun _ -> not pendingFinished.IsEmpty)

    member self.NumRunning() : int =
        lock self (fun _ -> running.Count)

    member self.NumFinished() : int =
        lock self (fun _ -> finished.Count)

    member self.GetFinishedTable() : Map<string,bool> =
        lock self (fun _ -> finished)


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

    member self.CheckJob (j:V1Job) (jst:JobStatusTable) =
        let name = j.Metadata.Name
        let ns = j.Metadata.NamespaceProperty
        
        let jobIsCompleted = j.Status.CompletionTime.HasValue
        let jobActivePodCount = j.Status.Active.GetValueOrDefault(0)
        let jobFailedPodCount = j.Status.Failed.GetValueOrDefault(0)
        let jobSucceededPodCount = j.Status.Succeeded.GetValueOrDefault(0)

        // Check if any containers were terminated for a non-OOM reason. We fail on the first non-OOM failure
        // seen, but we allow two failures if they are due to an OOM kill.
        let mutable isNonOomFail = false
        
        self.sleepUntilNextRateLimitedApiCallTime()
        for pod in self.Kube.ListNamespacedPod(namespaceParameter = ns, labelSelector="job-name=" + name).Items do
            if pod.Status.ContainerStatuses <> null
            then 
                for status in pod.Status.ContainerStatuses do
                    if status.State <> null 
                        && status.State.Terminated <> null 
                        && status.State.Terminated.ExitCode <> 0 // Success
                        && status.State.Terminated.ExitCode <> 137 // OOM
                    then
                        isNonOomFail <- true   
                done
        done

        if (jobIsCompleted && jobActivePodCount = 0) || jobFailedPodCount > 2 || isNonOomFail
        then
            LogInfo "Finished job %s: %d fail / %d success"
                name jobFailedPodCount jobSucceededPodCount
            let ok = (jobSucceededPodCount = 1)
            jst.NoteFinished name ok

    member self.WatchJob (j:V1Job) (jst:JobStatusTable) =
        let name = j.Metadata.Name
        let ns = j.Metadata.NamespaceProperty

        let checkStatus _ =
            self.sleepUntilNextRateLimitedApiCallTime()
            let js = self.Kube.ReadNamespacedJob(name=name, namespaceParameter=ns)
            self.CheckJob js jst
        // Unfortunately event "watches" time out and get disconnected,
        // and periodically also drop events, so we can neither rely on
        // a single event handler registration nor catching every change
        // event as they occur. Instead, we rely on fully re-reading the
        // state of the job every time the handler is (re)installed _or_
        // triggered by any event.
        let rec installHandler (firstWait:bool) =
            checkStatus()
            if jst.IsFinished name
            then LogInfo "Dropping handler for completed job %s" name
            else
                if firstWait
                then
                    LogInfo "Starting to wait for job %s" name
                else
                    LogInfo "Continuing to wait for job %s" name
                let handler (ety:WatchEventType) (job:V1Job) =
                    if (not (jst.IsFinished name))
                    then checkStatus()
                let action = System.Action<WatchEventType, V1Job>(handler)
                let reinstall = System.Action((fun _ -> installHandler false))
                self.sleepUntilNextRateLimitedApiCallTime()
                let task = self.Kube.WatchNamespacedJobAsync(name = name,
                                                             ``namespace`` = ns,
                                                             onEvent = action,
                                                             onClosed = reinstall)
                jst.NoteRunning name j task

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
        let mutable moreJobs = true

        // We check to see if there are pods that have been in "Pending"
        // state for more than 120 minutes. This typically means the cluster
        // is low on fixed resources and isn't actually going to be able to
        // run our parallel-job-set to completion, so we prefer to fail early
        // in this case.
        let podBuildupTimeoutMinutes = 120
        let podBuildupCheckMinutes = 3
        let mutable lastPodBuildupCheckTime = DateTime.UtcNow
        let checkPendingPodBuildup () : unit =
            let now = DateTime.UtcNow
            if now.Subtract(lastPodBuildupCheckTime).Minutes >= podBuildupCheckMinutes
            then
                begin
                    lastPodBuildupCheckTime <- now
                    let ns = self.NetworkCfg.NamespaceProperty
                    LogInfo "Checking for pod buildup"
                    for pod in self.Kube.ListNamespacedPod(namespaceParameter=ns).Items do
                        if (pod.Status.Phase = "Pending" ||
                            (pod.Status.ContainerStatuses.Count > 0 &&
                             pod.Status.ContainerStatuses.[0].Ready = false)) &&
                           pod.Metadata.CreationTimestamp.HasValue &&
                           now.Subtract(pod.Metadata.CreationTimestamp.Value).Minutes > podBuildupTimeoutMinutes
                        then
                            failwith (sprintf "Pod '%s' has been 'Pending' for more than %d minutes, cluster resources likely exhausted"
                                          pod.Metadata.Name podBuildupTimeoutMinutes)
                    done
                    LogInfo "Did not find pod buildup"
                end

        let waitJob () : unit =
            LogInfo "Waiting for next job to finish"

            // We monitor the jobs in case the watch handlers drop due to a connection issue 
            let checkJobs = fun () -> 
                self.sleepUntilNextRateLimitedApiCallTime()
                for job in self.Kube.ListNamespacedJob(namespaceParameter=self.NetworkCfg.NamespaceProperty).Items do
                    self.CheckJob job jst

            let (j, ok) = jst.WaitForNextFinish (checkJobs) (TimeSpan(0,10,0))
            let name = j.Metadata.Name
            if ok
            then LogInfo "Job %s passed" name
            else failwith ("Job " + name + " failed")
            try
                self.FinishJob destination j
            with
                | e ->
                    LogError "Error occurred during cleanup of job %s" name
                    raise e
            LogInfo "Finished cleaning up after job %s" name

        let addJob () : unit =
            LogInfo "Adding a job (numRunning = %d)" (jst.NumRunning())
            match nextJob() with
                 | None ->
                     if jst.NumRunning() = 0
                     then moreJobs <- false
                     else waitJob()
                 | Some(cmd) ->
                    let j = self.StartJobForCmd cmd image true
                    self.WatchJob j jst

        while moreJobs do
            checkPendingPodBuildup()
            if jst.HavePendingFinished()
            then waitJob()
            if jst.NumRunning() < parallelism
            then addJob()
            else waitJob()
        done
        LogInfo "Finished parallel-job loop"

        jst.GetFinishedTable()

    member self.CheckAllJobsSucceeded (jobs:Map<string,bool>) =
        let anyBad = ref false
        Map.iter (fun k v ->
                  if v
                  then LogInfo "Job %s passed" k
                  else (LogError "Job %s failed" k; anyBad := true)) jobs
        if !anyBad
        then failwith "One of more jobs failed"

    member self.StartJobForCmd (cmd:string array) (image:string) (useConfigFile:bool): V1Job =
        let jobNum = self.NextJobNum
        self.StartJob (self.NetworkCfg.GetJobFor jobNum cmd image useConfigFile)

    member self.FinishJob (destination:Destination) (j:V1Job) : unit =
        // We need to dump the job logs as we go and mop up the jobs
        // because the namespace we're running within has a limited
        // quota for number of jobs / pods available and a big parallel
        // catchup will exhaust that set.
        self.DumpJobLogs destination j.Metadata.Name
        self.NamespaceContent.Del(j)
