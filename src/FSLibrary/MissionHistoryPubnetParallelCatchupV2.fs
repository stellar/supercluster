// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryPubnetParallelCatchupV2

open Logging
open ScriptUtils
open StellarKubeSpecs
open StellarMissionContext
open StellarNetworkData
open StellarNetworkCfg
open StellarSupercluster

open System
open System.Diagnostics
open System.Net.Http
open System.IO

open Newtonsoft.Json.Linq
open Microsoft.FSharp.Control
open System.Threading
open System

open k8s
open CSLibrary

// Constants
// Baked into the supercluster image at this path. Overridable so a local run
// can point at a working copy without editing this file.
let helmChartPath =
    match Environment.GetEnvironmentVariable("SUPERCLUSTER_CHART_PATH") with
    | null
    | "" -> "/supercluster/src/MissionParallelCatchup/parallel_catchup_helm"
    | p -> p

// Comment out the path below for local testing
// Example command to run local testing (in the `supercluster/` directory):
// $ dotnet run --project src/App/App.fsproj -- mission HistoryPubnetParallelCatchupV2 --image=docker-registry.services.stellar-ops.com/dev/stellar-core:23.0.3-2779.4d1df2b03.jammy-vnext-buildtests  --pubnet-parallel-catchup-num-workers=2 --pubnet-parallel-catchup-starting-ledger=0 --pubnet-parallel-catchup-end-ledger=6400 --pubnet-parallel-catchup-ledgers-per-job 1280  --destination ./logs
// let helmChartPath = "src/MissionParallelCatchup/parallel_catchup_helm"
let valuesFilePath = helmChartPath + "/values.yaml"

// Keys in the <release>-catchup-progress ConfigMap. These were HTTP paths when
// the driver polled the monitor through a Gateway; it reads the ConfigMap now.
let jobMonitorStatusKey = "status.json" // live queue counts

let jobMonitorProgressKey = "progress.json" // durable per-range completion record
let jobMonitorLoggingIntervalSecs = 30 // frequency of the monitor reconcile loop: dispatch, liveness ping, status publish
let jobMonitorStatusCheckIntervalSecs = 60 // frequency of us querying job monitor's `/status` end point
let jobMonitorStatusCheckTimeOutSecs = 600
let mutable toPerformCleanup = true
let failedJobLogFileLineCount = 10000
let failedJobLogStreamLineCount = 1000

let mutable nonce : String = ""
let mutable helmReleaseName : String = ""

// Resolve --pubnet-parallel-catchup-profile into a ConfigMap the monitor mounts.
//
// Accepts a local path or an https URL, so a profile can come off disk or
// straight from a raw paste/gist link. Returns the ConfigMap name, or None to
// size from the configured requests.
//
// Never fatal: a profile only tightens requests, so a run must still start when
// one cannot be fetched. Failing the mission here would turn an optimisation
// into a dependency.
let resolveRangeProfile (context: MissionContext) : string option =
    let spec = context.pubnetParallelCatchupProfile

    if String.IsNullOrWhiteSpace spec then
        None
    else
        // The options are built before the helm install sets this, and the
        // ConfigMap has to land in the same cluster and namespace the release
        // will use.
        Environment.SetEnvironmentVariable("KUBECONFIG", ExpandHomeDirTilde context.kubeCfg)

        try
            let body =
                if spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
                    use client = new HttpClient()
                    client.Timeout <- TimeSpan.FromSeconds(30.0)
                    client.GetStringAsync(spec) |> Async.AwaitTask |> Async.RunSynchronously
                else
                    File.ReadAllText(ExpandHomeDirTilde spec)

            // Parse before shipping it: a 404 page or a truncated download would
            // otherwise reach the monitor as an unreadable mount.
            let doc = JObject.Parse(body)

            let count =
                match doc.["ranges"] with
                | :? JObject as r -> r.Count
                | _ -> 0

            if count = 0 then
                LogWarn "Range profile %s has no ranges; sizing from configured requests" spec
                None
            else
                let name = sprintf "%s-range-profile" helmReleaseName
                let file = Path.Combine(Path.GetTempPath(), sprintf "%s-profile.json" helmReleaseName)
                File.WriteAllText(file, body)

                RunShellCommand [| "kubectl"
                                   "create"
                                   "configmap"
                                   name
                                   "--namespace"
                                   context.namespaceProperty
                                   sprintf "--from-file=profile.json=%s" file |]
                |> ignore

                LogInfo "Range profile: %d ranges from %s -> configmap %s" count spec name
                Some name
        with ex ->
            LogWarn "Could not load range profile %s (%s); sizing from configured requests" spec ex.Message
            None


// Helper functions to convert label/taint tuples to Helm-compatible format using indexed notation
let requireNodeLabelToHelmIndexed (index: int) ((key: string), (value: string option)) =
    match value with
    | None -> sprintf "worker.requireNodeLabels[%d].key=%s,worker.requireNodeLabels[%d].operator=Exists" index key index
    | Some v ->
        sprintf
            "worker.requireNodeLabels[%d].key=%s,worker.requireNodeLabels[%d].operator=In,worker.requireNodeLabels[%d].values[0]=\"%s\""
            index
            key
            index
            index
            v

let avoidNodeLabelToHelmIndexed (index: int) ((key: string), (value: string option)) =
    match value with
    | None ->
        sprintf "worker.avoidNodeLabels[%d].key=%s,worker.avoidNodeLabels[%d].operator=DoesNotExist" index key index
    | Some v ->
        sprintf
            "worker.avoidNodeLabels[%d].key=%s,worker.avoidNodeLabels[%d].operator=NotIn,worker.avoidNodeLabels[%d].values[0]=\"%s\""
            index
            key
            index
            index
            v

let tolerateTaintToHelmIndexed (index: int) ((key: string), (effect: string option)) =
    let effectValue = Option.defaultValue "NoSchedule" effect
    sprintf "worker.tolerateNodeTaints[%d].key=%s,worker.tolerateNodeTaints[%d].effect=%s" index key index effectValue

let serviceAccountAnnotationsToHelmIndexed (index: int) (key: string, value: string) =
    sprintf "service_account.annotations[%d].key=%s,service_account.annotations[%d].value=%s" index key index value

let installProject (context: MissionContext) =
    LogInfo "Installing Helm chart with release name: %s" helmReleaseName

    // install the project with default values from the file and overridden values from the commandline
    let setOptions = ResizeArray<string>()
    setOptions.Add(sprintf "worker.stellar_core_image=%s" context.image)
    setOptions.Add(sprintf "worker.replicas=%d" context.pubnetParallelCatchupNumWorkers)

    // pvc: /data survives the pod so an evicted range resumes at L+1 (what makes
    // spot viable). ephemeral: /data is an emptyDir on the node -- denser
    // packing, no resume. The ephemeral-storage request below must match:
    // ~2Gi for pvc, ~35Gi for ephemeral, or the monitor logs a loud mismatch.
    setOptions.Add(sprintf "worker.storageMode=%s" context.pubnetParallelCatchupStorageMode)

    match resolveRangeProfile context with
    | Some cm -> setOptions.Add(sprintf "monitor.profileConfigMap=%s" cm)
    | None -> ()

    setOptions.Add(sprintf "range.order=%s" context.pubnetParallelCatchupRangeOrder)

    setOptions.Add(sprintf "range.startingLedger=%d" context.pubnetParallelCatchupStartingLedger)

    let endLedger =
        match context.pubnetParallelCatchupEndLedger with
        | Some value -> value
        | None -> GetLatestPubnetLedgerNumber()

    setOptions.Add(sprintf "range.latestLedgerNum=%d" endLedger)

    setOptions.Add(sprintf "range.ledgersPerJob=%d" context.pubnetParallelCatchupLedgersPerJob)

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

    // Only the ephemeral-storage pair is taken from StellarKubeSpecs. Its cpu and
    // memory belong to the V1 parallel catchup missions, which share that spec and
    // run a different execution model at parallelism 128/256 -- sizing V2 there
    // silently resized them too. V2's own worker cpu/memory are the chart defaults
    // in parallel_catchup_helm/values.yaml, overridable per run with
    // --pubnet-parallel-catchup-cpu-request.
    let resourceRequirements = ParallelCatchupCoreResourceRequirements
    // StellarKubeSpecs sizes ephemeral-storage for ephemeral mode, where /data is
    // an emptyDir on the node. In pvc mode /data is on the volume and the node
    // disk only holds logs and tmp, so asking for the full amount reserves disk
    // nothing uses -- and makes disk, not cpu, the binding dimension for packing.
    let storageReqGibi, storageLimGibi =
        if context.pubnetParallelCatchupStorageMode = "pvc" then
            "2Gi", "4Gi"
        else
            resourceRequirements.Requests.["ephemeral-storage"].ToString(),
            resourceRequirements.Limits.["ephemeral-storage"].ToString()

    LogInfo
        "Worker storage from StellarKubeCfg:\n\
             Storage request: %s\n\
             Storage limit: %s\n\
             (cpu and memory come from the chart; workers run with no cpu or memory limit)"
        storageReqGibi
        storageLimGibi

    // This is the DEFAULT cpu request, not a ceiling. The monitor does NOT clamp
    // profile-derived cpu to it: _slack_cpu returns the tier value straight
    // through, so a PROFILE_CPU_TIERS band above this value really is issued --
    // verified 2026-07-31, tiers of 1.5 and 2.0 rendered under a 1250m REQ_CPU.
    // It applies to ranges the profile cannot size at all.
    // Pushed only when the run explicitly asks. Otherwise the chart default stands,
    // so the chart is the single place V2's worker sizing is written down.
    if not (String.IsNullOrWhiteSpace context.pubnetParallelCatchupCpuRequest) then
        setOptions.Add(
            sprintf "worker.resources.requests.cpu=%s" context.pubnetParallelCatchupCpuRequest
        )

    setOptions.Add(sprintf "worker.resources.requests.ephemeral_storage=%s" storageReqGibi)
    setOptions.Add(sprintf "worker.resources.limits.ephemeral_storage=%s" storageLimGibi)

    // Construct command for fetching history files from S3 for core node
    // `index` and set the corresponding Helm option
    let setS3HistoryGetCommand (url: string) (index: int) =
        if index < 1 || index > 3 then
            failwith "s3HistoryGetCommand: index must be between 1 and 3 inclusive"

        // --no-progress is load-bearing, not cosmetic. The AWS CLI draws its
        // transfer meter with carriage returns and no newline, so a 628 MiB
        // bucket download arrives as one multi-megabyte "line". The log
        // collector reads the pod stream line-wise and aiohttp aborts any line
        // over 512 KiB, so every large download killed its own stream, which
        // then reconnected and hit the same wall. Measured on ssc-test
        // 2026-07-30: it starved every retry pod of a collector stream.
        let s3GetCommandBase = sprintf "aws s3 cp --no-progress --region %s" context.s3HistoryMirrorRegionPcV2
        let command = sprintf "%s s3://%s/core_live_00%d/{0} {1}" s3GetCommandBase url index
        setOptions.Add(sprintf "worker.historyGetCommandCore00%d=\"%s\"" index command)


    match context.s3HistoryMirrorOverridePcV2 with
    | Some mirrorUrl -> [ 1 .. 3 ] |> List.iter (setS3HistoryGetCommand mirrorUrl)
    | None -> ()

    setOptions.Add(sprintf "monitor.loggingIntervalSeconds=%d" jobMonitorLoggingIntervalSecs)

    // Every other mission gets this label from StellarKubeSpecs (Map.add
    // "mission" missionName); parallel catchup builds its pods from a helm
    // chart instead, so the old worker StatefulSet carried no mission label at
    // all. kube-state-metrics turns it into label_mission, which every
    // container-level Grafana panel joins on -- which is why this mission has
    // never appeared in the dashboard's mission list.
    setOptions.Add(sprintf "monitor.mission=%s" context.missionName)

    // Set ASAN_OPTIONS if provided
    match context.asanOptions with
    | Some asanOpts -> setOptions.Add(sprintf "worker.asanOptions=%s" asanOpts)
    | None -> ()

    // Convert labels and taints to Helm array format
    if not (List.isEmpty context.requireNodeLabelsPcV2) then
        let requireLabelsHelm =
            context.requireNodeLabelsPcV2
            |> List.mapi requireNodeLabelToHelmIndexed
            |> String.concat ","

        setOptions.Add(requireLabelsHelm)

    if not (List.isEmpty context.avoidNodeLabelsPcV2) then
        let avoidLabelsHelm =
            context.avoidNodeLabelsPcV2
            |> List.mapi avoidNodeLabelToHelmIndexed
            |> String.concat ","

        setOptions.Add(avoidLabelsHelm)

    if not (List.isEmpty context.tolerateNodeTaintsPcV2) then
        let tolerateTaintsHelm =
            context.tolerateNodeTaintsPcV2
            |> List.mapi tolerateTaintToHelmIndexed
            |> String.concat ","

        setOptions.Add(tolerateTaintsHelm)

    match context.serviceAccountAnnotationsPcV2 with
    | [] -> ()
    | _ ->
        context.serviceAccountAnnotationsPcV2
        |> List.mapi serviceAccountAnnotationsToHelmIndexed
        |> String.concat ","
        |> setOptions.Add

    // Expand tilde in kubeconfig path before setting environment variable
    let expandedKubeCfg = ExpandHomeDirTilde context.kubeCfg
    Environment.SetEnvironmentVariable("KUBECONFIG", expandedKubeCfg)

    // --namespace is not optional. Without it helm uses the kubeconfig's current
    // context, while every other call in this mission honours
    // context.namespaceProperty -- so a run explicitly targeted at one namespace
    // installs its monitor, Jobs and PVCs into a different one. Observed
    // 2026-07-30: a mission run with --namespace sandbox put a monitor and four
    // Jobs into the production namespace alongside a live run.
    RunShellCommand [| "helm"
                       "install"
                       helmReleaseName
                       helmChartPath
                       "--namespace"
                       context.namespaceProperty
                       "--values"
                       valuesFilePath
                       "--set"
                       String.Join(",", setOptions) |]
    |> ignore

    match RunShellCommand [| "helm"
                             "get"
                             "values"
                             helmReleaseName
                             "--namespace"
                             context.namespaceProperty |] with
    | Some valuesOutput -> LogInfo "%s" valuesOutput
    | _ -> ()

// Collect log files from all parallel catchup worker pods
// This function:
// 1. Automatically determines worker pod names from context.pubnetParallelCatchupNumWorkers
// 2. For each pod, finds all files matching "stellar-core-*.log" in /data
// 3. Creates a tar.gz archive and copies it to context.destination directory
let collectLogsFromPods (context: MissionContext) =
    // Worker pods are per-range now and are reaped within about a minute of
    // finishing, so there is nothing left to exec into at teardown. The monitor
    // pulls each pod's log while it is still alive -- on failure before the
    // retry, on success before the Job's TTL -- onto its own volume, so one
    // exec here replaces the ~1024 that the StatefulSet design needed.
    let monitorPods =
        context
            .kube
            .ListNamespacedPod(
                context.namespaceProperty,
                labelSelector = sprintf "app=job-monitor,release=%s" helmReleaseName
            )
            .Items
        |> Seq.map (fun p -> p.Metadata.Name)
        |> List.ofSeq

    match monitorPods with
    | [] -> LogWarn "No job-monitor pod found for release %s; worker logs cannot be collected" helmReleaseName
    | podName :: _ ->
        try
            LogInfo "Collecting worker logs from job-monitor pod %s to %s" podName context.destination.Path

            let outputFile =
                Path.Combine(context.destination.Path, sprintf "%s-worker-logs.tar" helmReleaseName)

            // Entries are already gzipped by the streaming collector, so this
            // bundles without re-compressing. Named range-<end>-a<attempt>.log.gz,
            // so a failing range is findable directly rather than by worker ordinal.
            // Already gzipped by the collector, so no -z. Keeps the per-attempt
            // .outcome verdicts (outcome/exitCode/pod -- useful post-mortem) and
            // drops .state, which is only the collector's resume bookkeeping.
            let command =
                [| "sh"
                   "-c"
                   // lost+found is the ext4 root of the logs PVC, not ours.
                   "cd /logs && tar -cf - --exclude='*.state' --exclude='./lost+found' ." |]

            RemoteCommandRunner.RunRemoteCommandAndCaptureOutput(
                kube = context.kube,
                ns = context.namespaceProperty,
                podName = podName,
                containerName = "job-monitor",
                command = command,
                outputFilePath = outputFile
            )

            let fileInfo = FileInfo(outputFile)

            if fileInfo.Exists && fileInfo.Length > 0L then
                LogInfo "Collected worker logs to %s (size: %d bytes)" outputFile fileInfo.Length
            else
                LogWarn "Worker log archive is empty: %s" outputFile

        with ex -> LogWarn "Could not collect worker logs from %s: %s" podName ex.Message

// Cleanup on exit. `signalTriggered` indicates we're running under a hard
// deadline (Jenkins' SoftKillWaitSeconds, ~5s by default, before SIGKILL).
// In that case we have to prioritize getting `helm uninstall` issued ahead
// of the much-slower log collection — otherwise we get SIGKILLed mid-
// collection and leak every worker pod, which is what we saw in practice
// with a 1024-worker run aborted from Jenkins.
let queryJobMonitor (context: MissionContext, key: String) =
    // The monitor publishes the same JSON it serves on /status into
    // <release>-catchup-progress. Reading it through the kube API removes the
    // Gateway/HTTPRoute dependency entirely -- the driver already has a client.
    try
        let cm =
            context.kube.ReadNamespacedConfigMap(helmReleaseName + "-catchup-progress", context.namespaceProperty)

        match cm.Data.TryGetValue key with
        | true, body ->
            LogInfo "job monitor status from configmap key '%s': %s" key body
            Some(JObject.Parse(body))
        | _ ->
            LogInfo "job monitor configmap has no '%s' yet" key
            None
    with ex ->
        LogError "Error reading job monitor configmap: %s" ex.Message
        None


// Emit what this run measured, next to the worker-log tar, so a later run can
// be given tighter per-range requests. An artifact rather than a ConfigMap or
// an S3 object: nothing for ArgoCD to reconcile, no second writer racing a
// concurrent mission, and not bounded by Prometheus retention.
// Fields carried from the monitor's progress record into the profile artifact.
// A PVC's size is absent on purpose -- it is not a scheduling dimension, so
// profiling it buys no packing. peakEphemeralBytes appears only for
// ephemeral-mode runs, and only for ranges that finished.
let rangeProfileFields =
    // peakAnonBytes is the field the sizing consumer prefers (kubelet-sampled
    // anon; peakRssBytes is the coarser Prometheus-era name for the same
    // quantity). Omitting it here silently stripped it from the mission's
    // profile artifact while the monitor's own progress.json carried it --
    // measured 2026-07-30: artifact 0% peakAnonBytes, volume copy 99%.
    [ "peakAnonBytes"
      "peakRssBytes"
      "peakWorkingSetBytes"
      "peakCpuCores"
      "peakEphemeralBytes"
      "seconds"
      // Kubernetes startTime -> completionTime for the winning Job only. The
      // monitor cannot reconstruct first dispatch -> success after predecessor
      // Jobs and their inter-attempt gaps are gone.
      "wallSeconds"
      "txApply" ]

// A missing measurement must stay missing rather than become a null: the
// consumer falls back to its configured default when the field is absent.
let projectRangeEntry (record: JObject) : JObject =
    let entry = JObject()

    for field in rangeProfileFields do
        match record.[field] with
        | null -> ()
        | v -> entry.[field] <- v

    entry


// The progress record, preferring the copy on the monitor's volume.
//
// The ConfigMap is only a mirror and is capped at 1 MiB -- about 6100 ranges at
// ~172 bytes each, reachable simply by halving ledgersPerJob. Past that the
// mirror stops updating while /logs/progress.json stays correct, so reading the
// ConfigMap would silently truncate the artifact.
let readProgressRecord (context: MissionContext) : JObject option =
    let monitorPods =
        context
            .kube
            .ListNamespacedPod(
                context.namespaceProperty,
                labelSelector = sprintf "app=job-monitor,release=%s" helmReleaseName
            )
            .Items
        |> Seq.map (fun p -> p.Metadata.Name)
        |> List.ofSeq

    let fromVolume =
        match monitorPods with
        | [] -> None
        | podName :: _ ->
            try
                let tmp = Path.Combine(Path.GetTempPath(), sprintf "%s-progress.json" helmReleaseName)

                RemoteCommandRunner.RunRemoteCommandAndCaptureOutput(
                    kube = context.kube,
                    ns = context.namespaceProperty,
                    podName = podName,
                    containerName = "job-monitor",
                    command = [| "cat"; "/logs/progress.json" |],
                    outputFilePath = tmp
                )

                let fi = FileInfo(tmp)

                if fi.Exists && fi.Length > 0L then
                    LogInfo "Progress record read from the monitor volume (%d bytes)" fi.Length
                    Some(JObject.Parse(File.ReadAllText(tmp)))
                else
                    None
            with ex ->
                LogWarn "Could not read /logs/progress.json (%s); falling back to the ConfigMap" ex.Message
                None

    match fromVolume with
    | Some p -> Some p
    | None ->
        // Degraded read, and it must not be silent. The ConfigMap is a state
        // mirror: the monitor strips every profiling field out of it to stay
        // under the 1 MiB cap, so a record sourced here carries attempts and
        // count and nothing else. Any range profile built from it will be
        // empty, and rangeProfileDocument will decline to write one.
        LogWarn
            "Falling back to the progress ConfigMap; it is a state mirror with no measurements, so no range profile can be built from it"

        queryJobMonitor (context, jobMonitorProgressKey)


// The `ranges` map of a profile artifact, built from a progress record's
// `completed` map. Pure, so the projection can be exercised without a cluster.
let buildRangeProfile (completed: JObject) : JObject =
    let ranges = JObject()

    // Keyed on the range end alone, with count kept as a field.
    //
    // Measured on ssc-test: 4.2x the ledgers per range (100 -> 420, the
    // default 320 overlap) moved peak disk by -1.6% and wall time by
    // 1.15x. Cost tracks ledger position -- how big the bucket set is to
    // download and apply -- far more than range length. Putting count in
    // the key would therefore discard the whole profile whenever
    // overlapLedgers or ledgersPerJob changed, to preserve a distinction
    // the measurements say is small.
    //
    // A consumer should resolve a range by exact end, else the nearest
    // measured end, else a run-wide fallback -- so a re-sliced run still
    // gets useful numbers instead of zero matches.
    for prop in completed.Properties() do
        let record = prop.Value :?> JObject
        let entry = projectRangeEntry record

        // The guard decides on measurements alone. count is bookkeeping, not a
        // measurement, so it is attached only after the entry has been found to
        // carry something real. Attaching it first made every entry non-empty
        // and defeated the guard completely: a ConfigMap-sourced record, which
        // has had all eight profiling fields stripped by the monitor's
        // _state_only(), still sailed through and produced a range with nothing
        // in it but a count.
        if entry.Count > 0 then
            match record.["count"] with
            | null -> ()
            | v -> entry.["count"] <- v

            // Same end from two differently-sized ranges: keep the
            // larger, since sizing from the smaller would under-provision.
            let existing = ranges.[prop.Name]

            let keep =
                isNull existing
                || (let a = entry.["count"]
                    let b = (existing :?> JObject).["count"]
                    isNull b || (not (isNull a) && a.Value<int>() >= b.Value<int>()))

            if keep then ranges.[prop.Name] <- entry

    ranges


// The profile document to write, or None when there is nothing worth writing.
let rangeProfileDocument (storageMode: string) (defaultLedgersPerRange: int) (completed: JObject) : JObject option =
    let ranges = buildRangeProfile completed

    // Slicing and storage mode both go in the name, because a profile is
    // only valid for the shape it was measured at.
    //
    // Slicing: keys are range ends and cost tracks range length, so a
    // 39382-ledger profile fed into a 16320-ledger run resolves through
    // nearest-end fallback and sizes everything wrong. Measured on
    // ssc-test -- it produced 1025 OOM retries in one run.
    //
    // Mode: an ephemeral profile carries peakEphemeralBytes and a pvc one
    // does not, so crossing them silently defaults the disk axis.
    let ledgersPerRange =
        let counts =
            ranges.Properties()
            |> Seq.choose
                (fun p ->
                    match (p.Value :?> JObject).["count"] with
                    | null -> None
                    | v -> Some(v.Value<int>()))
            |> Seq.toList

        match counts with
        | [] -> defaultLedgersPerRange
        | _ -> counts |> List.countBy id |> List.maxBy snd |> fst

    let doc = JObject()
    doc.["schema"] <- JValue(1)
    doc.["generated"] <- JValue(DateTime.UtcNow.ToString("o"))
    doc.["release"] <- JValue(helmReleaseName)
    doc.["storageMode"] <- JValue(storageMode)
    doc.["ledgersPerRange"] <- JValue(ledgersPerRange)
    doc.["ranges"] <- ranges

    // A profile with no measurements is worse than no profile at all: it looks
    // complete, so nothing downstream can tell it from a good one, and the next
    // run sizes itself from empty data. The usual cause is readProgressRecord
    // falling back to the progress ConfigMap, which is a state mirror with
    // every profiling field stripped. Writing nothing lets the consumer fall
    // back to its configured defaults, which is the safe outcome.
    if ranges.Count = 0 then None else Some doc


let writeRangeProfile (context: MissionContext) =
    match readProgressRecord context with
    | None -> LogInfo "No progress record to build a range profile from"
    | Some progress ->
        try
            let completed = progress.["completed"] :?> JObject

            let docOpt =
                rangeProfileDocument
                    context.pubnetParallelCatchupStorageMode
                    context.pubnetParallelCatchupLedgersPerJob
                    completed

            match docOpt with
            | None -> LogWarn "Progress record carried no measurements; not writing a range profile"
            | Some doc ->
                let ranges = doc.["ranges"] :?> JObject
                let ledgersPerRange = doc.["ledgersPerRange"].Value<int>()

                let path =
                    Path.Combine(
                        context.destination.Path,
                        sprintf
                            "%s-profile-%dledgers-%s.json"
                            helmReleaseName
                            ledgersPerRange
                            context.pubnetParallelCatchupStorageMode
                    )

                File.WriteAllText(path, doc.ToString())
                LogInfo "Wrote range profile for %d ranges to %s" ranges.Count path
        with ex -> LogWarn "Failed to write range profile: %s" ex.Message


let cleanup (signalTriggered: bool) (context: MissionContext) =
    if toPerformCleanup then
        toPerformCleanup <- false

        // Before either branch: `helm uninstall` deletes the progress ConfigMap
        // the profile is built from, so an aborted run would otherwise lose every
        // measurement it had already taken. One ConfigMap read and a local file
        // write -- cheap enough for the abort path's few seconds, and a run
        // stopped part-way is exactly when the partial profile is most wanted.
        try
            writeRangeProfile context
        with ex -> LogWarn "Failed to write range profile: %s" ex.Message

        if signalTriggered then
            // Abort path: resources first, logs are nice-to-have.
            // Skip log collection entirely — even parallelized it can't beat
            // Jenkins' ~5s grace before SIGKILL, and it can't beat the per-pod
            // terminationGracePeriodSeconds (default 30s) when scaled to 1024
            // workers. Whatever logs were captured inline by the failure
            // handler in the main loop are still on disk.
            LogInfo "Signal-triggered cleanup: uninstalling release %s" helmReleaseName

            RunShellCommand [| "helm"
                               "uninstall"
                               helmReleaseName
                               "--namespace"
                               context.namespaceProperty |]
            |> ignore
        else
            // Normal / legitimate-failure path: pods are still alive through
            // this entire window, so we can collect all logs before deleting.
            LogInfo "Cleaning up resources for release: %s" helmReleaseName

            try
                LogInfo "Attempting to collect worker logs before cleanup..."
                let stopwatch = Stopwatch.StartNew()
                collectLogsFromPods context
                stopwatch.Stop()
                LogInfo "Log collection completed in %.2f seconds" stopwatch.Elapsed.TotalSeconds
            with ex -> LogWarn "Failed to collect some or all worker logs: %s" ex.Message

            RunShellCommand [| "helm"
                               "uninstall"
                               helmReleaseName
                               "--namespace"
                               context.namespaceProperty |]
            |> ignore

let mutable cleanupContext : MissionContext option = None

// NOTE: AppDomain.ProcessExit handlers have a soft ~2-second runtime budget
// before .NET force-exits the process. If we ever observe that this budget is insufficient, switch to
// `PosixSignalRegistration.Create(PosixSignal.SIGTERM, ...)`
// which has no such budget and lets the handler run to completion within
// Jenkins' full SoftKillWaitSeconds window (~5s default).
System.AppDomain.CurrentDomain.ProcessExit.Add
    (fun _ ->
        match cleanupContext with
        | Some ctx -> cleanup true ctx
        | None -> ())

Console.CancelKeyPress.Add
    (fun _ ->
        match cleanupContext with
        | Some ctx -> cleanup true ctx
        | None -> ()

        Environment.Exit(0))

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

    let filename = sprintf "FAILED-last%dlines-%s.log" failedJobLogFileLineCount podName
    context.destination.WriteLines filename (logLines.ToArray())
    stream.Close()

let historyPubnetParallelCatchupV2 (context: MissionContext) =
    LogInfo "Running parallel catchup v2 ..."

    nonce <- (MakeNetworkNonce context.tag).ToString()
    helmReleaseName <- sprintf "parallel-catchup-%s" nonce
    LogDebug "nonce: '%s', release name: '%s'" nonce helmReleaseName

    // Set cleanup context so cleanup handlers can access it
    cleanupContext <- Some context

    installProject context

    let mutable allJobsFinished = false
    let mutable timeoutLeft = jobMonitorStatusCheckTimeOutSecs

    // Failures are reported once the run drains, not at first sight. Aborting on
    // the first condemned range abandons every range still in flight, and the
    // ranges that survive to the end of a run are the expensive tip ones this
    // mission exists to measure. Measured 2026-07-30: one condemned range at 97%
    // discarded 123 ranges of completed and in-flight work. The mission still
    // fails -- it just finishes the work it can first.
    let failedJobs = ResizeArray<string>()
    let seenFailures = System.Collections.Generic.HashSet<string>()

    while not allJobsFinished do
        Thread.Sleep(jobMonitorStatusCheckIntervalSecs * 1000)
        let statusOpt = queryJobMonitor (context, jobMonitorStatusKey)

        try
            match statusOpt with
            | Some status ->
                timeoutLeft <- jobMonitorStatusCheckTimeOutSecs
                let remainSize = status.Value<int>("num_remain")
                let jobsFailed = status.["jobs_failed"] :?> JArray
                let JobsInProgress = status.["jobs_in_progress"] :?> JArray

                for job in jobsFailed do
                    let text = job.ToString()

                    if seenFailures.Add(text) then
                        failedJobs.Add(text)
                        LogError "RANGE FAILED: %s -- run continues, mission will fail once it drains" text

                if remainSize = 0 && JobsInProgress.Count = 0 then
                    LogInfo "All queues empty. Mission complete."
                    allJobsFinished <- true

            | None ->
                LogError "no status"
                timeoutLeft <- timeoutLeft - jobMonitorStatusCheckIntervalSecs
                if timeoutLeft <= 0 then failwith "job monitor not reachable"
        with ex ->
            cleanup false context
            raise ex

    if failedJobs.Count <> 0 then
        LogInfo "%d job(s) failed:" failedJobs.Count

        for job in failedJobs do
            let ident = job.Split('|')
            LogInfo "%s, logs >>> " job

            // The pod is very likely reaped by now -- draining first means the
            // wait is the length of the run. Its log is on the monitor volume
            // either way, so a missing pod must not mask the failure below.
            if ident.Length > 1 then
                try
                    dumpLogs (context, ident.[1])
                with ex -> LogInfo "could not read pod log (%s); see collected logs" (ex.Message)

            LogInfo "<<<"

        cleanup false context
        failwith "Catch up failed, check logs for more info"

    cleanup false context
