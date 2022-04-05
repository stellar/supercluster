// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreSet

open stellar_dotnet_sdk

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
      fetchDBFromPeer: (CoreSetName * int) option }

    static member Default =
        { newDb = true
          newHist = true
          initialCatchup = false
          waitForConsensus = false
          fetchDBFromPeer = None }

    static member DefaultNoForceSCP =
        { newDb = true
          newHist = true
          initialCatchup = false
          waitForConsensus = true
          fetchDBFromPeer = None }

    static member CatchupNoForceSCP =
        { newDb = true
          newHist = true
          initialCatchup = true
          waitForConsensus = true
          fetchDBFromPeer = None }

    static member OnlyNewDb =
        { newDb = true
          newHist = false
          initialCatchup = false
          waitForConsensus = true
          fetchDBFromPeer = None }

    static member NoInitCmds =
        { newDb = false
          newHist = false
          initialCatchup = false
          waitForConsensus = true
          fetchDBFromPeer = None }

type GeoLoc = { lat: float; lon: float }

type QuorumSet =
    { thresholdPercent: int option
      validators: Map<PeerShortName, KeyPair>
      innerQuorumSets: QuorumSet array }

type QuorumSetSpec =
    | CoreSetQuorum of CoreSetName
    | CoreSetQuorumList of CoreSetName list
    | ExplicitQuorum of QuorumSet
    | AllPeersQuorum

// FIXME: see bug https://github.com/stellar/stellar-core/issues/2304
// the BucketListIsConsistentWithDatabase invariant blocks too long,
// so we provide a special variant to allow disabling it.
type InvariantChecksSpec =
    | AllInvariants
    | AllInvariantsExceptBucketConsistencyChecks
    | NoInvariants

type CoreSetOptions =
    { nodeCount: int
      nodeLocs: GeoLoc list option
      dbType: DBType
      emptyDirType: EmptyDirType
      syncStartupDelay: int option
      quorumSet: QuorumSetSpec
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
      tier1: bool option
      catchupMode: CatchupMode
      image: string
      initialization: CoreSetInitialization
      invariantChecks: InvariantChecksSpec
      dumpDatabase: bool
      maxSlotsToRemember: int
      maxBatchWriteCount: int
      inMemoryMode: bool }

    member self.WithWaitForConsensus(w: bool) =
        { self with initialization = { self.initialization with waitForConsensus = w } }

    static member GetDefault(image: string) =
        { nodeCount = 3
          nodeLocs = None
          dbType = Sqlite
          emptyDirType = MemoryBackedEmptyDir
          syncStartupDelay = Some(5)
          quorumSet = AllPeersQuorum
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
          tier1 = None
          catchupMode = CatchupComplete
          image = image
          initialization = CoreSetInitialization.Default
          invariantChecks = InvariantChecksSpec.AllInvariants
          dumpDatabase = true
          maxSlotsToRemember = 12
          maxBatchWriteCount = 1024
          inMemoryMode = false }

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
