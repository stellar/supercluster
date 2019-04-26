// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreHTTP

open FSharp.Data
open System.Threading

open StellarCoreCfg
open StellarNetworkCfg
open StellarCorePeer


// Note to curious reader: these are "type providers" whereby the compiler's
// type definition facility is extended by plugins that are themselves
// parameterized by literals that refer to external sources. In this case
// FSharp.Data.JsonProvider<> takes a sample of some JSON you want to load and
// infers a static type for it.

type Metrics = JsonProvider<"json-type-samples/sample-metrics.json">
type Info = JsonProvider<"json-type-samples/sample-info.json">

let DefaultRetry = 5

let rec WebExceptionRetry (n:int) f =
    try
        f()
    with
        | :? System.Net.WebException as w when n > 0 ->
            printfn "Web exception %s, retrying %d more times"
                (w.Status.ToString()) n
            Thread.Sleep(millisecondsTimeout = 1000)
            WebExceptionRetry (n-1) f


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
        if self.GetLedgerNum = n
        then ()
        else
            begin
                printfn "Waiting for ledger %d on %s"
                    n self.ShortName
                Thread.Sleep(millisecondsTimeout = 1000)
                self.WaitForLedgerNum n
            end

    member self.WaitForNextLedger() =
        self.WaitForLedgerNum (self.GetLedgerNum + 1)


let ReportAllPeerStatus (nCfg:NetworkCfg) =
    nCfg.EachPeer
        begin
        fun (p:Peer) ->
            let info = p.GetInfo
            let metrics = p.GetMetrics
            printfn "Peer '%s' startedOn '%s', state '%s', Overlay reading %f bytes/sec"
                    p.ShortName (info.StartedOn.ToString()) info.State metrics.OverlayByteRead.MeanRate
        end
