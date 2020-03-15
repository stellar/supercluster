// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNetworkData

open FSharp.Data
open stellar_dotnet_sdk

open StellarCoreSet
open StellarNetworkCfg

type HistoryArchiveState = JsonProvider<"json-type-samples/sample-stellar-history.json", ResolutionFolder=__SOURCE_DIRECTORY__>
let PubnetLatestHistoryArchiveState = "http://history.stellar.org/prd/core-live/core_live_001/.well-known/stellar-history.json"
let TestnetLatestHistoryArchiveState = "http://history.stellar.org/prd/core-testnet/core_testnet_001/.well-known/stellar-history.json"

type PubnetNode = JsonProvider<"json-pubnet-data/nodes.json", SampleIsList=true, ResolutionFolder=__SOURCE_DIRECTORY__>

let FullPubnetCoreSets (image:string) : CoreSet list =
    let allPubnetNodes = PubnetNode.GetSamples() |> Array.filter (fun n -> n.Active)
    let orgNodes, miscNodes =
        Array.partition (fun (n:PubnetNode.Root) -> n.HomeDomain.IsSome) allPubnetNodes
    let nodeToGeoLoc (n:PubnetNode.Root) : GeoLoc =
        {lat = float n.GeoData.Latitude
         lon = float n.GeoData.Longitude}
    let pubnetOpts = { CoreSetOptions.GetDefault image with
                         accelerateTime = false
                         initialization = { CoreSetInitialization.Default with forceScp = false }
                         dumpDatabase = false }
    let miscCoreSets : CoreSet array =
        Array.mapi
            (fun i n ->
                let coreSetOpts =
                    { pubnetOpts with
                        nodeCount = 1
                        nodeLocs = Some [nodeToGeoLoc n] }
                MakeLiveCoreSet (sprintf "misc-%d" i) coreSetOpts)
            miscNodes
    let groupedOrgNodes : (string * (PubnetNode.Root array)) array =
        Array.groupBy (fun (n:PubnetNode.Root) -> n.HomeDomain.Value) orgNodes
    let orgCoreSets : CoreSet array =
        Array.map
            (fun (k, nodes) ->
                let coreSetOpts =
                    { pubnetOpts with
                        nodeCount = Array.length nodes
                        nodeLocs = Some (List.map nodeToGeoLoc (List.ofArray nodes)) }
                MakeLiveCoreSet k coreSetOpts)
            groupedOrgNodes
    Array.append miscCoreSets orgCoreSets |> List.ofArray


let GetLatestPubnetLedgerNumber _ : int =
    let has = HistoryArchiveState.Load(PubnetLatestHistoryArchiveState)
    has.CurrentLedger

let GetLatestTestnetLedgerNumber _ : int =
    let has = HistoryArchiveState.Load(TestnetLatestHistoryArchiveState)
    has.CurrentLedger


let PubnetGetCommands = [
                        PeerShortName "core_live_001", "curl -sf http://history.stellar.org/prd/core-live/core_live_001/{0} -o {1}"
                        PeerShortName "core_live_002", "curl -sf http://history.stellar.org/prd/core-live/core_live_002/{0} -o {1}"
                        PeerShortName "core_live_003", "curl -sf http://history.stellar.org/prd/core-live/core_live_003/{0} -o {1}"
                        ] |> Map.ofList

let PubnetNodes = [
                  PeerShortName "core_live_001", KeyPair.FromAccountId("GCGB2S2KGYARPVIA37HYZXVRM2YZUEXA6S33ZU5BUDC6THSB62LZSTYH")
                  PeerShortName "core_live_002", KeyPair.FromAccountId("GCM6QMP3DLRPTAZW2UZPCPX2LF3SXWXKPMP3GKFZBDSF3QZGV2G5QSTK")
                  PeerShortName "core_live_003", KeyPair.FromAccountId("GABMKJM6I25XI4K7U6XWMULOUQIQ27BCTMLS6BYYSOWKTBUXVRJSXHYQ")
                  ] |> Map.ofList

let PubnetPeers = [
                  PeerDnsName "core-live4.stellar.org"
                  PeerDnsName "core-live5.stellar.org"
                  PeerDnsName "core-live6.stellar.org"
                  ]

let PubnetCoreSetOptions (image: string) = {
                    CoreSetOptions.GetDefault image with
                        quorumSet = Some([])
                        quorumSetKeys = PubnetNodes
                        historyGetCommands = PubnetGetCommands
                        peersDns = PubnetPeers
                        accelerateTime = false
                        initialization = { CoreSetInitialization.Default with forceScp = false }
                        dumpDatabase = false }

let TestnetGetCommands = [
                         PeerShortName "core_testnet_001", "curl -sf http://history.stellar.org/prd/core-testnet/core_testnet_001/{0} -o {1}"
                         PeerShortName "core_testnet_002", "curl -sf http://history.stellar.org/prd/core-testnet/core_testnet_002/{0} -o {1}"
                         PeerShortName "core_testnet_003", "curl -sf http://history.stellar.org/prd/core-testnet/core_testnet_003/{0} -o {1}"
                         ] |> Map.ofList

let TestnetNodes = [
                   PeerShortName "core_testnet_001", KeyPair.FromAccountId("GDKXE2OZMJIPOSLNA6N6F2BVCI3O777I2OOC4BV7VOYUEHYX7RTRYA7Y")
                   PeerShortName "core_testnet_002", KeyPair.FromAccountId("GCUCJTIYXSOXKBSNFGNFWW5MUQ54HKRPGJUTQFJ5RQXZXNOLNXYDHRAP")
                   PeerShortName "core_testnet_003", KeyPair.FromAccountId("GC2V2EFSXN6SQTWVYA5EPJPBWWIMSD2XQNKUOHGEKB535AQE2I6IXV2Z")
                   ] |> Map.ofList

let TestnetPeers = [
                   PeerDnsName "core-testnet1.stellar.org"
                   PeerDnsName "core-testnet2.stellar.org"
                   PeerDnsName "core-testnet3.stellar.org"
                   ]

let TestnetCoreSetOptions(image: string) = {
                    CoreSetOptions.GetDefault image with
                         quorumSet = Some([])
                         quorumSetKeys = TestnetNodes
                         historyGetCommands = TestnetGetCommands
                         peersDns = TestnetPeers
                         accelerateTime = false
                         initialization = { CoreSetInitialization.Default with forceScp = false }
                         dumpDatabase = false }

let clusterQuotas : NetworkQuotas =
    { ContainerMaxCpuMili = 4000
      ContainerMaxMemMebi = 8000
      NamespaceQuotaLimCpuMili = 80000
      NamespaceQuotaLimMemMebi = 62000
      NamespaceQuotaReqCpuMili = 10000
      NamespaceQuotaReqMemMebi = 22000
      NumConcurrentMissions = 1 }

// Geographic calculations for simulated network delays.
let greatCircleDistanceInKm (loc1:GeoLoc) (loc2:GeoLoc) : double =
    // So-called "Haversine formula" for approximate distances

    let earthRadiusInKm = 6371.0
    let degreesToRadians deg = (System.Math.PI / 180.0) * deg
    let lat1InRadians = degreesToRadians loc1.lat
    let lat2InRadians = degreesToRadians loc2.lat
    let deltaLatInDegrees = System.Math.Abs(loc2.lat-loc1.lat)
    let deltaLonInDegrees = System.Math.Abs(loc2.lon-loc1.lon)
    let deltaLatInRadians = degreesToRadians deltaLatInDegrees
    let deltaLonInRadians = degreesToRadians deltaLonInDegrees
    let sinHalfDeltaLat = System.Math.Sin(deltaLatInRadians/2.0)
    let sinHalfDeltaLon = System.Math.Sin(deltaLonInRadians/2.0)
    let a = ((sinHalfDeltaLat * sinHalfDeltaLat) +
             (sinHalfDeltaLon * sinHalfDeltaLon *
              System.Math.Cos(lat1InRadians) * System.Math.Cos(lat2InRadians)))
    let c = 2.0 * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1.0 - a))
    earthRadiusInKm * c

let networkDelayInMs (loc1:GeoLoc) (loc2:GeoLoc) : double =
    let idealSpeedOfLightInFibre = 200.0 // km/ms
    let km = greatCircleDistanceInKm loc1 loc2
    let ms = km / idealSpeedOfLightInFibre
    // Empirical slowdown is surprisingly variable: some paths (even long-distance ones)
    // run at nearly 80% of the ideal speed of light in fibre; others barely 20%. We consider
    // 50% or a 2x slowdown as "vaguely normal" with significant outliers; unfortunately
    // to get much better you really need to know the path (eg. trans-atlantic happens to
    // be very fast, but classifying a path as trans-atlantic vs. non is .. complicated!)
    let empiricalSlowdownPastIdeal = 2.0
    ms * empiricalSlowdownPastIdeal

let networkPingInMs (loc1:GeoLoc) (loc2:GeoLoc) : double =
    // A ping is a round trip, so double one-way delay.
    2.0 * (networkDelayInMs loc1 loc2)

