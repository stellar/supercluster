// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMission

open MissionBootAndSync
open MissionSimplePayment
open MissionComplexTopology
open MissionInMemoryMode
open MissionLoadGeneration
open MissionLoadGenerationWithSpikes
open MissionLoadGenerationWithTxSetLimit
open MissionMixedImageLoadGeneration
open MissionHistoryGenerateAndCatchup
open MissionHistoryPubnetMinimumCatchup
open MissionHistoryPubnetRecentCatchup
open MissionHistoryPubnetCompleteCatchup
open MissionHistoryPubnetParallelCatchup
open MissionHistoryPubnetParallelCatchupExtrawide
open MissionHistoryPubnetParallelCatchupV2
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
open MissionProtocolUpgradeWithLoad
open MissionDatabaseInplaceUpgrade
open MissionAcceptanceUnitTests
open MissionSimulatePubnet
open MissionSlowNodesNetwork
open MissionMaxTPSClassic
open StellarMissionContext
open MissionSorobanLoadGeneration
open MissionSorobanConfigUpgrades
open MissionSorobanInvokeHostLoad
open MissionSorobanCatchupWithPrevAndCurr
open MissionMixedImageNetworkSurvey
open MissionMaxTPSMixed
open MissionSimulatePubnetMixedLoad

type Mission = (MissionContext -> unit)


let allMissions : Map<string, Mission> =
    Map.ofSeq [| ("BootAndSync", bootAndSync)
                 ("SimplePayment", simplePayment)
                 ("ComplexTopology", complexTopology)
                 ("LoadGeneration", loadGeneration)
                 ("LoadGenerationWithSpikes", loadGenerationWithSpikes)
                 ("LoadGenerationWithTxSetLimit", loadGenerationWithTxSetLimit)
                 ("MixedImageLoadGenerationWithOldImageMajority", mixedImageLoadGenerationWithOldImageMajority)
                 ("MixedImageLoadGenerationWithNewImageMajority", mixedImageLoadGenerationWithNewImageMajority)
                 ("InMemoryMode", runInMemoryMode)
                 ("HistoryGenerateAndCatchup", historyGenerateAndCatchup)
                 ("HistoryPubnetMinimumCatchup", historyPubnetMinimumCatchup)
                 ("HistoryPubnetRecentCatchup", historyPubnetRecentCatchup)
                 ("HistoryPubnetCompleteCatchup", historyPubnetCompleteCatchup)
                 ("HistoryPubnetParallelCatchup", historyPubnetParallelCatchup)
                 ("HistoryPubnetParallelCatchupExtrawide", historyPubnetParallelCatchupExtrawide)
                 ("HistoryPubnetParallelCatchupV2", historyPubnetParallelCatchupV2)
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
                 ("ProtocolUpgradeWithLoad", protocolUpgradeWithLoad)
                 ("DatabaseInplaceUpgrade", databaseInplaceUpgrade)
                 ("AcceptanceUnitTests", acceptanceUnitTests)
                 ("SimulatePubnet", simulatePubnet)
                 ("SlowNodesNetwork", slowNodesNetwork)
                 ("MaxTPSClassic", maxTPSClassic)
                 ("SorobanLoadGeneration", sorobanLoadGeneration)
                 ("SorobanConfigUpgrades", sorobanConfigUpgrades)
                 ("SorobanInvokeHostLoad", sorobanInvokeHostLoad)
                 ("SorobanCatchupWithPrevAndCurr", sorobanCatchupWithPrevAndCurr)
                 ("MixedImageNetworkSurvey", mixedImageNetworkSurvey)
                 ("MaxTPSMixed", maxTPSMixed)
                 ("SimulatePubnetMixedLoad", simulatePubnetMixedLoad) |]
