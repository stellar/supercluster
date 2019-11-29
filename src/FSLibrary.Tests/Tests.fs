module Tests

open System
open Xunit
open System.Text.RegularExpressions

open StellarCoreSet
open StellarCoreCfg
open StellarShellCmd
open StellarNetworkCfg
open StellarKubeSpecs


[<Fact>]
let ``Network nonce looks reasonable`` () =
    let nonce = MakeNetworkNonce()
    let nstr = nonce.ToString()
    Assert.Matches(Regex("^ssc-[a-f0-9]+$"), nstr)


let coreSet = MakeLiveCoreSet "test" CoreSetOptions.Default
let quotas = MakeNetworkQuotas(1,1,1,1,1,1,1)
let loglevels = { LogDebugPartitions=[]; LogTracePartitions=[] }
let nameSpace = "stellar-supercluster"
let storageclass = "default"
let ingress = "local"
let nCfg = MakeNetworkCfg [coreSet] nameSpace quotas loglevels storageclass ingress None


[<Fact>]
let ``TOML Config looks reasonable`` () =
    let cfg = nCfg.StellarCoreCfg(coreSet, 1)
    let toml = cfg.ToString()
    let peer0DNS = nCfg.PeerDNSName coreSet 0
    let peer1DNS = nCfg.PeerDNSName coreSet 1
    let peer2DNS = nCfg.PeerDNSName coreSet 2
    let nonceStr = nCfg.networkNonce.ToString()
    let domain = nonceStr + "-stellar-core." + nameSpace + ".svc.cluster.local"
    Assert.Equal(nonceStr + "-peer-test-0." + domain, peer0DNS)
    Assert.Equal(nonceStr + "-peer-test-1." + domain, peer1DNS)
    Assert.Equal(nonceStr + "-peer-test-2." + domain, peer2DNS)
    Assert.Contains("DATABASE = \"sqlite3:///data/stellar.db\"", toml)
    Assert.Contains("BUCKET_DIR_PATH = \"/data/buckets\"", toml)
    Assert.Contains("PREFERRED_PEERS = [\"" + peer0DNS + "\", \"" + peer1DNS + "\", \"" + peer2DNS + "\"]", toml)
    Assert.Contains("[HISTORY." + nonceStr + "-peer-test-0]", toml)
    Assert.Contains("\"curl -sf http://" + peer0DNS + "/{0} -o {1}\"", toml)


[<Fact>]
let ``Core init commands look reasonable`` () =
    let cmds = nCfg.getInitCommands PeerSpecificConfigFile coreSet.options 
    let cmdStr = ShAnd(cmds).ToString()
    let exp = "{ /usr/local/bin/stellar-core new-db --conf \"/cfg/${STELLAR_CORE_PEER_SHORT_NAME}.cfg\" && " +
                "/usr/local/bin/stellar-core new-hist local --conf \"/cfg/${STELLAR_CORE_PEER_SHORT_NAME}.cfg\" && " +
                "/usr/local/bin/stellar-core force-scp --conf \"/cfg/${STELLAR_CORE_PEER_SHORT_NAME}.cfg\"; }"
    Assert.Equal(exp, cmdStr) 