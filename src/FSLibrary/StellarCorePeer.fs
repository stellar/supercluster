// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCorePeer

open stellar_dotnet_sdk

open StellarCoreCfg
open StellarCoreSet
open StellarNetworkCfg

type Peer =
    { networkCfg: NetworkCfg
      coreSet: CoreSet
      peerNum: int }

    member self.ShortName =
        CfgVal.peerShortName self.coreSet self.peerNum

    member self.DNSName =
        CfgVal.peerDNSName self.networkCfg.namespaceProperty self.coreSet self.peerNum


type NetworkCfg with
    member self.GetPeer (coreSet: CoreSet) i : Peer =
        { networkCfg = self;
          coreSet = coreSet;
          peerNum = i }

    member self.EachPeer f =
        for coreSet in self.coreSetList do
            for i in 0..(coreSet.CurrentCount - 1) do
                f (self.GetPeer coreSet i)

    member self.EachPeerInSets (coreSetArray: CoreSet array) f =
        for coreSet in coreSetArray do
            for i in 0..(coreSet.CurrentCount - 1) do
                f (self.GetPeer coreSet i)
