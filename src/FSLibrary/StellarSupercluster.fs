// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarSupercluster

open k8s
open k8s.Models

open Logging
open StellarNetworkCfg
open StellarCoreCfg
open StellarKubeSpecs
open StellarCorePeer
open StellarCoreHTTP
open StellarTransaction
open System

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
                      ns: V1Namespace,
                      statefulSets: V1StatefulSet list,
                      ingress: Extensionsv1beta1Ingress,
                      probeTimeout: int) =
    let networkCfg = networkCfg
    let kube = kube
    let ns = ns
    let ingress = ingress
    let mutable statefulSets = statefulSets
    let mutable keepData = false
    let probeTimeout = probeTimeout

    let mutable disposed = false
    let cleanup(disposing:bool) =
        if not disposed then
            disposed <- true
            if disposing then
                if keepData
                then
                    LogInfo "Disposing formation, keeping namespace '%s' for debug"
                        networkCfg.NamespaceProperty
                else
                    LogInfo "Disposing formation, deleting namespace '%s'"
                        networkCfg.NamespaceProperty
                    ignore (kube.DeleteNamespace(name = networkCfg.NamespaceProperty,
                                     propagationPolicy = "Foreground"))

                    let deleteVolume persistentVolume = 
                        try
                            kube.DeletePersistentVolume(name = persistentVolume) |> ignore
                        with
                        | x -> ()
                        true

                    (List.forall deleteVolume networkCfg.ToPersistentVolumeNames) |> ignore

    member self.StatefulSets = statefulSets
    member self.NetworkCfg = networkCfg
    member self.Kube = kube

    member self.KeepData =
        keepData <- true

    // implementation of IDisposable
    interface System.IDisposable with
        member self.Dispose() =
            cleanup(true)
            System.GC.SuppressFinalize(self)

    // override of finalizer
    override self.Finalize() =
        cleanup(false)

    override self.ToString() : string =
        let name = statefulSets.[0].Metadata.Name
        let ns = statefulSets.[0].Metadata.NamespaceProperty
        sprintf "%s/%s" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasReady (ss : V1StatefulSet) =
        use event = new System.Threading.ManualResetEventSlim(false)
        let name = ss.Metadata.Name
        let ns = ss.Metadata.NamespaceProperty
        let handler (ety:WatchEventType) (ss:V1StatefulSet) =
            let n = ss.Status.ReadyReplicas.GetValueOrDefault(0)
            let k = ss.Spec.Replicas.GetValueOrDefault(0)
            LogInfo "StatefulSet %s/%s: %d/%d replicas ready" ns name n k;
            if n = k then event.Set()
        let action = System.Action<WatchEventType, V1StatefulSet>(handler)
        use task = kube.WatchNamespacedStatefulSetAsync(name = name,
                                                        ``namespace`` = ns,
                                                        onEvent = handler)
        LogInfo "Waiting for replicas on %s/%s" ns name
        event.Wait()
        LogInfo "All replicas on %s/%s ready" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasOnAllSetsReady() =
        LogInfo "Waiting for replicas on %s" (self.ToString())
        for ss in statefulSets do
            self.WaitForAllReplicasReady ss
        LogInfo "All replicas on %s ready" (self.ToString())

    member self.ChangeCount name count =
        networkCfg.ChangeCount name count
        let cs = networkCfg.Find name
        let ss = kube.ReplaceNamespacedStatefulSet(
                   body = networkCfg.ToStatefulSet cs count probeTimeout,
                   name = CfgVal.peerSetName cs,
                   namespaceParameter = networkCfg.NamespaceProperty)
        let newSets = statefulSets |> List.filter (fun x -> x.Metadata.Name <> CfgVal.peerSetName cs)
        statefulSets <- ss :: newSets
        self.WaitForAllReplicasReady ss

    member self.WaitUntilReady =
        networkCfg.EachPeer (fun p -> p.WaitUntilReady)

    member self.WaitUntilSynced sets =
        networkCfg.EachPeerInSets (sets |> Array.ofList) (fun p -> p.WaitUntilSynced)

    member self.ReportStatus() =
        ReportAllPeerStatus networkCfg

    member self.CreateAccount cs u =
        let peer = networkCfg.GetPeer cs 0
        let tx = peer.TxCreateAccount u
        LogInfo "creating account for %O on %O" u self
        ignore (peer.SubmitSignedTransaction tx)
        ignore (peer.WaitForNextLedger())
        let acc = peer.GetAccount(u)
        LogInfo "created account for %O on %O with seq %d, balance %d"
            u self acc.SequenceNumber
            (peer.GetTestAccBalance (u.ToString()))

    member self.Pay cs src dst =
        let peer = networkCfg.GetPeer cs 0
        let tx = peer.TxPayment src dst
        LogInfo "paying from account %O to %O on %O" src dst self
        ignore (peer.SubmitSignedTransaction tx)
        ignore (peer.WaitForNextLedger())
        LogInfo "sent payment from %O (%O) to %O (%O) on %O"
            src (peer.GetSeqAndBalance src)
            dst (peer.GetSeqAndBalance dst)
            self

    member self.CheckNoErrorsAndPairwiseConsistency =
        let peer = networkCfg.GetPeer networkCfg.coreSets.[0] 0
        networkCfg.EachPeer
            begin
                fun p ->
                    p.CheckNoErrorMetrics(includeTxInternalErrors=false)
                    p.CheckConsistencyWith peer
            end

    member self.CheckUsesLatestProtocolVersion =
        networkCfg.EachPeer
            begin
                fun p ->
                    p.CheckUsesLatestProtocolVersion
            end

    member self.RunLoadgen cs lg =
        let peer = networkCfg.GetPeer cs 0
        LogInfo "Loadgen: %s" (peer.GenerateLoad lg)
        peer.WaitForLoadGenComplete lg

    member self.RunLoadgenAndCheckNoErrors cs =
        let peer = networkCfg.GetPeer cs 0
        let lg = { DefaultAccountCreationLoadGen with accounts = 10000 }
        LogInfo "Loadgen: %s" (peer.GenerateLoad lg)
        peer.WaitForLoadGenComplete lg
        self.CheckNoErrorsAndPairwiseConsistency


type Kubernetes with

    // Starts a StatefulSet, Service, and Ingress for a given NetworkCfg, then
    // waits for it to be ready.
    member self.MakeFormation (nCfg:NetworkCfg) pv keepData probeTimeout : ClusterFormation =
        let nsStr = nCfg.NamespaceProperty
        let ns = self.CreateNamespace(nCfg.Namespace())
        try
            self.CreateNamespacedService(body = nCfg.ToService(),
                                         namespaceParameter = nsStr) |> ignore
            self.CreateNamespacedConfigMap(body = nCfg.ToConfigMap(),
                                           namespaceParameter = nsStr) |> ignore
            let makeStatefulSet cs = 
                self.CreateNamespacedStatefulSet(body = nCfg.ToStatefulSet cs cs.CurrentCount probeTimeout,
                                                               namespaceParameter = nsStr)  
            let statefulSets = List.map makeStatefulSet nCfg.coreSets

            match pv with
            | Some(x) ->
                for persistentVolume in nCfg.ToPersistentVolumes x do
                    self.CreatePersistentVolume(body = persistentVolume) |> ignore

                for persistentVolumeClaim in nCfg.ToPersistentVolumeClaims do
                    self.CreateNamespacedPersistentVolumeClaim(body = persistentVolumeClaim,
                                                               namespaceParameter = nsStr) |> ignore
            | None -> ()

            for svc in nCfg.ToPerPodServices do
                ignore (self.CreateNamespacedService(namespaceParameter = nsStr,
                                                     body = svc))

            let ingress = self.CreateNamespacedIngress(namespaceParameter = nsStr,
                                                       body = nCfg.ToIngress())
            let formation = new ClusterFormation(networkCfg = nCfg,
                                                 kube = self,
                                                 ns = ns,
                                                 statefulSets = statefulSets,
                                                 ingress = ingress,
                                                 probeTimeout = probeTimeout)
            formation.WaitForAllReplicasOnAllSetsReady()
            formation
        with
        | x ->
            if keepData         
            then
                LogError "Exception while building formation, keeping namespace '%s' for debug"
                             nCfg.NamespaceProperty
            else
                LogError "Exception while building formation, deleting namespace '%s'"
                             nCfg.NamespaceProperty
                self.DeleteNamespace(name = nCfg.NamespaceProperty,
                                     propagationPolicy = "Foreground") |> ignore
                match pv with
                | Some(x) ->
                    for persistentVolume in nCfg.ToPersistentVolumes x do
                        try
                            self.DeletePersistentVolume(name = persistentVolume.Metadata.Name) |> ignore
                        with
                        | x -> ()
                | None -> ()
            reraise()

