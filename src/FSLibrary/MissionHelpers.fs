// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHelpers

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarSupercluster

let obtainVersion (context: MissionContext) image =
    let coreSet = MakeCoreSet "obtain-version" 1 1 { CoreSetOptions.Default with image = Some(image) }
    let networkCfg = MakeNetworkCfg([coreSet], context.ingressPort, None)
    use f = context.kube.MakeFormation networkCfg (Some(context.persistentVolume)) context.keepData context.probeTimeout

    let peer = networkCfg.GetPeer coreSet 0
    Some(peer.GetProtocolVersion)
