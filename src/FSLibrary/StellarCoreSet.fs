// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreSet

open stellar_dotnet_sdk

type PeerDnsName =
    | PeerDnsName of string
    member self.StringName =
        match self with
        | PeerDnsName(n) -> n

type PeerShortName =
    | PeerShortName of string
    member self.StringName =
        match self with
        | PeerShortName(n) -> n

type CoreSetName =
    | CoreSetName of string
    member self.StringName =
        match self with
        | CoreSetName(n) -> n

type HomeDomainName =
    | HomeDomainName of string
    member self.StringName =
        match self with
        | HomeDomainName(n) -> n

type CatchupMode =
    | CatchupComplete
    | CatchupRecent of int

type DBType =
    | Sqlite
    | SqliteMemory
    | Postgres

type CoreSetInitialization =
    { newDb : bool
      newHist : bool
      initialCatchup : bool
      forceScp : bool
      fetchDBFromPeer: (CoreSetName * int) option }

    static member Default =
      { newDb = true
        newHist = true
        initialCatchup = false
        forceScp = true
        fetchDBFromPeer = None }

    static member DefaultNoForceSCP =
      { newDb = true
        newHist = true
        initialCatchup = false
        forceScp = false
        fetchDBFromPeer = None }

    static member CatchupNoForceSCP =
      { newDb = true
        newHist = true
        initialCatchup = true
        forceScp = false
        fetchDBFromPeer = None }

    static member OnlyNewDb =
      { newDb = true
        newHist = false
        initialCatchup = false
        forceScp = false
        fetchDBFromPeer = None }

    static member NoInitCmds =
      { newDb = false
        newHist = false
        initialCatchup = false
        forceScp = false
        fetchDBFromPeer = None }

type GeoLoc = {lat:float; lon:float}

type QuorumSet = {
    thresholdPercent: int option
    validators: Map<PeerShortName, KeyPair>
    innerQuorumSets: QuorumSet array
}

type QuorumSetSpec =
    | CoreSetQuorum of CoreSetName
    | ExplicitQuorum of QuorumSet
    | AllPeersQuorum

type CoreSetOptions =
    { nodeCount : int
      nodeLocs : GeoLoc list option
      dbType : DBType
      syncStartupDelay : int option
      quorumSet : QuorumSetSpec
      historyNodes : CoreSetName list option
      historyGetCommands : Map<PeerShortName, string>
      localHistory : bool
      peers : CoreSetName list option
      peersDns : PeerDnsName list
      accelerateTime : bool
      unsafeQuorum : bool
      awaitSync : bool
      validate : bool
      catchupMode : CatchupMode
      image : string
      initialization : CoreSetInitialization
      dumpDatabase: bool
      fullyConnected: bool
      simulateApplyUsec : int
      maxSlotsToRemember : int
      maxBatchReadCount : int
      maxBatchWriteCount : int }

    member self.WithForceSCP (f:bool) =
        { self with initialization = { self.initialization with forceScp = f } }

    static member GetDefault(image: string) =
      { nodeCount = 3
        nodeLocs = None
        dbType = Sqlite
        syncStartupDelay = None
        quorumSet = AllPeersQuorum
        historyNodes = None
        historyGetCommands = Map.empty
        localHistory = true
        peers = None
        peersDns = List.empty
        accelerateTime = true
        unsafeQuorum = true
        awaitSync = true
        validate = true
        catchupMode = CatchupComplete
        image = image
        initialization = CoreSetInitialization.Default
        dumpDatabase = true
        fullyConnected = false
        simulateApplyUsec = 0
        maxSlotsToRemember = 12
        maxBatchReadCount = 1024
        maxBatchWriteCount = 1024 }

    member self.InConsensusOnlyMode (simulateApply:int) =
        { self with simulateApplyUsec = simulateApply; initialization = CoreSetInitialization.NoInitCmds }

type CoreSet =
    { name : CoreSetName
      options : CoreSetOptions
      keys : KeyPair array
      live : bool }

    member self.NumKeys : int =
        self.keys.Length

    member self.CurrentCount : int =
        if self.live then self.keys.Length else 0

    member self.WithLive (live : bool) : CoreSet =
        { name = self.name
          options = self.options
          keys = self.keys
          live = live }


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
