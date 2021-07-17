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

    member self.Destination = self.NetworkCfg.missionContext.destination

    member self.NamespaceContent = namespaceContent

    member self.NextJobNum : int =
        jobNumber <- jobNumber + 1
        jobNumber

    member self.AllJobNums : int list = [ 1 .. jobNumber ]

    member self.Kube = kube
    member self.NetworkCfg = networkCfg
    member self.SetNetworkCfg(n: NetworkCfg) = networkCfg <- n
    member self.StatefulSets = statefulSets
    member self.SetStatefulSets(s: V1StatefulSet list) = statefulSets <- s

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
                && ev.Reason <> "FailedMount"
                && not <| ev.Message.Contains("Startup probe failed"))
            events

    member self.CheckNoAbnormalKubeEvents(p: Peer) =
        let name = p.PodName.StringName

        for evt in self.GetAbnormalEventsForObject(p.PodName.StringName) do
            let estr = evt.ToString("Pod", name)
            LogError "Found abnormal event: %s" estr
            failwith estr

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
