// Copyright 2026 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarOrphanSweep

open System
open k8s
open k8s.Models

open Logging
open ScriptUtils

// Resources that look this old must belong to a failed prior run — no healthy
// mission runs anywhere near this long, and the Jenkins lock serializes CI
// jobs, so anything older than this threshold is presumed orphaned.
let defaultMaxAgeDays = 7

let private isOlderThan (cutoff: DateTime) (meta: V1ObjectMeta) : bool =
    meta.CreationTimestamp.HasValue && meta.CreationTimestamp.Value < cutoff

let private sweepKind
    (kind: string)
    (list: unit -> seq<V1ObjectMeta>)
    (delete: string -> unit)
    (cutoff: DateTime)
    : unit =
    for meta in list () do
        if isOlderThan cutoff meta then
            try
                LogInfo
                    "Orphan sweep: deleting %s/%s (created %s)"
                    kind
                    meta.Name
                    (meta.CreationTimestamp.Value.ToString("o"))

                delete meta.Name
            with ex -> LogWarn "Orphan sweep: failed to delete %s/%s: %s" kind meta.Name ex.Message

// Reap supercluster resources older than maxAgeDays in the given namespace.
//
// This is a best-effort backstop for the rare cases where a mission's normal
// cleanup didn't run (SIGKILL, OOM-kill, runner power loss, mid-Dispose crash).
// It targets the same resource type set the retired `clean` verb did:
//   Service, ConfigMap, StatefulSet, Ingress, Job, DaemonSet, Deployment.
// It also helm-uninstalls stale `parallel-catchup-*` releases (PCv2) so that
// helm's release secrets get tidied along with the workloads.
//
// The age threshold needs to be greater than the longest realistic mission
// runtime so an in-flight run is never reaped. PCv2 has historically taken
// up to ~48 hours; 7 days leaves plenty of margin.
let sweep (kube: Kubernetes) (ns: string) (maxAgeDays: int) : unit =
    let cutoff = DateTime.UtcNow.AddDays(-(float maxAgeDays))
    LogInfo "Orphan sweep starting: namespace=%s cutoff=%s (%d days)" ns (cutoff.ToString("o")) maxAgeDays

    // 1. helm-uninstall PCv2 releases whose StatefulSet is older than the cutoff.
    //    Done before the kubectl-style deletes so helm's release secrets get
    //    cleaned up properly, rather than left dangling pointing at deleted
    //    resources.
    let stsItems = kube.ListNamespacedStatefulSet(namespaceParameter = ns).Items

    for sts in stsItems do
        if isOlderThan cutoff sts.Metadata then
            let name = sts.Metadata.Name

            if name.StartsWith("parallel-catchup-") && name.EndsWith("-stellar-core") then
                let release = name.Substring(0, name.Length - "-stellar-core".Length)
                LogInfo "Orphan sweep: helm uninstall %s" release

                RunShellCommand [| "helm"
                                   "uninstall"
                                   release
                                   "-n"
                                   ns |]
                |> ignore

    // 2. Delete the same resource type set the retired `clean` verb targeted.
    //    The order matches the old NamespaceContent.Cleanup so dependent
    //    resources (e.g. service before statefulset) are removed in a sensible
    //    sequence, but k8s GC handles correctness either way.
    sweepKind
        "Service"
        (fun () ->
            kube.ListNamespacedService(namespaceParameter = ns).Items
            |> Seq.map (fun s -> s.Metadata))
        (fun n ->
            kube.DeleteNamespacedService(namespaceParameter = ns, name = n, propagationPolicy = "Foreground")
            |> ignore)
        cutoff

    sweepKind
        "StatefulSet"
        (fun () -> stsItems |> Seq.map (fun s -> s.Metadata))
        (fun n ->
            kube.DeleteNamespacedStatefulSet(namespaceParameter = ns, name = n, propagationPolicy = "Foreground")
            |> ignore)
        cutoff

    sweepKind
        "Deployment"
        (fun () ->
            kube.ListNamespacedDeployment(namespaceParameter = ns).Items
            |> Seq.map (fun d -> d.Metadata))
        (fun n ->
            kube.DeleteNamespacedDeployment(namespaceParameter = ns, name = n, propagationPolicy = "Foreground")
            |> ignore)
        cutoff

    sweepKind
        "ConfigMap"
        (fun () ->
            kube.ListNamespacedConfigMap(namespaceParameter = ns).Items
            |> Seq.map (fun c -> c.Metadata))
        (fun n ->
            kube.DeleteNamespacedConfigMap(namespaceParameter = ns, name = n, propagationPolicy = "Foreground")
            |> ignore)
        cutoff

    sweepKind
        "Ingress"
        (fun () ->
            kube.ListNamespacedIngress(namespaceParameter = ns).Items
            |> Seq.map (fun i -> i.Metadata))
        (fun n ->
            kube.DeleteNamespacedIngress(namespaceParameter = ns, name = n, propagationPolicy = "Foreground")
            |> ignore)
        cutoff

    sweepKind
        "Job"
        (fun () ->
            kube.ListNamespacedJob(namespaceParameter = ns).Items
            |> Seq.map (fun j -> j.Metadata))
        (fun n ->
            kube.DeleteNamespacedJob(namespaceParameter = ns, name = n, propagationPolicy = "Foreground")
            |> ignore)
        cutoff

    sweepKind
        "DaemonSet"
        (fun () ->
            kube.ListNamespacedDaemonSet(namespaceParameter = ns).Items
            |> Seq.map (fun d -> d.Metadata))
        (fun n ->
            kube.DeleteNamespacedDaemonSet(namespaceParameter = ns, name = n, propagationPolicy = "Foreground")
            |> ignore)
        cutoff

    LogInfo "Orphan sweep complete"
