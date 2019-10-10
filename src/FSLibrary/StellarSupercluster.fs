// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarSupercluster

open k8s
open k8s.Models

open Logging
open StellarNetworkCfg
open StellarCoreCfg
open StellarCoreSet
open StellarKubeSpecs
open StellarCorePeer
open StellarCoreHTTP
open StellarPersistentVolume
open StellarTransaction
open StellarNamespaceContent
open System
open Microsoft.Rest

let ExpandHomeDirTilde (s:string) : string =
    if s.StartsWith("~/")
    then
        let upp = Environment.SpecialFolder.UserProfile
        let home = Environment.GetFolderPath(upp)
        home + s.Substring(1)
    else
        s


// Loads a config file and builds a Kubernetes client object connected to the
// cluster described by it.
let ConnectToCluster (cfgFile:string) : Kubernetes =
    let cfgFileInfo = IO.FileInfo(ExpandHomeDirTilde cfgFile)
    let kCfg = k8s.KubernetesClientConfiguration.BuildConfigFromConfigFile(cfgFileInfo)
    new k8s.Kubernetes(kCfg)


// Prints the stellar-core StatefulSets and Pods on the provided cluster
let PollCluster (kube:Kubernetes) =
    let sets = kube.ListStatefulSetForAllNamespaces(labelSelector=CfgVal.labelSelector)
    for s in sets.Items do
        LogInfo "StatefulSet: ns=%s name=%s replicas=%d" s.Metadata.NamespaceProperty
                                                         s.Metadata.Name s.Status.Replicas
        let pods = kube.ListNamespacedPod(namespaceParameter = s.Metadata.NamespaceProperty,
                                          labelSelector = CfgVal.labelSelector)
        for p in pods.Items do
            LogInfo "        Pod ns=%s name=%s phase=%s IP=%s" p.Metadata.NamespaceProperty
                                                               p.Metadata.Name p.Status.Phase
                                                               p.Status.PodIP


// Typically you want to instantiate one of these per test scenario, and run methods on it.
// Unlike other types in this library it's a class type and implements IDisposable: it will
// tear down the Kubernetes namespace (and all contained cluster-side objects) as it goes.
type ClusterFormation(networkCfg: NetworkCfg,
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

    member self.Kube = kube
    member self.NetworkCfg = networkCfg

    member self.CleanNamespace() =
        LogInfo "Cleaning all resources from namespace '%s'"
            networkCfg.NamespaceProperty
        namespaceContent.AddAll()
        namespaceContent.Cleanup()

    member self.ForceCleanup() =
        let deleteVolume persistentVolume =
            try
                self.Kube.DeletePersistentVolume(name = persistentVolume) |> ignore
            with
            | x -> ()
            true

        (List.forall deleteVolume (networkCfg.ToPersistentVolumeNames())) |> ignore

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
        LogInfo "Waiting for replicas on %s/%s" ns name
        // Sometimes the "Wait" call here stalls out, presumably due to
        // a failure in the watch subscription: retry up to 100 times.
        let rec tryWait (n:int) =
            use event = new System.Threading.ManualResetEventSlim(false)
            let mutable active = true
            let handler (ety:WatchEventType) (ss:V1StatefulSet) =
                if active
                then
                    let n = ss.Status.ReadyReplicas.GetValueOrDefault(0)
                    let k = ss.Spec.Replicas.GetValueOrDefault(0)
                    LogInfo "StatefulSet %s/%s: %d/%d replicas ready" ns name n k;
                    if n = k then event.Set()
            let action = System.Action<WatchEventType, V1StatefulSet>(handler)
            use task = kube.WatchNamespacedStatefulSetAsync(name = name,
                                                            ``namespace`` = ns,
                                                            onEvent = handler)
            let timeout = 10000 * (max 1 (ss.Spec.Replicas.GetValueOrDefault(0)))
            if event.Wait(millisecondsTimeout = timeout)
            then ()
            else
                active <- false
                if n = 0
                then let msg = "Failed to start replicas on sts " + ss.Metadata.Name
                     LogError "%s" msg
                     failwith msg
                else
                    LogWarn "Retrying replica-start %d more times on sts %s"
                        (n-1) ss.Metadata.Name
                    tryWait (n-1)
        tryWait 100
        LogInfo "All replicas on %s/%s ready" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasOnAllSetsReady() =
        LogInfo "Waiting for replicas on %s" (self.ToString())
        for ss in statefulSets do
            self.WaitForAllReplicasReady ss
        LogInfo "All replicas on %s ready" (self.ToString())

    member self.WithLive name (live: bool) =
        networkCfg <- networkCfg.WithLive name live
        let coreSet = networkCfg.FindCoreSet name
        let peerSetName = networkCfg.PeerSetName coreSet
        let ss = kube.ReplaceNamespacedStatefulSet(
                   body = networkCfg.ToStatefulSet coreSet probeTimeout,
                   name = peerSetName,
                   namespaceParameter = networkCfg.NamespaceProperty)
        let newSets = statefulSets |> List.filter (fun x -> x.Metadata.Name <> peerSetName)
        statefulSets <- ss :: newSets
        self.WaitForAllReplicasReady ss

    member self.Start name =
        self.WithLive name true

    member self.Stop name =
        self.WithLive name false

    member self.WaitUntilReady () =
        networkCfg.EachPeer (fun p -> p.WaitUntilReady())

    member self.WaitUntilSynced (coreSetList: CoreSet list) =
        networkCfg.EachPeerInSets (coreSetList |> Array.ofList) (fun p -> p.WaitUntilSynced())

    member self.UpgradeProtocol (coreSetList: CoreSet list) (version: int) =
        networkCfg.EachPeerInSets (coreSetList |> Array.ofList) (fun p -> p.UpgradeProtocol version)

    member self.UpgradeProtocolToLatest (coreSetList: CoreSet list) =
        networkCfg.EachPeerInSets (coreSetList |> Array.ofList) (fun p -> p.UpgradeProtocolToLatest())

    member self.UpgradeMaxTxSize (coreSetList: CoreSet list) (maxTxSize: int) =
        networkCfg.EachPeerInSets (coreSetList |> Array.ofList) (fun p -> p.UpgradeMaxTxSize maxTxSize)

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


type Kubernetes with

    // Starts a StatefulSet, Service, and Ingress for a given NetworkCfg, then
    // waits for it to be ready.

    member self.MakeEmptyFormation (nCfg: NetworkCfg) : ClusterFormation =
        new ClusterFormation(networkCfg = nCfg,
                             kube = self,
                             statefulSets = [],
                             namespaceContent = NamespaceContent(self, nCfg.NamespaceProperty),
                             probeTimeout = 1)


    member self.MakeFormation (nCfg: NetworkCfg) (persistentVolume: PersistentVolume option) (keepData: bool) (probeTimeout: int) : ClusterFormation =
        let nsStr = nCfg.NamespaceProperty
        let namespaceContent = NamespaceContent(self, nsStr)
        try
            namespaceContent.Add(self.CreateNamespacedService(body = nCfg.ToService(),
                                                              namespaceParameter = nsStr))
            namespaceContent.Add(self.CreateNamespacedConfigMap(body = nCfg.ToConfigMap(),
                                                                namespaceParameter = nsStr))

            let makeStatefulSet coreSet =
                self.CreateNamespacedStatefulSet(body = nCfg.ToStatefulSet coreSet probeTimeout,
                                                 namespaceParameter = nsStr)
            let statefulSets = List.map makeStatefulSet nCfg.CoreSetList
            for statefulSet in statefulSets do
                namespaceContent.Add(statefulSet)

            match persistentVolume with
            | Some(pv) ->
                for persistentVolume in nCfg.ToPersistentVolumes pv do
                    self.CreatePersistentVolume(body = persistentVolume) |> ignore
            | None -> ()

            for persistentVolumeClaim in nCfg.ToPersistentVolumeClaims() do
                let claim = self.CreateNamespacedPersistentVolumeClaim(body = persistentVolumeClaim,
                                                                       namespaceParameter = nsStr)
                namespaceContent.Add(claim)

            for svc in nCfg.ToPerPodServices() do
                let service = self.CreateNamespacedService(namespaceParameter = nsStr,
                                                           body = svc)
                namespaceContent.Add(service)

            let ingress = self.CreateNamespacedIngress(namespaceParameter = nsStr,
                                         body = nCfg.ToIngress())
            namespaceContent.Add(ingress)

            let formation = new ClusterFormation(networkCfg = nCfg,
                                                 kube = self,
                                                 statefulSets = statefulSets,
                                                 namespaceContent = namespaceContent,
                                                 probeTimeout = probeTimeout)
            formation.WaitForAllReplicasOnAllSetsReady()
            formation
        with
        | x ->
            if keepData
            then
                LogError "Exception while building formation, keeping resources for run '%s' in namespace '%s' for debug"
                             nCfg.Nonce
                             nCfg.NamespaceProperty
            else
                LogError "Exception while building formation, cleaning up resources for run '%s' in namespace '%s'"
                             nCfg.Nonce
                             nCfg.NamespaceProperty
                namespaceContent.Cleanup()

                match persistentVolume with
                | Some(pv) ->
                    for persistentVolume in nCfg.ToPersistentVolumes pv do
                        try
                            self.DeletePersistentVolume(name = persistentVolume.Metadata.Name) |> ignore
                        with
                        | x -> ()
                | None -> ()
            reraise()

