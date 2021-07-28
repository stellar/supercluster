// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarPerformanceReporter

open StellarCoreHTTP
open StellarCorePeer
open StellarCoreSet
open StellarDestination
open StellarNetworkCfg
open System

type Timer =
    { mean: decimal
      min: decimal
      max: decimal
      stdDev: decimal
      median: decimal
      per75th: decimal
      per95th: decimal
      per99th: decimal }

    static member FromGenericTimer(t: Metrics.GenericTimer) =
        { mean = decimal (t.Mean)
          min = decimal (t.Min)
          max = decimal (t.Max)
          stdDev = decimal (t.Stddev)
          median = decimal (t.Median)
          per75th = decimal (t.``75``)
          per95th = decimal (t.``95``)
          per99th = decimal (t.``99``) }

type PerformanceRow =
    { time: DateTime
      txtype: string
      accounts: int
      expectedTxs: int
      appliedTxs: int
      txRate: int
      batchSize: int
      txsPerLedgerMean: decimal
      txsPerLedgerStdDev: decimal
      loadStepRate: Option<double>
      loadStepStdDev: Option<double>
      nominate: Timer
      prepare: Timer
      close: Timer
      age: Timer
      meanRate: decimal }

    member self.ToCsvRow() =
        let valueOrNan x =
            match x with
            | None -> Double.NaN
            | Some v -> float (v)

        PerformanceCsv.Row(
            self.time.ToString(),
            self.txtype,
            self.accounts,
            self.expectedTxs,
            self.appliedTxs,
            self.txRate,
            self.batchSize,
            self.txsPerLedgerMean,
            self.txsPerLedgerStdDev,
            valueOrNan self.loadStepRate,
            valueOrNan self.loadStepStdDev,
            self.nominate.mean,
            self.nominate.min,
            self.nominate.max,
            self.nominate.stdDev,
            self.nominate.median,
            self.nominate.per75th,
            self.nominate.per95th,
            self.nominate.per99th,
            self.prepare.mean,
            self.prepare.min,
            self.prepare.max,
            self.prepare.stdDev,
            self.prepare.median,
            self.prepare.per75th,
            self.prepare.per95th,
            self.prepare.per99th,
            self.close.mean,
            self.close.min,
            self.close.max,
            self.close.stdDev,
            self.close.median,
            self.close.per75th,
            self.close.per95th,
            self.close.per99th,
            self.age.mean,
            self.age.min,
            self.age.max,
            self.age.stdDev,
            self.age.median,
            self.age.per75th,
            self.age.per95th,
            self.age.per99th,
            self.meanRate
        )

type PerformanceReporter(networkCfg: NetworkCfg) =
    let networkCfg = networkCfg
    let mutable data : Map<PodName, List<PerformanceRow>> = Map.empty

    member self.GetPerformanceMetrics (p: Peer) (loadGen: LoadGen) =
        let metrics = p.GetMetrics()

        { time = DateTime.Now
          txtype = loadGen.mode.ToString()
          accounts = loadGen.accounts
          expectedTxs = loadGen.txs
          appliedTxs = metrics.LedgerTransactionApply.Count
          txRate = loadGen.txrate
          batchSize = loadGen.batchsize
          txsPerLedgerMean = decimal (metrics.LedgerTransactionCount.Mean)
          txsPerLedgerStdDev = decimal (metrics.LedgerTransactionCount.Stddev)
          loadStepRate = Option.map (fun (x: Metrics.GenericTimer) -> x.MeanRate) metrics.LoadgenStepSubmit
          loadStepStdDev = Option.map (fun (x: Metrics.GenericTimer) -> x.Stddev) metrics.LoadgenStepSubmit
          nominate = Timer.FromGenericTimer(metrics.ScpTimingNominated)
          prepare = Timer.FromGenericTimer(metrics.ScpTimingExternalized)
          close = Timer.FromGenericTimer(metrics.LedgerLedgerClose)
          age = Timer.FromGenericTimer(metrics.LedgerAgeClosed)
          meanRate = decimal (metrics.LedgerLedgerClose.MeanRate) }

    member self.RecordPerformanceMetrics (loadGen: LoadGen) f =
        networkCfg.EachPeer(fun p -> p.ClearMetrics())
        f ()

        networkCfg.EachPeer
            (fun p ->
                let metrics = self.GetPerformanceMetrics p loadGen

                if not (data.ContainsKey(p.PodName)) then
                    data <- data.Add(p.PodName, [])
                else
                    true |> ignore

                let newPeerData = List.append data.[p.PodName] [ metrics ]
                data <- Map.add p.PodName newPeerData data)

    member self.DumpPerformanceMetrics() =
        let destination = networkCfg.missionContext.destination
        let ns = networkCfg.NamespaceProperty

        let dumpPeerPerformanceMetrics (p: Peer) =
            let toCsvRow (x: PerformanceRow) = x.ToCsvRow()
            let name = p.PodName

            if data.ContainsKey name then
                let csv = new PerformanceCsv(data.[name] |> (Seq.map toCsvRow) |> Seq.toList)
                destination.WriteString(sprintf "%s.perf" name.StringName) (csv.SaveToString('\t'))

        networkCfg.EachPeer(dumpPeerPerformanceMetrics)
