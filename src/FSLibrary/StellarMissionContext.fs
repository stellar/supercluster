// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarMissionContext

open k8s
open StellarDestination

let GetOrDefault optional def =
    match optional with
    | Some(x) -> x
    | _ -> def

type LogLevels =
    { LogDebugPartitions: string list
      LogTracePartitions: string list }

type CoreResources =
    SmallTestResources
    | AcceptanceTestResources
    | SimulatePubnetResources
    | ParallelCatchupResources

// Resource allocation in k8s is quite complex.
//
// The namespace itself has a _quota_ which applies to the sum of all
// resources allocated within the namespace, so everything at the
// namespace quota level has to be divided by the number of containers
// we're going to instantiate.
//
// That quota in turn consists of a request quota and a limit quota for
// each resource type (CPU and memory), to which the following rules
// apply:
//
//   - Limits and requests of a container must fit within quota
//     (a container won't be scheduled if _either_ exceeds quota)
//   - Requests that can be met are provided by guaranteed resources
//   - Limits can be overprovisioned and are best-effort
//   - Limits must always be greater than requests
//   - A container can never use more than limit, it's a hard boundary
//
// Furthermore there is a container-level maximum for each resource
// that's _independent_ of the number of containers or the namespace's
// remaining quota.
//
// So we want to set limit as high as possible and request as low as
// possible, within the constraints of these quotas and maximums.
//
// Or we _would_ if it weren't for a further bug. Or maybe-bug. Maybe
// feature. People disagree on this point. See:
//
// https://github.com/kubernetes/kubernetes/issues/43916
//
// TL;DR: there is a kubernetes / kubelet view of "memory pressure"
// that is subtly and significantly different than the kernel's own
// idea of "memory pressure".
//
// In more detail: the kubelet conveys the memory limit to the kernel
// as a cgroup memory limit, and the kernel is then willing to fill
// up all the memory in that limit with page cache entries because
// why not? They're discardable any time the kernel feels it's under
// actual memory pressure. The kernel is working as designed.
//
// But the kubelet _also_ measures "memory usage" of the pods it has
// admitted and evicts them on the basis of that "memory usage" being
// too high for the amount of memory it has; and the memory usage it
// measures is the "working set" (not RSS) of the processes in the pod.
// The working set _includes_ the page cache entries that the kernel
// is holding on to, so even if the kernel _would_ free them all under
// pressure, it's not given an opportunity to: the kubelet will
// prematurely evict (or fail to admit) a pod on the basis of its flawed
// idea of being under memory pressure.
//
// The only possibly defensible explanation for this is that the kubelet
// wants to be absolutely 100% certain it will never ever experience
// the kernel's response to memory pressure, because that would be
// _potentially_ bad, like the kernel _could_ find itself unable to
// free cached pages fast enough and start thrashing, or something. It's
// pretty hazy why this was chosen, and it might be a bug. It might also
// be that the authors never notice it because they mostly run low-IO
// workloads like web servers.
//
// But in the meantime, the upshot is if we set _memory_ limits high,
// an IO-intensive pod will (to kubernetes' flawed perspective) use
// up the entirety of (or a lot of) its limit and thereby cause cluster
// wide memory pressure, possibly evicting or blocking new pods.
//
// The "fix" / workaround is to set limit to a level that we actually
// think is a reasonable RSS limit for a given stellar-core container,
// and when the page cache exceeds that limit it will start dropping
// pages rather than expanding further.

type NetworkQuotas =
    { ContainerMaxCpuMili: int
      ContainerMaxMemMebi: int
      NamespaceQuotaLimCpuMili: int
      NamespaceQuotaLimMemMebi: int
      NamespaceQuotaReqCpuMili: int
      NamespaceQuotaReqMemMebi: int
      NumConcurrentMissions: int }

    override self.ToString () =
        sprintf "max [cpu:%d, mem:%d] lim [cpu:%d, mem:%d] req [cpu:%d, mem:%d] missions %d"
            self.ContainerMaxCpuMili self.ContainerMaxMemMebi
            self.NamespaceQuotaLimCpuMili self.NamespaceQuotaLimMemMebi
            self.NamespaceQuotaReqCpuMili self.NamespaceQuotaReqMemMebi
            self.NumConcurrentMissions

    member self.AdjustedToCompensateForKubeletMemoryPressureBug(numContainers:int) : NetworkQuotas =
        // Adjust the quotas structure to work around the bug mentioned in the
        // comment above, https://github.com/kubernetes/kubernetes/issues/43916
        //
        // The largest memory-usage we see in practice is about 300mb,
        // running core under address sanitizer with
        // ASAN_OPTIONS=quarantine_size_mb=1:malloc_context_size=5
        // We give it a little more room here just in case.
        //
        // Or rather, we _would_ just use this fixed limit if it weren't for
        // yet _another_ countervailing consideration, which is that some of our
        // jobs run a relatively small number of containers but need each to
        // use as much memory as they can (eg. the acceptance unit tests);
        // so in the case of a small number of containers we _don't_ adjust
        // the limit downwards.
        if numContainers < 10
        then self
        else
            let practicalFixedLimMemMebi = 350
            { self with
                ContainerMaxMemMebi = practicalFixedLimMemMebi }

    member self.ContainerCpuReqMili (numContainers:int) : int =
        let divisor = numContainers * self.NumConcurrentMissions
        let nsFrac = self.NamespaceQuotaReqCpuMili / divisor
        let lim = (self.ContainerCpuLimMili numContainers)
        min 100 (min nsFrac lim)

    member self.ContainerCpuLimMili (numContainers:int) : int =
       let divisor = numContainers * self.NumConcurrentMissions
       let nsFrac = self.NamespaceQuotaLimCpuMili / divisor
       min nsFrac self.ContainerMaxCpuMili

    member self.ContainerMemReqMebi (numContainers:int) : int =
        let divisor = numContainers * self.NumConcurrentMissions
        let nsFrac = self.NamespaceQuotaReqMemMebi / divisor
        let lim = (self.ContainerMemLimMebi numContainers)
        min 100 (min nsFrac lim)

    member self.ContainerMemLimMebi (numContainers:int) : int =
        let divisor = numContainers * self.NumConcurrentMissions
        let nsFrac = self.NamespaceQuotaLimMemMebi / divisor
        min nsFrac self.ContainerMaxMemMebi


let MakeNetworkQuotas (containerMaxCpuMili: int,
                       containerMaxMemMebi: int,
                       namespaceQuotaLimCpuMili: int,
                       namespaceQuotaLimMemMebi: int,
                       namespaceQuotaReqCpuMili: int,
                       namespaceQuotaReqMemMebi: int,
                       numConcurrentMissions: int) =
    { ContainerMaxCpuMili = containerMaxCpuMili
      ContainerMaxMemMebi = containerMaxMemMebi
      NamespaceQuotaLimCpuMili = namespaceQuotaLimCpuMili
      NamespaceQuotaLimMemMebi = namespaceQuotaLimMemMebi
      NamespaceQuotaReqCpuMili = namespaceQuotaReqCpuMili
      NamespaceQuotaReqMemMebi = namespaceQuotaReqMemMebi
      NumConcurrentMissions = numConcurrentMissions }

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
      namespaceProperty : string
      quotas: NetworkQuotas
      logLevels: LogLevels
      ingressDomain : string
      exportToPrometheus : bool
      probeTimeout : int
      coreResources : CoreResources
      keepData : bool
      apiRateLimit: int }
