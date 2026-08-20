// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionQuorumIntersectionChecker

// Verifies stellar-core's (V2) quorum intersection checker on a live network.
// Topology:
//
//  v1 ⇄ v2            left pair: each 3-of-3 {v0,v1,v2}
//    ⇅  ⇅
//     v0              bridge  before: BOTH(2-of-3 {v0,v1,v2}, 2-of-3 {v3,v4,v5})
//    ⇗⇑⇖                      after : EITHER(same two inner sets)
//  v3 ⇄ v4 ⇄ v5       right trio: each 3-of-4 {v0,v3,v4,v5}
//
// The network initially enjoys quorum intersection and the checker flags the
// bridge as intersection-critical. The mission then restarts **only** the
// bridge with its threshold relaxed from 100% -> 50% ("both inner sets" to
// "either"), which creates two disjoint quorums: {v0,v1,v2} and {v3,v4,v5}.
//
// The bridge rejoins on side-b's live lineage, so its new quorum set propagates
// at current slots and side-b detect the split. side-a stalls during the
// bridge's restart and can never recover: each side-a node's only quorum slice
// contains the other stalled sibling, so neither can confirm a newer slot (but
// since side-a is stalled but not forked, it can't crash the network, so we
// have a clean detection). Detection is asserted through side-b's /quorum
// reporting, including the exact potential_split contents.

open Logging
open PollRetry
open StellarCoreSet
open StellarCorePeer
open StellarCoreHTTP
open StellarFormation
open StellarStatefulSets
open StellarMissionContext
open StellarSupercluster
open StellarDotnetSdk.Accounts
open k8s

let quorumIntersectionChecker (context: MissionContext) =
    // Keys are generated up front (rather than inside MakeLiveCoreSet) so the
    // explicit quorum sets below can cross-reference nodes in other core sets.
    let bridgeKeys = [| KeyPair.Random() |]
    let sideAKeys = Array.init 2 (fun _ -> KeyPair.Random())
    let sideBKeys = Array.init 3 (fun _ -> KeyPair.Random())
    let v0 = bridgeKeys.[0]

    let groupA = [ ("bridge-0", v0); ("side-a-0", sideAKeys.[0]); ("side-a-1", sideAKeys.[1]) ]

    let groupB =
        [ ("side-b-0", sideBKeys.[0])
          ("side-b-1", sideBKeys.[1])
          ("side-b-2", sideBKeys.[2]) ]

    let qset (pct: int) (members: (string * KeyPair) list) (inner: ExplicitQuorumSet list) : ExplicitQuorumSet =
        { thresholdPercent = Some pct
          validators = members |> List.map (fun (n, k) -> (PeerShortName n, k)) |> Map.ofList
          innerQuorumSets = Array.ofList inner }

    // stellar-core converts THRESHOLD_PERCENT p over n entries to threshold
    // 1 + (n*p - 1)/100 (round up): 66% of 3 = 2, 100% of 3 = 3, 75% of 4 = 3,
    // 100% of 2 inner sets = both, 50% of 2 inner sets = either.
    let bridgeQsetBoth = qset 100 [] [ qset 66 groupA []; qset 66 groupB [] ]
    let bridgeQsetEither = qset 50 [] [ qset 66 groupA []; qset 66 groupB [] ]
    let sideAQset = qset 100 groupA []
    let sideBQset = qset 75 (("bridge-0", v0) :: groupB) []

    let mkOptions (nodeCount: int) (q: ExplicitQuorumSet) =
        { CoreSetOptions.GetDefault context.image with
              nodeCount = nodeCount
              quorumSet = ExplicitQuorum q
              quorumSetConfigType = RequireExplicitQset
              quorumIntersectionChecker = true
              useQuorumIntersectionCheckerV2 = true }

    let bridgeCoreSet = MakeLiveCoreSetWithKeys "bridge" bridgeKeys (mkOptions 1 bridgeQsetBoth)
    let sideACoreSet = MakeLiveCoreSetWithKeys "side-a" sideAKeys (mkOptions 2 sideAQset)
    let sideBCoreSet = MakeLiveCoreSetWithKeys "side-b" sideBKeys (mkOptions 3 sideBQset)
    let allCoreSets = [ bridgeCoreSet; sideACoreSet; sideBCoreSet ]

    let expectedSplit =
        Set.ofList [ Set.ofList (List.map (fun (_, k: KeyPair) -> k.Address) groupA)
                     Set.ofList (List.map (fun (_, k: KeyPair) -> k.Address) groupB) ]

    context.ExecuteWithOptionalConsistencyCheck
        allCoreSets
        None
        false
        (fun (formation: StellarFormation) ->
            formation.WaitUntilSynced allCoreSets
            formation.UpgradeProtocolToLatest allCoreSets

            let sideAPeers = [ for i in 0 .. 1 -> formation.NetworkCfg.GetPeer sideACoreSet i ]
            let sideBPeers = [ for i in 0 .. 2 -> formation.NetworkCfg.GetPeer sideBCoreSet i ]

            // The bridge's CoreSet record is replaced by ChangeCoreSetOptions,
            // so always re-fetch it when constructing its Peer.
            let bridgePeer () =
                formation.NetworkCfg.GetPeer(formation.NetworkCfg.FindCoreSet bridgeCoreSet.name) 0

            let assertOn (peer: Peer) (cond: bool) (msg: string) =
                if not cond then failwithf "%s on %s" msg peer.ShortName.StringName

            // Renders a group list (reported by /quorum) for failure messages
            let showGroups (groups: Set<string> list) = groups |> List.map (String.concat ",") |> String.concat "; "

            // Asserts a scp.qic counter, fetching it exactly once and quoting
            // the observed value on failure.
            let assertMetric (peer: Peer) (name: string) (ok: int -> bool) (msg: string) =
                let observed = peer.GetQicMetricCount name
                assertOn peer (ok observed) (sprintf "%s (%s=%d)" msg name observed)

            // ---- Phase 0: every node sees a 6-node intersecting network ----
            for peer in bridgePeer () :: sideAPeers @ sideBPeers do
                // Since the checker recomputes whenever it detects a quorum
                // change, we wait until the result is computed over the full 6
                // node network.
                RetryUntilTrue
                    (fun _ ->
                        match peer.TryGetQuorumIntersectionInfo() with
                        | Some qi -> qi.nodeCount = 6
                        | None -> false)
                    (fun _ -> LogInfo "Waiting for full-network checker results on %s" peer.ShortName.StringName)

                let qi = peer.GetQuorumIntersectionInfo()
                assertOn peer qi.intersection "expected quorum intersection"

                assertOn
                    peer
                    (List.contains (Set.singleton v0.Address) qi.criticalGroups)
                    (sprintf
                        "expected bridge node %s in intersection-critical groups, observed [%s]"
                        v0.Address
                        (showGroups qi.criticalGroups))

                assertMetric peer "successful-run" (fun n -> n > 0) "expected successful checker runs"
                assertMetric peer "failed-run" (fun n -> n = 0) "checker runs failed"
                assertMetric peer "aborted-run" (fun n -> n = 0) "checker runs aborted"

            LogInfo "Phase 0 verified: network enjoys quorum intersection; bridge is intersection-critical"

            // Baselines. The scp.qic counters are cumulative and include
            // bootstrap-time runs on partial quorum maps, we need to track the
            // baseline and assert on deltas.
            let splitBaselines = [ for p in sideBPeers -> (p, p.GetQicMetricCount "result-potential-split") ]

            // side-a's verdicts are about to freeze; record where.
            let frozenBaselines = [ for p in sideAPeers -> (p, (p.GetQuorumIntersectionInfo()).lastCheckLedger) ]

            // ---- The flip: restart **only** the bridge, threshold 100% (both) -> 50% (either) ----
            formation.Stop bridgeCoreSet.name
            formation.ChangeCoreSetOptions bridgeCoreSet.name (mkOptions 1 bridgeQsetEither)
            formation.Start bridgeCoreSet.name
            formation.WaitUntilSynced [ formation.NetworkCfg.FindCoreSet bridgeCoreSet.name ]
            LogInfo "Bridge restarted with EITHER-side quorum set"

            // ---- Phase 1: detection on side-b, via /quorum (exact split contents) ----
            for peer in sideBPeers do
                RetryUntilTrue
                    (fun _ ->
                        match peer.TryGetQuorumIntersectionInfo() with
                        | Some qi -> not qi.intersection
                        | None -> false)
                    (fun _ -> LogInfo "Waiting for %s to detect quorum split" peer.ShortName.StringName)

                let qi = peer.GetQuorumIntersectionInfo()

                match qi.potentialSplit with
                | None -> failwithf "no potential_split reported on %s" peer.ShortName.StringName
                | Some (a, b) ->
                    assertOn
                        peer
                        (Set.ofList [ a; b ] = expectedSplit)
                        (sprintf "unexpected potential_split [%s] vs [%s]" (String.concat "," a) (String.concat "," b))

                assertMetric peer "failed-run" (fun n -> n = 0) "checker runs failed"
                assertMetric peer "aborted-run" (fun n -> n = 0) "checker runs aborted"
                assertMetric peer "result-unknown" (fun n -> n = 0) "checker returned unknown"

            for (peer, baseline) in splitBaselines do
                assertMetric
                    peer
                    "result-potential-split"
                    (fun n -> n > baseline)
                    (sprintf "expected result-potential-split counter to increase above baseline %d" baseline)

            // ---- detection on the bridge, via scp.qic metrics ----
            RetryUntilTrue
                (fun _ -> (bridgePeer ()).GetQicMetricCount "result-potential-split" > 0)
                (fun _ ->
                    LogInfo
                        "Waiting for %s to detect quorum split (via scp.qic metrics)"
                        (bridgePeer ()).ShortName.StringName)

            let bridge = bridgePeer ()
            assertMetric bridge "successful-run" (fun n -> n > 0) "expected successful checker runs"
            assertMetric bridge "failed-run" (fun n -> n = 0) "checker runs failed"
            assertMetric bridge "aborted-run" (fun n -> n = 0) "checker runs aborted"
            assertMetric bridge "result-unknown" (fun n -> n = 0) "checker returned unknown"

            LogInfo "Phase 1 verified: side-b detected the split live via /quorum; bridge via scp.qic metrics"

            // ---- Check the left side (side-a) remain frozen ----
            // Quorum intersection checker only computes during externalize and
            // since side A has not been closing ledger, the intersection status
            // will be outdated (true).
            (List.head sideBPeers).WaitForFewLedgers 5

            for (peer, baseline) in frozenBaselines do
                let qi = peer.GetQuorumIntersectionInfo()

                assertOn
                    peer
                    qi.intersection
                    (sprintf
                        "expected the frozen (stale) intersection=true verdict, observed intersection=%b"
                        qi.intersection)

                assertOn
                    peer
                    (qi.lastCheckLedger = baseline)
                    (sprintf "expected last_check_ledger to be frozen at %d, observed %d" baseline qi.lastCheckLedger)

            LogInfo "Frozen-left verified: severed nodes keep the stale pre-change verdict"

            // ---- No-harm: nobody crashed, the live side keeps closing ----
            for peer in [ bridgePeer (); List.head sideBPeers ] do
                peer.WaitForFewLedgers 2

            for peer in bridgePeer () :: sideAPeers @ sideBPeers do
                peer.CheckNoErrorMetrics false

                let pod =
                    formation.Kube.ReadNamespacedPod(
                        name = peer.PodName.StringName,
                        namespaceParameter = formation.NetworkCfg.NamespaceProperty
                    )

                for cs in pod.Status.ContainerStatuses do
                    assertOn
                        peer
                        (cs.RestartCount = 0)
                        (sprintf "container %s restarted %d times" cs.Name cs.RestartCount)

            LogInfo "No-harm verified: zero restarts, no error metrics, live side advancing")
