// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNetworkCfg

open stellar_dotnet_sdk

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
      peerKeys : KeyPair array }

    member self.NumPeers : int =
        Array.length self.peerKeys

    member self.NamespaceProperty() : string =
        self.networkNonce.ToString()


// Generates a fresh network of size n, with fresh keypairs for each node, and a
// random nonce to isolate the network.
let MakeNetworkCfg (n:int) : NetworkCfg =
    { networkNonce = MakeNetworkNonce()
      peerKeys = Array.init n (fun _ -> KeyPair.Random()) }

