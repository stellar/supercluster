// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNamespaceContent

open k8s
open k8s.Models
open Logging

type NamespaceContent(kube: Kubernetes, apiRateLimit: int, namespaceProperty: string) =

    let kube = kube
    let namespaceProperty = namespaceProperty
    let services : Set<string> ref = ref Set.empty
    let configMaps : Set<string> ref = ref Set.empty
    let statefulSets : Set<string> ref = ref Set.empty
    let ingresses : Set<string> ref = ref Set.empty
    let jobs : Set<string> ref = ref Set.empty

    let ignoreError f =
        try
            f () |> ignore
        with x -> ()

    let delService (name: string) =
        LogInfo "Deleting Service %s" name
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        ignoreError
            (fun _ ->
                kube.DeleteNamespacedService(
                    namespaceParameter = namespaceProperty,
                    name = name,
                    propagationPolicy = "Foreground"
                ))

    let delConfigMap (name: string) =
        LogInfo "Deleting ConfigMap %s" name
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        ignoreError
            (fun _ ->
                kube.DeleteNamespacedConfigMap(
                    namespaceParameter = namespaceProperty,
                    name = name,
                    propagationPolicy = "Foreground"
                ))

    let delStatefulSet (name: string) =
        LogInfo "Deleting StatefulSet %s" name
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        ignoreError
            (fun _ ->
                kube.DeleteNamespacedStatefulSet(
                    namespaceParameter = namespaceProperty,
                    name = name,
                    propagationPolicy = "Foreground"
                ))

    let delIngress (name: string) =
        LogInfo "Deleting Ingress %s" name
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        ignoreError
            (fun _ ->
                kube.DeleteNamespacedIngress(
                    namespaceParameter = namespaceProperty,
                    name = name,
                    propagationPolicy = "Foreground"
                ))

    let delJob (name: string) =
        LogInfo "Deleting Job %s" name
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        ignoreError
            (fun _ ->
                kube.DeleteNamespacedJob(
                    namespaceParameter = namespaceProperty,
                    name = name,
                    propagationPolicy = "Foreground"
                ))

    let cleanSet (f: 'a -> unit) (s: Set<'a> ref) : unit =
        Set.iter f (!s)
        s := Set.empty

    let addOne (s: Set<'a> ref) (x: 'a) : unit = s := Set.add x (!s)

    let delOne (f: 'a -> unit) (s: Set<'a> ref) (x: 'a) : unit =
        s := Set.remove x (!s)
        f x

    member self.Cleanup() =
        cleanSet delService services
        cleanSet delStatefulSet statefulSets
        cleanSet delConfigMap configMaps
        cleanSet delIngress ingresses
        cleanSet delJob jobs

    member self.Add(service: V1Service) = addOne services service.Metadata.Name

    member self.Add(configMap: V1ConfigMap) = addOne configMaps configMap.Metadata.Name

    member self.Add(statefulSet: V1StatefulSet) = addOne statefulSets statefulSet.Metadata.Name

    member self.Add(ingress: V1Ingress) = addOne ingresses ingress.Metadata.Name

    member self.Add(job: V1Job) = addOne jobs job.Metadata.Name

    member self.Del(service: V1Service) = delOne delService services service.Metadata.Name

    member self.Del(configMap: V1ConfigMap) = delOne delConfigMap configMaps configMap.Metadata.Name

    member self.Del(statefulSet: V1StatefulSet) = delOne delStatefulSet statefulSets statefulSet.Metadata.Name

    member self.Del(ingress: V1Ingress) = delOne delIngress ingresses ingress.Metadata.Name

    member self.Del(job: V1Job) = delOne delJob jobs job.Metadata.Name

    member self.AddAll() =
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        for s in kube.ListNamespacedService(namespaceParameter = namespaceProperty).Items do
            self.Add(s)

        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        for c in kube.ListNamespacedConfigMap(namespaceParameter = namespaceProperty).Items do
            self.Add(c)

        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        for s in kube.ListNamespacedStatefulSet(namespaceParameter = namespaceProperty).Items do
            self.Add(s)

        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)
        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        for i in kube.ListNamespacedIngress(namespaceParameter = namespaceProperty).Items do
            self.Add(i)

        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (apiRateLimit)

        for i in kube.ListNamespacedJob(namespaceParameter = namespaceProperty).Items do
            self.Add(i)
