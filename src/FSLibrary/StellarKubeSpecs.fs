// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarKubeSpecs

open k8s.Models

open StellarNetworkCfg
open StellarCoreCfg
open StellarCoreSet
open StellarPersistentVolume

let HistoryContainer =
    V1Container
        (name = "history",
         image = "nginx",
         command = [| "nginx" |],
         args = [| "-c"; CfgVal.historyCfgFile; |],
         volumeMounts = [| V1VolumeMount(name = CfgVal.dataVolumeName,
                                         mountPath = CfgVal.dataVolumePath)
                           V1VolumeMount(name = CfgVal.cfgVolumeName,
                                         mountPath = CfgVal.cfgVolumePath) |])

let SqliteContainer =
    V1Container
        (name = "sqlite",
         image = "dockerpinata/sqlite",
         command = [| "sleep" |],
         args = [|"356d"|],
         volumeMounts = [| V1VolumeMount(name = CfgVal.dataVolumeName,
                                         mountPath = CfgVal.dataVolumePath)|])

// Returns a k8s V1Container object using the stellar-core image, and running
// stellar-core with the specified sub-command(s) or arguments, passing a final
// argument pair "--conf /cfg/$(STELLAR_CORE_PEER_SHORT_NAME).cfg". This is run
// in an _environment_ that has STELLAR_CORE_PEER_SHORT_NAME set to the Pod's
// name, via a fieldRef env var referencing the Pod's "metadata.name" field. For
// example, on Pod peer-1, stellar-core will run with --conf /cfg/peer-1.cfg.
//
// This is admittedly a bit convoluted, but it's the simplest mechanism I could
// figure out to furnish each stellar-core with a Pod-specific config file.
let CoreContainerForCommand image (subCommands:string array) : V1Container =
    let peerNameFieldSel = V1ObjectFieldSelector(fieldPath = "metadata.name")
    let peerNameEnvVarSource = V1EnvVarSource(fieldRef = peerNameFieldSel)
    let peerNameEnvVar = V1EnvVar(name = CfgVal.peerNameEnvVarName,
                                  valueFrom = peerNameEnvVarSource)
    V1Container
        (name = "stellar-core-" + (Array.get subCommands 0),
         image = image,
         command = [| CfgVal.stellarCoreBinPath |], env = [| peerNameEnvVar |],
         args = Array.append subCommands [| "--conf"; CfgVal.peerNameEnvCfgFile |],
         volumeMounts = [| V1VolumeMount(name = CfgVal.dataVolumeName,
                                         mountPath = CfgVal.dataVolumePath)
                           V1VolumeMount(name = CfgVal.cfgVolumeName,
                                         mountPath = CfgVal.cfgVolumePath) |])


let WithReadinessProbe (container:V1Container) probeTimeout : V1Container =
    let httpPortStr = IntstrIntOrString(value = CfgVal.httpPort.ToString())
    let readyProbe = V1Probe(periodSeconds = System.Nullable<int>(5),
                             initialDelaySeconds = System.Nullable<int>(5),
                             timeoutSeconds = System.Nullable<int>(probeTimeout),
                             httpGet = V1HTTPGetAction(path = "/info",
                                                       port = httpPortStr))
    container.ReadinessProbe <- readyProbe
    container


// Extend NetworkCfg type with methods for producing various Kubernetes objects.
type NetworkCfg with

    member self.Namespace : V1Namespace =
        V1Namespace(spec = V1NamespaceSpec(),
                    metadata = V1ObjectMeta(name = self.NamespaceProperty))

    member self.NamespacedMeta (name:string) : V1ObjectMeta =
        V1ObjectMeta(name = name, labels = CfgVal.labels,
                     namespaceProperty = self.NamespaceProperty)

    member self.HistoryConfig() =
        ("nginx.conf", sprintf "
          error_log /var/log/nginx.error_log debug;\n
          daemon off;
          user root root;
          events {}\n
          http {\n
            server {\n
              autoindex on;\n
              listen 80;\n
              root %s;\n
            }\n
          }" CfgVal.historyPath)

    // Returns a ConfigMap dictionary that names "peer-0.cfg".."peer-N.cfg" to
    // TOML config file data for each such config. Mounting this ConfigMap on a
    // Pod will provide it access to _all_ the configs in the network, but it
    // only needs to figure out its own name to pick the file that contains its
    // config. This arrangement is a bit crude but makes it relatively easy to
    // get a node's config to it, and to browse the configuration of the entire
    // network eg. in the k8s dashboard.
    member self.ToConfigMap() : V1ConfigMap =
        let peerKeyValPair (coreSet: CoreSet) i = (CfgVal.peerCfgName coreSet i, (self.StellarCoreCfg (coreSet, i)).ToString())
        let cfgs = Array.append (self.MapAllPeers peerKeyValPair) [| self.HistoryConfig() |]
        V1ConfigMap(metadata = V1ObjectMeta(name = CfgVal.cfgMapName,
                                            namespaceProperty = self.NamespaceProperty),
                    data = Map.ofSeq cfgs)

    // Returns a PodTemplate that mounts the ConfigMap on /cfg and an empty data
    // volume on /data. Then initializes a local stellar-core database in
    // /data/stellar.db with buckets in /data/buckets and history archive in
    // /data/history, forces SCP on next startup, and runs.
    member self.ToPodTemplateSpec (coreSet: CoreSet) probeTimeout : V1PodTemplateSpec =
        let cfgVol = V1Volume(name = CfgVal.cfgVolumeName,
                              configMap = V1ConfigMapVolumeSource(name = CfgVal.cfgMapName))
        let dataVol = 
            match coreSet.options.persistentVolume with
            | None -> V1Volume(name = CfgVal.dataVolumeName,
                               emptyDir = V1EmptyDirVolumeSource())
            | Some(x) ->
                let claim = V1PersistentVolumeClaimVolumeSource(claimName = CfgVal.peristentVolumeClaimName self.NamespaceProperty x)
                V1Volume(name = CfgVal.dataVolumeName,
                         persistentVolumeClaim = claim)

        let imageName =
            match coreSet.options.image with
            | None -> CfgVal.stellarCoreImageName
            | Some(x) -> x

        let newDb i = if i.newDb then [| [| "new-db" |] |] else [||]
        let newHist i = if i.newHist then [| [| "new-hist"; CfgVal.localHistName |] |] else [||]
        let initialCatchup i = if i.initialCatchup then [| [| "catchup"; "current/0" |] |] else [||]
        let forceScp i = if i.forceScp then [| [| "force-scp" |] |] else [||]

        let initSequence (i : CoreSetInitialization) =
            Array.concat [ newDb i; newHist i; initialCatchup i; forceScp i ]


        let volumes = [| cfgVol; dataVol |]
        let initContianers = Array.map (fun p -> CoreContainerForCommand imageName p) (initSequence coreSet.options.initialization)

        let containers = [| WithReadinessProbe (CoreContainerForCommand imageName [| "run" |]) probeTimeout;
                            HistoryContainer; SqliteContainer |]

        let podSpec =
            V1PodSpec
                (initContainers = initContianers,
                 containers = containers,
                 volumes = volumes)
        V1PodTemplateSpec
                (spec = podSpec,
                 metadata = V1ObjectMeta(labels = CfgVal.labels,
                                         namespaceProperty = self.NamespaceProperty))

    // Returns a single "headless" (clusterIP=None) Service with the same name
    // as the StatefulSet. This is necessary to coax the DNS service to register
    // local DNS names for each of the Pod names in the StatefulSet (that we
    // then use to connect the peers to one another in their config files, and
    // hook the per-Pod Services and Ingress up to). Getting all this to work
    // requires that you install the DNS server component on your k8s cluster.
    member self.ToService () : V1Service =
        let serviceSpec = V1ServiceSpec(clusterIP = "None", selector = CfgVal.labels)
        V1Service(spec = serviceSpec,
                  metadata = self.NamespacedMeta CfgVal.serviceName)


    // Returns a StatefulSet object that will build stellar-core Pods named
    // peer-0 .. peer-N, and bind them to the generic "peer" Service above.
    member self.ToStatefulSet (coreSet: CoreSet) probeTimeout : V1StatefulSet =
        let statefulSetSpec =
            V1StatefulSetSpec
                (selector = V1LabelSelector(matchLabels = CfgVal.labels),
                 serviceName = CfgVal.serviceName,
                 template = self.ToPodTemplateSpec coreSet probeTimeout,
                 replicas = System.Nullable<int>(coreSet.CurrentCount))
        let statefulSet = V1StatefulSet(metadata = self.NamespacedMeta (CfgVal.peerSetName coreSet),
                                        spec = statefulSetSpec)
        statefulSet.Validate() |> ignore
        statefulSet


    // Returns an array of "per-Pod" Service objects, each named according to
    // the peer-N short names, and mapping (via a somewhat hacky misuse of the
    // ExternalName Service type -- thanks internet!) to the _internal_ DNS
    // names of each pod.
    //
    // This exists strictly to support the Ingress object below, that routes
    // separate URL prefixes to separate Pods (which is somewhat the opposite of
    // the load-balancing task Services, Pods, and Ingress systems typically
    // do).
    member self.ToPerPodServices () : V1Service array =
        let perPodService (coreSet: CoreSet) i =
            let name = CfgVal.peerShortName coreSet i
            let dnsName = CfgVal.peerDNSName self.namespaceProperty coreSet i
            let spec = V1ServiceSpec(``type`` = "ExternalName",
                                     ports =
                                      [|V1ServicePort(name = "core",
                                                      port = CfgVal.httpPort);
                                        V1ServicePort(name = "history",
                                                      port = 80)|],
                                     externalName = dnsName)
            V1Service(metadata = self.NamespacedMeta name, spec = spec)
        self.MapAllPeers perPodService

    member self.ToCoreSetPersistentVolumeNames () =
        let withPersistentVolumes = self.coreSetList |> List.filter (fun coreSet -> coreSet.options.persistentVolume <> None)
        let getName (coreSet: CoreSet) =
            coreSet.options.persistentVolume.Value
        let names = List.map getName withPersistentVolumes
        names |> List.sort |> List.distinct

    member self.ToPersistentVolumeNames () =
        let getFullName name =
            CfgVal.peristentVolumeName self.NamespaceProperty name
        List.map getFullName (self.ToCoreSetPersistentVolumeNames())

    member self.ToPersistentVolumes (pv: PersistentVolume) =
        let makePersistentVolume name =
            let volumeName = CfgVal.peristentVolumeName self.NamespaceProperty name
            let meta = V1ObjectMeta(name = volumeName)
            let accessModes = [|"ReadWriteOnce"|]
            let capacity = dict["storage", ResourceQuantity("1Gi")]
            pv.Create volumeName
            let hostPath = V1HostPathVolumeSource(path = pv.FullPath volumeName)
            let spec = V1PersistentVolumeSpec(accessModes = accessModes,
                                              storageClassName = volumeName,
                                              capacity = capacity,
                                              hostPath = hostPath,
                                              persistentVolumeReclaimPolicy = "Retain")
            V1PersistentVolume(metadata = meta, spec = spec)
        List.map makePersistentVolume (self.ToCoreSetPersistentVolumeNames())

    member self.ToPersistentVolumeClaims () =
        let makePersistentVolumeClaim name =
            let volumeName = CfgVal.peristentVolumeName self.NamespaceProperty name
            let claimName = CfgVal.peristentVolumeClaimName self.NamespaceProperty name
            let meta = V1ObjectMeta(name = claimName)
            let accessModes = [|"ReadWriteOnce"|]
            let requests = dict["storage", ResourceQuantity("1Gi")]
            let requirements = V1ResourceRequirements(requests = requests)

            let spec = V1PersistentVolumeClaimSpec(accessModes = accessModes,
                                                   storageClassName = volumeName,
                                                   resources = requirements)
            V1PersistentVolumeClaim(metadata = meta, spec = spec)
        List.map makePersistentVolumeClaim (self.ToCoreSetPersistentVolumeNames())

    // Returns an Ingress object with rules that map URLs /$namespace/peer-N/foo
    // to the per-Pod Service within $namespace named peer-N (which then, via
    // DNS mapping, goes to the Pod itself). Exposing this to external traffic
    // requires that you enable the nginx Ingress controller on your k8s
    // cluster.
    member self.ToIngress () : Extensionsv1beta1Ingress =
        let httpPortStr = IntstrIntOrString(value = CfgVal.httpPort.ToString())
        let coreBackend (pn:string) =
            Extensionsv1beta1IngressBackend(serviceName = pn,
                                            servicePort = httpPortStr)
        let historyBackend (pn:string) =
            Extensionsv1beta1IngressBackend(serviceName = pn,
                                            servicePort = IntstrIntOrString(value = "80"))
        let corePath (coreSet: CoreSet) i =
            let pn = CfgVal.peerShortName coreSet i
            Extensionsv1beta1HTTPIngressPath(path = sprintf "/%s/%s/core/(.*)"
                                                    (self.NamespaceProperty) pn,
                                             backend = coreBackend pn)
        let historyPath (coreSet: CoreSet) i =
            let pn = CfgVal.peerShortName coreSet i
            Extensionsv1beta1HTTPIngressPath(path = sprintf "/%s/%s/history/(.*)"
                                                    (self.NamespaceProperty) pn,
                                             backend = historyBackend pn)
        let corePaths = self.MapAllPeers corePath
        let historyPaths = self.MapAllPeers historyPath
        let rule = Extensionsv1beta1HTTPIngressRuleValue(paths = Array.concat [corePaths; historyPaths])
        let rules = [|Extensionsv1beta1IngressRule(http = rule)|]
        let spec = Extensionsv1beta1IngressSpec(rules = rules)
        let annotation = Map.ofArray [|("nginx.ingress.kubernetes.io/ssl-redirect", "false");
                                       ("nginx.ingress.kubernetes.io/rewrite-target", "/$1")|]
        let meta = V1ObjectMeta(name = "stellar-core-ingress",
                                namespaceProperty = self.NamespaceProperty,
                                annotations = annotation)
        Extensionsv1beta1Ingress(spec = spec, metadata = meta)
