// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarSupercluster

open k8s
open k8s.Models
open k8s.KubeConfigModels

open Logging
open StellarDataDump
open StellarMissionContext
open StellarNetworkCfg
open StellarFormation
open StellarStatefulSets
open StellarCoreSet
open StellarKubeSpecs
open StellarNamespaceContent
open StellarPerformanceReporter
open System

let ExpandHomeDirTilde (s: string) : string =
    if s.StartsWith("~/") then
        let upp = Environment.SpecialFolder.UserProfile
        let home = Environment.GetFolderPath(upp)
        home + s.Substring(1)
    else
        s

// Loads a config file and builds a Kubernetes client object connected to the
// cluster described by it. Takes an optional explicit namespace and returns a
// resolved namespace, which will be taken from the config file if no explicit
// namespace is provided.
let ConnectToCluster (cfgFile: string) (nsOpt: string option) : (Kubernetes * string) =
    let cfgFileExpanded = ExpandHomeDirTilde cfgFile
    let cfgFileInfo = IO.FileInfo(cfgFileExpanded)
    let kCfg = k8s.KubernetesClientConfiguration.LoadKubeConfig(cfgFileInfo)
    LogInfo "Connecting to cluster using kubeconfig %s" cfgFileExpanded

    let ns =
        match nsOpt with
        | Some ns ->
            LogInfo "Using explicit namespace '%s'" ns
            ns
        | None ->
            (let ctxOpt = Seq.tryFind (fun (c: Context) -> c.Name = kCfg.CurrentContext) kCfg.Contexts

             match ctxOpt with
             | Some c ->
                 LogInfo "Using namespace '%s' from kubeconfig context '%s'" c.ContextDetails.Namespace c.Name
                 c.ContextDetails.Namespace
             | None ->
                 LogInfo "Using default namespace 'stellar-supercluster'"
                 "stellar-supercluster")

    let clientConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(kCfg)
    let kube = new k8s.Kubernetes(clientConfig)
    (kube, ns)

// Prints the stellar-core StatefulSets and Pods on the provided cluster
let PollCluster (kube: Kubernetes) (ns: string) =

    let sets = kube.ListNamespacedStatefulSet(namespaceParameter = ns)

    for s in sets.Items do
        LogInfo "StatefulSet: ns=%s name=%s replicas=%d" ns s.Metadata.Name s.Status.Replicas

    let jobs = kube.ListNamespacedJob(namespaceParameter = ns)

    for j in jobs.Items do
        LogInfo "Job: ns=%s name=%s condition=%O" ns j.Metadata.Name (Seq.last j.Status.Conditions)

    let pods = kube.ListNamespacedPod(namespaceParameter = ns)

    for p in pods.Items do
        LogInfo "Pod: ns=%s name=%s phase=%s IP=%s" ns p.Metadata.Name p.Status.Phase p.Status.PodIP

let DumpPodInfo (kube: Kubernetes) (ns: string) =
    let pods = kube.ListNamespacedPod(namespaceParameter = ns)

    if pods <> null then
        LogInfo "There are %d pods in total" (Seq.length pods.Items)

        for p in pods.Items do
            let age =
                if p.Status.StartTime.HasValue then
                    System.DateTime.UtcNow.Subtract(p.Status.StartTime.Value).ToString(@"hh\:mm")
                else
                    "00:00"

            LogInfo "Pod: name=%s phase=%s age=%s (hr:min)" p.Metadata.Name p.Status.Phase age

// Typically one starts with `ConnectToCluster` above to get a `Kubernetes`
// object, and then calls one of these `Kubernetes` extension methods to
// establish a `StellarFormation` object to run tests against.
type Kubernetes with

    // Creates a minimal formation on which to run Jobs; no StatefulSets,
    // services, ingresses or anything.
    member self.MakeEmptyFormation(nCfg: NetworkCfg) : StellarFormation =
        new StellarFormation(
            networkCfg = nCfg,
            kube = self,
            statefulSets = [],
            namespaceContent = NamespaceContent(self, nCfg.missionContext.apiRateLimit, nCfg.NamespaceProperty)
        )


    // Creates a full-featured formation involving a StatefulSet, Service, and
    // Ingress for a given NetworkCfg, then waits for it to be ready.
    member self.MakeFormation(nCfg: NetworkCfg) : StellarFormation =
        let nsStr = nCfg.NamespaceProperty
        let namespaceContent = NamespaceContent(self, nCfg.missionContext.apiRateLimit, nsStr)
        let rps = nCfg.missionContext.apiRateLimit

        try
            let svc = nCfg.ToService()
            LogInfo "Creating Service %s" svc.Metadata.Name
            ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (rps)
            namespaceContent.Add(self.CreateNamespacedService(body = svc, namespaceParameter = nsStr))

            for cm in nCfg.ToConfigMaps() do
                LogInfo "Creating ConfigMap %s" cm.Metadata.Name
                ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (rps)
                namespaceContent.Add(self.CreateNamespacedConfigMap(body = cm, namespaceParameter = nsStr))

            let makeStatefulSet coreSet =
                let sts = nCfg.ToStatefulSet coreSet
                LogInfo "Creating StatefulSet %s" sts.Metadata.Name
                ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (rps)
                self.CreateNamespacedStatefulSet(body = sts, namespaceParameter = nsStr)

            let statefulSets = List.map makeStatefulSet nCfg.CoreSetList

            for statefulSet in statefulSets do
                namespaceContent.Add(statefulSet)

            for svc in nCfg.ToPerPodServices() do
                LogInfo "Creating Per-Pod Service %s" svc.Metadata.Name
                ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (rps)

                let service = self.CreateNamespacedService(namespaceParameter = nsStr, body = svc)
                namespaceContent.Add(service)

            if not (List.isEmpty statefulSets) then
                let ing = nCfg.ToIngress()
                LogInfo "Creating Ingress %s" ing.Metadata.Name
                ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (rps)
                let ingress = self.CreateNamespacedIngress(namespaceParameter = nsStr, body = ing)
                namespaceContent.Add(ingress)

            let formation =
                new StellarFormation(
                    networkCfg = nCfg,
                    kube = self,
                    statefulSets = statefulSets,
                    namespaceContent = namespaceContent
                )

            formation.WaitForAllReplicasOnAllSetsReady()

            if nCfg.missionContext.exportToPrometheus then
                LogInfo "Core metrics will be exported to prometheus"

                nCfg.MapAllPeers
                    (fun (cs: CoreSet) (i: int) ->
                        let podName = nCfg.PodName cs i
                        let shortName = nCfg.PeerShortName cs i
                        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (rps)

                        let pod : V1Pod =
                            self.ReadNamespacedPod(
                                name = podName.StringName,
                                namespaceParameter = nCfg.NamespaceProperty
                            )

                        LogInfo "Setting pod %s label short_name = %s" podName.StringName shortName.StringName
                        pod.Metadata.Labels.Add("short_name", shortName.StringName) |> ignore
                        ApiRateLimit.sleepUntilNextRateLimitedApiCallTime (rps)

                        self.ReplaceNamespacedPod(
                            body = pod,
                            name = podName.StringName,
                            namespaceParameter = nCfg.NamespaceProperty
                        ))
                |> ignore


            formation
        with x ->
            if nCfg.missionContext.keepData then
                LogError
                    "Exception while building formation, keeping resources for run '%s' in namespace '%s' for debug"
                    nCfg.Nonce
                    nCfg.NamespaceProperty
            else
                LogError
                    "Exception while building formation, cleaning up resources for run '%s' in namespace '%s'"
                    nCfg.Nonce
                    nCfg.NamespaceProperty

                namespaceContent.Cleanup()

            reraise ()


// Formations are _created_ by calling methods on MissionContexts, and
// various MissionContext.Execute* methods drive formation setup and teardown.
// The methods are extensions that have to be defined after Formation itself,
// so they reside here.
type MissionContext with

    member self.MakeFormation (coreSetList: CoreSet list) (passphrase: NetworkPassphrase option) : StellarFormation =
        let networkCfg = MakeNetworkCfg self coreSetList passphrase
        let formation = self.kube.MakeFormation networkCfg
        formation

    member self.MakeFormationForJob
        (opts: CoreSetOptions option)
        (passphrase: NetworkPassphrase option)
        : StellarFormation =
        let networkCfg = MakeNetworkCfg self [] passphrase
        let networkCfg = { networkCfg with jobCoreSetOptions = opts }
        self.kube.MakeFormation networkCfg

    member self.ExecuteJobs
        (opts: CoreSetOptions option)
        (passphrase: NetworkPassphrase option)
        (run: StellarFormation -> unit)
        =
        use formation = self.MakeFormationForJob opts passphrase

        try
            try
                run formation
            finally
                formation.DumpJobData()
        with x ->
            (if self.keepData then formation.KeepData()
             reraise ())

    member self.Execute
        (coreSetList: CoreSet list)
        (passphrase: NetworkPassphrase option)
        (run: StellarFormation -> unit)
        : unit =
        use formation = self.MakeFormation coreSetList passphrase

        try
            try
                formation.WaitUntilReady()
                run formation
                formation.CheckNoErrorsAndPairwiseConsistency()
            finally
                formation.DumpData()
        with x ->
            (if self.keepData then formation.KeepData()
             reraise ())

    member self.ExecuteWithPerformanceReporter
        (coreSetList: CoreSet list)
        (passphrase: NetworkPassphrase option)
        (run: StellarFormation -> PerformanceReporter -> unit)
        : unit =
        use formation = self.MakeFormation coreSetList passphrase
        let performanceReporter = PerformanceReporter formation.NetworkCfg

        try
            try
                formation.WaitUntilReady()
                run formation performanceReporter
                formation.CheckNoErrorsAndPairwiseConsistency()
            finally
                performanceReporter.DumpPerformanceMetrics()
                formation.DumpData()
        with x ->
            (if self.keepData then formation.KeepData()
             reraise ())
