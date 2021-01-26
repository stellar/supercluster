// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNetworkData

open FSharp.Data
open stellar_dotnet_sdk

open StellarCoreSet
open StellarCoreCfg
open StellarMissionContext

type HistoryArchiveState = JsonProvider<"json-type-samples/sample-stellar-history.json", ResolutionFolder=__SOURCE_DIRECTORY__>
let PubnetLatestHistoryArchiveState = "http://history.stellar.org/prd/core-live/core_live_001/.well-known/stellar-history.json"
let TestnetLatestHistoryArchiveState = "http://history.stellar.org/prd/core-testnet/core_testnet_001/.well-known/stellar-history.json"

type PubnetNode = JsonProvider<"json-pubnet-data/public-network-data-2021-01-05.json", SampleIsList=true, ResolutionFolder=__SOURCE_DIRECTORY__>
type Tier1PublicKey = JsonProvider<"json-pubnet-data/tier1keys.json", SampleIsList=true, ResolutionFolder=__SOURCE_DIRECTORY__>

let FullPubnetCoreSets (image:string) (manualclose:bool) : CoreSet list =
    // Our dataset is the samples used to build the datatype: json-pubnet-data/nodes.json
    let allPubnetNodes : PubnetNode.Root array = PubnetNode.GetSamples()

    let tier1KeySet : Set<string> = Tier1PublicKey.GetSamples()
                                     |> Array.map (fun n -> n.PublicKey)
                                     |> Set.ofArray

    // For each pubkey in the pubnet, we map it to an actual KeyPair (with a private
    // key) to use in the simulation. It's important to keep these straight! The keys
    // in the pubnet are _not_ used in the simulation. They are used as node identities
    // throughout the rest of this function, as strings called "pubkey", but should not
    // appear in the final CoreSets we're building.
    let mutable pubnetKeyToSimKey : Map<string,KeyPair> =
        Array.map (fun (n:PubnetNode.Root) -> (n.PublicKey, KeyPair.Random())) allPubnetNodes
        |> Map.ofArray

    // Not every pubkey used in the qsets is represented in the base set of pubnet nodes,
    // so we lazily extend the set here with new mappings as we discover new pubkeys.
    let getSimKey (pubkey:string) : KeyPair =
        match pubnetKeyToSimKey.TryFind pubkey with
        | Some(k) -> k
        | None ->
            let k = KeyPair.Random()
            pubnetKeyToSimKey <- pubnetKeyToSimKey.Add(pubkey, k)
            k

    // First we partition nodes in the network into those that have home domains and
    // those that do not. We call the former "org" nodes and the latter "misc" nodes.
    let orgNodes, miscNodes =
        Array.partition (fun (n:PubnetNode.Root) -> n.SbHomeDomain.IsSome) allPubnetNodes

    // We then group the org nodes by their home domains. The domain names are drawn
    // from the HomeDomains of the public network but with periods replaced with dashes,
    // and lowercased, so for example keybase.io turns into keybase-io.
    let groupedOrgNodes : (HomeDomainName * PubnetNode.Root array) array =
        Array.groupBy
            begin
                fun (n:PubnetNode.Root) ->
                    let cleanOrgName = n.SbHomeDomain.Value.Replace('.', '-')
                    let lowercase = cleanOrgName.ToLower()
                    HomeDomainName lowercase
            end
            orgNodes

    // Then build a map from accountID to HomeDomainName and index-within-domain
    // for each org node.
    let orgNodeHomeDomains : Map<string,(HomeDomainName * int)> =
        Array.collect
               begin
                   fun ((hdn:HomeDomainName), (nodes:PubnetNode.Root array)) ->
                       Array.mapi
                           begin
                               fun (i:int) (n:PubnetNode.Root) ->
                                   (n.PublicKey, (hdn, i))
                           end
                           nodes
               end
               groupedOrgNodes
        |> Map.ofArray

    // When we don't have real HomeDomainNames (eg. for misc nodes) we use a
    // node-XXXXXX name with the XXXXXX as the lowercased first 6 digits of the
    // pubkey. Lowercase because kubernetes resource objects have to have all
    // lowercase names.
    let anonymousNodeName (pubkey:string) : string =
        let simkey = (getSimKey pubkey).Address
        let extract = simkey.Substring(0, 6)
        let lowercase = extract.ToLower()
        "node-" + lowercase

    // Return either HomeDomainName name or a node-XXXXXX name derived from the pubkey.
    let homeDomainNameForKey (pubkey:string) : HomeDomainName =
        match orgNodeHomeDomains.TryFind pubkey with
        | Some(s, _) -> s
        | None -> HomeDomainName (anonymousNodeName pubkey)

    // Return either HomeDomainName-$i name or a node-XXXXXX-0 name derived from the pubkey.
    let peerShortNameForKey (pubkey:string) : PeerShortName =
        match orgNodeHomeDomains.TryFind pubkey with
        | Some(s, i) -> PeerShortName (sprintf "%s-%d" s.StringName i)
        | None -> PeerShortName ((anonymousNodeName pubkey) + "-0")

    let pubKeysToValidators pubKeys =
            pubKeys |> Array.map (fun k -> (peerShortNameForKey k, getSimKey k))
                    |> Map.ofArray

    // Recursively check if the given quorum set only contains tier 1.
    let rec checkOnlyContainsTier1 (q:PubnetNode.SbQuorumSet) : bool =
        Array.forall (fun k -> Set.contains k tier1KeySet) q.Validators
          && Array.forall checkOnlyContainsTier1Inner q.InnerQuorumSets
    and checkOnlyContainsTier1Inner (iq:PubnetNode.InnerQuorumSet) : bool =
        let q = new PubnetNode.SbQuorumSet(iq.JsonValue)
        checkOnlyContainsTier1 q

    // Recursively convert json qsets to typed QuorumSet type
    let rec qsetOfNodeQset (q:PubnetNode.SbQuorumSet) : QuorumSet =
        let sz = q.Validators.Length + q.InnerQuorumSets.Length
        let pct = percentOfThreshold sz (int(q.Threshold))
        { thresholdPercent = Some pct
          validators = pubKeysToValidators q.Validators
          innerQuorumSets = Array.map qsetOfNodeInnerQset q.InnerQuorumSets }
    and qsetOfNodeInnerQset (iq:PubnetNode.InnerQuorumSet) : QuorumSet =
        let q = new PubnetNode.SbQuorumSet(iq.JsonValue)
        qsetOfNodeQset q

    let defaultQuorum : QuorumSet =
        {
            thresholdPercent = None
            validators = allPubnetNodes |>
                            Array.filter (fun (n:PubnetNode.Root) ->
                                n.SbHomeDomain = Some "www.stellar.org") |>
                            Array.map (fun (n:PubnetNode.Root) -> n.PublicKey) |>
                            pubKeysToValidators
            innerQuorumSets = Array.empty
        }
    assert(not(Map.isEmpty defaultQuorum.validators))

    let qsetOfNodeQsetOrDefault (qOption:PubnetNode.SbQuorumSet option) : QuorumSet =
        match qOption with
            | Some q -> if (q.Validators.Length <> 0 || q.InnerQuorumSets.Length <> 0)
                            && checkOnlyContainsTier1 q
                        then qsetOfNodeQset q else defaultQuorum
            | None -> defaultQuorum

    let pubnetOpts = { CoreSetOptions.GetDefault image with
                         accelerateTime = false
                         historyNodes = Some([])
                         // We need to use a synchronized startup delay
                         // for networks as large as this, otherwise it loses
                         // sync before all the nodes are online.
                         syncStartupDelay = Some(30)
                         simulateApplyUsec = 1200
                         dumpDatabase = false }

    // Ashburn Virginia USA suburb of Washington a.k.a. AWS us-east-1
    // and GCP us-east-4; home to all SDF nodes, 2 LOBSTR nodes,
    // PaySend, Stellarbeat, 2 Satoshipay nodes, BAC, LockerX,
    // and all Blockdaemon nodes.
    // This location is used when a node doesn't have any geo data.
    let ashburn : GeoLoc = {lat = 38.89511; lon = -77.03637}

    let getGeoLocOrDefault (n:PubnetNode.Root) : GeoLoc =
        match n.SbGeoData with
        | Some geoData -> {lat = float geoData.Latitude
                           lon = float geoData.Longitude}
        | None -> ashburn

    let makeCoreSetWithExplicitKeys (hdn:HomeDomainName) (options:CoreSetOptions) (keys:KeyPair array) =
        { name = CoreSetName (hdn.StringName)
          options = options
          keys = keys
          live = true }

    let preferredPeersMapForAllNodes : Map<byte[], byte[] list> =
        let getSimPubKey (k:string) = (getSimKey k).PublicKey
        allPubnetNodes |>
            Array.map (fun (n:PubnetNode.Root) ->
                        let key = getSimPubKey n.PublicKey
                        let peers = n.Peers |> List.ofArray |> List.map getSimPubKey
                        (key, peers)) |>
            Map.ofArray

    let keysToPreferredPeersMap (keys:KeyPair array) =
        keys |> Array.map (fun (k:KeyPair) -> (k.PublicKey, Map.find k.PublicKey preferredPeersMapForAllNodes))
             |> Map.ofArray

    let miscCoreSets : CoreSet array =
        Array.mapi
            (fun (_:int) (n:PubnetNode.Root) ->
                let hdn = homeDomainNameForKey n.PublicKey
                let qset = qsetOfNodeQsetOrDefault n.SbQuorumSet
                let keys = [|getSimKey n.PublicKey|]
                let coreSetOpts =
                    { pubnetOpts with
                        nodeCount = 1
                        quorumSet = ExplicitQuorum qset
                        tier1 = Some (Set.contains n.PublicKey tier1KeySet)
                        nodeLocs = Some [getGeoLocOrDefault n]
                        preferredPeersMap = Some (keysToPreferredPeersMap keys) }
                let shouldForceScp = not manualclose
                let coreSetOpts = coreSetOpts.WithForceSCP shouldForceScp
                makeCoreSetWithExplicitKeys hdn coreSetOpts keys)
            miscNodes

    let orgCoreSets : CoreSet array =
        Array.map
            (fun ((hdn:HomeDomainName), (nodes:PubnetNode.Root array)) ->
                assert(nodes.Length <> 0)
                let qset = qsetOfNodeQsetOrDefault nodes.[0].SbQuorumSet
                let nodeList = List.ofArray nodes
                let keys = Array.map (fun (n:PubnetNode.Root) -> getSimKey n.PublicKey) nodes
                let coreSetOpts =
                    { pubnetOpts with
                        nodeCount = Array.length nodes
                        quorumSet = ExplicitQuorum qset
                        tier1 = Some (Set.contains nodes.[0].PublicKey tier1KeySet)
                        nodeLocs = Some (List.map getGeoLocOrDefault nodeList)
                        preferredPeersMap = Some (keysToPreferredPeersMap keys) }
                let shouldForceScp = not manualclose
                let coreSetOpts = coreSetOpts.WithForceSCP shouldForceScp
                makeCoreSetWithExplicitKeys hdn coreSetOpts keys)
            groupedOrgNodes

    Array.append miscCoreSets orgCoreSets
    |> List.ofArray


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

let PubnetQuorum : QuorumSet =
                  { thresholdPercent = None
                    validators = [
                        PeerShortName "core_live_001", KeyPair.FromAccountId("GCGB2S2KGYARPVIA37HYZXVRM2YZUEXA6S33ZU5BUDC6THSB62LZSTYH")
                        PeerShortName "core_live_002", KeyPair.FromAccountId("GCM6QMP3DLRPTAZW2UZPCPX2LF3SXWXKPMP3GKFZBDSF3QZGV2G5QSTK")
                        PeerShortName "core_live_003", KeyPair.FromAccountId("GABMKJM6I25XI4K7U6XWMULOUQIQ27BCTMLS6BYYSOWKTBUXVRJSXHYQ")
                        ] |> Map.ofList
                    innerQuorumSets = [||]
                  }

let PubnetPeers = [
                  PeerDnsName "core-live4.stellar.org"
                  PeerDnsName "core-live5.stellar.org"
                  PeerDnsName "core-live6.stellar.org"
                  ]

let PubnetCoreSetOptions (image: string) = {
                    CoreSetOptions.GetDefault image with
                        quorumSet = ExplicitQuorum PubnetQuorum
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

let TestnetQuorum : QuorumSet =
                   { thresholdPercent = None
                     validators = [
                         PeerShortName "core_testnet_001", KeyPair.FromAccountId("GDKXE2OZMJIPOSLNA6N6F2BVCI3O777I2OOC4BV7VOYUEHYX7RTRYA7Y")
                         PeerShortName "core_testnet_002", KeyPair.FromAccountId("GCUCJTIYXSOXKBSNFGNFWW5MUQ54HKRPGJUTQFJ5RQXZXNOLNXYDHRAP")
                         PeerShortName "core_testnet_003", KeyPair.FromAccountId("GC2V2EFSXN6SQTWVYA5EPJPBWWIMSD2XQNKUOHGEKB535AQE2I6IXV2Z")
                         ] |> Map.ofList
                     innerQuorumSets = [||] }

let TestnetPeers = [
                   PeerDnsName "core-testnet1.stellar.org"
                   PeerDnsName "core-testnet2.stellar.org"
                   PeerDnsName "core-testnet3.stellar.org"
                   ]

let TestnetCoreSetOptions(image: string) = {
                    CoreSetOptions.GetDefault image with
                         quorumSet = ExplicitQuorum TestnetQuorum
                         historyGetCommands = TestnetGetCommands
                         peersDns = TestnetPeers
                         accelerateTime = false
                         initialization = { CoreSetInitialization.Default with forceScp = false }
                         dumpDatabase = false }
