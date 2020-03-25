// Copyright 2020 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarNetworkDelays
open k8s
open k8s.Models
open StellarCoreSet
open StellarFormation
open StellarShellCmd
open StellarRemoteCommandExec

// This module calculates simulated network delays based on geographic distance
// between simulated peers, as well as the linux traffic-control commands
// necessary to install such delays on a given formation.

// Geographic calculations for simulated network delays.
let greatCircleDistanceInKm (loc1:GeoLoc) (loc2:GeoLoc) : double =
    // So-called "Haversine formula" for approximate distances

    let earthRadiusInKm = 6371.0
    let degreesToRadians deg = (System.Math.PI / 180.0) * deg
    let lat1InRadians = degreesToRadians loc1.lat
    let lat2InRadians = degreesToRadians loc2.lat
    let deltaLatInDegrees = System.Math.Abs(loc2.lat-loc1.lat)
    let deltaLonInDegrees = System.Math.Abs(loc2.lon-loc1.lon)
    let deltaLatInRadians = degreesToRadians deltaLatInDegrees
    let deltaLonInRadians = degreesToRadians deltaLonInDegrees
    let sinHalfDeltaLat = System.Math.Sin(deltaLatInRadians/2.0)
    let sinHalfDeltaLon = System.Math.Sin(deltaLonInRadians/2.0)
    let a = ((sinHalfDeltaLat * sinHalfDeltaLat) +
             (sinHalfDeltaLon * sinHalfDeltaLon *
              System.Math.Cos(lat1InRadians) * System.Math.Cos(lat2InRadians)))
    let c = 2.0 * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1.0 - a))
    earthRadiusInKm * c

let networkDelayInMs (loc1:GeoLoc) (loc2:GeoLoc) : double =
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

let networkPingInMs (loc1:GeoLoc) (loc2:GeoLoc) : double =
    // A ping is a round trip, so double one-way delay.
    2.0 * (networkDelayInMs loc1 loc2)

let getNetworkDelayCommands (loc1:GeoLoc) (locsAndIps:(PeerShortName * GeoLoc * string) list) : ShCmd array =
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
        ShCmd.OfStrs [| "ip"; "link"; "set"; "eth0"; "qlen"; "1000" |]

    let clearRootQdisc : ShCmd =
        // The root qdisc may or may not exist; clearing it might or might not
        // fail, so we absorb failure here with an OR-true.
        let c = ShCmd.OfStrs [|"tc"; "qdisc"; "del"; "dev"; "eth0"; "root"|]
        let t = ShCmd.OfStr "true"
        ShCmd.ShOr [|c; t|]

    let addRootQdisc : ShCmd =
        ShCmd.OfStrs [| "tc"; "qdisc"; "add"; "dev"; "eth0"; "root"
                        "handle"; "1:0"; "htb" |]

    let addClass (n:int) : ShCmd =
        ShCmd.OfStrs [| "tc"; "class"; "add"; "dev"; "eth0"
                        "parent"; "1:0"
                        "classid"; sprintf "1:%d" n;
                        "htb"; "rate"; "100mbit" |]

    let addFilter (n:int) (ipaddr:string) : ShCmd =
        ShCmd.OfStrs [| "tc"; "filter"; "add"; "dev"; "eth0"
                        "parent"; "1:0"
                        "protocol"; "ip"; "prio"; "1"
                        "u32"; "match"; "ip"; "dst"; ipaddr
                        "classid"; sprintf "1:%d" n |]

    let addNetemQdisc (n:int) (msDelay:int) : ShCmd =
        ShCmd.OfStrs [| "tc"; "qdisc"; "add"; "dev"; "eth0"
                        "parent"; sprintf "1:%d" n;
                        "netem"; "delay"; sprintf "%dms" msDelay |]

    let perIpCmds : ShCmd array =
        List.mapi
            begin fun (i:int) (_, (loc2:GeoLoc), (ip:string)) ->
                let (msDelay:int) = int(networkDelayInMs loc1 loc2)
                let classNo = 1 + i
                [
                    addClass classNo;
                    addFilter classNo ip;
                    addNetemQdisc classNo msDelay
                ]
            end
            locsAndIps
        |> List.concat |> Array.ofList

    (Array.append
                 [| setDeviceTxQlen; clearRootQdisc; addRootQdisc |]
                 perIpCmds)


type StellarFormation with
    // Returns a (geoloc, ipaddr) pair for each peer we have a GeoLoc for in any
    // of the provided CoreSets. Return value is for InstallNetworkDelays below.
    member self.GetPeerLocsAndIps(coreSetList: CoreSet list) : (PeerShortName * GeoLoc * string) list =
        let ns = self.NetworkCfg.NamespaceProperty
        List.collect
            begin fun (cs:CoreSet) ->
                match cs.options.nodeLocs with
                | None -> []
                | Some(locs) ->
                    List.mapi
                        begin fun (i:int) (loc:GeoLoc) ->
                            let shortName = self.NetworkCfg.PeerShortName cs i
                            let (pod:V1Pod) = self.Kube.ReadNamespacedPod(name = shortName.StringName,
                                                                          namespaceParameter = ns)
                            (shortName, loc, pod.Status.PodIP)
                        end
                        locs
            end
            coreSetList

    // For each peer in the coresets, calculate and install (by running a set of
    // linux traffic-control commands on the peer) a set of network delays
    // between that peer and every other peer in the coresets.
    member self.InstallNetworkDelays(coreSetList: CoreSet list) : unit =
        let locsAndIps = self.GetPeerLocsAndIps coreSetList
        for (peerName, loc1, _ip1) in locsAndIps do
            let mutable cmds = getNetworkDelayCommands loc1 locsAndIps
            while cmds.Length > 0 do
                // Apparently -- goodness knows why! -- we can only send
                // "moderately large" commands over stdin, 4kb or less;
                // any more and dotnet's IO-tasking system deadlocks, or
                // something fails in the transport layer, or such (???).
                //
                // We approximate this limit here with "50 commands at a time"
                // which should be well within the limit given the size of
                // commands we're sending (average 75 bytes).
                //
                // Note also: fsharp array slicing syntax is range-inclusive
                // and _mostly_ throws if you index past the last element,
                // except it allows you to ask for the slice beginning at
                // the array's length, which is the empty slice.
                //
                //  That is: x.[x.Length..] is an empty slice, whereas
                //           x.[..x.Length] is an error. Awesome!
                let lastCmdIdx = cmds.Length - 1
                let chunkLimInc = min lastCmdIdx 50
                let chunk = cmds.[..chunkLimInc]
                let blockCmd = ShCmd.ShSeq chunk
                let nextChunkStart = min (chunkLimInc+1) cmds.Length
                cmds <- cmds.[nextChunkStart..]
                self.RunRemoteCommand(peerName, blockCmd)
            done
