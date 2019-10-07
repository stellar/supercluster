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
    let cfgVolumeName = "cfg-volume"
    let cfgVolumePath = "/cfg"
    let dataVolumeName = "data-volume"
    let dataVolumePath = "/data"
    let peristentVolumeName ns name = sprintf "%s-pv-%s" ns name
    let peristentVolumeClaimName ns name = sprintf "pvc-%s" name
    let databasePath = dataVolumePath + "/stellar.db"
    let historyPath = dataVolumePath + "/history"
    let bucketsPath = dataVolumePath + "/buckets"
    let localHistName = "local"
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

        let debugLevelCommands =
            List.map
                (sprintf "ll?level=debug&partition=%s")
                self.network.logLevels.LogDebugPartitions

        let traceLevelCommands =
            List.map
                (sprintf "ll?level=trace&partition=%s")
                self.network.logLevels.LogTracePartitions

        let logLevelCommands = List.append debugLevelCommands traceLevelCommands

        t.Add("DATABASE", self.database.ToString()) |> ignore
        t.Add("HTTP_PORT", int64(CfgVal.httpPort)) |> ignore
        t.Add("PUBLIC_HTTP_PORT", true) |> ignore
        t.Add("BUCKET_DIR_PATH", CfgVal.bucketsPath) |> ignore
        t.Add("NETWORK_PASSPHRASE", self.networkPassphrase.ToString()) |> ignore
        t.Add("NODE_SEED", self.nodeSeed.SecretSeed) |> ignore
        t.Add("NODE_IS_VALIDATOR", self.nodeIsValidator) |> ignore
        t.Add("RUN_STANDALONE", self.runStandalone) |> ignore
        t.Add("PREFERRED_PEERS", self.preferredPeers) |> ignore
        t.Add("COMMANDS", logLevelCommands) |> ignore
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
        //t.Add("INVARIANT_CHECKS", self.invariantChecks) |> ignore
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

    // Returns a map containing all (shortname, keypair) pairs of a given CoreSet.
    member self.GetNameKeyList (coreSet: CoreSet) : (string * KeyPair) array =
        let map = Array.mapi (fun i k -> (self.PeerShortName coreSet i, k))
        map coreSet.keys

    // Returns a map containing the union of all (shortname, keypair) pairs of all
    // named CoreSets listed in the `coreSetNames` argument.
    member self.GetNameKeyList (coreSetNames: string list) : (string * KeyPair) array =
        List.map (self.FindCoreSet >> self.GetNameKeyList) coreSetNames |> Array.concat

    // Returns an array of all (shortname, keypair) pairs of all CoreSets.
    member self.GetNameKeyListAll() : (string * KeyPair) array =
        List.map self.GetNameKeyList self.CoreSetList |> Array.concat

    // Returns an array of all (shortname, dnsname) pairs of a given CoreSet.
    member self.DnsNamesWithKey(coreSet: CoreSet) : (string * string) array =
        let map = Array.mapi (fun i k -> (self.PeerShortName coreSet i, self.PeerDNSName coreSet i))
        map coreSet.keys

    // Returns an array of all (shortname, dnsname) pairs of all named CoreSets
    // listed in the `coreSetNames` argument.
    member self.DnsNamesWithKey(coreSetNames:string list) : (string * string) array =
        List.map (self.FindCoreSet >> self.DnsNamesWithKey) coreSetNames |> Array.concat

    // Returns an array of all (shortname, dnsname) pairs of all CoreSets.
    member self.DnsNamesWithKey() : (string * string) array =
        List.map self.DnsNamesWithKey self.CoreSetList |> Array.concat

    // Returns an array of dnsnames of a given CoreSet.
    member self.DnsNames(coreSet: CoreSet) : string array =
        let map = Array.mapi (fun i k -> (self.PeerDNSName coreSet i))
        map coreSet.keys

    // Returns an array of dnsnames of all named Coresets listed in the
    // `coreSetNames` argument.
    member self.DnsNames(coreSetNames:string list) : string array =
        List.map (self.FindCoreSet >> self.DnsNames) coreSetNames |> Array.concat

    // Returns an array of dnsnames of all Coresets.
    member self.DnsNames() : string array =
        List.map self.DnsNames self.CoreSetList |> Array.concat

    member self.QuorumSet(o: CoreSetOptions) : Map<string, KeyPair> =
        let fromQuorumSet =
            match o.quorumSet with
            | None -> self.GetNameKeyListAll()
            | Some(x) -> self.GetNameKeyList(x)

        List.concat [fromQuorumSet |> List.ofArray; o.quorumSetKeys |> Map.toList] |> Map.ofList

    member self.HistoryNodes(o: CoreSetOptions) : Map<string, string> =
        match o.historyNodes, o.quorumSet with
        | None, None -> self.DnsNamesWithKey() |> Map.ofArray
        | None, Some(x) -> self.DnsNamesWithKey(x) |> Map.ofArray
        | Some(x), _ -> self.DnsNamesWithKey(x) |> Map.ofArray

    member self.PreferredPeers(o: CoreSetOptions) =
        match o.peers with
        | None -> List.concat [self.DnsNames() |> List.ofArray; o.peersDns ]
        | Some(x) -> List.concat [self.DnsNames(x) |> List.ofArray; o.peersDns ]

    member self.StellarCoreCfg(c:CoreSet, i:int) : StellarCoreCfg =
        { network = self
          database = SQLite3File CfgVal.databasePath
          networkPassphrase = self.networkPassphrase
          nodeSeed = c.keys.[i]
          nodeIsValidator = c.options.validate
          runStandalone = false
          preferredPeers = self.PreferredPeers c.options
          catchupMode = c.options.catchupMode
          automaticMaintenancePeriod = 0
          automaticMaintenanceCount = 0
          accelerateTime = c.options.accelerateTime
          generateLoad = true
          manualClose = false
          invariantChecks = [".*"]
          unsafeQuorum = c.options.unsafeQuorum
          failureSafety = (-1)
          quorumSet = self.QuorumSet c.options
          historyNodes = self.HistoryNodes c.options
          historyGetCommands = c.options.historyGetCommands }
