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

// An extra values file layered on top of the chart's own, for a run that wants
// different values without a working copy of the chart. Unset is the normal
// case: helm reads <chart>/values.yaml as its base regardless, so naming that
// same path would only re-apply it to itself.
//
// Layered, not substituted, so it carries only the keys it changes.
// SUPERCLUSTER_CHART_PATH repoints the whole chart including its values; this
// repoints the values alone, so the baked chart can run against experimental
// numbers.
let extraValuesArgs =
    match Environment.GetEnvironmentVariable("SUPERCLUSTER_VALUES_PATH") with
    | null
    | "" -> [||]
    | p -> [| "--values"; p |]

// Example command to run local testing (in the `supercluster/` directory):
// $ dotnet run --project src/App/App.fsproj -- mission HistoryPubnetParallelCatchupV2 --image=docker-registry.services.stellar-ops.com/dev/stellar-core:23.0.3-2779.4d1df2b03.jammy-vnext-buildtests  --pubnet-parallel-catchup-num-workers=2 --pubnet-parallel-catchup-starting-ledger=0 --pubnet-parallel-catchup-end-ledger=6400 --pubnet-parallel-catchup-ledgers-per-job 1280  --destination ./logs

let jobMonitorLoggingIntervalSecs = 30 // frequency of the monitor reconcile loop: dispatch, liveness ping, status publish
let jobMonitorStatusCheckIntervalSecs = 60
let jobMonitorStatusCheckTimeOutSecs = 600

// Print one status line in ten. The poll stays at a minute because
// jobMonitorStatusCheckTimeOutSecs is spent in units of it -- 600 over a 60s
// interval tolerates ten consecutive failures, and slowing the poll instead
// would make a single transient blip fail the run. Nothing is lost by printing
// less: a range failure logs the moment it is seen, on its own line.
let jobMonitorStatusLogEveryNChecks = 10
let mutable toPerformCleanup = true
let failedJobLogFileLineCount = 10000
let failedJobLogStreamLineCount = 1000

let mutable statusChecks = 0
let mutable nonce : String = ""
let mutable helmReleaseName : String = ""

// Resolve --pubnet-parallel-catchup-profile into the profile body POSTed to /start.
//
// Accepts a local path or an https URL, so a profile can come off disk or
// straight from a raw paste/gist link. Returns None to size from the
// configured requests.
//
// Never fatal: a profile only tightens requests, so a run must still start when
// one cannot be fetched. Failing the mission here would turn an optimisation
// into a dependency.
let resolveRangeProfile (context: MissionContext) : string option =
    let spec = context.pubnetParallelCatchupProfile

    if String.IsNullOrWhiteSpace spec then
        None
    else
        try
            let body =
                // http as well as https: the only alternative is treating the
                // URL as a filename, which fails as "could not load" without
                // ever mentioning the scheme, and the run proceeds unprofiled --
                // every range sized from defaults. A profile moves resource
                // REQUESTS only, so a tampered one costs node size, not code
                // execution, and it is parsed and range-counted before use.
                if spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
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
                LogInfo "Range profile: %d ranges from %s" count spec
                Some body
        with ex ->
            LogWarn "Could not load range profile %s (%s); sizing from configured requests" spec ex.Message
            None


// The driver talks to the monitor over its HTTPRoute: profile in via POST
// /start, status out of /status, logs pulled per file. The previous channels --
// a status ConfigMap and `kubectl exec tar` -- both went through the API
// server; the logs alone measured ~0.3 MB per range, so ~1.2 GB of
// control-plane traffic on a 4000-range run, for bytes with no reason to be
// there.
let monitorRouteHost (context: MissionContext) = sprintf "%s.%s" nonce context.routeInternalDomain

// Where the socket actually goes. An external host (an ELB, say) still needs
// the Host header set to the route hostname, or the gateway cannot match it.
let monitorEndpoint (context: MissionContext) =
    match context.routeExternalHost with
    | Some h -> h
    | None -> monitorRouteHost context

// The per-request timeout has to be shorter than any retry window built on top
// of it, or one hung request eats the whole window and the retry never happens.
// Observed 2026-08-08: a /start attempt hung on a route that was still
// programming, the 10-minute client timeout outlived the 5-minute deadline, and
// the mission failed after exactly one attempt.
let private monitorClientWith (context: MissionContext) (timeout: TimeSpan) =
    let c = new HttpClient(BaseAddress = Uri(sprintf "http://%s" (monitorEndpoint context)))
    c.DefaultRequestHeaders.Host <- monitorRouteHost context
    c.Timeout <- timeout
    c

// Log pulls move whole files, so they get room; everything else is a short
// request that should fail fast and be retried.
let private monitorClient (context: MissionContext) =
    monitorClientWith context (TimeSpan.FromMinutes(10.0))

/// The run the monitor is asked to perform. The range travels with the profile
/// because both are per-run input -- the chart installs a generic monitor, and
/// this is what makes it a particular run. Validated as one document, so a bad
/// ledger range comes back as a 400 rather than generating no work and
/// reporting success on nothing.
let runDocument (context: MissionContext) (profileJson: string option) : string =
    let endLedger =
        match context.pubnetParallelCatchupEndLedger with
        | Some value -> value
        | None -> GetLatestPubnetLedgerNumber()

    // `run`, not `doc`: the profile ARTIFACT this mission writes is also a
    // document, and the contract tests scan for its keys by name.
    let rangeSpec = JObject()
    rangeSpec.["startingLedger"] <- JValue(context.pubnetParallelCatchupStartingLedger)
    rangeSpec.["latestLedgerNum"] <- JValue(endLedger)
    rangeSpec.["ledgersPerJob"] <- JValue(context.pubnetParallelCatchupLedgersPerJob)
    rangeSpec.["order"] <- JValue(context.pubnetParallelCatchupRangeOrder)

    let run = JObject()
    run.["range"] <- rangeSpec

    match profileJson with
    | Some body -> run.["profile"] <- JObject.Parse(body)
    | None -> ()

    run.ToString(Newtonsoft.Json.Formatting.None)

/// POST the run and let reconcile start. Retried: the route and the pod
/// both need a moment after `helm install`, and until this lands the monitor
/// deliberately dispatches nothing.
let startMission (context: MissionContext) (runJson: string) =
    use client = monitorClientWith context (TimeSpan.FromSeconds(15.0))
    let deadline = DateTime.UtcNow.AddMinutes(5.0)
    let mutable started = false

    while not started && DateTime.UtcNow < deadline do
        try
            use content = new StringContent(runJson, Text.Encoding.UTF8, "application/json")
            let r = client.PostAsync("/start", content) |> Async.AwaitTask |> Async.RunSynchronously
            if r.IsSuccessStatusCode then
                LogInfo "Mission started: profile POSTed to %s/start" (monitorEndpoint context)
                started <- true
            else
                Thread.Sleep(5000)
        with _ -> Thread.Sleep(5000)

    if not started then
        failwithf "could not reach the job monitor at %s to start the mission" (monitorEndpoint context)


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

    // The route the driver uses for /start, /status and the logs. Templated
    // only when routeHost is set, so an in-cluster caller still works without a
    // gateway.
    setOptions.Add(sprintf "monitor.routeHost=%s" (monitorRouteHost context))
    setOptions.Add(sprintf "monitor.gatewayName=%s" context.gatewayName)
    setOptions.Add(sprintf "monitor.gatewayNamespace=%s" context.gatewayNamespace)


    // Nodepool routing. Empty prefix ships the pre-tier behaviour: one label for
    // every worker. Set, each range goes to <prefix>-<tier> where the tier comes
    // from its measured peakAnonBytes, and gets that node to itself.
    setOptions.Add(sprintf "monitor.poolPrefix=%s" context.pubnetParallelCatchupPoolPrefix)

    // The monitor and collector ship as one image, pinned in the chart. Passing
    // it here is what lets a run test a build of them without editing values.
    if context.jobMonitorImagePcV2 <> "" then
        setOptions.Add(sprintf "monitor.image=%s" context.jobMonitorImagePcV2)

    // Off by default: ssc-eks provides the binding itself, and creating these
    // needs the installer to hold the rights being granted. A cluster without
    // that binding gets a 403 on the monitor's first ConfigMap read and
    // dispatches nothing -- ssc-test is such a cluster.
    if context.pubnetParallelCatchupCreateRbac then
        setOptions.Add("monitor.createRbac=true")

    if context.pubnetParallelCatchupPoolPrefix <> "" then
        // Routing needs the label KEY and the taint toleration, and neither has
        // a sensible default for an unpooled run -- both ship as []. Derived
        // here because a pooled run that sets only
        // the prefix otherwise fails twice over, and both failures are quiet.
        // Karpenter labels these nodes purpose=<prefix>-<tier>, which is exactly
        // the value job_monitor builds per range, and taints them <prefix>:
        // NoSchedule. Without the key there is no tier affinity at all (the pods
        // schedule anywhere); without the toleration they schedule nowhere.
        // Observed on ssc-test 2026-08-07: 10 workers Pending indefinitely,
        // "did not tolerate taint (taint=catchup:NoSchedule)".
        setOptions.Add(
            sprintf "worker.requireNodeLabels[0]=purpose:%s" context.pubnetParallelCatchupPoolPrefix
        )

        setOptions.Add(
            sprintf "worker.tolerateNodeTaints[0]=%s" context.pubnetParallelCatchupPoolPrefix
        )


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
    // Both pushed only when the run explicitly asks. Otherwise the chart default
    // stands, so the chart is the single place V2's worker sizing is written down.
    if not (String.IsNullOrWhiteSpace context.pubnetParallelCatchupCpuRequest) then
        setOptions.Add(
            sprintf "worker.resources.requests.cpu=%s" context.pubnetParallelCatchupCpuRequest
        )

    if not (String.IsNullOrWhiteSpace context.pubnetParallelCatchupMemRequest) then
        setOptions.Add(
            sprintf "worker.resources.requests.memory=%s" context.pubnetParallelCatchupMemRequest
        )

    // Ephemeral-storage is an ephemeral-mode concept: /data is an emptyDir on the
    // node there, and StellarKubeSpecs sizes it. In pvc mode /data is on the
    // volume and the node disk holds only logs and tmp, so the run reserves
    // nothing -- _resources already reads an empty REQ_EPHEMERAL as "leave both
    // axes off the pod", and a request sized for the other mode would make disk
    // rather than cpu the binding dimension for packing.
    if context.pubnetParallelCatchupStorageMode <> "pvc" then
        LogInfo
            "Worker ephemeral storage from StellarKubeCfg: request %s, limit %s"
            (resourceRequirements.Requests.["ephemeral-storage"].ToString())
            (resourceRequirements.Limits.["ephemeral-storage"].ToString())

        setOptions.Add(
            sprintf
                "worker.resources.requests.ephemeral_storage=%s"
                (resourceRequirements.Requests.["ephemeral-storage"].ToString())
        )

        setOptions.Add(
            sprintf
                "worker.resources.limits.ephemeral_storage=%s"
                (resourceRequirements.Limits.["ephemeral-storage"].ToString())
        )

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
            // From 1: a pooled run claims index 0 for the label it routes on,
            // and mapi from 0 would overwrite it with whichever came second.
            |> List.mapi (fun i pair ->
                requireNodeLabelToHelmIndexed
                    (if context.pubnetParallelCatchupPoolPrefix <> "" then i + 1 else i)
                    pair)
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
    // The pool maps get their own --set each. Every other option is folded into
    // ONE comma-joined --set, and these are themselves comma-separated, so
    // folding them in would split each tier into a separate assignment. helm
    // reads a backslash-escaped comma as data rather than as a separator.
    //
    // They arrive per run rather than from the chart because the claims differ
    // by capacity: the spot pools were doubled on 2026-08-04 so a claim is half
    // a node and two pods share it, while the on-demand pools kept their
    // original sizes, where that same claim is the node's NAMEPLATE -- and
    // nameplate is not allocatable, so nothing schedules at all.
    let poolMapArgs =
        [ "monitor.poolCpu", context.pubnetParallelCatchupPoolCpu
          "monitor.poolMem", context.pubnetParallelCatchupPoolMem ]
        |> List.filter (fun (_, v) -> not (String.IsNullOrWhiteSpace v))
        |> List.collect (fun (key, v) -> [ "--set"; sprintf "%s=%s" key (v.Replace(",", "\\,")) ])
        |> Array.ofList

    RunShellCommand(
        Array.concat [ [| "helm"; "install"; helmReleaseName; helmChartPath |]
                       [| "--namespace"; context.namespaceProperty |]
                       extraValuesArgs
                       poolMapArgs
                       [| "--set"; String.Join(",", setOptions) |] ]
    )
    |> ignore

    match RunShellCommand [| "helm"
                             "get"
                             "values"
                             helmReleaseName
                             "--namespace"
                             context.namespaceProperty |] with
    | Some valuesOutput -> LogInfo "%s" valuesOutput
    | _ -> ()

// How often the main loop pulls logs. Every 10 minutes flattens the teardown
// cost -- which measured ~20 minutes for a full run, all of it after the work
// finished -- without the pull competing with the collector for the volume.
let logFetchIntervalSecs = 600

let private monitorPodName (context: MissionContext) : string option =
    context
        .kube
        .ListNamespacedPod(
            context.namespaceProperty,
            labelSelector = sprintf "app=job-monitor,release=%s" helmReleaseName
        )
        .Items
    |> Seq.map (fun p -> p.Metadata.Name)
    |> Seq.tryHead

/// Pull every artifact the destination does not already hold.
///
/// Per file, over the monitor's HTTPRoute. The tar-over-exec this replaces put
/// every byte through the API server -- ~0.3 MB per range, so ~1.2 GB on a
/// 4000-range run -- and streamed the whole volume as one archive, which at 200
/// workers came back truncated 2 times in 3 with no error surfaced. A file is
/// its own unit here: a cut transfer resumes from the byte it reached, and one
/// bad fetch costs one file rather than the pass.
/// Which subfolder of the destination an artifact belongs in.
///
/// Sorted on the way out, not on the volume: the monitor's paths are an
/// implementation detail, while this is what a human opens. Flat, a 4000-range
/// run lands ~16000 files beside the five that summarise it -- three per range
/// that are per-range detail, and one bundle-wide file each for the monitor log,
/// the driver log, the progress record, the profile it produced and the run it
/// was asked for.
///
/// Keeping the volume flat also leaves the HTTP surface alone: /logs/<name>
/// takes one path element and no separator, which is what stops the route --
/// reachable from outside the cluster once its HTTPRoute is attached -- being
/// walked out of LOG_DIR.
let artifactFolder (name: string) =
    if name.EndsWith(".log.gz") then "range-logs"
    elif name.EndsWith(".metrics") then "metrics"
    elif name.EndsWith(".done")
         || name.EndsWith(".started")
         || name.EndsWith(".state")
         || name = "mission_started" then
        "state"
    else ""

let collectLogs (context: MissionContext) (destination: string) =
    Directory.CreateDirectory(destination) |> ignore
    use client = monitorClient context

    let manifest =
        client.GetStringAsync("/logs") |> Async.AwaitTask |> Async.RunSynchronously
        |> JArray.Parse

    let fetchOne (entry: JToken) =
        let name = entry.["name"].ToString()
        let size = entry.["size"].Value<int64>()
        let folder = artifactFolder name

        let path =
            if folder = "" then
                Path.Combine(destination, name)
            else
                Directory.CreateDirectory(Path.Combine(destination, folder)) |> ignore
                Path.Combine(destination, folder, name)

        let have = if File.Exists path then FileInfo(path).Length else 0L

        // Resume and skip both assume the file only ever grew, which is true of
        // a worker log and of nothing else here. progress.json is rewritten
        // whole on every reconcile and grows as ranges complete, so a Range
        // request would splice the new document's tail onto the old one's
        // prefix -- invalid JSON, in the bundle, silently, since the profile is
        // built from the copy read off the pod rather than this one. Equal
        // length is no safer: a rewrite can change a value without changing the
        // length. Everything that is not append-only is refetched whole, which
        // costs little -- progress.json is ~1 MB at 4000 ranges against ~1.4 GB
        // of worker logs.
        let appendOnly = name.EndsWith(".log.gz")

        // File.Exists is not redundant: a .done marker is zero bytes, so a
        // length comparison alone reads "absent locally" as "already have it"
        // and never fetches it. Measured 2026-08-08: 88 of 110 artifacts
        // collected, and every .done was among the 22 missing.
        if appendOnly && File.Exists path && have = size then
            -1L   // already whole; distinct from a zero-byte file we did fetch
        else
            let req = new HttpRequestMessage(HttpMethod.Get, "/logs/" + name)
            if appendOnly && have > 0L && have < size then
                req.Headers.Range <- Headers.RangeHeaderValue(Nullable(have), Nullable())

            use resp = client.SendAsync(req) |> Async.AwaitTask |> Async.RunSynchronously
            resp.EnsureSuccessStatusCode() |> ignore
            let bytes = resp.Content.ReadAsByteArrayAsync() |> Async.AwaitTask |> Async.RunSynchronously

            // Append on a partial answer, replace on a whole one: a server that
            // ignored Range would otherwise double the file.
            if resp.StatusCode = Net.HttpStatusCode.PartialContent then
                use fs = new FileStream(path, FileMode.Append, FileAccess.Write)
                fs.Write(bytes, 0, bytes.Length)
            else
                File.WriteAllBytes(path, bytes)

            int64 bytes.Length

    // Bounded: the monitor serves these from the same pod that runs reconcile.
    let fetched =
        manifest
        |> Seq.toArray
        |> Array.map (fun e -> async { return (try fetchOne e with ex ->
                                                 LogWarn "log fetch failed for %s: %s"
                                                     (e.["name"].ToString()) ex.Message
                                                 -1L) })
        |> fun work -> Async.Parallel(work, 8)
        |> Async.RunSynchronously

    let moved = fetched |> Array.filter (fun n -> n > 0L) |> Array.sum
    // >= 0 counts a zero-byte artifact we really did fetch. .done markers are
    // empty by design, so "bytes > 0" undercounts exactly the files whose
    // existence IS the signal.
    let touched = fetched |> Array.filter (fun n -> n >= 0L) |> Array.length
    LogInfo "Collected %d of %d artifacts (%d bytes) from %s"
        touched (Seq.length manifest) moved (monitorEndpoint context)

/// One log pass. Idempotent -- the manifest comparison is what makes a repeat
/// pass cheap, so there is no watermark to keep.
let collectLogsFromPods (context: MissionContext) =
    collectLogs context context.destination.Path

// Cleanup on exit. `signalTriggered` indicates we're running under a hard
// deadline (Jenkins' SoftKillWaitSeconds, ~5s by default, before SIGKILL).
// In that case we have to prioritize getting `helm uninstall` issued ahead
// of the much-slower log collection — otherwise we get SIGKILLed mid-
// collection and leak every worker pod, which is what we saw in practice
// with a 1024-worker run aborted from Jenkins.

let queryJobMonitor (context: MissionContext) =
    try
        use client = monitorClient context
        let body = client.GetStringAsync("/status") |> Async.AwaitTask |> Async.RunSynchronously

        statusChecks <- statusChecks + 1

        if statusChecks % jobMonitorStatusLogEveryNChecks = 1 then
            LogInfo "job monitor status: %s" body

        Some(JObject.Parse(body))
    with ex ->
        LogError "Error reading job monitor status: %s" ex.Message
        None


// Emit what this run measured, so a later run can be given tighter
// per-range requests. An artifact rather than a ConfigMap or
// an S3 object: nothing for ArgoCD to reconcile, no second writer racing a
// concurrent mission, and not bounded by Prometheus retention.
// Fields carried from the monitor's progress record into the profile artifact.
// A PVC's size is absent on purpose -- it is not a scheduling dimension, so
// profiling it buys no packing. peakEphemeralBytes appears only for
// ephemeral-mode runs, and only for ranges that finished.
//
// A subset of what the monitor records: it measures more per range than the next
// run can size from, and a measurement nothing reads is pure weight in an
// artifact that was already 963 KB at 4805 ranges.
let rangeProfileFields =
    // The memory figure the sizing consumer reads, sampled from kubelet by the
    // collector. Omitting it strips it from the artifact while progress.json
    // still carries it, so the next run sizes every range from defaults.
    [ "peakAnonBytes"
      "peakWorkingSetBytes"
      "peakEphemeralBytes"
      // The only timing the next run sizes from: it sets the percentile basis,
      // the dispatch order and the runtime insurance thresholds. wallSeconds and
      // txApply are recorded per range as Prometheus metrics but nothing sizes
      // or orders from either, so they stay out of the artifact.
      "seconds" ]

// A missing measurement must stay missing rather than become a null: the
// consumer falls back to its configured default when the field is absent.
let projectRangeEntry (record: JObject) : JObject =
    let entry = JObject()

    for field in rangeProfileFields do
        match record.[field] with
        | null -> ()
        | v -> entry.[field] <- v

    entry


// The progress record, read only from the monitor's volume.
// No record is the safe outcome -- the consumer falls back to its defaults.
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
                LogWarn "Could not read /logs/progress.json (%s); no range profile will be written" ex.Message
                None

    fromVolume


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
        // and defeated the guard completely: a record that measured nothing
        // still sailed through and produced a range holding only a count.
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
    // run sizes itself from empty data. Writing nothing lets the consumer fall
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

        // Before either branch: `helm uninstall` takes the monitor pod, and with
        // it the volume the profile is read from, so an aborted run would
        // otherwise lose every measurement it had already taken. One pod exec
        // and a local file write -- cheap enough for the abort path's few
        // seconds, and a run stopped part-way is exactly when the partial
        // profile is most wanted.
        try
            writeRangeProfile context
        with ex -> LogWarn "Failed to write range profile: %s" ex.Message

        if signalTriggered then
            // Abort path: resources first, logs are nice-to-have.
            // Skip log collection entirely — even parallelized it can't beat
            // Jenkins' ~5s grace before SIGKILL, and it can't beat the per-pod
            // terminationGracePeriodSeconds (default 30s) when scaled to 1024
            // workers. Whatever logs were captured inline by the failure
            // handler in the main loop are still on disk -- and since the main
            // loop now fetches every logFetchIntervalSecs, an abort keeps every
            // part up to the last pass rather than losing the run's logs whole.
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

    // The monitor dispatches nothing until this arrives, so a profile that
    // cannot be delivered fails the run here rather than silently sizing every
    // range as unprofiled.
    startMission context (runDocument context (resolveRangeProfile context))

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
    let mutable lastLogFetch = DateTime.UtcNow

    while not allJobsFinished do
        Thread.Sleep(jobMonitorStatusCheckIntervalSecs * 1000)
        let statusOpt = queryJobMonitor context

        try
            match statusOpt with
            | Some status ->
                timeoutLeft <- jobMonitorStatusCheckTimeOutSecs
                let remainSize = status.Value<int>("num_remain")
                let jobsFailed = status.["jobs_failed"] :?> JArray
                let jobsInProgress = status.Value<int>("queue_in_progress_count")

                for job in jobsFailed do
                    let text = job.ToString()

                    if seenFailures.Add(text) then
                        failedJobs.Add(text)
                        LogError "RANGE FAILED: %s -- run continues, mission will fail once it drains" text

                if remainSize = 0 && jobsInProgress = 0 then
                    LogInfo "All queues empty. Mission complete."
                    allJobsFinished <- true

                // Pull the logs written since the last pass, so teardown moves a
                // delta instead of the whole volume. Measured at ~20 minutes for
                // a full run, all of it after the work had finished. Isolated in
                // its own try: a failed pass re-fetches the same window next
                // time and must never take the mission down.
                if (DateTime.UtcNow - lastLogFetch).TotalSeconds >= float logFetchIntervalSecs then
                    lastLogFetch <- DateTime.UtcNow

                    try
                        collectLogsFromPods context
                    with ex -> LogWarn "Incremental log collection failed: %s" ex.Message

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
