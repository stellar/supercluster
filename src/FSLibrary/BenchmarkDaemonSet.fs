// Copyright 2025 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module BenchmarkDaemonSet

open k8s.Models
open System
open System.Collections.Generic
open ScriptUtils
open StellarKubeSpecs
open StellarNetworkCfg
open StellarNetworkDelays
open StellarMissionContext

// Docker image for iperf3 - a tool for measuring network throughput and latency
// We use it to run bidirectional P2P network performance tests between stellar-core nodes
let benchmarkImage = "networkstatic/iperf3:latest"

let benchmarkLabels = Map.ofSeq [ "app", "network-benchmark" ]

// Extract node name from benchmark pod name (format: {runId}-benchmark-{shortName}-0)
let extractNodeNameFromBenchmarkPod (podName: string) (topology: Map<string, string array>) : string =
    let parts = podName.Split('-')
    // Remove StatefulSet replica suffix if present
    let podNameWithoutReplica =
        if parts.Length > 0 && parts.[parts.Length - 1] = "0" then
            String.Join("-", parts.[0..parts.Length - 2])
        else
            podName

    match podNameWithoutReplica.IndexOf("-benchmark-") with
    | -1 -> podName
    | idx ->
        let shortName = podNameWithoutReplica.Substring(idx + 11)

        topology
        |> Map.tryFindKey (fun fullName _ -> fullName = shortName)
        |> Option.defaultValue podName

// Headless service for benchmark pod DNS resolution
let createBenchmarkHeadlessService (nCfg: NetworkCfg) (runId: string) : V1Service =
    V1Service(
        metadata =
            V1ObjectMeta(
                name = sprintf "%s-benchmark" runId,
                labels = benchmarkLabels,
                namespaceProperty = nCfg.NamespaceProperty
            ),
        spec =
            V1ServiceSpec(
                clusterIP = "None",
                selector = benchmarkLabels,
                ports = [| V1ServicePort(name = "iperf3", port = 5201, protocol = "TCP") |]
            )
    )

// Create StatefulSet for benchmark with the same topology as stellar-core sts
//
// Network Topology and Port Management:
// ======================================
// There is a benchmark pod per stellar-core pod, which contains a server and client
// container. Since the network is p2p, each test will generate bidirectional load,
// but there is still a "server" that need to do all the accounting work. To distribute this,
// for each peer connection we randomize which node will be the server vs. client. This means
// that for each peer connection, a pod will have either a server or client instance.
//
// Each pod has one server container and one client container, but they hold several
// instances of each. To avoid port conflicts, we give each pod a unique index
// and manage connections as follows:
//
// 1. SERVER CONTAINER:
//    - Exposes ports for all nodes (otherwise DNS won't work)
//    - Only starts iperf3 servers for peers that will connect to us
//    - Server on port (5201 + i) accepts connections from the client peer with index i
//    - All servers run concurrently in the background
//
// 2. CLIENT CONTAINER:
//    - Starts a client instance per server peer
//    - Each node's client uses its own pod index to connect to peer's server
//
// CONNECTION LOGIC:
// - Each pod has a unique global index
// - Each client connects to port (5201 + its_own_index) on all servers
// - Only one node in each connection initiates a test as the server (determined by hash)
//
// NETWORK DELAYS AND DNS:
// - Network delay container applies tc rules based on destination IPs
// - All ports must be exposed in container spec for proper DNS/endpoint creation
let createBenchmarkStatefulSet
    (nCfg: NetworkCfg)
    (runId: string)
    (coreSetName: string)
    (nodeName: string)
    (peers: string array)
    (sourcePeers: string array)
    (globalNodeIndex: Map<string, int>)
    (duration: int)
    (coreSet: StellarCoreSet.CoreSet)
    (nodeIndex: int)
    : V1StatefulSet =

    let shortName = nodeName
    let stsName = sprintf "%s-benchmark-%s" runId shortName

    // Pod template - contains:
    // 1. Server container: multiple instances of iperf3 servers
    // 2. Client container: multiple instances of iperf3 clients
    //    - Note: number of Server + Client instances = number of peers
    // 3. Network delay container (optional): applies geographic latency simulation via tc
    let podTemplate =
        let podMetadata =
            V1ObjectMeta(
                labels = Map.add "coreset" coreSetName benchmarkLabels,
                annotations = Map.ofSeq [ "nodeIndex", nodeIndex.ToString() ]
            )

        // Server container
        let serverContainer =
            // We must expose all ports for Kubernetes service endpoint creation
            let allPorts =
                globalNodeIndex
                |> Map.toArray
                |> Array.map (fun (_, idx) -> V1ContainerPort(containerPort = 5201 + idx, protocol = "TCP"))

            // Only start servers for peers that will actually connect to us
            let serverCommands =
                sourcePeers
                |> Array.choose (fun peerName -> Map.tryFind peerName globalNodeIndex)
                |> Array.map (fun idx -> sprintf "iperf3 -s -p %d &" (5201 + idx))
                |> String.concat "\n"

            V1Container(
                name = "server",
                image = benchmarkImage,
                command = [| "/bin/sh" |],
                args = [| "-c"; serverCommands + "\nwait" |],
                ports = allPorts,
                resources = GetCoreResourceRequirements nCfg.missionContext.coreResources
            )

        // Client container
        let clientContainer =
            let nodeIdx = Map.find nodeName globalNodeIndex

            // Transform stellar-core DNS names to benchmark pod short names
            let benchmarkPeers =
                peers
                |> Array.map
                    (fun peerDns ->
                        // Extract the pod name from the DNS (everything before first dot)
                        let podName = peerDns.Split('.').[0]
                        podName)

            let peerList = String.Join(",", benchmarkPeers)

            let clientScriptTemplate =
                let scriptPath = GetScriptPath "start-benchmark-client.sh"
                System.IO.File.ReadAllText(scriptPath)

            let clientScript =
                clientScriptTemplate
                    .Replace("NODE_PLACEHOLDER", nodeName)
                    .Replace("PEERS_PLACEHOLDER", peerList)
                    .Replace("NAMESPACE_PLACEHOLDER", nCfg.NamespaceProperty)
                    .Replace("DURATION_PLACEHOLDER", duration.ToString())
                    .Replace("INDEX_PLACEHOLDER", nodeIdx.ToString())
                    .Replace("RUN_ID_PLACEHOLDER", runId)
                    .Replace("HEADLESS_SERVICE_PLACEHOLDER", sprintf "%s-benchmark" runId)

            // Client script spins up instance per peer server
            V1Container(
                name = "client",
                image = benchmarkImage,
                command = [| "/bin/bash" |],
                args = [| "-c"; clientScript |],
                volumeMounts = [| V1VolumeMount(name = "results", mountPath = "/results") |],
                resources = GetCoreResourceRequirements nCfg.missionContext.coreResources
            )

        // Network delay container if needed
        let containers =
            if nCfg.NeedNetworkDelayScript then
                let baseDelayScript = (nCfg.NetworkDelayScript coreSet nodeIndex).ToString()

                // Use the same script to install delays as we do for stellar-core pods, but swap out the DNS patterns
                // for the benchmark pods' DNS names.
                let delayScript =
                    let mutable modifiedScript = baseDelayScript

                    // Pattern captures both the coreset name and the node name
                    let pattern =
                        @"(ssc-[a-z0-9]+-[a-z0-9]+-sts)-([a-z0-9\-]+)\.ssc-[a-z0-9]+-[a-z0-9]+-stellar-core\.[a-z0-9\-]+\.svc\.cluster\.local"

                    // Replace stellar-core DNS names with benchmark pod DNS names
                    // From: ssc-2128z-941b4e-sts-node-3-0.ssc-2128z-941b4e-stellar-core.namespace.svc.cluster.local
                    // To:   ssc-613dz-benchmark-ssc-2128z-941b4e-sts-node-3-0-0.ssc-613dz-benchmark.namespace.svc.cluster.local
                    modifiedScript <-
                        System.Text.RegularExpressions.Regex.Replace(
                            modifiedScript,
                            pattern,
                            fun m ->
                                let coreSetPrefix = m.Groups.[1].Value
                                let nodeName = m.Groups.[2].Value

                                // Create the full benchmark pod DNS name
                                sprintf
                                    "%s-benchmark-%s-%s-0.%s-benchmark.%s.svc.cluster.local"
                                    runId
                                    coreSetPrefix
                                    nodeName
                                    runId
                                    nCfg.NamespaceProperty
                        )

                    modifiedScript

                let netdelayContainer =
                    V1Container(
                        name = "netdelay",
                        image = nCfg.missionContext.netdelayImage,
                        command = [| "sh" |],
                        args = [| "-c"; delayScript |],
                        resources = SmallTestCoreResourceRequirements,
                        securityContext = V1SecurityContext(capabilities = V1Capabilities(add = [| "NET_ADMIN" |]))
                    )

                [| serverContainer; clientContainer; netdelayContainer |]
            else
                [| serverContainer; clientContainer |]

        let volumes = [| V1Volume(name = "results", emptyDir = V1EmptyDirVolumeSource()) |]

        V1PodTemplateSpec(
            metadata = podMetadata,
            spec =
                V1PodSpec(
                    containers = containers,
                    volumes = volumes,
                    shareProcessNamespace = System.Nullable<bool>(true)
                )
        )

    let statefulSetSpec =
        V1StatefulSetSpec(
            selector = V1LabelSelector(matchLabels = Map.add "coreset" coreSetName benchmarkLabels),
            serviceName = sprintf "%s-benchmark" runId, // Points to the headless service
            podManagementPolicy = "Parallel",
            template = podTemplate,
            replicas = System.Nullable<int>(1)
        )

    V1StatefulSet(
        metadata = V1ObjectMeta(name = stsName, labels = benchmarkLabels, namespaceProperty = nCfg.NamespaceProperty),
        spec = statefulSetSpec
    )

// ConfigMap containing TCP tuning script
let createTcpTuningConfigMap (nCfg: NetworkCfg) : V1ConfigMap =
    let scriptPath = GetScriptPath "tcp-tune.sh"

    V1ConfigMap(
        metadata = V1ObjectMeta(name = "tcp-tuning-script", namespaceProperty = nCfg.NamespaceProperty),
        data = dict [ ("tcp-tune.sh", System.IO.File.ReadAllText(scriptPath)) ]
    )

let private createTcpDaemonSet
    (name: string)
    (scriptName: string)
    (labels: IDictionary<string, string>)
    (nCfg: NetworkCfg)
    =
    V1DaemonSet(
        metadata = V1ObjectMeta(name = name, labels = labels, namespaceProperty = nCfg.NamespaceProperty),
        spec =
            V1DaemonSetSpec(
                selector = V1LabelSelector(matchLabels = dict [ ("app", name) ]),
                template =
                    V1PodTemplateSpec(
                        metadata = V1ObjectMeta(labels = dict [ ("app", name) ]),
                        spec =
                            V1PodSpec(
                                hostNetwork = System.Nullable<bool>(true),
                                hostPID = System.Nullable<bool>(true),
                                containers =
                                    [| V1Container(
                                           name = name,
                                           image = "busybox:latest",
                                           command = [| "/bin/sh" |],
                                           args = [| sprintf "/scripts/%s" scriptName; "--daemon" |],
                                           volumeMounts =
                                               [| V1VolumeMount(
                                                      name = "tcp-tuning-script",
                                                      mountPath = "/scripts",
                                                      readOnlyProperty = System.Nullable<bool>(true)
                                                  ) |],
                                           securityContext =
                                               V1SecurityContext(
                                                   privileged = System.Nullable<bool>(true),
                                                   capabilities = V1Capabilities(add = [| "SYS_ADMIN"; "NET_ADMIN" |])
                                               ),
                                           resources =
                                               V1ResourceRequirements(
                                                   requests =
                                                       dict [ ("memory", ResourceQuantity("50Mi"))
                                                              ("cpu", ResourceQuantity("10m")) ],
                                                   limits =
                                                       dict [ ("memory", ResourceQuantity("100Mi"))
                                                              ("cpu", ResourceQuantity("50m")) ]
                                               )
                                       ) |],
                                volumes =
                                    [| V1Volume(
                                           name = "tcp-tuning-script",
                                           configMap =
                                               V1ConfigMapVolumeSource(
                                                   name = "tcp-tuning-script",
                                                   defaultMode = System.Nullable<int>(0o755)
                                               )
                                       ) |],
                                tolerations = [| V1Toleration(operatorProperty = "Exists") |]
                            )
                    )
            )
    )

// DaemonSet to apply TCP performance tuning
let createTcpTuningDaemonSet (nCfg: NetworkCfg) : V1DaemonSet =
    createTcpDaemonSet
        "tcp-tuning"
        "tcp-tune.sh"
        (dict [ ("app", "tcp-tuning")
                ("component", "network-optimization") ])
        nCfg
