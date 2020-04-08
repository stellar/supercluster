// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNetworkData

open FSharp.Data
open stellar_dotnet_sdk

open FSharp.Data
open StellarCoreSet
open StellarCoreCfg
open StellarNetworkCfg
open stellar_dotnet_sdk.responses.results

type HistoryArchiveState = JsonProvider<"json-type-samples/sample-stellar-history.json", ResolutionFolder=__SOURCE_DIRECTORY__>
let PubnetLatestHistoryArchiveState = "http://history.stellar.org/prd/core-live/core_live_001/.well-known/stellar-history.json"
let TestnetLatestHistoryArchiveState = "http://history.stellar.org/prd/core-testnet/core_testnet_001/.well-known/stellar-history.json"

type PubnetNode = JsonProvider<"json-pubnet-data/nodes.json", SampleIsList=true, ResolutionFolder=__SOURCE_DIRECTORY__>

let FullPubnetCoreSets (image:string) : CoreSet list =

    // Our dataset is the samples used to build the datatype: json-pubnet-data/nodes.json
    let allPubnetNodes : PubnetNode.Root array =
        PubnetNode.GetSamples()
        |> Array.filter (fun n -> n.Active &&
                                  ((n.QuorumSet.Validators.Length <> 0) ||
                                   (n.QuorumSet.InnerQuorumSets.Length <> 0)))

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
        Array.partition (fun (n:PubnetNode.Root) -> n.HomeDomain.IsSome) allPubnetNodes

    // We then group the org nodes by their home domains. The domain names are drawn
    // from the HomeDomains of the public network but with periods replaced with dashes,
    // and lowercased, so for example keybase.io turns into keybase-io.
    let groupedOrgNodes : (HomeDomainName * PubnetNode.Root array) array =
        Array.groupBy
            begin
                fun (n:PubnetNode.Root) ->
                    let cleanOrgName = n.HomeDomain.Value.Replace('.', '-')
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

    // Recursively convert json qsets to typed QuorumSet type
    let rec qsetOfNodeQset (q:PubnetNode.QuorumSet) : QuorumSet =
        let sz = q.Validators.Length + q.InnerQuorumSets.Length
        let pct = percentOfThreshold sz (int(q.Threshold))
        { thresholdPercent = Some pct
          validators = Array.map (fun k -> (peerShortNameForKey k, getSimKey k)) q.Validators
                       |> Map.ofArray
          innerQuorumSets = Array.map qsetOfNodeInnerQset q.InnerQuorumSets }
    and qsetOfNodeInnerQset (iq:PubnetNode.InnerQuorumSet) : QuorumSet =
        let q = new PubnetNode.QuorumSet(iq.JsonValue)
        qsetOfNodeQset q

    // Convert the json geolocation data to a typed GeoLoc type
    let nodeToGeoLoc (n:PubnetNode.Root) : GeoLoc =
        {lat = float n.GeoData.Latitude
         lon = float n.GeoData.Longitude}

    let historyGetCmds : Map<PeerShortName, string> =
        Array.filter (fun (n:PubnetNode.Root) -> n.HistoryUrl.IsSome) orgNodes
        |> Array.map
               (fun (n:PubnetNode.Root) ->
                   let shortName = peerShortNameForKey n.PublicKey
                   let uri = System.Uri(n.HistoryUrl.Value)
                   let cmd = curlGetCmd uri
                   (shortName, cmd))
        |> Map.ofArray

    let pubnetOpts = { CoreSetOptions.GetDefault image with
                         accelerateTime = false
                         historyNodes = Some([])
                         historyGetCommands = historyGetCmds
                         // We need to use a synchronized startup delay
                         // for networks as large as this, otherwise it loses
                         // sync before all the nodes are online.
                         syncStartupDelay = Some(30)
                         localHistory = false
                         dumpDatabase = false }

    let makeCoreSetWithExplicitKeys (hdn:HomeDomainName) (options:CoreSetOptions) (keys:KeyPair array) =
        { name = CoreSetName (hdn.StringName)
          options = options
          keys = keys
          live = true }

    let miscCoreSets : CoreSet array =
        Array.mapi
            (fun (i:int) (n:PubnetNode.Root) ->
                let hdn = homeDomainNameForKey n.PublicKey
                let qset = qsetOfNodeQset n.QuorumSet
                let coreSetOpts =
                    { pubnetOpts with
                        nodeCount = 1
                        quorumSet = ExplicitQuorum qset
                        nodeLocs = Some [nodeToGeoLoc n] }
                let shouldForceScp = n.IsValidator
                let coreSetOpts = coreSetOpts.WithForceSCP shouldForceScp
                let keys = [|getSimKey n.PublicKey|]
                makeCoreSetWithExplicitKeys hdn coreSetOpts keys)
            miscNodes

    let orgCoreSets : CoreSet array =
        Array.map
            (fun ((hdn:HomeDomainName), (nodes:PubnetNode.Root array)) ->
                assert(nodes.Length <> 0)
                let qset = qsetOfNodeQset nodes.[0].QuorumSet
                let coreSetOpts =
                    { pubnetOpts with
                        nodeCount = Array.length nodes
                        quorumSet = ExplicitQuorum qset
                        nodeLocs = Some (List.map nodeToGeoLoc (List.ofArray nodes)) }
                let shouldForceScp = nodes.[0].IsValidator
                let coreSetOpts = coreSetOpts.WithForceSCP shouldForceScp
                let keys = Array.map (fun (n:PubnetNode.Root) -> getSimKey n.PublicKey) nodes
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

let clusterQuotas : NetworkQuotas =
    { ContainerMaxCpuMili = 4000
      ContainerMaxMemMebi = 8000
      NamespaceQuotaLimCpuMili = 80000
      NamespaceQuotaLimMemMebi = 62000
      NamespaceQuotaReqCpuMili = 10000
      NamespaceQuotaReqMemMebi = 22000
      NumConcurrentMissions = 1 }
