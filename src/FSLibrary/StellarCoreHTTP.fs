// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreHTTP

open FSharp.Data
open FSharp.Data.JsonExtensions
open stellar_dotnet_sdk

open PollRetry
open Logging
open StellarNetworkCfg
open StellarCorePeer


// Note to curious reader: these are "type providers" whereby the compiler's
// type definition facility is extended by plugins that are themselves
// parameterized by literals that refer to external sources. In this case
// FSharp.Data.JsonProvider<> takes a sample of some JSON you want to load and
// infers a static type for it.

type Metrics = JsonProvider<"json-type-samples/sample-metrics.json", SampleIsList=true>
type Info = JsonProvider<"json-type-samples/sample-info.json">
type TestAcc = JsonProvider<"json-type-samples/sample-testacc.json", SampleIsList=true>
type Tx = JsonProvider<"json-type-samples/sample-tx.json", SampleIsList=true>


type LoadGenMode =
    | GenerateAccountCreationLoad
    | GeneratePaymentLoad

    override self.ToString() =
       match self with
        | GenerateAccountCreationLoad -> "create"
        | GeneratePaymentLoad -> "pay"


type LoadGen =
    { mode: LoadGenMode
      accounts: int
      txs: int
      txrate: int
      offset: int
      batchsize: int }

    member self.ToQuery : (string*string) list =
        [
           ("mode", self.mode.ToString())
           ("accounts", self.accounts.ToString());
           ("txs", self.txs.ToString());
           ("txrate", self.txrate.ToString());
           ("offset", self.offset.ToString());
           ("batchsize", self.batchsize.ToString());
        ]


let DefaultAccountCreationLoadGen =
    { mode = GenerateAccountCreationLoad
      accounts = 1000
      txs = 0
      txrate = 10
      offset = 0
      batchsize = 100 }


type UpgradeParameters =
    { upgradeTime: System.DateTime
      protocolVersion: Option<int>
      baseFee: Option<int>
      maxTxSize: Option<int>
      baseReserve: Option<int> }

    member self.ToQuery : (string*string) list =
        let maybe name opt = Option.toList (Option.map (fun v -> (name, v.ToString())) opt)
        List.concat
            [|
                [("mode", "set")];
                [("upgradetime", self.upgradeTime.ToUniversalTime().ToString("o"))];
                maybe "protocolversion" self.protocolVersion;
                maybe "basefee" self.baseFee;
                maybe "basereserve" self.baseReserve;
                maybe "maxtxsize" self.maxTxSize;
            |]

let DefaultUpgradeParameters =
    { upgradeTime = System.DateTime.Parse("1970-01-01T00:00:00Z")
      protocolVersion = None
      baseFee = None
      maxTxSize = None
      baseReserve = None }


let MeterCountOr (def:int) (m:Option<Metrics.GenericMeter>) : int =
    match m with
        | Some(n) -> n.Count
        | None -> def


let ConsistencyCheckIterationCount : int = 5

exception PeerRejectedUpgrades of string
exception InconsistentPeers of (Peer * Peer)
exception MaybeInconsistentPeers of (Peer * Peer)
exception PeerHasNonzeroErrorMetrics of (Peer * string * int)
exception TransactionRejected of Transaction


type Peer with

    member self.URL (path:string) : string =
        sprintf "http://localhost:8080/%s/%s/%s"
            (self.networkCfg.networkNonce.ToString())
            self.ShortName
            path

    member self.GetMetrics : Metrics.Metrics =
        WebExceptionRetry DefaultRetry
            (fun _ -> Metrics.Load(self.URL "metrics").Metrics)

    member self.GetInfo : Info.Info =
        WebExceptionRetry DefaultRetry
            (fun _ -> Info.Load(self.URL "info").Info)

    member self.GetLedgerNum : int =
        self.GetInfo.Ledger.Num

    member self.GetProtocolVersion : int =
        self.GetInfo.ProtocolVersion

    member self.SetUpgrades (upgrades:UpgradeParameters) =
        let res =
            WebExceptionRetry DefaultRetry
                (fun _ -> Http.RequestString(httpMethod="GET",
                                             url=self.URL "upgrades",
                                             query=upgrades.ToQuery))
        if res.ToLower().Contains("exception")
        then raise (PeerRejectedUpgrades res)

    member self.WaitForLedgerNum (n:int) =
        RetryUntilTrue
            (fun _ -> self.GetLedgerNum = n)
            (fun _ -> LogInfo "Waiting for ledger %d on %s"
                              n self.ShortName )

    member self.WaitForNextLedger() =
        self.WaitForLedgerNum (self.GetLedgerNum + 1)

    member self.CheckNoErrorMetrics(includeTxInternalErrors:bool) =
        let raiseIfNonzero (c:int) (n:string) =
            if c <> 0
            then raise (PeerHasNonzeroErrorMetrics (self, n, c))
        let m:Metrics.Metrics = self.GetMetrics
        raiseIfNonzero m.ScpEnvelopeInvalidsig.Count "scp.envelope.invalidsig"
        raiseIfNonzero m.HistoryPublishFailure.Count "history.publish.failure"
        raiseIfNonzero m.LedgerInvariantFailure.Count "ledger.invariant.failure"
        if includeTxInternalErrors
        then raiseIfNonzero m.LedgerTransactionInternalError.Count
                 "ledger.transaction.internal-error"
        LogInfo "No errors found on %s" self.ShortName

    member self.CheckConsistencyWith (other:Peer) =
        let rec loop (ours:Map<int,string>) (theirs:Map<int,string>) (n:int) =
            if n < 0
            then raise (MaybeInconsistentPeers (self, other))
            else
                if Map.exists
                    begin
                        fun k v ->
                            match theirs.TryFind k with
                                | None -> false
                                | Some w when v = w ->
                                    LogInfo "found agreeing ledger %d = %s on %s and %s"
                                        k (v.Remove 6) self.ShortName other.ShortName
                                    true
                                | Some w -> raise (InconsistentPeers (self, other))
                    end
                        ours
                then ()
                else
                    let ourLedger = self.GetInfo.Ledger
                    let theirLedger = other.GetInfo.Ledger

                    loop
                        (Map.add ourLedger.Num ourLedger.Hash ours)
                        (Map.add theirLedger.Num theirLedger.Hash theirs)
                        (n-1)
        loop Map.empty Map.empty ConsistencyCheckIterationCount

    member self.ClearMetrics() =
        WebExceptionRetry DefaultRetry
            (fun _ -> Http.RequestString(httpMethod="GET",
                                         url=self.URL "clearmetrics"))

    member self.GetTestAcc (accName:string) : TestAcc.Root =
        // NB: work around buggy JSON parser upstream, see
        // https://github.com/fsharp/FSharp.Data/pull/1262
        let s = WebExceptionRetry DefaultRetry
                    (fun _ -> Http.RequestString(httpMethod = "GET",
                                                 url = self.URL("testacc"),
                                                 query = [("name", accName)]))
        TestAcc.Parse(if s.Trim().StartsWith("null") then "{}" else s)

    member self.GetTestAccBalance (accName:string) : int64 =
        RetryUntilSome
            (fun _ -> self.GetTestAcc(accName).Balance)
            (fun _ -> LogWarn "Waiting for account %s to exist, to read balance" accName)

    member self.GetTestAccSeq (accName:string) : int64 =
        RetryUntilSome
            (fun _ -> self.GetTestAcc(accName).Seqnum)
            (fun _ -> LogWarn "Waiting for account %s to exist, to read seqnum" accName)

    member self.GenerateLoad (lg:LoadGen) =
        WebExceptionRetry DefaultRetry
            (fun _ -> Http.RequestString(httpMethod="GET",
                                         url=self.URL "generateload",
                                         query=lg.ToQuery))

    member self.SubmitSignedTransaction (tx:Transaction) : Tx.Root =
        let b64 = tx.ToEnvelopeXdrBase64()
        let uri = (self.URL "tx") + "?blob=" + (System.Uri.EscapeDataString b64)
        let s = WebExceptionRetry DefaultRetry
                    (fun  _ ->
                     // Work around buggy URI-escaping upstream,
                     // see https://github.com/fsharp/FSharp.Data/issues/1263
                     LogDebug "Submitting transaction: %s" uri
                     let req = System.Net.WebRequest.CreateHttp uri
                     req.Method <- "GET"
                     use stream = req.GetResponse().GetResponseStream()
                     use reader = new System.IO.StreamReader(stream)
                     reader.ReadToEnd())
        LogDebug "Transaction response: %s" s
        let res = Tx.Parse(s)
        if res.Status <> "PENDING"
        then
            LogError "Transaction result %s" res.Status
            match res.Error with
                | None -> ()
                | Some(r) ->
                    let txr = responses.TransactionResult.FromXdr(r)
                    LogError "Result details: %O" txr
            raise (TransactionRejected tx)
        else res

    member self.WaitForLoadGenComplete (lg:LoadGen) =
        RetryUntilTrue
            (fun _ ->
                let m = self.GetMetrics
                (MeterCountOr 0 m.LoadgenRunStart) =
                     (MeterCountOr 0 m.LoadgenRunComplete))
            (fun _ ->
                let m = self.GetMetrics
                LogInfo "Waiting for loadgen run %d to finish, %d/%d accts, %d/%d txns"
                            (MeterCountOr 0 m.LoadgenRunStart)
                            (MeterCountOr 0 m.LoadgenAccountCreated) lg.accounts
                            (MeterCountOr 0 m.LoadgenTxnAttempted) lg.txs)


let ReportAllPeerStatus (nCfg:NetworkCfg) =
    nCfg.EachPeer
        begin
        fun (p:Peer) ->
            let info = p.GetInfo
            let metrics = p.GetMetrics
            LogInfo "Peer '%s' startedOn '%s', state '%s', overlay reading %.1f bytes/sec"
                      p.ShortName (info.StartedOn.ToString())
                      info.State metrics.OverlayByteRead.MeanRate
        end
