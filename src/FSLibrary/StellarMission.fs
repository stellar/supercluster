// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMission

open MissionSimplePayment
open MissionComplexTopology
open MissionLoadGeneration
open MissionBenchmarkBaseline
open MissionBenchmarkIncreaseTxRate
open MissionHistoryGenerateAndCatchup
open MissionHistoryPubnetMinimumCatchup
open MissionHistoryPubnetRecentCatchup
open MissionHistoryPubnetCompleteCatchup
open MissionHistoryTestnetMinimumCatchup
open MissionHistoryTestnetRecentCatchup
open MissionHistoryTestnetCompleteCatchup
open MissionVersionMixConsensus
open MissionVersionMixNewCatchupToOld
open MissionVersionMixOldCatchupToNew
open MissionProtocolUpgradeTestnet
open MissionProtocolUpgradePubnet
open MissionDatabaseInplaceUpgrade
open StellarMissionContext

type Mission = (MissionContext -> unit)


let allMissions : Map<string, Mission> =
    Map.ofSeq [|
        ("SimplePayment", simplePayment)
        ("ComplexTopology", complexTopology)
        ("LoadGeneration", loadGeneration)
        ("BenchmarkBaseline", benchmarkBaseline)
        ("BenchmarkIncreaseTxRate", benchmarkIncreaseTxRate)
        ("HistoryGenerateAndCatchup", historyGenerateAndCatchup)
        ("HistoryPubnetMinimumCatchup", historyPubnetMinimumCatchup)
        ("HistoryPubnetRecentCatchup", historyPubnetRecentCatchup)
        ("HistoryPubnetCompleteCatchup", historyPubnetCompleteCatchup)
        ("HistoryTestnetMinimumCatchup", historyTestnetMinimumCatchup)
        ("HistoryTestnetRecentCatchup", historyTestnetRecentCatchup)
        ("HistoryTestnetCompleteCatchup", historyTestnetCompleteCatchup)
        ("VersionMixConsensus", versionMixConsensus)
        ("VersionMixNewCatchupToOld", versionMixNewCatchupToOld)
        ("VersionMixOldCatchupToNew", versionMixOldCatchupToNew)
        ("ProtocolUpgradeTestnet", protocolUpgradeTestnet)
        ("ProtocolUpgradePubnet", protocolUpgradePubnet)
        ("DatabaseInplaceUpgrade", databaseInplaceUpgrade)
    |]
