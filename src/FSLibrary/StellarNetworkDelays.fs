// Copyright 2020 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNetworkDelays

open StellarCoreSet
open StellarNetworkCfg
open StellarShellCmd

// This module calculates simulated network delays based on geographic distance
// between simulated peers, as well as the linux traffic-control commands
// necessary to install such delays on a given formation.

// Geographic calculations for simulated network delays.
let greatCircleDistanceInKm (loc1: GeoLoc) (loc2: GeoLoc) : double =
    // So-called "Haversine formula" for approximate distances

    let earthRadiusInKm = 6371.0
    let degreesToRadians deg = (System.Math.PI / 180.0) * deg
    let lat1InRadians = degreesToRadians loc1.lat
    let lat2InRadians = degreesToRadians loc2.lat
    let deltaLatInDegrees = System.Math.Abs(loc2.lat - loc1.lat)
    let deltaLonInDegrees = System.Math.Abs(loc2.lon - loc1.lon)
    let deltaLatInRadians = degreesToRadians deltaLatInDegrees
    let deltaLonInRadians = degreesToRadians deltaLonInDegrees
    let sinHalfDeltaLat = System.Math.Sin(deltaLatInRadians / 2.0)
    let sinHalfDeltaLon = System.Math.Sin(deltaLonInRadians / 2.0)

    let a =
        ((sinHalfDeltaLat * sinHalfDeltaLat)
         + (sinHalfDeltaLon
            * sinHalfDeltaLon
            * System.Math.Cos(lat1InRadians)
            * System.Math.Cos(lat2InRadians)))

    let c = 2.0 * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1.0 - a))
    earthRadiusInKm * c

let networkDelayInMs (loc1: GeoLoc) (loc2: GeoLoc) : double =
    let idealSpeedOfLightInFibre = 200.0 // km/ms
    let km = greatCircleDistanceInKm loc1 loc2
    let ms = km / idealSpeedOfLightInFibre
    // Empirical slowdown is surprisingly variable: some paths (even
    // long-distance ones) run at nearly 80% of the ideal speed of light in
    // fibre; others barely 20%. We consider 50% or a 2x slowdown as "vaguely
    // normal" with significant outliers; unfortunately to get much better you
    // really need to know the path (eg. trans-atlantic happens to be very fast,
    // but classifying a path as trans-atlantic vs. non is .. complicated!)
    let empiricalSlowdownPastIdeal = 2.0
    ms * empiricalSlowdownPastIdeal


let networkPingInMs (loc1: GeoLoc) (loc2: GeoLoc) : double =
    // A ping is a round trip, so double one-way delay.
    2.0 * (networkDelayInMs loc1 loc2)

let getNetworkDelayCommands (loc1: GeoLoc) (locsAndNames: (GeoLoc * PeerDnsName) array) (delay: int option) : ShCmd =
    // Traffic shaping happens using the 'tc' command on linux. This is a
    // complicated command. We build up the commands in pieces.

    // We're going to build a "Queueing Discipline" (qdisc) attached to
    // eth0. Every qdisc has a major number also called its handle which is
    // written n: or 1:0 and this establishes a number-space for classes
    // associated with the qdisc such as n:1, n:2, n:3 and so on. The root qdisc
    // is conventionally called 1:0 and in our case we'll be making one of type
    // "Hierarchical Token Bucket" (htb).
    //
    // Filter rules will be attached to the root qdisc to classify each
    // destination host in the network into its own "class", starting from 1. So
    // traffic to the first peer in the network will go in class 1:1, traffic to
    // the second peer in the network will go in class 1:2, etc.
    //
    // Each of these classes will in turn have a "Network Emulation" (netem)
    // qdisc that is classless and configured to simulate the transmission delay
    // between the "loc1" peer and the destination peer associated with the htb
    // class that's the parent of the netem.
    //
    // Here's a diagram:
    //
    //    root qdisc
    //    (htb) 1:0 --+--> class 1:1 --> netem qdisc, 100ms delay
    //                |
    //                +--> class 1:2 --> netem qdisc, 50ms delay
    //                |
    //                +--> class 1:3 --> netem qdisc, 200ms delay

    let setDeviceTxQlen : ShCmd =
        // the txqlen on eth0 is 0-sized on container virtual devs; this makes
        // subsequent qdiscs attached to it drop packets far more than they
        // should.  See https://bugzilla.redhat.com/show_bug.cgi?id=1152231
        ShCmd.OfStrs [| "ip"
                        "link"
                        "set"
                        "eth0"
                        "qlen"
                        "1000" |]

    let clearRootQdisc : ShCmd =
        // The root qdisc may or may not exist; clearing it might or might not
        // fail, so we absorb failure here with an OR-true.
        let c =
            ShCmd.OfStrs [| "tc"
                            "qdisc"
                            "del"
                            "dev"
                            "eth0"
                            "root" |]

        let t = ShCmd.OfStr "true"
        ShCmd.ShOr [| c; t |]

    let addRootQdisc : ShCmd =
        ShCmd.OfStrs [| "tc"
                        "qdisc"
                        "add"
                        "dev"
                        "eth0"
                        "root"
                        "handle"
                        "1:0"
                        "htb" |]

    let addClass (n: int) : ShCmd =
        ShCmd.OfStrs [| "tc"
                        "class"
                        "add"
                        "dev"
                        "eth0"
                        "parent"
                        "1:0"
                        "classid"
                        sprintf "1:%d" n
                        "htb"
                        "rate"
                        "10gbit" |]

    let peerVar (n: int) : string = sprintf "PEER%d" n

    let resolveName (n: int) (dns: PeerDnsName) : ShCmd =
        let tmpFile = "tmp.txt"
        let varName = peerVar n
        let pv : ShPiece = ShVar(ShName varName)
        let z : ShPiece = ShBare "z"

        let test : ShCmd =
            ShCmd [| ShWord.OfStr "test"
                     ShPieces [| z; pv |]
                     ShWord.OfStr "="
                     ShPieces [| z |] |]

        let probe : ShCmd =
            ShCmd.OfStrs [| "host"
                            "-t"
                            "A"
                            dns.StringName |]

        let probe : ShCmd = probe.StdOutTo tmpFile

        let probe : ShCmd =
            probe.OrElse [| "echo"
                            "temporary failure" |]

        let grep : ShCmd =
            ShCmd.OfStrs [| "grep"
                            "address"
                            tmpFile |]

        let sed : ShCmd =
            ShCmd.OfStrs [| "sed"
                            "-i"
                            "-e"
                            "s/.*address//"
                            tmpFile |]

        let defSub : ShCmd = ShCmd.DefVarSub varName [| "cat"; tmpFile |]
        let then_ = ShSeq [| sed; defSub |]
        let elif_ = [||]
        let else_ = Some(ShCmd.OfStrs [| "sleep"; "1" |])
        let defIf : ShCmd = ShIf(grep, then_, elif_, else_)
        let body : ShCmd = ShSeq [| probe; defIf |]
        let loop : ShCmd = ShWhile(test, body)

        ShSeq [| ShCmd.OfStrs [| "rm"; "-f"; tmpFile |]
                 loop |]

    let peerVarRef (n: int) : string = "${" + (peerVar n) + "}"

    let addFilter (n: int) : ShCmd =
        ShCmd.OfStrs [| "tc"
                        "filter"
                        "add"
                        "dev"
                        "eth0"
                        "parent"
                        "1:0"
                        "protocol"
                        "ip"
                        "prio"
                        "1"
                        "u32"
                        "match"
                        "ip"
                        "dst"
                        peerVarRef n
                        "classid"
                        sprintf "1:%d" n |]

    let addNetemQdisc (n: int) (msDelay: int) : ShCmd =
        ShCmd.OfStrs [| "tc"
                        "qdisc"
                        "add"
                        "dev"
                        "eth0"
                        "parent"
                        sprintf "1:%d" n
                        "netem"
                        "delay"
                        sprintf "%dms" msDelay |]

    let perPeerResolveCmds : ShCmd array =
        Array.mapi
            (fun (i: int) (loc2: GeoLoc, peer: PeerDnsName) ->
                let classNo = 1 + i
                [| resolveName classNo peer |])
            locsAndNames
        |> Array.concat

    let perPeerCmds : ShCmd array =
        Array.mapi
            (fun (i: int) (loc2: GeoLoc, peer: PeerDnsName) ->
                let (msDelay: int) = if delay.IsSome then delay.Value else int (networkDelayInMs loc1 loc2)
                let classNo = 1 + i
                [| addClass classNo; addFilter classNo; addNetemQdisc classNo msDelay |])
            locsAndNames
        |> Array.concat

    let seq =
        Array.concat [| [| setDeviceTxQlen |]
                        perPeerResolveCmds
                        [| clearRootQdisc; addRootQdisc |]
                        perPeerCmds
                        [| ShCmd.OfStrs [| "tc"; "qdisc" |]
                           // End in an infinite sleep-loop, so k8s don't
                           // exit-restart the container endlessly.
                           ShCmd.While [| "true" |] [|
                               "sleep"
                               "1000"
                           |] |] |]

    ShSeq seq


type NetworkCfg with

    member self.LocAndDnsName (cs: CoreSet) (i: int) : (GeoLoc * PeerDnsName) Option =
        match cs.options.nodeLocs with
        | None -> None
        | Some locs -> Some(locs.[i], self.PeerDnsName cs i)

    member self.LocAndDnsNameForKey(k: byte []) : (GeoLoc * PeerDnsName) Option =
        Array.tryPick
            id
            (self.MapAllPeers(fun cs i -> if cs.keys.[i].PublicKey = k then self.LocAndDnsName cs i else None))

    member self.NeedNetworkDelayScript : bool =
        if self.missionContext.installNetworkDelay.IsSome then
            let atLeastOneLocation = Map.exists (fun _ cs -> cs.options.nodeLocs.IsSome) self.coreSets

            if atLeastOneLocation then
                true
            else
                // If there's _some_ geo info, we can extrapolate.
                // However, if there's _no_ geo info, we can't really do anything.
                // Don't install network delay if your topology has no geo info.
                failwith "Network delays can't be installed if no geo info provided."
        else
            false

    member self.NetworkDelayScript (cs: CoreSet) (i: int) : ShCmd =
        match cs.options.nodeLocs with
        | None -> ShCmd.True()
        | Some (locs) ->
            let selfLoc = locs.[i]

            let otherLocsAndNames : (GeoLoc * PeerDnsName) array =
                match cs.options.preferredPeersMap with
                | Some (otherMap) ->
                    let otherKeys = Array.ofList otherMap.[cs.keys.[i].PublicKey]
                    Array.choose self.LocAndDnsNameForKey otherKeys
                | None -> Array.choose id (self.MapAllPeers self.LocAndDnsName)

            getNetworkDelayCommands selfLoc otherLocsAndNames self.missionContext.flatNetworkDelay
