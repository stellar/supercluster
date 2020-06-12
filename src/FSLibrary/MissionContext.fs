// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMissionContext

open k8s
open k8s.Models

open StellarCoreHTTP
open StellarCorePeer
open StellarDataDump
open StellarDestination
open StellarNetworkCfg
open StellarPerformanceReporter
open StellarFormation
open StellarSupercluster
open StellarCoreSet

let GetOrDefault optional def =
    match optional with
    | Some(x) -> x
    | _ -> def

type MissionContext =
    { kube : Kubernetes
      destination : Destination
      image : string
      oldImage : string option
      txRate : int
      maxTxRate : int
      numAccounts : int
      numTxs : int
      spikeSize : int
      spikeInterval : int
      numNodes : int
      quotas: NetworkQuotas
      logLevels: LogLevels
      ingressDomain : string
      exportToPrometheus : bool
      storageClass : string
      namespaceProperty : string
      keepData : bool
      probeTimeout : int }

    member self.MakeFormation (coreSetList: CoreSet list) (passphrase: NetworkPassphrase option) : StellarFormation =
        let networkCfg =
            MakeNetworkCfg coreSetList
                self.namespaceProperty
                self.quotas
                self.logLevels
                self.storageClass
                self.ingressDomain
                self.exportToPrometheus
                passphrase
        self.kube.MakeFormation networkCfg self.keepData self.probeTimeout

    member self.MakeFormationForJob (opts:CoreSetOptions option) (passphrase: NetworkPassphrase option) : StellarFormation =
        let networkCfg =
            MakeNetworkCfg []
                self.namespaceProperty
                self.quotas
                self.logLevels
                self.storageClass
                self.ingressDomain
                self.exportToPrometheus
                passphrase
        let networkCfg = { networkCfg with jobCoreSetOptions = opts }
        self.kube.MakeFormation networkCfg self.keepData self.probeTimeout

    member self.ExecuteJobs (opts:CoreSetOptions option)
                            (passphrase:NetworkPassphrase option)
                            (run:StellarFormation->unit) =
      use formation = self.MakeFormationForJob opts passphrase
      try
          try
              run formation
          finally
              formation.DumpJobData self.destination
      with
      | x -> (if self.keepData then formation.KeepData()
              reraise())

    member self.Execute (coreSetList: CoreSet list) (passphrase: NetworkPassphrase option) (run:StellarFormation->unit) : unit =
      use formation = self.MakeFormation coreSetList passphrase
      try
          try
              formation.WaitUntilReady()
              run formation
              formation.CheckNoErrorsAndPairwiseConsistency()
          finally
             formation.DumpData self.destination
      with
      | x -> (
                if self.keepData then formation.KeepData()
                reraise()
             )

    member self.ExecuteWithPerformanceReporter
            (coreSetList: CoreSet list) (passphrase: NetworkPassphrase option)
            (run:StellarFormation->PerformanceReporter->unit) : unit =
      use formation = self.MakeFormation coreSetList passphrase
      let performanceReporter = PerformanceReporter formation.NetworkCfg
      try
          try
              formation.WaitUntilReady()
              run formation performanceReporter
              formation.CheckNoErrorsAndPairwiseConsistency()
          finally
              performanceReporter.DumpPerformanceMetrics self.destination
              formation.DumpData self.destination
      with
      | x -> (
                if self.keepData then formation.KeepData()
                reraise()
             )

    member self.WithNominalLoad : MissionContext =
      { self with numTxs = 100; numAccounts = 100 }

    member self.GenerateAccountCreationLoad : LoadGen =
      { mode = GenerateAccountCreationLoad
        accounts = self.numAccounts
        txs = 0
        spikesize = 0
        spikeinterval = 0
        // Use conservative rate for account creation, as the network may quickly get overloaded
        txrate = 5
        offset = 0
        batchsize = 100 }

    member self.GeneratePaymentLoad : LoadGen=
      { mode = GeneratePaymentLoad
        accounts = self.numAccounts
        txs = self.numTxs
        txrate = self.txRate
        spikesize = self.spikeSize
        spikeinterval = self.spikeInterval
        offset = 0
        batchsize = 100 }
