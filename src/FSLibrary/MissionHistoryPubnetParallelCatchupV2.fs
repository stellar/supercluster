// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetParallelCatchupV2

open Logging
open StellarKubeSpecs
open StellarMissionContext
open StellarNetworkData
open StellarNetworkCfg

open System
open System.Diagnostics
open System.Net.Http

open Newtonsoft.Json.Linq
open Microsoft.FSharp.Control
open System.Threading
open System

open k8s

// Constants
let helmReleaseName = "parallel-catchup"
let helmChartPath = "/supercluster/src/MissionParallelCatchup/parallel_catchup_helm"
// let helmChartPath = "../MissionParallelCatchup/parallel_catchup_helm" // for local testing
let valuesFilePath = helmChartPath + "/values.yaml"
let jobMonitorDomainName = "ssc-job-monitor.services.stellar-ops.com"
let jobMonitorStatusEndPoint = "/status"
let jobMonitorMetricsEndPoint = "/metrics"
let jobMonitorLoggingIntervalSecs = 30 // frequency of job monitor's internal information gathering (querying core endpoint and redis metrics) and logging
let jobMonitorStatusCheckIntervalSecs = 60 // frequency of us querying job monitor's `/status` end point
let jobMonitorMetricsCheckIntervalSecs = 60 // frequency of us querying job monitor's `/metrics` end point
let jobMonitorStatusCheckTimeOutSecs = 600
let mutable toPerformCleanup = true
let failedJobLogFileLineCount = 10000
let failedJobLogStreamLineCount = 1000

let mutable nonce : String = ""
let jobMonitorHostName () = sprintf "%s.%s" nonce jobMonitorDomainName

let runCommand (command: string []) =
    try
        let psi = ProcessStartInfo()
        psi.FileName <- command.[0]
        psi.Arguments <- String.Join(" ", command.[1..])
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use ps = Process.Start(psi)
        ps.WaitForExit()

        let output = ps.StandardOutput.ReadToEnd()
        let error = ps.StandardError.ReadToEnd()

        if ps.ExitCode <> 0 then
            LogError "Command '%s' failed with error: %s" (String.Join(" ", command)) error
            None
        else
            Some(output)
    with ex ->
        LogError "Command execution failed: %s" ex.Message
        None

let installProject (context: MissionContext) =
    LogInfo "Installing Helm chart..."

    // install the project with default values from the file and overridden values from the commandline
    let setOptions = ResizeArray<string>()
    setOptions.Add(sprintf "worker.stellar_core_image=%s" context.image)
    setOptions.Add(sprintf "worker.replicas=%d" context.pubnetParallelCatchupNumWorkers)
    setOptions.Add(sprintf "range_generator.params.starting_ledger=%d" context.pubnetParallelCatchupStartingLedger)
    // Skip known results by default
    setOptions.Add(
        sprintf
            "worker.catchup_skip_known_results_for_testing=%b"
            (Option.defaultValue true context.catchupSkipKnownResultsForTesting)
    )
    // Check events consistency invariant by default
    setOptions.Add(
        sprintf
            "worker.check_events_are_consistent_with_entry_diffs=%b"
            (Option.defaultValue true context.checkEventsAreConsistentWithEntryDiffs)
    )

    // read the resource requirements defined in StellarKubeSpecs.fs (where resource for various missions are centralized)
    let resourceRequirements = ParallelCatchupCoreResourceRequirements
    let cpuReqMili = resourceRequirements.Requests.["cpu"].ToString()
    let memReqMebi = resourceRequirements.Requests.["memory"].ToString()
    let cpuLimMili = resourceRequirements.Limits.["cpu"].ToString()
    let memLimMebi = resourceRequirements.Limits.["memory"].ToString()
    let storageReqGibi = resourceRequirements.Requests.["ephemeral-storage"].ToString()
    let storageLimGibi = resourceRequirements.Limits.["ephemeral-storage"].ToString()

    LogInfo
        "Resource requirements from StellarKubeCfg:\n\
             CPU request: %s\n\
             CPU limit: %s\n\
             Memory request: %s\n\
             Memory limit: %s\n\
             Storage request: %s\n\
             Storage limit: %s"
        cpuReqMili
        cpuLimMili
        memReqMebi
        memLimMebi
        storageReqGibi
        storageLimGibi

    setOptions.Add(sprintf "worker.resources.requests.cpu=%s" cpuReqMili)
    setOptions.Add(sprintf "worker.resources.requests.memory=%s" memReqMebi)
    setOptions.Add(sprintf "worker.resources.limits.cpu=%s" cpuLimMili)
    setOptions.Add(sprintf "worker.resources.limits.memory=%s" memLimMebi)
    setOptions.Add(sprintf "worker.resources.requests.ephemeral_storage=%s" storageReqGibi)
    setOptions.Add(sprintf "worker.resources.limits.ephemeral_storage=%s" storageLimGibi)

    let endLedger =
        match context.pubnetParallelCatchupEndLedger with
        | Some value -> value
        | None -> GetLatestPubnetLedgerNumber()

    setOptions.Add(sprintf "range_generator.params.latest_ledger_num=%d" endLedger)
    setOptions.Add(sprintf "monitor.hostname=%s" (jobMonitorHostName ()))
    setOptions.Add(sprintf "monitor.path=/%s/(.*)" context.namespaceProperty)
    setOptions.Add(sprintf "monitor.logging_interval_seconds=%d" jobMonitorLoggingIntervalSecs)

    // comment out the line below when doing local testing
    Environment.SetEnvironmentVariable("KUBECONFIG", context.kubeCfg)

    runCommand [| "helm"
                  "install"
                  helmReleaseName
                  helmChartPath
                  "--values"
                  valuesFilePath
                  "--set"
                  String.Join(",", setOptions) |]
    |> ignore

    match runCommand [| "helm"
                        "get"
                        "values"
                        helmReleaseName |] with
    | Some valuesOutput -> LogInfo "%s" valuesOutput
    | _ -> ()

// Cleanup on exit
let cleanup () =
    if toPerformCleanup then
        toPerformCleanup <- false
        LogInfo "Cleaning up resources..."

        runCommand [| "helm"
                      "uninstall"
                      helmReleaseName |]
        |> ignore

System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> cleanup ())

Console.CancelKeyPress.Add
    (fun _ ->
        cleanup ()
        Environment.Exit(0))

let queryJobMonitor (path: String, endPoint: String) =
    try
        use client = new HttpClient()
        let url = "http://" + jobMonitorHostName () + path + endPoint
        let response = client.GetStringAsync(url).Result

        LogInfo "job monitor query '%s', got response: %s" url response
        let json = JObject.Parse(response)
        Some(json)
    with ex ->
        LogError "Error querying job monitor '%s': %s" endPoint ex.Message
        None


let dumpLogs (context: MissionContext, podName: String) =
    let stream =
        context.kube.ReadNamespacedPodLog(
            name = podName,
            namespaceParameter = context.namespaceProperty,
            container = "stellar-core",
            tailLines = Nullable<int> failedJobLogFileLineCount // lines to log to the file
        )
    // log the last few lines to the concole
    use reader = new System.IO.StreamReader(stream)
    let logLines = ResizeArray<string>()

    while not reader.EndOfStream do
        logLines.Add(reader.ReadLine())

    let lineStart = max 0 (logLines.Count - failedJobLogStreamLineCount)

    for i in lineStart .. logLines.Count - 1 do
        LogInfo "%s" logLines.[i]

    let filename = sprintf "%s.log" podName
    context.destination.WriteLines filename (logLines.ToArray())
    stream.Close()

let historyPubnetParallelCatchupV2 (context: MissionContext) =
    LogInfo "Running parallel catchup v2 ..."

    nonce <- (MakeNetworkNonce context.tag).ToString()
    LogDebug "nonce: '%s'" nonce

    installProject (context)

    let mutable allJobsFinished = false
    let mutable timeoutLeft = jobMonitorStatusCheckTimeOutSecs
    let mutable timeBeforeNextMetricsCheck = jobMonitorMetricsCheckIntervalSecs
    let jobMonitorPath = "/" + context.namespaceProperty

    while not allJobsFinished do
        Thread.Sleep(jobMonitorStatusCheckIntervalSecs * 1000)
        let statusOpt = queryJobMonitor (jobMonitorPath, jobMonitorStatusEndPoint)

        try
            match statusOpt with
            | Some status ->
                timeoutLeft <- jobMonitorStatusCheckTimeOutSecs
                let remainSize = status.Value<int>("num_remain")
                let jobsFailed = status.["jobs_failed"] :?> JArray
                let JobsInProgress = status.["jobs_in_progress"] :?> JArray

                if jobsFailed.Count <> 0 then
                    LogInfo "One or more jobs have failed:"

                    for job in jobsFailed do
                        let ident = job.ToString().Split('|')
                        let key = ident.[0]
                        let podName = ident.[1]
                        LogInfo "%s, logs >>> " (job.ToString())
                        dumpLogs (context, podName)
                        LogInfo "<<<"

                    failwith "Catch up failed, check logs for more info"

                if remainSize = 0 && JobsInProgress.Count = 0 then
                    // perform a final get for the metrics
                    queryJobMonitor (jobMonitorPath, jobMonitorMetricsEndPoint) |> ignore

                    // check all workers are down
                    let allWorkersDown =
                        status.["workers"] :?> JArray
                        |> Seq.forall (fun w -> (w :?> JObject).["status"].ToString() = "down")

                    if not allWorkersDown then
                        failwith "No jobs left but some workers are still running."

                    LogInfo "No job left and all workers are down."
                    allJobsFinished <- true

                // check the metrics
                timeBeforeNextMetricsCheck <- timeBeforeNextMetricsCheck - jobMonitorStatusCheckIntervalSecs

                if timeBeforeNextMetricsCheck <= 0 then
                    queryJobMonitor (jobMonitorPath, jobMonitorMetricsEndPoint) |> ignore
                    timeBeforeNextMetricsCheck <- jobMonitorMetricsCheckIntervalSecs

            | None ->
                LogError "no status"
                timeoutLeft <- timeoutLeft - jobMonitorStatusCheckIntervalSecs
                if timeoutLeft <= 0 then failwith "job monitor not reachable"
        with ex ->
            cleanup ()
            raise ex

    cleanup ()
