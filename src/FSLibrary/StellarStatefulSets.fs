// Copyright 2021 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarStatefulSets

open k8s
open k8s.Models
open Logging
open StellarFormation
open StellarDataDump
open StellarCoreSet
open StellarKubeSpecs
open StellarCorePeer
open StellarCoreHTTP
open StellarTransaction
open StellarNetworkDelays
open BenchmarkDaemonSet
open ApiRateLimit
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

type StellarFormation with

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

        self.LaunchLogTailingTasksForCoreSet(self.GetCoreSetForStatefulSet ss)

        LogInfo "All replicas on %s/%s ready" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasOnAllSetsReady() =
        if not self.StatefulSets.IsEmpty then
            LogInfo "Waiting for replicas on %s" (self.ToString())

            for ss in self.StatefulSets do
                self.WaitForAllReplicasReady ss

            LogInfo "All replicas on %s ready" (self.ToString())

    member self.WithLive name (live: bool) =
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
        self.WaitForAllReplicasReady ss

    member self.Start name = self.WithLive name true

    member self.Stop name = self.WithLive name false

    member self.WaitUntilReady() = self.NetworkCfg.EachPeer(fun p -> p.WaitUntilReady())

    member self.WaitUntilAllLiveSynced() = self.NetworkCfg.EachPeer(fun p -> p.WaitUntilSynced())

    member self.WaitUntilSynced(coreSetList: CoreSet list) =
        coreSetList
        |> List.iter
            (fun coreSet ->
                if coreSet.CurrentCount = 0 then
                    failwith ("Coreset " + coreSet.name.StringName + " is not live"))

        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.WaitUntilSynced())

    member self.WaitUntilConnected(coreSetList: CoreSet list) =
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.WaitUntilConnected)

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

    member self.DeployUpgradeEntriesAndArm
        (coreSetList: CoreSet list)
        (loadGen: LoadGen)
        (upgradeTime: System.DateTime)
        =
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        let resStr = peer.GenerateLoad loadGen

        let contractKey = Loadgen.Parse(resStr).ConfigUpgradeSetKey

        LogInfo "Loadgen: %s" resStr
        peer.WaitForLoadGenComplete loadGen

        // Arm upgrades on each peer in the core set
        self.NetworkCfg.EachPeerInSets
            (List.toArray coreSetList)
            (fun peer -> peer.UpgradeNetworkSetting contractKey upgradeTime)

    member self.clearMetrics(coreSets: CoreSet list) =
        self.NetworkCfg.EachPeerInSets(coreSets |> List.toArray) (fun peer -> peer.ClearMetrics())

    // This is similar to RunLoadgen but runs a 1/N fractional portion of a
    // given LoadGen on node 0 of each of N CoreSets.
    member self.RunMultiLoadgen (coreSets: CoreSet list) (fullLoadGen: LoadGen) =
        let n = List.length coreSets

        let fractionalLoadGen (i: int) : LoadGen =
            let getFraction attr = if i + 1 = n then (attr / n + attr % n) else attr / n

            { fullLoadGen with
                  accounts = fullLoadGen.accounts / n
                  txs = fullLoadGen.txs / n
                  spikesize = getFraction fullLoadGen.spikesize
                  txrate = getFraction fullLoadGen.txrate }

        let loadGenPeers = List.map (fun cs -> self.NetworkCfg.GetPeer cs 0) coreSets

        for (i, peer) in (List.indexed loadGenPeers) do
            let loadGen = fractionalLoadGen i
            let offset = loadGen.accounts * i
            let peerSpecificLoadgen = { loadGen with offset = offset }
            LogInfo "Loadgen: %s with offset %d" (peer.GenerateLoad peerSpecificLoadgen) offset

        while List.exists
                  (fun (peer: Peer) -> not (peer.IsLoadGenComplete() = Success || peer.IsLoadGenComplete() = Failure))
                  loadGenPeers do
            Thread.Sleep(millisecondsTimeout = 3000)

            for (i, peer) in (List.indexed loadGenPeers) do
                peer.LogLoadGenProgressTowards(fractionalLoadGen i)

            // Check if any loadGen has failed
            if List.exists (fun (peer: Peer) -> peer.IsLoadGenComplete() = Failure) loadGenPeers then
                // Stop all runs
                for peer in loadGenPeers do
                    LogInfo "%s  loadgen: %s" (peer.ShortName.ToString()) (peer.StopLoadGen())

                failwith "Loadgen failed!"

        // Final check after the loop completes
        if List.exists (fun (peer: Peer) -> peer.IsLoadGenComplete() = Failure) loadGenPeers then
            failwith "Loadgen failed!"

    // Deploys TCP tuning DaemonSets to configure node-level network settings.
    //
    // How it works:
    // - DaemonSets run privileged containers on every node in the cluster
    // - Containers modify kernel TCP parameters via sysctl commands
    // - Settings persist at the node level even after DaemonSet deletion
    // - All pods scheduled on tuned nodes benefit from the optimized settings
    //
    // After the run finishes, note that the settings remain permanently changed
    // on those nodes, even for follow up runs. To account for this, we always
    // set TCP settings at the start of the run, either to our optimized settings
    // or Linux defaults.
    //
    // Scripts are stored in a ConfigMap and mounted into DaemonSet pods at /scripts/
    member self.DeployTcpTuningDaemonSet() : unit =
        let ns = self.NetworkCfg.NamespaceProperty

        LogInfo "Creating TCP scripts ConfigMap..."
        let configMap = BenchmarkDaemonSet.createTcpScriptsConfigMap self.NetworkCfg

        self.Kube.CreateNamespacedConfigMap(body = configMap, namespaceParameter = ns)
        |> ignore

        self.NamespaceContent.Add(configMap)

        // Determine which DaemonSet to deploy based on TCP tuning flag
        let (daemonSetName, daemonSet, actionMsg) =
            if not self.NetworkCfg.missionContext.enableTcpTuning then
                LogInfo "Resetting TCP settings to defaults"
                ("tcp-reset", BenchmarkDaemonSet.createTcpResetDaemonSet self.NetworkCfg, "reset")
            else
                LogInfo "Setting TCP settings for network performance"
                ("tcp-tuning", BenchmarkDaemonSet.createTcpTuningDaemonSet self.NetworkCfg, "applied")

        try
            // Create and deploy the DaemonSet
            let ds = self.Kube.CreateNamespacedDaemonSet(body = daemonSet, namespaceParameter = ns)
            LogInfo "Created %s DaemonSet, waiting for settings to be %s..." daemonSetName actionMsg

            // Wait for DaemonSet to be ready on all nodes
            let mutable allReady = false
            let mutable attempts = 0
            let maxAttempts = if daemonSetName = "tcp-reset" then 20 else 30

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
                LogWarn "%s DaemonSet did not complete on all nodes within timeout" daemonSetName
            else if daemonSetName = "tcp-tuning" then
                // Give a bit more time for tuning settings to take effect
                System.Threading.Thread.Sleep(3000)

            // Delete the DaemonSet - settings will persist on nodes
            try
                self.Kube.DeleteNamespacedDaemonSet(name = daemonSetName, namespaceParameter = ns)
                |> ignore
            with ex ->
                LogWarn "Failed to delete %s DaemonSet: %s" daemonSetName ex.Message
                // Track it for cleanup if deletion failed
                if daemonSetName = "tcp-tuning" then self.NamespaceContent.Add(ds)
        with ex -> LogWarn "Failed to deploy %s DaemonSet: %s. TCP settings may be invalid" daemonSetName ex.Message

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
    member self.RunP2PNetworkBenchmark() : unit =
        assert (self.NetworkCfg.missionContext.benchmarkInfrastructure.IsSome)

        LogInfo "==============================================="
        LogInfo "Starting P2P Network Infrastructure Benchmark"
        LogInfo "==============================================="

        let ns = self.NetworkCfg.NamespaceProperty
        let apiRateLimit = self.NetworkCfg.missionContext.apiRateLimit
        let runId = sprintf "ssc-%xz" (System.Random().Next(0x10000))

        // Extract peer topology
        let topology = extractPeerTopology self.NetworkCfg
        let avgPeerCount = getAveragePeerCount topology

        LogInfo "Network topology: %d nodes, average %.1f peers per node" (Map.count topology) avgPeerCount
        LogInfo "Using run ID: %s" runId

        // Deploy iperf3 servers for each node
        LogInfo "Deploying iperf3 servers using StatefulSets..."

        // Create a global index for all nodes
        // Maps each node name to a unique integer (0 to N-1) used for port assignment
        // Each client connects to peers using port = 5201 + source_node_index
        // Every connection has a unique port to make make collision avoidance easy
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
                            // Extract pod name from DNS name (e.g., "ssc-xxx-sts-bd-0" from "ssc-xxx-sts-bd-0.garand.svc.cluster.local")
                            let targetPodName = targetDns.Split('.').[0]
                            (targetPodName, sourceName)))
            |> Array.groupBy fst
            |> Array.map (fun (target, sources) -> (target, sources |> Array.map snd))
            |> Map.ofArray

        let duration = self.NetworkCfg.missionContext.benchmarkDurationSeconds.Value

        // We need to mirror the stellar-core topology exactly here for accurate testing.
        // First, spin up a single headless service for DNS.
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

        // All StatefulSets created successfully (or we would have failed above)
        let successfulStatefulSets = statefulSetDeployments |> Map.ofArray
        let totalCreated = Map.count successfulStatefulSets

        LogInfo "All %d StatefulSets created successfully. Waiting for pods to be ready..." totalCreated

        // Wait for pods to be ready.
        successfulStatefulSets
        |> Map.iter
            (fun nodeName stsName ->
                let shortName = nodeName

                let podName = sprintf "%s-0" stsName // StatefulSet pods have -0 suffix

                let mutable ready = false
                let mutable attempts = 0

                while not ready && attempts < 30 do
                    try
                        let pod = self.Kube.ReadNamespacedPod(name = podName, namespaceParameter = ns)
                        // Check if all containers are ready
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

        // The benchmark tests are already running in the client containers
        LogInfo "Benchmark tests running for %d seconds..." duration

        // Wait for tests to complete, plus some time for writing results.
        let waitTime = duration + 20
        LogInfo "Waiting %d seconds for tests to complete..." waitTime
        System.Threading.Thread.Sleep(waitTime * 1000)

        // Collect results while pods are still running
        LogInfo "Collecting benchmark results from pods..."

        let testId = sprintf "benchmark-%s" (System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))

        // Get all benchmark pods for this specific run. Make sure they all succeeded with non-zero exit codes
        // and are still running so we can pull data from them.
        let labelSelector = "app=network-benchmark"

        let podList =
            self.Kube.ListNamespacedPod(namespaceParameter = ns, labelSelector = labelSelector)

        let podDataList =
            // First, report on failed pods
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

            podList.Items
            |> Seq.filter
                (fun pod ->
                    // Only collect from pods belonging to this run
                    let belongsToRun = pod.Metadata.Name.StartsWith(sprintf "%s-benchmark-" runId)

                    if not belongsToRun then
                        false
                    else
                        let clientContainerReady =
                            match pod.Status.ContainerStatuses with
                            | null ->
                                LogDebug "  Pod %s: No container statuses available" pod.Metadata.Name
                                false
                            | statuses ->
                                match statuses |> Seq.tryFind (fun cs -> cs.Name = "client") with
                                | None ->
                                    LogDebug "  Pod %s: No 'client' container found" pod.Metadata.Name
                                    false
                                | Some cs ->
                                    // Since we use sleep infinity, container should be running
                                    let isRunning = cs.State.Running <> null
                                    let isReady = cs.Ready
                                    isRunning // Container must be running

                        clientContainerReady)
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

                    // First check if the tests completed successfully by reading the exit code
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
                        // Use kubectl exec to get the raw iperf3 JSON files from the pod
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
                                // Check if it's the "container not found" error, which means the container already terminated
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
                                Some output // Return raw kubectl output

                        match kubectlOutput with
                        | None -> failwith "Failed to retrieve kubectl output"
                        | Some output ->
                            // Return pod data for Python processing
                            Some
                                {| name = pod.Metadata.Name
                                   node_name = nodeName
                                   logs = logs
                                   kubectl_output = output |})
            |> Array.ofSeq

        // Now process the results using Python script
        LogInfo "Processing benchmark results..."

        // Prepare data for Python script
        let topologyData = topology |> Map.map (fun nodeName peers -> peers)

        let inputData =
            {| test_id = testId
               pods = podDataList
               topology = topologyData
               network_delay_enabled = self.NetworkCfg.NeedNetworkDelayScript
               duration_seconds = duration |}

        let jsonInput = Newtonsoft.Json.JsonConvert.SerializeObject(inputData)

        // Call Python script to process results from stdin
        let pythonScriptPath =
            System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "scripts", "parse_benchmark_results.py")

        let processInfo = System.Diagnostics.ProcessStartInfo()
        processInfo.FileName <- "python3"
        processInfo.Arguments <- pythonScriptPath
        processInfo.UseShellExecute <- false
        processInfo.RedirectStandardInput <- true
        processInfo.RedirectStandardOutput <- true
        processInfo.RedirectStandardError <- true

        try
            use proc = System.Diagnostics.Process.Start(processInfo)
            // Write JSON to stdin
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
                // Display the formatted results
                LogInfo "%s" output

                // Extract the filename from stderr if present
                if stderr.Contains("RESULTS_FILE:") then
                    let startIdx = stderr.IndexOf("RESULTS_FILE:") + 13
                    let resultsFile = stderr.Substring(startIdx).Trim()
                    LogInfo "Results saved to %s" resultsFile
        with ex -> failwithf "Failed to run Python script: %s" ex.Message

        LogInfo "Cleaning up benchmark resources..."

        // Delete all StatefulSets
        successfulStatefulSets
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

        LogInfo "Network benchmark complete!"
