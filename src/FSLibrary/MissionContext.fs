// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMissionContext

open k8s

open StellarCoreHTTP
open StellarDataDump
open StellarDestination
open StellarNetworkCfg
open StellarPerformanceReporter
open StellarPersistentVolume
open StellarSupercluster

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
      ingressPort : int
      persistentVolume : PersistentVolume
      keepData : bool
      probeTimeout : int }

    member self.Execute coreSets passprhase run =
      let networkCfg = MakeNetworkCfg(coreSets, self.ingressPort, passprhase)
      use f = self.kube.MakeFormation networkCfg (Some(self.persistentVolume)) self.keepData self.probeTimeout
      try
          try
              f.WaitUntilReady
              run f
              f.CheckNoErrorsAndPairwiseConsistency
          finally
             f.DumpData self.destination
      with
      | x -> (
                if self.keepData then f.KeepData
                reraise()
             )

    member self.ExecuteWithPerformanceReporter coreSets passprhase run =
      let networkCfg = MakeNetworkCfg(coreSets, self.ingressPort, passprhase)
      use f = self.kube.MakeFormation networkCfg (Some(self.persistentVolume)) self.keepData self.probeTimeout
      let pr = PerformanceReporter networkCfg
      try
          try
              f.WaitUntilReady
              run f pr
              f.CheckNoErrorsAndPairwiseConsistency
          finally
              pr.DumpPerformanceMetrics self.destination
              f.DumpData self.destination
      with
      | x -> (
                if self.keepData then f.KeepData
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
