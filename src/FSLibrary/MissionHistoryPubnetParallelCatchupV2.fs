// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetParallelCatchupV2

open Logging
open StellarMissionContext
open StellarNetworkData

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Collections.Generic

open Newtonsoft.Json.Linq
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions
open Microsoft.FSharp.Control
open System.Threading

// Constants
let helmReleaseName = "parallel-catchup"
let helmChartPath = "../MissionParallelCatchup/parallel_catchup_helm"
let valuesFilePath = helmChartPath + "/values.yaml"
let jobMonitorStatusCheckIntervalSecs = 30
let jobMonitorStatusCheckTimeOutSecs = 300
let mutable cleanupCalled = false

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

    let deserializer =
        DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()

    let values =
        use reader = new StreamReader(valuesFilePath)
        deserializer.Deserialize<obj>(reader)

    // Update the replicas value
    let updatedValues =
        match values with
        | :? Dictionary<obj, obj> as dict ->
            let mutable workerObj = null

            if dict.TryGetValue(box "worker", &workerObj) then
                match workerObj with
                | :? Dictionary<obj, obj> as workerDict ->
                    workerDict.[(box "stellar_core_image")] <- (box context.image)
                    workerDict.[(box "replicas")] <- (box context.pubnetParallelCatchupNumWorkers)
                    dict.[(box "worker")] <- box workerDict
                | _ -> failwith "Unexpected YAML structure"
            else
                failwith "Unexpected YAML structure"

            let mutable rangeObj = null

            if dict.TryGetValue(box "range_generator", &rangeObj) then
                match rangeObj with
                | :? Dictionary<obj, obj> as rangeDict ->
                    rangeDict.[(box "starting_ledger")] <- (box context.pubnetParallelCatchupStartingLedger)
                    rangeDict.[(box "latest_ledger_num")] <- (box (GetLatestPubnetLedgerNumber()))
                    dict.[(box "range_generator")] <- box rangeDict
                | _ -> failwith "Unexpected YAML structure"

            dict
        | _ ->
            LogError "%s" (values.ToString())
            failwith "Unexpected YAML structure"

    // Serialize the updated values to a temporary file
    let serializer =
        SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()

    let tempFile = Path.GetTempFileName()
    use writer = new StreamWriter(tempFile)
    serializer.Serialize(writer, updatedValues)
    writer.Flush()
    // install the project, then delete the temporary helm values file
    runCommand [| "helm"
                  "install"
                  helmReleaseName
                  helmChartPath
                  "--values"
                  tempFile |]
    |> ignore

    File.Delete(tempFile)

// Cleanup on exit
let cleanup () =
    if not cleanupCalled then
        cleanupCalled <- true
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

let getJobMonitorStatus () =
    try
        use client = new HttpClient()

        let response =
            client
                .GetStringAsync(
                    "http://ssc-job-monitor.services.stellar-ops.com/status"
                )
                .Result

        LogInfo "job monitor response: %s" response
        let json = JObject.Parse(response)
        Some(json)
    with ex ->
        LogError "Error querying job monitor: %s" ex.Message
        None

let historyPubnetParallelCatchupV2 (context: MissionContext) =
    LogInfo "Running parallel catchup v2 ..."

    installProject (context)

    let mutable jobFinished = false
    let mutable timeoutLeft = jobMonitorStatusCheckTimeOutSecs

    while not jobFinished do
        Thread.Sleep(jobMonitorStatusCheckIntervalSecs * 1000)
        let statusOpt = getJobMonitorStatus ()

        match statusOpt with
        | Some status ->
            timeoutLeft <- jobMonitorStatusCheckTimeOutSecs
            let remainSize = status.Value<int>("jobs_remain")
            let progressSize = status.Value<int>("jobs_in_progress")

            let allWorkersDown =
                let workers = status.["workers"] :?> JArray
                workers |> Seq.forall (fun w -> (w :?> JObject).["status"].ToString() = "down")

            if allWorkersDown && remainSize = 0 && progressSize = 0 then
                LogInfo "No job left and all workers are down."
                jobFinished <- true
        | None ->
            LogError "no status"
            timeoutLeft <- timeoutLeft - jobMonitorStatusCheckIntervalSecs
            if timeoutLeft <= 0 then failwith "job monitor not reachable"

    cleanup ()
