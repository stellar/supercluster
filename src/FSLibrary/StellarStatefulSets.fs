// Copyright 2021 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarStatefulSets

open k8s
open k8s.Models
open Logging
open ScriptUtils
open StellarFormation
open StellarDataDump
open StellarCoreSet
open StellarKubeSpecs
open StellarCorePeer
open StellarCoreHTTP
open StellarTransaction
open StellarNetworkDelays
open System
open System.Threading

// Extract the peer topology from stellar-core configuration
// Returns a map from node name to its peers
let private extractPeerTopology (nCfg: StellarNetworkCfg.NetworkCfg) : Map<string, string array> =
    let topology =
        nCfg.MapAllPeers
            (fun coreSet i ->
                let podName = nCfg.PodName coreSet i

                let peerList =
                    match coreSet.options.preferredPeersMap with
                    | Some ppMap ->
                        let nodeKey = coreSet.keys.[i].PublicKey

                        if ppMap.ContainsKey(nodeKey) then
                            ppMap.[nodeKey]
                            |> List.map
                                (fun peerKey ->
                                    // Find the DNS name for this peer key by searching all peers
                                    nCfg.MapAllPeers
                                        (fun cs j ->
                                            if cs.keys.[j].PublicKey = peerKey then
                                                Some (nCfg.PeerDnsName cs j).StringName
                                            else
                                                None)
                                    |> Array.tryPick id)
                            |> List.choose id
                            |> Array.ofList
                        else
                            [||]
                    | None ->
                        // If no preferred peers, use default peering logic
                        nCfg.MapAllPeers
                            (fun cs j ->
                                if cs <> coreSet || i <> j then
                                    Some (nCfg.PeerDnsName cs j).StringName
                                else
                                    None)
                        |> Array.choose id

                (podName.StringName, peerList))
        |> Array.ofSeq

    Map.ofArray topology

// Calculate average peers per node
let private getAveragePeerCount (topology: Map<string, string array>) : float =
    let counts = topology |> Map.toSeq |> Seq.map (fun (_, peers) -> Array.length peers)
    let total = Seq.sum counts
    let nodeCount = Map.count topology
    if nodeCount > 0 then float total / float nodeCount else 0.0

// Divide aggregate rate, account space and duration across actual generators.
// In fixed-duration mode (LoadOnEveryValidator runs), derive each transaction
// budget from its assigned rate; equal transaction budgets would make
// remainder-rate peers finish early. Otherwise this is upstream's even split.
let PartitionValidatorLoad (n: int) (fixedDuration: bool) (full: LoadGen) =
    if n <= 0 then invalidArg "n" "Need at least one generator"

    if fixedDuration && full.accounts < n then
        invalidArg "n" "Need accounts for every generator"

    if fixedDuration && (full.txrate <= 0 || full.txs % full.txrate <> 0) then
        invalidArg "full" "Fixed-duration load must have an integral duration"

    let fraction value i = value / n + (if i < value % n then 1 else 0)

    [ for i in 0 .. n - 1 do
          let classic = full.classicTxRate |> Option.map (fun v -> fraction v i)
          let soroban = full.sorobanTxRate |> Option.map (fun v -> fraction v i)

          let rate =
              match classic, soroban with
              | Some c, Some s -> c + s
              | Some c, None -> c
              | None, Some s -> s
              | None, None -> fraction full.txrate i

          yield
              { full with
                    accounts = full.accounts / n
                    offset = (full.accounts / n) * i
                    txs = if fixedDuration then rate * (full.txs / full.txrate) else full.txs / n
                    spikesize = fraction full.spikesize i
                    txrate = rate
                    classicTxRate = classic
                    sorobanTxRate = soroban } ]

// A single generator selection is shared by launch and final submission
// accounting: every validator of each core set when everyValidator
// (StellarKubeSpecs.LoadOnEveryValidator), else node 0 of each, as upstream.
let LoadgenPeerIndices (everyValidator: bool) (coreSets: CoreSet list) =
    coreSets
    |> List.collect (fun cs -> [ for i in 0 .. (if everyValidator then cs.options.nodeCount - 1 else 0) -> cs, i ])

// Pure check of an observed stellar-core pod -> worker-node mapping for
// --one-stellar-core-per-host: every pod in mustBeScheduled is listed exactly
// once and on a node, and no two scheduled pods share a node. Pods not yet
// scheduled that are not in mustBeScheduled (other core sets still starting)
// are ignored. Returns the distinct nodes. Exposed for unit tests.
let validateOnePerHost (mustBeScheduled: string list) (observed: (string * string) list) : Result<string list, string> =
    let names = List.map fst observed
    let isScheduled (node: string) = not (String.IsNullOrWhiteSpace node)

    let missing =
        mustBeScheduled
        |> List.filter (fun pod -> not (List.contains pod names))
        |> List.sort

    let duplicates =
        names
        |> List.countBy id
        |> List.filter (fun (_, c) -> c > 1)
        |> List.map fst
        |> List.sort

    let unscheduled =
        observed
        |> List.filter (fun (pod, node) -> List.contains pod mustBeScheduled && not (isScheduled node))
        |> List.map fst
        |> List.sort

    let scheduled = observed |> List.filter (fun (_, node) -> isScheduled node)

    if List.isEmpty mustBeScheduled then
        Error "no stellar-core pods to check (empty core sets)"
    elif not (List.isEmpty missing) then
        Error(
            sprintf
                "missing stellar-core pods (%d of %d): %s"
                missing.Length
                mustBeScheduled.Length
                (String.concat " " missing)
        )
    elif not (List.isEmpty duplicates) then
        Error(sprintf "duplicate stellar-core pod names: %s" (String.concat " " duplicates))
    elif not (List.isEmpty unscheduled) then
        Error(sprintf "stellar-core pods without a worker node (unscheduled): %s" (String.concat " " unscheduled))
    else
        let hosts = scheduled |> List.map snd |> List.distinct

        if hosts.Length < scheduled.Length then
            let shared =
                scheduled
                |> List.groupBy snd
                |> List.filter (fun (_, ps) -> ps.Length > 1)
                |> List.map (fun (h, ps) -> sprintf "%s: %s" h (String.concat " " (List.map fst ps)))

            Error(
                sprintf
                    "stellar-core pods share worker nodes (%d pods on %d nodes): %s"
                    scheduled.Length
                    hosts.Length
                    (String.concat "; " shared)
            )
        else
            Ok hosts

// With --one-stellar-core-per-host a stellar-core pod stays unschedulable until an
// eligible worker node is free for it. On an autoscaling cluster that is
// normal for about a minute while nodes are provisioned; past these bounds
// the run is not going to get its nodes, so it fails instead of waiting for
// replicas that can never become ready.
let unschedulableGraceSec = 180

let unschedulableAutoscaledGraceSec = 600
let autoscalerFailureConfirmSec = 60

// Last scheduling check per run (nonce). Restarts wait for every StatefulSet in
// parallel; sharing the 15 s throttle keeps that to one check per run.
let private lastSchedulingChecks = System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>()

// When the scheduler marked this pod Unschedulable, and its explanation (e.g.
// "0/16 nodes are available: 13 node(s) had untolerated taint(s), 3 node(s)
// didn't match pod anti-affinity rules"). None once it is scheduled or before
// the scheduler has looked at it. Exposed for unit tests.
let unschedulableSince (pod: V1Pod) : (DateTime * string) option =
    if isNull pod.Status || isNull pod.Status.Conditions then
        None
    else
        pod.Status.Conditions
        |> Seq.tryFind (fun c -> c.Type = "PodScheduled" && c.Status = "False" && c.Reason = "Unschedulable")
        |> Option.map
            (fun c ->
                let since =
                    if c.LastTransitionTime.HasValue then
                        c.LastTransitionTime.Value.ToUniversalTime()
                    else
                        DateTime.UtcNow

                since, (if isNull c.Message then "" else c.Message))

let private reportedByKarpenter (ev: Corev1Event) : bool =
    (not (isNull ev.ReportingComponent) && ev.ReportingComponent.Contains "karpenter")
    || (not (isNull ev.Source)
        && not (isNull ev.Source.Component)
        && ev.Source.Component.Contains "karpenter")

// Autoscaler reports on a pending pod: Karpenter's Nominated and
// cluster-autoscaler's TriggeredScaleUp mean a node is on its way; Karpenter's
// FailedScheduling and cluster-autoscaler's NotTriggerScaleUp mean no node
// pool can provide one. Exposed for unit tests.
let autoscalerProvisioning (ev: Corev1Event) : bool =
    ev.Reason = "TriggeredScaleUp"
    || (ev.Reason = "Nominated" && reportedByKarpenter ev)

let autoscalerCannotProvision (ev: Corev1Event) : bool =
    ev.Reason = "NotTriggerScaleUp"
    || (ev.Reason = "FailedScheduling" && reportedByKarpenter ev)

let private eventTime (ev: Corev1Event) : DateTime =
    if ev.LastTimestamp.HasValue then ev.LastTimestamp.Value
    elif ev.EventTime.HasValue then ev.EventTime.Value
    else DateTime.MinValue

// The failure to report when the run cannot schedule its stellar-core pods, if it
// cannot: the longest-stalled pod that the autoscaler says it cannot provision
// a node for (once that has held for autoscalerFailureConfirmSec) or that has
// been unschedulable past the grace period (longer while an autoscaler is
// provisioning for this run). Each stalled entry is (pod, unschedulable since,
// scheduler message, autoscaler failure). Exposed for unit tests.
let schedulingStallVerdict
    (now: DateTime)
    (autoscalerActive: bool)
    (podCount: int)
    (stalled: (string * DateTime * string * string option) list)
    : string option =
    let grace =
        if autoscalerActive then
            unschedulableAutoscaledGraceSec
        else
            unschedulableGraceSec

    let fix =
        sprintf
            "--one-stellar-core-per-host needs a separate eligible worker node for each of the %d stellar-core pods: make that many nodes available to this run (node pool size or limits, labels matching --require-node-labels and --avoid-node-labels, taints covered by --tolerate-node-taints), run a smaller topology, or drop --one-stellar-core-per-host"
            podCount

    stalled
    |> List.sortBy (fun (_, since, _, _) -> since)
    |> List.tryPick
        (fun (pod, since, schedulerMessage, autoscalerFailure) ->
            let waited = int (now - since).TotalSeconds

            match autoscalerFailure with
            | Some failure when waited >= autoscalerFailureConfirmSec ->
                Some(
                    sprintf
                        "Stellar-core pod %s cannot be scheduled and the cluster autoscaler cannot provision a node for it: %s. %s."
                        pod
                        failure
                        fix
                )
            | _ when waited >= grace ->
                Some(
                    sprintf
                        "Stellar-core pod %s has been unschedulable for %d s (%s): %s. %s."
                        pod
                        waited
                        (if autoscalerActive then
                             "while the autoscaler was provisioning nodes"
                         else
                             "no autoscaler is provisioning nodes for this run")
                        schedulerMessage
                        fix
                )
            | _ -> None)

// What the autoscaler says about the stalled stellar-core pods: whether it is
// provisioning nodes for this run at all, and for each stalled pod its latest
// report that it cannot provision one (unless a nomination came after it).
// Events outlive their pods and StatefulSet pods reuse names across restarts,
// so only events for the current pods' UIDs count. stalled holds (pod,
// unschedulable since, scheduler message). Exposed for unit tests.
let autoscalerView
    (podUids: Set<string>)
    (events: Corev1Event list)
    (stalled: (string * DateTime * string) list)
    : bool * (string * DateTime * string * string option) list =
    let current =
        events
        |> List.filter (fun ev -> not (isNull ev.InvolvedObject) && podUids.Contains ev.InvolvedObject.Uid)

    let latest (pred: Corev1Event -> bool) (pod: string) =
        current
        |> List.filter (fun ev -> ev.InvolvedObject.Name = pod && pred ev)
        |> List.sortBy eventTime
        |> List.tryLast

    let withFailures =
        stalled
        |> List.map
            (fun (pod, since, msg) ->
                let failure =
                    match latest autoscalerCannotProvision pod, latest autoscalerProvisioning pod with
                    | Some f, Some n when eventTime n > eventTime f -> None
                    | Some f, _ -> Some f.Message
                    | None, _ -> None

                pod, since, msg, failure)

    (current |> List.exists autoscalerProvisioning), withFailures

// Bounded overlay-mesh wait (--overlay-v2-optimized). A libp2p
// simultaneous-dial collision can leave one peer edge missing after a boot or
// a mass restart. It never heals, so WaitUntilConnected would wait forever.
// Instead, wait (bounded) for every node to answer, then for a full mesh, and
// restart all nodes to redraw a mesh that stops growing.
type MeshWaitBounds =
    { pollSec: int
      // Every node must answer within this of a (re)start. The core
      // container's liveness probe restarts a core that is still not
      // answering about 3 min after it starts, so a node silent this long is
      // crash-looping, and a redraw would not help.
      bootTimeoutSec: int
      // Once every node has answered, an incomplete mesh whose connection
      // count has not grown for this long has wedged: two ticks of the
      // overlay's 30 s safety-net reconnect ...
      stallSec: int
      // ... and one still incomplete this long is redrawn anyway.
      meshTimeoutSec: int
      maxAttempts: int }

let meshWaitBounds =
    { pollSec = 5
      bootTimeoutSec = 300
      stallSec = 60
      meshTimeoutSec = 120
      maxAttempts = 3 }

// One probe of the mesh: the nodes that did not answer, the nodes
// connected to all of their preferred peers, all nodes, and the authenticated
// connections summed over the nodes that answered.
type MeshSample = { silent: string list; fullyConnected: int; total: int; connections: int }

type MeshWaitVerdict =
    | Meshed
    | KeepWaiting
    | BootTimedOut
    | Wedged

// When the mesh phase of an attempt started (every node had answered), when
// the connection count last grew, and its high-water mark.
type MeshWaitState = { meshStartSec: int option; lastProgressSec: int; bestConnections: int }

let initialMeshWaitState = { meshStartSec = None; lastProgressSec = 0; bestConnections = 0 }

// Judges a sample taken `nowSec` (wall clock) into an attempt, returning the
// verdict and the state for the next sample. Until every node has answered a
// probe only the boot bound applies; from then on a node missing a probe just
// adds no connections. Exposed for unit tests.
let stepMeshWait
    (b: MeshWaitBounds)
    (s: MeshWaitState)
    (nowSec: int)
    (m: MeshSample)
    : MeshWaitVerdict * MeshWaitState =
    if m.total > 0 && m.fullyConnected = m.total then
        Meshed, s
    elif s.meshStartSec.IsNone && not (List.isEmpty m.silent) then
        (if nowSec >= b.bootTimeoutSec then BootTimedOut else KeepWaiting), s
    else
        let meshStart = defaultArg s.meshStartSec nowSec

        let lastProgress =
            if s.meshStartSec.IsNone || m.connections > s.bestConnections then
                nowSec
            else
                s.lastProgressSec

        let verdict =
            if nowSec - lastProgress >= b.stallSec || nowSec - meshStart >= b.meshTimeoutSec then
                Wedged
            else
                KeepWaiting

        verdict,
        { meshStartSec = Some meshStart
          lastProgressSec = lastProgress
          bestConnections = max s.bestConnections m.connections }

type StellarFormation with

    member self.LoadGenPeers (coreSets: CoreSet list) (loadGen: LoadGen) =
        LoadgenPeerIndices(LoadOnEveryValidator self.NetworkCfg.missionContext loadGen.mode) coreSets
        |> List.map (fun (cs, i) -> self.NetworkCfg.GetPeer cs i)

    member self.GetCoreSetForStatefulSet(ss: V1StatefulSet) =
        List.find (fun cs -> (self.NetworkCfg.StatefulSetName cs).StringName = ss.Name()) self.NetworkCfg.CoreSetList

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasReady(ss: V1StatefulSet) =
        let name = ss.Metadata.Name
        let ns = ss.Metadata.NamespaceProperty
        let fs = sprintf "metadata.name=%s" name
        let mutable forbiddenEvent = None

        // This pattern of a recursive handler-install routine that reinstalls
        // itself when `onClosed` fires is necessary because watches
        // automatically time out after 100 seconds and the connection closes.
        let rec installHandler () =
            async {
                LogInfo "Waiting for replicas on %s/%s" ns name

                // First we check to see if we've been woken up because a FailedCreate + forbidden
                // event occurred; this happens typically when we exceed quotas on a cluster or
                // some other policy reason.

                for ev in self.GetEventsForObject(name).Items do
                    if ev.Reason = "FailedCreate" && ev.Message.Contains("forbidden") then
                        // If so, we record the causal event and wake up the waiter.
                        forbiddenEvent <- Some(ev)

                match forbiddenEvent with
                | Some (ev) -> ()
                | None ->
                    if self.NetworkCfg.missionContext.oneStellarCorePerHost then
                        let now = DateTime.UtcNow
                        let run = self.NetworkCfg.Nonce
                        let last = lastSchedulingChecks.GetOrAdd(run, DateTime.MinValue)

                        if
                            (now - last).TotalSeconds >= 15.0
                            && lastSchedulingChecks.TryUpdate
                                (
                                    run,
                                    now,
                                    last
                                )
                        then
                            self.FailIfCorePodsUnschedulable()

                    self.sleepUntilNextRateLimitedApiCallTime ()

                    let s =
                        self
                            .Kube
                            .ListNamespacedStatefulSet(namespaceParameter = ns, fieldSelector = fs)
                            .Items.Item(0)
                    // Assuming we weren't failed, we look to see how the sts is doing in terms
                    // of creating the number of ready replicas we asked for.
                    let n = s.Status.ReadyReplicas.GetValueOrDefault(0)
                    let k = s.Spec.Replicas.GetValueOrDefault(0)
                    LogInfo "StatefulSet %s/%s: %d/%d replicas ready" ns name n k

                    if n <> k then
                        // Still need to wait a bit longer
                        do! Async.Sleep(3000)
                        return! installHandler ()
            }

        installHandler () |> Async.RunSynchronously

        match forbiddenEvent with
        | None -> ()
        | Some (ev) -> failwith (sprintf "Statefulset %s pod creation forbidden: %s" name ev.Message)

        let coreSet = self.GetCoreSetForStatefulSet ss
        self.LaunchLogTailingTasksForCoreSet coreSet

        LogInfo "All replicas on %s/%s ready" ns name

        // Every time a core set's pods come up, at formation creation and on
        // every restart, whatever the mission.
        if self.NetworkCfg.missionContext.oneStellarCorePerHost && coreSet.CurrentCount > 0 then
            self.CheckCorePlacement [ coreSet ]

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasOnAllSetsReady() =
        if not self.StatefulSets.IsEmpty then
            LogInfo "Waiting for replicas on %s" (self.ToString())

            for ss in self.StatefulSets do
                self.WaitForAllReplicasReady ss

            LogInfo "All replicas on %s ready" (self.ToString())

    // This run's stellar-core StatefulSet pods, selected by the label that
    // --one-stellar-core-per-host puts on them.
    member self.ListCorePods() : V1Pod list =
        let cfg = self.NetworkCfg

        let selector =
            sprintf
                "app=stellar-core,%s=%s,%s=%s"
                StellarCoreCfg.CfgVal.onePerHostLabelKey
                StellarCoreCfg.CfgVal.onePerHostLabelValue
                StellarCoreCfg.CfgVal.runNonceLabelKey
                cfg.Nonce

        self.sleepUntilNextRateLimitedApiCallTime ()

        self
            .Kube
            .ListNamespacedPod(
                namespaceParameter = cfg.NamespaceProperty,
                labelSelector = selector
            )
            .Items
        |> List.ofSeq

    // --one-stellar-core-per-host: logs where the given core sets' pods landed
    // and fails unless each is scheduled and no two of this run's stellar-core
    // pods share a worker node (see validateOnePerHost); pods on their way out
    // are left out. WaitForAllReplicasReady runs it whenever a core set's pods
    // have come up, so formation creation and every restart, in every
    // mission, are checked. The mapping is otherwise invisible in run logs,
    // and a packed placement silently changes what a benchmark measures.
    member self.CheckCorePlacement(coreSets: CoreSet list) =
        let cfg = self.NetworkCfg

        let mustBeScheduled =
            [ for cs in coreSets do
                  for i in 0 .. cs.CurrentCount - 1 do
                      yield (cfg.PodName cs i).StringName ]

        let observed =
            self.ListCorePods()
            |> List.filter (fun p -> not p.Metadata.DeletionTimestamp.HasValue)
            |> List.map (fun p -> p.Metadata.Name, (if isNull p.Spec.NodeName then "" else p.Spec.NodeName))

        for pod, node in observed |> List.filter (fun (pod, _) -> List.contains pod mustBeScheduled) do
            LogInfo "Placement: %s on %s" pod (if node = "" then "<unscheduled>" else node)

        match validateOnePerHost mustBeScheduled observed with
        | Ok hosts ->
            LogInfo "Placement check passed: %d stellar-core pods of this run, one per worker node" hosts.Length
        | Error msg -> failwithf "Placement check failed (--one-stellar-core-per-host): %s" msg

    // With --one-stellar-core-per-host, fail with an actionable message as soon
    // as stellar-core pods clearly cannot be scheduled (see autoscalerView and
    // schedulingStallVerdict), instead of waiting for replicas that can never
    // become ready. Uses only namespaced pod and event reads.
    member self.FailIfCorePodsUnschedulable() =
        let pods = self.ListCorePods()

        let stalled =
            pods
            |> List.choose
                (fun p ->
                    unschedulableSince p
                    |> Option.map (fun (since, msg) -> p.Metadata.Name, since, msg))

        if not (List.isEmpty stalled) then
            let podEvents (reason: string) =
                self.sleepUntilNextRateLimitedApiCallTime ()

                self
                    .Kube
                    .ListNamespacedEvent(
                        namespaceParameter = self.NetworkCfg.NamespaceProperty,
                        fieldSelector = sprintf "involvedObject.kind=Pod,reason=%s" reason
                    )
                    .Items
                |> List.ofSeq

            let events =
                [ "Nominated"; "TriggeredScaleUp"; "FailedScheduling"; "NotTriggerScaleUp" ]
                |> List.collect podEvents

            let podUids = pods |> List.map (fun p -> p.Metadata.Uid) |> Set.ofList
            let autoscalerActive, withAutoscaler = autoscalerView podUids events stalled
            let now = DateTime.UtcNow
            let _, oldest, oldestMessage, _ = withAutoscaler |> List.minBy (fun (_, since, _, _) -> since)

            LogInfo
                "%d stellar-core pod(s) unschedulable, longest for %d s (autoscaler %s): %s"
                stalled.Length
                (int (now - oldest).TotalSeconds)
                (if autoscalerActive then "provisioning" else "not seen")
                oldestMessage

            let podCount = self.NetworkCfg.CoreSetList |> List.sumBy (fun cs -> cs.CurrentCount)

            match schedulingStallVerdict now autoscalerActive podCount withAutoscaler with
            | Some failure -> failwith failure
            | None -> ()

    member self.WithLive name (live: bool) =
        // Serialize the shared-state mutation and the StatefulSet replace:
        // missions start and stop core sets concurrently, and an unsynchronized
        // read-modify-write of networkCfg/statefulSets here would lose a
        // node's live update and leave its StatefulSet scaled to 0 replicas
        // (wedging the network, which then waits forever for the missing peer
        // connections). The slow readiness wait stays outside the lock so
        // nodes still come up in parallel.
        let ss =
            lock
                self.StateLock
                (fun () ->
                    self.SetNetworkCfg(self.NetworkCfg.WithLive name live)
                    let coreSet = self.NetworkCfg.FindCoreSet name
                    let stsName = self.NetworkCfg.StatefulSetName coreSet
                    self.sleepUntilNextRateLimitedApiCallTime ()

                    let ss =
                        self.Kube.ReplaceNamespacedStatefulSet(
                            body = self.NetworkCfg.ToStatefulSet coreSet,
                            name = stsName.StringName,
                            namespaceParameter = self.NetworkCfg.NamespaceProperty
                        )

                    let newSets =
                        self.StatefulSets
                        |> List.filter (fun x -> x.Metadata.Name <> stsName.StringName)

                    self.SetStatefulSets(ss :: newSets)
                    ss)

        self.WaitForAllReplicasReady ss

    member self.Start name = self.WithLive name true

    member self.Stop name = self.WithLive name false

    // Replaces the named CoreSet's options (preserving its keys) and pushes the
    // regenerated per-peer ConfigMaps to the cluster. Callers should Stop the
    // core set first, call this, then Start it so the pods boot with the new
    // configuration.
    member self.ChangeCoreSetOptions (name: CoreSetName) (options: CoreSetOptions) =
        let newCfg = self.NetworkCfg.WithCoreSetOptions name options
        let coreSet = newCfg.FindCoreSet name

        for i in 0 .. coreSet.keys.Length - 1 do
            let cm = newCfg.PeerConfigMap(coreSet, i)
            self.sleepUntilNextRateLimitedApiCallTime ()

            self.Kube.ReplaceNamespacedConfigMap(
                body = cm,
                name = cm.Metadata.Name,
                namespaceParameter = newCfg.NamespaceProperty
            )
            |> ignore

            LogInfo "Replaced ConfigMap %s with reconfigured options" cm.Metadata.Name

        self.SetNetworkCfg newCfg

    member self.WaitUntilReady() = self.NetworkCfg.EachPeer(fun p -> p.WaitUntilReady())

    member self.WaitUntilAllLiveSynced() = self.NetworkCfg.EachPeer(fun p -> p.WaitUntilSynced())

    member self.WaitUntilSynced(coreSetList: CoreSet list) =
        coreSetList
        |> List.iter
            (fun coreSet ->
                if coreSet.CurrentCount = 0 then
                    failwith ("Coreset " + coreSet.name.StringName + " is not live"))

        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.WaitUntilSynced())

    // Waits until every node is connected to all of its preferred peers. Under
    // --overlay-v2-optimized that is the bounded mesh wait, which returns only
    // once every node is fully connected and redraws a wedged mesh instead of
    // waiting forever (EnsureMeshedOrRedraw); otherwise the unbounded per-node
    // wait.
    member self.WaitUntilConnected(coreSetList: CoreSet list) =
        if self.NetworkCfg.missionContext.overlayV2Optimized then
            self.EnsureMeshedOrRedraw coreSetList
        else
            self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.WaitUntilConnected)

    // Probes every node once, in parallel and without retries, for the bounded
    // mesh wait.
    member self.MeshProgress(coreSetList: CoreSet list) : MeshSample =
        let peers = self.NetworkCfg.PeersInSets(coreSetList |> Array.ofList)

        let counts =
            peers
            |> List.map (fun p -> async { return p, p.TryGetAuthenticatedCount() })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> List.ofArray

        { silent =
              counts
              |> List.filter (fun (_, c) -> c.IsNone)
              |> List.map (fun (p, _) -> p.ShortName.StringName)
          fullyConnected =
              counts
              |> List.filter
                  (fun (p, c) ->
                      match c with
                      | Some n -> n >= p.DesiredNumberOfConnections
                      | None -> false)
              |> List.length
          total = List.length peers
          connections = counts |> List.sumBy (fun (_, c) -> defaultArg c 0) }

    // --overlay-v2-optimized: waits for the overlay mesh of `coreSetList` to
    // complete, within meshWaitBounds, restarting all of its nodes to redraw a
    // wedged mesh. Fails if a node never answers or the mesh never forms.
    member self.EnsureMeshedOrRedraw(coreSetList: CoreSet list) =
        let b = meshWaitBounds

        let restartAll () =
            coreSetList
            |> List.map (fun cs -> async { self.Stop cs.name })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> ignore

            coreSetList
            |> List.map (fun cs -> async { self.Start cs.name })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> ignore

        let waitOnce (attempt: int) : MeshWaitVerdict * MeshSample =
            let clock = System.Diagnostics.Stopwatch.StartNew()
            let mutable state = initialMeshWaitState
            let mutable nextLogSec = 30
            let mutable result = None

            while result.IsNone do
                let m = self.MeshProgress coreSetList
                let nowSec = int clock.Elapsed.TotalSeconds

                match stepMeshWait b state nowSec m with
                | KeepWaiting, next ->
                    if nowSec >= nextLogSec then
                        nextLogSec <- nowSec + 30

                        LogInfo
                            "Overlay mesh: %d/%d nodes answering, %d fully connected, %d connections (attempt %d/%d, %ds)"
                            (m.total - List.length m.silent)
                            m.total
                            m.fullyConnected
                            m.connections
                            attempt
                            b.maxAttempts
                            nowSec

                    state <- next
                    System.Threading.Thread.Sleep(b.pollSec * 1000)
                | verdict, _ -> result <- Some(verdict, m)

            result.Value

        let rec attempt (n: int) =
            match waitOnce n with
            | Meshed, m -> LogInfo "Overlay mesh complete: %d/%d nodes fully connected (attempt %d)" m.total m.total n
            | BootTimedOut, m ->
                failwithf
                    "Overlay mesh: %d of %d nodes did not answer within %d s of starting: %s"
                    (List.length m.silent)
                    m.total
                    b.bootTimeoutSec
                    (String.concat ", " m.silent)
            | _, m when n < b.maxAttempts ->
                LogWarn
                    "Overlay mesh wedged at %d/%d fully connected nodes, %d connections; restarting all nodes to redraw it (attempt %d/%d)"
                    m.fullyConnected
                    m.total
                    m.connections
                    (n + 1)
                    b.maxAttempts

                restartAll ()
                attempt (n + 1)
            | _, m ->
                failwithf
                    "Overlay mesh failed to form after %d attempts: %d/%d nodes fully connected, %d connections"
                    b.maxAttempts
                    m.fullyConnected
                    m.total
                    m.connections

        attempt 1

    member self.EnsureAllNodesInSync(coreSetList: CoreSet list) =
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.EnsureInSync)

    member self.ManualClose(coreSetList: CoreSet list) =
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.ManualClose())

    // When upgrading multiple nodes, configure upgrade time a bit ahead to ensure nodes have enough
    // of a buffer to set upgrades
    member self.UpgradeProtocol (coreSetList: CoreSet list) (version: int) =
        let upgradeTime = System.DateTime.UtcNow.AddSeconds(15.0)
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.UpgradeProtocol version upgradeTime)
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForProtocol(version) |> ignore

    member self.ScheduleProtocolUpgrade (coreSetList: CoreSet list) (version: int) (upgradeTime: System.DateTime) =
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.UpgradeProtocol version upgradeTime)

    member self.UpgradeProtocolToLatest(coreSetList: CoreSet list) =
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        let latest = peer.GetSupportedProtocolVersion()
        self.UpgradeProtocol coreSetList latest

    member self.UpgradeMaxTxSetSize (coreSetList: CoreSet list) (maxTxSetSize: int) =
        let upgradeTime = System.DateTime.UtcNow.AddSeconds(15.0)

        self.NetworkCfg.EachPeerInSets
            (coreSetList |> Array.ofList)
            (fun p -> p.UpgradeMaxTxSetSize maxTxSetSize upgradeTime)

        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForMaxTxSetSize maxTxSetSize |> ignore

    member self.UpgradeSorobanMaxTxSetSize (coreSetList: CoreSet list) (maxTxSetSize: int) =
        self.NetworkCfg.EachPeerInSets
            (coreSetList |> Array.ofList)
            (fun p -> p.UpgradeSorobanMaxTxSetSize maxTxSetSize System.DateTime.UtcNow)

        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForSorobanMaxTxSetSize maxTxSetSize |> ignore

    member self.UpgradeSorobanLedgerLimitsWithMultiplier (coreSetList: CoreSet list) (multiplier: int) =
        self.SetupUpgradeContract coreSetList.[0]
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0

        let expectedInstructions = peer.GetLedgerMaxInstructions() * int64 (multiplier)

        self.DeployUpgradeEntriesAndArm
            coreSetList
            { LoadGen.GetDefault() with
                  mode = CreateSorobanUpgrade
                  ledgerMaxInstructions = Some(expectedInstructions)
                  ledgerMaxReadBytes = Some(peer.GetLedgerReadBytes() * multiplier)
                  ledgerMaxWriteBytes = Some(peer.GetLedgerWriteBytes() * multiplier)
                  ledgerMaxTxCount = Some(peer.GetSorobanMaxTxSetSize() * multiplier)
                  ledgerMaxReadLedgerEntries = Some(peer.GetLedgerReadEntries() * multiplier)
                  ledgerMaxWriteLedgerEntries = Some(peer.GetLedgerWriteEntries() * multiplier)
                  ledgerMaxTransactionsSizeBytes = Some(peer.GetLedgerMaxTransactionsSizeBytes() * multiplier) }
            (System.DateTime.UtcNow)

        peer.WaitForLedgerMaxInstructions expectedInstructions |> ignore

    member self.UpgradeSorobanTxLimitsWithMultiplier (coreSetList: CoreSet list) (multiplier: int) =
        self.SetupUpgradeContract coreSetList.[0]
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0

        let expectedInstructions = peer.GetTxMaxInstructions() * int64 (multiplier)

        self.DeployUpgradeEntriesAndArm
            coreSetList
            { LoadGen.GetDefault() with
                  mode = CreateSorobanUpgrade
                  txMaxInstructions = Some(expectedInstructions)
                  txMaxReadBytes = Some(peer.GetTxReadBytes() * multiplier)
                  txMaxWriteBytes = Some(peer.GetTxWriteBytes() * multiplier)
                  txMaxReadLedgerEntries = Some(peer.GetTxReadEntries() * multiplier)
                  txMaxWriteLedgerEntries = Some(peer.GetTxWriteEntries() * multiplier)
                  txMaxSizeBytes = Some(peer.GetMaxTxSize() * multiplier)
                  txMemoryLimit = Some(peer.GetTxMemoryLimit() * multiplier)
                  maxContractSizeBytes = Some(peer.GetMaxContractSize() * multiplier)
                  maxContractDataKeySizeBytes = Some(peer.GetMaxContractDataKeySize() * multiplier)
                  maxContractDataEntrySizeBytes = Some(peer.GetMaxContractDataEntrySize() * multiplier)
                  txMaxContractEventsSizeBytes = Some(peer.GetTxMaxContractEventsSize() * multiplier)
                  // For protocol versions before p23, we shouldn't set txMaxFootprintSize
                  txMaxFootprintSize = Option.map ((*) multiplier) (peer.GetTxMaxFootprintSize()) }
            (System.DateTime.UtcNow)

        peer.WaitForTxMaxInstructions expectedInstructions |> ignore

    member self.UpgradeToMinimumSCPConfig(coreSetList: CoreSet list) =
        self.SetupUpgradeContract coreSetList.[0]
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0

        self.DeployUpgradeEntriesAndArm
            coreSetList
            { LoadGen.GetDefault() with
                  mode = CreateSorobanUpgrade
                  ledgerTargetCloseTimeMilliseconds = Some(4000)
                  ballotTimeoutIncrementMilliseconds = Some(750)
                  ballotTimeoutInitialMilliseconds = Some(750)
                  nominationTimeoutInitialMilliseconds = Some(750)
                  nominationTimeoutIncrementMilliseconds = Some(750) }
            (System.DateTime.UtcNow.AddSeconds(20.0))

        peer.WaitForScpLedgerCloseTime 4000 |> ignore

    member self.UpgradeSCPTargetLedgerCloseTime (coreSetList: CoreSet list) (closeTimeMs: int) =
        self.SetupUpgradeContract coreSetList.[0]
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0

        self.DeployUpgradeEntriesAndArm
            coreSetList
            { LoadGen.GetDefault() with
                  mode = CreateSorobanUpgrade
                  ledgerTargetCloseTimeMilliseconds = Some(closeTimeMs) }
            (System.DateTime.UtcNow.AddSeconds(20.0))

        peer.WaitForScpLedgerCloseTime closeTimeMs |> ignore

    member self.ReportStatus() = ReportAllPeerStatus self.NetworkCfg

    member self.CreateAccount (coreSet: CoreSet) (u: Username) =
        let peer = self.NetworkCfg.GetPeer coreSet 0
        let tx = peer.TxCreateAccount u
        LogInfo "creating account for %O on %O" u self
        peer.SubmitSignedTransaction tx |> ignore
        peer.WaitForNextLedger() |> ignore
        let acc = peer.GetAccount(u)

        LogInfo
            "created account for %O on %O with seq %d, balance %d"
            u
            self
            acc.SequenceNumber
            (peer.GetTestAccBalance(u.ToString()))

    member self.Pay (coreSet: CoreSet) (src: Username) (dst: Username) =
        let peer = self.NetworkCfg.GetPeer coreSet 0
        let tx = peer.TxPayment src dst
        let seq = peer.GetTestAccSeq(src.ToString())

        LogInfo "paying from account %O to %O on %O" src dst self
        peer.SubmitSignedTransaction tx |> ignore
        peer.WaitForNextSeq(src.ToString()) seq |> ignore

        LogInfo
            "sent payment from %O (%O) to %O (%O) on %O"
            src
            (peer.GetSeqAndBalance src)
            dst
            (peer.GetSeqAndBalance dst)
            self

    member self.CheckNoErrorsAndPairwiseConsistency() =
        let cs = List.filter (fun cs -> cs.live = true) self.NetworkCfg.CoreSetList

        if not (List.isEmpty cs) then
            let peer = self.NetworkCfg.GetPeer cs.[0] 0

            self.NetworkCfg.EachPeer
                (fun p ->
                    // REVERTME: Temporarily disable abnormal-event checking
                    // self.CheckNoAbnormalKubeEvents p
                    p.CheckNoErrorMetrics(includeTxInternalErrors = false)
                    p.CheckConsistencyWith peer)

    member self.CheckUsesLatestProtocolVersion() = self.NetworkCfg.EachPeer(fun p -> p.CheckUsesLatestProtocolVersion())

    member self.RunLoadgen (coreSet: CoreSet) (loadGen: LoadGen) =
        let peer = self.NetworkCfg.GetPeer coreSet 0
        LogInfo "Loadgen: %s" (peer.GenerateLoad loadGen)
        peer.WaitForLoadGenComplete loadGen

    member self.SetupUpgradeContract(coreSet: CoreSet) =
        let loadgen =
            { LoadGen.GetDefault() with
                  mode = SetupSorobanUpgrade
                  minSorobanPercentSuccess = Some 100 }

        self.RunLoadgen coreSet loadgen

    // Deploy upgrade-set entries by running a CreateSorobanUpgrade loadgen on node 0
    // of the first core set, and return the resulting ConfigUpgradeSetKey so the
    // caller can arm it later. Keeping deploy separate from arm lets a caller create
    // the upgrade-set entry while the network is quiet: deploying it takes a single
    // Soroban transaction, and once concurrent load saturates the
    // (instruction-limited) Soroban lane that transaction can be starved and never
    // make it into a ledger, leaving the entry unwritten.
    member self.DeployUpgradeEntries (coreSetList: CoreSet list) (loadGen: LoadGen) : string =
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        let resStr = peer.GenerateLoad loadGen

        let configUpgradeSetKey = Loadgen.Parse(resStr).ConfigUpgradeSetKey

        LogInfo "Loadgen: %s" resStr
        peer.WaitForLoadGenComplete loadGen
        configUpgradeSetKey

    // Arm a previously-deployed upgrade-set (identified by configUpgradeSetKey) on each peer
    // in the given core sets, to take effect at upgradeTime.
    member self.ArmUpgradeEntries
        (coreSetList: CoreSet list)
        (configUpgradeSetKey: string)
        (upgradeTime: System.DateTime)
        =
        self.NetworkCfg.EachPeerInSets
            (List.toArray coreSetList)
            (fun peer -> peer.UpgradeNetworkSetting configUpgradeSetKey upgradeTime)

    member self.DeployUpgradeEntriesAndArm
        (coreSetList: CoreSet list)
        (loadGen: LoadGen)
        (upgradeTime: System.DateTime)
        =
        let configUpgradeSetKey = self.DeployUpgradeEntries coreSetList loadGen
        self.ArmUpgradeEntries coreSetList configUpgradeSetKey upgradeTime

    member self.DeployUpgradeEntriesAndArmAfter
        (coreSetList: CoreSet list)
        (loadGen: LoadGen)
        (delay: System.TimeSpan)
        =
        let configUpgradeSetKey = self.DeployUpgradeEntries coreSetList loadGen
        let upgradeTime = System.DateTime.UtcNow.Add(delay)
        self.ArmUpgradeEntries coreSetList configUpgradeSetKey upgradeTime

    member self.clearMetrics(coreSets: CoreSet list) =
        self.NetworkCfg.PeersInSets(coreSets |> List.toArray)
        |> List.map (fun peer -> async { peer.ClearMetrics() })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore

    // This is similar to RunLoadgen but runs a 1/N fractional portion of a
    // given LoadGen on each of N generators (LoadGenPeers: node 0 of each
    // CoreSet, or every validator of each for LoadOnEveryValidator runs).
    member self.RunMultiLoadgen (coreSets: CoreSet list) (fullLoadGen: LoadGen) =
        let everyValidator = LoadOnEveryValidator self.NetworkCfg.missionContext fullLoadGen.mode
        let loadGenPeers = self.LoadGenPeers coreSets fullLoadGen
        let shares = PartitionValidatorLoad loadGenPeers.Length everyValidator fullLoadGen

        let hasNonZeroRate (loadGen: LoadGen) =
            match loadGen.classicTxRate, loadGen.sorobanTxRate with
            | Some classicRate, Some sorobanRate -> classicRate <> 0 || sorobanRate <> 0
            | Some classicRate, None -> classicRate <> 0
            | None, Some sorobanRate -> sorobanRate <> 0
            | None, None -> true

        let peerLoadGens =
            List.zip loadGenPeers shares
            |> List.map (fun (peer, loadGen) -> peer, loadGen, loadGen.offset)
            |> List.filter (fun (_, loadGen, _) -> hasNonZeroRate loadGen)

        if List.isEmpty peerLoadGens then
            failwith "Loadgen failed: no peer has a non-zero tx rate"

        for (peer, peerSpecificLoadgen, offset) in peerLoadGens do
            LogInfo
                "LOAD_SHARE peer=%s rate=%d txs=%d accounts=%d offset=%d"
                peer.ShortName.StringName
                peerSpecificLoadgen.txrate
                peerSpecificLoadgen.txs
                peerSpecificLoadgen.accounts
                offset

            LogInfo "Loadgen: %s with offset %d" (peer.GenerateLoad peerSpecificLoadgen) offset

        while List.exists
                  (fun (peer: Peer, _, _) ->
                      not (peer.IsLoadGenComplete() = Success || peer.IsLoadGenComplete() = Failure))
                  peerLoadGens do
            Thread.Sleep(millisecondsTimeout = 3000)

            for (peer, loadGen, _) in peerLoadGens do
                peer.LogLoadGenProgressTowards(loadGen)

            // Check if any loadGen has failed
            if List.exists (fun (peer: Peer, _, _) -> peer.IsLoadGenComplete() = Failure) peerLoadGens then
                // Stop all runs
                for (peer, _, _) in peerLoadGens do
                    LogInfo "%s  loadgen: %s" (peer.ShortName.ToString()) (peer.StopLoadGen())

                failwith "Loadgen failed!"

        // Final check after the loop completes
        if List.exists (fun (peer: Peer, _, _) -> peer.IsLoadGenComplete() = Failure) peerLoadGens then
            failwith "Loadgen failed!"

    // Deploys TCP tuning DaemonSets to configure node-level network settings if enabled.
    // We want a DaemonSet here to ensure one pod per node automatically.
    // Script runs with --daemon flag so that the DaemonSet is marked "Ready" if script is successful.
    // Settings persist on nodes after DaemonSet deletion, we manually delete the DaemonSet after the run,
    // or exit with an error code if any were not in the "Ready" state.
    member self.MaybeDeployTcpTuningDaemonSet() : unit =
        if self.NetworkCfg.missionContext.enableTcpTuning then
            let ns = self.NetworkCfg.NamespaceProperty

            LogInfo "Creating TCP tuning ConfigMap..."
            let configMap = BenchmarkDaemonSet.createTcpTuningConfigMap self.NetworkCfg

            self.Kube.CreateNamespacedConfigMap(body = configMap, namespaceParameter = ns)
            |> ignore

            self.NamespaceContent.Add(configMap)
            LogInfo "Setting TCP settings for network performance"
            let daemonSetName = "tcp-tuning"
            let daemonSet = BenchmarkDaemonSet.createTcpTuningDaemonSet self.NetworkCfg
            let actionMsg = "applied"

            // Create and deploy the DaemonSet
            let ds = self.Kube.CreateNamespacedDaemonSet(body = daemonSet, namespaceParameter = ns)
            LogInfo "Created %s DaemonSet, waiting for settings to be %s..." daemonSetName actionMsg

            // Wait for DaemonSet to be ready on all nodes
            let mutable allReady = false
            let mutable attempts = 0
            let maxAttempts = 30

            while not allReady && attempts < maxAttempts do
                System.Threading.Thread.Sleep(2000)
                attempts <- attempts + 1

                try
                    let currentDs = self.Kube.ReadNamespacedDaemonSet(name = daemonSetName, namespaceParameter = ns)
                    let desired = currentDs.Status.DesiredNumberScheduled
                    let numReady = currentDs.Status.NumberReady
                    LogInfo "%s DaemonSet: %d/%d nodes ready" daemonSetName numReady desired

                    if desired > 0 && numReady = desired then
                        allReady <- true
                        LogInfo "TCP settings %s on all %d nodes" actionMsg desired
                with ex -> LogWarn "Failed to check %s DaemonSet status: %s" daemonSetName ex.Message

            if not allReady then
                failwithf
                    "TCP tuning DaemonSet did not complete on all nodes within timeout (waited %d seconds)"
                    (maxAttempts * 2)
            else
                // Give a bit more time for tuning settings to take effect
                System.Threading.Thread.Sleep(3000)

            // Delete the DaemonSet - settings will persist on nodes
            try
                self.Kube.DeleteNamespacedDaemonSet(name = daemonSetName, namespaceParameter = ns)
                |> ignore
            with ex ->
                LogWarn "Failed to delete %s DaemonSet: %s" daemonSetName ex.Message
                // Track it for cleanup if deletion failed
                self.NamespaceContent.Add(ds)

    // Runs a P2P network infrastructure benchmark that mirrors the stellar-core network topology.
    //
    // Architecture Overview:
    // - Creates a benchmark pod for each stellar-core node in the network with same connection topology of the actual stellar-core mission
    // - Each benchmark pod runs in a StatefulSet (1 replica each) for stable DNS names
    // - All StatefulSets share a single headless service for DNS resolution
    // - Pod naming: {runId}-benchmark-{shortName}-0 (e.g., ssc-1234z-benchmark-lo-0-0)
    //
    // Pod Structure:
    // Each benchmark pod contains two containers:
    // 1. Server container: Runs multiple iperf3 servers
    //    - One server per incoming connection from other nodes
    //    - Each server listens on port 5201 + source_node_index (ensures unique ports)
    // 2. Client container: Runs iperf3 clients
    //    - Connects to all peer nodes as defined in the stellar-core topology
    //    - iperf3 sends the maximum possible traffic to each peer node simultaneously
    //    - Raw results saved to /results/*.json in the container
    //
    // Results Collection:
    // - After tests complete, collects logs and raw iperf3 JSON results from all pods
    // - parse_benchmark_results.py creates and writes a results summary file

    // Helper function to setup network topology and create node index mappings
    member private self.SetupBenchmarkTopology() =
        let topology = extractPeerTopology self.NetworkCfg
        let avgPeerCount = getAveragePeerCount topology

        LogInfo "Network topology: %d nodes, average %.1f peers per node" (Map.count topology) avgPeerCount

        // Create a global index for all nodes
        // Maps each node name to a unique integer (0 to N-1) used for port assignment
        let globalNodeIndex =
            topology
            |> Map.toArray
            |> Array.mapi (fun i (nodeName, _) -> (nodeName, i))
            |> Map.ofArray

        // Build reverse topology: for each node, who connects to it
        // Original topology tells clients who to connect to, reverse topology tells servers which ports to open
        // Note: topology maps pod names to DNS names, so we need to extract pod names from DNS names
        let reverseTopology =
            topology
            |> Map.toArray
            |> Array.collect
                (fun (sourceName, targetPeerDnsNames) ->
                    targetPeerDnsNames
                    |> Array.map
                        (fun targetDns ->
                            let targetPodName = targetDns.Split('.').[0]
                            (targetPodName, sourceName)))
            |> Array.groupBy fst
            |> Array.map (fun (target, sources) -> (target, sources |> Array.map snd))
            |> Map.ofArray

        (topology, globalNodeIndex, reverseTopology, avgPeerCount)

    member private self.CreateBenchmarkStatefulSets
        (
            runId: string,
            topology: Map<string, string []>,
            globalNodeIndex: Map<string, int>,
            reverseTopology: Map<string, string []>,
            duration: int
        ) =
        let ns = self.NetworkCfg.NamespaceProperty
        let apiRateLimit = self.NetworkCfg.missionContext.apiRateLimit

        // Create headless service for DNS
        LogInfo "Creating headless service for benchmark StatefulSets..."
        let headlessService = BenchmarkDaemonSet.createBenchmarkHeadlessService self.NetworkCfg runId

        try
            ApiRateLimit.sleepUntilNextRateLimitedApiCallTime apiRateLimit

            let svc =
                self.Kube.CreateNamespacedService(body = headlessService, namespaceParameter = ns)

            LogInfo "Created headless service %s" svc.Metadata.Name
        with ex -> failwithf "Failed to create headless service: %s" ex.Message

        // Each pod gets a StatefulSet to connect to he DNS service above.
        LogInfo "Creating StatefulSets for benchmark nodes..."

        let statefulSetDeployments =
            self.NetworkCfg.MapAllPeers
                (fun coreSet nodeIndex ->
                    let nodeName = (self.NetworkCfg.PodName coreSet nodeIndex).StringName
                    let peers = Map.find nodeName topology

                    // Get the list of nodes that will connect to this node
                    let sourcePeers =
                        match Map.tryFind nodeName reverseTopology with
                        | Some sources -> sources
                        | None -> [||]

                    // Extract short name for StatefulSet
                    let parts = nodeName.Split('-')
                    let coreSetName = if parts.Length >= 3 then parts.[parts.Length - 2] else coreSet.name.StringName

                    // Create the StatefulSet
                    let statefulSet =
                        BenchmarkDaemonSet.createBenchmarkStatefulSet
                            self.NetworkCfg
                            runId
                            coreSetName
                            nodeName
                            peers
                            sourcePeers
                            globalNodeIndex
                            duration
                            coreSet
                            nodeIndex

                    try
                        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime apiRateLimit

                        let sts =
                            self.Kube.CreateNamespacedStatefulSet(body = statefulSet, namespaceParameter = ns)

                        LogInfo "Created StatefulSet %s for %s" sts.Metadata.Name nodeName
                        (nodeName, sts.Metadata.Name)
                    with ex -> failwithf "Failed to create StatefulSet for %s: %s" nodeName ex.Message)

        let successfulStatefulSets = statefulSetDeployments |> Map.ofArray
        let totalCreated = Map.count successfulStatefulSets
        LogInfo "All %d StatefulSets created successfully." totalCreated
        successfulStatefulSets

    member private self.WaitForBenchmarkPodsReady(statefulSets: Map<string, string>) =
        let ns = self.NetworkCfg.NamespaceProperty
        LogInfo "Waiting for pods to be ready..."

        statefulSets
        |> Map.iter
            (fun nodeName stsName ->
                let podName = sprintf "%s-0" stsName // StatefulSet pods have -0 suffix
                let mutable ready = false
                let mutable attempts = 0

                while not ready && attempts < 30 do
                    try
                        let pod = self.Kube.ReadNamespacedPod(name = podName, namespaceParameter = ns)

                        if pod.Status.Phase = "Running"
                           && pod.Status.ContainerStatuses <> null
                           && pod.Status.ContainerStatuses |> Seq.forall (fun cs -> cs.Ready) then
                            ready <- true
                            LogInfo "Pod %s is ready" podName
                        else
                            System.Threading.Thread.Sleep(2000)
                            attempts <- attempts + 1
                    with _ ->
                        System.Threading.Thread.Sleep(2000)
                        attempts <- attempts + 1

                if not ready then
                    LogWarn "Pod %s failed to become ready after %d attempts" podName attempts)

    member private self.CollectBenchmarkResults(runId: string, topology: Map<string, string []>, duration: int) =
        let ns = self.NetworkCfg.NamespaceProperty
        let testId = sprintf "benchmark-%s" (System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))

        LogInfo "Collecting benchmark results from pods..."

        // Get all benchmark pods for this specific run
        let labelSelector = "app=network-benchmark"

        let podList =
            self.Kube.ListNamespacedPod(namespaceParameter = ns, labelSelector = labelSelector)

        // Check for failed pods
        let failedPods =
            podList.Items
            |> Seq.filter (fun pod -> pod.Metadata.Name.StartsWith(sprintf "%s-benchmark-" runId))
            |> Seq.filter
                (fun pod ->
                    match pod.Status.ContainerStatuses with
                    | null -> false
                    | statuses ->
                        statuses
                        |> Seq.exists
                            (fun cs ->
                                cs.Name = "client"
                                && cs.State.Terminated <> null
                                && cs.State.Terminated.ExitCode <> 0))
            |> Seq.toList

        if not (List.isEmpty failedPods) then
            LogError "The following benchmark pods failed with non-zero exit codes:"

            for pod in failedPods do
                let clientStatus = pod.Status.ContainerStatuses |> Seq.tryFind (fun cs -> cs.Name = "client")

                match clientStatus with
                | Some cs when cs.State.Terminated <> null ->
                    LogError
                        "  - %s: exit code %d (reason: %s)"
                        pod.Metadata.Name
                        cs.State.Terminated.ExitCode
                        (if cs.State.Terminated.Reason <> null then
                             cs.State.Terminated.Reason
                         else
                             "unknown")
                | _ -> LogError "  - %s: unknown failure" pod.Metadata.Name

            failwithf "Benchmark run aborted: %d pods failed with non-zero exit codes" (List.length failedPods)

        // Collect data from each pod
        let podDataList =
            podList.Items
            |> Seq.filter (fun pod -> pod.Metadata.Name.StartsWith(sprintf "%s-benchmark-" runId))
            |> Seq.filter
                (fun pod ->
                    match pod.Status.ContainerStatuses with
                    | null -> false
                    | statuses ->
                        match statuses |> Seq.tryFind (fun cs -> cs.Name = "client") with
                        | Some cs -> cs.State.Running <> null
                        | None -> false)
            |> Seq.choose
                (fun pod ->
                    let nodeName = BenchmarkDaemonSet.extractNodeNameFromBenchmarkPod pod.Metadata.Name topology

                    // Get logs from the client container
                    let logs =
                        let logStream =
                            self.Kube.ReadNamespacedPodLog(
                                name = pod.Metadata.Name,
                                namespaceParameter = ns,
                                container = "client"
                            )

                        use reader = new System.IO.StreamReader(logStream)
                        reader.ReadToEnd()

                    // Check if tests completed successfully
                    let testsSucceeded =
                        let processInfo = System.Diagnostics.ProcessStartInfo()
                        processInfo.FileName <- "kubectl"
                        processInfo.WorkingDirectory <- "/"

                        processInfo.Arguments <-
                            sprintf
                                "exec %s -n %s -c client -- sh -c \"cat /results/exit_code 2>/dev/null\""
                                pod.Metadata.Name
                                ns

                        processInfo.UseShellExecute <- false
                        processInfo.RedirectStandardOutput <- true
                        processInfo.RedirectStandardError <- true

                        use proc = System.Diagnostics.Process.Start(processInfo)
                        let output = proc.StandardOutput.ReadToEnd().Trim()
                        let stderr = proc.StandardError.ReadToEnd()
                        proc.WaitForExit()

                        if proc.ExitCode <> 0 then
                            LogError
                                "Pod %s: Cannot read exit_code file (kubectl exit code %d): %s"
                                pod.Metadata.Name
                                proc.ExitCode
                                stderr

                            false
                        else if output = "" then
                            LogError "Pod %s: exit_code file is empty or doesn't exist yet" pod.Metadata.Name
                            false
                        else if output = "0" then
                            true
                        else
                            LogError "Pod %s: Tests failed with exit_code=%s" pod.Metadata.Name output
                            false

                    if not testsSucceeded then
                        failwithf "Pod %s: Tests failed or incomplete, aborting benchmark" pod.Metadata.Name
                    else
                        // Get the raw iperf3 JSON files from the pod
                        let kubectlOutput =
                            let processInfo = System.Diagnostics.ProcessStartInfo()
                            processInfo.FileName <- "kubectl"
                            processInfo.WorkingDirectory <- "/"

                            processInfo.Arguments <-
                                sprintf
                                    "exec %s -n %s -c client -- sh -c \"cat /results/*.json 2>/dev/null\""
                                    pod.Metadata.Name
                                    ns

                            processInfo.UseShellExecute <- false
                            processInfo.RedirectStandardOutput <- true
                            processInfo.RedirectStandardError <- true

                            use proc = System.Diagnostics.Process.Start(processInfo)
                            let output = proc.StandardOutput.ReadToEnd()
                            let stderr = proc.StandardError.ReadToEnd()
                            proc.WaitForExit()

                            if proc.ExitCode <> 0 then
                                if stderr.Contains("container not found") then
                                    failwithf
                                        "Cannot retrieve results from terminated container in pod %s"
                                        pod.Metadata.Name
                                else
                                    failwithf
                                        "Failed to retrieve results from pod %s (kubectl exec failed): %s"
                                        pod.Metadata.Name
                                        stderr
                            else if String.IsNullOrWhiteSpace(output) then
                                failwithf "No benchmark results found in pod %s (empty output)" pod.Metadata.Name
                            else
                                Some output

                        match kubectlOutput with
                        | None -> failwith "Failed to retrieve kubectl output"
                        | Some output ->
                            Some
                                {| name = pod.Metadata.Name
                                   node_name = nodeName
                                   logs = logs
                                   kubectl_output = output |})
            |> Array.ofSeq

        (testId, podDataList)

    // Helper function to process results with Python script
    member private self.ProcessBenchmarkResults
        (
            testId: string,
            podDataList: _ [],
            topology: Map<string, string []>,
            duration: int
        ) =
        LogInfo "Processing benchmark results..."

        let topologyData = topology |> Map.map (fun nodeName peers -> peers)

        let inputData =
            {| test_id = testId
               pods = podDataList
               topology = topologyData
               network_delay_enabled = self.NetworkCfg.NeedNetworkDelayScript
               duration_seconds = duration |}

        let jsonInput = Newtonsoft.Json.JsonConvert.SerializeObject(inputData)

        // Call Python script to process results
        let pythonScriptPath = GetScriptPath "parse_benchmark_results.py"

        let processInfo = System.Diagnostics.ProcessStartInfo()
        processInfo.FileName <- "python3"
        processInfo.Arguments <- pythonScriptPath
        processInfo.UseShellExecute <- false
        processInfo.RedirectStandardInput <- true
        processInfo.RedirectStandardOutput <- true
        processInfo.RedirectStandardError <- true

        try
            use proc = System.Diagnostics.Process.Start(processInfo)
            proc.StandardInput.Write(jsonInput)
            proc.StandardInput.Close()

            let output = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()

            if proc.ExitCode <> 0 then
                LogError "Python script failed: %s" stderr
                LogInfo "Falling back to basic results display"
                LogInfo "Collected data from %d pods" (Array.length podDataList)
            else
                LogInfo "%s" output

                if stderr.Contains("RESULTS_FILE:") then
                    let startIdx = stderr.IndexOf("RESULTS_FILE:") + 13
                    let resultsFile = stderr.Substring(startIdx).Trim()
                    LogInfo "Results saved to %s" resultsFile
        with ex -> failwithf "Failed to run Python script: %s" ex.Message

    member private self.CleanupBenchmarkResources(runId: string, statefulSets: Map<string, string>) =
        let ns = self.NetworkCfg.NamespaceProperty
        LogInfo "Cleaning up benchmark resources..."

        // Delete all StatefulSets
        statefulSets
        |> Map.iter
            (fun nodeName stsName ->
                try
                    self.Kube.DeleteNamespacedStatefulSet(name = stsName, namespaceParameter = ns)
                    |> ignore

                    LogInfo "Deleted StatefulSet %s" stsName
                with ex -> LogWarn "Failed to delete StatefulSet %s: %s" stsName ex.Message)

        // Delete the headless service
        try
            let serviceName = sprintf "%s-benchmark" runId

            self.Kube.DeleteNamespacedService(name = serviceName, namespaceParameter = ns)
            |> ignore

            LogInfo "Deleted headless service %s" serviceName
        with ex -> LogWarn "Failed to delete headless service: %s" ex.Message

    member self.RunP2PNetworkBenchmark() : unit =
        assert (self.NetworkCfg.missionContext.benchmarkInfrastructure.IsSome)

        LogInfo "==============================================="
        LogInfo "Starting P2P Network Infrastructure Benchmark"
        LogInfo "==============================================="

        let runId = sprintf "ssc-%xz" (System.Random().Next(0x10000))
        LogInfo "Using run ID: %s" runId

        // Setup network topology
        let (topology, globalNodeIndex, reverseTopology, avgPeerCount) = self.SetupBenchmarkTopology()

        let duration = self.NetworkCfg.missionContext.benchmarkDurationSeconds.Value

        // Create and deploy benchmark StatefulSets
        let successfulStatefulSets =
            self.CreateBenchmarkStatefulSets(runId, topology, globalNodeIndex, reverseTopology, duration)

        self.WaitForBenchmarkPodsReady(successfulStatefulSets)
        LogInfo "Benchmark tests running for %d seconds..." duration

        // Wait for tests to complete, plus some time for writing results
        let waitTime = duration + 20
        LogInfo "Waiting %d seconds for tests to complete..." waitTime
        System.Threading.Thread.Sleep(waitTime * 1000)

        // Collect benchmark results from pods
        let (testId, podDataList) = self.CollectBenchmarkResults(runId, topology, duration)
        self.ProcessBenchmarkResults(testId, podDataList, topology, duration)

        // Cleanup benchmark resources
        self.CleanupBenchmarkResources(runId, successfulStatefulSets)
        LogInfo "Network benchmark complete!"
