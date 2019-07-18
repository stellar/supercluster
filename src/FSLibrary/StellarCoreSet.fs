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
    { quorumSet : string list option
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
      { quorumSet = None
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
      mutable currentCount : int }

    member self.NumKeys : int =
        self.keys.Length

    member self.CurrentCount : int =
        self.currentCount

    member self.SetCurrentCount c =
        self.currentCount <- c

let MakeCoreSet name initialCount maxCount options =
    { name = name
      options = options
      keys = Array.init maxCount (fun _ -> KeyPair.Random())
      currentCount = initialCount }
