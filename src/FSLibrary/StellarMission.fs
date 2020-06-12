// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMission

open MissionSimplePayment
open MissionComplexTopology
open MissionLoadGeneration
open MissionLoadGenerationWithSpikes
open MissionBenchmarkBaseline
open MissionBenchmarkConsensusOnly
open MissionBenchmarkIncreaseTxRate
open MissionHistoryGenerateAndCatchup
open MissionHistoryPubnetMinimumCatchup
open MissionHistoryPubnetRecentCatchup
open MissionHistoryPubnetCompleteCatchup
open MissionHistoryPubnetParallelCatchup
open MissionHistoryPubnetPerformance
open MissionHistoryTestnetMinimumCatchup
open MissionHistoryTestnetRecentCatchup
open MissionHistoryTestnetCompleteCatchup
open MissionHistoryTestnetParallelCatchup
open MissionHistoryTestnetPerformance
open MissionVersionMixConsensus
open MissionVersionMixNewCatchupToOld
open MissionVersionMixOldCatchupToNew
open MissionProtocolUpgradeTestnet
open MissionProtocolUpgradePubnet
open MissionDatabaseInplaceUpgrade
open MissionAcceptanceUnitTests
open MissionSimulatePubnet
open StellarMissionContext

type Mission = (MissionContext -> unit)


let allMissions : Map<string, Mission> =
    Map.ofSeq [|
        ("SimplePayment", simplePayment)
        ("ComplexTopology", complexTopology)
        ("LoadGeneration", loadGeneration)
        ("LoadGenerationWithSpikes", loadGenerationWithSpikes)
        ("BenchmarkBaseline", benchmarkBaseline)
        ("BenchmarkConsensusOnly", benchmarkConsensusOnly)
        ("BenchmarkIncreaseTxRate", benchmarkIncreaseTxRate)
        ("HistoryGenerateAndCatchup", historyGenerateAndCatchup)
        ("HistoryPubnetMinimumCatchup", historyPubnetMinimumCatchup)
        ("HistoryPubnetRecentCatchup", historyPubnetRecentCatchup)
        ("HistoryPubnetCompleteCatchup", historyPubnetCompleteCatchup)
        ("HistoryPubnetParallelCatchup", historyPubnetParallelCatchup)
        ("HistoryPubnetPerformance", historyPubnetPerformance)
        ("HistoryTestnetMinimumCatchup", historyTestnetMinimumCatchup)
        ("HistoryTestnetRecentCatchup", historyTestnetRecentCatchup)
        ("HistoryTestnetCompleteCatchup", historyTestnetCompleteCatchup)
        ("HistoryTestnetParallelCatchup", historyTestnetParallelCatchup)
        ("HistoryTestnetPerformance", historyTestnetPerformance)
        ("VersionMixConsensus", versionMixConsensus)
        ("VersionMixNewCatchupToOld", versionMixNewCatchupToOld)
        ("VersionMixOldCatchupToNew", versionMixOldCatchupToNew)
        ("ProtocolUpgradeTestnet", protocolUpgradeTestnet)
        ("ProtocolUpgradePubnet", protocolUpgradePubnet)
        ("DatabaseInplaceUpgrade", databaseInplaceUpgrade)
        ("AcceptanceUnitTests", acceptanceUnitTests)
        ("SimulatePubnet", simulatePubnet)
    |]
