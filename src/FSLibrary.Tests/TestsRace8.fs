// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

// RACE #8 -- the ConfigMap fallback yields a measurement-free profile artifact
// that is indistinguishable from a good one.
//
// readProgressRecord prefers /logs/progress.json on the monitor pod, but on ANY
// failure of that exec it silently falls back to the progress ConfigMap. The
// ConfigMap is a state mirror: job_monitor.py's _state_only() strips every
// profiling field out of it on purpose, to stay under the 1 MiB cap. So the
// fallback hands writeRangeProfile a `completed` map in which every record has
// had all eight measurements removed and only bookkeeping (attempts, count)
// left behind.
//
// The `entry.Count > 0` guard exists to skip measurement-free entries, but
// count is attached to the entry BEFORE the guard runs, so every entry has at
// least one field and every entry passes. The result is an artifact with the
// right number of ranges and zero measurements -- observed twice in the field,
// reporting 0% peakAnonBytes while the monitor's own progress.json carried 99%.
// The next run then sizes from a profile that silently has no data.
//
// These tests assert on the values the production projection actually returns,
// never on the text of the source file.
module Race8Tests

open Xunit
open Newtonsoft.Json.Linq
open MissionHistoryPubnetParallelCatchupV2

/// Exactly job_monitor.py's _PROFILE_ONLY_FIELDS -- the fields _state_only()
/// removes when it mirrors progress.json into the capped ConfigMap.
let private profileOnlyFields =
    [ "peakAnonBytes"; "peakRssBytes"; "peakWorkingSetBytes"; "peakCpuCores"
      "peakEphemeralBytes"; "txApply"; "seconds"; "wallSeconds" ]

/// A completed record the way /logs/progress.json carries it: bookkeeping plus
/// real measurements.
let private measuredRecord (count: int) (anon: int64) =
    let r = JObject()
    r.["attempts"] <- JValue(1)
    r.["count"] <- JValue(count)
    r.["seconds"] <- JValue(120.0)
    r.["wallSeconds"] <- JValue(130.0)
    r.["txApply"] <- JValue(60.0)
    r.["peakAnonBytes"] <- JValue(anon)
    r.["peakWorkingSetBytes"] <- JValue(anon + 1000L)
    r

/// The same record as it survives the ConfigMap mirror.
let private configMapMirrored (record: JObject) =
    let r = record.DeepClone() :?> JObject

    for f in profileOnlyFields do
        r.Remove(f) |> ignore

    r

let private completedMap (pairs: (string * JObject) list) =
    let c = JObject()

    for (k, v) in pairs do
        c.[k] <- v

    c


[<Fact>]
let ``a configmap-mirrored range carries no measurement and must not enter the profile`` () =
    let mirrored = configMapMirrored (measuredRecord 420 900L)

    // Precondition: the mirror really does strip every measurement, leaving
    // only bookkeeping. If this ever stops holding, the rest is meaningless.
    for f in profileOnlyFields do
        Assert.Null(mirrored.[f])

    Assert.NotNull(mirrored.["count"])
    Assert.NotNull(mirrored.["attempts"])

    let ranges = buildRangeProfile (completedMap [ "420", mirrored ])

    Assert.Equal(0, ranges.Count)


[<Fact>]
let ``count alone never satisfies the measurement guard`` () =
    // This is the defeated guard in its smallest form: count is the only field,
    // and it is bookkeeping, not a measurement.
    let onlyCount = JObject()
    onlyCount.["count"] <- JValue(420)

    let ranges = buildRangeProfile (completedMap [ "420", onlyCount ])

    Assert.Equal(0, ranges.Count)


[<Fact>]
let ``a run whose progress came from the configmap produces no profile artifact`` () =
    // The headline symptom: a full-looking artifact, right number of ranges,
    // zero measurements. Writing nothing is correct here -- the next run then
    // falls back to its configured defaults instead of sizing from empty data.
    let completed =
        completedMap
            [ "420", configMapMirrored (measuredRecord 420 900L)
              "840", configMapMirrored (measuredRecord 420 950L)
              "1260", configMapMirrored (measuredRecord 420 990L) ]

    match rangeProfileDocument "pvc" 20000 completed with
    | None -> ()
    | Some doc ->
        let ranges = doc.["ranges"] :?> JObject

        failwithf
            "wrote a profile artifact with %d ranges and zero measurements: %s"
            ranges.Count
            (ranges.ToString())


[<Fact>]
let ``the profile counts only ranges that actually measured something`` () =
    // A partly-degraded read is the dangerous case: the artifact looks
    // populated, so nothing downstream can tell the stripped ranges apart from
    // the measured one.
    let completed =
        completedMap
            [ "420", measuredRecord 420 900L
              "840", configMapMirrored (measuredRecord 420 950L)
              "1260", configMapMirrored (measuredRecord 420 990L) ]

    let ranges = buildRangeProfile completed

    Assert.Equal(1, ranges.Count)
    Assert.NotNull(ranges.["420"])
    Assert.Null(ranges.["840"])
    Assert.Null(ranges.["1260"])


[<Fact>]
let ``a measured run still produces a complete profile`` () =
    // Guard against over-correcting: a good read must still write everything.
    let completed =
        completedMap
            [ "420", measuredRecord 420 900L
              "840", measuredRecord 420 950L ]

    match rangeProfileDocument "pvc" 20000 completed with
    | None -> failwith "refused to write a profile that carries real measurements"
    | Some doc ->
        let ranges = doc.["ranges"] :?> JObject
        Assert.Equal(2, ranges.Count)
        Assert.Equal(900L, ranges.["420"].["peakAnonBytes"].Value<int64>())
        Assert.Equal(950L, ranges.["840"].["peakAnonBytes"].Value<int64>())
        Assert.Equal(60.0, ranges.["420"].["txApply"].Value<float>())
        // count is still carried, and the slicing is inferred from it rather
        // than from the caller's default.
        Assert.Equal(420, ranges.["420"].["count"].Value<int>())
        Assert.Equal(420, doc.["ledgersPerRange"].Value<int>())
        Assert.Equal("pvc", doc.["storageMode"].Value<string>())


[<Fact>]
let ``a single real measurement is enough to keep a range and it keeps its count`` () =
    // The fix must move count after the guard without dropping it -- count is
    // what ledgersPerRange is inferred from.
    let r = JObject()
    r.["attempts"] <- JValue(2)
    r.["count"] <- JValue(420)
    r.["wallSeconds"] <- JValue(77.0)

    let ranges = buildRangeProfile (completedMap [ "420", r ])

    Assert.Equal(1, ranges.Count)
    Assert.Equal(77.0, ranges.["420"].["wallSeconds"].Value<float>())
    Assert.Equal(420, ranges.["420"].["count"].Value<int>())
