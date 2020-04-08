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

type LogLevels =
    { LogDebugPartitions: string list
      LogTracePartitions: string list }

// Resource allocation in k8s is quite complex.
//
// The namespace itself has a _quota_ which applies to the sum of all
// resources allocated within the namespace, so everything at the
// namespace quota level has to be divided by the number of containers
// we're going to instantiate.
//
// That quota in turn consists of a request quota and a limit quota for
// each resource type (CPU and memory), to which the following rules
// apply:
//
//   - Limits and requests of a container must fit within quota
//     (a container won't be scheduled if _either_ exceeds quota)
//   - Requests that can be met are provided by guaranteed resources
//   - Limits can be overprovisioned and are best-effort
//   - Limits must always be greater than requests
//   - A container can never use more than limit, it's a hard boundary
//
// Furthermore there is a container-level maximum for each resource
// that's _independent_ of the number of containers or the namespace's
// remaining quota.
//
// So we want to set limit as high as possible and request as low as
// possible, within the constraints of these quotas and maximums.

type NetworkQuotas =
    { ContainerMaxCpuMili: int
      ContainerMaxMemMebi: int
      NamespaceQuotaLimCpuMili: int
      NamespaceQuotaLimMemMebi: int
      NamespaceQuotaReqCpuMili: int
      NamespaceQuotaReqMemMebi: int
      NumConcurrentMissions: int }

    override self.ToString () =
        sprintf "max [cpu:%d, mem:%d] lim [cpu:%d, mem:%d] req [cpu:%d, mem:%d] missions %d"
            self.ContainerMaxCpuMili self.ContainerMaxMemMebi
            self.NamespaceQuotaLimCpuMili self.NamespaceQuotaLimMemMebi
            self.NamespaceQuotaReqCpuMili self.NamespaceQuotaReqMemMebi
            self.NumConcurrentMissions

    member self.ContainerCpuReqMili (numContainers:int) : int =
        let divisor = numContainers * self.NumConcurrentMissions
        let nsFrac = self.NamespaceQuotaReqCpuMili / divisor
        let lim = (self.ContainerCpuLimMili numContainers)
        min 100 (min nsFrac lim)

    member self.ContainerCpuLimMili (numContainers:int) : int =
       let divisor = numContainers * self.NumConcurrentMissions
       let nsFrac = self.NamespaceQuotaLimCpuMili / divisor
       min nsFrac self.ContainerMaxCpuMili

    member self.ContainerMemReqMebi (numContainers:int) : int =
        let divisor = numContainers * self.NumConcurrentMissions
        let nsFrac = self.NamespaceQuotaReqMemMebi / divisor
        let lim = (self.ContainerMemLimMebi numContainers)
        min 100 (min nsFrac lim)

    member self.ContainerMemLimMebi (numContainers:int) : int =
        let divisor = numContainers * self.NumConcurrentMissions
        let nsFrac = self.NamespaceQuotaLimMemMebi / divisor
        min nsFrac self.ContainerMaxMemMebi


let MakeNetworkQuotas (containerMaxCpuMili: int,
                       containerMaxMemMebi: int,
                       namespaceQuotaLimCpuMili: int,
                       namespaceQuotaLimMemMebi: int,
                       namespaceQuotaReqCpuMili: int,
                       namespaceQuotaReqMemMebi: int,
                       numConcurrentMissions: int) =
    { ContainerMaxCpuMili = containerMaxCpuMili
      ContainerMaxMemMebi = containerMaxMemMebi
      NamespaceQuotaLimCpuMili = namespaceQuotaLimCpuMili
      NamespaceQuotaLimMemMebi = namespaceQuotaLimMemMebi
      NamespaceQuotaReqCpuMili = namespaceQuotaReqCpuMili
      NamespaceQuotaReqMemMebi = namespaceQuotaReqMemMebi
      NumConcurrentMissions = numConcurrentMissions }

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
      coreSets : Map<CoreSetName,CoreSet>
      jobCoreSetOptions : CoreSetOptions option
      quotas: NetworkQuotas
      logLevels: LogLevels
      storageClass: string
      ingressDomain : string }

    member self.FindCoreSet (n:CoreSetName) : CoreSet =
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

    member self.StatefulSetName (cs:CoreSet) : string =
        sprintf "%s-sts-%s" self.Nonce cs.name.StringName

    member self.PeerShortName (cs:CoreSet) (n:int) : PeerShortName =
        PeerShortName (sprintf "%s-%d" (self.StatefulSetName cs) n)

    member self.ServiceName : string =
        sprintf "%s-stellar-core" self.Nonce

    member self.IngressName : string =
        sprintf "%s-stellar-core-ingress" self.Nonce

    member self.JobName(i:int) : string =
        sprintf "%s-stellar-core-job-%d" self.Nonce i

    member self.PeerCfgMapName (cs:CoreSet) (i:int) : string =
        sprintf "%s-cfg-map" (self.PeerShortName cs i).StringName

    member self.JobCfgMapName : string =
        sprintf "%s-job-cfg-map" self.Nonce

    member self.HistoryCfgMapName : string =
        sprintf "%s-history-cfg-map" self.Nonce

    member self.PeerDnsName (cs:CoreSet) (n:int) : PeerDnsName =
        let s = sprintf "%s.%s.%s.svc.cluster.local"
                    (self.PeerShortName cs n).StringName
                    self.ServiceName
                    self.namespaceProperty
        PeerDnsName s

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
        (quotas: NetworkQuotas)
        (logLevels: LogLevels)
        (storageClass: string)
        (ingressDomain: string)
        (passphrase: NetworkPassphrase option) : NetworkCfg =
    let nonce = MakeNetworkNonce()
    { networkNonce = nonce
      networkPassphrase = match passphrase with
                          | None -> PrivateNet nonce
                          | Some(x) -> x
      namespaceProperty = namespaceProperty
      coreSets = List.map (fun cs -> (cs.name, cs)) coreSetList |> Map.ofList
      jobCoreSetOptions = None
      quotas = quotas
      logLevels = logLevels
      storageClass = storageClass
      ingressDomain = ingressDomain }
