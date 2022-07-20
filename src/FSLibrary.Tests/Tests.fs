module Tests

open StellarDestination
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
open MissionCatchupHelpers
open Xunit.Abstractions


[<Fact>]
let ``Network nonce looks reasonable`` () =
    let nonce = MakeNetworkNonce None
    let nstr = nonce.ToString()
    Assert.Matches(Regex("^ssc-[a-z0-9-]+$"), nstr)

let coreSetOptions =
    { CoreSetOptions.GetDefault "stellar/stellar-core" with
          syncStartupDelay = None }

let coreSet = MakeLiveCoreSet "test" coreSetOptions
let passOpt : NetworkPassphrase option = None

let ctx : MissionContext =
    { kube = null
      destination = Destination(System.IO.Path.GetTempPath())
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
      numNodes = 100
      namespaceProperty = "stellar-supercluster"
      logLevels = { LogDebugPartitions = []; LogTracePartitions = [] }
      ingressClass = "private"
      ingressInternalDomain = "local"
      ingressExternalHost = None
      ingressExternalPort = 80
      exportToPrometheus = false
      probeTimeout = 10
      coreResources = SmallTestResources
      keepData = true
      unevenSched = false
      requireNodeLabels = []
      avoidNodeLabels = []
      tolerateNodeTaints = []
      apiRateLimit = 10
      pubnetData = None
      flatQuorum = None
      tier1Keys = None
      peerReadingCapacity = None
      peerFloodCapacity = None
      sleepMainThread = None
      flowControlSendMoreBatchSize = None
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
      opCountDistribution =
          Some(
              __SOURCE_DIRECTORY__
              + "/../FSLibrary/csv-type-samples/sample-loadgen-op-count-distribution.csv"
          )
      networkSizeLimit = 100
      tier1OrgsToAdd = 0
      nonTier1NodesToAdd = 0
      randomSeed = 0
      pubnetParallelCatchupStartingLedger = 0
      tag = None }

let netdata = __SOURCE_DIRECTORY__ + "/../../../data/public-network-data-2021-01-05.json"
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
        Assert.Contains("LOADGEN_OP_COUNT_FOR_TESTING = [1, 2, 10]", toml)
        Assert.Contains("LOADGEN_OP_COUNT_DISTRIBUTION_FOR_TESTING = [80, 19, 1]", toml)

    // Test init config
    // REVERTME: temporarily avoid looking for HTTP_PORT=0 on InitContainers
    // let initCfg = nCfg.StellarCoreCfg(coreSet, 1, InitCoreContainer)
    // Assert.Contains("HTTP_PORT = 0", initCfg.ToString())

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
        let exp = "{ ; }"
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
             Assert.Contains(coreSets, (fun cs -> cs.name = (CoreSetName "expert")))
             let sdfCoreSet = List.find (fun cs -> cs.name = sdfCoreSetName) coreSets
             Assert.Equal(3, sdfCoreSet.options.nodeCount)
             let cfg = nCfg.StellarCoreCfg(sdfCoreSet, 0, MainCoreContainer)
             let toml = cfg.ToString()
             Assert.Contains("[QUORUM_SET.sub1]", toml)
             Assert.Contains("[HISTORY.local]", toml)
             Assert.Matches(Regex("VALIDATORS.*blockdaemon-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*stellar-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*keybase-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*wirexapp-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*coinqvest-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*satoshipay-0"), toml)
             Assert.Matches(Regex("VALIDATORS.*lobstr-0"), toml))

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
