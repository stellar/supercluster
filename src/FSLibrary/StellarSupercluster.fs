// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarSupercluster

open k8s
open k8s.Models

open Logging
open StellarNetworkCfg
open StellarFormation
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


// Typically one starts with `ConnectToCluster` above to get a `Kubernetes`
// object, and then calls one of these `Kubernetes` extension methods to
// establish a `StellarFormation` object to run tests against.
type Kubernetes with

    // Creates a minimal formation on which to run Jobs; no StatefulSets,
    // services, ingresses or anything.
    member self.MakeEmptyFormation (nCfg: NetworkCfg) : StellarFormation =
        new StellarFormation(networkCfg = nCfg,
                             kube = self,
                             statefulSets = [],
                             namespaceContent = NamespaceContent(self, nCfg.NamespaceProperty),
                             probeTimeout = 1)


    // Creates a full-featured formation involving a StatefulSet, Service, and
    // Ingress for a given NetworkCfg, then waits for it to be ready.
    member self.MakeFormation (nCfg: NetworkCfg) (persistentVolume: PersistentVolume option) (keepData: bool) (probeTimeout: int) : StellarFormation =
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

            if not (List.isEmpty statefulSets)
            then
                let ingress = self.CreateNamespacedIngress(namespaceParameter = nsStr,
                                                           body = nCfg.ToIngress())
                namespaceContent.Add(ingress)

            let formation = new StellarFormation(networkCfg = nCfg,
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

