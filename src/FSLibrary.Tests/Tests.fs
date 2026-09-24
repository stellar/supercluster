module Tests

open StellarDestination
open StellarDotnetSdk.Accounts
open StellarMissionContext
open Xunit
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
      overlayV2Optimized = false
      keepData = true
      unevenSched = false
      oneStellarCorePerHost = false
      dedicatedNodes = false
      requireNodeLabels = []
      avoidNodeLabels = []
      tolerateNodeTaints = []
      apiRateLimit = 10
      httpProxyReplicas = 2
      pubnetData = None
      measureE2eLatency = false
      flatQuorum = None
      tier1Keys = None
      loadgenKeys = None
      maxConnections = None
      fullyConnectTier1 = false
      peerReadingCapacity = None
      peerFloodCapacity = None
      enableBackgroundSigValidation = false
      enableParallelApply = false
      enableInMemoryBuckets = false
      disableTxMetaForTesting = false
      offeredTxBytesPerSec = None
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
      tag = None
      numPregeneratedTxs = None
      enableTailLogging = true
      catchupSkipKnownResultsForTesting = None
      checkEventsAreConsistentWithEntryDiffs = None
      updateSorobanCosts = None
      genesisTestAccountCount = None
      asanOptions = None
      coreEnv = []
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
      tier1OrgCount = None
      pregenerateTxsPerValidator = false
      runForMinBlockTime = false
      forceOldStyleTriggerTimerPct = 0
      uniformDrift = []
      bimodalDrift = []
      driftPct = 0
      ledgerCloseTimeMs = None
      forceOldStyleTriggerTimer = None }

let netdata =
    __SOURCE_DIRECTORY__
    + "/../../../data/public-network-data-2026-06-03-trimmed-located.json"

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
    member __.``One-stellar-core-per-host adds a required self anti-affinity scoped to stellar-core pods``() =
        let nCfg =
            MakeNetworkCfg
                { ctx with
                      dedicatedNodes = true
                      oneStellarCorePerHost = true
                      installNetworkDelay = Some false }
                [ coreSet ]
                passOpt

        let tmpl = nCfg.ToPodTemplateSpec coreSet

        // Stellar-core pods carry the one-per-host label the self term selects on.
        Assert.Equal(CfgVal.onePerHostLabelValue, tmpl.Metadata.Labels.[CfgVal.onePerHostLabelKey])

        let terms =
            tmpl.Spec.Affinity.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution

        Assert.Equal(2, terms.Count)

        // The existing isolation from other runs is preserved (run-nonce NotIn).
        let otherRuns =
            terms
            |> Seq.find (fun t -> (Seq.exactlyOne t.LabelSelector.MatchExpressions).OperatorProperty = "NotIn")

        Assert.Equal("kubernetes.io/hostname", otherRuns.TopologyKey)
        Assert.Equal(nCfg.Nonce, Seq.exactlyOne (Seq.exactlyOne otherRuns.LabelSelector.MatchExpressions).Values)

        // The new term: this run's stellar-core pods repel each other per hostname.
        let selfTerm =
            terms
            |> Seq.find (fun t -> (Seq.exactlyOne t.LabelSelector.MatchExpressions).OperatorProperty = "In")

        Assert.Equal("kubernetes.io/hostname", selfTerm.TopologyKey)
        Assert.Equal("stellar-core", selfTerm.LabelSelector.MatchLabels.["app"])
        Assert.Equal(CfgVal.onePerHostLabelValue, selfTerm.LabelSelector.MatchLabels.[CfgVal.onePerHostLabelKey])
        let expr = Seq.exactlyOne selfTerm.LabelSelector.MatchExpressions
        Assert.Equal(CfgVal.runNonceLabelKey, expr.Key)
        Assert.Equal(nCfg.Nonce, Seq.exactlyOne expr.Values)

        // The HTTP proxy is not a stellar-core pod: it keeps only the other-runs
        // term and does not carry the label, so it is neither repelled nor repelling.
        let proxy = nCfg.ToHttpProxyDeployment().Spec.Template
        Assert.False(proxy.Metadata.Labels.ContainsKey CfgVal.onePerHostLabelKey)

        let proxyTerms =
            proxy.Spec.Affinity.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution

        Assert.Equal(1, proxyTerms.Count)
        Assert.Equal("NotIn", (Seq.exactlyOne proxyTerms.[0].LabelSelector.MatchExpressions).OperatorProperty)

    [<Fact>]
    member __.``One-stellar-core-per-host keeps node-label filters and works without dedicated nodes``() =
        // Node-label filters (require + avoid) must survive unchanged next to
        // the self term, and the self term must not depend on dedicatedNodes.
        let nCfg =
            MakeNetworkCfg
                { ctx with
                      oneStellarCorePerHost = true
                      requireNodeLabels = [ ("purpose", Some "largetests") ]
                      avoidNodeLabels = [ ("spot", None) ]
                      installNetworkDelay = Some false }
                [ coreSet ]
                passOpt

        let spec = (nCfg.ToPodTemplateSpec coreSet).Spec

        let nodeTerm =
            Seq.exactlyOne spec.Affinity.NodeAffinity.RequiredDuringSchedulingIgnoredDuringExecution.NodeSelectorTerms

        let exprs =
            nodeTerm.MatchExpressions
            |> Seq.map (fun e -> e.Key, e.OperatorProperty)
            |> Set.ofSeq

        Assert.Equal<Set<string * string>>(
            Set.ofList [ ("purpose", "In")
                         ("spot", "DoesNotExist") ],
            exprs
        )

        let terms = spec.Affinity.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution
        Assert.Equal(1, terms.Count)
        Assert.Equal("In", (Seq.exactlyOne terms.[0].LabelSelector.MatchExpressions).OperatorProperty)
        Assert.Equal(CfgVal.onePerHostLabelValue, terms.[0].LabelSelector.MatchLabels.[CfgVal.onePerHostLabelKey])

        // Job pods keep the plain mission affinity: node filters, no pod anti-affinity.
        let proxy = nCfg.ToHttpProxyDeployment().Spec.Template.Spec
        Assert.NotNull(proxy.Affinity.NodeAffinity)
        Assert.Null(proxy.Affinity.PodAntiAffinity)

    [<Fact>]
    member __.``One-stellar-core-per-host is off by default``() =
        let nCfg =
            MakeNetworkCfg { ctx with dedicatedNodes = true; installNetworkDelay = Some false } [ coreSet ] passOpt

        let tmpl = nCfg.ToPodTemplateSpec coreSet
        // Neither the label nor the self term, as upstream.
        Assert.False(tmpl.Metadata.Labels.ContainsKey CfgVal.onePerHostLabelKey)

        let terms =
            tmpl.Spec.Affinity.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution

        Assert.Equal(1, terms.Count)
        Assert.Equal("NotIn", (Seq.exactlyOne terms.[0].LabelSelector.MatchExpressions).OperatorProperty)

    [<Fact>]
    member __.``One-per-host placement accepts scheduled pods on distinct nodes``() =
        let mustBeScheduled = [ "sts-a-0"; "sts-a-1"; "sts-b-0" ]
        let ok = [ ("sts-a-0", "node-1"); ("sts-a-1", "node-2"); ("sts-b-0", "node-3") ]

        match StellarStatefulSets.validateOnePerHost mustBeScheduled ok with
        | Ok hosts -> Assert.Equal<string list>([ "node-1"; "node-2"; "node-3" ], hosts)
        | Error e -> failwithf "expected Ok, got Error %s" e

        // Another core set whose pods are still starting does not fail the check.
        let othersStarting = ok @ [ ("sts-c-0", ""); ("sts-c-1", "node-4") ]

        match StellarStatefulSets.validateOnePerHost mustBeScheduled othersStarting with
        | Ok hosts -> Assert.Equal(4, hosts.Length)
        | Error e -> failwithf "expected Ok, got Error %s" e

    [<Fact>]
    member __.``One-per-host placement rejects incomplete or shared mappings``() =
        let mustBeScheduled = [ "sts-a-0"; "sts-a-1"; "sts-b-0" ]

        let expectError (needle: string) (observed: (string * string) list) =
            match StellarStatefulSets.validateOnePerHost mustBeScheduled observed with
            | Ok _ -> failwithf "expected an error mentioning %s" needle
            | Error e -> Assert.Contains(needle, e)

        // Empty listing: nothing observed at all.
        expectError "missing" []
        // Truncated listing: one pod absent.
        expectError "missing" [ ("sts-a-0", "node-1"); ("sts-a-1", "node-2") ]
        // Duplicate pod names.
        expectError
            "duplicate"
            [ ("sts-a-0", "node-1")
              ("sts-a-0", "node-2")
              ("sts-a-1", "node-3")
              ("sts-b-0", "node-4") ]
        // Unscheduled placeholders: empty and whitespace node names, even with otherwise distinct hosts.
        expectError "unscheduled" [ ("sts-a-0", "node-1"); ("sts-a-1", ""); ("sts-b-0", "node-3") ]
        expectError "unscheduled" [ ("sts-a-0", "node-1"); ("sts-a-1", "node-2"); ("sts-b-0", "  ") ]
        // Shared host, within the checked pods or with another core set's pod.
        expectError "share worker nodes" [ ("sts-a-0", "node-1"); ("sts-a-1", "node-1"); ("sts-b-0", "node-3") ]

        expectError
            "share worker nodes"
            [ ("sts-a-0", "node-1")
              ("sts-a-1", "node-2")
              ("sts-b-0", "node-3")
              ("sts-c-0", "node-2") ]
        // Nothing to check is itself an error (never vacuously pass).
        match StellarStatefulSets.validateOnePerHost [] [] with
        | Ok _ -> failwith "expected an error for an empty set"
        | Error e -> Assert.Contains("no stellar-core pods to check", e)

    [<Fact>]
    member __.``One-stellar-core-per-host covers watchers and reserves each core container's limits``() =
        let watcherSet = MakeLiveCoreSet "watcher" { coreSetOptions with validate = false }

        let core (c: MissionContext) (cs: CoreSet) =
            let nCfg = MakeNetworkCfg { c with installNetworkDelay = Some false } [ cs ] passOpt
            let tmpl = nCfg.ToPodTemplateSpec cs

            tmpl,
            tmpl.Spec.Containers
            |> Seq.find (fun k -> k.Name = CfgVal.stellarCoreContainerName "run")

        let onePerHost = { ctx with oneStellarCorePerHost = true }

        // Watchers are stellar-core pods too: one per host like validators.
        let watcherTmpl, _ = core onePerHost watcherSet
        Assert.Equal(CfgVal.onePerHostLabelValue, watcherTmpl.Metadata.Labels.[CfgVal.onePerHostLabelKey])

        // Each core container requests its limits, so the node an autoscaler
        // provisions for it fits what it may use; other containers keep theirs.
        let tmpl, coreContainer = core onePerHost coreSet

        for KeyValue (name, limit) in coreContainer.Resources.Limits do
            Assert.Equal(limit, coreContainer.Resources.Requests.[name])

        let plainTmpl, plainCore = core ctx coreSet
        Assert.NotEqual(plainCore.Resources.Limits.["cpu"], plainCore.Resources.Requests.["cpu"])

        let history (t: k8s.Models.V1PodTemplateSpec) =
            (t.Spec.Containers |> Seq.find (fun k -> k.Name = "history")).Resources.Requests.["cpu"]

        Assert.Equal(history plainTmpl, history tmpl)

    [<Fact>]
    member __.``Reserving limits raises requests to the limits and keeps unlimited requests``() =
        let r = makeResourceRequirements 500 128 4000 6000
        let reserved = reserveLimits r
        Assert.Equal(k8s.Models.ResourceQuantity("4000m"), reserved.Requests.["cpu"])
        Assert.Equal(k8s.Models.ResourceQuantity("6000Mi"), reserved.Requests.["memory"])
        // The input is not modified (the requirement values are shared).
        Assert.Equal(k8s.Models.ResourceQuantity("500m"), r.Requests.["cpu"])

        let requests = System.Collections.Generic.Dictionary<string, k8s.Models.ResourceQuantity>()
        requests.["cpu"] <- k8s.Models.ResourceQuantity("8000m")
        let limits = System.Collections.Generic.Dictionary<string, k8s.Models.ResourceQuantity>()
        limits.["memory"] <- k8s.Models.ResourceQuantity("16Gi")

        let noCpuLimit =
            reserveLimits (k8s.Models.V1ResourceRequirements(requests = requests, limits = limits))

        Assert.Equal(k8s.Models.ResourceQuantity("8000m"), noCpuLimit.Requests.["cpu"])
        Assert.Equal(k8s.Models.ResourceQuantity("16Gi"), noCpuLimit.Requests.["memory"])

    [<Fact>]
    member __.``Unschedulable stellar-core pods are read from their scheduling condition``() =
        let t0 = System.DateTime(2026, 9, 23, 12, 0, 0, System.DateTimeKind.Utc)

        let msg = "0/3 nodes are available: 3 node(s) didn't match pod anti-affinity rules."

        let pod (conditions: k8s.Models.V1PodCondition list) =
            k8s.Models.V1Pod(
                Metadata = k8s.Models.V1ObjectMeta(Name = "sts-a-0"),
                Status = k8s.Models.V1PodStatus(Conditions = ResizeArray(conditions))
            )

        let stuck =
            pod [ k8s.Models.V1PodCondition(
                      Type = "PodScheduled",
                      Status = "False",
                      Reason = "Unschedulable",
                      Message = msg,
                      LastTransitionTime = System.Nullable t0
                  ) ]

        Assert.Equal(Some(t0, msg), StellarStatefulSets.unschedulableSince stuck)

        Assert.Equal(
            None,
            StellarStatefulSets.unschedulableSince (
                pod [ k8s.Models.V1PodCondition(Type = "PodScheduled", Status = "True") ]
            )
        )

        Assert.Equal(
            None,
            StellarStatefulSets.unschedulableSince (
                k8s.Models.V1Pod(Metadata = k8s.Models.V1ObjectMeta(Name = "sts-a-1"))
            )
        )

    [<Fact>]
    member __.``Autoscaler events are told apart from the default scheduler``() =
        let ev reason comp = k8s.Models.Corev1Event(Reason = reason, ReportingComponent = comp)

        Assert.True(StellarStatefulSets.autoscalerProvisioning (ev "Nominated" "karpenter"))
        Assert.True(StellarStatefulSets.autoscalerProvisioning (ev "TriggeredScaleUp" "cluster-autoscaler"))
        Assert.False(StellarStatefulSets.autoscalerProvisioning (ev "FailedScheduling" "default-scheduler"))
        Assert.True(StellarStatefulSets.autoscalerCannotProvision (ev "FailedScheduling" "karpenter"))
        Assert.True(StellarStatefulSets.autoscalerCannotProvision (ev "NotTriggerScaleUp" "cluster-autoscaler"))
        Assert.False(StellarStatefulSets.autoscalerCannotProvision (ev "FailedScheduling" "default-scheduler"))

    [<Fact>]
    member __.``Stellar-core pod scheduling stalls fail fast with an actionable message``() =
        let t0 = System.DateTime(2026, 9, 23, 12, 0, 0, System.DateTimeKind.Utc)
        let at (s: int) = t0.AddSeconds(float s)

        let msg =
            "0/16 nodes are available: 13 node(s) had untolerated taint(s), 3 node(s) didn't match pod anti-affinity rules."

        let verdict = StellarStatefulSets.schedulingStallVerdict
        let stalled = [ ("sts-a-0", t0, msg, None) ]

        // Normal provisioning waits (under a minute on Karpenter) never fail.
        Assert.Equal(None, verdict (at 60) true 30 stalled)
        Assert.Equal(None, verdict (at 179) false 30 stalled)

        // With no autoscaler provisioning, 3 minutes unschedulable fails with
        // the scheduler's reason and what to change.
        match verdict (at 180) false 30 stalled with
        | Some e ->
            Assert.Contains("sts-a-0", e)
            Assert.Contains(msg, e)
            Assert.Contains("each of the 30 stellar-core pods", e)
            Assert.Contains("--tolerate-node-taints", e)
        | None -> failwith "expected a stall verdict"

        // While an autoscaler provisions nodes, the bound is 10 minutes.
        Assert.Equal(None, verdict (at 599) true 30 stalled)
        Assert.True((verdict (at 600) true 30 stalled).IsSome)

        // The autoscaler's own "cannot provision" report fails after a short confirmation.
        let refused =
            [ ("sts-a-1", t0, msg, Some "all available instance types exceed limits for nodepool") ]

        Assert.Equal(None, verdict (at 59) true 30 refused)

        match verdict (at 60) true 30 refused with
        | Some e -> Assert.Contains("exceed limits for nodepool", e)
        | None -> failwith "expected an autoscaler verdict"

        Assert.Equal(None, verdict (at 100000) false 30 [])

    [<Fact>]
    member __.``Only events of the current stellar-core pods count toward the autoscaler verdict``() =
        let t0 = System.DateTime(2026, 9, 23, 12, 0, 0, System.DateTimeKind.Utc)

        let ev reason uid (at: int) =
            k8s.Models.Corev1Event(
                Reason = reason,
                ReportingComponent = "karpenter",
                Message = reason + " message",
                InvolvedObject = k8s.Models.V1ObjectReference(Kind = "Pod", Name = "sts-a-0", Uid = uid),
                LastTimestamp = System.Nullable(t0.AddSeconds(float at))
            )

        let stalled = [ ("sts-a-0", t0, "0/3 nodes are available") ]

        let failureOf (view: (string * System.DateTime * string * string option) list) =
            let _, _, _, failure = view.Head
            failure

        // Reports about an earlier pod with the same name are ignored.
        let active, stale =
            StellarStatefulSets.autoscalerView
                (set [ "new-uid" ])
                [ ev "FailedScheduling" "old-uid" 0; ev "Nominated" "old-uid" 5 ]
                stalled

        Assert.False(active)
        Assert.Equal(None, failureOf stale)

        // For the current pod a cannot-provision report counts, unless a
        // nomination came after it.
        let _, refused =
            StellarStatefulSets.autoscalerView (set [ "new-uid" ]) [ ev "FailedScheduling" "new-uid" 10 ] stalled

        Assert.Equal(Some "FailedScheduling message", failureOf refused)

        let active, renominated =
            StellarStatefulSets.autoscalerView
                (set [ "new-uid" ])
                [ ev "FailedScheduling" "new-uid" 10; ev "Nominated" "new-uid" 20 ]
                stalled

        Assert.True(active)
        Assert.Equal(None, failureOf renominated)

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
             // Ensure that an org with both tier1 and non-tier1 nodes (such as
             // Public Node) got split into two separate core sets.
             Assert.Contains(coreSets, (fun cs -> cs.name = (CoreSetName "publicnode")))
             Assert.Contains(coreSets, (fun cs -> cs.name = (CoreSetName "publicnode-non-tier1")))
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
             Assert.Matches(Regex("VALIDATORS.*lobstr-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*franklintempleton-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*moneygram-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*range-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*withobsrvr-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*ylds-0"), toml))

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

// A stand-in for the apiserver: rejects the first `failures` requests with 429,
// then succeeds, and counts how many times it was actually called.
type private ThrottlingStub(failures: int) =
    inherit System.Net.Http.HttpMessageHandler()
    let mutable calls = 0
    member __.Calls = calls

    override __.SendAsync(_req, _ct) =
        calls <- calls + 1

        let code =
            if calls <= failures then
                System.Net.HttpStatusCode.TooManyRequests
            else
                System.Net.HttpStatusCode.OK

        System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(code))

let private sendThrough
    (handler: ApiRateLimit.ThrottleRetryHandler)
    (stub: ThrottlingStub)
    (verb: System.Net.Http.HttpMethod)
    =
    handler.InnerHandler <- stub
    use invoker = new System.Net.Http.HttpMessageInvoker(handler)

    let req =
        new System.Net.Http.HttpRequestMessage(verb, "http://apiserver.invalid/api/v1/nodes")

    invoker.SendAsync(req, System.Threading.CancellationToken.None).Result

[<Fact>]
let ``Throttle retry rides out 429s and returns the eventual success`` () =
    let stub = new ThrottlingStub(3)
    let handler = new ApiRateLimit.ThrottleRetryHandler(System.TimeSpan.FromSeconds 30.0)
    let resp = sendThrough handler stub System.Net.Http.HttpMethod.Get
    Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)
    // Three rejections plus the attempt that succeeded.
    Assert.Equal(4, stub.Calls)

[<Fact>]
let ``Throttle retry leaves DELETE alone so teardown stays bounded`` () =
    let stub = new ThrottlingStub(5)
    let handler = new ApiRateLimit.ThrottleRetryHandler(System.TimeSpan.FromSeconds 30.0)
    let resp = sendThrough handler stub System.Net.Http.HttpMethod.Delete
    Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, resp.StatusCode)
    Assert.Equal(1, stub.Calls)

[<Fact>]
let ``Throttle retry gives up at the deadline and surfaces the 429`` () =
    let stub = new ThrottlingStub(1000)
    let handler = new ApiRateLimit.ThrottleRetryHandler(System.TimeSpan.Zero)
    let resp = sendThrough handler stub System.Net.Http.HttpMethod.Get
    // The 429 must reach the caller rather than being swallowed or masked.
    Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, resp.StatusCode)
    Assert.Equal(1, stub.Calls)

[<Fact>]
let ``Throttle retry never starts an attempt the budget cannot pay for`` () =
    let stub = new ThrottlingStub(1000)
    // 750ms budget: the 500ms backoff fits, the 1000ms one does not, so it stops.
    let handler = new ApiRateLimit.ThrottleRetryHandler(System.TimeSpan.FromMilliseconds 750.0)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let resp = sendThrough handler stub System.Net.Http.HttpMethod.Get
    sw.Stop()
    Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, resp.StatusCode)
    // One wait of 500ms and two attempts; the second wait would have overrun.
    Assert.Equal(2, stub.Calls)
    // Stopping early is the point: it must not have slept out the full budget.
    Assert.True(sw.Elapsed < System.TimeSpan.FromMilliseconds 750.0, sprintf "took %O" sw.Elapsed)

[<Fact>]
let ``core-env parser accepts NAME=VALUE pairs and preserves values with '='`` () =
    let parsed =
        MissionContext.parseCoreEnv [ "STELLAR_OVERLAY_UDP_RECEIVE_BUFFER_BYTES=4194304"
                                      "STELLAR_OVERLAY_UDP_RECEIVE_BUFFER_FORCE=1"
                                      "STELLAR_OVERLAY_TRACE_TIMINGS=1"
                                      "RUST_LOG=info,stellar_overlay::timing=debug"
                                      "FOO=a=b" ]

    Assert.Equal<(string * string) list>(
        [ ("STELLAR_OVERLAY_UDP_RECEIVE_BUFFER_BYTES", "4194304")
          ("STELLAR_OVERLAY_UDP_RECEIVE_BUFFER_FORCE", "1")
          ("STELLAR_OVERLAY_TRACE_TIMINGS", "1")
          ("RUST_LOG", "info,stellar_overlay::timing=debug")
          ("FOO", "a=b") ],
        parsed
    )

    Assert.Empty(MissionContext.parseCoreEnv [])

[<Fact>]
let ``core-env parser rejects malformed and duplicate entries`` () =
    Assert.ThrowsAny<System.Exception>(fun () -> MissionContext.parseCoreEnv [ "NOEQUALS" ] |> ignore)
    |> ignore

    Assert.ThrowsAny<System.Exception>(fun () -> MissionContext.parseCoreEnv [ "=novalue-name" ] |> ignore)
    |> ignore

    Assert.ThrowsAny<System.Exception>
        (fun () ->
            MissionContext.parseCoreEnv [ "A=1"
                                          "A=2" ]
            |> ignore)
    |> ignore

    // Blank entries and names that are not environment variable names are
    // malformed too, not silently dropped or passed on to Kubernetes.
    // A trailing newline in the name must not pass either ('$' would allow it).
    for bad in [ ""
                 "   "
                 "FOO-BAR=1"
                 "A B=1"
                 "1A=2"
                 " RUST_LOG=info"
                 "foo\n=3"
                 "FOO\r\n=1" ] do
        Assert.ThrowsAny<System.Exception>(fun () -> MissionContext.parseCoreEnv [ bad ] |> ignore)
        |> ignore

[<Fact>]
let ``core-env cannot override the variables the harness sets`` () =
    for reserved in [ "STELLAR_CORE_PEER_SHORT_NAME"; "ASAN_OPTIONS" ] do
        Assert.ThrowsAny<System.Exception>(fun () -> rejectReservedCoreEnv [ (reserved, "x") ] |> ignore)
        |> ignore

        Assert.ThrowsAny<System.Exception>
            (fun () ->
                CoreContainerForCommand
                    "img"
                    NoConfigFile
                    None
                    [ (reserved, "x") ]
                    SmallTestResources
                    [| "run" |]
                    [||]
                    [| "core-0" |]
                |> ignore)
        |> ignore

    // Other names pass.
    rejectReservedCoreEnv [ ("RUST_LOG", "info") ]

[<Fact>]
let ``core container env carries opt-in extra variables after the fixed ones`` () =
    let c =
        CoreContainerForCommand
            "img"
            NoConfigFile
            None
            [ ("STELLAR_OVERLAY_UDP_RECEIVE_BUFFER_BYTES", "4194304") ]
            SmallTestResources
            [| "run" |]
            [||]
            [| "core-0" |]

    let names = c.Env |> Seq.map (fun e -> e.Name) |> List.ofSeq

    Assert.Equal<string list>(
        [ "STELLAR_CORE_PEER_SHORT_NAME"
          "ASAN_OPTIONS"
          "STELLAR_OVERLAY_UDP_RECEIVE_BUFFER_BYTES" ],
        names
    )

    Assert.Equal(
        "4194304",
        (c.Env |> Seq.find (fun e -> e.Name = "STELLAR_OVERLAY_UDP_RECEIVE_BUFFER_BYTES"))
            .Value
    )

[<Fact>]
let ``core container env is unchanged when no extra variables are given`` () =
    let c =
        CoreContainerForCommand "img" NoConfigFile None [] SmallTestResources [| "run" |] [||] [| "core-0" |]

    Assert.Equal<string list>(
        [ "STELLAR_CORE_PEER_SHORT_NAME"; "ASAN_OPTIONS" ],
        c.Env |> Seq.map (fun e -> e.Name) |> List.ofSeq
    )

[<Fact>]
let ``Min block time candidates are the whole seconds in the range, bounds included`` () =
    Assert.Equal<int list>([ 4000; 5000 ], MinBlockTimeTest.wholeSecondCandidates 4000 5000)
    Assert.Equal<int list>([ 2000; 3000; 4000 ], MinBlockTimeTest.wholeSecondCandidates 1500 4999)
    Assert.Equal<int list>([ 1000 ], MinBlockTimeTest.wholeSecondCandidates 0 1000)
    Assert.Empty(MinBlockTimeTest.wholeSecondCandidates 1100 1900)

[<Fact>]
let ``Min block time search finds the smallest passing candidate`` () =
    let candidates = [ 1000 .. 1000 .. 5000 ]

    for threshold in candidates do
        let evaluated = System.Collections.Generic.List<int>()

        let result =
            MinBlockTimeTest.searchMinPassing
                candidates
                (fun t ->
                    evaluated.Add t
                    t >= threshold)

        Assert.Equal(Some threshold, result)
        Assert.InRange(evaluated.Count, 1, 3)

    Assert.Equal(None, MinBlockTimeTest.searchMinPassing candidates (fun _ -> false))
    Assert.Equal(None, MinBlockTimeTest.searchMinPassing [] (fun _ -> true))

    // The default [4000, 5000] range evaluates 4000 first, then 5000 only if 4000 fails.
    let evaluated = System.Collections.Generic.List<int>()

    let result =
        MinBlockTimeTest.searchMinPassing
            [ 4000; 5000 ]
            (fun t ->
                evaluated.Add t
                t >= 5000)

    Assert.Equal(Some 5000, result)
    Assert.Equal<int list>([ 4000; 5000 ], List.ofSeq evaluated)

[<Theory>]
[<InlineData(1000)>]
[<InlineData(2000)>]
[<InlineData(3000)>]
[<InlineData(5000)>]
let ``All validator partitions preserve rate duration and disjoint account slices`` (rate: int) =
    let full =
        { LoadGen.GetDefault() with
              mode = MixedPregenSACPayment
              accounts = 1200000
              txrate = rate
              txs = rate * 960
              classicTxRate = Some 0
              sorobanTxRate = Some rate }

    let shares = StellarStatefulSets.PartitionValidatorLoad 57 true full
    Assert.Equal(57, shares.Length)
    Assert.Equal(rate, shares |> List.sumBy (fun s -> s.txrate))
    Assert.Equal(rate * 960, shares |> List.sumBy (fun s -> s.txs))
    Assert.Equal(1199964, shares |> List.sumBy (fun s -> s.accounts))
    let rates = shares |> List.map (fun s -> s.txrate)
    Assert.True(List.max rates - List.min rates <= 1)

    shares
    |> List.iteri
        (fun i s ->
            Assert.Equal(21052, s.accounts)
            Assert.Equal(i * 21052, s.offset)
            Assert.Equal(s.txrate * 960, s.txs)
            Assert.Equal(Some s.txrate, s.sorobanTxRate)
            Assert.Equal(Some 0, s.classicTxRate)
            Assert.True(s.offset + s.accounts <= full.accounts))

    for a, b in List.pairwise shares do
        Assert.True(a.offset + a.accounts <= b.offset)

[<Fact>]
let ``57 validator topology and initialization match every generator partition`` () =
    let sets = StableApproximateTier1CoreSetsWithOrgCount "frozen-image" false (Some 19)
    Assert.Equal(19, sets.Length)
    Assert.Equal(57, sets |> List.sumBy (fun s -> s.options.nodeCount))

    let full =
        { LoadGen.GetDefault() with
              mode = MixedPregenSACPayment
              accounts = 1200000
              txrate = 5000
              txs = 4800000
              classicTxRate = Some 0
              sorobanTxRate = Some 5000 }

    let shares = StellarStatefulSets.PartitionValidatorLoad 57 true full
    let keys = sets |> List.collect (fun s -> s.keys |> Array.toList)
    Assert.Equal(57, keys |> List.map (fun k -> k.AccountId) |> Set.ofList |> Set.count)

    sets
    |> List.iteri
        (fun orgIndex cs ->
            Assert.Equal(Some true, cs.options.tier1)
            Assert.Equal(3, cs.options.nodeCount)
            Assert.Equal(57, cs.options.preferredPeersMap.Value.Count)

            for peers in cs.options.preferredPeersMap.Value.Values do
                Assert.Equal(56, peers.Length)

            match cs.options.quorumSet with
            | ExplicitQuorum q ->
                Assert.Equal(Some 67, q.thresholdPercent)
                Assert.Equal(19, q.innerQuorumSets.Length)

                for inner in q.innerQuorumSets do
                    Assert.Equal(Some 51, inner.thresholdPercent)
                    Assert.Equal(3, inner.validators.Count)
            | _ -> failwith "Expected explicit organization quorum"

            let options =
                { cs.options with
                      initialization =
                          { cs.options.initialization with
                                pregenerateTxs = Some(10000, 21052, orgIndex * 3 * 21052) } }

            for i in 0 .. 2 do
                let actual = PregenerationOptionsForPeer options i
                let share = shares.[orgIndex * 3 + i]
                Assert.Equal(Some(10000, share.accounts, share.offset), actual.initialization.pregenerateTxs))

[<Fact>]
let ``Fixed duration partition rejects a partial second transaction budget`` () =
    let full = { LoadGen.GetDefault() with accounts = 1200000; txrate = 5000; txs = 4800001 }

    Assert.Throws<System.ArgumentException>(fun () -> StellarStatefulSets.PartitionValidatorLoad 57 true full |> ignore)
    |> ignore

[<Fact>]
let ``Submission accounting includes all started validators and preserves legacy selection`` () =
    let sets = StableApproximateTier1CoreSetsWithOrgCount "frozen-image" false (Some 19)
    let selected = StellarStatefulSets.LoadgenPeerIndices true sets
    Assert.Equal(57, selected.Length)

    Assert.Equal(
        57,
        selected
        |> List.map (fun (cs, i) -> cs.keys.[i].AccountId)
        |> Set.ofList
        |> Set.count
    )

    Assert.Equal<int list>(
        [ 19; 19; 19 ],
        [ for i in 0 .. 2 -> selected |> List.filter (fun (_, j) -> i = j) |> List.length ]
    )
    // A one-peer-per-organization tally would incorrectly report only one third.
    let submitted = selected |> List.sumBy (fun (_, i) -> if i = 0 then 34000 else 33000)
    Assert.Equal(1900000, submitted)
    let legacy = StellarStatefulSets.LoadgenPeerIndices false sets
    Assert.Equal(19, legacy.Length)
    Assert.All(legacy, (fun (_, i) -> Assert.Equal(0, i)))

let private tomlKeyCount (key: string) (toml: string) =
    let pattern = sprintf "^%s = " (Regex.Escape key)
    Regex.Matches(toml, pattern, RegexOptions.Multiline).Count

let private validatorToml (c: MissionContext) =
    let cfg = MakeNetworkCfg c [ coreSet ] passOpt
    cfg.StellarCoreCfg(coreSet, 0, MainCoreContainer).ToString()

let private bucketIndexKey = "BUCKETLIST_DB_INDEX_PAGE_SIZE_EXPONENT"

let private v2ctx = { ctx with overlayV2Optimized = true }

[<Fact>]
let ``Overlay v2 perf missions default to in-memory BucketListDB and emit the key exactly once`` () =
    let perf = MissionContext.withOverlayV2PerfDefaults v2ctx
    Assert.True(perf.enableInMemoryBuckets)
    let toml = validatorToml perf
    Assert.Equal(1, tomlKeyCount bucketIndexKey toml)
    Assert.Contains(bucketIndexKey + " = 0", toml)
    // --run-for-max-tps used to add the key in its own branch as well; with the
    // perf default on, it must still appear only once.
    Assert.Equal(1, tomlKeyCount bucketIndexKey (validatorToml { perf with runForMaxTps = Some "classic" }))
    Assert.Equal(1, tomlKeyCount bucketIndexKey (validatorToml { perf with runForMaxTps = Some "soroban" }))
    // --in-memory-buckets on a perf mission is the same setting, still once.
    let forced =
        MissionContext.withOverlayV2PerfDefaults { v2ctx with enableInMemoryBuckets = true }

    Assert.Equal(1, tomlKeyCount bucketIndexKey (validatorToml forced))

[<Fact>]
let ``Without --overlay-v2-optimized perf missions get the command-line context unchanged`` () =
    let plain = MissionContext.withOverlayV2PerfDefaults ctx
    Assert.Equal(ctx, plain)
    Assert.Equal(0, tomlKeyCount bucketIndexKey (validatorToml plain))
    Assert.Equal(0, tomlKeyCount "DISABLE_TX_META_FOR_TESTING" (validatorToml plain))

    Assert.Equal(
        SimulatePubnetTier1PerfResources,
        MissionContext.perfMissionCoreResources ctx SimulatePubnetTier1PerfResources
    )

    Assert.Equal(MaxTPSClassicResources, MissionContext.perfMissionCoreResources ctx MaxTPSClassicResources)
    Assert.Equal(PerfBenchmarkResources, MissionContext.perfMissionCoreResources v2ctx SimulatePubnetTier1PerfResources)
    Assert.Equal(PerfBenchmarkResources, MissionContext.perfMissionCoreResources v2ctx MaxTPSClassicResources)

[<Fact>]
let ``Overlay v2 perf missions run one stellar-core pod per host`` () =
    Assert.True((MissionContext.withOverlayV2PerfDefaults v2ctx).oneStellarCorePerHost)
    Assert.False((MissionContext.withOverlayV2PerfDefaults ctx).oneStellarCorePerHost)

[<Fact>]
let ``Non-perf missions keep disk-backed BucketListDB unless --in-memory-buckets or --run-for-max-tps`` () =
    Assert.Equal(0, tomlKeyCount bucketIndexKey (validatorToml ctx))
    Assert.Equal(1, tomlKeyCount bucketIndexKey (validatorToml { ctx with enableInMemoryBuckets = true }))
    // The long-standing --run-for-max-tps behaviour is unchanged, alone or
    // combined with --in-memory-buckets.
    Assert.Equal(1, tomlKeyCount bucketIndexKey (validatorToml { ctx with runForMaxTps = Some "classic" }))

    let both = { ctx with enableInMemoryBuckets = true; runForMaxTps = Some "classic" }
    Assert.Equal(1, tomlKeyCount bucketIndexKey (validatorToml both))

let private cpuOf (r: k8s.Models.V1ResourceRequirements) = r.Limits.["cpu"].ToDecimal()
let private gib = 1024M * 1024M * 1024M

[<Fact>]
let ``Perf benchmark validators have no CPU limit and keep their reservation`` () =
    let res = GetCoreResourceRequirements PerfBenchmarkResources
    Assert.False(res.Limits.ContainsKey "cpu")
    Assert.Equal(8M, res.Requests.["cpu"].ToDecimal())
    Assert.Equal(16M * gib, res.Requests.["memory"].ToDecimal())
    Assert.Equal(16M * gib, res.Limits.["memory"].ToDecimal())

    // Missions outside the perf set, and perf missions without
    // --overlay-v2-optimized (upstream's Tier1 perf resources), keep their CPU limits.
    Assert.Equal(4M, cpuOf (GetCoreResourceRequirements SimulatePubnetTier1PerfResources))
    Assert.Equal(4M, cpuOf (GetCoreResourceRequirements MaxTPSClassicResources))

[<Fact>]
let ``Perf benchmark pods drop only the core container CPU limit`` () =
    let nCfgPerf =
        MakeNetworkCfg
            { ctx with
                  coreResources = PerfBenchmarkResources
                  installNetworkDelay = Some false }
            [ coreSet ]
            passOpt

    let containers = (nCfgPerf.ToPodTemplateSpec coreSet).Spec.Containers
    let core = containers |> Seq.find (fun c -> c.Name = CfgVal.stellarCoreContainerName "run")
    Assert.False(core.Resources.Limits.ContainsKey "cpu")
    Assert.Equal(8M, core.Resources.Requests.["cpu"].ToDecimal())
    let sidecars = containers |> Seq.filter (fun c -> c.Name <> core.Name) |> List.ofSeq
    Assert.NotEmpty(sidecars)
    // The history sidecar keeps HistoryResourceRequirements (50m CPU limit).
    let history = sidecars |> List.find (fun c -> c.Name = "history")
    Assert.Equal(0.05M, cpuOf history.Resources)
    Assert.All(sidecars, (fun c -> Assert.True(c.Resources.Limits.ContainsKey "cpu")))

[<Fact>]
let ``Perf benchmark core containers default TOKIO_WORKER_THREADS unless --core-env sets it`` () =
    let envOf (extra: (string * string) list) (cr: CoreResources) =
        let c =
            CoreContainerForCommand "img" NoConfigFile None extra cr [| "run" |] [||] [| "core-0" |]

        c.Env |> Seq.map (fun e -> e.Name, e.Value) |> List.ofSeq

    let tokio env = env |> List.filter (fun (n, _) -> n = "TOKIO_WORKER_THREADS")

    let perf = PerfBenchmarkResources
    Assert.Equal<(string * string) list>([ ("TOKIO_WORKER_THREADS", "8") ], tokio (envOf [] perf))
    // A --core-env value wins and is not duplicated.
    Assert.Equal<(string * string) list>(
        [ ("TOKIO_WORKER_THREADS", "4") ],
        tokio (envOf [ ("TOKIO_WORKER_THREADS", "4") ] perf)
    )
    // Other --core-env entries keep their place ahead of the default.
    Assert.Equal<string list>(
        [ "STELLAR_CORE_PEER_SHORT_NAME"
          "ASAN_OPTIONS"
          "RUST_LOG"
          "TOKIO_WORKER_THREADS" ],
        envOf [ ("RUST_LOG", "info") ] perf |> List.map fst
    )
    // Other resource classes get no default.
    Assert.Empty(tokio (envOf [] SimulatePubnetTier1PerfResources))
    Assert.Empty(tokio (envOf [] MaxTPSClassicResources))

let private txMetaKey = "DISABLE_TX_META_FOR_TESTING"

[<Fact>]
let ``Overlay v2 perf missions disable test-only tx meta and emit the key exactly once`` () =
    let perf = MissionContext.withOverlayV2PerfDefaults v2ctx
    Assert.True(perf.disableTxMetaForTesting)
    let toml = validatorToml perf
    Assert.Equal(1, tomlKeyCount txMetaKey toml)
    Assert.Contains(txMetaKey + " = true", toml)
    // Still once alongside the other perf and max-TPS settings.
    Assert.Equal(1, tomlKeyCount txMetaKey (validatorToml { perf with runForMaxTps = Some "classic" }))
    Assert.Equal(1, tomlKeyCount txMetaKey (validatorToml { perf with runForMinBlockTime = true }))

    // The init container's config (new-db / new-hist) gets it too.
    let initToml =
        (MakeNetworkCfg perf [ coreSet ] passOpt)
            .StellarCoreCfg(coreSet, 0, InitCoreContainer)
            .ToString()

    Assert.Equal(1, tomlKeyCount txMetaKey initToml)

[<Fact>]
let ``Non-perf missions never disable tx meta`` () =
    Assert.Equal(0, tomlKeyCount txMetaKey (validatorToml ctx))
    Assert.Equal(0, tomlKeyCount txMetaKey (validatorToml { ctx with runForMaxTps = Some "classic" }))

[<Fact>]
let ``--overlay-v2-optimized lists its settings in the run log only when set`` () =
    Assert.Empty(MissionContext.describeOverlayV2 ctx)
    Assert.NotEmpty(MissionContext.describeOverlayV2 v2ctx)

[<Fact>]
let ``--overlay-v2-optimized tunes MinBlockTime tx-set limits, load window and tx-set byte allowances`` () =
    Assert.Equal(200, MissionContext.txSetSizeBufferPct ctx)
    Assert.Equal(125, MissionContext.txSetSizeBufferPct v2ctx)
    Assert.Equal(300, MissionContext.minBlockTimeLoadDurationSec ctx)
    Assert.Equal(960, MissionContext.minBlockTimeLoadDurationSec v2ctx)

    let mib = 1024 * 1024
    let allowances = MissionContext.txSetByteAllowances
    let offering classic soroban = Some(classic, soroban)
    Assert.Equal(None, allowances ctx)
    // Under the flag only a mission that declares its offered load gets a split.
    Assert.Equal(None, allowances v2ctx)
    Assert.Equal(Some(1 * mib, 9 * mib), allowances { v2ctx with offeredTxBytesPerSec = offering 0L 5_000_000L })
    Assert.Equal(None, allowances { ctx with offeredTxBytesPerSec = offering 0L 5_000_000L })
    // The max-TPS modes keep their own splits, with or without the flag.
    Assert.Equal(Some(9 * mib, 1 * mib), allowances { v2ctx with runForMaxTps = Some "classic" })
    Assert.Equal(Some(1 * mib, 9 * mib), allowances { ctx with runForMaxTps = Some "soroban" })
    Assert.Equal(None, allowances { v2ctx with runForMaxTps = Some "classic-prev-version" })
    // The run log reports what the configs get.
    let logged (c: MissionContext) =
        MissionContext.describeOverlayV2 c
        |> List.find (fun l -> l.StartsWith "tx-set byte allowances")

    Assert.StartsWith("tx-set byte allowances: MinBlockTime* splits 10 MiB", logged v2ctx)

    Assert.Equal(
        "tx-set byte allowances: classic 1.0 MiB, Soroban 9.0 MiB",
        logged { v2ctx with offeredTxBytesPerSec = offering 0L 5_000_000L }
    )

    Assert.Equal(
        "tx-set byte allowances: classic 9.0 MiB, Soroban 1.0 MiB",
        logged { v2ctx with runForMaxTps = Some "classic" }
    )

[<Fact>]
let ``MinBlockTime tx-set limits scale with the buffer percentage`` () =
    // 1000 TPS at T=2000ms is 2000 txs per ledger.
    Assert.Equal(4000, MinBlockTimeTest.classicMaxTxSetSizeForTargetPct 2000 1000 200)
    Assert.Equal(2500, MinBlockTimeTest.classicMaxTxSetSizeForTargetPct 2000 1000 125)
    Assert.Equal(8750, MinBlockTimeTest.classicMaxTxSetSizeForTargetPct 1000 7000 125)
    // The historical 2x is the 200% case; tiny limits are floored at 100.
    Assert.Equal(
        MinBlockTimeTest.classicMaxTxSetSizeForTarget 3000 1234,
        MinBlockTimeTest.classicMaxTxSetSizeForTargetPct 3000 1234 200
    )

    Assert.Equal(100, MinBlockTimeTest.classicMaxTxSetSizeForTargetPct 1000 3 125)

[<Fact>]
let ``The tx-set byte allowance splits 10 MiB by the offered load, at least 1 MiB each`` () =
    let mib = 1024 * 1024
    let split = MissionContext.splitTxSetByteAllowance
    // Soroban-only (E0) and classic-only runs.
    Assert.Equal((1 * mib, 9 * mib), split 0L 5_000_000L)
    Assert.Equal((9 * mib, 1 * mib), split 600_000L 0L)
    // Nothing offered: core's even split.
    Assert.Equal((5 * mib, 5 * mib), split 0L 0L)
    // Proportional: 1:4 gives 2 MiB and 8 MiB.
    Assert.Equal((2 * mib, 8 * mib), split 200_000L 800_000L)
    // The smaller phase keeps 1 MiB.
    Assert.Equal((1 * mib, 9 * mib), split 1L 5_000_000L)

    // Whenever the offered bytes per ledger fit in 10 MiB, each phase gets at
    // least its share: 3000 classic TPS (200 B) and 1000 SAC TPS (1000 B) at
    // T = 2 s and 125% offer 1.5 MB and 2.5 MB per ledger.
    let classic, soroban = split (3000L * 200L) (1000L * 1000L)
    Assert.True(int64 classic >= 3000L * 200L * 5L / 2L)
    Assert.True(int64 soroban >= 1000L * 1000L * 5L / 2L)
    Assert.Equal(10 * mib, classic + soroban)

[<Fact>]
let ``The tx-set byte allowances reach the node configs`` () =
    let mib = 1024 * 1024
    let plain = validatorToml ctx
    Assert.DoesNotContain("TESTING_MAX_SOROBAN_BYTE_ALLOWANCE", plain)
    Assert.DoesNotContain("TESTING_MAX_CLASSIC_BYTE_ALLOWANCE", plain)

    // Under the flag without a declared load, core's defaults.
    Assert.DoesNotContain("TESTING_MAX_SOROBAN_BYTE_ALLOWANCE", validatorToml v2ctx)

    let classicOnly = validatorToml { v2ctx with offeredTxBytesPerSec = Some(600_000L, 0L) }
    Assert.Contains(sprintf "TESTING_MAX_CLASSIC_BYTE_ALLOWANCE = %d" (9 * mib), classicOnly)
    Assert.Contains(sprintf "TESTING_MAX_SOROBAN_BYTE_ALLOWANCE = %d" (1 * mib), classicOnly)

    // --run-for-max-tps keeps its own split.
    let maxTps = validatorToml { v2ctx with runForMaxTps = Some "classic" }
    Assert.Contains(sprintf "TESTING_MAX_CLASSIC_BYTE_ALLOWANCE = %d" (9 * mib), maxTps)
    Assert.Contains(sprintf "TESTING_MAX_SOROBAN_BYTE_ALLOWANCE = %d" (1 * mib), maxTps)

[<Fact>]
let ``Tier1 topology keeps its 10 organizations unless --tier1-org-count adds diverse ones`` () =
    let orgs (sets: CoreSet list) = sets |> List.map (fun cs -> cs.name.StringName) |> List.sort
    let upstream = StableApproximateTier1CoreSets "img" false
    Assert.Equal(10, upstream.Length)
    Assert.Equal<string list>(orgs upstream, orgs (StableApproximateTier1CoreSetsWithOrgCount "img" false None))
    Assert.Equal<string list>(orgs upstream, orgs (StableApproximateTier1CoreSetsWithOrgCount "img" false (Some 10)))
    let twelve = StableApproximateTier1CoreSetsWithOrgCount "img" false (Some 12)
    Assert.Equal<string list>(List.sort (orgs upstream @ [ "x01"; "x02" ]), orgs twelve)
    // Adding organizations never changes the base ones.
    let locsOf (sets: CoreSet list) name = (sets |> List.find (fun cs -> cs.name.StringName = name)).options.nodeLocs

    for org in orgs upstream do
        Assert.Equal(locsOf upstream org, locsOf twelve org)

    Assert.Equal(40, tier1MaxOrgCount)
    let all = StableApproximateTier1CoreSetsWithOrgCount "img" false (Some 40)
    Assert.Equal(40, all.Length)
    Assert.Equal(120, all |> List.sumBy (fun cs -> cs.options.nodeCount))
    Assert.All(all, (fun cs -> Assert.Equal(3, cs.options.nodeLocs.Value.Length)))
    // Far more locations than the base topology's 13, on every inhabited
    // continent (southern-hemisphere and eastern-hemisphere sites included).
    let distinctLocs (sets: CoreSet list) = sets |> List.collect (fun cs -> cs.options.nodeLocs.Value) |> List.distinct

    let locs = distinctLocs all
    Assert.Equal(13, (distinctLocs upstream).Length)

    Assert.Equal(
        37,
        (distinctLocs (StableApproximateTier1CoreSetsWithOrgCount "img" false (Some 19)))
            .Length
    )

    Assert.Equal(51, locs.Length)
    Assert.Contains(locs, (fun l -> l.lat < -30.0 && l.lon > 140.0)) // Oceania
    Assert.Contains(locs, (fun l -> l.lat < -20.0 && l.lon < -40.0)) // South America
    Assert.Contains(locs, (fun l -> l.lat < -20.0 && l.lon > 10.0 && l.lon < 40.0)) // southern Africa
    // Each extra organization is distinct: no two share the same three locations.
    let extraLocSets =
        all
        |> List.filter (fun cs -> cs.name.StringName.StartsWith "x")
        |> List.map (fun cs -> List.sort cs.options.nodeLocs.Value)

    Assert.Equal(30, extraLocSets.Length)
    Assert.Equal(30, extraLocSets |> List.distinct |> List.length)
    // The quorum grows with the organizations: 67% of 40 inner sets.
    match all.Head.options.quorumSet with
    | ExplicitQuorum q -> Assert.Equal(40, q.innerQuorumSets.Length)
    | _ -> failwith "Expected explicit organization quorum"

    Assert.ThrowsAny<System.Exception>
        (fun () -> StableApproximateTier1CoreSetsWithOrgCount "img" false (Some 9) |> ignore)
    |> ignore

    Assert.ThrowsAny<System.Exception>
        (fun () -> StableApproximateTier1CoreSetsWithOrgCount "img" false (Some 41) |> ignore)
    |> ignore

[<Fact>]
let ``MinBlockTime marks only its active load generators, matching by name`` () =
    let a = MakeLiveCoreSet "a" coreSetOptions
    let b = MakeLiveCoreSet "b" coreSetOptions
    // The formation's copy of a set can carry other option changes (pregenerated-tx slices).
    let aChanged = { a with options = { a.options with nodeCount = 5 } }
    let sets = [ aChanged; b ]
    let marked = MinBlockTimeTest.markLoadGenerators [ a ] sets

    Assert.Equal<bool list>([ true; false ], marked |> List.map (fun cs -> cs.options.generatesLoad))
    Assert.Equal(5, marked.Head.options.nodeCount)

[<Fact>]
let ``The e2e latency metric goes on core sets that generate load, only when measuring`` () =
    let toml (c: MissionContext) (cs: CoreSet) =
        (MakeNetworkCfg c [ cs ] passOpt)
            .StellarCoreCfg(cs, 0, MainCoreContainer)
            .ToString()

    let key = "LOADGEN_MEASURE_TX_E2E_LATENCY_FOR_TESTING"
    let only = [ coreSet ]
    let generator = MinBlockTimeTest.markLoadGenerators only only |> List.head

    let measuring = { ctx with runForMinBlockTime = true; measureE2eLatency = true }
    Assert.Equal(1, tomlKeyCount key (toml measuring generator))
    Assert.Equal(0, tomlKeyCount key (toml measuring coreSet))
    Assert.Equal(0, tomlKeyCount key (toml { measuring with measureE2eLatency = false } generator))
    // --overlay-v2-optimized adds no metric of its own, and no tx batching or
    // parallel-apply keys: the Rust-overlay core ignores the first and has
    // deprecated the second.
    let v2 = { measuring with overlayV2Optimized = true }
    Assert.Equal(0, tomlKeyCount key (toml v2 coreSet))
    Assert.Equal(0, tomlKeyCount "EXPERIMENTAL_TX_BATCH_MAX_SIZE" (toml v2 generator))
    Assert.Equal(0, tomlKeyCount "EXPERIMENTAL_PARALLEL_LEDGER_APPLY" (toml v2 generator))

[<Fact>]
let ``--measure-e2e-latency needs --loadgen-keys except for MinBlockTime missions`` () =
    let needsKeys = MissionContext.e2eLatencyNeedsLoadgenKeys
    let minBlockTimeOnly = [ "MinBlockTimeClassic"; "MinBlockTimeMixed" ]
    let withOther = [ "MinBlockTimeMixed"; "SimulatePubnet" ]
    Assert.False(needsKeys [ "MinBlockTimeMixed" ])
    Assert.False(needsKeys minBlockTimeOnly)
    Assert.True(needsKeys [ "MaxTPSMixed" ])
    Assert.True(needsKeys withOther)

[<Fact>]
let ``DATABASE, the postgres sidecar and the pod's postgres setup agree`` () =
    // DATABASE is postgres, the pod has the postgres sidecar, and the core
    // container waits for it; for every node, or for none.
    let postgresEverywhere (c: MissionContext) (cs: CoreSet) =
        let cfg = MakeNetworkCfg { c with installNetworkDelay = Some false } [ cs ] passOpt
        let toml = cfg.StellarCoreCfg(cs, 0, MainCoreContainer).ToString()
        let containers = (cfg.ToPodTemplateSpec cs).Spec.Containers
        let core = containers |> Seq.find (fun k -> k.Name = CfgVal.stellarCoreContainerName "run")

        let waits =
            (String.concat " " core.Command + String.concat " " core.Args)
                .Contains "pg_isready"

        [ toml.Contains "DATABASE = \"postgresql://"
          containers |> Seq.exists (fun k -> k.Name = "postgres")
          waits ]

    // Job pods decide it the same way.
    let jobPostgres (c: MissionContext) (opts: CoreSetOptions) =
        let cfg = { MakeNetworkCfg c [ coreSet ] passOpt with jobCoreSetOptions = Some opts }
        let containers = (cfg.GetJobPodTemplateSpec "job" [| "run" |] "img" false).Spec.Containers
        let core = containers |> Seq.head

        let waits =
            (String.concat " " core.Command + String.concat " " core.Args)
                .Contains "pg_isready"

        [ containers |> Seq.exists (fun k -> k.Name = "postgres"); waits ]

    let pgSet = { coreSet with options = { coreSet.options with dbType = Postgres } }
    let maxTps = { ctx with runForMaxTps = Some "soroban" }
    Assert.Equal<bool list>([ true; true ], jobPostgres maxTps coreSetOptions)
    Assert.Equal<bool list>([ true; true ], jobPostgres ctx pgSet.options)
    Assert.Equal<bool list>([ false; false ], jobPostgres ctx coreSetOptions)
    // The core set's dbType is the default Sqlite; max-TPS still runs on postgres.
    Assert.Equal<bool list>([ true; true; true ], postgresEverywhere maxTps coreSet)
    Assert.Equal<bool list>([ true; true; true ], postgresEverywhere ctx pgSet)
    Assert.Equal<bool list>([ false; false; false ], postgresEverywhere ctx coreSet)

// Steps the bounded mesh wait over samples taken every poll, indexed by seconds
// into an attempt; returns the verdict and when it came.
let private simulateMeshWait
    (sampleAt: int -> StellarStatefulSets.MeshSample)
    : StellarStatefulSets.MeshWaitVerdict * int =
    let bounds = StellarStatefulSets.meshWaitBounds

    let rec go (s: StellarStatefulSets.MeshWaitState) (t: int) =
        match StellarStatefulSets.stepMeshWait bounds s t (sampleAt t) with
        | StellarStatefulSets.KeepWaiting, next when t < 3600 -> go next (t + bounds.pollSec)
        | verdict, _ -> verdict, t

    go StellarStatefulSets.initialMeshWaitState 0

let private meshSample (silent: string list) (full: int) (connections: int) : StellarStatefulSets.MeshSample =
    { StellarStatefulSets.MeshSample.silent = silent
      fullyConnected = full
      total = 4
      connections = connections }

[<Fact>]
let ``The bounded mesh wait returns as soon as the mesh is complete`` () =
    let verdict, at =
        simulateMeshWait (fun t -> if t < 40 then meshSample [] 2 8 else meshSample [] 4 12)

    Assert.Equal(StellarStatefulSets.Meshed, verdict)
    Assert.Equal(40, at)

[<Fact>]
let ``The bounded mesh wait fails a node that never answers without redrawing`` () =
    let verdict, at = simulateMeshWait (fun _ -> meshSample [ "n3" ] 0 0)
    Assert.Equal(StellarStatefulSets.BootTimedOut, verdict)
    Assert.Equal(StellarStatefulSets.meshWaitBounds.bootTimeoutSec, at)

[<Fact>]
let ``The bounded mesh wait redraws a mesh that stops growing`` () =
    // Connections grow until 20 s, then stall short of a full mesh.
    let verdict, at = simulateMeshWait (fun t -> meshSample [] 2 (6 + min t 20 / 5))

    Assert.Equal(StellarStatefulSets.Wedged, verdict)
    Assert.Equal(20 + StellarStatefulSets.meshWaitBounds.stallSec, at)

[<Fact>]
let ``The bounded mesh wait redraws a mesh still incomplete after its budget`` () =
    // Nodes answer from 30 s on; connections keep growing but never fill the mesh.
    let verdict, at =
        simulateMeshWait (fun t -> if t < 30 then meshSample [ "n0" ] 0 0 else meshSample [] 3 t)

    Assert.Equal(StellarStatefulSets.Wedged, verdict)
    Assert.Equal(30 + StellarStatefulSets.meshWaitBounds.meshTimeoutSec, at)

[<Fact>]
let ``Once every node has answered, a missed probe does not fail the boot bound`` () =
    let verdict, at =
        simulateMeshWait (fun t -> if t = 0 then meshSample [] 2 5 else meshSample [ "n1" ] 1 3)

    Assert.Equal(StellarStatefulSets.Wedged, verdict)
    Assert.Equal(StellarStatefulSets.meshWaitBounds.stallSec, at)

[<Fact>]
let ``MIXED_PREGEN runs use at most one generator per requested TPS`` () =
    let sets = StableApproximateTier1CoreSets "img" false
    let count everyValidator tps = (MinBlockTimeTest.activeLoadGenCoreSets everyValidator tps sets).Length
    // With load on every validator each organization brings its 3 validators.
    Assert.Equal(10, count true 5000)
    Assert.Equal(10, count true 30)
    Assert.Equal(3, count true 10)
    Assert.Equal(1, count true 2)
    Assert.Equal(1, count true 0)
    // One generator per organization otherwise, as upstream.
    Assert.Equal(5, count false 5)
    Assert.Equal(10, count false 5000)

[<Fact>]
let ``MIXED_PREGEN load runs on every validator in MinBlockTime runs`` () =
    let minBlock = { ctx with runForMinBlockTime = true }
    Assert.True(LoadOnEveryValidator minBlock MixedPregenSACPayment)
    Assert.False(LoadOnEveryValidator minBlock GeneratePaymentLoad)
    Assert.False(LoadOnEveryValidator ctx MixedPregenSACPayment)

    // Upstream's per-node split: equal accounts and txs, offsets by slice.
    let full =
        { LoadGen.GetDefault() with
              accounts = 1000
              txrate = 100
              txs = 30000
              spikesize = 10 }

    let shares = StellarStatefulSets.PartitionValidatorLoad 3 false full
    Assert.Equal<int list>([ 333; 333; 333 ], shares |> List.map (fun s -> s.accounts))
    Assert.Equal<int list>([ 0; 333; 666 ], shares |> List.map (fun s -> s.offset))
    Assert.Equal<int list>([ 10000; 10000; 10000 ], shares |> List.map (fun s -> s.txs))
    Assert.Equal<int list>([ 34; 33; 33 ], shares |> List.map (fun s -> s.txrate))

[<Fact>]
let ``Validators pregenerate their own account slices only when the MinBlockTime load runs on every validator`` () =
    let pregenOpts =
        { coreSetOptions with
              nodeCount = 3
              initialization = { coreSetOptions.initialization with pregenerateTxs = Some(1000, 100, 0) } }

    let pregenSet = MakeLiveCoreSet "pregen" pregenOpts

    let script (c: MissionContext) =
        let pod =
            (MakeNetworkCfg { c with installNetworkDelay = Some false } [ pregenSet ] passOpt)
                .ToPodTemplateSpec pregenSet

        let core =
            pod.Spec.Containers
            |> Seq.find (fun k -> k.Name = CfgVal.stellarCoreContainerName "run")

        String.concat " " core.Args

    // A MinBlockTime context whose mixed mode is only the option default (e.g.
    // MinBlockTimeClassic) keeps one shared pregeneration command.
    let shared = script { ctx with runForMinBlockTime = true }
    Assert.Contains("'--offset 0'", shared)
    Assert.DoesNotContain("'--offset 100'", shared)
    let perValidator = script { ctx with runForMinBlockTime = true; pregenerateTxsPerValidator = true }

    for offset in [ 0; 100; 200 ] do
        Assert.Contains(sprintf "'--offset %d'" offset, perValidator)
