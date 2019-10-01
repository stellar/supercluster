// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNetworkCfg

open stellar_dotnet_sdk

open StellarCoreSet

// Random nonce used as both a Kubernetes namespace and a stellar network
// identifier to qualify the remaining objects we construct.

type NetworkNonce =
    | NetworkNonce of byte array
    override self.ToString() =
        let (NetworkNonce n) = self
        // "ssc" == "stellar supercluster", just to help group namespaces.
        "ssc-" + (Util.BytesToHex n).ToLower()


let MakeNetworkNonce() : NetworkNonce =
    let bytes : byte array = Array.zeroCreate 6
    let rng = System.Security.Cryptography.RNGCryptoServiceProvider.Create()
    rng.GetBytes(bytes) |> ignore
    NetworkNonce bytes

// Symbolic type for the different sorts of NETWORK_PASSPHRASE that can show up
// in a stellar-core.cfg file. Usually use PrivateNet, which takes a nonce and
// will not collide with any other networks.
type NetworkPassphrase =
    | SDFTestNet
    | SDFMainNet
    | PrivateNet of NetworkNonce
    override self.ToString() : string =
        match self with
        | SDFTestNet -> "Test SDF Network ; September 2015"
        | SDFMainNet -> "Public Global Stellar Network ; September 2015"
        | PrivateNet n -> sprintf "Private test network '%s'" (n.ToString())

// A logical group of stellar-core peers (and possibly other resources like
// horizon peers, if we get there). The network nonce will be used to form a k8s
// namespace, which isolates all subsequent resources related to this network
// from any others on the k8s cluster. Deleting the namespace from k8s will
// delete all the associated Pods, ConfigMaps, Services, Ingresses and such.
//
// Other objects (StellarCoreCfg structures and .cfg files, Kubernetes objects
// for representing the network, etc.) should be derived from this.

type NetworkCfg =
    { networkNonce : NetworkNonce
      networkPassphrase : NetworkPassphrase
      namespaceProperty : string
      coreSets : Map<string,CoreSet>
      quotaLimitCPU: int
      quotaLimitMemoryMB: int
      ingressDomain : string }

    member self.FindCoreSet (n:string) : CoreSet =
        Map.find n self.coreSets

    member self.Nonce : string =
        self.networkNonce.ToString()

    member self.NamespaceProperty : string =
        self.namespaceProperty

    member self.MapAllPeers<'a> (f:CoreSet->int->'a) : 'a array =
        let mapCoreSetPeers (cs:CoreSet) = Array.mapi (fun i k -> f cs i) cs.keys
        Map.toArray self.coreSets
        |> Array.collect (fun (_, cs) -> mapCoreSetPeers cs)

    member self.MaxPeerCount : int =
        Map.fold (fun n k v -> n + v.options.nodeCount) 0 self.coreSets

    member self.CoreSetList : CoreSet list =
        Map.toList self.coreSets |> List.map (fun (_, v) -> v)

    member self.PeerSetName (cs:CoreSet) : string =
        sprintf "%s-peer-%s" self.Nonce cs.name

    member self.PeerShortName (cs:CoreSet) (n:int) : string =
        sprintf "%s-%d" (self.PeerSetName cs) n

    member self.PeerCfgName (cs:CoreSet) (n:int) : string =
        sprintf "%s.cfg" (self.PeerShortName cs n)

    member self.ServiceName : string =
        sprintf "%s-stellar-core" self.Nonce

    member self.IngressName : string =
        sprintf "%s-stellar-core-ingress" self.Nonce

    member self.CfgMapName : string =
        sprintf "%s-stellar-core-cfg" self.Nonce

    member self.PeerDNSName (cs:CoreSet) (n:int) : string =
        sprintf "%s.%s.%s.svc.cluster.local"
            (self.PeerShortName cs n)
            self.ServiceName
            self.namespaceProperty

    member self.IngressHostName : string =
        sprintf "%s.%s" self.Nonce self.ingressDomain

    member self.WithLive name (live: bool) =
        let coreSet = self.FindCoreSet(name).WithLive live
        { self with coreSets = self.coreSets.Add(name, coreSet) }

// Generates a fresh network of size n, with fresh keypairs for each node, and a
// random nonce to isolate the network.
let MakeNetworkCfg
        (coreSetList: CoreSet list)
        (namespaceProperty: string)
        (quotaLimitCPU: int)
        (quotaLimitMemoryMB: int)
        (ingressDomain: string)
        (passphrase: NetworkPassphrase option) : NetworkCfg =
    let nonce = MakeNetworkNonce()
    { networkNonce = nonce
      networkPassphrase = match passphrase with
                          | None -> PrivateNet nonce
                          | Some(x) -> x
      namespaceProperty = namespaceProperty
      coreSets = List.map (fun cs -> (cs.name, cs)) coreSetList |> Map.ofList
      quotaLimitCPU = quotaLimitCPU
      quotaLimitMemoryMB = quotaLimitMemoryMB
      ingressDomain = ingressDomain }
