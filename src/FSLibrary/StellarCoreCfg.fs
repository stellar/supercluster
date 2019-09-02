// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreCfg

open stellar_dotnet_sdk
open Nett

open StellarCoreSet
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
    let peristentVolumeName ns name = sprintf "%s-pv-%s" ns name
    let peristentVolumeClaimName ns name = sprintf "pvc-%s" name
    let databasePath = dataVolumePath + "/stellar.db"
    let historyPath = dataVolumePath + "/history"
    let bucketsPath = dataVolumePath + "/buckets"
    let peerSetName (coreSet: CoreSet) = sprintf "peer-%s" coreSet.name
    let peerShortName (coreSet: CoreSet) n = sprintf "%s-%d" (peerSetName coreSet) n
    let peerCfgName (coreSet: CoreSet) n = sprintf "%s.cfg" (peerShortName coreSet n)
    let localHistName = "local"
    let serviceName = "stellar-core"
    let peerDNSName (nonce : NetworkNonce) (coreSet: CoreSet) n =
        sprintf "%s.%s.%s.svc.cluster.local" (peerShortName coreSet n) serviceName (nonce.ToString())
    let peerNameEnvVarName = "STELLAR_CORE_PEER_SHORT_NAME"
    let peerNameEnvCfgFile = cfgVolumePath + "/$(STELLAR_CORE_PEER_SHORT_NAME).cfg"
    let historyCfgFile = cfgVolumePath + "/nginx.conf"


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
    { network : NetworkCfg
      database : DatabaseURL
      networkPassphrase : NetworkPassphrase
      nodeSeed : KeyPair
      nodeIsValidator : bool
      runStandalone : bool
      preferredPeers : string list
      catchupMode: CatchupMode
      automaticMaintenancePeriod: int
      automaticMaintenanceCount: int
      accelerateTime: bool
      generateLoad: bool
      manualClose: bool
      invariantChecks: string list
      unsafeQuorum : bool
      failureSafety : int
      quorumSet : Map<string, KeyPair>
      historyNodes : Map<string, string>
      historyGetCommands : Map<string, string> }

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
        let remoteHist dnsName =
            Map.ofSeq [| ("get", sprintf "curl -sf %s/{0} -o {1}" dnsName) |]
        let getHist getCommand = 
            Map.ofSeq [| ("get", getCommand) |]

        t.Add("DATABASE", self.database.ToString()) |> ignore
        t.Add("HTTP_PORT", int64(CfgVal.httpPort)) |> ignore
        t.Add("PUBLIC_HTTP_PORT", true) |> ignore
        t.Add("BUCKET_DIR_PATH", CfgVal.bucketsPath) |> ignore
        t.Add("NETWORK_PASSPHRASE", self.networkPassphrase.ToString()) |> ignore
        t.Add("NODE_SEED", self.nodeSeed.SecretSeed) |> ignore
        t.Add("NODE_IS_VALIDATOR", self.nodeIsValidator) |> ignore
        t.Add("RUN_STANDALONE", self.runStandalone) |> ignore
        t.Add("PREFERRED_PEERS", self.preferredPeers) |> ignore
        t.Add("CATCHUP_COMPLETE", self.catchupMode = CatchupComplete) |> ignore
        t.Add("CATCHUP_RECENT",
              match self.catchupMode with
                | CatchupComplete -> 0
                | CatchupRecent n -> n) |> ignore
        t.Add("AUTOMATIC_MAINTENANCE_PERIOD", self.automaticMaintenancePeriod) |> ignore
        t.Add("AUTOMATIC_MAINTENANCE_COUNT", self.automaticMaintenanceCount) |> ignore
        t.Add("ARTIFICIALLY_ACCELERATE_TIME_FOR_TESTING", self.accelerateTime) |> ignore
        t.Add("ARTIFICIALLY_GENERATE_LOAD_FOR_TESTING", self.generateLoad) |> ignore
        t.Add("MANUAL_CLOSE", self.manualClose) |> ignore
        t.Add("INVARIANT_CHECKS", self.invariantChecks) |> ignore
        t.Add("UNSAFE_QUORUM", self.unsafeQuorum) |> ignore
        t.Add("FAILURE_SAFETY", self.failureSafety) |> ignore
        t.Add("QUORUM_SET", Map.ofSeq [| ("VALIDATORS", qset) |]) |> ignore
        let localTab = t.Add("HISTORY", Toml.Create(), TomlObjectFactory.RequireTomlObject()).Added
        localTab.Add(CfgVal.localHistName, hist) |> ignore
        for historyNode in self.historyNodes do
            localTab.Add(historyNode.Key, remoteHist historyNode.Value) |> ignore
        for historyGetCommand in self.historyGetCommands do
            localTab.Add(historyGetCommand.Key, getHist historyGetCommand.Value) |> ignore
        t

    override self.ToString() : string = self.ToTOML().ToString()

// Extension to the NetworkCfg type to make StellarCoreCfg objects
// for each of its peers. This just creates a default; if you want
// to modify a field, use a copy-and-update record expression like
// {cfg with accelerateTime = true}
type NetworkCfg with
    member self.GetNameKeyList (coreSet: CoreSet) =
        let map = Array.mapi (fun i k -> (CfgVal.peerShortName coreSet i, k))
        map coreSet.keys

    member self.GetNameKeyList (coreSetNames: string list) =
        List.map (self.Find >> self.GetNameKeyList) coreSetNames |> Array.concat

    member self.GetNameKeyListAll() =
        List.map self.GetNameKeyList self.coreSetList |> Array.concat

    member self.DnsNamesWithKey(coreSet: CoreSet) =
        let map = Array.mapi (fun i k -> (CfgVal.peerShortName coreSet i, CfgVal.peerDNSName self.networkNonce coreSet i))
        map coreSet.keys

    member self.DnsNamesWithKey(csl) =
        List.map (self.Find >> self.DnsNamesWithKey) csl |> Array.concat

    member self.DnsNamesWithKey() =
        List.map self.DnsNamesWithKey self.coreSetList |> Array.concat

    member self.DnsNames(coreSet: CoreSet) =
        let map = Array.mapi (fun i k -> (CfgVal.peerDNSName self.networkNonce coreSet i))
        map coreSet.keys

    member self.DnsNames(csl) =
        List.map (self.Find >> self.DnsNames) csl |> Array.concat

    member self.DnsNames() =
        List.map self.DnsNames self.coreSetList |> Array.concat

    member self.QuorumSet(o: CoreSetOptions) =
        let fromQuorumSet = 
            match o.quorumSet with
            | None -> self.GetNameKeyListAll()
            | Some(x) -> self.GetNameKeyList(x)

        List.concat [fromQuorumSet |> List.ofArray; o.quorumSetKeys |> Map.toList] |> Map.ofList
 
    member self.HistoryNodes(o: CoreSetOptions) =
        match o.historyNodes, o.quorumSet with
        | None, None -> self.DnsNamesWithKey() |> Map.ofArray
        | None, Some(x) -> self.DnsNamesWithKey(x) |> Map.ofArray
        | Some(x), _ -> self.DnsNamesWithKey(x) |> Map.ofArray

    member self.PreferredPeers(o: CoreSetOptions) =
        match o.peers with
        | None -> List.concat [self.DnsNames() |> List.ofArray; o.peersDns ]
        | Some(x) -> List.concat [self.DnsNames(x) |> List.ofArray; o.peersDns ]                

    member self.StellarCoreCfg(c, i) : StellarCoreCfg =
        { network = self
          database = SQLite3File CfgVal.databasePath
          networkPassphrase = self.networkPassphrase
          nodeSeed = c.keys.[i]
          nodeIsValidator = c.options.validate
          runStandalone = false
          preferredPeers = self.PreferredPeers c.options
          catchupMode = c.options.catchupMode
          automaticMaintenancePeriod = 30
          automaticMaintenanceCount = 10000
          accelerateTime = false
          generateLoad = true
          manualClose = false
          invariantChecks = [".*"]
          unsafeQuorum = true
          failureSafety = (-1)
          quorumSet = self.QuorumSet c.options
          historyNodes = self.HistoryNodes c.options
          historyGetCommands = c.options.historyGetCommands }
