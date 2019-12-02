// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module MissionHistoryTestnetPerformance

open Logging
open System
open StellarCoreSet
open StellarMissionContext
open StellarNetworkCfg
open StellarNetworkData
open StellarFormation
open StellarJobExec

let historyTestnetPerformance (context : MissionContext) =
    let opts = { TestnetCoreSetOptions with
                     localHistory = false
                     initialization = { TestnetCoreSetOptions.initialization with
                                            newHist = false
                                            forceScp = false } }

    // Testnet is reset every quarter (~90 days) so this test is not _perfectly_
    // stable. It attempts to replay a 10k ledger prefix (a half day of traffic)
    // from testnet, ending at ledger 100k (around day 5). This will therefore
    // change its runtime a bit from days 1-5 after a testnet reset.

    let currLedger:int64 = int64(GetLatestTestnetLedgerNumber ())
    let secondLedger:int64 = min currLedger 100000L
    let firstLedger:int64 = max 1L (secondLedger - 10000L)
    let delta:int64 = secondLedger - firstLedger
    assert(delta > 0L)
    context.ExecuteJobs (Some(opts)) (Some(SDFTestNet))
        begin
        fun (formation: StellarFormation) ->

            (formation.RunSingleJobWithTimeout context.destination
                 (Some(TimeSpan.FromMinutes(10.0)))
                 [| "catchup"; sprintf "%d/0" firstLedger |])
            |> formation.CheckAllJobsSucceeded

            (formation.RunSingleJobWithTimeout context.destination
                 (Some(TimeSpan.FromHours(4.0)))
                 [| "catchup"; sprintf "%d/%d" secondLedger delta |])
            |> formation.CheckAllJobsSucceeded

        end
