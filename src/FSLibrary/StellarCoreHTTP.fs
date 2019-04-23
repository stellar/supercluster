// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreHTTP

open FSharp.Data
open StellarCoreCfg
open StellarNetworkCfg

// Note to curious reader: these are "type providers" whereby the compiler's
// type definition facility is extended by plugins that are themselves
// parameterized by literals that refer to external sources. In this case
// FSharp.Data.JsonProvider<> takes a sample of some JSON you want to load and
// infers a static type for it.

type Metrics = JsonProvider<"json-type-samples/sample-metrics.json">
type Info = JsonProvider<"json-type-samples/sample-info.json">

let PeerURL (nCfg:NetworkCfg) (peer:int) (path:string) =
    sprintf "http://localhost:8080/%s/%s/%s"
        (nCfg.networkNonce.ToString())
        (CfgVal.peerShortName peer)
        path

let GetMetricsFromPeer (nCfg:NetworkCfg) (peer:int) : Metrics.Metrics =
    Metrics.Load(PeerURL nCfg peer "metrics").Metrics

let GetInfoFromPeer (nCfg:NetworkCfg) (peer:int) : Info.Info =
    Info.Load(PeerURL nCfg peer "info").Info

let GetAllMetrics (nCfg:NetworkCfg) : Metrics.Metrics array =
    Array.init nCfg.NumPeers (GetMetricsFromPeer nCfg)

let GetAllInfos (nCfg:NetworkCfg) : Info.Info array =
    Array.init nCfg.NumPeers (GetInfoFromPeer nCfg)

let ReportAllPeerStatus (nCfg:NetworkCfg) =
    for i in 0 .. (nCfg.NumPeers - 1) do
        let mutable retries : int = 5
        while retries > 0 do
            try
                let info = GetInfoFromPeer nCfg i
                let metrics = GetMetricsFromPeer nCfg i
                printfn "Peer %d startedOn '%s', state '%s', Overlay reading %f bytes/sec"
                        i (info.StartedOn.ToString()) info.State metrics.OverlayByteRead.MeanRate
                retries <- 0
            with
                | :? System.Net.WebException as w ->
                    printfn "Web exception %s while fetching HTTP data for peer %d, retrying" (w.Status.ToString()) i
                    System.Threading.Thread.Sleep(millisecondsTimeout = 1000)
                    retries <- retries - 1

