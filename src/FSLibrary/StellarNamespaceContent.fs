// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNamespaceContent

open k8s
open k8s.Models

type NamespaceContent(kube: Kubernetes,
                      namespaceProperty: string) =

    let kube = kube
    let namespaceProperty = namespaceProperty
    let mutable serviceList: string list = []
    let mutable configMapList: string list = []
    let mutable statefulSetList: string list = []
    let mutable persistentVolumeClaimList: string list = []
    let mutable ingressList: string list = []

    member self.Cleanup() =
        let safeDelete f list =
            for item in list do
                try
                    f(item) |> ignore
                with
                | x -> ()

        safeDelete (fun name -> kube.DeleteNamespacedService(namespaceParameter = namespaceProperty, name = name, propagationPolicy = "Foreground")) serviceList
        safeDelete (fun name -> kube.DeleteNamespacedConfigMap(namespaceParameter = namespaceProperty, name = name, propagationPolicy = "Foreground")) configMapList
        safeDelete (fun name -> kube.DeleteNamespacedStatefulSet(namespaceParameter = namespaceProperty, name = name, propagationPolicy = "Foreground")) statefulSetList
        safeDelete (fun name -> kube.DeleteNamespacedPersistentVolumeClaim(namespaceParameter = namespaceProperty, name = name, propagationPolicy = "Foreground")) persistentVolumeClaimList
        safeDelete (fun name -> kube.DeleteNamespacedIngress(namespaceParameter = namespaceProperty, name = name, propagationPolicy = "Foreground")) ingressList

    member self.Add(service: V1Service) =
        serviceList <- service.Metadata.Name :: serviceList

    member self.Add(configMap: V1ConfigMap) =
        configMapList <- configMap.Metadata.Name :: configMapList

    member self.Add(statefulSet: V1StatefulSet) =
        statefulSetList <- statefulSet.Metadata.Name :: statefulSetList

    member self.Add(persistentVolumeClaim: V1PersistentVolumeClaim) =
        persistentVolumeClaimList <- persistentVolumeClaim.Metadata.Name :: persistentVolumeClaimList

    member self.Add(ingress: Extensionsv1beta1Ingress) =
        ingressList <- ingress.Metadata.Name :: ingressList

    member self.AddAll() =
        for s in kube.ListNamespacedService(namespaceParameter = namespaceProperty).Items do
            self.Add(s)
        for c in kube.ListNamespacedConfigMap(namespaceParameter = namespaceProperty).Items do
            self.Add(c)
        for s in kube.ListNamespacedStatefulSet(namespaceParameter = namespaceProperty).Items do
            self.Add(s)
        for c in kube.ListNamespacedPersistentVolumeClaim(namespaceParameter = namespaceProperty).Items do
            self.Add(c)
        for i in kube.ListNamespacedIngress(namespaceParameter = namespaceProperty).Items do
            self.Add(i)
