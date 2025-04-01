// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreCfg

open FSharp.Data
open Nett
open System.Text.RegularExpressions
open StellarCoreSet
open StellarNetworkCfg
open StellarShellCmd
open StellarDotnetSdk.Accounts

// Submodule of short fixed (or lightly parametrized) config values: names,
// paths, labels, etc.
module CfgVal =
    let httpPort = 11626
    let prometheusExporterPort = 9473
    let labels = Map.ofSeq [ "app", "stellar-core" ]
    let labelSelector = "app = stellar-core"
    let stellarCoreBinPath = "stellar-core"
    let allCoreContainerCmds = [| "new-hist"; "new-db"; "catchup"; "run"; "test" |]

    let stellarCoreContainerName (cmd: string) =
        assert (Array.contains cmd allCoreContainerCmds)
        "stellar-core-" + cmd

    let dataVolumeName = "data-volume"
    let dataVolumePath = "/data"
    let databasePath = dataVolumePath + "/stellar.db"
    let historyPath = dataVolumePath + "/history"
    let bucketsDir = "buckets"
    let bucketsPath = dataVolumePath + "/" + bucketsDir
    let localHistName = "local"
    let peerNameEnvVarName = "STELLAR_CORE_PEER_SHORT_NAME"
    let asanOptionsEnvVarName = "ASAN_OPTIONS"
    let asanOptionsEnvVarValue = "quarantine_size_mb=1:malloc_context_size=5"
    let peerCfgFileName = "stellar-core.cfg"
    let peerInitCfgFileName = "stellar-core-init.cfg"
    let peerDelayCfgFileName = "install-delays.sh"

    let peerNameEnvCfgFileWord : ShWord =
        ShWord.ShPieces [| ShBare("/cfg-")
                           ShVar(ShName peerNameEnvVarName)
                           ShBare("/" + peerCfgFileName) |]

    let peerNameEnvInitCfgFileWord : ShWord =
        ShWord.ShPieces [| ShBare("/cfg-")
                           ShVar(ShName peerNameEnvVarName)
                           ShBare("/" + peerInitCfgFileName) |]

    let peerNameEnvDelayCfgFileWord : ShWord =
        ShWord.ShPieces [| ShBare("/cfg-")
                           ShVar(ShName peerNameEnvVarName)
                           ShBare("/" + peerDelayCfgFileName) |]

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
    let databaseBackupURL (host: PeerDnsName) = "http://" + host.StringName + "/stellar-backup.db"
    let bucketsBackupURL (host: PeerDnsName) = "http://" + host.StringName + "/buckets.tar.gz"
    let bucketsDownloadPath = dataVolumePath + "/buckets.tar.gz"

    let metaStreamPath = "metadata.xdr"
    let maximumLedgerClosetimeDrift = 120

// This is the formula stellar-core uses to calculate a threshold from
// a percent.
let thresholdOfPercent (sz: int) (pct: int) : int = 1 + (((sz * pct) - 1) / 100)

// And this is (hopefully) its inverse!
let percentOfThreshold (sz: int) (thr: int) : int = 1 + ((100 * (thr - 1)) / sz)

// Symbolic type of the different sorts of DATABASE that can show up in a
// stellar-core.cfg. Usually use SQLite3File of some path in a Pod's local
// volume.
type DatabaseURL =
    | SQLite3Memory
    | SQLite3File of path: string
    | PostgreSQL of database: string * user: string * pass: string * host: string
    override self.ToString() : string =
        match self with
        | SQLite3Memory -> "sqlite3://:memory:"
        | SQLite3File s -> sprintf "sqlite3://%s" s
        | PostgreSQL (d, u, p, h) -> sprintf "postgresql://dbname=%s user=%s password=%s host=%s" d u p h

let curlGetCmd (uri: System.Uri) : string = sprintf "curl -sf %s{0} -o {1}" (uri.ToString())

let curlGetCmdFromPeer (peer: PeerDnsName) : string = curlGetCmd (System.UriBuilder("http", peer.StringName).Uri)

type CoreContainerType =
    | InitCoreContainer
    | MainCoreContainer

// This exists to work around a buggy interaction between FSharp.Data and
// dotnet 5.0.300: __SOURCE_DIRECTORY__ is not [<Literal>] anymore, but
// JsonProvider's ResolutionFolder argument needs to be.
[<Literal>]
let cwd = __SOURCE_DIRECTORY__

type Distribution =
    CsvProvider<"csv-type-samples/sample-loadgen-op-count-distribution.csv", HasHeaders=true, ResolutionFolder=cwd>

// Add a loadgen distribution to a TOML table.
let distributionToToml (d: (int * int) list) (name: string) (t: TomlTable) : unit =
    match d with
    | [] -> ()
    | _ ->
        let values = List.map fst d
        let weights = List.map snd d

        t.Add("LOADGEN_" + name + "_FOR_TESTING", values) |> ignore
        t.Add("LOADGEN_" + name + "_DISTRIBUTION_FOR_TESTING", weights) |> ignore

// Represents the contents of a stellar-core.cfg file, along with method to
// write it out to TOML.
type StellarCoreCfg =
    { network: NetworkCfg
      database: DatabaseURL
      networkPassphrase: NetworkPassphrase
      nodeSeed: KeyPair
      nodeIsValidator: bool
      runStandalone: bool
      image: string
      preferredPeers: PeerDnsName list
      targetPeerConnections: int
      preferredPeersOnly: bool
      catchupMode: CatchupMode
      automaticMaintenancePeriod: int
      automaticMaintenanceCount: int
      accelerateTime: bool
      generateLoad: bool
      updateSorobanCosts: bool option
      manualClose: bool
      invariantChecks: InvariantChecksSpec
      unsafeQuorum: bool
      failureSafety: int
      quorumSet: QuorumSet
      historyNodes: Map<PeerShortName, PeerDnsName>
      historyGetCommands: Map<PeerShortName, string>
      localHistory: bool
      maxSlotsToRemember: int
      maxBatchWriteCount: int
      inMemoryMode: bool
      addArtificialDelayUsec: int option // optional delay for testing in microseconds
      deprecatedSQLState: bool
      surveyPhaseDuration: int option
      containerType: CoreContainerType }

    member self.ToTOML() : TomlTable =
        let t = Toml.Create()

        let defaultHist =
            Map.ofSeq [| ("get", sprintf "cp %s/{0} {1}" CfgVal.historyPath)
                         ("put", sprintf "cp {0} %s/{1}" CfgVal.historyPath)
                         ("mkdir", sprintf "mkdir -p %s/{0}" CfgVal.historyPath) |]

        let histGetOnly = defaultHist |> Map.filter (fun key _ -> key = "get")

        let remoteHist (dnsName: PeerDnsName) = Map.ofSeq [| ("get", curlGetCmdFromPeer dnsName) |]
        let getHist getCommand = Map.ofSeq [| ("get", getCommand) |]

        let debugLevelCommands =
            List.map (sprintf "ll?level=debug&partition=%s") self.network.missionContext.logLevels.LogDebugPartitions

        let traceLevelCommands =
            List.map (sprintf "ll?level=trace&partition=%s") self.network.missionContext.logLevels.LogTracePartitions

        let logLevelCommands = List.append debugLevelCommands traceLevelCommands
        let preferredPeers = List.map (fun (x: PeerDnsName) -> x.StringName) self.preferredPeers

        t.Add("DATABASE", self.database.ToString()) |> ignore
        t.Add("DEPRECATED_SQL_LEDGER_STATE", self.deprecatedSQLState) |> ignore
        t.Add("METADATA_DEBUG_LEDGERS", 0) |> ignore
        t.Add("MAX_OUTBOUND_QUEUE_SIZE", 10000) |> ignore
        t.Add("EXPERIMENTAL_PARALLEL_LEDGER_APPLY", true) |> ignore

        match self.network.missionContext.genesisTestAccountCount with
        | Some count -> t.Add("GENESIS_TEST_ACCOUNT_COUNT", count) |> ignore
        | None -> ()

        match self.containerType with
        // REVERTME: temporarily use same nonzero port for both container types.
        | _ -> t.Add("HTTP_PORT", int64 (CfgVal.httpPort)) |> ignore

        t.Add("PUBLIC_HTTP_PORT", true) |> ignore
        t.Add("BUCKET_DIR_PATH", CfgVal.bucketsPath) |> ignore
        t.Add("NETWORK_PASSPHRASE", self.networkPassphrase.ToString()) |> ignore
        t.Add("NODE_SEED", self.nodeSeed.SecretSeed) |> ignore
        t.Add("NODE_IS_VALIDATOR", self.nodeIsValidator) |> ignore
        t.Add("RUN_STANDALONE", self.runStandalone) |> ignore
        t.Add("PREFERRED_PEERS", preferredPeers) |> ignore
        t.Add("PREFERRED_PEERS_ONLY", self.preferredPeersOnly) |> ignore
        t.Add("COMMANDS", logLevelCommands) |> ignore
        t.Add("CATCHUP_COMPLETE", self.catchupMode = CatchupComplete) |> ignore

        if self.network.missionContext.enableBackggroundOverlay then
            t.Add("EXPERIMENTAL_BACKGROUND_OVERLAY_PROCESSING", true) |> ignore

        match self.network.missionContext.peerReadingCapacity, self.network.missionContext.peerFloodCapacity with
        | None, None -> ()
        | Some read, Some flood ->
            t.Add("PEER_FLOOD_READING_CAPACITY", flood) |> ignore
            t.Add("PEER_READING_CAPACITY", read) |> ignore
        | _, _ ->
            raise (
                System.ArgumentException
                    "PEER_FLOOD_READING_CAPACITY and PEER_READING_CAPACITY must be defined together"
            )

        let maybeAddGlobalDelay () =
            match self.network.missionContext.sleepMainThread with
            | None -> ()
            | Some sleep -> t.Add("ARTIFICIALLY_SLEEP_MAIN_THREAD_FOR_TESTING", sleep) |> ignore

        match self.addArtificialDelayUsec with
        | None -> maybeAddGlobalDelay ()
        | Some sleep -> t.Add("ARTIFICIALLY_SLEEP_MAIN_THREAD_FOR_TESTING", sleep) |> ignore

        match self.network.missionContext.flowControlSendMoreBatchSize with
        | None -> ()
        | Some batchSize -> t.Add("FLOW_CONTROL_SEND_MORE_BATCH_SIZE", batchSize) |> ignore

        t.Add("MAXIMUM_LEDGER_CLOSETIME_DRIFT", CfgVal.maximumLedgerClosetimeDrift)
        |> ignore

        t.Add(
            "CATCHUP_RECENT",
            match self.catchupMode with
            | CatchupComplete -> 0
            | CatchupRecent n -> n
        )
        |> ignore

        t.Add("MAX_SLOTS_TO_REMEMBER", self.maxSlotsToRemember) |> ignore
        t.Add("MAX_BATCH_WRITE_COUNT", self.maxBatchWriteCount) |> ignore
        t.Add("AUTOMATIC_MAINTENANCE_PERIOD", self.automaticMaintenancePeriod) |> ignore
        t.Add("AUTOMATIC_MAINTENANCE_COUNT", self.automaticMaintenanceCount) |> ignore
        t.Add("ARTIFICIALLY_ACCELERATE_TIME_FOR_TESTING", self.accelerateTime) |> ignore
        t.Add("ARTIFICIALLY_GENERATE_LOAD_FOR_TESTING", self.generateLoad) |> ignore

        if self.updateSorobanCosts.IsSome then
            t.Add("UPDATE_SOROBAN_COSTS_DURING_PROTOCOL_UPGRADE_FOR_TESTING", self.updateSorobanCosts.Value)
            |> ignore

        if self.network.missionContext.peerFloodCapacityBytes.IsSome then
            t.Add("PEER_FLOOD_READING_CAPACITY_BYTES", self.network.missionContext.peerFloodCapacityBytes.Value)
            |> ignore

        if self.network.missionContext.flowControlSendMoreBatchSizeBytes.IsSome then
            t.Add(
                "FLOW_CONTROL_SEND_MORE_BATCH_SIZE_BYTES",
                self.network.missionContext.flowControlSendMoreBatchSizeBytes.Value
            )
            |> ignore

        if self.network.missionContext.outboundByteLimit.IsSome then
            t.Add("OUTBOUND_TX_QUEUE_BYTE_LIMIT", self.network.missionContext.outboundByteLimit.Value)
            |> ignore

        if self.inMemoryMode then
            t.Add("METADATA_OUTPUT_STREAM", CfgVal.metaStreamPath) |> ignore

        match self.network.missionContext.simulateApplyWeight, self.network.missionContext.simulateApplyDuration with
        | None, None -> ()
        | Some weight, Some duration ->
            t.Add("OP_APPLY_SLEEP_TIME_DURATION_FOR_TESTING", duration) |> ignore
            t.Add("OP_APPLY_SLEEP_TIME_WEIGHT_FOR_TESTING", weight) |> ignore
        | _, _ ->
            raise (
                System.ArgumentException "simulate-apply-weight and simulate-apply-duration must be defined together"
            )

        if self.network.missionContext.opCountDistribution.IsSome then
            // Read the content of the file specified by `opCountDistribution`
            // and convert that into the config options.
            let distribution =
                seq {
                    for row in Distribution.Load(self.network.missionContext.opCountDistribution.Value).Rows ->
                        row.OpCount, row.Frequency
                }

            t.Add("LOADGEN_OP_COUNT_FOR_TESTING", distribution |> Seq.map fst) |> ignore

            t.Add("LOADGEN_OP_COUNT_DISTRIBUTION_FOR_TESTING", distribution |> Seq.map snd)
            |> ignore

        distributionToToml self.network.missionContext.wasmBytesDistribution "WASM_BYTES" t
        distributionToToml self.network.missionContext.dataEntriesDistribution "NUM_DATA_ENTRIES" t
        distributionToToml self.network.missionContext.totalKiloBytesDistribution "IO_KILOBYTES" t
        distributionToToml self.network.missionContext.txSizeBytesDistribution "TX_SIZE_BYTES" t
        distributionToToml self.network.missionContext.instructionsDistribution "INSTRUCTIONS" t

        t.Add("TARGET_PEER_CONNECTIONS", self.targetPeerConnections) |> ignore

        t.Add("MAX_ADDITIONAL_PEER_CONNECTIONS", self.targetPeerConnections * 3)
        |> ignore

        t.Add("QUORUM_INTERSECTION_CHECKER", false) |> ignore
        t.Add("MANUAL_CLOSE", self.manualClose) |> ignore

        let invList =
            match self.invariantChecks with
            | AllInvariants -> [ ".*" ]
            | AllInvariantsExceptBucketConsistencyChecks -> [ "(?!BucketListIsConsistentWithDatabase).*" ]
            | NoInvariants -> []

        t.Add("INVARIANT_CHECKS", invList) |> ignore
        t.Add("UNSAFE_QUORUM", self.unsafeQuorum) |> ignore
        t.Add("FAILURE_SAFETY", self.failureSafety) |> ignore

        // Only add TX_BATCH_MAX_SIZE to config if explicitly set in command line
        match self.network.missionContext.txBatchMaxSize with
        | Some batchSize -> t.Add("TX_BATCH_MAX_SIZE", batchSize) |> ignore
        | None -> ()

        // 1s to pull a tx
        t.Add("FLOOD_DEMAND_BACKOFF_DELAY_MS", 1000) |> ignore
        t.Add("BUCKETLIST_DB_INDEX_PAGE_SIZE_EXPONENT", 0) |> ignore
        t.Add("BUCKETLIST_DB_PERSIST_INDEX", false) |> ignore

        match self.surveyPhaseDuration with
        | None -> ()
        | Some duration -> t.Add("ARTIFICIALLY_SET_SURVEY_PHASE_DURATION_FOR_TESTING", duration) |> ignore

        // Add tables (and subtables, recursively) for qsets.
        let rec addQsetAt (label: string) (qs: QuorumSet) =
            let validators : string array =
                Map.toArray qs.validators
                |> Array.map (fun (n: PeerShortName, k: KeyPair) -> sprintf "%s %s" k.Address n.StringName)

            let innerTab = t.Add(label, Toml.Create(), TomlObjectFactory.RequireTomlObject()).Added
            innerTab.Add("VALIDATORS", validators) |> ignore

            match qs.thresholdPercent with
            | None -> ()
            | Some (pct) -> innerTab.Add("THRESHOLD_PERCENT", pct) |> ignore

            Array.iteri (fun (i: int) (qs: QuorumSet) -> addQsetAt (sprintf "%s.sub%d" label i) qs) qs.innerQuorumSets

        addQsetAt "QUORUM_SET" self.quorumSet

        let localTab = t.Add("HISTORY", Toml.Create(), TomlObjectFactory.RequireTomlObject()).Added
        // When simulateApplyWeight = Some _, stellar-core sets MODE_STORES_HISTORY
        // which is used for simulations that only test consensus.
        // In such cases, we should not pass put and mkdir commands.
        if self.localHistory then
            localTab.Add(
                CfgVal.localHistName,
                if self.network.missionContext.simulateApplyWeight.IsSome then
                    histGetOnly
                else
                    defaultHist
            )
            |> ignore

        for historyNode in self.historyNodes do
            localTab.Add(historyNode.Key.StringName, remoteHist historyNode.Value) |> ignore

        for historyGetCommand in self.historyGetCommands do
            localTab.Add(historyGetCommand.Key.StringName, getHist historyGetCommand.Value)
            |> ignore

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
    member self.GetNameKeyList(coreSet: CoreSet) : (PeerShortName * KeyPair) array =
        let map = Array.mapi (fun i k -> (self.PeerShortName coreSet i, k))
        map coreSet.keys

    // Returns a map containing the union of all (shortname, keypair) pairs of all
    // named CoreSets listed in the `coreSetNames` argument.
    member self.GetNameKeyList(coreSetNames: CoreSetName list) : (PeerShortName * KeyPair) array =
        List.map (self.FindCoreSet >> self.GetNameKeyList) coreSetNames |> Array.concat

    // Returns an array of all (shortname, keypair) pairs of all CoreSets.
    member self.GetNameKeyListAll() : (PeerShortName * KeyPair) array =
        List.map self.GetNameKeyList self.CoreSetList |> Array.concat

    // Returns an array of all (shortname, dnsname) pairs of a given CoreSet.
    member self.DnsNamesWithKey(coreSet: CoreSet) : (PeerShortName * PeerDnsName) array =
        let map =
            Array.mapi (fun i k -> (self.PeerShortName coreSet i, self.PeerDnsName coreSet i))

        map coreSet.keys

    // Returns an array of all (shortname, dnsname) pairs of all named CoreSets
    // listed in the `coreSetNames` argument.
    member self.DnsNamesWithKey(coreSetNames: CoreSetName list) : (PeerShortName * PeerDnsName) array =
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
    member self.DnsNames(coreSetNames: CoreSetName list) : PeerDnsName array =
        List.map (self.FindCoreSet >> self.DnsNames) coreSetNames |> Array.concat

    // Returns an array of dnsnames of all Coresets.
    member self.DnsNames() : PeerDnsName array = List.map self.DnsNames self.CoreSetList |> Array.concat

    member self.pubKeyToPeerDnsNameMap : Map<byte [], PeerDnsName> =
        let processCoreSet (coreSet: CoreSet) : (byte [] * PeerDnsName) list =
            coreSet.keys
            |> Array.mapi (fun i (k: KeyPair) -> (k.PublicKey, self.PeerDnsName coreSet i))
            |> List.ofArray

        self.CoreSetList |> List.map processCoreSet |> List.concat |> Map.ofList

    member self.QuorumSet(o: CoreSetOptions) : QuorumSet =
        let ofNameKeyList (nks: (PeerShortName * KeyPair) array) (threshold: int option) : QuorumSet =
            { thresholdPercent = threshold
              validators = Map.ofArray nks
              innerQuorumSets = [||] }

        match o.quorumSet with
        | AllPeersQuorum -> ofNameKeyList (self.GetNameKeyListAll()) None
        | CoreSetQuorum (ns) -> ofNameKeyList (self.GetNameKeyList [ ns ]) None
        | CoreSetQuorumList (q) -> ofNameKeyList (self.GetNameKeyList q) None
        | CoreSetQuorumListWithThreshold (q, t) -> ofNameKeyList (self.GetNameKeyList q) (Some(t))
        | ExplicitQuorum (e) -> e

    member self.HistoryNodes(o: CoreSetOptions) : Map<PeerShortName, PeerDnsName> =
        match o.historyNodes, o.quorumSet with
        | None, CoreSetQuorum (x) -> self.DnsNamesWithKey([ x ]) |> Map.ofArray
        | None, _ -> self.DnsNamesWithKey() |> Map.ofArray
        | Some (x), _ -> self.DnsNamesWithKey(x) |> Map.ofArray

    member self.PreferredPeers(o: CoreSetOptions) =
        match o.peers with
        | None ->
            List.concat [ self.DnsNames() |> List.ofArray
                          o.peersDns ]
        | Some (x) ->
            List.concat [ self.DnsNames(x) |> List.ofArray
                          o.peersDns ]

    member self.PreferredPeersForNode (o: CoreSetOptions) (keyPair: KeyPair) =
        match o.preferredPeersMap with
        | Some (map) ->
            let preferredPeers : byte [] list = Map.find keyPair.PublicKey map
            List.map (fun key -> self.pubKeyToPeerDnsNameMap.[key]) preferredPeers
        | None -> failwith "Unable to create preferredPeers without preferredPeersMap"

    member self.getDbUrl(o: CoreSetOptions) : DatabaseURL =
        match o.dbType with
        | Postgres -> PostgreSQL(CfgVal.pgDb, CfgVal.pgUser, CfgVal.pgPassword, CfgVal.pgHost)
        | Sqlite -> SQLite3File CfgVal.databasePath
        | SqliteMemory -> SQLite3Memory

    member self.StellarCoreCfgForJob(opts: CoreSetOptions) : StellarCoreCfg =
        { network = self
          database = self.getDbUrl opts
          networkPassphrase = self.networkPassphrase
          nodeSeed = KeyPair.Random()
          nodeIsValidator = false
          runStandalone = false
          image = opts.image
          preferredPeers = self.PreferredPeers opts
          preferredPeersOnly = false
          targetPeerConnections = 16
          catchupMode = opts.catchupMode
          automaticMaintenancePeriod = if opts.performMaintenance then 10 else 0
          automaticMaintenanceCount = if opts.performMaintenance then 50000 else 0
          accelerateTime = opts.accelerateTime
          generateLoad = true
          updateSorobanCosts = opts.updateSorobanCosts
          manualClose = false
          invariantChecks = opts.invariantChecks
          unsafeQuorum = opts.unsafeQuorum
          failureSafety = 0
          quorumSet = self.QuorumSet opts
          historyNodes = self.HistoryNodes opts
          historyGetCommands = opts.historyGetCommands
          localHistory = opts.localHistory
          maxSlotsToRemember = opts.maxSlotsToRemember
          maxBatchWriteCount = opts.maxBatchWriteCount
          inMemoryMode = opts.inMemoryMode
          addArtificialDelayUsec = opts.addArtificialDelayUsec
          deprecatedSQLState = opts.deprecatedSQLState
          surveyPhaseDuration = opts.surveyPhaseDuration
          containerType = MainCoreContainer }

    member self.StellarCoreCfg(c: CoreSet, i: int, ctype: CoreContainerType) : StellarCoreCfg =
        { network = self
          database = self.getDbUrl c.options
          networkPassphrase = self.networkPassphrase
          nodeSeed = c.keys.[i]
          nodeIsValidator = c.options.validate
          runStandalone = false
          image = c.options.image
          preferredPeers =
              match c.options.preferredPeersMap with
              | None -> self.PreferredPeers c.options
              | _ -> self.PreferredPeersForNode c.options (c.keys.[i])
          preferredPeersOnly = c.options.preferredPeersMap.IsSome
          targetPeerConnections =
              match c.options.preferredPeersMap with
              | None -> 16
              | _ -> self.PreferredPeersForNode c.options (c.keys.[i]) |> List.length
          catchupMode = c.options.catchupMode
          automaticMaintenancePeriod = if c.options.performMaintenance then 10 else 0
          automaticMaintenanceCount = if c.options.performMaintenance then 50000 else 0
          accelerateTime = c.options.accelerateTime
          generateLoad = true
          updateSorobanCosts = c.options.updateSorobanCosts
          manualClose = false
          invariantChecks = c.options.invariantChecks
          unsafeQuorum = c.options.unsafeQuorum
          failureSafety = 0
          quorumSet = self.QuorumSet c.options
          historyNodes = self.HistoryNodes c.options
          historyGetCommands = c.options.historyGetCommands
          localHistory = c.options.localHistory
          maxSlotsToRemember = c.options.maxSlotsToRemember
          maxBatchWriteCount = c.options.maxBatchWriteCount
          inMemoryMode = c.options.inMemoryMode
          addArtificialDelayUsec = c.options.addArtificialDelayUsec
          deprecatedSQLState = c.options.deprecatedSQLState
          surveyPhaseDuration = c.options.surveyPhaseDuration
          containerType = ctype }
