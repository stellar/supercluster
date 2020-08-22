// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarFormation

open k8s
open k8s.Models

open Logging
open StellarNetworkCfg
open StellarCoreSet
open StellarKubeSpecs
open StellarCorePeer
open StellarCoreHTTP
open StellarTransaction
open StellarNamespaceContent
open System


// Typically you want to instantiate one of these per mission / test scenario,
// and run methods on it. Unlike most other types in this library it's a class
// type with a fair amount of internal mutable state, and implements
// IDisposable: it tracks the `kube`-owned objects that it allocates inside
// its `namespaceContent` member, and deletes them when it is disposed.
type StellarFormation(networkCfg: NetworkCfg,
                      kube: Kubernetes,
                      statefulSets: V1StatefulSet list,
                      namespaceContent: NamespaceContent,
                      probeTimeout: int) =

    let mutable networkCfg = networkCfg
    let kube = kube
    let mutable statefulSets = statefulSets
    let mutable keepData = false
    let namespaceContent = namespaceContent
    let probeTimeout = probeTimeout
    let mutable disposed = false
    let mutable jobNumber = 0

    member self.sleepUntilNextRateLimitedApiCallTime () =
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime(networkCfg.apiRateLimitRequestsPerSecond)

    member self.NamespaceContent =
        namespaceContent

    member self.NextJobNum : int =
        jobNumber <- jobNumber + 1
        jobNumber

    member self.AllJobNums : int list =
        [ 1 .. jobNumber ]

    member self.Kube = kube
    member self.NetworkCfg = networkCfg
    member self.SetNetworkCfg (n:NetworkCfg) =
        networkCfg <- n
    member self.CleanNamespace() =
        LogInfo "Cleaning all resources from namespace '%s'"
            networkCfg.NamespaceProperty
        namespaceContent.AddAll()
        namespaceContent.Cleanup()

    member self.ForceCleanup() =
        LogInfo "Cleaning run '%s' resources from namespace '%s'"
            (networkCfg.networkNonce.ToString())
            networkCfg.NamespaceProperty
        namespaceContent.Cleanup()

    member self.Cleanup(disposing:bool) =
        if not disposed then
            disposed <- true
            if disposing then
                if keepData
                then
                    LogInfo "Disposing formation, keeping namespace '%s' for debug"
                        networkCfg.NamespaceProperty
                else
                    self.ForceCleanup()

    member self.KeepData() =
        keepData <- true

    // implementation of IDisposable
    interface System.IDisposable with
        member self.Dispose() =
            self.Cleanup(true)
            System.GC.SuppressFinalize(self)

    // override of finalizer
    override self.Finalize() =
        self.Cleanup(false)

    override self.ToString() : string =
        let name = networkCfg.ServiceName
        let ns = networkCfg.NamespaceProperty
        sprintf "%s/%s" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasReady (ss : V1StatefulSet) =
        let name = ss.Metadata.Name
        let ns = ss.Metadata.NamespaceProperty
        use event = new System.Threading.ManualResetEventSlim(false)

        // This pattern of a recursive handler-install routine that reinstalls
        // itself when `onClosed` fires is necessary because watches
        // automatically time out after 100 seconds and the connection closes.
        let rec installHandler() =
            LogInfo "Waiting for replicas on %s/%s" ns name
            let handler (ety:WatchEventType) (ss:V1StatefulSet) =
                LogInfo "Saw event for statefulset %s: %s" name (ety.ToString())
                if not event.IsSet
                then
                    let n = ss.Status.ReadyReplicas.GetValueOrDefault(0)
                    let k = ss.Spec.Replicas.GetValueOrDefault(0)
                    LogInfo "StatefulSet %s/%s: %d/%d replicas ready" ns name n k;
                    if n = k then event.Set()
            let action = System.Action<WatchEventType, V1StatefulSet>(handler)
            let reinstall = System.Action(installHandler)
            if not event.IsSet
            then
                self.sleepUntilNextRateLimitedApiCallTime()
                kube.WatchNamespacedStatefulSetAsync(name = name,
                                                    ``namespace`` = ns,
                                                    onEvent = action,
                                                    onClosed = reinstall) |> ignore
        installHandler()
        event.Wait() |> ignore
        LogInfo "All replicas on %s/%s ready" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasOnAllSetsReady() =
        if not statefulSets.IsEmpty
        then
            LogInfo "Waiting for replicas on %s" (self.ToString())
            for ss in statefulSets do
                self.WaitForAllReplicasReady ss
            LogInfo "All replicas on %s ready" (self.ToString())

    member self.WithLive name (live: bool) =
        networkCfg <- networkCfg.WithLive name live
        let coreSet = networkCfg.FindCoreSet name
        let stsName = networkCfg.StatefulSetName coreSet
        self.sleepUntilNextRateLimitedApiCallTime()
        let ss = kube.ReplaceNamespacedStatefulSet(
                   body = networkCfg.ToStatefulSet coreSet probeTimeout,
                   name = stsName.StringName,
                   namespaceParameter = networkCfg.NamespaceProperty)
        let newSets = statefulSets |> List.filter (fun x -> x.Metadata.Name <> stsName.StringName)
        statefulSets <- ss :: newSets
        self.WaitForAllReplicasReady ss

    member self.Start name =
        self.WithLive name true

    member self.Stop name =
        self.WithLive name false

    member self.WaitUntilReady () =
        networkCfg.EachPeer (fun p -> p.WaitUntilReady())

    member self.AddPersistentVolumeClaim (pvc:V1PersistentVolumeClaim) : unit =
        let ns = networkCfg.NamespaceProperty
        self.sleepUntilNextRateLimitedApiCallTime()
        let claim = self.Kube.CreateNamespacedPersistentVolumeClaim(body = pvc,
                                                                    namespaceParameter = ns)
        namespaceContent.Add(claim)

    member self.WaitUntilSynced (coreSetList: CoreSet list) =
        networkCfg.EachPeerInSets (coreSetList |> Array.ofList) (fun p -> p.WaitUntilSynced())

    // When upgrading multiple nodes, configure upgrade time a bit ahead to ensure nodes have enough
    // of a buffer to set upgrades
    member self.UpgradeProtocol (coreSetList: CoreSet list) (version: int) =
        let upgradeTime = System.DateTime.UtcNow.AddSeconds(15.0)
        networkCfg.EachPeerInSets (coreSetList |> Array.ofList) (fun p -> p.UpgradeProtocol version upgradeTime)
        let peer = networkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForProtocol(version) |> ignore

    member self.UpgradeProtocolToLatest (coreSetList: CoreSet list) =
        let peer = networkCfg.GetPeer coreSetList.[0] 0
        let latest = peer.GetSupportedProtocolVersion()
        self.UpgradeProtocol coreSetList latest

    member self.UpgradeMaxTxSize (coreSetList: CoreSet list) (maxTxSize: int) =
        let upgradeTime = System.DateTime.UtcNow.AddSeconds(15.0)
        networkCfg.EachPeerInSets (coreSetList |> Array.ofList) (fun p -> p.UpgradeMaxTxSize maxTxSize upgradeTime)
        let peer = networkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForMaxTxSetSize maxTxSize |> ignore

    member self.ReportStatus () =
        ReportAllPeerStatus networkCfg

    member self.CreateAccount (coreSet: CoreSet) (u: Username) =
        let peer = networkCfg.GetPeer coreSet 0
        let tx = peer.TxCreateAccount u
        LogInfo "creating account for %O on %O" u self
        peer.SubmitSignedTransaction tx |> ignore
        peer.WaitForNextLedger() |> ignore
        let acc = peer.GetAccount(u)
        LogInfo "created account for %O on %O with seq %d, balance %d"
            u self acc.SequenceNumber
            (peer.GetTestAccBalance (u.ToString()))

    member self.Pay (coreSet: CoreSet) (src: Username) (dst: Username) =
        let peer = networkCfg.GetPeer coreSet 0
        let tx = peer.TxPayment src dst
        LogInfo "paying from account %O to %O on %O" src dst self
        peer.SubmitSignedTransaction tx |> ignore
        peer.WaitForNextLedger() |> ignore
        LogInfo "sent payment from %O (%O) to %O (%O) on %O"
            src (peer.GetSeqAndBalance src)
            dst (peer.GetSeqAndBalance dst)
            self

    member self.CheckNoErrorsAndPairwiseConsistency () =
        let peer = networkCfg.GetPeer networkCfg.CoreSetList.[0] 0
        networkCfg.EachPeer
            begin
                fun p ->
                    p.CheckNoErrorMetrics(includeTxInternalErrors=false)
                    p.CheckConsistencyWith peer
            end

    member self.CheckUsesLatestProtocolVersion () =
        networkCfg.EachPeer
            begin
                fun p ->
                    p.CheckUsesLatestProtocolVersion()
            end

    member self.RunLoadgen (coreSet: CoreSet) (loadGen: LoadGen) =
        let peer = networkCfg.GetPeer coreSet 0
        LogInfo "Loadgen: %s" (peer.GenerateLoad loadGen)
        peer.WaitForLoadGenComplete loadGen

    member self.RunLoadgenAndCheckNoErrors (coreSet: CoreSet) =
        let peer = networkCfg.GetPeer coreSet 0
        let loadGen = { DefaultAccountCreationLoadGen with accounts = 10000 }
        LogInfo "Loadgen: %s" (peer.GenerateLoad loadGen)
        peer.WaitForLoadGenComplete loadGen
        self.CheckNoErrorsAndPairwiseConsistency()
