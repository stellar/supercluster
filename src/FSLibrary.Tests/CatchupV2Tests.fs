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
    // An empty flag must leave the chart's pin alone. monitor.image= resolves to
    // ":latest" or fails the pull.
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
    // A pooled run claims index 0; the caller's entries must start at 1. Both at
    // 0 and the second --set wins, so the pod matches on capacity alone and lands
    // on any tier -- a supergiant range on a dwarf node, an OOM per range.
    let src =
        System.IO.File.ReadAllText(
            "../../../../FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs")

    Assert.Contains("worker.requireNodeLabels[0]=purpose:%s", src)
    Assert.Contains("if context.pubnetParallelCatchupPoolPrefix <> \"\" then i + 1 else i", src)


[<Fact>]
let ``the pool maps ride their own --set with their commas escaped`` () =
    // Every other option is folded into ONE comma-joined --set; these maps are
    // themselves comma-separated, so folding them in delivers a map of one tier.
    let src =
        System.IO.File.ReadAllText(
            "../../../../FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs")

    Assert.Contains("v.Replace(\",\", \"\\\\,\")", src)
    Assert.Contains("poolMapArgs", src)
    // Empty must not reach helm: monitor.poolCpu= blanks the chart default.
    Assert.Contains("List.filter (fun (_, v) -> not (String.IsNullOrWhiteSpace v))", src)

// The profile artifact. A completed record always carries bookkeeping (attempts,
// count) and may carry no measurement at all, when the collector never sampled
// that range. Such a record must not become an entry.
//
// RACE #8: count was attached BEFORE the `entry.Count > 0` guard, so every entry
// was non-empty and none were skipped. Seen twice in the field -- 0%
// peakAnonBytes in the artifact against 99% in progress.json.
//
// What that costs: profile_for resolves a range to the nearest measured end
// ABOVE it, so one junk entry at 1200 captures every range beneath it and hides
// the real 1600. Range 1100 then routes to protostar instead of supergiant, and
// nothing logs it.


/// Superset of rangeProfileFields: wallSeconds and txApply are recorded, never
/// projected.
let private measurementFields =
    [ "peakAnonBytes"; "peakWorkingSetBytes"; "peakEphemeralBytes"
      "txApply"; "seconds"; "wallSeconds" ]

/// A record as /logs/progress.json carries it.
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

/// The same record with every measurement stripped.
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
let ``a measurement-free record cannot become a profile entry`` () =
    // Nothing measured -- the whole document is refused, rather than written
    // with the right range count and no data.
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
    // The over-correction guard: a good read must still write everything.
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
        // Slicing is inferred from the records, not from the caller's 20000.
        Assert.Equal(420, ranges.["420"].["count"].Value<int>())
        Assert.Equal(420, doc.["ledgersPerRange"].Value<int>())
        Assert.Equal("pvc", doc.["storageMode"].Value<string>())


[<Fact>]
let ``a measured range does not drag its unmeasured neighbours in`` () =
    // The realistic shape, and the only one that catches a guard deciding per RUN
    // rather than per RECORD: one that latches on the first measurement passes
    // both tests above while every later junk record rides in behind it.
    let completed =
        completedMap
            [ "1200", unmeasured (measuredRecord 400 900L)
              "1600", measuredRecord 400 950L
              "2000", unmeasured (measuredRecord 400 990L) ]

    match rangeProfileDocument "pvc" 20000 completed with
    | None -> failwith "refused a profile that carries one real measurement"
    | Some doc ->
        let ranges = doc.["ranges"] :?> JObject
        Assert.Equal(1, ranges.Count)
        Assert.NotNull(ranges.["1600"])
        Assert.Null(ranges.["1200"])
        Assert.Null(ranges.["2000"])
