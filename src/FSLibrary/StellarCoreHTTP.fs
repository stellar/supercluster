// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreHTTP

open FSharp.Data
open stellar_dotnet_sdk
open StellarCoreSet
open PollRetry
open Logging
open StellarMissionContext
open StellarNetworkCfg
open StellarCorePeer
open System.Threading


// Note to curious reader: these are "type providers" whereby the compiler's
// type definition facility is extended by plugins that are themselves
// parameterized by literals that refer to external sources. In this case
// FSharp.Data.JsonProvider<> takes a sample of some JSON you want to load and
// infers a static type for it.

// This exists to work around a buggy interaction between FSharp.Data and
// dotnet 5.0.300: __SOURCE_DIRECTORY__ is not [<Literal>] anymore, but
// JsonProvider's ResolutionFolder argument needs to be.
[<Literal>]
let cwd = __SOURCE_DIRECTORY__

type Metrics = JsonProvider<"json-type-samples/sample-metrics.json", SampleIsList=true, ResolutionFolder=cwd>
type Info = JsonProvider<"json-type-samples/sample-info.json", SampleIsList=true, ResolutionFolder=cwd>
type TestAcc = JsonProvider<"json-type-samples/sample-testacc.json", SampleIsList=true, ResolutionFolder=cwd>
type Tx = JsonProvider<"json-type-samples/sample-tx.json", SampleIsList=true, ResolutionFolder=cwd>
type PerformanceCsv = CsvProvider<"csv-type-samples/sample-performance.csv", HasHeaders=true, ResolutionFolder=cwd>

type LoadGenMode =
    | GenerateAccountCreationLoad
    | GeneratePaymentLoad
    | GeneratePretendLoad

    override self.ToString() =
        match self with
        | GenerateAccountCreationLoad -> "create"
        | GeneratePaymentLoad -> "pay"
        | GeneratePretendLoad -> "pretend"


type LoadGen =
    { mode: LoadGenMode
      accounts: int
      txs: int
      spikesize: int
      spikeinterval: int
      txrate: int
      offset: int
      batchsize: int }

    member self.ToQuery : (string * string) list =
        [ ("mode", self.mode.ToString())
          ("accounts", self.accounts.ToString())
          ("txs", self.txs.ToString())
          ("txrate", self.txrate.ToString())
          ("spikesize", self.spikesize.ToString())
          ("spikeinterval", self.spikeinterval.ToString())
          ("batchsize", self.batchsize.ToString())
          ("offset", self.offset.ToString()) ]

type MissionContext with

    member self.WithNominalLoad : MissionContext = { self with numTxs = 100; numAccounts = 100 }

    member self.GenerateAccountCreationLoad : LoadGen =
        { mode = GenerateAccountCreationLoad
          accounts = self.numAccounts
          txs = 0
          spikesize = 0
          spikeinterval = 0
          // Use conservative rate for account creation, as the network may quickly get overloaded
          txrate = 5
          offset = 0
          batchsize = 100 }

    member self.GeneratePaymentLoad : LoadGen =
        { mode = GeneratePaymentLoad
          accounts = self.numAccounts
          txs = self.numTxs
          txrate = self.txRate
          spikesize = self.spikeSize
          spikeinterval = self.spikeInterval
          offset = 0
          batchsize = 100 }

    member self.GeneratePretendLoad : LoadGen =
        { mode = GeneratePretendLoad
          accounts = self.numAccounts
          txs = self.numTxs
          txrate = self.txRate
          spikesize = self.spikeSize
          spikeinterval = self.spikeInterval
          offset = 0
          batchsize = 100 }


let DefaultAccountCreationLoadGen =
    { mode = GenerateAccountCreationLoad
      accounts = 1000
      txs = 0
      spikesize = 0
      spikeinterval = 0
      txrate = 10
      offset = 0
      batchsize = 100 }


type UpgradeParameters =
    { upgradeTime: System.DateTime
      protocolVersion: Option<int>
      baseFee: Option<int>
      maxTxSetSize: Option<int>
      baseReserve: Option<int> }

    member self.ToQuery : (string * string) list =
        let maybe name opt = Option.toList (Option.map (fun v -> (name, v.ToString())) opt)

        List.concat [| [ ("mode", "set") ]
                       [ ("upgradetime", self.upgradeTime.ToUniversalTime().ToString("o")) ]
                       maybe "protocolversion" self.protocolVersion
                       maybe "basefee" self.baseFee
                       maybe "basereserve" self.baseReserve
                       maybe "maxtxsetsize" self.maxTxSetSize |]

let DefaultUpgradeParameters =
    { upgradeTime = System.DateTime.Parse("1970-01-01T00:00:00Z")
      protocolVersion = None
      baseFee = None
      maxTxSetSize = None
      baseReserve = None }


let MeterCountOr (def: int) (m: Option<Metrics.GenericMeter>) : int =
    match m with
    | Some (n) -> n.Count
    | None -> def


let ConsistencyCheckIterationCount : int = 5

exception PeerRejectedUpgradesException of string
exception InconsistentPeersException of (Peer * Peer)
exception MaybeInconsistentPeersException of (Peer * Peer)
exception PeerHasNonzeroErrorMetricsException of (Peer * string * int)
exception TransactionRejectedException of Transaction
exception ProtocolVersionNotUpgradedException of (int * int)
exception NodeLostSyncException of string

type Peer with

    member self.Headers =
        let host = self.networkCfg.IngressInternalHostName
        [ HttpRequestHeaders.Host host ]

    member self.URL(path: string) : string =
        sprintf
            "http://%s:%d/%s/core/%s"
            self.networkCfg.IngressExternalHostName
            self.networkCfg.missionContext.ingressExternalPort
            self.PodName.StringName
            path

    member self.fetch(path: string) : string =
        let url = self.URL path
        Http.RequestString(url, headers = self.Headers)

    member self.GetState() =
        WebExceptionRetry DefaultRetry (fun _ -> Info.Parse(self.fetch "info").Info.State)

    member self.GetStatusOrState() : string =
        WebExceptionRetry
            DefaultRetry
            (fun _ ->
                let i = Info.Parse(self.fetch "info").Info
                if i.Status.Length = 0 then i.State else i.Status.[0])

    member self.GetMetrics() : Metrics.Metrics =
        WebExceptionRetry DefaultRetry (fun _ -> Metrics.Parse(self.fetch "metrics").Metrics)

    member self.GetInfo() : Info.Info = WebExceptionRetry DefaultRetry (fun _ -> Info.Parse(self.fetch "info").Info)

    member self.GetLedgerNum() : int = self.GetInfo().Ledger.Num

    member self.GetMaxTxSetSize() : int = self.GetInfo().Ledger.MaxTxSetSize

    member self.GetLedgerProtocolVersion() : int = self.GetInfo().Ledger.Version

    member self.GetSupportedProtocolVersion() : int = self.GetInfo().ProtocolVersion

    member self.SetUpgrades(upgrades: UpgradeParameters) =
        let res =
            WebExceptionRetry
                DefaultRetry
                (fun _ ->
                    Http.RequestString(
                        httpMethod = "GET",
                        url = self.URL "upgrades",
                        headers = self.Headers,
                        query = upgrades.ToQuery
                    ))

        if res.ToLower().Contains("exception") then
            raise (PeerRejectedUpgradesException res)

    member self.UpgradeProtocol (version: int) (time: System.DateTime) =
        let upgrades =
            { DefaultUpgradeParameters with
                  protocolVersion = Some(version)
                  upgradeTime = time }

        self.SetUpgrades(upgrades)

    member self.UpgradeProtocolToLatest(time: System.DateTime) =
        let upgrades =
            { DefaultUpgradeParameters with
                  protocolVersion = Some(self.GetSupportedProtocolVersion())
                  upgradeTime = time }

        self.SetUpgrades(upgrades)

    member self.UpgradeMaxTxSetSize (txSetSize: int) (time: System.DateTime) =
        let upgrades =
            { DefaultUpgradeParameters with
                  maxTxSetSize = Some(txSetSize)
                  upgradeTime = time }

        self.SetUpgrades(upgrades)

    member self.WaitForLedgerNum(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetLedgerNum() >= n)
            (fun _ -> LogInfo "Waiting for ledger %d on %s: %s" n self.ShortName.StringName (self.GetStatusOrState()))

    member self.WaitForFewLedgers(count: int) = self.WaitForLedgerNum(self.GetLedgerNum() + count)

    member self.WaitForNextLedger() = self.WaitForFewLedgers(1)

    member self.WaitForProtocol(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetLedgerProtocolVersion() = n)
            (fun _ -> LogInfo "Waiting for protocol %d on %s" n self.ShortName.StringName)

    member self.WaitForLatestProtocol() =
        let latest = self.GetSupportedProtocolVersion()
        self.WaitForProtocol latest

    member self.WaitForMaxTxSetSize(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetMaxTxSetSize() = n)
            (fun _ -> LogInfo "Waiting for MaxTxSetSize=%d on %s" n self.ShortName.StringName)

    member self.WaitForNextSeq (src: string) (n: int64) =
        let desiredSeq = n + int64 (1)

        RetryUntilTrue
            (fun _ -> self.GetTestAccSeq(src) >= desiredSeq)
            (fun _ -> LogInfo "Waiting for seqnum=%d for account %s" desiredSeq src)

    member self.CheckNoErrorMetrics(includeTxInternalErrors: bool) =
        let raiseIfNonzero (c: int) (n: string) = if c <> 0 then raise (PeerHasNonzeroErrorMetricsException(self, n, c))
        let m : Metrics.Metrics = self.GetMetrics()
        raiseIfNonzero m.ScpEnvelopeInvalidsig.Count "scp.envelope.invalidsig"
        raiseIfNonzero m.HistoryPublishFailure.Count "history.publish.failure"
        raiseIfNonzero m.LedgerInvariantFailure.Count "ledger.invariant.failure"

        if includeTxInternalErrors then
            raiseIfNonzero m.LedgerTransactionInternalError.Count "ledger.transaction.internal-error"

        LogInfo "No errors found on %s" self.ShortName.StringName

    member self.CheckConsistencyWith(other: Peer) =
        let shortHash (v: string) : string = v.Remove 6

        let rec loop (ours: Map<int, string>) (theirs: Map<int, string>) (n: int) =
            if n < 0 then
                raise (MaybeInconsistentPeersException(self, other))
            else

            // Sleep to allow ledgers to close. No need to sleep on the first iteration
            if Map.exists
                (fun k v ->
                    match theirs.TryFind k with
                    | None -> false
                    | Some w when v = w ->
                        LogInfo
                            "found agreeing ledger %d = %s on %s and %s"
                            k
                            (shortHash v)
                            self.ShortName.StringName
                            other.ShortName.StringName

                        true
                    | Some w ->
                        LogError
                            "Inconsistent peers: ledger %d = %s on %s and %s on %s"
                            k
                            (shortHash v)
                            self.ShortName.StringName
                            (shortHash w)
                            other.ShortName.StringName

                        raise (InconsistentPeersException(self, other)))
                ours then
                ()
            else

                // Sleep to allow ledgers to close. No need to sleep on the first iteration
                if n < ConsistencyCheckIterationCount then
                    // Sleep for 1 second when using accelerateTime, and 5 seconds otherwise
                    // because it does not make much sense to check every 1 second
                    // if the network closes ledgers every 5 seconds.
                    Thread.Sleep(if self.coreSet.options.accelerateTime then 1000 else 5000)

                let ensureSyncedIfTier1 p =
                    if p.coreSet.options.tier1 = Some true && p.GetState() <> "Synced!" then
                        (NodeLostSyncException p.ShortName.StringName) |> raise

                ensureSyncedIfTier1 self
                ensureSyncedIfTier1 other

                let ourLedger = self.GetInfo().Ledger
                let theirLedger = other.GetInfo().Ledger

                loop
                    (Map.add ourLedger.Num ourLedger.Hash ours)
                    (Map.add theirLedger.Num theirLedger.Hash theirs)
                    (n - 1)

        loop Map.empty Map.empty ConsistencyCheckIterationCount

    member self.CheckUsesLatestProtocolVersion() =
        let lastestProtocolVersion = self.GetSupportedProtocolVersion()
        let currentProtocolVersion = self.GetLedgerProtocolVersion()

        if lastestProtocolVersion <> currentProtocolVersion then
            raise (ProtocolVersionNotUpgradedException(currentProtocolVersion, lastestProtocolVersion))

    member self.ClearMetrics() =
        WebExceptionRetry
            DefaultRetry
            (fun _ -> Http.RequestString(httpMethod = "GET", headers = self.Headers, url = self.URL "clearmetrics"))
        |> ignore

    member self.GetTestAcc(accName: string) : TestAcc.Root =
        // NB: work around buggy JSON parser upstream, see
        // https://github.com/fsharp/FSharp.Data/pull/1262
        let s =
            WebExceptionRetry
                DefaultRetry
                (fun _ ->
                    Http.RequestString(
                        httpMethod = "GET",
                        url = self.URL("testacc"),
                        headers = self.Headers,
                        query = [ ("name", accName) ]
                    ))

        TestAcc.Parse(if s.Trim().StartsWith("null") then "{}" else s)

    member self.GetTestAccBalance(accName: string) : int64 =
        RetryUntilSome
            (fun _ -> self.GetTestAcc(accName).Balance)
            (fun _ -> LogWarn "Waiting for account %s to exist, to read balance" accName)

    member self.GetTestAccSeq(accName: string) : int64 =
        RetryUntilSome
            (fun _ -> self.GetTestAcc(accName).Seqnum)
            (fun _ -> LogWarn "Waiting for account %s to exist, to read seqnum" accName)

    member self.GenerateLoad(loadGen: LoadGen) : string =
        WebExceptionRetry
            DefaultRetry
            (fun _ ->
                Http.RequestString(
                    httpMethod = "GET",
                    headers = self.Headers,
                    url = self.URL "generateload",
                    query = loadGen.ToQuery
                ))

    member self.ManualClose() =
        WebExceptionRetry
            DefaultRetry
            (fun _ -> Http.RequestString(httpMethod = "GET", headers = self.Headers, url = self.URL "manualclose"))
        |> ignore

    member self.SubmitSignedTransaction(tx: Transaction) : Tx.Root =
        let b64 = tx.ToEnvelopeXdrBase64()
        let uri = (self.URL "tx") + "?blob=" + (System.Uri.EscapeDataString b64)

        let s =
            WebExceptionRetry
                DefaultRetry
                (fun _ ->
                    // Work around buggy URI-escaping upstream,
                    // see https://github.com/fsharp/FSharp.Data/issues/1263
                    LogDebug "Submitting transaction: %s" uri
                    let req = System.Net.WebRequest.CreateHttp uri
                    req.Method <- "GET"
                    let hdrs = System.Net.WebHeaderCollection()
                    hdrs.Add(System.Net.HttpRequestHeader.Host, self.networkCfg.IngressInternalHostName)
                    req.Headers <- hdrs
                    use stream = req.GetResponse().GetResponseStream()
                    use reader = new System.IO.StreamReader(stream)
                    reader.ReadToEnd())

        LogDebug "Transaction response: %s" s
        let res = Tx.Parse(s)

        if res.Status <> "PENDING" then
            LogError "Transaction result %s" res.Status

            match res.Error with
            | None -> ()
            | Some (r) ->
                let txr = responses.TransactionResult.FromXdr(r)
                LogError "Result details: %O" txr

            raise (TransactionRejectedException tx)
        else
            res

    member self.LogCoreSetListStatusWithTiers(coreSetList: CoreSet list) : unit =
        let tier1CoreSetList = List.filter (fun cs -> cs.options.tier1 = Some true) coreSetList
        let nonTier1CoreSetList = List.filter (fun cs -> cs.options.tier1 = Some false) coreSetList

        let getStateList (cs: CoreSet) =
            Seq.map (fun i -> (self.networkCfg.GetPeer cs i).GetState()) (seq { 0 .. (cs.options.nodeCount - 1) })
            |> Seq.toList

        let tier1StatusList = List.map getStateList tier1CoreSetList |> List.concat
        let nonTier1StatusList = List.map getStateList nonTier1CoreSetList |> List.concat

        let countAndLogStates ls nodeType =
            List.sort ls
            |> Seq.countBy id
            |> Seq.iter (fun x -> LogInfo "%d %s nodes have state %s" (snd x) nodeType (fst x))

        countAndLogStates tier1StatusList "tier1"
        countAndLogStates nonTier1StatusList "non-tier1"

    member self.IsLoadGenComplete() =
        if self.GetState() <> "Synced!" then
            (NodeLostSyncException self.ShortName.StringName) |> raise

        let m = self.GetMetrics()

        if (MeterCountOr 0 m.LoadgenRunFailed) <> 0 then
            failwith "Loadgen failed"
        else
            (MeterCountOr 0 m.LoadgenRunStart) = (MeterCountOr 0 m.LoadgenRunComplete)

    member self.LogLoadGenProgressTowards(loadGen: LoadGen) =
        let m = self.GetMetrics()

        LogInfo
            "Waiting for loadgen run %d on %s to finish, %d/%d accts, %d/%d txns"
            (MeterCountOr 0 m.LoadgenRunStart)
            self.ShortName.StringName
            (MeterCountOr 0 m.LoadgenAccountCreated)
            loadGen.accounts
            (MeterCountOr 0 m.LoadgenTxnAttempted)
            loadGen.txs

    member self.WaitForLoadGenComplete(loadGen: LoadGen) =
        RetryUntilTrue
            (fun _ -> self.IsLoadGenComplete())
            (fun _ ->
                self.LogLoadGenProgressTowards loadGen
                self.LogCoreSetListStatusWithTiers self.networkCfg.CoreSetList)

    // WaitUntilConnected waits until every node is connected to all the nodes in
    // its preferredPeersMap. This ensures that the simulation is deterministic.
    member self.WaitUntilConnected =
        let desiredNumberOfConnection =
            match self.coreSet.options.preferredPeersMap with
            | None -> failwith "preferredPeersMap is needed to determine # of desired connections"
            | Some map ->
                let preferredPeers = Map.find (self.coreSet.keys.[self.peerNum].PublicKey) map
                List.length preferredPeers

        RetryUntilTrue
            (fun _ -> self.GetInfo().Peers.AuthenticatedCount >= desiredNumberOfConnection)
            (fun _ ->
                LogInfo
                    "Waiting until %s is connected: currently %d connections, want at least %d connections"
                    self.ShortName.StringName
                    (self.GetInfo().Peers.AuthenticatedCount)
                    desiredNumberOfConnection)

    member self.EnsureInSync =
        if self.GetState() <> "Synced!" then
            failwith (sprintf "%s is in state %s" (self.ShortName.StringName) (self.GetState()))

    member self.WaitUntilReady() =
        RetryUntilTrue
            (fun _ -> self.GetState() <> "Booting")
            (fun _ -> LogInfo "Waiting until %s is ready: %s" self.ShortName.StringName (self.GetStatusOrState()))

    member self.WaitUntilSynced() =
        RetryUntilTrue
            (fun _ -> self.GetState() = "Synced!")
            (fun _ -> LogInfo "Waiting until %s is synced: %s" self.ShortName.StringName (self.GetStatusOrState()))

    member self.WaitForAuthenticatedPeers(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetInfo().Peers.AuthenticatedCount >= n)
            (fun _ -> LogInfo "Waiting until %s has >= %d authenticated peers" self.ShortName.StringName n)

let ReportAllPeerStatus (nCfg: NetworkCfg) =
    nCfg.EachPeer
        (fun (p: Peer) ->
            let info = p.GetInfo()
            let metrics = p.GetMetrics()

            LogInfo
                "Peer '%s' startedOn '%s', state '%s', overlay reading %.1f bytes/sec"
                p.ShortName.StringName
                (info.StartedOn.ToString())
                info.State
                metrics.OverlayByteRead.MeanRate)
