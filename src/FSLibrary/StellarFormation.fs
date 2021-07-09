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

// The default ToString on Corev1Event just says "k8s.models.Corev1Event", not
// very useful. We dress it up a bit here.
type Corev1Event with
    member self.ToString(objType: string, objName: string) =
        sprintf
            "Event on %s Name=%s - Type=%s, Reason=%s, Message=%s"
            objType
            objName
            self.Type
            self.Reason
            self.Message

// Typically you want to instantiate one of these per mission / test scenario,
// and run methods on it. Unlike most other types in this library it's a class
// type with a fair amount of internal mutable state, and implements
// IDisposable: it tracks the `kube`-owned objects that it allocates inside
// its `namespaceContent` member, and deletes them when it is disposed.
type StellarFormation
    (
        networkCfg: NetworkCfg,
        kube: Kubernetes,
        statefulSets: V1StatefulSet list,
        namespaceContent: NamespaceContent
    ) =

    let mutable networkCfg = networkCfg
    let kube = kube
    let mutable statefulSets = statefulSets
    let mutable keepData = false
    let namespaceContent = namespaceContent
    let mutable disposed = false
    let mutable jobNumber = 0

    member self.sleepUntilNextRateLimitedApiCallTime() =
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (networkCfg.missionContext.apiRateLimit)

    member self.NamespaceContent = namespaceContent

    member self.NextJobNum : int =
        jobNumber <- jobNumber + 1
        jobNumber

    member self.AllJobNums : int list = [ 1 .. jobNumber ]

    member self.Kube = kube
    member self.NetworkCfg = networkCfg
    member self.SetNetworkCfg(n: NetworkCfg) = networkCfg <- n

    member self.CleanNamespace() =
        LogInfo "Cleaning all resources from namespace '%s'" networkCfg.NamespaceProperty
        namespaceContent.AddAll()
        namespaceContent.Cleanup()

    member self.ForceCleanup() =
        LogInfo
            "Cleaning run '%s' resources from namespace '%s'"
            (networkCfg.networkNonce.ToString())
            networkCfg.NamespaceProperty

        namespaceContent.Cleanup()

    member self.Cleanup(disposing: bool) =
        if not disposed then
            disposed <- true

            if disposing then
                if keepData then
                    LogInfo "Disposing formation, keeping namespace '%s' for debug" networkCfg.NamespaceProperty
                else
                    self.ForceCleanup()

    member self.KeepData() = keepData <- true

    // implementation of IDisposable
    interface System.IDisposable with
        member self.Dispose() =
            self.Cleanup(true)
            System.GC.SuppressFinalize(self)

    // override of finalizer
    override self.Finalize() = self.Cleanup(false)

    override self.ToString() : string =
        let name = networkCfg.ServiceName
        let ns = networkCfg.NamespaceProperty
        sprintf "%s/%s" ns name

    member self.GetEventsForObject(name: string) =
        let ns = self.NetworkCfg.NamespaceProperty
        let fs = sprintf "involvedObject.name=%s" name
        self.sleepUntilNextRateLimitedApiCallTime ()
        self.Kube.ListNamespacedEvent(namespaceParameter = ns, fieldSelector = fs)

    member self.GetAbnormalEventsForObject(name: string) =
        let events = List.ofSeq (self.GetEventsForObject(name).Items)

        List.filter
            (fun (ev: Corev1Event) ->
                ev.Type <> "Normal"
                && ev.Reason <> "DNSConfigForming"
                && ev.Reason <> "FailedMount")
            events

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasReady(ss: V1StatefulSet) =
        let name = ss.Metadata.Name
        let ns = ss.Metadata.NamespaceProperty
        let mutable forbiddenEvent = None
        use event = new System.Threading.ManualResetEventSlim(false)

        // This pattern of a recursive handler-install routine that reinstalls
        // itself when `onClosed` fires is necessary because watches
        // automatically time out after 100 seconds and the connection closes.
        let rec installHandler () =
            LogInfo "Waiting for replicas on %s/%s" ns name

            let handler (ety: WatchEventType) (ss: V1StatefulSet) =
                LogInfo "Saw event for statefulset %s: %s" name (ety.ToString())

                if not event.IsSet then
                    // First we check to see if we've been woken up because a FailedCreate + forbidden
                    // event occurred; this happens typically when we exceed quotas on a cluster or
                    // some other policy reason.

                    for ev in self.GetEventsForObject(name).Items do
                        if ev.Reason = "FailedCreate" && ev.Message.Contains("forbidden") then
                            // If so, we record the causal event and wake up the waiter.
                            forbiddenEvent <- Some(ev)
                            event.Set()
                    // Assuming we weren't failed, we look to see how the sts is doing in terms
                    // of creating the number of ready replicas we asked for.
                    let n = ss.Status.ReadyReplicas.GetValueOrDefault(0)
                    let k = ss.Spec.Replicas.GetValueOrDefault(0)
                    LogInfo "StatefulSet %s/%s: %d/%d replicas ready" ns name n k
                    if n = k then event.Set()

            let action = System.Action<WatchEventType, V1StatefulSet>(handler)
            let reinstall = System.Action(installHandler)

            if not event.IsSet then
                self.sleepUntilNextRateLimitedApiCallTime ()

                kube.WatchNamespacedStatefulSetAsync(
                    name = name,
                    ``namespace`` = ns,
                    onEvent = action,
                    onClosed = reinstall
                )
                |> ignore

        installHandler ()
        event.Wait() |> ignore

        match forbiddenEvent with
        | None -> ()
        | Some (ev) -> failwith (sprintf "Statefulset %s pod creation forbidden: %s" name ev.Message)

        LogInfo "All replicas on %s/%s ready" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasOnAllSetsReady() =
        if not statefulSets.IsEmpty then
            LogInfo "Waiting for replicas on %s" (self.ToString())

            for ss in statefulSets do
                self.WaitForAllReplicasReady ss

            LogInfo "All replicas on %s ready" (self.ToString())

    member self.WithLive name (live: bool) =
        networkCfg <- networkCfg.WithLive name live
        let coreSet = networkCfg.FindCoreSet name
        let stsName = networkCfg.StatefulSetName coreSet
        self.sleepUntilNextRateLimitedApiCallTime ()

        let ss =
            kube.ReplaceNamespacedStatefulSet(
                body = networkCfg.ToStatefulSet coreSet,
                name = stsName.StringName,
                namespaceParameter = networkCfg.NamespaceProperty
            )

        let newSets = statefulSets |> List.filter (fun x -> x.Metadata.Name <> stsName.StringName)
        statefulSets <- ss :: newSets
        self.WaitForAllReplicasReady ss

    member self.Start name = self.WithLive name true

    member self.Stop name = self.WithLive name false

    member self.WaitUntilReady() = networkCfg.EachPeer(fun p -> p.WaitUntilReady())

    member self.WaitUntilAllLiveSynced() = networkCfg.EachPeer(fun p -> p.WaitUntilSynced())

    member self.WaitUntilSynced(coreSetList: CoreSet list) =
        coreSetList
        |> List.iter
            (fun coreSet ->
                if coreSet.CurrentCount = 0 then
                    failwith ("Coreset " + coreSet.name.StringName + " is not live"))

        networkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.WaitUntilSynced())

    member self.WaitUntilConnected(coreSetList: CoreSet list) =
        networkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.WaitUntilConnected)

    member self.EnsureAllNodesInSync(coreSetList: CoreSet list) =
        networkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.EnsureInSync)

    member self.ManualClose(coreSetList: CoreSet list) =
        networkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.ManualClose())

    // When upgrading multiple nodes, configure upgrade time a bit ahead to ensure nodes have enough
    // of a buffer to set upgrades
    member self.UpgradeProtocol (coreSetList: CoreSet list) (version: int) =
        let upgradeTime = System.DateTime.UtcNow.AddSeconds(15.0)
        networkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.UpgradeProtocol version upgradeTime)
        let peer = networkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForProtocol(version) |> ignore

    member self.UpgradeProtocolToLatest(coreSetList: CoreSet list) =
        let peer = networkCfg.GetPeer coreSetList.[0] 0
        let latest = peer.GetSupportedProtocolVersion()
        self.UpgradeProtocol coreSetList latest

    member self.UpgradeMaxTxSize (coreSetList: CoreSet list) (maxTxSize: int) =
        let upgradeTime = System.DateTime.UtcNow.AddSeconds(15.0)
        networkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.UpgradeMaxTxSize maxTxSize upgradeTime)
        let peer = networkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForMaxTxSetSize maxTxSize |> ignore

    member self.ReportStatus() = ReportAllPeerStatus networkCfg

    member self.CreateAccount (coreSet: CoreSet) (u: Username) =
        let peer = networkCfg.GetPeer coreSet 0
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
        let peer = networkCfg.GetPeer coreSet 0
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

    member self.CheckNoAbnormalKubeEvents(p: Peer) =
        let name = p.PodName.StringName

        for evt in self.GetAbnormalEventsForObject(p.PodName.StringName) do
            let estr = evt.ToString("Pod", name)
            LogError "Found abnormal event: %s" estr
            failwith estr

    member self.CheckNoErrorsAndPairwiseConsistency() =
        let cs = List.filter (fun cs -> cs.live = true) networkCfg.CoreSetList

        if not (List.isEmpty cs) then
            let peer = networkCfg.GetPeer cs.[0] 0

            networkCfg.EachPeer
                (fun p ->
                    self.CheckNoAbnormalKubeEvents p
                    p.CheckNoErrorMetrics(includeTxInternalErrors = false)
                    p.CheckConsistencyWith peer)

    member self.CheckUsesLatestProtocolVersion() = networkCfg.EachPeer(fun p -> p.CheckUsesLatestProtocolVersion())

    member self.RunLoadgen (coreSet: CoreSet) (loadGen: LoadGen) =
        let peer = networkCfg.GetPeer coreSet 0
        LogInfo "Loadgen: %s" (peer.GenerateLoad loadGen)
        peer.WaitForLoadGenComplete loadGen

    member self.RunLoadgenAndCheckNoErrors(coreSet: CoreSet) =
        let peer = networkCfg.GetPeer coreSet 0
        let loadGen = { DefaultAccountCreationLoadGen with accounts = 10000 }
        LogInfo "Loadgen: %s" (peer.GenerateLoad loadGen)
        peer.WaitForLoadGenComplete loadGen
        self.CheckNoErrorsAndPairwiseConsistency()
