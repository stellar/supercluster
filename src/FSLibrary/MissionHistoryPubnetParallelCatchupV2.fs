// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetParallelCatchupV2

open Logging
open StellarMissionContext
open StellarNetworkData

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
let valuesFilePath = helmChartPath + "/values.yaml"
let jobMonitorHostName = "ssc-job-monitor.services.stellar-ops.com"
let jobMonitorStatusEndPoint = "/status"
let jobMonitorMetricsEndPoint = "/metrics"
let jobMonitorLoggingIntervalSecs = 300 // frequency of job monitor's internal information gathering (querying core endpoint and redis metrics) and logging
let jobMonitorStatusCheckIntervalSecs = 300 // frequency of us querying job monitor's `/status` end point
let jobMonitorMetricsCheckIntervalSecs = 300 // frequency of us querying job monitor's `/metrics` end point
let jobMonitorStatusCheckTimeOutSecs = 600
let mutable toPerformCleanup = true

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

    let endLedger =
        match context.pubnetParallelCatchupEndLedger with
        | Some value -> value
        | None -> GetLatestPubnetLedgerNumber()

    setOptions.Add(sprintf "range_generator.params.latest_ledger_num=%d" endLedger)
    setOptions.Add(sprintf "monitor.hostname=%s" jobMonitorHostName)
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
        let url = "http://" + jobMonitorHostName + path + endPoint
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
            tailLines = Nullable<int> 10000
        )

    let filename = sprintf "%s.log" podName
    context.destination.WriteStream filename stream
    stream.Close()

let historyPubnetParallelCatchupV2 (context: MissionContext) =
    LogInfo "Running parallel catchup v2 ..."

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
                    for job in jobsFailed do
                        let podName = job.ToString().Split('|').[1]
                        dumpLogs (context, podName)

                    let res = jobsFailed |> Seq.map string |> String.concat ","
                    failwith (sprintf "Catch up failed on these ranges: %s" res)

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
