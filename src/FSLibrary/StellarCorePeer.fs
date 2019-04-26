// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCorePeer

open stellar_dotnet_sdk

open StellarCoreCfg
open StellarNetworkCfg

type Peer =
    { networkCfg: NetworkCfg
      peerNum: int }

    member self.ShortName =
        CfgVal.peerShortName self.peerNum

    member self.DNSName =
        CfgVal.peerDNSName self.networkCfg.networkNonce self.peerNum


type NetworkCfg with
    member self.GetPeer (i:int) : Peer =
        { networkCfg = self;
          peerNum = i }

    member self.EachPeer f =
        for i in 0..(self.NumPeers-1) do
            f (self.GetPeer i)
