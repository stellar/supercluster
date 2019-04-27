// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreHTTP

open FSharp.Data
open FSharp.Data.JsonExtensions

open PollRetry
open StellarCoreCfg
open StellarNetworkCfg
open StellarCorePeer


// Note to curious reader: these are "type providers" whereby the compiler's
// type definition facility is extended by plugins that are themselves
// parameterized by literals that refer to external sources. In this case
// FSharp.Data.JsonProvider<> takes a sample of some JSON you want to load and
// infers a static type for it.

type Metrics = JsonProvider<"json-type-samples/sample-metrics.json", SampleIsList=true>
type Info = JsonProvider<"json-type-samples/sample-info.json">
type TestAcc = JsonProvider<"json-type-samples/sample-testacc.json">


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

    member self.ToQuery =
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

let MeterCountOr (def:int) (m:Option<Metrics.GenericMeter>) : int =
    match m with
        | Some(n) -> n.Count
        | None -> def

type Peer with

    member self.URL (path:string) =
        sprintf "http://localhost:8080/%s/%s/%s"
            (self.networkCfg.networkNonce.ToString())
            self.ShortName
            path

    member self.GetMetrics =
        WebExceptionRetry DefaultRetry
            (fun _ -> Metrics.Load(self.URL "metrics").Metrics)

    member self.GetInfo =
        WebExceptionRetry DefaultRetry
            (fun _ -> Info.Load(self.URL "info").Info)

    member self.GetLedgerNum =
        self.GetInfo.Ledger.Num

    member self.WaitForLedgerNum (n:int) =
        RetryUntilTrue
            (fun _ -> self.GetLedgerNum = n)
            (fun _ -> printfn "Waiting for ledger %d on %s"
                              n self.ShortName )

    member self.WaitForNextLedger() =
        self.WaitForLedgerNum (self.GetLedgerNum + 1)

    member self.GetTestAcc (accName:string) =
        WebExceptionRetry DefaultRetry
            (fun _ -> TestAcc.Load(self.URL("testacc") + "?name=" + accName))

    member self.GetTestAccBalance (accName:string) : int64 =
        self.GetTestAcc(accName).Balance

    member self.GetTestAccSeq (accName:string) : int =
        self.GetTestAcc(accName).Seqnum

    member self.GenerateLoad (lg:LoadGen) =
        WebExceptionRetry DefaultRetry
            (fun _ -> Http.RequestString(httpMethod="GET",
                                         url=self.URL "generateload",
                                         query=lg.ToQuery))

    member self.WaitForLoadGenComplete (lg:LoadGen) =
        RetryUntilTrue
            (fun _ ->
                let m = self.GetMetrics
                (MeterCountOr 0 m.LoadgenRunStart) =
                     (MeterCountOr 0 m.LoadgenRunComplete))
            (fun _ ->
                let m = self.GetMetrics
                printfn "Waiting for loadgen run %d to finish, %d/%d accts, %d/%d txns"
                            (MeterCountOr 0 m.LoadgenRunStart)
                            (MeterCountOr 0 m.LoadgenAccountCreated) lg.accounts
                            (MeterCountOr 0 m.LoadgenTxnAttempted) lg.txs)


let ReportAllPeerStatus (nCfg:NetworkCfg) =
    nCfg.EachPeer
        begin
        fun (p:Peer) ->
            let info = p.GetInfo
            let metrics = p.GetMetrics
            printfn "Peer '%s' startedOn '%s', state '%s', Overlay reading %f bytes/sec"
                    p.ShortName (info.StartedOn.ToString()) info.State metrics.OverlayByteRead.MeanRate
        end
