// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

// This mission tests the EXPERIMENTAL_TRIGGER_TIMER feature with a mix of
// nodes that have it enabled vs disabled, under configurable clock drift
// distributions. It uses generated pubnet topologies (--pubnet-data) and
// overlays trigger timer and clock offset settings onto the CoreSets.
//
// CLI parameters:
//   --trigger-timer-flag-pct N        percentage of nodes with the flag (0-100, default 100)
//   --drift-pct N                     percentage of nodes that drift (0-100, default 0)
//   --uniform-drift=lower,upper       uniform random drift in [lower,upper] signed ms (e.g. -2000,+2000)
//   --bimodal-drift=m1,M1,m2,M2      first half in [m1,M1], second half in [m2,M2] signed ms

module MissionTriggerTimerMixConsensus

open Logging
open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarFormation
open StellarMissionContext
open StellarNetworkData
open StellarStatefulSets
open StellarSupercluster

type ClockDriftDistribution =
    | NoDrift
    | UniformDrift of lower: int * upper: int
    | BimodalDrift of min1: int * max1: int * min2: int * max2: int

// Round ms to whole seconds, ceiling away from zero: 1500 -> 2, -800 -> -1
let private ceilToSec (ms: int) =
    if ms >= 0 then (ms + 999) / 1000
    else -((abs ms + 999) / 1000)

// Drift suffix for a single offset: 0 -> "", 1500 -> "-p2", -800 -> "-m1"
let private driftSuffix (ms: int) =
    let s = ceilToSec ms
    if s > 0 then sprintf "-p%d" s
    elif s < 0 then sprintf "-m%d" (abs s)
    else ""

// Build an annotated CoreSet name: append "-expr" if flag enabled, plus drift suffix
let private annotateName (baseName: string) (flagEnabled: bool) (offsetMs: int) =
    let flagPart = if flagEnabled then "-expr" else ""
    CoreSetName(baseName + flagPart + driftSuffix offsetMs)

let private parseDrift (context: MissionContext) : ClockDriftDistribution =
    match context.uniformDrift, context.bimodalDrift with
    | [], [] -> NoDrift
    | [ lower; upper ], [] ->
        if upper < lower then
            failwith (sprintf "uniform-drift requires lower <= upper, got %d,%d" lower upper)
        UniformDrift(lower, upper)
    | [], [ min1; max1; min2; max2 ] ->
        if max1 < min1 then
            failwith (sprintf "bimodal-drift first range requires min <= max, got %d,%d" min1 max1)
        if max2 < min2 then
            failwith (sprintf "bimodal-drift second range requires min <= max, got %d,%d" min2 max2)
        BimodalDrift(min1, max1, min2, max2)
    | _ :: _, _ :: _ -> failwith "Cannot specify both --uniform-drift and --bimodal-drift"
    | u, [] -> failwith (sprintf "--uniform-drift requires exactly 2 values (lower,upper), got %d" u.Length)
    | [], b -> failwith (sprintf "--bimodal-drift requires exactly 4 values (min1,max1,min2,max2), got %d" b.Length)

let triggerTimerMixConsensus (baseContext: MissionContext) =
    let drift = parseDrift baseContext
    let flagPct = baseContext.triggerTimerFlagPct
    let driftPct = baseContext.driftPct

    if flagPct < 0 || flagPct > 100 then
        failwith (sprintf "trigger-timer-flag-pct must be 0-100, got %d" flagPct)

    if driftPct < 0 || driftPct > 100 then
        failwith (sprintf "drift-pct must be 0-100, got %d" driftPct)

    let context =
        { baseContext with
              numAccounts = 40000
              numTxs = 90000
              txRate = 150
              coreResources = MediumTestResources
              genesisTestAccountCount = Some 40000
              installNetworkDelay = Some(baseContext.installNetworkDelay |> Option.defaultValue true)
              maxConnections = Some(baseContext.maxConnections |> Option.defaultValue 65) }

    let baseCoreSets = FullPubnetCoreSets context true false

    let totalNodes =
        List.sumBy (fun (cs: CoreSet) -> cs.options.nodeCount) baseCoreSets

    match drift with
    | NoDrift when driftPct > 0 ->
        failwith "drift-pct > 0 but no drift distribution specified (use --uniform-drift or --bimodal-drift)"
    | _ -> ()

    LogInfo
        "TriggerTimerMixConsensus: %d total nodes, flag-pct=%d%%, drift-pct=%d%%"
        totalNodes
        flagPct
        driftPct

    // Each node independently has a flagPct% chance of having the trigger
    // timer flag enabled, and a driftPct% chance of drifting. When drifting,
    // bimodal nodes have a 50/50 chance of being in the first or second group.
    let rng = System.Random(context.randomSeed)

    let sampleFlag () = rng.Next(100) < flagPct

    let sampleOffset () =
        match drift with
        | NoDrift -> 0
        | _ when rng.Next(100) >= driftPct -> 0
        | UniformDrift (lower, upper) -> rng.Next(lower, upper + 1)
        | BimodalDrift (min1, max1, min2, max2) ->
            if rng.Next(2) = 0 then rng.Next(min1, max1 + 1)
            else rng.Next(min2, max2 + 1)

    // Walk through CoreSets, splitting each into single-node CoreSets so that
    // each node gets its own name with flag/drift annotation.
    let modifiedCoreSets =
        baseCoreSets
        |> List.collect (fun cs ->
            let nc = cs.options.nodeCount

            [ for j in 0 .. nc - 1 do
                  let flagEnabled = sampleFlag ()
                  let offset = sampleOffset ()

                  let baseName =
                      if nc > 1 then sprintf "%s-%d" cs.name.StringName j
                      else cs.name.StringName

                  let annotatedName = annotateName baseName flagEnabled offset

                  LogInfo
                      "  Node %s: trigger_timer=%b, offset=%d"
                      annotatedName.StringName
                      flagEnabled
                      offset

                  { cs with
                        name = annotatedName
                        keys = [| cs.keys.[j] |]
                        options =
                            { cs.options with
                                  nodeCount = 1
                                  nodeLocs =
                                      cs.options.nodeLocs
                                      |> Option.map (fun locs -> [ locs.[j] ])
                                  experimentalTriggerTimer = if flagEnabled then Some true else None
                                  clockOffsets = if offset <> 0 then Some [ offset ] else None } } ])

    let tier1 =
        List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) modifiedCoreSets

    let nonTier1 =
        List.filter (fun (cs: CoreSet) -> cs.options.tier1 <> Some true) modifiedCoreSets

    context.Execute
        modifiedCoreSets
        None
        (fun (formation: StellarFormation) ->
            formation.WaitUntilConnected modifiedCoreSets
            formation.ManualClose tier1
            formation.WaitUntilSynced modifiedCoreSets

            formation.UpgradeProtocolToLatest tier1
            formation.UpgradeMaxTxSetSize tier1 (context.txRate * 10)

            let loadPeer =
                if nonTier1.Length > 0 then nonTier1.[0] else tier1.[0]

            formation.RunLoadgen loadPeer context.GeneratePaymentLoad

            formation.CheckNoErrorsAndPairwiseConsistency()
            formation.EnsureAllNodesInSync modifiedCoreSets)
