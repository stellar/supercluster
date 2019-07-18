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
    ignore (rng.GetBytes(bytes))
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
      mutable coreSets : CoreSet list
      ingressPort : int }

    member self.Find (n:string) =
        (List.find (fun x -> (x.name = n)) self.coreSets)

    member self.NamespaceProperty : string =
        self.networkNonce.ToString()

    member self.MapCoreSetPeers f cs =
        Array.mapi (fun i k -> f cs i) cs.keys

    member self.MapAllPeers f =
        List.map (self.MapCoreSetPeers f) self.coreSets |> Array.concat

    member self.ChangeCount name count =
        let cs = self.Find(name)
        cs.SetCurrentCount count
        let newCoreSets = List.filter (fun x -> (x. name <> name)) self.coreSets
        self.coreSets <- cs :: newCoreSets

// Generates a fresh network of size n, with fresh keypairs for each node, and a
// random nonce to isolate the network.
let MakeNetworkCfg (c,p,passprhase) : NetworkCfg =
    let nonce = MakeNetworkNonce()
    { networkNonce = nonce
      networkPassphrase = match passprhase with
                          | None -> PrivateNet nonce
                          | Some(x) -> x
      coreSets = c
      ingressPort = p }
