// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetParallelCatchup

open k8s
open k8s.Models

open Logging
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarSupercluster
open System

let historyPubnetParallelCatchup (context : MissionContext) =
    use formation = context.MakeFormationForJob PubnetCoreSet (Some(SDFMainNet))
    let j = formation.StartJobForCmds [|
                                       [| "new-db" |];
                                       [| "offline-info" |]
                                       |]
    use event = new System.Threading.ManualResetEventSlim(false)
    let mutable jobPassed = false
    let mutable handlerActive = true
    let handler (ety:WatchEventType) (job:V1Job) =
        if handlerActive
        then
            if ety.Equals(WatchEventType.Modified)
            then
                let jobActive = job.Status.Active.GetValueOrDefault(0)
                let jobFailed = job.Status.Failed.GetValueOrDefault(0)
                LogInfo "Job watch-event (Modified) on %s: %d jobs active" (job.ToString()) jobActive
                if jobActive = 0
                then
                    LogInfo "Finished job with %d failures on %s" jobFailed (job.ToString())
                    jobPassed <- (jobFailed = 0)
                    handlerActive <- false
                    event.Set()
            else
                LogInfo "Job watch-event (%s) on %s" (ety.ToString()) (job.ToString())
    let action = System.Action<WatchEventType, V1Job>(handler)
    let task = formation.Kube.WatchNamespacedJobAsync(name = j.Metadata.Name,
                                                      ``namespace`` = j.Metadata.NamespaceProperty,
                                                      onEvent = action)
    event.Wait()
    if jobPassed
    then LogInfo "Job passed"
    else LogInfo "Job failed"
