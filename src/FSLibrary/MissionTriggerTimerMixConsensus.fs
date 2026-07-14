// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

// This mission tests the EXPERIMENTAL_TRIGGER_TIMER feature with a mix of
// nodes that have it enabled vs disabled, under configurable clock drift
// distributions. It uses generated pubnet topologies (--pubnet-data) and
// overlays trigger timer and clock offset settings onto the CoreSets.
//
// Load is always generated in the same MIXED_PREGEN_* (classic + synthetic
// Soroban) mode as MissionMinBlockTimeMixed, with the same node resources,
// account count, and load duration (see MinBlockTimeTest.fs). Unlike that
// mission we do NOT binary-search for a minimum block time; we run a single
// load pass at a fixed block time and check consensus stays healthy.
//
// CLI parameters:
//   --trigger-timer-flag-pct N        percentage of nodes with the flag (0-100, default 100)
//   --drift-pct N                     percentage of nodes that drift (0-100, default 0)
//   --uniform-drift=lower,upper       uniform random drift in [lower,upper] signed ms (e.g. -2000,+2000)
//   --bimodal-drift=m1,M1,m2,M2      first half in [m1,M1], second half in [m2,M2] signed ms
//   --ledger-close-time-ms N          target ledger close time in ms (default 5000)
//   --min-block-time-mixed-mode M     MIXED_PREGEN_* loadgen mode (default mixed_pregen_sac_payment)
//   --classic-tx-rate N               classic TPS (defaults to half of --tx-rate)
//   --soroban-tx-rate N               soroban TPS (defaults to half of --tx-rate)

module MissionTriggerTimerMixConsensus

open Logging
open MinBlockTimeTest
open StellarCoreHTTP
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

// Networks larger than this drive load from tier1 only; smaller ones use every
// node. Matches MinBlockTimeTest.fs / MaxTPSTest.fs.
let private smallNetworkSize = 10

// Round ms to whole seconds, ceiling away from zero: 1500 -> 2, -800 -> -1
let private ceilToSec (ms: int) = if ms >= 0 then (ms + 999) / 1000 else -((abs ms + 999) / 1000)

// Drift suffix for a single offset: 0 -> "", 1500 -> "-p2", -800 -> "-m1"
let private driftSuffix (ms: int) =
    let s = ceilToSec ms

    if s > 0 then sprintf "-p%d" s
    elif s < 0 then sprintf "-m%d" (abs s)
    else ""

// Pod names are "<nonce>-sts-<name>-<n>" and Kubernetes rejects any name longer
// than 63 chars (it appends an 11-char nonce of its own; see StellarCoreSet.fs).
// With a 16-char run nonce plus the "-sts-"/"-N" scaffolding, the CoreSet name
// itself only has ~29 chars of headroom. Real pubnet home-domain names (e.g.
// "blockdaemon-non-tier1") plus our "-expr"/drift annotations overflow that, so
// we cap the name length here.
let private maxCoreSetNameLen = 26

// Build an annotated CoreSet name: "<home-domain>-<idx><flag><drift>". The
// globally-unique idx guarantees the name stays distinct even after the
// descriptive home-domain prefix is truncated to fit the pod-name budget.
let private annotateName (baseName: string) (idx: int) (flagEnabled: bool) (offsetMs: int) =
    let flagPart = if flagEnabled then "-expr" else ""
    let suffix = sprintf "-%d%s%s" idx flagPart (driftSuffix offsetMs)
    let budget = maxCoreSetNameLen - suffix.Length

    let prefix =
        if baseName.Length > budget then
            baseName.Substring(0, max 0 budget).TrimEnd('-')
        else
            baseName

    CoreSetName(prefix + suffix)

// Drift beyond 10 minutes is surely a configuration mistake. Bounding the
// range also keeps the arithmetic on drift values (ceilToSec, rng.Next
// upper+1) safely away from Int32 overflow.
let private maxDriftMagnitudeMs = 600000

let private checkDriftMs (ms: int) =
    if ms > maxDriftMagnitudeMs || ms < -maxDriftMagnitudeMs then
        failwith (sprintf "clock drift values must be within +/-%d ms, got %d" maxDriftMagnitudeMs ms)

let private parseDrift (context: MissionContext) : ClockDriftDistribution =
    List.iter checkDriftMs (context.uniformDrift @ context.bimodalDrift)

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
    // This mission assigns the trigger timer per node; a blanket setting for
    // all nodes would defeat its purpose.
    if baseContext.enableTriggerTimer.IsSome then
        failwith
            "--enable-trigger-timer is not supported by TriggerTimerMixConsensus; use --trigger-timer-flag-pct instead"

    let drift = parseDrift baseContext
    let flagPct = baseContext.triggerTimerFlagPct
    let driftPct = baseContext.driftPct

    if flagPct < 0 || flagPct > 100 then
        failwith (sprintf "trigger-timer-flag-pct must be 0-100, got %d" flagPct)

    if driftPct < 0 || driftPct > 100 then
        failwith (sprintf "drift-pct must be 0-100, got %d" driftPct)

    match drift with
    | NoDrift when driftPct > 0 ->
        failwith "drift-pct > 0 but no drift distribution specified (use --uniform-drift or --bimodal-drift)"
    | UniformDrift _
    | BimodalDrift _ when driftPct = 0 ->
        failwith "drift distribution specified but drift-pct is 0 (set --drift-pct to a value > 0)"
    | _ -> ()

    // Mixed (classic + synthetic Soroban) load, configured exactly like
    // MissionMinBlockTimeMixed. Split --tx-rate evenly when neither component
    // rate is given.
    let mode = parseMixedPregenMode baseContext.minBlockTimeMixedMode

    let classicTxRate, sorobanTxRate =
        match baseContext.minBlockTimeMixedClassicTxRate, baseContext.minBlockTimeMixedSorobanTxRate with
        | None, None ->
            let soroban = baseContext.txRate / 2
            baseContext.txRate - soroban, soroban
        | Some classic, None -> classic, 0
        | None, Some soroban -> 0, soroban
        | Some classic, Some soroban -> classic, soroban

    if classicTxRate < 0 || sorobanTxRate < 0 then
        failwith "--classic-tx-rate and --soroban-tx-rate must be non-negative"

    if classicTxRate = 0 && sorobanTxRate = 0 then
        failwith "At least one of --classic-tx-rate or --soroban-tx-rate must be non-zero"

    let txRate = classicTxRate + sorobanTxRate

    // Node resources and load are standardized with MissionMinBlockTime{Classic,
    // Mixed} (see MinBlockTimeTest.fs) so the trigger timer is exercised under
    // the same conditions as the minimum-block-time search. We keep this
    // mission's own knobs (drift, trigger-timer flag pct, block time) but match
    // the perf-test node size, account count, pregenerated tx pool, and load
    // duration.
    //
    // Load duration is the ~5 min window MinBlockTimeTest uses to decide a
    // "success" (txs = txRate * 300), so numTxs = txRate * loadGenDurationSec.
    let loadGenDurationSec = 300

    let genesisAccounts = baseContext.genesisTestAccountCount |> Option.defaultValue 100000

    let numPregeneratedTxs = baseContext.numPregeneratedTxs |> Option.defaultValue 2500000

    // Block time we run (and size tx-set limits) at. Always upgraded before
    // load; defaults to 5000ms when --ledger-close-time-ms is unset.
    let targetMs = baseContext.ledgerCloseTimeMs |> Option.defaultValue 5000

    let context =
        { baseContext with
              txRate = txRate
              numAccounts = genesisAccounts
              numTxs = txRate * loadGenDurationSec
              coreResources = SimulatePubnetTier1PerfResources
              genesisTestAccountCount = Some genesisAccounts
              numPregeneratedTxs = Some numPregeneratedTxs
              enableTailLogging = false
              installNetworkDelay = Some(baseContext.installNetworkDelay |> Option.defaultValue true)
              maxConnections = Some(baseContext.maxConnections |> Option.defaultValue 65) }

    let baseLoadGen =
        { LoadGen.GetDefault() with
              mode = mode
              spikesize = context.spikeSize
              spikeinterval = context.spikeInterval
              offset = 0
              maxfeerate = None
              skiplowfeetxs = false
              classicTxRate = Some classicTxRate
              sorobanTxRate = Some sorobanTxRate }

    let baseCoreSets = FullPubnetCoreSets context true false

    let totalNodes = List.sumBy (fun (cs: CoreSet) -> cs.options.nodeCount) baseCoreSets

    LogInfo
        "TriggerTimerMixConsensus: %d total nodes, flag-pct=%d%%, drift-pct=%d%%, mode=%s, classic-tps=%d, soroban-tps=%d, block-time=%dms"
        totalNodes
        flagPct
        driftPct
        (mode.ToString())
        classicTxRate
        sorobanTxRate
        targetMs

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
            if rng.Next(2) = 0 then rng.Next(min1, max1 + 1) else rng.Next(min2, max2 + 1)

    // Walk through CoreSets, splitting each into single-node CoreSets so that
    // each node gets its own name with flag/drift annotation. A monotonic index
    // gives every node a globally-unique name even after truncation.
    let mutable nodeIdx = 0

    let modifiedCoreSets =
        baseCoreSets
        |> List.collect
            (fun cs ->
                let nc = cs.options.nodeCount

                [ for j in 0 .. nc - 1 do
                      let flagEnabled = sampleFlag ()
                      let offset = sampleOffset ()
                      let idx = nodeIdx
                      nodeIdx <- nodeIdx + 1

                      let annotatedName = annotateName cs.name.StringName idx flagEnabled offset

                      LogInfo "  Node %s: trigger_timer=%b, offset=%d" annotatedName.StringName flagEnabled offset

                      { cs with
                            name = annotatedName
                            keys = [| cs.keys.[j] |]
                            options =
                                { cs.options with
                                      nodeCount = 1
                                      nodeLocs = cs.options.nodeLocs |> Option.map (fun locs -> [ locs.[j] ])
                                      experimentalTriggerTimer = if flagEnabled then Some true else None
                                      clockOffsets = if offset <> 0 then Some [ offset ] else None } } ])

    let tier1 = List.filter (fun (cs: CoreSet) -> cs.options.tier1 = Some true) modifiedCoreSets

    // Mirror MissionMinBlockTime{Classic,Mixed} (see MinBlockTimeTest.fs): on
    // larger networks the tier1 nodes drive load; on tiny ones every node does.
    let loadGenNodes =
        if List.length modifiedCoreSets > smallNetworkSize then
            tier1
        else
            modifiedCoreSets

    // MIXED_PREGEN_* load runs on a subset of the loadgen nodes sized to the
    // requested per-type TPS (at least one), as in MinBlockTimeTest.
    let activeLoadGenNodes =
        let requestedCount = max classicTxRate sorobanTxRate

        loadGenNodes
        |> List.truncate (min (List.length loadGenNodes) (max 1 requestedCount))

    let isLoadGenNode cs = List.exists (fun (cs': CoreSet) -> cs' = cs) loadGenNodes

    // Pre-generate signed txs on the loadgen nodes: partition the genesis
    // accounts evenly across the active loadgen nodes and assign each one a
    // distinct account slice via its offset (mirrors MinBlockTimeTest's mixed
    // pregen partitioning).
    let partitionCount = List.length activeLoadGenNodes
    let accountsPerNode = genesisAccounts / partitionCount
    let mutable partitionIdx = 0

    let preparedCoreSets =
        modifiedCoreSets
        |> List.map
            (fun cs ->
                let pregenerateTxs =
                    if isLoadGenNode cs then
                        let i = partitionIdx % partitionCount
                        partitionIdx <- partitionIdx + 1
                        Some(numPregeneratedTxs, accountsPerNode, accountsPerNode * i)
                    else
                        Some(0, 1, 0)

                { cs with
                      options =
                          { cs.options with
                                initialization = { cs.options.initialization with pregenerateTxs = pregenerateTxs } } })

    // The fixed load pass: same shape as MinBlockTimeTest's evaluateAt, minus
    // the binary search. RunMultiLoadgen splits accounts/txs/per-type TPS across
    // the active nodes.
    let loadGen =
        { baseLoadGen with
              accounts = genesisAccounts
              txs = txRate * loadGenDurationSec
              txrate = txRate }

    context.ExecuteWithOptionalConsistencyCheck
        preparedCoreSets
        None
        false
        (fun (formation: StellarFormation) ->
            formation.WaitUntilConnected preparedCoreSets
            formation.ManualClose tier1
            formation.WaitUntilSynced preparedCoreSets

            formation.UpgradeProtocolToLatest tier1

            LogInfo "Upgrading target ledger close time to %d ms" targetMs
            formation.UpgradeSCPTargetLedgerCloseTime tier1 targetMs

            // Raise classic and Soroban network limits to fit the requested TPS
            // at the target block time before applying load.
            formation.UpgradeMaxTxSetSize tier1 (classicMaxTxSetSizeForTarget targetMs classicTxRate)
            upgradeMixedPregenSorobanLimits formation tier1 baseLoadGen targetMs

            // MIXED_PREGEN_* modes are overlay-only loadgen modes, so enable
            // overlay-only mode for the duration of the load pass.
            withOverlayOnlyMode
                formation
                preparedCoreSets
                (fun () -> formation.RunMultiLoadgen activeLoadGenNodes loadGen)

            formation.CheckNoErrorsAndPairwiseConsistency()
            formation.EnsureAllNodesInSync preparedCoreSets)
