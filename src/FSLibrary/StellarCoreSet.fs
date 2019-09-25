// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreSet

open stellar_dotnet_sdk

type CatchupMode =
    | CatchupComplete
    | CatchupRecent of int

type CoreSetInitialization =
    { newDb : bool
      newHist : bool
      initialCatchup : bool
      forceScp : bool }

    static member Default =
      { newDb = true
        newHist = true
        initialCatchup = false
        forceScp = true }


type CoreSetOptions =
    { nodeCount : int
      quorumSet : string list option
      quorumSetKeys : Map<string, KeyPair>
      historyNodes : string list option
      historyGetCommands : Map<string, string>
      peers : string list option
      peersDns : string list
      accelerateTime : bool
      awaitSync : bool
      validate : bool
      catchupMode : CatchupMode
      image : string option
      persistentVolume : string option
      initialization : CoreSetInitialization }

    static member Default =
      { nodeCount = 3
        quorumSet = None
        quorumSetKeys = Map.empty
        historyNodes = None
        historyGetCommands = Map.empty
        peers = None
        peersDns = List.empty
        accelerateTime = false
        awaitSync = true
        validate = true
        catchupMode = CatchupComplete
        image = None
        persistentVolume = None
        initialization = CoreSetInitialization.Default }

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
