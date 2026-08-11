module Tests

open StellarDestination
open StellarDotnetSdk.Accounts
open StellarMissionContext
open Xunit
open Newtonsoft.Json.Linq
open MissionHistoryPubnetParallelCatchupV2
open System.Text.RegularExpressions

open StellarCoreSet
open StellarCoreCfg
open StellarShellCmd
open StellarNetworkCfg
open StellarKubeSpecs
open StellarNetworkData
open StellarNetworkDelays
open StellarCoreHTTP
open MissionCatchupHelpers
open Xunit.Abstractions


[<Fact>]
let ``Network nonce looks reasonable`` () =
    let nonce = MakeNetworkNonce None
    let nstr = nonce.ToString()
    Assert.Matches(Regex("^ssc-[a-z0-9-]+$"), nstr)

let coreSetOptions =
    { CoreSetOptions.GetDefault "stellar/stellar-core" with
          syncStartupDelay = None
          homeDomain = None }

let coreSet = MakeLiveCoreSet "test" coreSetOptions
let passOpt : NetworkPassphrase option = None

let ctx : MissionContext =
    { kube = null
      kubeCfg = ""
      destination = Destination(System.IO.Path.GetTempPath())
      missionName = "Tests"
      image = "stellar/stellar-core"
      oldImage = None
      netdelayImage = ""
      nginxImage = ""
      postgresImage = ""
      prometheusExporterImage = ""
      txRate = 10
      maxTxRate = 10
      numAccounts = 1000
      numTxs = 1000
      spikeSize = 1000
      spikeInterval = 10
      numWasms = None
      numInstances = None
      maxFeeRate = Some(1000)
      skipLowFeeTxs = false
      numNodes = 100
      namespaceProperty = "stellar-supercluster"
      logLevels = { LogDebugPartitions = []; LogTracePartitions = [] }
      gatewayName = "traefik-gateway-private"
      gatewayNamespace = "traefik"
      routeInternalDomain = "local"
      routeExternalHost = None
      routeExternalPort = 80
      exportToPrometheus = false
      probeTimeout = 10
      coreResources = SmallTestResources
      keepData = true
      unevenSched = false
      dedicatedNodes = false
      requireNodeLabels = []
      avoidNodeLabels = []
      tolerateNodeTaints = []
      apiRateLimit = 10
      httpProxyReplicas = 2
      pubnetData = None
      flatQuorum = None
      tier1Keys = None
      maxConnections = None
      fullyConnectTier1 = false
      peerReadingCapacity = None
      peerFloodCapacity = None
      enableBackgroundSigValidation = false
      enableParallelApply = false
      enableInMemoryBuckets = false
      peerFloodCapacityBytes = None
      outboundByteLimit = None
      sleepMainThread = None
      flowControlSendMoreBatchSize = None
      flowControlSendMoreBatchSizeBytes = None
      installNetworkDelay = Some true
      flatNetworkDelay = None
      simulateApplyDuration =
          Some(
              seq {
                  10
                  100
              }
          )
      simulateApplyWeight =
          Some(
              seq {
                  30
                  70
              }
          )
      byteCountDistribution = []
      wasmBytesDistribution = []
      dataEntriesDistribution = []
      totalKiloBytesDistribution = []
      txSizeBytesDistribution = []
      instructionsDistribution = []
      payWeight = None
      sorobanUploadWeight = None
      sorobanInvokeWeight = None
      minSorobanPercentSuccess = None
      networkSizeLimit = 100
      tier1OrgsToAdd = 0
      nonTier1NodesToAdd = 0
      randomSeed = 0
      pubnetParallelCatchupStartingLedger = 0
      pubnetParallelCatchupEndLedger = None
      pubnetParallelCatchupLedgersPerJob = 16000
      pubnetParallelCatchupNumWorkers = 192
      pubnetParallelCatchupStorageMode = "pvc"
      pubnetParallelCatchupProfile = ""
      pubnetParallelCatchupRangeOrder = "tip-first"
      pubnetParallelCatchupPoolPrefix = ""
      jobMonitorImagePcV2 = ""
      pubnetParallelCatchupCpuRequest = ""
      pubnetParallelCatchupMemRequest = ""
      pubnetParallelCatchupPoolCpu = ""
      pubnetParallelCatchupPoolMem = ""
      pubnetParallelCatchupCreateRbac = false
      jobMonitorNodeLabels = []
      jobMonitorTolerateTaints = []
      tag = None
      numPregeneratedTxs = None
      enableTailLogging = true
      catchupSkipKnownResultsForTesting = None
      checkEventsAreConsistentWithEntryDiffs = None
      updateSorobanCosts = None
      genesisTestAccountCount = None
      asanOptions = None
      enableRelaxedAutoQsetConfig = false
      jobMonitorExternalHost = None
      txBatchMaxSize = None
      runForMaxTps = None
      requireNodeLabelsPcV2 = []
      avoidNodeLabelsPcV2 = []
      tolerateNodeTaintsPcV2 = []
      serviceAccountAnnotationsPcV2 = []
      s3HistoryMirrorOverridePcV2 = None
      s3HistoryMirrorRegionPcV2 = "us-east-1"
      benchmarkInfrastructure = None
      benchmarkInfrastructureOnly = None
      benchmarkDurationSeconds = None
      enableTcpTuning = false
      minBlockTimeMs = 4000
      maxBlockTimeMs = 5000
      minBlockTimeMixedMode = "mixed_pregen_sac_payment"
      minBlockTimeMixedClassicTxRate = None
      minBlockTimeMixedSorobanTxRate = None
      runForMinBlockTime = false
      forceOldStyleTriggerTimerPct = 0
      uniformDrift = []
      bimodalDrift = []
      driftPct = 0
      ledgerCloseTimeMs = None
      forceOldStyleTriggerTimer = None }

let netdata = __SOURCE_DIRECTORY__ + "/../../../data/public-network-data-2024-08-01.json"
let pubkeys = __SOURCE_DIRECTORY__ + "/../../../data/tier1keys.json"
let pubnetctx = { ctx with pubnetData = Some netdata; tier1Keys = Some pubkeys }

let nCfg = MakeNetworkCfg ctx [ coreSet ] passOpt

type Tests(output: ITestOutputHelper) =

    [<Fact>]
    member __.``TOML Config looks reasonable``() =
        let cfg = nCfg.StellarCoreCfg(coreSet, 1, MainCoreContainer)
        let toml = cfg.ToString()
        let peer0DNS = (nCfg.PeerDnsName coreSet 0).StringName
        let peer1DNS = (nCfg.PeerDnsName coreSet 1).StringName
        let peer2DNS = (nCfg.PeerDnsName coreSet 2).StringName
        let nonceStr = nCfg.networkNonce.ToString()
        let domain = nonceStr + "-stellar-core." + ctx.namespaceProperty + ".svc.cluster.local"
        Assert.Equal(nonceStr + "-sts-test-0." + domain, peer0DNS)
        Assert.Equal(nonceStr + "-sts-test-1." + domain, peer1DNS)
        Assert.Equal(nonceStr + "-sts-test-2." + domain, peer2DNS)
        Assert.Contains("DATABASE = \"sqlite3:///data/stellar.db\"", toml)
        Assert.Contains("BUCKET_DIR_PATH = \"/data/buckets\"", toml)

        Assert.Contains(
            "PREFERRED_PEERS = [\""
            + peer0DNS
            + "\", \""
            + peer1DNS
            + "\", \""
            + peer2DNS
            + "\"]",
            toml
        )

        Assert.Contains("[HISTORY.test-0]", toml)
        Assert.Contains("\"curl -sf http://" + peer0DNS + "/{0} -o {1}\"", toml)
        Assert.Contains("OP_APPLY_SLEEP_TIME_DURATION_FOR_TESTING = [10, 100]", toml)
        Assert.Contains("OP_APPLY_SLEEP_TIME_WEIGHT_FOR_TESTING = [30, 70]", toml)
        Assert.Contains("HTTP_PORT = " + CfgVal.httpPort.ToString(), toml)
        // Trigger timer and clock offset settings must be omitted unless
        // explicitly configured on the CoreSet or the mission context.
        Assert.DoesNotContain("FORCE_OLD_STYLE_PREPARE_START_TRIGGER_TIMER", toml)
        Assert.DoesNotContain("ARTIFICIALLY_SET_SYSTEM_CLOCK_OFFSET_FOR_TESTING", toml)

    [<Fact>]
    member __.``Quorum intersection checker config defaults to disabled``() =
        let cfg = nCfg.StellarCoreCfg(coreSet, 1, MainCoreContainer)
        let toml = cfg.ToString()
        Assert.Contains("QUORUM_INTERSECTION_CHECKER = false", toml)
        Assert.DoesNotContain("USE_QUORUM_INTERSECTION_CHECKER_V2", toml)
        Assert.DoesNotContain("QUORUM_INTERSECTION_CHECKER_TIME_LIMIT_MS", toml)
        Assert.DoesNotContain("QUORUM_INTERSECTION_CHECKER_MEMORY_LIMIT_BYTES", toml)

    [<Fact>]
    member __.``Quorum intersection checker config can be enabled with V2 and limits``() =
        let opts =
            { coreSetOptions with
                  quorumIntersectionChecker = true
                  useQuorumIntersectionCheckerV2 = true
                  quorumIntersectionCheckerTimeLimitMs = Some 10000L
                  quorumIntersectionCheckerMemoryLimitBytes = Some 209715200L }

        let cs = MakeLiveCoreSet "qic" opts
        let cfg = (MakeNetworkCfg ctx [ cs ] passOpt).StellarCoreCfg(cs, 0, MainCoreContainer)
        let toml = cfg.ToString()
        Assert.Contains("QUORUM_INTERSECTION_CHECKER = true", toml)
        Assert.Contains("USE_QUORUM_INTERSECTION_CHECKER_V2 = true", toml)
        Assert.Contains("QUORUM_INTERSECTION_CHECKER_TIME_LIMIT_MS = 10000", toml)
        Assert.Contains("QUORUM_INTERSECTION_CHECKER_MEMORY_LIMIT_BYTES = 209715200", toml)

    [<Fact>]
    member __.``MakeLiveCoreSetWithKeys preserves supplied keys``() =
        let keys = Array.init 3 (fun _ -> KeyPair.Random())
        let cs = MakeLiveCoreSetWithKeys "withkeys" keys coreSetOptions
        Assert.Equal<KeyPair array>(keys, cs.keys)
        Assert.True(cs.live)
        Assert.Equal(CoreSetName "withkeys", cs.name)

    [<Fact>]
    member __.``MakeLiveCoreSetWithKeys rejects key count mismatch``() =
        let keys = Array.init 2 (fun _ -> KeyPair.Random())

        Assert.Throws<System.Exception>(fun () -> MakeLiveCoreSetWithKeys "withkeys" keys coreSetOptions |> ignore)
        |> ignore

    [<Fact>]
    member __.``WithCoreSetOptions swaps options preserving keys and liveness``() =
        let nCfg2 = MakeNetworkCfg ctx [ coreSet ] passOpt
        let newOpts = { coreSetOptions with quorumIntersectionChecker = true }
        let nCfg3 = nCfg2.WithCoreSetOptions(CoreSetName "test") newOpts
        let before = nCfg2.FindCoreSet(CoreSetName "test")
        let after = nCfg3.FindCoreSet(CoreSetName "test")
        Assert.Equal<KeyPair array>(before.keys, after.keys)
        Assert.Equal(before.live, after.live)
        Assert.True(after.options.quorumIntersectionChecker)
        let toml = nCfg3.StellarCoreCfg(after, 0, MainCoreContainer).ToString()
        Assert.Contains("QUORUM_INTERSECTION_CHECKER = true", toml)

    [<Fact>]
    member __.``WithCoreSetOptions rejects nodeCount changes``() =
        let nCfg2 = MakeNetworkCfg ctx [ coreSet ] passOpt
        let newOpts = { coreSetOptions with nodeCount = coreSetOptions.nodeCount + 1 }

        Assert.Throws<System.Exception>(fun () -> nCfg2.WithCoreSetOptions(CoreSetName "test") newOpts |> ignore)
        |> ignore

    [<Fact>]
    member __.``PeerConfigMap matches ToConfigMaps output``() =
        let ctx = { ctx with installNetworkDelay = None }
        let nCfg2 = MakeNetworkCfg ctx [ coreSet ] passOpt

        let fromAll =
            nCfg2.ToConfigMaps()
            |> Array.find (fun cm -> cm.Metadata.Name = nCfg2.PeerCfgMapName coreSet 0)

        let single = nCfg2.PeerConfigMap(coreSet, 0)
        Assert.Equal(fromAll.Metadata.Name, single.Metadata.Name)

        Assert.Equal<string seq>(
            Seq.sort (
                Seq.map (fun (kv: System.Collections.Generic.KeyValuePair<string, string>) -> kv.Key) fromAll.Data
            ),
            Seq.sort (Seq.map (fun (kv: System.Collections.Generic.KeyValuePair<string, string>) -> kv.Key) single.Data)
        )

    [<Fact>]
    member __.``TOML Config emits trigger timer and per-node clock offsets``() =
        let opts =
            { coreSetOptions with
                  forceOldStyleTriggerTimer = Some true
                  clockOffsets = Some [ 0; -800; 1500 ] }

        let cs = MakeLiveCoreSet "test" opts
        let cfg = MakeNetworkCfg ctx [ cs ] passOpt

        let tomlOfNode i = cfg.StellarCoreCfg(cs, i, MainCoreContainer).ToString()

        for i in 0 .. 2 do
            Assert.Contains("FORCE_OLD_STYLE_PREPARE_START_TRIGGER_TIMER = true", tomlOfNode i)

        Assert.Contains("ARTIFICIALLY_SET_SYSTEM_CLOCK_OFFSET_FOR_TESTING = 0", tomlOfNode 0)
        Assert.Contains("ARTIFICIALLY_SET_SYSTEM_CLOCK_OFFSET_FOR_TESTING = -800", tomlOfNode 1)
        Assert.Contains("ARTIFICIALLY_SET_SYSTEM_CLOCK_OFFSET_FOR_TESTING = 1500", tomlOfNode 2)

    [<Fact>]
    member __.``TOML Config falls back to mission-level trigger timer setting``() =
        let tomlWith ctxOverride =
            let cfg = MakeNetworkCfg ctxOverride [ coreSet ] passOpt
            cfg.StellarCoreCfg(coreSet, 0, MainCoreContainer).ToString()

        // The CoreSet leaves the option unset, so the mission-level flag
        // decides whether (and with which value) the key is emitted.
        Assert.Contains(
            "FORCE_OLD_STYLE_PREPARE_START_TRIGGER_TIMER = true",
            tomlWith { ctx with forceOldStyleTriggerTimer = Some true }
        )

        Assert.Contains(
            "FORCE_OLD_STYLE_PREPARE_START_TRIGGER_TIMER = false",
            tomlWith { ctx with forceOldStyleTriggerTimer = Some false }
        )

        Assert.DoesNotContain("FORCE_OLD_STYLE_PREPARE_START_TRIGGER_TIMER", tomlWith ctx)

        // A CoreSet-level setting wins over the mission-level flag.
        let csOn =
            MakeLiveCoreSet "test" { coreSetOptions with forceOldStyleTriggerTimer = Some true }

        let cfgOn =
            MakeNetworkCfg { ctx with forceOldStyleTriggerTimer = Some false } [ csOn ] passOpt

        Assert.Contains(
            "FORCE_OLD_STYLE_PREPARE_START_TRIGGER_TIMER = true",
            cfgOn.StellarCoreCfg(csOn, 0, MainCoreContainer).ToString()
        )

    // Test init config
    // REVERTME: temporarily avoid looking for HTTP_PORT=0 on InitContainers
    // let initCfg = nCfg.StellarCoreCfg(coreSet, 1, InitCoreContainer)
    // Assert.Contains("HTTP_PORT = 0", initCfg.ToString())

    [<Fact>]
    member __.``Dedicated-nodes mission gets per-run pod anti-affinity``() =
        let nCfgDedicated =
            MakeNetworkCfg { ctx with dedicatedNodes = true; installNetworkDelay = Some false } [ coreSet ] passOpt

        let spec = (nCfgDedicated.ToPodTemplateSpec coreSet).Spec

        // Pods are tagged with their run nonce, which is what the anti-affinity
        // discriminates on.
        Assert.Equal(nCfgDedicated.Nonce, nCfgDedicated.PodLabels().[CfgVal.runNonceLabelKey])

        // A single required pod anti-affinity term repels other runs' pods.
        Assert.NotNull(spec.Affinity)
        Assert.NotNull(spec.Affinity.PodAntiAffinity)
        let terms = spec.Affinity.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution
        Assert.Equal(1, terms.Count)
        let term = terms.[0]
        Assert.Equal("kubernetes.io/hostname", term.TopologyKey)
        Assert.Equal("stellar-core", term.LabelSelector.MatchLabels.["app"])
        let expr = Seq.exactlyOne term.LabelSelector.MatchExpressions
        Assert.Equal(CfgVal.runNonceLabelKey, expr.Key)
        Assert.Equal("NotIn", expr.OperatorProperty)
        Assert.Equal(nCfgDedicated.Nonce, Seq.exactlyOne expr.Values)

    [<Fact>]
    member __.``Non-dedicated mission has no affinity``() =
        // The default ctx sets no node labels and dedicatedNodes = false, so
        // there is no affinity block at all -- but pods still carry the nonce.
        let nCfgPlain = MakeNetworkCfg { ctx with installNetworkDelay = Some false } [ coreSet ] passOpt
        let spec = (nCfgPlain.ToPodTemplateSpec coreSet).Spec
        Assert.Null(spec.Affinity)
        Assert.Equal(nCfgPlain.Nonce, nCfgPlain.PodLabels().[CfgVal.runNonceLabelKey])

    [<Fact>]
    member __.``HTTP proxy pod inherits mission node affinity and tolerations``() =
        // Regression: the HTTP proxy Deployment must carry the mission's node
        // placement (self.Affinity()/self.Tolerations()) like the core pods it
        // fronts -- otherwise it cannot schedule onto a tainted/dedicated pool
        // and stays Pending on a busy cluster.
        let nCfg =
            MakeNetworkCfg
                { ctx with
                      requireNodeLabels = [ ("purpose", Some "largetests") ]
                      tolerateNodeTaints = [ ("largetests", None) ]
                      installNetworkDelay = Some false }
                [ coreSet ]
                passOpt

        let spec = nCfg.ToHttpProxyDeployment().Spec.Template.Spec

        // Node affinity requires the mission node label.
        Assert.NotNull(spec.Affinity)
        Assert.NotNull(spec.Affinity.NodeAffinity)

        let term =
            Seq.exactlyOne spec.Affinity.NodeAffinity.RequiredDuringSchedulingIgnoredDuringExecution.NodeSelectorTerms

        let expr = Seq.exactlyOne term.MatchExpressions
        Assert.Equal("purpose", expr.Key)
        Assert.Equal("In", expr.OperatorProperty)
        Assert.Equal("largetests", Seq.exactlyOne expr.Values)

        // Tolerates the mission node taint.
        Assert.True(
            spec.Tolerations
            |> Seq.exists (fun t -> t.Key = "largetests" && t.OperatorProperty = "Exists")
        )

    [<Fact>]
    member __.``Core init commands look reasonable``() =
        let nCfgWithoutSimulateApply =
            MakeNetworkCfg { ctx with simulateApplyWeight = None; simulateApplyDuration = None } [ coreSet ] passOpt

        let cmds = nCfgWithoutSimulateApply.getInitCommands PeerSpecificConfigFile coreSet.options
        let cmdStr = ShAnd(cmds).ToString()

        let exp =
            "{ stellar-core new-db --conf \"/cfg-${STELLAR_CORE_PEER_SHORT_NAME}/stellar-core-init.cfg\" && "
            + "{ stellar-core new-hist local --conf \"/cfg-${STELLAR_CORE_PEER_SHORT_NAME}/stellar-core-init.cfg\" || true; }; }"

        Assert.Equal(exp, cmdStr)

        let cmds = nCfg.getInitCommands PeerSpecificConfigFile coreSet.options
        let cmdStr = ShAnd(cmds).ToString()
        Assert.Equal(exp, cmdStr)

    [<Fact>]
    member __.``Shell convenience methods work``() =
        let cmds =
            [| ShCmd.DefVarSub "pid" [| "pidof"; "postgresql" |]
               ShCmd.OfStrs [| "kill"
                               "-HUP"
                               "${pid}" |] |]

        let s = (ShCmd.ShSeq cmds).ToString()
        let exp = "{ pid=`pidof postgresql`; kill -HUP \"${pid}\"; }"
        Assert.Equal(s, exp)

    [<Fact>]
    member __.``PercentOfThreshold function is correct``() =
        let pct = percentOfThreshold 3 2
        Assert.Equal(34, pct)
        let pct = percentOfThreshold 4 2
        Assert.Equal(26, pct)
        let pct = percentOfThreshold 4 3
        Assert.Equal(51, pct)
        let thr = thresholdOfPercent 3 34
        Assert.Equal(2, thr)
        let thr = thresholdOfPercent 3 66
        Assert.Equal(2, thr)
        let thr = thresholdOfPercent 3 67
        Assert.Equal(3, thr)
        let thr = thresholdOfPercent 4 24
        Assert.Equal(1, thr)
        let thr = thresholdOfPercent 4 25
        Assert.Equal(1, thr)
        let thr = thresholdOfPercent 4 26
        Assert.Equal(2, thr)
        let thr = thresholdOfPercent 4 50
        Assert.Equal(2, thr)
        let thr = thresholdOfPercent 4 51
        Assert.Equal(3, thr)

    [<Fact>]
    member __.``Inverse threshold function is actually inverse``() =
        for sz = 1 to 20 do
            for thr = 1 to sz do
                let pct = percentOfThreshold sz thr
                Assert.Equal(thr, thresholdOfPercent sz pct)

    [<Fact>]
    member __.``Public network conversion looks reasonable``() =
        if System.IO.File.Exists(netdata) && System.IO.File.Exists(pubkeys) then
            (let coreSets = FullPubnetCoreSets pubnetctx false true
             let nCfg = MakeNetworkCfg pubnetctx coreSets passOpt
             let sdfCoreSetName = CoreSetName "stellar"
             Assert.Contains(coreSets, (fun cs -> cs.name = sdfCoreSetName))
             // Ensure that 'validator.stellar.expert' got a different name from
             // 'www.stellar.org'.
             Assert.Contains(coreSets, (fun cs -> cs.name = (CoreSetName "expert-non-tier1")))
             let sdfCoreSet = List.find (fun cs -> cs.name = sdfCoreSetName) coreSets
             Assert.Equal(3, sdfCoreSet.options.nodeCount)
             let cfg = nCfg.StellarCoreCfg(sdfCoreSet, 0, MainCoreContainer)
             let toml = cfg.ToString()
             Assert.Contains("[QUORUM_SET.sub1]", toml)
             Assert.Contains("[HISTORY.local]", toml)
             Assert.Matches(Regex("VALIDATORS.*blockdaemon-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*stellar-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*publicnode-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*creit-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*satoshipay-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*lobstr-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*franklintempleton-0"), toml))

    [<Fact>]
    member __.``Geographic calculations are reasonable``() =
        // We want to test ping time and distance calculations for
        // a variety of node locations both far apart and close together.

        // Ashburn to Beauharnois: empirically 792km, pingtime 29ms
        // Calculated approximation: 756km, 15ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Beauharnois, 750.0, 760.0)
        Assert.InRange(networkPingInMs Ashburn Beauharnois, 10.0, 20.0)

        // Ashburn to Chennai: empirically 13783km, pingtime 205ms
        // Calculated approximation: 13773km, 275ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Chennai, 13770.0, 13780.0)
        Assert.InRange(networkPingInMs Ashburn Chennai, 270.0, 280.0)

        // Ashburn to Columbus: empirically 494km, pingtime 12ms
        // Calculated approximation: 537km, 11ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Columbus, 530.0, 540.0)
        Assert.InRange(networkPingInMs Ashburn Columbus, 10.0, 15.0)

        // Ashburn to Falkenstein: empirically 6767km, pingtime 93ms
        // Calculated approximation: 6747km, 135ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Falkenstein, 6740.0, 6750.0)
        Assert.InRange(networkPingInMs Ashburn Falkenstein, 130.0, 140.0)

        // Ashburn to Frankfurt: empirically 6549km, pingtime 97ms
        // Calculated approximation: 6531km, 131ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Frankfurt, 6530.0, 6540.0)
        Assert.InRange(networkPingInMs Ashburn Frankfurt, 130.0, 140.0)

        // Ashburn to HongKong: empirically 13100km, pingtime 220ms
        // Calculated approximation: 13109km, 262ms
        Assert.InRange(greatCircleDistanceInKm Ashburn HongKong, 13100.0, 13120.0)
        Assert.InRange(networkPingInMs Ashburn HongKong, 250.0, 270.0)

        // Ashburn to Portland: empirically 3748km, pingtime 71ms
        // Calculated approximation: 3781km, 76ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Portland, 3780.0, 3790.0)
        Assert.InRange(networkPingInMs Ashburn Portland, 70.0, 80.0)

        // Ashburn to Pudong: empirically 11975km, pingtime 218ms
        // Calculated approximation: 12002km, 240ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Pudong, 12000.0, 12010.0)
        Assert.InRange(networkPingInMs Ashburn Pudong, 230.0, 250.0)

        // Ashburn to Purfleet: empirically 5918km, pingtime 77ms
        // Calculated approximation: 5922km, 118ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Purfleet, 5920.0, 5930.0)
        Assert.InRange(networkPingInMs Ashburn Purfleet, 110.0, 120.0)

        // Ashburn to SaoPaulo: empirically 7655km, pingtime 126ms
        // Calculated approximation: 7634km, 153ms
        Assert.InRange(greatCircleDistanceInKm Ashburn SaoPaulo, 7630.0, 7640.0)
        Assert.InRange(networkPingInMs Ashburn SaoPaulo, 150.0, 160.0)

        // Ashburn to Singapore: empirically 15532km, pingtime 264ms
        // Calculated approximation: 15540km, 311ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Singapore, 15530.0, 15550.0)
        Assert.InRange(networkPingInMs Ashburn Singapore, 310.0, 320.0)

        // Ashburn to Tokyo: empirically 10882km, pingtime 166ms
        // Calculated approximation: 10905km, 218ms
        Assert.InRange(greatCircleDistanceInKm Ashburn Tokyo, 10900.0, 10910.0)
        Assert.InRange(networkPingInMs Ashburn Tokyo, 210.0, 220.0)

        // Tokyo to Pudong: empirically 1764km, pingtime 63ms
        // Calculated approximation: 1766km, 35ms
        Assert.InRange(greatCircleDistanceInKm Tokyo Pudong, 1760.0, 1770.0)
        Assert.InRange(networkPingInMs Tokyo Pudong, 30.0, 40.0)

        // Tokyo to Hong Kong: empirically 2887km, pingtime 50ms
        // Calculated approximation: 2892km, 58ms
        Assert.InRange(greatCircleDistanceInKm Tokyo HongKong, 2890.0, 2900.0)
        Assert.InRange(networkPingInMs Tokyo HongKong, 50.0, 60.0)

        // Tokyo to Singapore: empirically 5325km, pingtime 80ms
        // Calculated approximation: 5320km, 106ms
        Assert.InRange(greatCircleDistanceInKm Tokyo Singapore, 5310.0, 5330.0)
        Assert.InRange(networkPingInMs Tokyo Singapore, 100.0, 110.0)

        // Falkenstein to Frankfurt: empirically 264km, pingtime 5ms
        // Calculated approximation: 262ms, 5ms
        Assert.InRange(greatCircleDistanceInKm Falkenstein Frankfurt, 260.0, 270.0)
        Assert.InRange(networkPingInMs Falkenstein Frankfurt, 4.0, 6.0)

        // Falkenstein to Purfleet: empirically 879km, pingtime 15.72ms
        // Calculated approximation: 854ms, 17ms
        Assert.InRange(greatCircleDistanceInKm Falkenstein Purfleet, 850.0, 860.0)
        Assert.InRange(networkPingInMs Falkenstein Purfleet, 15.0, 20.0)

    [<Fact>]
    member __.``Traffic control commands are reasonable``() =
        let Ashburn = { lat = 38.89511; lon = -77.03637 }
        let Beauharnois = { lat = 45.2986777; lon = -73.9288762 }
        let Chennai = { lat = 13.08784; lon = 80.27847 }
        let dns1 = PeerDnsName "www.foo.com"
        let dns2 = PeerDnsName "www.bar.com"
        let cmd = getNetworkDelayCommands Ashburn [| (Beauharnois, dns1); (Chennai, dns2) |] None
        let cmdStr = cmd.ToString()

        Assert.Contains(dns1.StringName, cmdStr)
        Assert.Contains(dns2.StringName, cmdStr)
        let delay1 = int (networkDelayInMs Ashburn Beauharnois)
        let delay2 = int (networkDelayInMs Ashburn Chennai)
        Assert.Contains(sprintf "netem delay %dms" delay1, cmdStr)
        Assert.Contains(sprintf "netem delay %dms" delay2, cmdStr)

    [<Fact>]
    member __.``Public network delay commands are reasonable``() =
        if System.IO.File.Exists(netdata) && System.IO.File.Exists(pubkeys) then
            (let allCoreSets = FullPubnetCoreSets pubnetctx true true
             let fullNetCfg = MakeNetworkCfg pubnetctx allCoreSets passOpt

             let sdf = List.find (fun (cs: CoreSet) -> cs.name.StringName = "stellar") allCoreSets

             let delayCmd = fullNetCfg.NetworkDelayScript sdf 0
             let str = delayCmd.ToString()
             Assert.Matches(Regex("host -t A ssc-.*cluster.local"), str))

    [<Fact>]
    member __.``Parallel catchup ranges are reasonable``() =

        // startingLedger = 0
        let jobArr1 = getCatchupRanges 5 0 19 1
        Assert.Equal(4, jobArr1.Length)
        Assert.Equal("4/6", jobArr1.[0].[1])
        Assert.Equal("9/6", jobArr1.[1].[1])
        Assert.Equal("14/6", jobArr1.[2].[1])
        Assert.Equal("19/6", jobArr1.[3].[1])

        // next range would end at startingLedger(50), but it's
        // already contained in the previously calculated range (56/8)
        let jobArr2 = getCatchupRanges 6 50 62 2
        Assert.Equal(2, jobArr2.Length)
        Assert.Equal("56/8", jobArr2.[0].[1])
        Assert.Equal("62/8", jobArr2.[1].[1])


        let jobArr3 = getCatchupRanges 5 50 61 1
        Assert.Equal(3, jobArr3.Length)
        Assert.Equal("51/6", jobArr3.[0].[1])
        Assert.Equal("56/6", jobArr3.[1].[1])
        Assert.Equal("61/6", jobArr3.[2].[1])

// ---------------------------------------------------------------------------
// MissionHistoryPubnetParallelCatchupV2
// ---------------------------------------------------------------------------
[<Fact>]
let ``the job monitor image is overridable and defaults to the chart`` () =
    // An empty flag must leave the chart's pin alone. monitor.image= resolves to
    // ":latest" or fails the pull.
    let src =
        System.IO.File.ReadAllText("../../../../FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs")

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
        System.IO.File.ReadAllText("../../../../FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs")

    Assert.Contains("worker.requireNodeLabels[0]=purpose:%s", src)
    Assert.Contains("if context.pubnetParallelCatchupPoolPrefix <> \"\" then i + 1 else i", src)


[<Fact>]
let ``the pool maps ride their own --set with their commas escaped`` () =
    // Every other option is folded into ONE comma-joined --set; these maps are
    // themselves comma-separated, so folding them in delivers a map of one tier.
    let src =
        System.IO.File.ReadAllText("../../../../FSLibrary/MissionHistoryPubnetParallelCatchupV2.fs")

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
    [ "peakAnonBytes"
      "peakWorkingSetBytes"
      "peakEphemeralBytes"
      "txApply"
      "seconds"
      "wallSeconds" ]

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
        completedMap [ "420", unmeasured (measuredRecord 420 900L)
                       "840", unmeasured (measuredRecord 420 950L)
                       "1260", unmeasured (measuredRecord 420 990L) ]

    match rangeProfileDocument "pvc" 20000 completed with
    | None -> ()
    | Some doc ->
        let ranges = doc.["ranges"] :?> JObject

        failwithf "wrote a profile artifact with %d ranges and zero measurements: %s" ranges.Count (ranges.ToString())


[<Fact>]
let ``a measured run still produces a complete profile`` () =
    // The over-correction guard: a good read must still write everything.
    let completed =
        completedMap [ "420", measuredRecord 420 900L
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
        completedMap [ "1200", unmeasured (measuredRecord 400 900L)
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
    [<Fact>]
    member __.``ParseQuorumIntersectionInfo handles intersecting result``() =
        let json = """{ "node": "GAAA", "qset": {},
                 "transitive": { "intersection": true, "node_count": 6,
                                 "last_check_ledger": 12,
                                 "critical": [["GBBB"], ["GCCC", "GDDD"]] } }"""

        match ParseQuorumIntersectionInfo json with
        | None -> failwith "expected Some"
        | Some qi ->
            Assert.True(qi.intersection)
            Assert.Equal(6, qi.nodeCount)
            Assert.Equal(12, qi.lastCheckLedger)
            Assert.Equal<Set<string> list>([ Set.ofList [ "GBBB" ]; Set.ofList [ "GCCC"; "GDDD" ] ], qi.criticalGroups)
            Assert.True(qi.potentialSplit.IsNone)

    [<Fact>]
    member __.``ParseQuorumIntersectionInfo handles split result``() =
        let json = """{ "node": "GAAA", "qset": {},
                 "transitive": { "intersection": false, "node_count": 6,
                                 "last_check_ledger": 20, "last_good_ledger": 15,
                                 "potential_split": [["GBBB", "GCCC"], ["GDDD"]] } }"""

        match ParseQuorumIntersectionInfo json with
        | None -> failwith "expected Some"
        | Some qi ->
            Assert.False(qi.intersection)
            Assert.Equal<Set<string> list>([], qi.criticalGroups)

            match qi.potentialSplit with
            | Some (a, b) ->
                Assert.Equal<Set<string>>(Set.ofList [ "GBBB"; "GCCC" ], a)
                Assert.Equal<Set<string>>(Set.ofList [ "GDDD" ], b)
            | None -> failwith "expected potential_split"

    [<Fact>]
    member __.``ParseQuorumIntersectionInfo returns None without results``() =
        Assert.True((ParseQuorumIntersectionInfo """{ "node": "GAAA", "qset": {} }""").IsNone)

        let json = """{ "transitive": { "intersection": true, "node_count": 3,
                                 "last_check_ledger": 5, "critical": null } }"""

        match ParseQuorumIntersectionInfo json with
        | Some qi -> Assert.Equal<Set<string> list>([], qi.criticalGroups)
        | None -> failwith "expected Some"

    [<Fact>]
    member __.``ParseMetricCount reads counter or defaults to zero``() =
        let json = """{ "metrics": { "scp.qic.successful-run": { "type": "counter", "count": 3 },
                              "scp.qic.result-potential-split": { "type": "counter", "count": 1 },
                              "scp.qic.no-count": { "type": "counter" } } }"""

        Assert.Equal(3, ParseMetricCount json "scp.qic.successful-run")
        Assert.Equal(1, ParseMetricCount json "scp.qic.result-potential-split")
        Assert.Equal(0, ParseMetricCount json "scp.qic.no-count")
        Assert.Equal(0, ParseMetricCount json "scp.qic.failed-run")
        Assert.Equal(0, ParseMetricCount """{ }""" "scp.qic.failed-run")

    [<Fact>]
    member __.``QuorumIntersectionChecker mission is registered``() =
        Assert.True(StellarMission.allMissions.ContainsKey "QuorumIntersectionChecker")
