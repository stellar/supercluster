// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarSupercluster

open stellar_dotnet_sdk
open k8s
open k8s.Models

open Logging
open StellarNetworkCfg
open StellarCoreCfg
open StellarKubeSpecs
open StellarCorePeer
open StellarCoreHTTP
open StellarTransaction

let ExpandHomeDirTilde (s:string) : string =
    if s.StartsWith("~/")
    then
        let upp = System.Environment.SpecialFolder.UserProfile
        let home = System.Environment.GetFolderPath(upp)
        home + s.Substring(1)
    else
        s


// Loads a config file and builds a Kubernetes client object connected to the
// cluster described by it.
let ConnectToCluster (cfgFile:string) : Kubernetes =
    let cfgFileInfo = System.IO.FileInfo(ExpandHomeDirTilde cfgFile)
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
                      statefulSet: V1StatefulSet,
                      ingress: Extensionsv1beta1Ingress) =

    let mutable disposed = false
    let cleanup(disposing:bool) =
        if not disposed then
            disposed <- true
            if disposing then
                LogInfo "Disposing formation, deleting namespace '%s'"
                    networkCfg.NamespaceProperty
                ignore (kube.DeleteNamespace(name = networkCfg.NamespaceProperty,
                                 propagationPolicy = "Foreground"))

    // implementation of IDisposable
    interface System.IDisposable with
        member self.Dispose() =
            cleanup(true)
            System.GC.SuppressFinalize(self)

    // override of finalizer
    override self.Finalize() =
        cleanup(false)

    member self.networkCfg = networkCfg
    member self.kube = kube
    member self.ns = ns
    member self.statefulSet = statefulSet
    member self.ingress = ingress

    override self.ToString() : string =
        let name = self.statefulSet.Metadata.Name
        let ns = self.statefulSet.Metadata.NamespaceProperty
        sprintf "%s/%s" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasReady() =
        use event = new System.Threading.ManualResetEventSlim(false)
        let name = self.statefulSet.Metadata.Name
        let ns = self.statefulSet.Metadata.NamespaceProperty
        let handler (ety:WatchEventType) (ss:V1StatefulSet) =
            let n = ss.Status.ReadyReplicas.GetValueOrDefault(0)
            let k = ss.Spec.Replicas.GetValueOrDefault(0)
            LogInfo "StatefulSet %s/%s: %d/%d replicas ready" ns name n k;
            if n = k then event.Set()
        let action = System.Action<WatchEventType, V1StatefulSet>(handler)
        use task = self.kube.WatchNamespacedStatefulSetAsync(name = name,
                                                             ``namespace`` = ns,
                                                             onEvent = handler)
        LogInfo "Waiting for replicas"
        event.Wait()
        LogInfo "All replicas ready"

    member self.ReportStatus() =
        ReportAllPeerStatus self.networkCfg

    member self.CreateAccount (u:Username) : unit =
        let peer = self.networkCfg.GetPeer 0
        let tx = peer.TxCreateAccount u
        LogInfo "creating account for %O on %O" u self
        ignore (peer.SubmitSignedTransaction tx)
        ignore (peer.WaitForNextLedger())
        let acc = peer.GetAccount(u)
        LogInfo "created account for %O on %O with seq %d, balance %d"
            u self acc.SequenceNumber
            (peer.GetTestAccBalance (u.ToString()))

    member self.Pay (src:Username) (dst:Username) : unit =
        let peer = self.networkCfg.GetPeer 0
        let tx = peer.TxPayment src dst
        LogInfo "paying from account %O to %O on %O" src dst self
        ignore (peer.SubmitSignedTransaction tx)
        ignore (peer.WaitForNextLedger())
        LogInfo "sent payment from %O (%O) to %O (%O) on %O"
            src (peer.GetSeqAndBalance src)
            dst (peer.GetSeqAndBalance dst)
            self

    member self.CheckNoErrorsAndPairwiseConsistency() : unit =
        let peer = self.networkCfg.GetPeer 0
        self.networkCfg.EachPeer
            begin
                fun p ->
                    p.CheckNoErrorMetrics(includeTxInternalErrors=false)
                    p.CheckConsistencyWith peer
            end

    member self.RunLoadgenAndCheckNoErrors() : unit =
        let peer = self.networkCfg.GetPeer 0
        let lg = { DefaultAccountCreationLoadGen with accounts = 10000 }
        LogInfo "Loadgen: %s" (peer.GenerateLoad lg)
        peer.WaitForLoadGenComplete lg
        self.CheckNoErrorsAndPairwiseConsistency()


type Kubernetes with

    // Starts a StatefulSet, Service, and Ingress for a given NetworkCfg, then
    // waits for it to be ready.
    member self.MakeFormation (nCfg:NetworkCfg) : ClusterFormation =
        let nsStr = nCfg.NamespaceProperty
        let ns = self.CreateNamespace(nCfg.Namespace())
        try
            let svc = self.CreateNamespacedService(body = nCfg.ToService(),
                                                   namespaceParameter = nsStr)
            let cfgMap = self.CreateNamespacedConfigMap(body = nCfg.ToConfigMap(),
                                                        namespaceParameter = nsStr)
            let statefulSet = self.CreateNamespacedStatefulSet(body = nCfg.ToStatefulSet CfgVal.peerSetName,
                                                               namespaceParameter = nsStr)

            for svc in nCfg.ToPerPodServices() do
                ignore (self.CreateNamespacedService(namespaceParameter = nsStr,
                                                     body = svc))

            let ingress = self.CreateNamespacedIngress(namespaceParameter = nsStr,
                                                       body = nCfg.ToIngress())
            let formation = new ClusterFormation(networkCfg = nCfg,
                                                 kube = self,
                                                 ns = ns,
                                                 statefulSet = statefulSet,
                                                 ingress = ingress)
            formation.WaitForAllReplicasReady()
            formation
        with
        | x ->
            LogError "Exception while building formation, deleting namespace '%s'"
                         nCfg.NamespaceProperty
            ignore (self.DeleteNamespace(name = nCfg.NamespaceProperty,
                                         propagationPolicy = "Foreground"))
            reraise()

