// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMissionContext

open k8s

open StellarCoreHTTP
open StellarCorePeer
open StellarDataDump
open StellarDestination
open StellarNetworkCfg
open StellarPerformanceReporter
open StellarPersistentVolume
open StellarSupercluster
open StellarCoreSet

let GetOrDefault optional def =
    match optional with
    | Some(x) -> x
    | _ -> def

type MissionContext =
    { kube : Kubernetes
      destination : Destination
      image : string option
      oldImage : string option
      txRate : int
      maxTxRate : int
      numAccounts : int
      numTxs : int
      numNodes : int
      ingressDomain : string
      persistentVolume : PersistentVolume
      namespaceProperty : string
      keepData : bool
      probeTimeout : int }

    member self.Execute (coreSetList: CoreSet list) (passphrase: NetworkPassphrase option) run =
      let networkCfg = MakeNetworkCfg coreSetList self.namespaceProperty self.ingressDomain passphrase
      use formation = self.kube.MakeFormation networkCfg (Some(self.persistentVolume)) self.keepData self.probeTimeout
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

    member self.ExecuteWithPerformanceReporter (coreSetList: CoreSet list) (passphrase: NetworkPassphrase option) run =
      let networkCfg = MakeNetworkCfg coreSetList self.namespaceProperty self.ingressDomain passphrase
      use formation = self.kube.MakeFormation networkCfg (Some(self.persistentVolume)) self.keepData self.probeTimeout
      let performanceReporter = PerformanceReporter networkCfg
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

    member self.GenerateAccountCreationLoad =
      { mode = GenerateAccountCreationLoad
        accounts = self.numAccounts
        txs = 0
        txrate = self.txRate
        offset = 0
        batchsize = 100 }

    member self.GeneratePaymentLoad =
      { mode = GeneratePaymentLoad
        accounts = self.numAccounts
        txs = self.numTxs
        txrate = self.txRate
        offset = 0
        batchsize = 100 }
