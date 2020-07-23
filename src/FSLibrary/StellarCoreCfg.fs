// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreCfg

open stellar_dotnet_sdk
open Nett

open System.Text.RegularExpressions
open StellarCoreSet
open StellarNetworkCfg
open StellarShellCmd

// Submodule of short fixed (or lightly parametrized) config values: names,
// paths, labels, etc.
module CfgVal =
    let httpPort = 11626
    let prometheusExporterPort = 9473
    let labels = Map.ofSeq [ "app", "stellar-core" ]
    let labelSelector = "app = stellar-core"
    let stellarCoreBinPath = "stellar-core"
    let stellarCoreContainerName (cmd:string) = "stellar-core-" + cmd
    let dataVolumeName = "data-volume"
    let dataVolumePath = "/data"
    let peristentVolumeClaimName peerOrJobName = sprintf "pvc-%s" peerOrJobName
    let databasePath = dataVolumePath + "/stellar.db"
    let historyPath = dataVolumePath + "/history"
    let bucketsDir = "buckets"
    let bucketsPath = dataVolumePath + "/" + bucketsDir
    let localHistName = "local"
    let peerNameEnvVarName = "STELLAR_CORE_PEER_SHORT_NAME"
    let peerCfgFileName = "stellar-core.cfg"
    let peerNameEnvCfgFileWord: ShWord =
        ShWord.ShPieces [| ShBare ("/cfg-");
                           ShVar (ShName peerNameEnvVarName);
                           ShBare ("/" + peerCfgFileName) |]
    let cfgVolumeName peerOrJobName = sprintf "cfg-%s" peerOrJobName
    let cfgVolumePath peerOrJobName = "/" + (cfgVolumeName peerOrJobName)
    let jobCfgVolumeName = "cfg-job"
    let jobCfgVolumePath = "/" + jobCfgVolumeName
    let jobCfgFileName = "stellar-core.cfg"
    let jobCfgFilePath = jobCfgVolumePath + "/" + jobCfgFileName

    let historyCfgVolumeName = "cfg-history"
    let historyCfgVolumePath = "/" + historyCfgVolumeName
    let historyCfgFileName = "nginx.conf"
    let historyCfgFilePath = historyCfgVolumePath + "/" + historyCfgFileName

    // pg configs
    let pgDb = "postgres"
    let pgUser = "postgres"
    let pgPassword = "password"
    let pgHost = "localhost"

    // We very crudely overload the /data/history directory as a general way
    // to transfer files from one peer to another over HTTP via nginx + curl
    let databaseBackupPath = historyPath + "/stellar-backup.db"
    let bucketsBackupPath = historyPath + "/buckets.tar.gz"
    let databaseBackupURL (host:PeerDnsName) = "http://" + host.StringName + "/stellar-backup.db"
    let bucketsBackupURL (host:PeerDnsName) = "http://" + host.StringName + "/buckets.tar.gz"
    let bucketsDownloadPath = dataVolumePath + "/buckets.tar.gz"

// This is the formula stellar-core uses to calculate a threshold from
// a percent.
let thresholdOfPercent (sz:int) (pct:int) : int =
    1 + (((sz * pct) - 1) / 100)

// And this is (hopefully) its inverse!
let percentOfThreshold (sz:int) (thr:int) : int =
    1 + ((100 * (thr - 1)) / sz)

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

let curlGetCmd (uri:System.Uri) : string =
    sprintf "curl -sf %s{0} -o {1}" (uri.ToString())

let curlGetCmdFromPeer (peer:PeerDnsName) : string =
    curlGetCmd (System.UriBuilder("http", peer.StringName).Uri)

// Represents the contents of a stellar-core.cfg file, along with method to
// write it out to TOML.
type StellarCoreCfg =
    { network : NetworkCfg
      database : DatabaseURL
      networkPassphrase : NetworkPassphrase
      nodeSeed : KeyPair
      nodeIsValidator : bool
      runStandalone : bool
      preferredPeers : PeerDnsName list
      catchupMode: CatchupMode
      automaticMaintenancePeriod: int
      automaticMaintenanceCount: int
      accelerateTime: bool
      generateLoad: bool
      manualClose: bool
      invariantChecks: string list
      unsafeQuorum : bool
      failureSafety : int
      quorumSet : QuorumSet
      historyNodes : Map<PeerShortName, PeerDnsName>
      historyGetCommands : Map<PeerShortName, string>
      localHistory: bool
      maxSlotsToRemember: int
      maxBatchReadCount: int
      maxBatchWriteCount: int}

    member self.ToTOML() : TomlTable =
        let t = Toml.Create()

        let hist =
            Map.ofSeq [| ("get", sprintf "cp %s/{0} {1}" CfgVal.historyPath)
                         ("put", sprintf "cp {0} %s/{1}" CfgVal.historyPath)
                         ("mkdir", sprintf "mkdir -p %s/{0}" CfgVal.historyPath) |]
        let remoteHist (dnsName:PeerDnsName) =
            Map.ofSeq [| ("get", curlGetCmdFromPeer dnsName) |]
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
        let preferredPeers = List.map (fun (x:PeerDnsName) -> x.StringName) self.preferredPeers

        t.Add("DATABASE", self.database.ToString()) |> ignore
        t.Add("HTTP_PORT", int64(CfgVal.httpPort)) |> ignore
        t.Add("PUBLIC_HTTP_PORT", true) |> ignore
        t.Add("BUCKET_DIR_PATH", CfgVal.bucketsPath) |> ignore
        t.Add("NETWORK_PASSPHRASE", self.networkPassphrase.ToString()) |> ignore
        t.Add("NODE_SEED", self.nodeSeed.SecretSeed) |> ignore
        t.Add("NODE_IS_VALIDATOR", self.nodeIsValidator) |> ignore
        t.Add("RUN_STANDALONE", self.runStandalone) |> ignore
        t.Add("PREFERRED_PEERS", preferredPeers) |> ignore
        t.Add("COMMANDS", logLevelCommands) |> ignore
        t.Add("CATCHUP_COMPLETE", self.catchupMode = CatchupComplete) |> ignore
        t.Add("CATCHUP_RECENT",
              match self.catchupMode with
                | CatchupComplete -> 0
                | CatchupRecent n -> n) |> ignore
        t.Add("MAX_SLOTS_TO_REMEMBER", self.maxSlotsToRemember) |> ignore
        t.Add("MAX_BATCH_READ_COUNT", self.maxBatchReadCount) |> ignore
        t.Add("MAX_BATCH_WRITE_COUNT", self.maxBatchWriteCount) |> ignore
        t.Add("AUTOMATIC_MAINTENANCE_PERIOD", self.automaticMaintenancePeriod) |> ignore
        t.Add("AUTOMATIC_MAINTENANCE_COUNT", self.automaticMaintenanceCount) |> ignore
        t.Add("ARTIFICIALLY_ACCELERATE_TIME_FOR_TESTING", self.accelerateTime) |> ignore
        t.Add("ARTIFICIALLY_GENERATE_LOAD_FOR_TESTING", self.generateLoad) |> ignore

        let n = self.preferredPeers.Length
        t.Add("TARGET_PEER_CONNECTIONS", 16) |> ignore
        t.Add("MAX_ADDITIONAL_PEER_CONNECTIONS", min n 128) |> ignore

        t.Add("QUORUM_INTERSECTION_CHECKER", false) |> ignore
        t.Add("MANUAL_CLOSE", self.manualClose) |> ignore
        t.Add("INVARIANT_CHECKS", self.invariantChecks) |> ignore
        t.Add("UNSAFE_QUORUM", self.unsafeQuorum) |> ignore
        t.Add("FAILURE_SAFETY", self.failureSafety) |> ignore

        // Add tables (and subtables, recursively) for qsets.
        let rec addQsetAt (label:string) (qs:QuorumSet) =
            let validators : string array =
                Map.toArray qs.validators
                    |> Array.map (fun ((n:PeerShortName), (k:KeyPair)) ->
                                     sprintf "%s %s" k.Address n.StringName)
            let innerTab = t.Add(label, Toml.Create(), TomlObjectFactory.RequireTomlObject()).Added
            innerTab.Add("VALIDATORS", validators) |> ignore
            match qs.thresholdPercent with
            | None -> ()
            | Some(pct) -> innerTab.Add("THRESHOLD_PERCENT", pct) |> ignore
            Array.iteri
                (fun (i:int) (qs:QuorumSet) -> addQsetAt (sprintf "%s.sub%d" label i) qs)
                qs.innerQuorumSets
        addQsetAt "QUORUM_SET" self.quorumSet

        let localTab = t.Add("HISTORY", Toml.Create(), TomlObjectFactory.RequireTomlObject()).Added
        if self.localHistory
        then localTab.Add(CfgVal.localHistName, hist) |> ignore
        for historyNode in self.historyNodes do
            localTab.Add(historyNode.Key.StringName, remoteHist historyNode.Value) |> ignore
        for historyGetCommand in self.historyGetCommands do
            localTab.Add(historyGetCommand.Key.StringName, getHist historyGetCommand.Value) |> ignore
        t

    override self.ToString() : string =
        // Unfortunately Nett mis-quotes dotted keys -- it'll write out keys
        // like ['QUORUM_SET.sub1'] which should be ['QUORUM_SET'.'sub1'] or
        // just [QUORUM_SET.sub1] -- so we manually unquote these here. Sigh.
        let nettStr = self.ToTOML().ToString()
        Regex.Replace(nettStr, @"^\['([a-zA-Z0-9_\-\.]+)'\]$", "[$1]", RegexOptions.Multiline)

// Extension to the NetworkCfg type to make StellarCoreCfg objects
// for each of its peers. This just creates a default; if you want
// to modify a field, use a copy-and-update record expression like
// {cfg with accelerateTime = true}
type NetworkCfg with

    // Returns a map containing all (shortname, keypair) pairs of a given CoreSet.
    member self.GetNameKeyList (coreSet: CoreSet) : (PeerShortName * KeyPair) array =
        let map = Array.mapi (fun i k -> (self.PeerShortName coreSet i, k))
        map coreSet.keys

    // Returns a map containing the union of all (shortname, keypair) pairs of all
    // named CoreSets listed in the `coreSetNames` argument.
    member self.GetNameKeyList (coreSetNames: CoreSetName list) : (PeerShortName * KeyPair) array =
        List.map (self.FindCoreSet >> self.GetNameKeyList) coreSetNames |> Array.concat

    // Returns an array of all (shortname, keypair) pairs of all CoreSets.
    member self.GetNameKeyListAll() : (PeerShortName * KeyPair) array =
        List.map self.GetNameKeyList self.CoreSetList |> Array.concat

    // Returns an array of all (shortname, dnsname) pairs of a given CoreSet.
    member self.DnsNamesWithKey(coreSet: CoreSet) : (PeerShortName * PeerDnsName) array =
        let map = Array.mapi (fun i k -> (self.PeerShortName coreSet i, self.PeerDnsName coreSet i))
        map coreSet.keys

    // Returns an array of all (shortname, dnsname) pairs of all named CoreSets
    // listed in the `coreSetNames` argument.
    member self.DnsNamesWithKey(coreSetNames:CoreSetName list) : (PeerShortName * PeerDnsName) array =
        List.map (self.FindCoreSet >> self.DnsNamesWithKey) coreSetNames |> Array.concat

    // Returns an array of all (shortname, dnsname) pairs of all CoreSets.
    member self.DnsNamesWithKey() : (PeerShortName * PeerDnsName) array =
        List.map self.DnsNamesWithKey self.CoreSetList |> Array.concat

    // Returns an array of dnsnames of a given CoreSet.
    member self.DnsNames(coreSet: CoreSet) : PeerDnsName array =
        let map = Array.mapi (fun i k -> (self.PeerDnsName coreSet i))
        map coreSet.keys

    // Returns an array of dnsnames of all named Coresets listed in the
    // `coreSetNames` argument.
    member self.DnsNames(coreSetNames:CoreSetName list) : PeerDnsName array =
        List.map (self.FindCoreSet >> self.DnsNames) coreSetNames |> Array.concat

    // Returns an array of dnsnames of all Coresets.
    member self.DnsNames() : PeerDnsName array =
        List.map self.DnsNames self.CoreSetList |> Array.concat

    member self.QuorumSet(o: CoreSetOptions) : QuorumSet =
        let ofNameKeyList (nks:(PeerShortName * KeyPair) array) : QuorumSet =
            { thresholdPercent = None
              validators = Map.ofArray nks
              innerQuorumSets = [||] }
        match o.quorumSet with
            | AllPeersQuorum -> ofNameKeyList (self.GetNameKeyListAll())
            | CoreSetQuorum(ns) -> ofNameKeyList (self.GetNameKeyList [ns])
            | ExplicitQuorum(e) -> e

    member self.HistoryNodes(o: CoreSetOptions) : Map<PeerShortName, PeerDnsName> =
        match o.historyNodes, o.quorumSet with
        | None, CoreSetQuorum(x) -> self.DnsNamesWithKey([x]) |> Map.ofArray
        | None, _ -> self.DnsNamesWithKey() |> Map.ofArray
        | Some(x), _ -> self.DnsNamesWithKey(x) |> Map.ofArray

    member self.PreferredPeers(o: CoreSetOptions) =
        match o.peers with
        | None -> List.concat [self.DnsNames() |> List.ofArray; o.peersDns ]
        | Some(x) -> List.concat [self.DnsNames(x) |> List.ofArray; o.peersDns ]


    member self.getDbUrl(o: CoreSetOptions): DatabaseURL =
        match o.dbType with
        Postgres -> PostgreSQL(CfgVal.pgDb, CfgVal.pgUser, CfgVal.pgPassword, CfgVal.pgHost)
        | Sqlite -> SQLite3File CfgVal.databasePath
        | SqliteMemory -> SQLite3Memory

    member self.StellarCoreCfgForJob(opts:CoreSetOptions) : StellarCoreCfg =
        { network = self
          database = self.getDbUrl opts
          networkPassphrase = self.networkPassphrase
          nodeSeed = KeyPair.Random()
          nodeIsValidator = false
          runStandalone = false
          preferredPeers = self.PreferredPeers opts
          catchupMode = opts.catchupMode
          automaticMaintenancePeriod = 10
          automaticMaintenanceCount = 50000
          accelerateTime = opts.accelerateTime
          generateLoad = true
          manualClose = false
          // FIXME: see bug https://github.com/stellar/stellar-core/issues/2304
          // the BucketListIsConsistentWithDatabase invariant blocks too long,
          // so we explicitly disable it here.
          invariantChecks = ["(?!BucketListIsConsistentWithDatabase).*"]
          unsafeQuorum = opts.unsafeQuorum
          failureSafety = 0
          quorumSet = self.QuorumSet opts
          historyNodes = self.HistoryNodes opts
          historyGetCommands = opts.historyGetCommands
          localHistory = opts.localHistory
          maxSlotsToRemember = opts.maxSlotsToRemember
          maxBatchReadCount = opts.maxBatchReadCount
          maxBatchWriteCount = opts.maxBatchWriteCount }

    member self.StellarCoreCfg(c:CoreSet, i:int) : StellarCoreCfg =
        { network = self
          database = self.getDbUrl c.options
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
          // FIXME: see bug https://github.com/stellar/stellar-core/issues/2304
          // the BucketListIsConsistentWithDatabase invariant blocks too long,
          // so we explicitly disable it here.
          invariantChecks = ["(?!BucketListIsConsistentWithDatabase).*"]
          unsafeQuorum = c.options.unsafeQuorum
          failureSafety = 0
          quorumSet = self.QuorumSet c.options
          historyNodes = self.HistoryNodes c.options
          historyGetCommands = c.options.historyGetCommands
          localHistory = c.options.localHistory
          maxSlotsToRemember = c.options.maxSlotsToRemember
          maxBatchReadCount = c.options.maxBatchReadCount
          maxBatchWriteCount = c.options.maxBatchWriteCount }
