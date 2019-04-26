// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarSupercluster

open stellar_dotnet_sdk
open k8s
open k8s.Models

open StellarNetworkCfg
open StellarCoreCfg
open StellarKubeSpecs
open StellarCoreHTTP

// Watches the provided StatefulSet until the count of ready replicas equals the
// count of configured replicas. This normally represents "successful startup".
let WaitForAllReplicasReady (kube:Kubernetes) (statefulSet:V1StatefulSet) =
    use event = new System.Threading.ManualResetEventSlim(false)
    let name = statefulSet.Metadata.Name
    let ns = statefulSet.Metadata.NamespaceProperty
    let handler (ety:WatchEventType) (ss:V1StatefulSet) =
        let n = ss.Status.ReadyReplicas.GetValueOrDefault(0)
        let k = ss.Spec.Replicas.GetValueOrDefault(0)
        printfn "StatefulSet %s/%s: %d/%d replicas ready" ns name n k;
        if n = k then event.Set()
    let action = System.Action<WatchEventType, V1StatefulSet>(handler)
    use task = kube.WatchNamespacedStatefulSetAsync(name = name,
                                                    ``namespace`` = ns,
                                                    onEvent = handler)
    printfn "Waiting for replicas"
    event.Wait()
    printfn "All replicas ready"


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
        printfn "StatefulSet: ns=%s name=%s replicas=%d" s.Metadata.NamespaceProperty
                                                         s.Metadata.Name s.Status.Replicas
        let pods = kube.ListNamespacedPod(namespaceParameter = s.Metadata.NamespaceProperty,
                                          labelSelector = CfgVal.labelSelector)
        for p in pods.Items do
            printfn "\tPod ns=%s name=%s phase=%s IP=%s" p.Metadata.NamespaceProperty
                                                         p.Metadata.Name p.Status.Phase
                                                         p.Status.PodIP

// Starts a StatefulSet, Service, and Ingress for a given NetworkCfg, then
// waits for it to be ready.
let RunCluster (kube:Kubernetes) (nCfg:NetworkCfg) =
    let nsStr = nCfg.NamespaceProperty()
    let ns = kube.CreateNamespace(nCfg.Namespace())
    let svc = kube.CreateNamespacedService(body = nCfg.ToService(),
                                           namespaceParameter = nsStr)
    let cfgMap = kube.CreateNamespacedConfigMap(body = nCfg.ToConfigMap(),
                                                namespaceParameter = nsStr)
    let statefulSet = kube.CreateNamespacedStatefulSet(body = nCfg.ToStatefulSet CfgVal.peerSetName,
                                                       namespaceParameter = nsStr)

    for svc in nCfg.ToPerPodServices() do
        ignore (kube.CreateNamespacedService(namespaceParameter = nsStr,
                                             body = svc))

    let ingress = kube.CreateNamespacedIngress(namespaceParameter = nsStr,
                                               body = nCfg.ToIngress())

    WaitForAllReplicasReady kube statefulSet
    ReportAllPeerStatus nCfg
    ()

