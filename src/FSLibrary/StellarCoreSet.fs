// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreSet

open StellarDotnetSdk.Accounts

// A PodName followed by the (nonce-qualified) service DNS suffix for a
// given network, such as
// ssc-483463dbb624-sts-stellar-io-0.ssc-483463dbb624-stellar-core.sandbox.svc.cluster.local
type PeerDnsName =
    | PeerDnsName of string
    member self.StringName =
        match self with
        | PeerDnsName (n) -> n

// A StatefulSetName followed by a number, such as ssc-483463dbb624-sts-core-0
// or ssc-483463dbb624-sts-stellar-2. These are the names that
// kubernetes will assign to pods in a statefulset as it instantiates the pod
// template.
type PodName =
    | PodName of string
    member self.StringName =
        match self with
        | PodName (n) ->
            // Kubernetes will stick a 11-digit trailing nonce on this name and
            // complain if the result is any larger than 63 characters. This
            // is a DNS label-length limitation, evidently.
            if n.Length + 11 > 63 then
                failwith "Pod name %s is too long, Kubernetes will reject it"

            n

// A nonce-qualified CoreSetName like ssc-483463dbb624-sts-stellar that
// can be used to identify a statefulset in kubernetes without colliding with
// others in the namespace.
type StatefulSetName =
    | StatefulSetName of string
    member self.StringName =
        match self with
        | StatefulSetName (n) -> n

// A CoreSetName followed by a number, such as core-0 or
// stellar-1. Identifies a peer within a statefulset in places that are
// already qualified (eg. pod labels for use in prometheus time series)
type PeerShortName =
    | PeerShortName of string
    member self.StringName =
        match self with
        | PeerShortName (n) -> n

// A short symbolic name like "core" or one derived from a HomeDomainName like
// "stellar", used in deriving statefulset names.
type CoreSetName =
    | CoreSetName of string
    member self.StringName =
        match self with
        | CoreSetName (n) -> n

// A name like "www.stellar.org" used in grouping nodes in a qset. Also used to
// derive CoreSetNames by changing dots to hyphens.
type HomeDomainName =
    | HomeDomainName of string
    member self.StringName =
        match self with
        | HomeDomainName (n) -> n

type CatchupMode =
    | CatchupComplete
    | CatchupRecent of int

type DBType =
    | Sqlite
    | SqliteMemory
    | Postgres

type EmptyDirType =
    | MemoryBackedEmptyDir
    | DiskBackedEmptyDir

type CoreSetInitialization =
    { newDb: bool
      newHist: bool
      initialCatchup: bool
      waitForConsensus: bool
      fetchDBFromPeer: (CoreSetName * int) option
      // (numTxs, numAccounts, offset)
      pregenerateTxs: (int * int * int) option }

    static member Default =
        { newDb = true
          newHist = true
          initialCatchup = false
          waitForConsensus = false
          fetchDBFromPeer = None
          pregenerateTxs = None }

    static member DefaultNoForceSCP =
        { newDb = true
          newHist = true
          initialCatchup = false
          waitForConsensus = true
          fetchDBFromPeer = None
          pregenerateTxs = None }

    static member CatchupNoForceSCP =
        { newDb = true
          newHist = true
          initialCatchup = true
          waitForConsensus = true
          fetchDBFromPeer = None
          pregenerateTxs = None }

    static member OnlyNewDb =
        { newDb = true
          newHist = false
          initialCatchup = false
          waitForConsensus = true
          fetchDBFromPeer = None
          pregenerateTxs = None }

    static member NoInitCmds =
        { newDb = false
          newHist = false
          initialCatchup = false
          waitForConsensus = true
          fetchDBFromPeer = None
          pregenerateTxs = None }

type GeoLoc = { lat: float; lon: float }

type ExplicitQuorumSet =
    { thresholdPercent: int option
      validators: Map<PeerShortName, KeyPair>
      innerQuorumSets: ExplicitQuorumSet array }

type Quality =
    | Critical
    | High
    | Medium
    | Low

type HomeDomain = { name: string; quality: Quality }

type AutoValidator = { name: PeerShortName; homeDomain: string; keys: KeyPair }

type AutoQuorumSet = { homeDomains: HomeDomain list; validators: AutoValidator list }

type QuorumSet =
    | ExplicitQuorumSet of ExplicitQuorumSet
    | AutoQuorumSet of AutoQuorumSet

type QuorumSetSpec =
    | CoreSetQuorum of CoreSetName
    | CoreSetQuorumList of CoreSetName list
    | CoreSetQuorumListWithThreshold of CoreSetName list * int
    | ExplicitQuorum of ExplicitQuorumSet
    | AutoQuorum of AutoQuorumSet
    | AllPeersQuorum

// FIXME: see bug https://github.com/stellar/stellar-core/issues/2304
// the BucketListIsConsistentWithDatabase invariant blocks too long,
// so we provide a special variant to allow disabling it.

// We don't enable the EventsAreConsistentWithEntryDiffs invariant on
// any of the missions that use this spec because events would also need
// to be enabled. We enable the events and the invariant for the
// HistoryPubnetParallelCatchupV2 mission (which is configured
// under MissionParallelCatchup/parallel_catchup_helm/files/stellar-core.cfg),
// So we don't need it for the other missions.
type InvariantChecksSpec =
    | AllInvariantsExceptEvents
    | AllInvariantsExceptBucketConsistencyChecksAndEvents
    | NoInvariants

// Determines how quorum set configurations should be generated
type QuorumSetConfiguration =
    // Prefer automatic quorum set configuration. Fall back on explicit quorum
    // set configuration if automatic configuration is not possible, or if
    // --enable-relaxed-auto-qset-config is not set.
    | PreferAutoQset
    // Require automatic quorum set configuration. Fail if automatic
    // configuration is not possible. Uses automatic configuration even if
    // --enable-relaxed-auto-qset-config is not set, so missions using this
    // option *must* satisfy the HIGH quality validator checks present in
    // stellar-core.
    | RequireAutoQset
    // Require explicit quorum set configuration.
    | RequireExplicitQset

type CoreSetOptions =
    { nodeCount: int
      nodeLocs: GeoLoc list option
      dbType: DBType
      emptyDirType: EmptyDirType
      syncStartupDelay: int option
      quorumSet: QuorumSetSpec
      quorumSetConfigType: QuorumSetConfiguration
      forceOldStyleLeaderElection: bool
      historyNodes: CoreSetName list option
      preferredPeersMap: Map<byte [], byte [] list> option
      historyGetCommands: Map<PeerShortName, string>
      localHistory: bool
      peers: CoreSetName list option
      peersDns: PeerDnsName list
      accelerateTime: bool
      performMaintenance: bool
      unsafeQuorum: bool
      awaitSync: bool
      validate: bool
      homeDomain: string option
      tier1: bool option
      catchupMode: CatchupMode
      image: string
      initialization: CoreSetInitialization
      invariantChecks: InvariantChecksSpec
      dumpDatabase: bool
      maxSlotsToRemember: int
      maxBatchWriteCount: int
      emitMeta: bool
      addArtificialDelayUsec: int option
      experimentalTriggerTimer: bool option
      clockOffsets: int list option
      surveyPhaseDuration: int option
      updateSorobanCosts: bool option
      // `skipHighCriticalValidatorChecks` exists to allow supercluster to
      // remain compatible with older stellar-core images that do not have the
      // ability to turn of validator checks for HIGH and CRITICAL validators
      skipHighCriticalValidatorChecks: bool }

    member self.WithWaitForConsensus(w: bool) =
        { self with initialization = { self.initialization with waitForConsensus = w } }

    static member GetDefault(image: string) =
        { nodeCount = 3
          nodeLocs = None
          dbType = Sqlite
          emptyDirType = MemoryBackedEmptyDir
          syncStartupDelay = Some(5)
          quorumSet = AllPeersQuorum
          forceOldStyleLeaderElection = false
          quorumSetConfigType = PreferAutoQset
          historyNodes = None
          preferredPeersMap = None
          historyGetCommands = Map.empty
          localHistory = true
          peers = None
          peersDns = List.empty
          accelerateTime = true
          performMaintenance = true
          unsafeQuorum = true
          awaitSync = true
          validate = true
          homeDomain = Some "stellar.org"
          tier1 = None
          catchupMode = CatchupComplete
          image = image
          initialization = CoreSetInitialization.Default
          invariantChecks = InvariantChecksSpec.AllInvariantsExceptEvents
          dumpDatabase = true
          maxSlotsToRemember = 12
          maxBatchWriteCount = 1024
          emitMeta = false
          addArtificialDelayUsec = None
          experimentalTriggerTimer = None
          clockOffsets = None
          surveyPhaseDuration = None
          updateSorobanCosts = None
          skipHighCriticalValidatorChecks = true }

type CoreSet =
    { name: CoreSetName
      options: CoreSetOptions
      keys: KeyPair array
      live: bool }

    member self.NumKeys : int = self.keys.Length

    member self.CurrentCount : int = if self.live then self.keys.Length else 0

    member self.WithLive(live: bool) : CoreSet =
        { name = self.name; options = self.options; keys = self.keys; live = live }


let MakeLiveCoreSet (name: string) (options: CoreSetOptions) : CoreSet =
    { name = CoreSetName name
      options = options
      keys = Array.init options.nodeCount (fun _ -> KeyPair.Random())
      live = true }

let MakeDeferredCoreSet (name: string) (options: CoreSetOptions) : CoreSet =
    { name = CoreSetName name
      options = options
      keys = Array.init options.nodeCount (fun _ -> KeyPair.Random())
      live = false }
