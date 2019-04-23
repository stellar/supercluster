// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreCfg

open stellar_dotnet_sdk
open Nett

open StellarNetworkCfg

// Submodule of short fixed (or lightly parametrized) config values: names,
// paths, labels, etc.
module CfgVal =
    let httpPort = 11626
    let labels = Map.ofSeq [ "app", "stellar-core" ]
    let labelSelector = "app = stellar-core"
    let stellarCoreImageName = "stellar/stellar-core"
    let stellarCoreBinPath = "/usr/local/bin/stellar-core"
    let cfgMapName = "stellar-core-cfg"
    let cfgVolumeName = "cfg-volume"
    let cfgVolumePath = "/cfg"
    let dataVolumeName = "data-volume"
    let dataVolumePath = "/data"
    let databasePath = dataVolumePath + "/stellar.db"
    let historyPath = dataVolumePath + "/history"
    let bucketsPath = dataVolumePath + "/buckets"
    let localHistName = "local"
    let peerSetName = "peer"
    let peerShortName n = sprintf "%s-%d" peerSetName n
    let peerCfgName n = sprintf "%s.cfg" (peerShortName n)
    let serviceName = "stellar-core"
    let peerDNSName (nonce : NetworkNonce) (n : int) =
        sprintf "%s.%s.%s.svc.cluster.local" (peerShortName n) serviceName (nonce.ToString())
    let peerNameEnvVarName = "STELLAR_CORE_PEER_SHORT_NAME"
    let peerNameEnvCfgFile = cfgVolumePath + "/$(STELLAR_CORE_PEER_SHORT_NAME).cfg"


// Symbolic type for the different sorts of NETWORK_PASSPHRASE that can show up
// in a stellar-core.cfg file. Usually use PrivateNet, which takes a nonce and
// will not collide with any other networks.
type NetworkPassphrase =
    | SDFTestNet
    | SDFMainNet
    | PrivateNet of NetworkNonce
    override self.ToString() : string =
        match self with
        | SDFTestNet -> "Test SDF Network ; September 2015"
        | SDFMainNet -> "Public Global Stellar Network ; September 2015"
        | PrivateNet n -> sprintf "Private test network '%s'" (n.ToString())


// Symbolic type of the different sorts of DATABASE that can show up in a
// stellar-core.cfg. Usually use SQLite3File of some path in a Pod's local
// volume.
type DatabaseURL =
    | SQLite3Memory
    | SQLite3File of path : string
    | PostgreSQL of database : string * user : string * pass : string * host : string
    override self.ToString() : string =
        match self with
        | SQLite3Memory -> "sqlite3://:memory:"
        | SQLite3File s -> sprintf "sqlite3://%s" s
        | PostgreSQL(d, u, p, h) -> sprintf "postgresql://dbname=%s user=%s password=%s host=%s" d u p h


// Represents the contents of a stellar-core.cfg file, along with method to
// write it out to TOML.
type StellarCoreCfg =
    { database : DatabaseURL
      networkPassphrase : NetworkPassphrase
      nodeSeed : KeyPair
      nodeIsValidator : bool
      runStandalone : bool
      knownPeers : string array
      unsafeQuorum : bool
      failureSafety : int
      quorumSet : Map<string, KeyPair> }

    member self.ToTOML() : TomlTable =
        let t = Toml.Create()

        let qset =
            self.quorumSet
            |> Map.toList
            |> List.map (fun (k, v) -> sprintf "%s %s" v.Address k)

        let hist =
            Map.ofSeq [| ("get", sprintf "cp %s/{0} {1}" CfgVal.historyPath)
                         ("put", sprintf "cp {0} %s/{1}" CfgVal.historyPath)
                         ("mkdir", sprintf "mkdir -p %s/{0}" CfgVal.historyPath) |]

        ignore (t.Add("DATABASE", self.database.ToString()))
        ignore (t.Add("HTTP_PORT", int64(CfgVal.httpPort)))
        ignore (t.Add("PUBLIC_HTTP_PORT", true))
        ignore (t.Add("BUCKET_DIR_PATH", CfgVal.bucketsPath))
        ignore (t.Add("NETWORK_PASSPHRASE", self.networkPassphrase.ToString()))
        ignore (t.Add("NODE_SEED", self.nodeSeed.SecretSeed))
        ignore (t.Add("NODE_IS_VALIDATOR", self.nodeIsValidator))
        ignore (t.Add("RUN_STANDALONE", self.runStandalone))
        ignore (t.Add("KNOWN_PEERS", self.knownPeers))
        ignore (t.Add("UNSAFE_QUORUM", self.unsafeQuorum))
        ignore (t.Add("FAILURE_SAFETY", self.failureSafety))
        ignore (t.Add("QUORUM_SET", Map.ofSeq [| ("VALIDATORS", qset) |]))
        let localTab = t.Add("HISTORY", Toml.Create(), TomlObjectFactory.RequireTomlObject()).Added
        ignore (localTab.Add(CfgVal.localHistName, hist))
        t

    override self.ToString() : string = self.ToTOML().ToString()


// Extension to the NetworkCfg type to make StellarCoreCfg objects
// for each of its peers.
type NetworkCfg with
    member self.StellarCoreCfgForPeer(i : int) : StellarCoreCfg =
        let numPeers = Array.length self.peerKeys
        { database = SQLite3File CfgVal.databasePath
          networkPassphrase = PrivateNet self.networkNonce
          nodeSeed = Array.get self.peerKeys i
          nodeIsValidator = true
          runStandalone = false
          knownPeers = Array.init numPeers (CfgVal.peerDNSName self.networkNonce)
          unsafeQuorum = true
          failureSafety = (-1)
          quorumSet = Map.ofSeq (Array.mapi (fun (i : int) k -> ((CfgVal.peerShortName i), k)) self.peerKeys) }
