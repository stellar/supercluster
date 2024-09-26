// Copyright 2021 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarStatefulSets

open k8s
open k8s.Models
open Logging
open StellarFormation
open StellarDataDump
open StellarCoreSet
open StellarKubeSpecs
open StellarCorePeer
open StellarCoreHTTP
open StellarTransaction
open System
open System.Threading

type StellarFormation with

    member self.GetCoreSetForStatefulSet(ss: V1StatefulSet) =
        List.find (fun cs -> (self.NetworkCfg.StatefulSetName cs).StringName = ss.Name()) self.NetworkCfg.CoreSetList

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasReady(ss: V1StatefulSet) =
        let name = ss.Metadata.Name
        let ns = ss.Metadata.NamespaceProperty
        let fs = sprintf "metadata.name=%s" name
        let mutable forbiddenEvent = None

        // This pattern of a recursive handler-install routine that reinstalls
        // itself when `onClosed` fires is necessary because watches
        // automatically time out after 100 seconds and the connection closes.
        let rec installHandler () =
            async {
                LogInfo "Waiting for replicas on %s/%s" ns name

                // First we check to see if we've been woken up because a FailedCreate + forbidden
                // event occurred; this happens typically when we exceed quotas on a cluster or
                // some other policy reason.

                for ev in self.GetEventsForObject(name).Items do
                    if ev.Reason = "FailedCreate" && ev.Message.Contains("forbidden") then
                        // If so, we record the causal event and wake up the waiter.
                        forbiddenEvent <- Some(ev)

                match forbiddenEvent with
                | Some (ev) -> ()
                | None ->
                    self.sleepUntilNextRateLimitedApiCallTime ()

                    let s =
                        self
                            .Kube
                            .ListNamespacedStatefulSet(namespaceParameter = ns, fieldSelector = fs)
                            .Items.Item(0)
                    // Assuming we weren't failed, we look to see how the sts is doing in terms
                    // of creating the number of ready replicas we asked for.
                    let n = s.Status.ReadyReplicas.GetValueOrDefault(0)
                    let k = s.Spec.Replicas.GetValueOrDefault(0)
                    LogInfo "StatefulSet %s/%s: %d/%d replicas ready" ns name n k

                    if n <> k then
                        // Still need to wait a bit longer
                        do! Async.Sleep(3000)
                        return! installHandler ()
            }

        installHandler () |> Async.RunSynchronously

        match forbiddenEvent with
        | None -> ()
        | Some (ev) -> failwith (sprintf "Statefulset %s pod creation forbidden: %s" name ev.Message)

        self.LaunchLogTailingTasksForCoreSet(self.GetCoreSetForStatefulSet ss)

        LogInfo "All replicas on %s/%s ready" ns name

    // Watches the provided StatefulSet until the count of ready replicas equals the
    // count of configured replicas. This normally represents "successful startup".
    member self.WaitForAllReplicasOnAllSetsReady() =
        if not self.StatefulSets.IsEmpty then
            LogInfo "Waiting for replicas on %s" (self.ToString())

            for ss in self.StatefulSets do
                self.WaitForAllReplicasReady ss

            LogInfo "All replicas on %s ready" (self.ToString())

    member self.WithLive name (live: bool) =
        self.SetNetworkCfg(self.NetworkCfg.WithLive name live)
        let coreSet = self.NetworkCfg.FindCoreSet name
        let stsName = self.NetworkCfg.StatefulSetName coreSet
        self.sleepUntilNextRateLimitedApiCallTime ()

        let ss =
            self.Kube.ReplaceNamespacedStatefulSet(
                body = self.NetworkCfg.ToStatefulSet coreSet,
                name = stsName.StringName,
                namespaceParameter = self.NetworkCfg.NamespaceProperty
            )

        let newSets =
            self.StatefulSets
            |> List.filter (fun x -> x.Metadata.Name <> stsName.StringName)

        self.SetStatefulSets(ss :: newSets)
        self.WaitForAllReplicasReady ss

    member self.Start name = self.WithLive name true

    member self.Stop name = self.WithLive name false

    member self.WaitUntilReady() = self.NetworkCfg.EachPeer(fun p -> p.WaitUntilReady())

    member self.WaitUntilAllLiveSynced() = self.NetworkCfg.EachPeer(fun p -> p.WaitUntilSynced())

    member self.WaitUntilSynced(coreSetList: CoreSet list) =
        coreSetList
        |> List.iter
            (fun coreSet ->
                if coreSet.CurrentCount = 0 then
                    failwith ("Coreset " + coreSet.name.StringName + " is not live"))

        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.WaitUntilSynced())

    member self.WaitUntilConnected(coreSetList: CoreSet list) =
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.WaitUntilConnected)

    member self.EnsureAllNodesInSync(coreSetList: CoreSet list) =
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.EnsureInSync)

    member self.ManualClose(coreSetList: CoreSet list) =
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.ManualClose())

    // When upgrading multiple nodes, configure upgrade time a bit ahead to ensure nodes have enough
    // of a buffer to set upgrades
    member self.UpgradeProtocol (coreSetList: CoreSet list) (version: int) =
        let upgradeTime = System.DateTime.UtcNow.AddSeconds(15.0)
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.UpgradeProtocol version upgradeTime)
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForProtocol(version) |> ignore

    member self.ScheduleProtocolUpgrade (coreSetList: CoreSet list) (version: int) (upgradeTime: System.DateTime) =
        self.NetworkCfg.EachPeerInSets(coreSetList |> Array.ofList) (fun p -> p.UpgradeProtocol version upgradeTime)

    member self.UpgradeProtocolToLatest(coreSetList: CoreSet list) =
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        let latest = peer.GetSupportedProtocolVersion()
        self.UpgradeProtocol coreSetList latest

    member self.UpgradeMaxTxSetSize (coreSetList: CoreSet list) (maxTxSetSize: int) =
        let upgradeTime = System.DateTime.UtcNow.AddSeconds(15.0)

        self.NetworkCfg.EachPeerInSets
            (coreSetList |> Array.ofList)
            (fun p -> p.UpgradeMaxTxSetSize maxTxSetSize upgradeTime)

        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForMaxTxSetSize maxTxSetSize |> ignore

    member self.UpgradeSorobanMaxTxSetSize (coreSetList: CoreSet list) (maxTxSetSize: int) =
        self.NetworkCfg.EachPeerInSets
            (coreSetList |> Array.ofList)
            (fun p -> p.UpgradeSorobanMaxTxSetSize maxTxSetSize System.DateTime.UtcNow)

        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        peer.WaitForSorobanMaxTxSetSize maxTxSetSize |> ignore

    member self.UpgradeSorobanLedgerLimitsWithMultiplier (coreSetList: CoreSet list) (multiplier: int) =
        self.SetupUpgradeContract coreSetList.[0]
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0

        let expectedInstructions = peer.GetLedgerMaxInstructions() * int64 (multiplier)

        self.DeployUpgradeEntriesAndArm
            coreSetList
            { LoadGen.GetDefault() with
                  mode = CreateSorobanUpgrade
                  ledgerMaxInstructions = Some(expectedInstructions)
                  ledgerMaxReadBytes = Some(peer.GetLedgerReadBytes() * multiplier)
                  ledgerMaxWriteBytes = Some(peer.GetLedgerWriteBytes() * multiplier)
                  ledgerMaxTxCount = Some(peer.GetSorobanMaxTxSetSize() * multiplier)
                  ledgerMaxReadLedgerEntries = Some(peer.GetLedgerReadEntries() * multiplier)
                  ledgerMaxWriteLedgerEntries = Some(peer.GetLedgerWriteEntries() * multiplier)
                  ledgerMaxTransactionsSizeBytes = Some(peer.GetLedgerMaxTransactionsSizeBytes() * multiplier) }
            (System.DateTime.UtcNow)

        peer.WaitForLedgerMaxInstructions expectedInstructions |> ignore

    member self.UpgradeSorobanTxLimitsWithMultiplier (coreSetList: CoreSet list) (multiplier: int) =
        self.SetupUpgradeContract coreSetList.[0]
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0

        let expectedInstructions = peer.GetTxMaxInstructions() * int64 (multiplier)

        self.DeployUpgradeEntriesAndArm
            coreSetList
            { LoadGen.GetDefault() with
                  mode = CreateSorobanUpgrade
                  txMaxInstructions = Some(expectedInstructions)
                  txMaxReadBytes = Some(peer.GetTxReadBytes() * multiplier)
                  txMaxWriteBytes = Some(peer.GetTxWriteBytes() * multiplier)
                  txMaxReadLedgerEntries = Some(peer.GetTxReadEntries() * multiplier)
                  txMaxWriteLedgerEntries = Some(peer.GetTxWriteEntries() * multiplier)
                  txMaxSizeBytes = Some(peer.GetMaxTxSize() * multiplier)
                  txMemoryLimit = Some(peer.GetTxMemoryLimit() * multiplier)
                  maxContractSizeBytes = Some(peer.GetMaxContractSize() * multiplier)
                  maxContractDataKeySizeBytes = Some(peer.GetMaxContractDataKeySize() * multiplier)
                  maxContractDataEntrySizeBytes = Some(peer.GetMaxContractDataEntrySize() * multiplier)
                  txMaxContractEventsSizeBytes = Some(peer.GetTxMaxContractEventsSize() * multiplier) }
            (System.DateTime.UtcNow)

        peer.WaitForTxMaxInstructions expectedInstructions |> ignore

    member self.ReportStatus() = ReportAllPeerStatus self.NetworkCfg

    member self.CreateAccount (coreSet: CoreSet) (u: Username) =
        let peer = self.NetworkCfg.GetPeer coreSet 0
        let tx = peer.TxCreateAccount u
        LogInfo "creating account for %O on %O" u self
        peer.SubmitSignedTransaction tx |> ignore
        peer.WaitForNextLedger() |> ignore
        let acc = peer.GetAccount(u)

        LogInfo
            "created account for %O on %O with seq %d, balance %d"
            u
            self
            acc.SequenceNumber
            (peer.GetTestAccBalance(u.ToString()))

    member self.Pay (coreSet: CoreSet) (src: Username) (dst: Username) =
        let peer = self.NetworkCfg.GetPeer coreSet 0
        let tx = peer.TxPayment src dst
        let seq = peer.GetTestAccSeq(src.ToString())

        LogInfo "paying from account %O to %O on %O" src dst self
        peer.SubmitSignedTransaction tx |> ignore
        peer.WaitForNextSeq(src.ToString()) seq |> ignore

        LogInfo
            "sent payment from %O (%O) to %O (%O) on %O"
            src
            (peer.GetSeqAndBalance src)
            dst
            (peer.GetSeqAndBalance dst)
            self

    member self.CheckNoErrorsAndPairwiseConsistency() =
        let cs = List.filter (fun cs -> cs.live = true) self.NetworkCfg.CoreSetList

        if not (List.isEmpty cs) then
            let peer = self.NetworkCfg.GetPeer cs.[0] 0

            self.NetworkCfg.EachPeer
                (fun p ->
                    // REVERTME: Temporarily disable abnormal-event checking
                    // self.CheckNoAbnormalKubeEvents p
                    p.CheckNoErrorMetrics(includeTxInternalErrors = false)
                    p.CheckConsistencyWith peer)

    member self.CheckUsesLatestProtocolVersion() = self.NetworkCfg.EachPeer(fun p -> p.CheckUsesLatestProtocolVersion())

    member self.RunLoadgen (coreSet: CoreSet) (loadGen: LoadGen) =
        let peer = self.NetworkCfg.GetPeer coreSet 0
        LogInfo "Loadgen: %s" (peer.GenerateLoad loadGen)
        peer.WaitForLoadGenComplete loadGen

    member self.SetupUpgradeContract(coreSet: CoreSet) =
        let loadgen =
            { LoadGen.GetDefault() with
                  mode = SetupSorobanUpgrade
                  minSorobanPercentSuccess = Some 100 }

        self.RunLoadgen coreSet loadgen

    member self.DeployUpgradeEntriesAndArm
        (coreSetList: CoreSet list)
        (loadGen: LoadGen)
        (upgradeTime: System.DateTime)
        =
        let peer = self.NetworkCfg.GetPeer coreSetList.[0] 0
        let resStr = peer.GenerateLoad loadGen

        let contractKey = Loadgen.Parse(resStr).ConfigUpgradeSetKey

        LogInfo "Loadgen: %s" resStr
        peer.WaitForLoadGenComplete loadGen

        // Arm upgrades on each peer in the core set
        self.NetworkCfg.EachPeerInSets
            (List.toArray coreSetList)
            (fun peer -> peer.UpgradeNetworkSetting contractKey upgradeTime)

    member self.clearMetrics(coreSets: CoreSet list) =
        self.NetworkCfg.EachPeerInSets(coreSets |> List.toArray) (fun peer -> peer.ClearMetrics())

    // This is similar to RunLoadgen but runs a 1/N fractional portion of a
    // given LoadGen on node 0 of each of N CoreSets.
    member self.RunMultiLoadgen (coreSets: CoreSet list) (fullLoadGen: LoadGen) =
        let n = List.length coreSets

        let fractionalLoadGen (i: int) : LoadGen =
            let getFraction attr = if i + 1 = n then (attr / n + attr % n) else attr / n

            { fullLoadGen with
                  accounts = fullLoadGen.accounts / n
                  txs = fullLoadGen.txs / n
                  spikesize = getFraction fullLoadGen.spikesize
                  txrate = getFraction fullLoadGen.txrate }

        let loadGenPeers = List.map (fun cs -> self.NetworkCfg.GetPeer cs 0) coreSets

        for (i, peer) in (List.indexed loadGenPeers) do
            let loadGen = fractionalLoadGen i
            let offset = loadGen.accounts * i
            let peerSpecificLoadgen = { loadGen with offset = offset }
            LogInfo "Loadgen: %s with offset %d" (peer.GenerateLoad peerSpecificLoadgen) offset

        while List.exists
                  (fun (peer: Peer) -> not (peer.IsLoadGenComplete() = Success || peer.IsLoadGenComplete() = Failure))
                  loadGenPeers do
            Thread.Sleep(millisecondsTimeout = 3000)

            for (i, peer) in (List.indexed loadGenPeers) do
                peer.LogLoadGenProgressTowards(fractionalLoadGen i)

            // Check if any loadGen has failed
            if List.exists (fun (peer: Peer) -> peer.IsLoadGenComplete() = Failure) loadGenPeers then
                failwith "Loadgen failed!"
