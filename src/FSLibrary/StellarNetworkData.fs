// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNetworkData

open FSharp.Data
open stellar_dotnet_sdk
open StellarCoreSet
open StellarCoreCfg
open StellarMissionContext
open Logging

type HistoryArchiveState = JsonProvider<"json-type-samples/sample-stellar-history.json", ResolutionFolder=cwd>

let PubnetLatestHistoryArchiveState =
    "http://history.stellar.org/prd/core-live/core_live_001/.well-known/stellar-history.json"

let TestnetLatestHistoryArchiveState =
    "http://history.stellar.org/prd/core-testnet/core_testnet_001/.well-known/stellar-history.json"

type PubnetNode = JsonProvider<"json-type-samples/sample-network-data.json", SampleIsList=false, ResolutionFolder=cwd>
type Tier1PublicKey = JsonProvider<"json-type-samples/sample-keys.json", SampleIsList=false, ResolutionFolder=cwd>

// When scaling the network, we need to pick
// the degree of each new node.
// The following numbers are added here based on pubnet observations/educated guesses.
let minPeerCountTier1 = 25

let maxPeerCountTier1 = 81

let peerCountTier1 (random: System.Random) : int = random.Next(minPeerCountTier1, maxPeerCountTier1)

let minPeerCountNonTier1 = 1

let maxPeerCountNonTier1 = 71

let peerCountNonTier1 (random: System.Random) : int =
    if random.Next(2) = 0 then
        8
    else
        random.Next(minPeerCountNonTier1, maxPeerCountNonTier1)

// For simplicity, we will make each new tier-1 organization contain exactly 3 nodes
// while scaling the pubnet.
let tier1OrgSize = 3

// A bunch of specific GeoLocs follow

// Ashburn, suburb of Washington DC USA a.k.a. AWS us-east-1
// and GCP us-east-4; home to all SDF nodes, 1 LOBSTR node,
// PaySend, Stellarbeat, 2 Satoshipay nodes, BAC, LockerX.
let Ashburn = { lat = 38.89511; lon = -77.03637 }

// Brussels Belgium a.k.a. GCP europe-west1 home of
// Blockdaemon 1
let Brussels = { lat = 50.9009; lon = 4.4855 }

// Beauharnois, suburb of Montreal a.k.a. OVH DC, home of LAPO 4,
// PublicNode Bootes, etc.
let Beauharnois = { lat = 45.2986777; lon = -73.9288762 }

// Bengaluru India a.k.a. DigitalOcean DC home of LOBSTR 5
let Bengaluru = { lat = 12.9634; lon = 77.5855 }

// Chennai India a.k.a. IBM/Softlayer DC, home of IBM India node
let Chennai = { lat = 13.08784; lon = 80.27847 }

// Clifton USA a.k.a. DigitalOcean DC home of LOBSTR 3
let Clifton = { lat = 40.8364; lon = -74.1403 }

// Columbus USA a.k.a. AWS DC (us-east-2) home of Stellarport Ohio
let Columbus = { lat = 39.9828671; lon = -83.1309106 }

// Council Bluffs USA a.k.a. GCP us-central1 home of Blockdaemon 2
let CouncilBluffs = { lat = 41.2619; lon = -95.8608 }

// Falkenstein/Vogtland Germany a.k.a. Hetzner DC
// home of StellarExpertV2, LOBSTR 1, HelpCoin 1, COINQVEST
// "germany", PublicNode Hercules, etc.
let Falkenstein = { lat = 50.4788652; lon = 12.3348363 }

// Frankfurt Germany a.k.a. AWS DC (eu-central-1) and Hetzner DC
// home of keybase 2 and SCHUNK 1
let Frankfurt = { lat = 50.11552; lon = 8.68417 }

// Helsinki Finland a.k.a. Hetzner DC home of COINQVEST "finland"
let Helsinki = { lat = 60.1719; lon = 24.9347 }

// Hong Kong a.k.a. IBM/Softlayer DC home of IBM HK node
let HongKong = { lat = 22.3526738; lon = 113.9876171 }

// Portland USA a.k.a. AWS DC (us-west-1) home of keybase1
let Portland = { lat = 45.5426916; lon = -122.7243663 }

// Pudong, suburb of Shanghai China a.k.a. Azure DC (china-east-2),
// home of fchain core3
let Pudong = { lat = 31.0856396; lon = 121.4547635 }

// Purfleet, suburb of London UK, a.k.a. OVH and Azure DCs (uksouth)
// home of StellarExpertV3, PublicNode Lyra, Wirex UK, Sakkex UK, etc.
let Purfleet = { lat = 51.4819587; lon = 0.2220319 }

// Sao Paulo Brazil a.k.a. AWS DC (sa-east-1) and IBM/Softlayer
// DC, home of at least IBM Brazil node
let SaoPaulo = { lat = -23.6821604; lon = -46.8754795 }

// Singapore a.k.a. DCs for OVH, AWS (ap-southeast-1),
// Azure (southeastasia), DigitalOcean. Home of LAPO 6,
// Wirex Singapore, fchain core 2, Hawking, etc.
let Singapore = { lat = 1.3437449; lon = 103.7540051 }

// Taipei Taiwan a.k.a. asia-east1 home of Blockdaemon 3
let Taipei = { lat = 25.0329; lon = 121.5654 }

// Tokyo Japan a.k.a. AWS DC (ap-northeast-1) home of lots
// of nodes.
let Tokyo = { lat = 35.6895; lon = 139.69171 }

let locations =
    [ Ashburn
      Brussels
      Beauharnois
      Bengaluru
      Chennai
      Clifton
      Columbus
      CouncilBluffs
      Falkenstein
      Frankfurt
      Helsinki
      HongKong
      Portland
      Pudong
      Purfleet
      SaoPaulo
      Singapore
      Taipei
      Tokyo ]
    |> Array.ofList

let tier1OrgNames = [ "sdf"; "bd"; "lo"; "wx"; "pn"; "cq"; "sp" ]

// Each edge connecting a and b is represented as (a, b) if a < b, and (b, a) otherwise.
// This makes sense as the graph is undirected, and it also makes it easier to handle a set of edges.
let extractEdges (graph: PubnetNode.Root array) : (string * string) array =
    let getEdgesFromNode (node: PubnetNode.Root) : (string * string) array =
        node.Peers
        |> Array.filter (fun peer -> peer < node.PublicKey) // This filter ensures that we add each edge exactly once.
        |> Array.map (fun peer -> (peer, node.PublicKey))

    graph |> Array.map getEdgesFromNode |> Array.reduce Array.append

// Given `edgeSet` containing edges (each edge is represented as a pair of strings),
// create a map whose key is a pubkey and the corresponding value is the list of
// nodes it's connected to.
let createAdjacencyMap (edgeList: (string * string) list) : Map<string, string list> =
    // So far, each edge connecting a and b is represented by the tuple (a, b) or (b, a).
    // When creating the adjacency list for the given graph,
    // we would like to add b to a's list, and a to b's list.
    // Therefore, for each edge, we swap the vertices and append it to the edge list.
    let adjacencyList : (string * (string list)) list =
        edgeList
        |> List.append (List.map (fun (x, y) -> (y, x)) edgeList)
        |> List.groupBy fst
        |> List.map (fun (x, y) -> (x, List.map snd y))

    Map.ofList adjacencyList

// Add edges for `newNodes` and return a new adjacency map.
// The degree of each new node is determined by
// 1. Check if it's a tier-1 node by `tier1KeySet`.
// 2. Use `peerCountTier1` or `peerCountNonTier1` to decide.
//
// To preserve the degree of each existing node,
// each new node's edges come from splitting an existing edge.
//
// More specifically, if we're adding a new node u,
// then we pick a random edge (a, b), remove (a, b) and add (a, u) and (u, b).
// We continue this process until u has a desired degree.
let addEdges
    (graph: PubnetNode.Root array)
    (newNodes: string array)
    (tier1KeySet: Set<string>)
    (random: System.Random)
    : Map<string, string list> =

    // Note that each edge is represented by a pair (a, b) with a < b.
    // This ensures that each edge appears exactly once in edgeArray.
    let edgeArray = extractEdges graph

    // At any point, both `edgeArrayList` and `edgeSet` contain all the edges. (and nothing more)
    // `edgeArrayList` is used to pick a random edge,
    // and `edgeSet` helps us efficiently determine if a certain edge already exists.
    let edgeArrayList : ResizeArray<string * string> = new ResizeArray<string * string>(edgeArray)
    let mutable edgeSet : Set<string * string> = edgeArray |> Set.ofArray

    for u in newNodes do
        let mutable degreeRemaining =
            if Set.contains u tier1KeySet then
                peerCountTier1 random
            else
                peerCountNonTier1 random

        if degreeRemaining >= (Array.length graph) then
            // The chosen degree is larger than the given graph's cardinality.
            // This error is likely caused by passing the incorrect pubnet graph file.
            // We choose to throw here because
            // 1. This new node would have a degree significantly larger
            //    than any node in the original graph.
            // 2. The following algorithm would likely fail to find enough edges.
            // 3. If we adjust `degreeRemaining`, then the degree distribution would be impacted.
            failwith "The original graph is too small"

        let maxRetryCount = 100
        let mutable errorCount = 0

        while degreeRemaining > 0 do
            let index = random.Next(0, Set.count edgeSet)
            let (a, b) = edgeArrayList.[index]
            let maybeNewEdge1 = (min a u, max a u)
            let maybeNewEdge2 = (min b u, max b u)

            if (a <> u)
               && (b <> u)
               && (not (Set.contains maybeNewEdge1 edgeSet))
               && (not (Set.contains maybeNewEdge2 edgeSet)) then
                degreeRemaining <- degreeRemaining - 2
                // 1. Replace the current edge with maybeNewEdge1
                // 2. Append maybeNewEdge2
                //
                // This ensures that edgeArrayList is exactly the list of all edges.
                edgeArrayList.[index] <- maybeNewEdge1
                edgeArrayList.Add(maybeNewEdge2)
                edgeSet <- edgeSet.Add(maybeNewEdge1)
                edgeSet <- edgeSet.Add(maybeNewEdge2)
                edgeSet <- edgeSet.Remove((a, b))
                errorCount <- 0
            else
                errorCount <- errorCount + 1

                if errorCount >= maxRetryCount then
                    // We have failed to find an edge for this node
                    // `errorCount` times in a row.
                    // This implies that there is likely no edge that `u` can be connected to.
                    // Therefore, we choose to throw here.
                    failwith (sprintf "Unable to find an edge for %s after %d attempts" u maxRetryCount)

    createAdjacencyMap (Set.toList edgeSet)

let FullPubnetCoreSets (context: MissionContext) (manualclose: bool) (enforceMinConnectionCount: bool) : CoreSet list =

    if context.pubnetData.IsNone then
        failwith "pubnet simulation requires --pubnet-data=<filename.json>"

    let allPubnetNodes : PubnetNode.Root array = PubnetNode.Load(context.pubnetData.Value)

    // A Random object with a fixed seed.
    let random = System.Random context.randomSeed

    let newTier1Nodes =
        [ for i in 1 .. context.tier1OrgsToAdd * tier1OrgSize ->
              PubnetNode.Parse(
                  sprintf
                      """ [{ "publicKey": "%s", "sb_homeDomain": "home.domain.%d" }] """
                      (KeyPair.Random().Address)
                      ((i - 1) / tier1OrgSize)
              ).[0] ]
        |> Array.ofList

    let newNonTier1Nodes =
        [ for i in 1 .. context.nonTier1NodesToAdd ->
              PubnetNode.Parse(sprintf """ [{ "publicKey": "%s" }] """ (KeyPair.Random().Address)).[0] ]
        |> Array.ofList

    let tier1KeySet : Set<string> =
        if context.tier1Keys.IsSome then
            let newTier1Keys = Array.map (fun (n: PubnetNode.Root) -> n.PublicKey) newTier1Nodes in

            Tier1PublicKey.Load(context.tier1Keys.Value)
            |> Array.map (fun n -> n.PublicKey)
            |> Array.append newTier1Keys
            |> Set.ofArray
        else
            PubnetNode.Load(context.pubnetData.Value)
            |> Array.filter (fun n -> n.SbHomeDomain.IsSome && List.contains n.SbHomeDomain.Value tier1OrgNames)
            |> Array.map (fun n -> n.PublicKey)
            |> Set.ofArray


    // Shuffle the nodes to ensure that the order will
    // not affect the outcome of the scaling algorithm.
    let newNodes =
        Array.append newTier1Nodes newNonTier1Nodes
        |> Array.sortBy (fun _ -> random.Next())

    let allPubnetNodes = allPubnetNodes |> Array.append newNodes

    // For each pubkey in the pubnet, we map it to an actual KeyPair (with a private
    // key) to use in the simulation. It's important to keep these straight! The keys
    // in the pubnet are _not_ used in the simulation. They are used as node identities
    // throughout the rest of this function, as strings called "pubkey", but should not
    // appear in the final CoreSets we're building.
    let mutable pubnetKeyToSimKey : Map<string, KeyPair> =
        Array.map (fun (n: PubnetNode.Root) -> (n.PublicKey, KeyPair.Random())) allPubnetNodes
        |> Map.ofArray

    // Not every pubkey used in the qsets is represented in the base set of pubnet nodes,
    // so we lazily extend the set here with new mappings as we discover new pubkeys.
    let getSimKey (pubkey: string) : KeyPair =
        match pubnetKeyToSimKey.TryFind pubkey with
        | Some (k) -> k
        | None ->
            let k = KeyPair.Random()
            pubnetKeyToSimKey <- pubnetKeyToSimKey.Add(pubkey, k)
            k

    // It is important that we add edges before trimming using `networkSizeLimit`.
    // This is because `networkSizeLimit` may be smaller than the degree of some new node
    // and cause an issue to the scaling algorithm.
    let adjacencyMap =
        addEdges allPubnetNodes (Array.map (fun (n: PubnetNode.Root) -> n.PublicKey) newNodes) tier1KeySet random

    // First, we will remove all nodes with <= 4 connections because those nodes
    // seem to fail to stay in sync with the network.
    // Note that removing nodes may lead to more nodes having <= 4 connections,
    // but anecdotally this has never been an issue.
    // Next, we partition nodes in the network into those that have home domains and
    // those that do not. We call the former "org" nodes and the latter "misc" nodes.
    let minAllowedConnectionCount = if enforceMinConnectionCount then 4 else 1

    let orgNodes, miscNodes =
        allPubnetNodes
        |> Array.filter (fun (n: PubnetNode.Root) -> minAllowedConnectionCount <= Array.length n.Peers)
        |> Array.partition (fun (n: PubnetNode.Root) -> n.SbHomeDomain.IsSome)

    // We then trim down the set of misc nodes so that they fit within simulation
    // size limit passed. If we can't even fit the org nodes, we fail here.
    let miscNodes =
        (let numOrgNodes = Array.length orgNodes
         let numMiscNodes = Array.length miscNodes

         let _ =
             if numOrgNodes > context.networkSizeLimit then
                 failwith "simulated network size limit too small to fit org nodes"

         let takeMiscNodes = min (context.networkSizeLimit - numOrgNodes) numMiscNodes
         Array.take takeMiscNodes miscNodes)

    let allPubnetNodes = Array.append orgNodes miscNodes
    let _ = assert ((Array.length allPubnetNodes) <= context.networkSizeLimit)

    let allPubnetNodeKeys =
        Array.map (fun (n: PubnetNode.Root) -> n.PublicKey) allPubnetNodes
        |> Set.ofArray

    LogInfo "SimulatePubnet will run with %d nodes" (Array.length allPubnetNodes)

    // We then group the org nodes by their home domains. The domain names are drawn
    // from the HomeDomains of the public network but with periods replaced with dashes,
    // and lowercased, so for example keybase.io turns into keybase-io.
    let groupedOrgNodes : (HomeDomainName * PubnetNode.Root array) array =
        Array.groupBy
            (fun (n: PubnetNode.Root) ->
                let domain = n.SbHomeDomain.Value
                // We turn 'www.stellar.org' into 'stellar'
                // and 'stellar.blockdaemon.com' into 'blockdaemon'
                // 'validator.stellar.expert' is a special case
                // since it would also turn into 'stellar'.
                // As a slightly hacky fix, we will call it 'exert'.
                let cleanOrgName =
                    if domain = "validator.stellar.expert" then "expert"
                    else if domain.Contains('.') then ((Array.rev (domain.Split('.'))).[1])
                    else domain

                let lowercase = cleanOrgName.ToLower()
                HomeDomainName lowercase)
            orgNodes

    // Then build a map from accountID to HomeDomainName and index-within-domain
    // for each org node.
    let orgNodeHomeDomains : Map<string, (HomeDomainName * int)> =
        Array.collect
            (fun (hdn: HomeDomainName, nodes: PubnetNode.Root array) ->
                Array.mapi (fun (i: int) (n: PubnetNode.Root) -> (n.PublicKey, (hdn, i))) nodes)
            groupedOrgNodes
        |> Map.ofArray

    // When we don't have real HomeDomainNames (eg. for misc nodes) we use a
    // node-XXXXXX name with the XXXXXX as the lowercased first 6 digits of the
    // pubkey. Lowercase because kubernetes resource objects have to have all
    // lowercase names.
    let anonymousNodeName (pubkey: string) : string =
        let simkey = (getSimKey pubkey).Address
        let extract = simkey.Substring(0, 6)
        let lowercase = extract.ToLower()
        "node-" + lowercase

    // Return either HomeDomainName name or a node-XXXXXX name derived from the pubkey.
    let homeDomainNameForKey (pubkey: string) : HomeDomainName =
        match orgNodeHomeDomains.TryFind pubkey with
        | Some (s, _) -> s
        | None -> HomeDomainName(anonymousNodeName pubkey)

    // Return either HomeDomainName-$i name or a node-XXXXXX-0 name derived from the pubkey.
    let peerShortNameForKey (pubkey: string) : PeerShortName =
        match orgNodeHomeDomains.TryFind pubkey with
        | Some (s, i) -> PeerShortName(sprintf "%s-%d" s.StringName i)
        | None -> PeerShortName((anonymousNodeName pubkey) + "-0")

    let pubKeysToValidators (pubKeys: string array) : Map<PeerShortName, KeyPair> =
        pubKeys
        |> Array.map (fun k -> (peerShortNameForKey k, getSimKey k))
        |> Map.ofArray

    // A full-tier-1 qset.
    // This contains all tier-1 nodes with
    // * 3f + 1 for cross organization thresholds (top level).
    // * 2f + 1 for thresholds within each organization.
    let defaultQuorum : QuorumSet =
        let tier1NodesGroupedByHomeDomain : (string array) array =
            allPubnetNodes
            |> Array.filter
                (fun (n: PubnetNode.Root) -> (Set.contains n.PublicKey tier1KeySet) && n.SbHomeDomain.IsSome)
            |> Array.groupBy (fun (n: PubnetNode.Root) -> n.SbHomeDomain.Value)
            |> Array.map
                (fun (_, nodes: PubnetNode.Root []) -> Array.map (fun (n: PubnetNode.Root) -> n.PublicKey) nodes)

        let orgToQSet (org: string array) : QuorumSet =
            { thresholdPercent = Some(51) // Simple majority
              validators = pubKeysToValidators org
              innerQuorumSets = Array.empty }

        let nOrgs = Array.length tier1NodesGroupedByHomeDomain

        { thresholdPercent = Some(67) // 3f + 1
          validators = Map.empty
          innerQuorumSets = Array.map orgToQSet tier1NodesGroupedByHomeDomain }

    let pubnetOpts =
        { CoreSetOptions.GetDefault context.image with
              accelerateTime = false
              historyNodes = Some([])
              // We need to use a synchronized startup delay
              // for networks as large as this, otherwise it loses
              // sync before all the nodes are online.
              syncStartupDelay = Some(30)
              invariantChecks = AllInvariantsExceptBucketConsistencyChecks
              dumpDatabase = false }

    // Sorted list of known geolocations.
    // We can choose an arbitrary geolocation such that the distribution follows that of the given data
    // by uniformly randomly selecting an element from this array.
    // Since this list contains duplicates, it is weighted. (i.e., A common geolocation appears
    // multiple times in the list and thus is more likely to get selected.)
    // Since this is sorted, we will have the same assignments across different runs
    // as long as the random function is persistent.
    let geoLocations : GeoLoc array =
        allPubnetNodes
        |> Array.filter (fun (n: PubnetNode.Root) -> n.SbGeoData.IsSome)
        |> Array.map
            (fun (n: PubnetNode.Root) ->
                { lat = float n.SbGeoData.Value.Latitude
                  lon = float n.SbGeoData.Value.Longitude })
        |> Seq.ofArray
        |> Seq.sortBy (fun x -> x.lat, x.lon)
        |> Array.ofSeq

    // If the given node has geolocation info, use it.
    // Otherwise, pseudo-randomly select one from the list of geolocations that we are aware of.
    // As mentioned above, this assignment is weighted. (i.e., A common geolocation is more likely to be selected.)
    // Since the assignment depends on the Random object with a fixed seed,
    // this assignment is persistent across different runs.
    let getGeoLocOrDefault (n: PubnetNode.Root) : GeoLoc =
        match n.SbGeoData with
        | Some geoData -> { lat = float geoData.Latitude; lon = float geoData.Longitude }
        | None ->
            if Array.length geoLocations <> 0 then
                geoLocations.[random.Next(0, Array.length geoLocations)]
            else
                locations.[random.Next(0, Array.length locations)]

    let makeCoreSetWithExplicitKeys (hdn: HomeDomainName) (options: CoreSetOptions) (keys: KeyPair array) =
        { name = CoreSetName(hdn.StringName)
          options = options
          keys = keys
          live = true }

    let preferredPeersMapForAllNodes : Map<byte [], byte [] list> =
        let getSimPubKey (k: string) = (getSimKey k).PublicKey

        allPubnetNodes
        |> Array.map
            (fun (n: PubnetNode.Root) ->
                let key = getSimPubKey n.PublicKey

                let peers =
                    Map.find n.PublicKey adjacencyMap
                    |>
                    // This filtering is necessary since we intentionally remove some nodes
                    // using networkSizeLimit.
                    List.filter (fun (k: string) -> Set.contains k allPubnetNodeKeys)
                    |> List.map getSimPubKey

                (key, peers))
        |> Map.ofArray

    let keysToPreferredPeersMap (keys: KeyPair array) =
        keys
        |> Array.map (fun (k: KeyPair) -> (k.PublicKey, Map.find k.PublicKey preferredPeersMapForAllNodes))
        |> Map.ofArray

    let miscCoreSets : CoreSet array =
        Array.mapi
            (fun (_: int) (n: PubnetNode.Root) ->
                let hdn = homeDomainNameForKey n.PublicKey
                let keys = [| getSimKey n.PublicKey |]

                let coreSetOpts =
                    { pubnetOpts with
                          nodeCount = 1
                          quorumSet = ExplicitQuorum defaultQuorum
                          tier1 = Some(Set.contains n.PublicKey tier1KeySet)
                          nodeLocs = Some [ getGeoLocOrDefault n ]
                          preferredPeersMap = Some(keysToPreferredPeersMap keys) }

                let shouldWaitForConsensus = manualclose
                let coreSetOpts = coreSetOpts.WithWaitForConsensus shouldWaitForConsensus
                makeCoreSetWithExplicitKeys hdn coreSetOpts keys)
            miscNodes

    let orgCoreSets : CoreSet array =
        Array.map
            (fun (hdn: HomeDomainName, nodes: PubnetNode.Root array) ->
                assert (nodes.Length <> 0)
                let nodeList = List.ofArray nodes
                let keys = Array.map (fun (n: PubnetNode.Root) -> getSimKey n.PublicKey) nodes

                let coreSetOpts =
                    { pubnetOpts with
                          nodeCount = Array.length nodes
                          quorumSet = ExplicitQuorum defaultQuorum
                          tier1 = Some(Set.contains nodes.[0].PublicKey tier1KeySet)
                          nodeLocs = Some(List.map getGeoLocOrDefault nodeList)
                          preferredPeersMap = Some(keysToPreferredPeersMap keys) }

                let shouldWaitForConsensus = manualclose
                let coreSetOpts = coreSetOpts.WithWaitForConsensus shouldWaitForConsensus
                makeCoreSetWithExplicitKeys hdn coreSetOpts keys)
            groupedOrgNodes

    Array.append miscCoreSets orgCoreSets |> List.ofArray


let GetLatestPubnetLedgerNumber _ : int =
    let has = HistoryArchiveState.Load(PubnetLatestHistoryArchiveState)
    has.CurrentLedger

let GetLatestTestnetLedgerNumber _ : int =
    let has = HistoryArchiveState.Load(TestnetLatestHistoryArchiveState)
    has.CurrentLedger

let PubnetGetCommands =
    [ PeerShortName "core_live_001", "curl -sf http://history.stellar.org/prd/core-live/core_live_001/{0} -o {1}"
      PeerShortName "core_live_002", "curl -sf http://history.stellar.org/prd/core-live/core_live_002/{0} -o {1}"
      PeerShortName "core_live_003", "curl -sf http://history.stellar.org/prd/core-live/core_live_003/{0} -o {1}" ]
    |> Map.ofList

let PubnetQuorum : QuorumSet =
    { thresholdPercent = None
      validators =
          [ PeerShortName "core_live_001",
            KeyPair.FromAccountId("GCGB2S2KGYARPVIA37HYZXVRM2YZUEXA6S33ZU5BUDC6THSB62LZSTYH")
            PeerShortName "core_live_002",
            KeyPair.FromAccountId("GCM6QMP3DLRPTAZW2UZPCPX2LF3SXWXKPMP3GKFZBDSF3QZGV2G5QSTK")
            PeerShortName "core_live_003",
            KeyPair.FromAccountId("GABMKJM6I25XI4K7U6XWMULOUQIQ27BCTMLS6BYYSOWKTBUXVRJSXHYQ") ]
          |> Map.ofList
      innerQuorumSets = [||] }

let PubnetPeers =
    [ PeerDnsName "core-live4.stellar.org"
      PeerDnsName "core-live5.stellar.org"
      PeerDnsName "core-live6.stellar.org" ]

let PubnetCoreSetOptions (image: string) =
    { CoreSetOptions.GetDefault image with
          emptyDirType = DiskBackedEmptyDir
          quorumSet = ExplicitQuorum PubnetQuorum
          historyGetCommands = PubnetGetCommands
          peersDns = PubnetPeers
          accelerateTime = false
          initialization = { CoreSetInitialization.Default with waitForConsensus = true }
          dumpDatabase = false }

let TestnetGetCommands =
    [ PeerShortName "core_testnet_001",
      "curl -sf http://history.stellar.org/prd/core-testnet/core_testnet_001/{0} -o {1}"
      PeerShortName "core_testnet_002",
      "curl -sf http://history.stellar.org/prd/core-testnet/core_testnet_002/{0} -o {1}"
      PeerShortName "core_testnet_003",
      "curl -sf http://history.stellar.org/prd/core-testnet/core_testnet_003/{0} -o {1}" ]
    |> Map.ofList

let TestnetQuorum : QuorumSet =
    { thresholdPercent = None
      validators =
          [ PeerShortName "core_testnet_001",
            KeyPair.FromAccountId("GDKXE2OZMJIPOSLNA6N6F2BVCI3O777I2OOC4BV7VOYUEHYX7RTRYA7Y")
            PeerShortName "core_testnet_002",
            KeyPair.FromAccountId("GCUCJTIYXSOXKBSNFGNFWW5MUQ54HKRPGJUTQFJ5RQXZXNOLNXYDHRAP")
            PeerShortName "core_testnet_003",
            KeyPair.FromAccountId("GC2V2EFSXN6SQTWVYA5EPJPBWWIMSD2XQNKUOHGEKB535AQE2I6IXV2Z") ]
          |> Map.ofList
      innerQuorumSets = [||] }

let TestnetPeers =
    [ PeerDnsName "core-testnet1.stellar.org"
      PeerDnsName "core-testnet2.stellar.org"
      PeerDnsName "core-testnet3.stellar.org" ]

let TestnetCoreSetOptions (image: string) =
    { CoreSetOptions.GetDefault image with
          emptyDirType = DiskBackedEmptyDir
          quorumSet = ExplicitQuorum TestnetQuorum
          historyGetCommands = TestnetGetCommands
          peersDns = TestnetPeers
          accelerateTime = false
          initialization = { CoreSetInitialization.Default with waitForConsensus = true }
          dumpDatabase = false }

// This coreset is a synthetic approximation of the Tier1 group, intended
// to be used in stable benchmarks rather than experiments.
let StableApproximateTier1CoreSets image : CoreSet list =
    let allOrgs : Map<string, GeoLoc list> =
        Map.ofList [ ("bd", [ Brussels; CouncilBluffs; Taipei ])
                     ("cq", [ Falkenstein; Helsinki; HongKong ])
                     ("kb", [ Ashburn; Frankfurt; Portland ])
                     ("lo", [ Ashburn; Bengaluru; Falkenstein; Helsinki; Singapore ])
                     ("sp", [ CouncilBluffs; Frankfurt; Singapore ])
                     ("sdf", [ Ashburn; Ashburn; Ashburn ])
                     ("wx", [ CouncilBluffs; Purfleet; Singapore ]) ]

    let allOrgPairs = Map.toList allOrgs
    let orgKeys _ nodes = List.map (fun _ -> KeyPair.Random()) nodes
    let allKeys = Map.map orgKeys allOrgs
    let pk (k: KeyPair) : byte [] = k.PublicKey
    let allPksList : List<byte []> = Map.toList allKeys |> List.collect (fun (_, keys) -> List.map pk keys)
    let connections = List.map (fun k -> (k, List.except [ k ] allPksList)) allPksList |> Map.ofList

    let namedKeys org =
        List.indexed allKeys.[org]
        |> List.map (fun (i, k) -> (PeerShortName(sprintf "%s-%d" org i), k))
        |> Map.ofList

    let orgToQset org =
        { thresholdPercent = Some(51)
          validators = namedKeys org
          innerQuorumSets = Array.empty }

    let topQset =
        { thresholdPercent = Some(67)
          validators = Map.empty
          innerQuorumSets = Map.toArray allOrgs |> Array.map (fun (org, _) -> orgToQset org) }

    let orgCoreSet (org: string, locs: GeoLoc list) : CoreSet =
        let coreSetOpts =
            { CoreSetOptions.GetDefault image with
                  emptyDirType = DiskBackedEmptyDir
                  performMaintenance = true
                  nodeCount = locs.Length
                  nodeLocs = Some(locs)
                  preferredPeersMap = Some(connections)
                  quorumSet = ExplicitQuorum topQset
                  localHistory = false
                  accelerateTime = false
                  tier1 = Some(true)
                  initialization = CoreSetInitialization.OnlyNewDb
                  invariantChecks = InvariantChecksSpec.NoInvariants
                  dumpDatabase = false }

        { name = CoreSetName(org)
          keys = Array.ofList allKeys.[org]
          live = true
          options = coreSetOpts }

    List.map orgCoreSet allOrgPairs
