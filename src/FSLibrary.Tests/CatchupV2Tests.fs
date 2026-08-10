// Copyright 2024 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

// Everything covering MissionHistoryPubnetParallelCatchupV2: the profile
// artifact it writes, and the helm invocation it builds.
//
// The profile tests exist for one defect. A completed record can carry
// bookkeeping (attempts, count) and no measurement at all -- the collector never
// wrote peaks for that range, or the read that would have supplied them was
// degraded. The `entry.Count > 0` guard skips such records, but count used to be
// attached BEFORE the guard ran, so every entry had at least one field and every
// entry passed. The result is an artifact with the right number of ranges and
// zero measurements, which is worse than no artifact: nothing downstream can
// tell it from a good one, and the next run sizes everything from defaults while
// looking correctly configured. Observed twice in the field, reporting 0%
// peakAnonBytes while the monitor's own progress.json carried 99%.
//
// These assert on the values the production projection returns, never on the
// text of the source file -- except where the subject IS the helm invocation,
// which has no return value to inspect.
module CatchupV2Tests

open Xunit
open Newtonsoft.Json.Linq
open MissionHistoryPubnetParallelCatchupV2


[<Fact>]
let ``the job monitor image is overridable and defaults to the chart`` () =
    // The monitor and collector ship as one image pinned in values.yaml. Passing
    // it per run is what lets a build of them be tested without editing the
    // chart -- but an empty flag must leave the chart's pin alone rather than
    // setting monitor.image to nothing, which resolves to ":latest" or fails the
    // pull outright.
    let src =
        System.IO.File.ReadAllText(
            "../../../../FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs")

    Assert.Contains("if context.jobMonitorImagePcV2 <> \"\" then", src)
    Assert.Contains("monitor.image=%s", src)

    let guard = src.IndexOf("if context.jobMonitorImagePcV2 <> \"\" then")
    let use_ = src.IndexOf("monitor.image=%s")
    Assert.True(guard < use_, "monitor.image must only be set inside the non-empty guard")


[<Fact>]
let ``a pooled run does not let caller labels overwrite the routing label`` () =
    // A pooled run claims worker.requireNodeLabels[0] for the label it routes
    // on, and requireNodeLabelsPcV2 used to index its own entries from 0 as
    // well. Both fire on a pooled run carrying a capacity label, and the second
    // --set wins: the routing label is replaced, so the pod matches on capacity
    // alone and lands on any tier at all -- a range sized for supergiant on a
    // dwarf node, which is an OOM per range rather than a slow run.
    let src =
        System.IO.File.ReadAllText(
            "../../../../FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs")

    Assert.Contains("worker.requireNodeLabels[0]=purpose:%s", src)
    Assert.Contains("if context.pubnetParallelCatchupPoolPrefix <> \"\" then i + 1 else i", src)


[<Fact>]
let ``the pool maps ride their own --set with their commas escaped`` () =
    // Every other option is folded into ONE comma-joined --set. The pool maps
    // are themselves comma-separated, so folding them in would split each tier
    // into a separate helm assignment and the map would arrive holding one
    // tier. This is why the overlay used to be a second --values file.
    let src =
        System.IO.File.ReadAllText(
            "../../../../FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs")

    Assert.Contains("v.Replace(\",\", \"\\\\,\")", src)
    Assert.Contains("poolMapArgs", src)
    // Empty must not reach helm at all: monitor.poolCpu= would blank the chart
    // default and every tier would fall back to the flat request.
    Assert.Contains("List.filter (fun (_, v) -> not (String.IsNullOrWhiteSpace v))", src)

// RACE #8 -- a measurement-free profile artifact that is indistinguishable from
// a good one.
//
// A completed record can carry bookkeeping (attempts, count) and no measurement
// at all: the collector never wrote peaks for that range, or the read that would
// have supplied them was degraded. Such a record must not become a profile entry.
//
// The `entry.Count > 0` guard exists to skip measurement-free entries, but count
// used to be attached to the entry BEFORE the guard ran, so every entry had at
// least one field and every entry passed. The result is an artifact with the
// right number of ranges and zero measurements -- observed twice in the field,
// reporting 0% peakAnonBytes while the monitor's own progress.json carried 99%.
// The next run then sizes from a profile that silently has no data.
//
// These tests assert on the values the production projection actually returns,
// never on the text of the source file.


/// Every measurement a completed record can carry. A superset of
/// rangeProfileFields: wallSeconds and txApply are recorded but never projected.
let private measurementFields =
    [ "peakAnonBytes"; "peakWorkingSetBytes"; "peakEphemeralBytes"
      "txApply"; "seconds"; "wallSeconds" ]

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
let private unmeasured (record: JObject) =
    let r = record.DeepClone() :?> JObject

    for f in measurementFields do
        r.Remove(f) |> ignore

    r

let private completedMap (pairs: (string * JObject) list) =
    let c = JObject()

    for (k, v) in pairs do
        c.[k] <- v

    c


[<Fact>]
let ``a run whose ranges measured nothing produces no profile artifact`` () =
    // The headline symptom: a full-looking artifact, right number of ranges,
    // zero measurements -- what a run whose collector never wrote peaks leaves
    // behind. Writing nothing is correct: the next run then falls back to its
    // configured defaults instead of sizing from empty data.
    let completed =
        completedMap
            [ "420", unmeasured (measuredRecord 420 900L)
              "840", unmeasured (measuredRecord 420 950L)
              "1260", unmeasured (measuredRecord 420 990L) ]

    match rangeProfileDocument "pvc" 20000 completed with
    | None -> ()
    | Some doc ->
        let ranges = doc.["ranges"] :?> JObject

        failwithf
            "wrote a profile artifact with %d ranges and zero measurements: %s"
            ranges.Count
            (ranges.ToString())


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
        Assert.Equal(120.0, ranges.["420"].["seconds"].Value<float>())
        // Measured but deliberately not projected: recorded as metrics, never
        // sized or ordered from, and pure weight in a 1 MiB-capped artifact.
        Assert.Null(ranges.["420"].["wallSeconds"])
        Assert.Null(ranges.["420"].["txApply"])
        // count is still carried, and the slicing is inferred from it rather
        // than from the caller's default.
        Assert.Equal(420, ranges.["420"].["count"].Value<int>())
        Assert.Equal(420, doc.["ledgersPerRange"].Value<int>())
        Assert.Equal("pvc", doc.["storageMode"].Value<string>())
