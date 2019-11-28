// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreSet

open stellar_dotnet_sdk

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
      fetchDBFromPeer: (string * int) option }

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


type CoreSetOptions =
    { nodeCount : int
      dbType : DBType
      quorumSet : string list option
      quorumSetKeys : Map<string, KeyPair>
      historyNodes : string list option
      historyGetCommands : Map<string, string>
      localHistory : bool
      peers : string list option
      peersDns : string list
      accelerateTime : bool
      unsafeQuorum : bool
      awaitSync : bool
      validate : bool
      catchupMode : CatchupMode
      image : string option
      initialization : CoreSetInitialization
      dumpDatabase: bool }

    member self.WithForceSCP (f:bool) =
        { self with initialization = { self.initialization with forceScp = f } }

    static member Default =
      { nodeCount = 3
        dbType = Sqlite
        quorumSet = None
        quorumSetKeys = Map.empty
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
        image = None
        initialization = CoreSetInitialization.Default
        dumpDatabase = true }

type CoreSet =
    { name : string
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
    { name = name
      options = options
      keys = Array.init options.nodeCount (fun _ -> KeyPair.Random())
      live = true }

let MakeDeferredCoreSet (name: string) (options: CoreSetOptions) : CoreSet =
    { name = name
      options = options
      keys = Array.init options.nodeCount (fun _ -> KeyPair.Random())
      live = false }
