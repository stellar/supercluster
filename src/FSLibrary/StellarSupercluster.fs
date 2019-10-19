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
    let mutable jobNumber = 0

    member self.NextJobNum : int =
        jobNumber <- jobNumber + 1
        jobNumber

    member self.AllJobNums : int list =
        [ 1 .. jobNumber ]

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
        use event = new System.Threading.ManualResetEventSlim(false)

        // This pattern of a recursive handler-install routine that reinstalls
        // itself when `onClosed` fires is necessary because watches
        // automatically time out after 100 seconds and the connection closes.
        let rec installHandler() =
            LogInfo "Waiting for replicas on %s/%s" ns name
            let handler (ety:WatchEventType) (ss:V1StatefulSet) =
                LogInfo "Saw event for statefulset %s: %s" name (ety.ToString())
                if not event.IsSet
                then
                    let n = ss.Status.ReadyReplicas.GetValueOrDefault(0)
                    let k = ss.Spec.Replicas.GetValueOrDefault(0)
                    LogInfo "StatefulSet %s/%s: %d/%d replicas ready" ns name n k;
                    if n = k then event.Set()
            let action = System.Action<WatchEventType, V1StatefulSet>(handler)
            let reinstall = System.Action(installHandler)
            if not event.IsSet
            then kube.WatchNamespacedStatefulSetAsync(name = name,
                                                      ``namespace`` = ns,
                                                      onEvent = action,
                                                      onClosed = reinstall) |> ignore
        installHandler()
        event.Wait() |> ignore
        LogInfo "All replicas on %s/%s ready" ns name

    member self.WatchJob (j:V1Job) : (System.Threading.WaitHandle * bool ref) =
        let name = j.Metadata.Name
        let ns = j.Metadata.NamespaceProperty
        let ok = ref false
        let event = new System.Threading.ManualResetEventSlim(false)
        // As the handler gets repeatedly reinstalled, it's called to re-handle
        // all the same events it's already seen, so we have to keep a refcell
        // with the maximum event it's already handled.
        let handledEv = ref 0
        let rec installHandler (firstWait:bool) =
            if firstWait
            then LogInfo "Waiting for job %s" name
            else LogInfo "Continuing to wait for job %s" name
            let nextEv = ref 0
            let handler (ety:WatchEventType) (job:V1Job) =
                nextEv := !nextEv + 1
                if (not event.IsSet) && (!nextEv > !handledEv)
                then
                    LogInfo "Saw event for job %s: %s" name (ety.ToString())
                    handledEv := !nextEv
                    if ety.Equals(WatchEventType.Modified)
                    then
                        let jobActive = job.Status.Active.GetValueOrDefault(0)
                        if jobActive = 0
                        then
                            let jobSucceeded = job.Status.Succeeded.GetValueOrDefault(0)
                            let jobFailed = job.Status.Failed.GetValueOrDefault(0)
                            LogInfo "Finished job %s: %d fail / %d success"
                                    name jobFailed jobSucceeded
                            ok := (jobFailed = 0) && (jobSucceeded = 1)
                            event.Set() |> ignore

            let action = System.Action<WatchEventType, V1Job>(handler)
            let reinstall = System.Action((fun _ -> installHandler false))
            if not event.IsSet
            then kube.WatchNamespacedJobAsync(name = name,
                                              ``namespace`` = ns,
                                              onEvent = action,
                                              onClosed = reinstall) |> ignore
        installHandler true
        (event.WaitHandle, ok)

    member self.RunParallelJobsInRandomOrder (parallelism:int)
                                             (allJobs:((string array) array)) : Map<string,bool> =
        let jobArr = Array.copy allJobs
        let shuffle (arr:'a array) =
            let rng = System.Random()
            let rnd _ = rng.Next(arr.Length)
            let swap i j =
                let tmp = arr.[i]
                arr.[i] <- arr.[j]
                arr.[j] <- tmp
            Array.iteri (fun i _ -> swap i (rnd())) arr
        shuffle jobArr
        let mutable jobQueue = Array.toList jobArr
        self.RunParallelJobs parallelism
            (fun _ -> (match jobQueue with
                       | [] -> None
                       | head::tail -> jobQueue <- tail
                                       Some head))


    member self.RunParallelJobs (parallelism:int)
                                (nextJob:(unit->(string array) option)) : Map<string,bool> =
        let mutable running = Map.empty
        let mutable finished = Map.empty
        let rec loop _ =
            if running.Count < parallelism
            then addJob()
            else waitJob()

        and addJob _ =
            match nextJob() with
                 | None ->
                     if running.IsEmpty
                     then finished
                     else waitJob()
                 | Some(cmd) ->
                    let j = self.StartJobForCmd cmd
                    let name = j.Metadata.Name
                    let (waitHandle, ok) = self.WatchJob j
                    running <- running.Add(name, (j, waitHandle, ok))
                    loop()

        and waitJob _ =
            let triples = Map.toArray running
            let handles = Array.map (fun (_, (_, handle, _)) -> handle) triples
            let i = System.Threading.WaitHandle.WaitAny(handles)
            let (n, (j, _, ok)) = triples.[i]
            if !ok
            then LogInfo "Job %s passed" n
            else LogInfo "Job %s failed" n
            finished <- finished.Add(n, (!ok))
            running <- running.Remove n
            self.FinishJob j
            loop()

        let oldConfig = networkCfg
        let c = parallelism * networkCfg.quotas.NumConcurrentMissions
        networkCfg <- { networkCfg with
                            quotas = { networkCfg.quotas with
                                           NumConcurrentMissions = c } }
        let res = loop()
        networkCfg <- oldConfig
        res

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasOnAllSetsReady() =
        if not statefulSets.IsEmpty
        then
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

    member self.StartJob (j:V1Job) : V1Job =
        try
            let ns = networkCfg.NamespaceProperty
            let j = self.Kube.CreateNamespacedJob(body=j,
                                                  namespaceParameter = ns)
            namespaceContent.Add(j)
            j
        with
        | :? HttpOperationException as w ->
            LogError "err: %s" w.Message
            LogError "err: %s" w.Response.Content
            LogError "err: %s" w.Response.ReasonPhrase
            reraise()

    member self.AddPersistentVolumeClaim (pvc:V1PersistentVolumeClaim) : unit =
        let ns = networkCfg.NamespaceProperty
        let claim = self.Kube.CreateNamespacedPersistentVolumeClaim(body = pvc,
                                                                    namespaceParameter = ns)
        namespaceContent.Add(claim)

    member self.AddDynamicPersistentVolumeClaimForJob (n:int) : unit =
        let pvc = self.NetworkCfg.ToDynamicPersistentVolumeClaimForJob n
        self.AddPersistentVolumeClaim pvc

    member self.StartJobForCmd (cmd:string array) : V1Job =
        let jobNum = self.NextJobNum
        self.AddDynamicPersistentVolumeClaimForJob jobNum
        self.StartJob (networkCfg.GetJobFor jobNum cmd)

    member self.FinishJob (j:V1Job) : unit =
        // We mop up the PVCs here after each job finishes to avoid keeping
        // huge amounts of idle EBS storage allocated.
        let pvc = self.NetworkCfg.ToDynamicPersistentVolumeClaim j.Metadata.Name
        namespaceContent.Del(pvc)

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

            if not (List.isEmpty statefulSets)
            then
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

