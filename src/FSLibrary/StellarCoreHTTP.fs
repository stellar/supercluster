// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarCoreHTTP

open FSharp.Data
open StellarCoreSet
open PollRetry
open Logging
open StellarMissionContext
open StellarNetworkCfg
open StellarCorePeer
open System.Threading
open StellarDotnetSdk.Transactions
open StellarDotnetSdk.Responses.Results

// Note to curious reader: these are "type providers" whereby the compiler's
// type definition facility is extended by plugins that are themselves
// parameterized by literals that refer to external sources. In this case
// FSharp.Data.JsonProvider<> takes a sample of some JSON you want to load and
// infers a static type for it.

// This exists to work around a buggy interaction between FSharp.Data and
// dotnet 5.0.300: __SOURCE_DIRECTORY__ is not [<Literal>] anymore, but
// JsonProvider's ResolutionFolder argument needs to be.
[<Literal>]
let cwd = __SOURCE_DIRECTORY__

type Metrics = JsonProvider<"json-type-samples/sample-metrics.json", SampleIsList=true, ResolutionFolder=cwd>
type Info = JsonProvider<"json-type-samples/sample-info.json", SampleIsList=true, ResolutionFolder=cwd>
type SorobanInfo = JsonProvider<"json-type-samples/sample-soroban-info.json", SampleIsList=true, ResolutionFolder=cwd>
type TestAcc = JsonProvider<"json-type-samples/sample-testacc.json", SampleIsList=true, ResolutionFolder=cwd>
type Tx = JsonProvider<"json-type-samples/sample-tx.json", SampleIsList=true, ResolutionFolder=cwd>
type Loadgen = JsonProvider<"json-type-samples/loadgen-sample.json", SampleIsList=true, ResolutionFolder=cwd>

type LoadGenStatus =
    | Success
    | Failure
    | NotStarted
    | Running

type LoadGenMode =
    | GenerateAccountCreationLoad
    | GeneratePaymentLoad
    | GeneratePretendLoad
    | GenerateSorobanUploadLoad
    | SetupSorobanUpgrade
    | CreateSorobanUpgrade
    | SorobanInvokeSetup
    | SorobanInvoke
    | MixedClassicSoroban
    | StopRun
    | PayPregenerated

    override self.ToString() =
        match self with
        | GenerateAccountCreationLoad -> "create"
        | GeneratePaymentLoad -> "pay"
        | GeneratePretendLoad -> "pretend"
        | GenerateSorobanUploadLoad -> "soroban_upload"
        | SetupSorobanUpgrade -> "upgrade_setup"
        | CreateSorobanUpgrade -> "create_upgrade"
        | SorobanInvokeSetup -> "soroban_invoke_setup"
        | SorobanInvoke -> "soroban_invoke"
        | MixedClassicSoroban -> "mixed_classic_soroban"
        | StopRun -> "stop"
        | PayPregenerated -> "pay_pregenerated"

type LoadGen =
    { mode: LoadGenMode
      accounts: int
      txs: int
      spikesize: int
      spikeinterval: int
      txrate: int
      offset: int
      maxfeerate: int option
      skiplowfeetxs: bool
      minSorobanPercentSuccess: int option

      // New fields for SOROBAN_INVOKE mode
      wasms: int option
      instances: int option

      // Fields for SOROBAN_CREATE_UPGRADE parameters
      maxContractSizeBytes: int option
      maxContractDataKeySizeBytes: int option
      maxContractDataEntrySizeBytes: int option
      ledgerMaxInstructions: int64 option
      txMaxInstructions: int64 option
      txMemoryLimit: int option
      ledgerMaxReadLedgerEntries: int option
      ledgerMaxReadBytes: int option
      ledgerMaxWriteLedgerEntries: int option
      ledgerMaxWriteBytes: int option
      ledgerMaxTxCount: int option
      txMaxReadLedgerEntries: int option
      txMaxReadBytes: int option
      txMaxWriteLedgerEntries: int option
      txMaxWriteBytes: int option
      txMaxContractEventsSizeBytes: int option
      ledgerMaxTransactionsSizeBytes: int option
      txMaxSizeBytes: int option
      bucketListSizeWindowSampleSize: int option
      evictionScanSize: int64 option
      startingEvictionScanLevel: int option

      // Fields for BLEND_CLASSIC_SOROBAN mode
      payWeight: int option
      sorobanUploadWeight: int option
      sorobanInvokeWeight: int option }

    member self.ToQuery : (string * string) list =
        let mandatoryParams =
            [ ("mode", self.mode.ToString())
              ("accounts", self.accounts.ToString())
              ("txs", self.txs.ToString())
              ("txrate", self.txrate.ToString())
              ("spikesize", self.spikesize.ToString())
              ("spikeinterval", self.spikeinterval.ToString())
              ("offset", self.offset.ToString())
              ("skiplowfeetxs", (if self.skiplowfeetxs then "true" else "false")) ]

        let optionalParam (name: string) (value: 'T option) =
            match value with
            | Some v -> [ (name, v.ToString()) ]
            | None -> []

        let optionalParams =
            optionalParam "maxfeerate" self.maxfeerate
            @ optionalParam "wasms" self.wasms
              @ optionalParam "instances" self.instances
                @ optionalParam "minpercentsuccess" self.minSorobanPercentSuccess
                  @ optionalParam "mxcntrctsz" self.maxContractSizeBytes
                    @ optionalParam "mxcntrctkeysz" self.maxContractDataKeySizeBytes
                      @ optionalParam "mxcntrctdatasz" self.maxContractDataEntrySizeBytes
                        @ optionalParam "ldgrmxinstrc" self.ledgerMaxInstructions
                          @ optionalParam "txmxinstrc" self.txMaxInstructions
                            @ optionalParam "txmemlim" self.txMemoryLimit
                              @ optionalParam "ldgrmxrdntry" self.ledgerMaxReadLedgerEntries
                                @ optionalParam "ldgrmxrdbyt" self.ledgerMaxReadBytes
                                  @ optionalParam "ldgrmxwrntry" self.ledgerMaxWriteLedgerEntries
                                    @ optionalParam "ldgrmxwrbyt" self.ledgerMaxWriteBytes
                                      @ optionalParam "ldgrmxtxcnt" self.ledgerMaxTxCount
                                        @ optionalParam "txmxrdntry" self.txMaxReadLedgerEntries
                                          @ optionalParam "txmxrdbyt" self.txMaxReadBytes
                                            @ optionalParam "txmxwrntry" self.txMaxWriteLedgerEntries
                                              @ optionalParam "txmxwrbyt" self.txMaxWriteBytes
                                                @ optionalParam "txmxevntsz" self.txMaxContractEventsSizeBytes
                                                  @ optionalParam "ldgrmxtxsz" self.ledgerMaxTransactionsSizeBytes
                                                    @ optionalParam "txmxsz" self.txMaxSizeBytes
                                                      @ optionalParam "wndowsz" self.bucketListSizeWindowSampleSize
                                                        @ optionalParam "evctsz" self.evictionScanSize
                                                          @ optionalParam "evctlvl" self.startingEvictionScanLevel
                                                            @ optionalParam "payweight" self.payWeight
                                                              @ optionalParam
                                                                  "sorobanuploadweight"
                                                                  self.sorobanUploadWeight
                                                                @ optionalParam
                                                                    "sorobaninvokeweight"
                                                                    self.sorobanInvokeWeight

        mandatoryParams @ optionalParams

    static member GetDefault() =
        { mode = GenerateAccountCreationLoad
          accounts = 100
          txs = 100
          spikesize = 0
          spikeinterval = 0
          txrate = 2
          offset = 0
          maxfeerate = None
          skiplowfeetxs = false
          wasms = None
          instances = None
          minSorobanPercentSuccess = None
          maxContractSizeBytes = None
          maxContractDataKeySizeBytes = None
          maxContractDataEntrySizeBytes = None
          ledgerMaxInstructions = None
          txMaxInstructions = None
          txMemoryLimit = None
          ledgerMaxReadLedgerEntries = None
          ledgerMaxReadBytes = None
          ledgerMaxWriteLedgerEntries = None
          ledgerMaxWriteBytes = None
          ledgerMaxTxCount = None
          txMaxReadLedgerEntries = None
          txMaxReadBytes = None
          txMaxWriteLedgerEntries = None
          txMaxWriteBytes = None
          txMaxContractEventsSizeBytes = None
          ledgerMaxTransactionsSizeBytes = None
          txMaxSizeBytes = None
          bucketListSizeWindowSampleSize = None
          evictionScanSize = None
          startingEvictionScanLevel = None
          payWeight = None
          sorobanUploadWeight = None
          sorobanInvokeWeight = None }

// Takes a default value `v` and a list `l` and returns `v` if `l` is empty,
// otherwise `l`.
let defaultList (v: 'a list) (l: 'a list) =
    match l with
    | [] -> v
    | _ -> l

type MissionContext with

    member self.WithNominalLoad : MissionContext = { self with numTxs = 100; numAccounts = 100 }

    // For loadgen missions that increase soroban ledger and transaction limits.
    member self.WithMediumLoadgenOptions : MissionContext =
        { self with
              wasmBytesDistribution = [ (30000, 1) ]
              dataEntriesDistribution = [ (5, 1) ]
              totalKiloBytesDistribution = [ (3, 1) ]
              txSizeBytesDistribution = [ (512, 1) ]
              instructionsDistribution = [ (25000000, 1) ] }

    // For loadgen missions that do not increase both soroban ledger and
    // transaction limits. These are intended to fit within the default limits.
    member self.WithSmallLoadgenOptions : MissionContext =
        { self with
              wasmBytesDistribution = [ (1024, 1) ]
              dataEntriesDistribution = [ (1, 1) ]
              totalKiloBytesDistribution = [ (1, 1) ]
              txSizeBytesDistribution = [ (64, 1) ]
              instructionsDistribution = [ (1000000, 1) ] }

    member self.GenerateAccountCreationLoad : LoadGen =
        { LoadGen.GetDefault() with
              mode = GenerateAccountCreationLoad
              accounts = self.numAccounts
              txs = 0
              spikesize = 0
              spikeinterval = 0
              // Use conservative rate for account creation, as the network may quickly get overloaded
              txrate = 2
              offset = 0
              maxfeerate = self.maxFeeRate
              skiplowfeetxs = self.skipLowFeeTxs }

    member self.GeneratePaymentLoad : LoadGen =
        { LoadGen.GetDefault() with
              mode = GeneratePaymentLoad
              accounts = self.numAccounts
              txs = self.numTxs
              txrate = self.txRate
              spikesize = self.spikeSize
              spikeinterval = self.spikeInterval
              offset = 0
              maxfeerate = self.maxFeeRate
              skiplowfeetxs = self.skipLowFeeTxs }

    member self.GeneratePretendLoad : LoadGen =
        { LoadGen.GetDefault() with
              mode = GeneratePretendLoad
              accounts = self.numAccounts
              txs = self.numTxs
              txrate = self.txRate
              spikesize = self.spikeSize
              spikeinterval = self.spikeInterval
              offset = 0
              maxfeerate = self.maxFeeRate
              skiplowfeetxs = self.skipLowFeeTxs }

    member self.GenerateSorobanUploadLoad : LoadGen =
        { LoadGen.GetDefault() with
              mode = GenerateSorobanUploadLoad
              accounts = self.numAccounts
              txs = self.numTxs
              txrate = self.txRate
              spikesize = self.spikeSize
              spikeinterval = self.spikeInterval
              offset = 0
              maxfeerate = self.maxFeeRate
              skiplowfeetxs = self.skipLowFeeTxs }

    member self.GenerateSorobanInvokeLoad : LoadGen =
        { LoadGen.GetDefault() with
              mode = SorobanInvoke
              accounts = self.numAccounts
              txs = self.numTxs
              txrate = self.txRate
              spikesize = self.spikeSize
              spikeinterval = self.spikeInterval
              offset = 0
              maxfeerate = self.maxFeeRate
              skiplowfeetxs = self.skipLowFeeTxs }

    member self.SetupSorobanInvoke : LoadGen =
        { LoadGen.GetDefault() with
              mode = SorobanInvokeSetup
              accounts = self.numAccounts
              txs = self.numTxs
              txrate = self.txRate
              spikesize = self.spikeSize
              spikeinterval = self.spikeInterval
              offset = 0
              maxfeerate = self.maxFeeRate
              skiplowfeetxs = self.skipLowFeeTxs
              wasms = self.numWasms
              instances = self.numInstances }

let DefaultAccountCreationLoadGen =
    { LoadGen.GetDefault() with
          mode = GenerateAccountCreationLoad
          accounts = 1000
          txs = 0
          spikesize = 0
          spikeinterval = 0
          txrate = 2
          offset = 0
          maxfeerate = None
          skiplowfeetxs = false }


type UpgradeParameters =
    { upgradeTime: System.DateTime
      protocolVersion: Option<int>
      baseFee: Option<int>
      maxTxSetSize: Option<int>
      maxSorobanTxSetSize: Option<int>
      configUpgradeSetKey: Option<string>
      baseReserve: Option<int> }

    member self.ToQuery : (string * string) list =
        let maybe name opt = Option.toList (Option.map (fun v -> (name, v.ToString())) opt)

        List.concat [| [ ("mode", "set") ]
                       [ ("upgradetime", self.upgradeTime.ToUniversalTime().ToString("o")) ]
                       maybe "protocolversion" self.protocolVersion
                       maybe "basefee" self.baseFee
                       maybe "basereserve" self.baseReserve
                       maybe "maxtxsetsize" self.maxTxSetSize
                       maybe "maxsorobantxsetsize" self.maxSorobanTxSetSize
                       maybe "configupgradesetkey" self.configUpgradeSetKey |]

let DefaultUpgradeParameters =
    { upgradeTime = System.DateTime.Parse("1970-01-01T00:00:00Z")
      protocolVersion = None
      baseFee = None
      maxTxSetSize = None
      maxSorobanTxSetSize = None
      configUpgradeSetKey = None
      baseReserve = None }


let MeterCountOr (def: int) (m: Option<Metrics.GenericMeter>) : int =
    match m with
    | Some (n) -> n.Count
    | None -> def


let ConsistencyCheckIterationCount : int = 5

exception PeerRejectedUpgradesException of string
exception InconsistentPeersException of (Peer * Peer)
exception MaybeInconsistentPeersException of (Peer * Peer)
exception PeerHasNonzeroErrorMetricsException of (Peer * string * int)
exception TransactionRejectedException of Transaction
exception ProtocolVersionNotUpgradedException of (int * int)
exception NodeLostSyncException of string

type Peer with

    member self.Headers =
        let host = self.networkCfg.IngressInternalHostName
        [ HttpRequestHeaders.Host host ]

    member self.URL(path: string) : string =
        sprintf
            "http://%s:%d/%s/core/%s"
            self.networkCfg.IngressExternalHostName
            self.networkCfg.missionContext.ingressExternalPort
            self.PodName.StringName
            path

    member self.fetch(path: string) : string =
        let url = self.URL path
        Http.RequestString(url, headers = self.Headers)

    member self.GetState() =
        WebExceptionRetry DefaultRetry (fun _ -> Info.Parse(self.fetch "info").Info.State)

    member self.GetStatusOrState() : string =
        WebExceptionRetry
            DefaultRetry
            (fun _ ->
                let i = Info.Parse(self.fetch "info").Info
                if i.Status.Length = 0 then i.State else i.Status.[0])

    member self.GetMetrics() : Metrics.Metrics =
        WebExceptionRetry DefaultRetry (fun _ -> Metrics.Parse(self.fetch "metrics").Metrics)

    member self.GetRawMetrics() = WebExceptionRetry DefaultRetry (fun _ -> self.fetch "metrics")

    member self.GetInfo() : Info.Info = WebExceptionRetry DefaultRetry (fun _ -> Info.Parse(self.fetch "info").Info)

    member self.GetSorobanInfo() : SorobanInfo.Root =
        WebExceptionRetry DefaultRetry (fun _ -> SorobanInfo.Parse(self.fetch "sorobaninfo"))

    member self.GetLedgerNum() : int = self.GetInfo().Ledger.Num

    member self.GetMaxTxSetSize() : int = self.GetInfo().Ledger.MaxTxSetSize

    member self.GetSorobanMaxTxSetSize() : int = self.GetInfo().Ledger.MaxSorobanTxSetSize.Value

    member self.GetLedgerMaxInstructions() : int64 = self.GetSorobanInfo().Ledger.MaxInstructions

    member self.GetLedgerReadBytes() : int = self.GetSorobanInfo().Ledger.MaxReadBytes

    member self.GetLedgerWriteBytes() : int = self.GetSorobanInfo().Ledger.MaxWriteBytes

    member self.GetLedgerReadEntries() : int = self.GetSorobanInfo().Ledger.MaxReadLedgerEntries

    member self.GetLedgerWriteEntries() : int = self.GetSorobanInfo().Ledger.MaxWriteLedgerEntries

    member self.GetLedgerMaxTxCount() : int = self.GetSorobanInfo().Ledger.MaxTxCount

    member self.GetLedgerMaxTransactionsSizeBytes() : int = self.GetSorobanInfo().Ledger.MaxTxSizeBytes

    member self.GetTxMaxInstructions() : int64 = self.GetSorobanInfo().Tx.MaxInstructions

    member self.GetTxReadBytes() : int = self.GetSorobanInfo().Tx.MaxReadBytes

    member self.GetTxWriteBytes() : int = self.GetSorobanInfo().Tx.MaxWriteBytes

    member self.GetTxReadEntries() : int = self.GetSorobanInfo().Tx.MaxReadLedgerEntries

    member self.GetTxWriteEntries() : int = self.GetSorobanInfo().Tx.MaxWriteLedgerEntries

    member self.GetTxMemoryLimit() : int = self.GetSorobanInfo().Tx.MemoryLimit

    member self.GetMaxTxSize() : int = self.GetSorobanInfo().Tx.MaxSizeBytes

    member self.GetMaxContractSize() : int = self.GetSorobanInfo().MaxContractSize

    member self.GetMaxContractDataKeySize() : int = self.GetSorobanInfo().MaxContractDataKeySize

    member self.GetMaxContractDataEntrySize() : int = self.GetSorobanInfo().MaxContractDataEntrySize

    member self.GetTxMaxContractEventsSize() : int = self.GetSorobanInfo().Tx.MaxContractEventsSizeBytes

    member self.GetLedgerProtocolVersion() : int = self.GetInfo().Ledger.Version

    member self.GetSupportedProtocolVersion() : int = self.GetInfo().ProtocolVersion

    member self.SetUpgrades(upgrades: UpgradeParameters) =
        let res =
            WebExceptionRetry
                DefaultRetry
                (fun _ ->
                    Http.RequestString(
                        httpMethod = "GET",
                        url = self.URL "upgrades",
                        headers = self.Headers,
                        query = upgrades.ToQuery
                    ))

        if res.ToLower().Contains("exception") then
            raise (PeerRejectedUpgradesException res)

    member self.UpgradeProtocol (version: int) (time: System.DateTime) =
        let upgrades =
            { DefaultUpgradeParameters with
                  protocolVersion = Some(version)
                  upgradeTime = time }

        self.SetUpgrades(upgrades)

    member self.UpgradeProtocolToLatest(time: System.DateTime) =
        let upgrades =
            { DefaultUpgradeParameters with
                  protocolVersion = Some(self.GetSupportedProtocolVersion())
                  upgradeTime = time }

        self.SetUpgrades(upgrades)

    member self.UpgradeMaxTxSetSize (txSetSize: int) (time: System.DateTime) =
        let upgrades =
            { DefaultUpgradeParameters with
                  maxTxSetSize = Some(txSetSize)
                  upgradeTime = time }

        self.SetUpgrades(upgrades)

    member self.UpgradeSorobanMaxTxSetSize (txSetSize: int) (time: System.DateTime) =
        let upgrades =
            { DefaultUpgradeParameters with
                  maxSorobanTxSetSize = Some(txSetSize)
                  upgradeTime = time }

        self.SetUpgrades(upgrades)

    member self.UpgradeNetworkSetting (configUpgradeSetKey: string) (time: System.DateTime) =
        let upgrades =
            { DefaultUpgradeParameters with
                  configUpgradeSetKey = Some(configUpgradeSetKey)
                  upgradeTime = time }

        self.SetUpgrades(upgrades)

    member self.WaitForLedgerNum(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetLedgerNum() >= n)
            (fun _ -> LogInfo "Waiting for ledger %d on %s: %s" n self.ShortName.StringName (self.GetStatusOrState()))

    member self.WaitForFewLedgers(count: int) = self.WaitForLedgerNum(self.GetLedgerNum() + count)

    member self.WaitForNextLedger() = self.WaitForFewLedgers(1)

    member self.WaitForProtocol(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetLedgerProtocolVersion() = n)
            (fun _ -> LogInfo "Waiting for protocol %d on %s" n self.ShortName.StringName)

    member self.WaitForLatestProtocol() =
        let latest = self.GetSupportedProtocolVersion()
        self.WaitForProtocol latest

    member self.WaitForMaxTxSetSize(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetMaxTxSetSize() = n)
            (fun _ -> LogInfo "Waiting for MaxTxSetSize=%d on %s" n self.ShortName.StringName)

    member self.WaitForSorobanMaxTxSetSize(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetSorobanMaxTxSetSize() = n)
            (fun _ -> LogInfo "Waiting for SorobanMaxTxSetSize=%d on %s" n self.ShortName.StringName)

    member self.WaitForLedgerMaxInstructions(n: int64) =
        RetryUntilTrue
            (fun _ -> self.GetLedgerMaxInstructions() = n)
            (fun _ -> LogInfo "Waiting for LedgerMaxInstructions=%d on %s" n self.ShortName.StringName)

    member self.WaitForLedgerMaxTxCount(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetLedgerMaxTxCount() = n)
            (fun _ -> LogInfo "Waiting for LedgerMaxTxCount=%d on %s" n self.ShortName.StringName)

    member self.WaitForTxMaxInstructions(n: int64) =
        RetryUntilTrue
            (fun _ -> self.GetTxMaxInstructions() = n)
            (fun _ -> LogInfo "Waiting for TxMaxInstructions=%d on %s" n self.ShortName.StringName)

    member self.WaitForMaxTxSize(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetMaxTxSize() = n)
            (fun _ -> LogInfo "Waiting for MaxTxSize=%d on %s" n self.ShortName.StringName)

    member self.WaitForNextSeq (src: string) (n: int64) =
        let desiredSeq = n + int64 (1)

        RetryUntilTrue
            (fun _ -> self.GetTestAccSeq(src) >= desiredSeq)
            (fun _ -> LogInfo "Waiting for seqnum=%d for account %s" desiredSeq src)

    member self.CheckNoErrorMetrics(includeTxInternalErrors: bool) =
        let raiseIfNonzero (c: int) (n: string) = if c <> 0 then raise (PeerHasNonzeroErrorMetricsException(self, n, c))
        let m : Metrics.Metrics = self.GetMetrics()
        raiseIfNonzero m.ScpEnvelopeInvalidsig.Count "scp.envelope.invalidsig"
        raiseIfNonzero m.HistoryPublishFailure.Count "history.publish.failure"
        raiseIfNonzero m.LedgerInvariantFailure.Count "ledger.invariant.failure"

        if includeTxInternalErrors then
            raiseIfNonzero m.LedgerTransactionInternalError.Count "ledger.transaction.internal-error"

        LogInfo "No errors found on %s" self.ShortName.StringName

    member self.CheckConsistencyWith(other: Peer) =
        let shortHash (v: string) : string = v.Remove 6

        let rec loop (ours: Map<int, string>) (theirs: Map<int, string>) (n: int) =
            if n < 0 then
                raise (MaybeInconsistentPeersException(self, other))
            else

            // Sleep to allow ledgers to close. No need to sleep on the first iteration
            if Map.exists
                (fun k v ->
                    match theirs.TryFind k with
                    | None -> false
                    | Some w when v = w ->
                        LogInfo
                            "found agreeing ledger %d = %s on %s and %s"
                            k
                            (shortHash v)
                            self.ShortName.StringName
                            other.ShortName.StringName

                        true
                    | Some w ->
                        LogError
                            "Inconsistent peers: ledger %d = %s on %s and %s on %s"
                            k
                            (shortHash v)
                            self.ShortName.StringName
                            (shortHash w)
                            other.ShortName.StringName

                        raise (InconsistentPeersException(self, other)))
                ours then
                ()
            else

                // Sleep to allow ledgers to close. No need to sleep on the first iteration
                if n < ConsistencyCheckIterationCount then
                    // Sleep for 1 second when using accelerateTime, and 5 seconds otherwise
                    // because it does not make much sense to check every 1 second
                    // if the network closes ledgers every 5 seconds.
                    Thread.Sleep(if self.coreSet.options.accelerateTime then 1000 else 5000)

                let ensureSyncedIfTier1 p =
                    if p.coreSet.options.tier1 = Some true && p.GetState() <> "Synced!" then
                        (NodeLostSyncException p.ShortName.StringName) |> raise

                ensureSyncedIfTier1 self
                ensureSyncedIfTier1 other

                let ourLedger = self.GetInfo().Ledger
                let theirLedger = other.GetInfo().Ledger

                loop
                    (Map.add ourLedger.Num ourLedger.Hash ours)
                    (Map.add theirLedger.Num theirLedger.Hash theirs)
                    (n - 1)

        loop Map.empty Map.empty ConsistencyCheckIterationCount

    member self.CheckUsesLatestProtocolVersion() =
        let lastestProtocolVersion = self.GetSupportedProtocolVersion()
        let currentProtocolVersion = self.GetLedgerProtocolVersion()

        if lastestProtocolVersion <> currentProtocolVersion then
            raise (ProtocolVersionNotUpgradedException(currentProtocolVersion, lastestProtocolVersion))

    member self.ClearMetrics() =
        WebExceptionRetry
            DefaultRetry
            (fun _ -> Http.RequestString(httpMethod = "GET", headers = self.Headers, url = self.URL "clearmetrics"))
        |> ignore

    member self.GetTestAcc(accName: string) : TestAcc.Root =
        // NB: work around buggy JSON parser upstream, see
        // https://github.com/fsharp/FSharp.Data/pull/1262
        let s =
            WebExceptionRetry
                DefaultRetry
                (fun _ ->
                    Http.RequestString(
                        httpMethod = "GET",
                        url = self.URL("testacc"),
                        headers = self.Headers,
                        query = [ ("name", accName) ]
                    ))

        TestAcc.Parse(if s.Trim().StartsWith("null") then "{}" else s)

    member self.StartSurveyCollecting(nonce: int) =
        WebExceptionRetry
            DefaultRetry
            (fun _ ->
                Http.RequestString(
                    httpMethod = "GET",
                    url = self.URL("startsurveycollecting"),
                    headers = self.Headers,
                    query = [ ("nonce", nonce.ToString()) ]
                ))

    member self.StopSurveyCollecting() =
        WebExceptionRetry
            DefaultRetry
            (fun _ ->
                Http.RequestString(httpMethod = "GET", url = self.URL("stopsurveycollecting"), headers = self.Headers))

    member self.SurveyTopologyTimeSliced (node: string) (inboundPeersIndex: int) (outboundPeersIndex: int) =
        WebExceptionRetry
            DefaultRetry
            (fun _ ->
                Http.RequestString(
                    httpMethod = "GET",
                    url = self.URL("surveytopologytimesliced"),
                    headers = self.Headers,
                    query =
                        [ ("node", node)
                          ("inboundpeerindex", inboundPeersIndex.ToString())
                          ("outboundpeerindex", outboundPeersIndex.ToString()) ]
                ))

    member self.GetSurveyResult() =
        WebExceptionRetry
            DefaultRetry
            (fun _ -> Http.RequestString(httpMethod = "GET", url = self.URL("getsurveyresult"), headers = self.Headers))

    member self.GetTestAccBalance(accName: string) : int64 =
        RetryUntilSome
            (fun _ -> self.GetTestAcc(accName).Balance)
            (fun _ -> LogWarn "Waiting for account %s to exist, to read balance" accName)

    member self.GetTestAccSeq(accName: string) : int64 =
        RetryUntilSome
            (fun _ -> self.GetTestAcc(accName).Seqnum)
            (fun _ -> LogWarn "Waiting for account %s to exist, to read seqnum" accName)

    member self.GenerateLoad(loadGen: LoadGen) : string =
        WebExceptionRetry
            DefaultRetry
            (fun _ ->
                Http.RequestString(
                    httpMethod = "GET",
                    headers = self.Headers,
                    url = self.URL "generateload",
                    query = loadGen.ToQuery
                ))

    member self.StopLoadGen() : string = self.GenerateLoad { LoadGen.GetDefault() with mode = StopRun }

    member self.ManualClose() =
        WebExceptionRetry
            DefaultRetry
            (fun _ -> Http.RequestString(httpMethod = "GET", headers = self.Headers, url = self.URL "manualclose"))
        |> ignore

    member self.SubmitSignedTransaction(tx: Transaction) : Tx.Root =
        let b64 = tx.ToEnvelopeXdrBase64()

        let s =
            WebExceptionRetry
                DefaultRetry
                (fun _ ->
                    LogDebug
                        "Submitting transaction: %s"
                        ((self.URL "tx") + "?blob=" + (System.Uri.EscapeDataString b64))

                    let response =
                        Http.RequestStream(
                            (self.URL "tx"),
                            httpMethod = "GET",
                            query = [ "blob", b64 ],
                            headers = [ "Host", self.networkCfg.IngressInternalHostName ]
                        )

                    use reader = new System.IO.StreamReader(response.ResponseStream)
                    reader.ReadToEnd())

        LogDebug "Transaction response: %s" s
        let res = Tx.Parse(s)

        if res.Status <> "PENDING" then
            LogError "Transaction result %s" res.Status

            match res.Error with
            | None -> ()
            | Some (r) ->
                let txr = TransactionResult.FromXdrBase64(r)
                LogError "Result details: %O" txr

            raise (TransactionRejectedException tx)
        else
            res

    member self.LogCoreSetListStatusWithTiers(coreSetList: CoreSet list) : unit =
        let tier1CoreSetList = List.filter (fun cs -> cs.options.tier1 = Some true) coreSetList
        let nonTier1CoreSetList = List.filter (fun cs -> cs.options.tier1 = Some false) coreSetList

        let getStateList (cs: CoreSet) =
            Seq.map (fun i -> (self.networkCfg.GetPeer cs i).GetState()) (seq { 0 .. (cs.options.nodeCount - 1) })
            |> Seq.toList

        let tier1StatusList = List.map getStateList tier1CoreSetList |> List.concat
        let nonTier1StatusList = List.map getStateList nonTier1CoreSetList |> List.concat

        let countAndLogStates ls nodeType =
            List.sort ls
            |> Seq.countBy id
            |> Seq.iter (fun x -> LogInfo "%d %s nodes have state %s" (snd x) nodeType (fst x))

        countAndLogStates tier1StatusList "tier1"
        countAndLogStates nonTier1StatusList "non-tier1"

    member self.IsLoadGenComplete() =
        if self.GetState() <> "Synced!" then
            (NodeLostSyncException self.ShortName.StringName) |> raise

        let m = self.GetMetrics()

        if (MeterCountOr 0 m.LoadgenRunFailed) <> 0 then
            Failure
        elif (MeterCountOr 0 m.LoadgenRunStart) = 0 then
            NotStarted
        elif (MeterCountOr 0 m.LoadgenRunStart) = (MeterCountOr 0 m.LoadgenRunComplete) then
            Success
        else
            Running

    member self.LogLoadGenProgressTowards(loadGen: LoadGen) =
        let m = self.GetMetrics()

        LogInfo
            "Waiting for loadgen run %d on %s to finish, %d/%d accts, %d/%d txns"
            (MeterCountOr 0 m.LoadgenRunStart)
            self.ShortName.StringName
            (MeterCountOr 0 m.LoadgenAccountCreated)
            loadGen.accounts
            (MeterCountOr 0 m.LoadgenTxnAttempted)
            loadGen.txs

    member self.WaitForLoadGenComplete(loadGen: LoadGen) =
        RetryUntilTrue
            (fun _ -> (self.IsLoadGenComplete() = Success || self.IsLoadGenComplete() = Failure))
            (fun _ ->
                self.LogLoadGenProgressTowards loadGen
                self.LogCoreSetListStatusWithTiers self.networkCfg.CoreSetList)

        if self.IsLoadGenComplete() <> Success then failwith "Loadgen failed!"

    // WaitUntilConnected waits until every node is connected to all the nodes in
    // its preferredPeersMap. This ensures that the simulation is deterministic.
    member self.WaitUntilConnected =
        let desiredNumberOfConnection =
            match self.coreSet.options.preferredPeersMap with
            | None -> failwith "preferredPeersMap is needed to determine # of desired connections"
            | Some map ->
                let preferredPeers = Map.find (self.coreSet.keys.[self.peerNum].PublicKey) map
                List.length preferredPeers

        RetryUntilTrue
            (fun _ -> self.GetInfo().Peers.AuthenticatedCount >= desiredNumberOfConnection)
            (fun _ ->
                LogInfo
                    "Waiting until %s is connected: currently %d connections, want at least %d connections"
                    self.ShortName.StringName
                    (self.GetInfo().Peers.AuthenticatedCount)
                    desiredNumberOfConnection)

    member self.EnsureInSync =
        if self.GetState() <> "Synced!" then
            failwith (sprintf "%s is in state %s" (self.ShortName.StringName) (self.GetState()))

    member self.WaitUntilReady() =
        RetryUntilTrue
            (fun _ -> self.GetState() <> "Booting")
            (fun _ -> LogInfo "Waiting until %s is ready: %s" self.ShortName.StringName (self.GetStatusOrState()))

    member self.WaitUntilSynced() =
        RetryUntilTrue
            (fun _ -> self.GetState() = "Synced!")
            (fun _ -> LogInfo "Waiting until %s is synced: %s" self.ShortName.StringName (self.GetStatusOrState()))

    member self.WaitForAuthenticatedPeers(n: int) =
        RetryUntilTrue
            (fun _ -> self.GetInfo().Peers.AuthenticatedCount >= n)
            (fun _ -> LogInfo "Waiting until %s has >= %d authenticated peers" self.ShortName.StringName n)

let ReportAllPeerStatus (nCfg: NetworkCfg) =
    nCfg.EachPeer
        (fun (p: Peer) ->
            let info = p.GetInfo()
            let metrics = p.GetMetrics()

            LogInfo
                "Peer '%s' startedOn '%s', state '%s', overlay reading %.1f bytes/sec"
                p.ShortName.StringName
                (info.StartedOn.ToString())
                info.State
                metrics.OverlayByteRead.MeanRate)
