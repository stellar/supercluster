// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMission

open Logging
open StellarNetworkCfg
open StellarCorePeer
open StellarCoreHTTP
open StellarTransaction
open StellarSupercluster

open k8s

type Mission = (Kubernetes -> unit)

let simplePayment (kube:Kubernetes) =
    use f = kube.MakeFormation (MakeNetworkCfg 3)
    f.CreateAccount UserAlice
    f.CreateAccount UserBob
    f.Pay UserAlice UserBob
    f.CheckNoErrorsAndPairwiseConsistency()

let allMissions : Map<string, Mission> =
    Map.ofSeq [|
        ("SimplePayment", simplePayment)
    |]
