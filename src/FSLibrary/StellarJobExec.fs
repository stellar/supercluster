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

type StellarFormation with

    member self.StartJob (j:V1Job) : V1Job =
        try
            let ns = self.NetworkCfg.NamespaceProperty
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


    member self.WatchJob (j:V1Job) : (System.Threading.WaitHandle * bool ref) =
        let name = j.Metadata.Name
        let ns = j.Metadata.NamespaceProperty
        let ok = ref false
        let event = new System.Threading.ManualResetEventSlim(false)

        let checkStatus _ =
            let js = self.Kube.ReadNamespacedJob(name=name, namespaceParameter=ns)
            let jobCompleted = js.Status.CompletionTime.HasValue
            let jobActive = js.Status.Active.GetValueOrDefault(0)
            if jobCompleted && jobActive = 0
            then
                let jobSucceeded = js.Status.Succeeded.GetValueOrDefault(0)
                let jobFailed = js.Status.Failed.GetValueOrDefault(0)
                LogInfo "Finished job %s: %d fail / %d success"
                    name jobFailed jobSucceeded
                ok := (jobFailed = 0) && (jobSucceeded = 1)
                event.Set() |> ignore

        // Unfortunately event "watches" time out and get disconnected,
        // and periodically also drop events, so we can neither rely on
        // a single event handler registration nor catching every change
        // event as they occur. Instead, we rely on fully re-reading the
        // state of the job every time the handler is (re)installed _or_
        // triggered by any event.
        let rec installHandler (firstWait:bool) =
            if firstWait
            then LogInfo "Waiting for job %s" name
            else LogInfo "Continuing to wait for job %s" name
            checkStatus()
            if not event.IsSet
            then
                let handler (ety:WatchEventType) (job:V1Job) =
                    if (not event.IsSet)
                    then checkStatus()
                let action = System.Action<WatchEventType, V1Job>(handler)
                let reinstall = System.Action((fun _ -> installHandler false))
                self.Kube.WatchNamespacedJobAsync(name = name,
                                                  ``namespace`` = ns,
                                                  onEvent = action,
                                                  onClosed = reinstall) |> ignore
        installHandler true
        (event.WaitHandle, ok)

    member self.RunSingleJob (destination:Destination)
                             (job:(string array)) : Map<string,bool> =
        self.RunParallelJobsInRandomOrder 1 destination [| job |]

    member self.RunParallelJobsInRandomOrder (parallelism:int)
                                             (destination:Destination)
                                             (allJobs:((string array) array)) : Map<string,bool> =
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
                                       Some head))

    member self.RunParallelJobs (parallelism:int)
                                (destination:Destination)
                                (nextJob:(unit->(string array) option))
                                : Map<string,bool> =
        let mutable running = Map.empty
        let mutable finished = Map.empty
        let rec loop _ =
            if running.Count < parallelism
            then addJob()
            else waitJob()

        and addJob _ =
            match nextJob() with
                 | None ->
                     if running.IsEmpty
                     then finished
                     else waitJob()
                 | Some(cmd) ->
                    let j = self.StartJobForCmd cmd
                    let name = j.Metadata.Name
                    let (waitHandle, ok) = self.WatchJob j
                    running <- running.Add(name, (j, waitHandle, ok))
                    loop()

        and waitJob _ =
            let triples = Map.toArray running
            let handles = Array.map (fun (_, (_, handle, _)) -> handle) triples
            let i = System.Threading.WaitHandle.WaitAny(handles)
            let (n, (j, _, ok)) = triples.[i]
            if !ok
            then LogInfo "Job %s passed" n
            else LogInfo "Job %s failed" n
            finished <- finished.Add(n, (!ok))
            running <- running.Remove n
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

    member self.StartJobForCmd (cmd:string array) : V1Job =
        let jobNum = self.NextJobNum
        self.AddDynamicPersistentVolumeClaimForJob jobNum
        self.StartJob (self.NetworkCfg.GetJobFor jobNum cmd)

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
