module Tests

open StellarDestination
open StellarMissionContext
open Xunit
open System
open System.Text.RegularExpressions

open StellarCoreSet
open StellarCoreCfg
open StellarShellCmd
open StellarNetworkCfg
open StellarKubeSpecs
open StellarNetworkData
open StellarNetworkDelays
open Xunit.Abstractions
open k8s


[<Fact>]
let ``Network nonce looks reasonable`` () =
    let nonce = MakeNetworkNonce()
    let nstr = nonce.ToString()
    Assert.Matches(Regex("^ssc-[a-f0-9]+$"), nstr)

let coreSetOptions = { CoreSetOptions.GetDefault "stellar/stellar-core" with syncStartupDelay = None }
let coreSet = MakeLiveCoreSet "test" coreSetOptions
let passOpt : NetworkPassphrase option = None
let ctx : MissionContext =
  {
    kube = null
    destination = Destination(System.IO.Path.GetTempPath())
    image = "stellar/stellar-core"
    oldImage = None
    txRate = 10
    maxTxRate = 10
    numAccounts = 1000
    numTxs = 1000
    spikeSize = 1000
    spikeInterval = 10
    numNodes = 100
    namespaceProperty = "stellar-supercluster"
    logLevels = { LogDebugPartitions=[]; LogTracePartitions=[] }
    ingressDomain = "local"
    exportToPrometheus = false
    probeTimeout = 10
    coreResources = SmallTestResources
    keepData = true
    apiRateLimit = 10
  }

let nCfg = MakeNetworkCfg ctx [coreSet] passOpt

type Tests(output:ITestOutputHelper) =

    [<Fact>]
    member __.``TOML Config looks reasonable`` () =
        let cfg = nCfg.StellarCoreCfg(coreSet, 1)
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
        Assert.Contains("PREFERRED_PEERS = [\"" + peer0DNS + "\", \"" + peer1DNS + "\", \"" + peer2DNS + "\"]", toml)
        Assert.Contains("[HISTORY.test-0]", toml)
        Assert.Contains("\"curl -sf http://" + peer0DNS + "/{0} -o {1}\"", toml)


    [<Fact>]
    member __.``Core init commands look reasonable`` () =
        let cmds = nCfg.getInitCommands PeerSpecificConfigFile coreSet.options
        let cmdStr = ShAnd(cmds).ToString()
        let exp = "{ stellar-core new-db --conf \"/cfg-${STELLAR_CORE_PEER_SHORT_NAME}/stellar-core.cfg\" && " +
                    "{ stellar-core new-hist local --conf \"/cfg-${STELLAR_CORE_PEER_SHORT_NAME}/stellar-core.cfg\" || true; } && " +
                    "stellar-core force-scp --conf \"/cfg-${STELLAR_CORE_PEER_SHORT_NAME}/stellar-core.cfg\"; }"
        Assert.Equal(exp, cmdStr)

    [<Fact>]
    member __.``Shell convenience methods work`` () =
        let cmds = [|
                    ShCmd.DefVarSub "pid" [|"pidof"; "postgresql"|];
                    ShCmd.OfStrs [| "kill"; "-HUP"; "${pid}"; |]
                    |]
        let s = (ShCmd.ShSeq cmds).ToString()
        let exp = "{ pid=`pidof postgresql`; kill -HUP \"${pid}\"; }"
        Assert.Equal(s, exp)

    [<Fact>]
    member __.``Inverse threshold function is actually inverse`` () =
        for sz = 1 to 20 do
            for thr = 1 to sz do
                let pct = percentOfThreshold sz thr
                Assert.Equal(thr, thresholdOfPercent sz pct)

    [<Fact>]
    member __.``Public network conversion looks reasonable`` () =
        let coreSets = FullPubnetCoreSets "stellar/stellar-core" false
        let nCfg = MakeNetworkCfg ctx coreSets passOpt
        let sdfCoreSetName = CoreSetName "www-stellar-org"
        Assert.Contains(coreSets, fun cs -> cs.name = sdfCoreSetName)
        let sdfCoreSet = List.find (fun cs -> cs.name = sdfCoreSetName) coreSets
        let cfg = nCfg.StellarCoreCfg(sdfCoreSet, 0)
        let toml = cfg.ToString()
        Assert.Contains("[QUORUM_SET.sub1]", toml)
        Assert.Contains("[HISTORY.www-stellar-org-0]", toml)
        Assert.Contains("http://history.stellar.org/prd/core-live/core_live_001", toml)
        Assert.Matches(Regex("VALIDATORS.*stellar-blockdaemon-com-0"), toml)
        Assert.Matches(Regex("VALIDATORS.*www-stellar-org-0"), toml)
        Assert.Matches(Regex("VALIDATORS.*keybase-io-0"), toml)
        Assert.Matches(Regex("VALIDATORS.*wirexapp-com-0"), toml)
        Assert.Matches(Regex("VALIDATORS.*coinqvest-com-0"), toml)
        Assert.Matches(Regex("VALIDATORS.*satoshipay-io-0"), toml)
        Assert.Matches(Regex("VALIDATORS.*lobstr-co-0"), toml)

    [<Fact>]
    member __.``Geographic calculations are reasonable`` () =
        // We want to test ping time and distance calculations for
        // a variety of node locations both far apart and close together.

        // Ashburn Virginia USA suburb of Washington a.k.a. AWS us-east-1
        // and GCP us-east-4; home to all SDF nodes, 2 LOBSTR nodes,
        // PaySend, Stellarbeat, 2 Satoshipay nodes, BAC, LockerX,
        // and all Blockdaemon nodes.
        let Ashburn = {lat = 38.89511; lon = -77.03637}

        // Beauharnois, suburb of Montreal a.k.a. OVH DC, home of LAPO 4,
        // PublicNode Bootes, etc.
        let Beauharnois = {lat = 45.2986777; lon= -73.9288762}

        // Chennai India a.k.a. IBM/Softlayer DC, home of IBM India node
        let Chennai = {lat = 13.08784; lon = 80.27847}

        // Columbus Ohio USA a.k.a. AWS DC (us-east-2) home of Stellarport Ohio
        let Columbus = {lat = 39.9828671; lon = -83.1309106}

        // Falkenstein/Vogtland Germany a.k.a. Hetzner DC
        // home of StellarExpertV2, LOBSTR 1, HelpCoin 1, COINQVEST
        // "germany", PublicNode Hercules, etc.
        let Falkenstein = {lat = 50.4788652; lon = 12.3348363}

        // Frankfurt Germany a.k.a. AWS DC (eu-central-1) and Hetzner DC
        // home of keybase 2 and SCHUNK 1
        let Frankfurt = {lat = 50.11552; lon = 8.68417}

        // IBM/Softlayer DC home of IBM HK node
        let HongKong = {lat = 22.3526738; lon = 113.9876171}

        // Portland Oregon USA a.k.a. AWS DC (us-west-1) home of keybase1
        let Portland = {lat = 45.5426916; lon = -122.7243663}

        // Pudong, suburb of Shanghai a.k.a. Azure DC (china-east-2),
        // home of fchain core3
        let Pudong = {lat = 31.0856396; lon = 121.4547635}

        // Purfleet UK, suburb of London, a.k.a. OVH and Azure DCs (uksouth)
        // home of StellarExpertV3, PublicNode Lyra, Wirex UK, Sakkex UK, etc.
        let Purfleet = {lat = 51.4819587; lon = 0.2220319}

        // Sao Paulo Brazil a.k.a. AWS DC (sa-east-1) and IBM/Softlayer
        // DC, home of at least IBM Brazil node
        let SaoPaulo = {lat = -23.6821604; lon = -46.8754795}

        // Singapore a.k.a. DCs for OVH, AWS (ap-southeast-1),
        // Azure (southeastasia), DigitalOcean. Home of LAPO 6,
        // Wirex Singapore, fchain core 2, Hawking, etc.
        let Singapore = {lat = 1.3437449; lon = 103.7540051}

        // Tokyo Japan a.k.a. AWS DC (ap-northeast-1) home of lots
        // of nodes.
        let Tokyo = {lat = 35.6895; lon = 139.69171}

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
    member __.``Traffic control commands are reasonable`` () =
        let Ashburn = {lat = 38.89511; lon = -77.03637}
        let Beauharnois = {lat = 45.2986777; lon= -73.9288762}
        let Chennai = {lat = 13.08784; lon = 80.27847}
        let n1 = PodName "n1"
        let ip1 = "192.168.1.236"
        let n2 = PodName "n2"
        let ip2 = "192.168.1.237"
        let cmds = getNetworkDelayCommands Ashburn [(n1,Beauharnois,ip1);
                                                    (n2,Chennai,ip2)]

        let mutable cmdStr = ""
        for item in cmds do
            cmdStr <- cmdStr + (item.ToString())

        Assert.Contains(ip1, cmdStr)
        Assert.Contains(ip2, cmdStr)
        let delay1 = int(networkDelayInMs Ashburn Beauharnois)
        let delay2 = int(networkDelayInMs Ashburn Chennai)
        Assert.Contains(sprintf "netem delay %dms" delay1, cmdStr)
        Assert.Contains(sprintf "netem delay %dms" delay2, cmdStr)

