// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarKubeSpecs

open k8s.Models

open StellarNetworkCfg
open StellarCoreCfg
open StellarCoreSet
open StellarPersistentVolume

// Containers that run stellar-core may or may-not have a final '--conf'
// argument appended to their command-line. The argument is specified one of 3
// ways:
type ConfigOption =

    // Pass no '--conf' argument
    | NoConfigFile

    // Pass a single '--conf /cfg/job.conf' argument, with content derived from
    // NetworkCfg.jobCoreSetOptions
    | SharedJobConfigFile

    // Pass a single '--conf /cfg/$(STELLAR_CORE_PEER_SHORT_NAME).cfg' argument,
    // where the container is run in an environment with
    // STELLAR_CORE_PEER_SHORT_NAME set to self.PeerShortName so that the
    // container picks up a peer-specific config.
    | PeerSpecificConfigFile

let ContainerVolumeMounts : V1VolumeMount array =
    [| V1VolumeMount(name = CfgVal.dataVolumeName,
                     mountPath = CfgVal.dataVolumePath)
       V1VolumeMount(name = CfgVal.cfgVolumeName,
                     mountPath = CfgVal.cfgVolumePath) |]


let HistoryContainer =
    V1Container
        (name = "history",
         image = "nginx",
         command = [| "nginx" |],
         args = [| "-c"; CfgVal.historyCfgFile; |],
         volumeMounts = ContainerVolumeMounts )


let resourceRequirements (q:NetworkQuotas) (numContainers:int) : V1ResourceRequirements =
    let cpuReq = q.ContainerCpuReqMili numContainers
    let memReq = q.ContainerMemReqMega numContainers
    let cpuLim = q.ContainerCpuLimMili numContainers
    let memLim = q.ContainerMemLimMega numContainers
    let requests = dict["cpu", ResourceQuantity(sprintf "%dm" cpuReq);
                        "memory", ResourceQuantity(sprintf "%dMi" memReq)]
    let limits = dict["cpu", ResourceQuantity(sprintf "%dm" cpuLim);
                      "memory", ResourceQuantity(sprintf "%dMi" memLim)]
    V1ResourceRequirements(requests = requests,
                           limits = limits)


let ContainerForCommand (q:NetworkQuotas) (numContainers:int)
                        (image:string) (containerName:string)
                        (command:string) (args:string array)
                        (env:V1EnvVar array) : V1Container =
    V1Container
        (name = containerName, image = image,
         command = [| command |], env = env,
         args = args, resources = resourceRequirements q numContainers,
         volumeMounts = ContainerVolumeMounts)


let CoreContainerForCommand (q:NetworkQuotas) (numContainers:int)
        (image:string) (configOpt:ConfigOption)
        (subCommands:string array) : V1Container =
    let peerNameFieldSel = V1ObjectFieldSelector(fieldPath = "metadata.name")
    let peerNameEnvVarSource = V1EnvVarSource(fieldRef = peerNameFieldSel)
    let peerNameEnvVar = V1EnvVar(name = CfgVal.peerNameEnvVarName,
                                  valueFrom = peerNameEnvVarSource)
    let cfgFileArgs = (match configOpt with
                       | NoConfigFile -> [| |]
                       | SharedJobConfigFile -> [| "--conf"; CfgVal.jobCfgFile |]
                       | PeerSpecificConfigFile -> [| "--conf"; CfgVal.peerNameEnvCfgFile |])
    let containerName = CfgVal.stellarCoreContainerName (Array.get subCommands 0)
    let command = CfgVal.stellarCoreBinPath
    let args = Array.append subCommands cfgFileArgs
    let env = [| peerNameEnvVar |]
    ContainerForCommand q numContainers image containerName command args env


let WithReadinessProbe (container:V1Container) probeTimeout : V1Container =
    let httpPortStr = IntstrIntOrString(value = CfgVal.httpPort.ToString())
    let readyProbe = V1Probe(periodSeconds = System.Nullable<int>(5),
                             initialDelaySeconds = System.Nullable<int>(10),
                             failureThreshold = System.Nullable<int>(6),
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

    member self.HistoryConfig() : (string * string) =
        (CfgVal.historyCfgFilename, sprintf "
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

    member self.JobConfig(opts:CoreSetOptions) : (string * string) =
        let cfg = self.StellarCoreCfgForJob opts
        (CfgVal.jobCfgFilename, (cfg.ToString()))

    // Returns a ConfigMap dictionary that names "peer-0.cfg".."peer-N.cfg" to
    // TOML config file data for each such config. Mounting this ConfigMap on a
    // Pod will provide it access to _all_ the configs in the network, but it
    // only needs to figure out its own name to pick the file that contains its
    // config. This arrangement is a bit crude but makes it relatively easy to
    // get a node's config to it, and to browse the configuration of the entire
    // network eg. in the k8s dashboard.
    member self.ToConfigMap() : V1ConfigMap =
        let peerKeyValPair (coreSet: CoreSet) i = (self.PeerCfgName coreSet i, (self.StellarCoreCfg (coreSet, i)).ToString())
        let cfgs = Array.append (self.MapAllPeers peerKeyValPair) [| self.HistoryConfig() |]
        let cfgs = match self.jobCoreSetOptions with
                   | None -> cfgs
                   | Some(opts) -> Array.append cfgs [| self.JobConfig(opts) |]
        V1ConfigMap(metadata = self.NamespacedMeta self.CfgMapName,
                    data = Map.ofSeq cfgs)

    member self.GetJobPodTemplateSpec (jobName:string) (command: string array) : V1PodTemplateSpec =
        let cfgOpt = (if self.jobCoreSetOptions.IsNone
                      then NoConfigFile
                      else SharedJobConfigFile)
        let maxPeers = 1
        let imageName = CfgVal.stellarCoreImageName
        let containers = [| CoreContainerForCommand self.quotas maxPeers imageName cfgOpt command |]
        let cfgVol = V1Volume(name = CfgVal.cfgVolumeName,
                              configMap = V1ConfigMapVolumeSource(name = self.CfgMapName))
        let ns = self.NamespaceProperty
        let pvcName = CfgVal.peristentVolumeClaimName ns jobName
        let dataVol = V1Volume(name = CfgVal.dataVolumeName,
                               persistentVolumeClaim = V1PersistentVolumeClaimVolumeSource(claimName = pvcName))
        let initContainers =
            match self.jobCoreSetOptions with
                | None -> [| |]
                | Some(opts) -> self.InitContainersFor opts.initialization maxPeers imageName cfgOpt

        V1PodTemplateSpec
                (spec = V1PodSpec (initContainers = initContainers,
                                   containers = containers,
                                   volumes = [| cfgVol; dataVol |],
                                   restartPolicy = "Never"),
                 metadata = V1ObjectMeta(labels = CfgVal.labels,
                                         namespaceProperty = ns))

    member self.GetJobFor (jobNum:int) (command: string array) : V1Job =
        let jobName = self.JobName jobNum
        let template = self.GetJobPodTemplateSpec jobName command
        V1Job(spec = V1JobSpec(template = template),
              metadata = self.NamespacedMeta jobName)

    member self.InitContainersFor (init:CoreSetInitialization) (maxPeers:int) (imageName:string) (cfgOpt:ConfigOption) : V1Container array =
        let newDb i = if i.newDb then [| [| "new-db" |] |] else [||]
        let newHist i = if i.newHist then [| [| "new-hist"; CfgVal.localHistName |] |] else [||]
        let initialCatchup i = if i.initialCatchup then [| [| "catchup"; "current/0" |] |] else [||]
        let forceScp i = if i.forceScp then [| [| "force-scp" |] |] else [||]

        let initSequence (i : CoreSetInitialization) =
            Array.concat [ newDb i; newHist i; initialCatchup i; forceScp i ]

        let coreSteps =
            Array.map
                (fun p -> CoreContainerForCommand self.quotas maxPeers imageName cfgOpt p)
                (initSequence init)

        let restoreDBStep coreSet i =
            let coreSet = self.FindCoreSet coreSet
            let dnsName = self.PeerDNSName coreSet i
            let env = [| |]
            let cc container command args =
                ContainerForCommand
                    self.quotas maxPeers imageName container command args env
            [|
                cc "curl-database" "curl" [| "-o"; CfgVal.databasePath;
                                             CfgVal.databaseBackupURL dnsName |];
                cc "curl-buckets" "curl" [| "-o"; CfgVal.bucketsDownloadPath;
                                            CfgVal.bucketsBackupURL dnsName |];
                cc "untar-buckets" "tar" [| "xf"; CfgVal.bucketsDownloadPath;
                                            "-C"; CfgVal.dataVolumePath |]
            |]

        match init.fetchDBFromPeer with
            | None -> coreSteps
            | Some(coreSet, i) ->
                Array.append coreSteps (restoreDBStep coreSet i)

    // Returns a PodTemplate that mounts the ConfigMap on /cfg and an empty data
    // volume on /data. Then initializes a local stellar-core database in
    // /data/stellar.db with buckets in /data/buckets and history archive in
    // /data/history, forces SCP on next startup, and runs.
    member self.ToPodTemplateSpec (coreSet: CoreSet) probeTimeout : V1PodTemplateSpec =
        let cfgVol = V1Volume(name = CfgVal.cfgVolumeName,
                              configMap = V1ConfigMapVolumeSource(name = self.CfgMapName))
        let dataVol =
            match coreSet.options.persistentVolume with
            | None -> V1Volume(name = CfgVal.dataVolumeName,
                               emptyDir = V1EmptyDirVolumeSource(medium = "Memory"))
            | Some(x) ->
                let claim = V1PersistentVolumeClaimVolumeSource(claimName = CfgVal.peristentVolumeClaimName self.NamespaceProperty x)
                V1Volume(name = CfgVal.dataVolumeName,
                         persistentVolumeClaim = claim)

        let imageName =
            match coreSet.options.image with
            | None -> CfgVal.stellarCoreImageName
            | Some(x) -> x

        let cfgOpt = PeerSpecificConfigFile
        let volumes = [| cfgVol; dataVol |]
        let maxPeers = max 1 self.MaxPeerCount
        let initContianers = self.InitContainersFor coreSet.options.initialization maxPeers imageName cfgOpt
        let containers = [| WithReadinessProbe
                                (CoreContainerForCommand self.quotas maxPeers imageName cfgOpt [| "run" |])
                                probeTimeout;
                            HistoryContainer; |]

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
                  metadata = self.NamespacedMeta self.ServiceName)


    // Returns a StatefulSet object that will build stellar-core Pods named
    // peer-0 .. peer-N, and bind them to the generic "peer" Service above.
    member self.ToStatefulSet (coreSet: CoreSet) probeTimeout : V1StatefulSet =
        let statefulSetSpec =
            V1StatefulSetSpec
                (selector = V1LabelSelector(matchLabels = CfgVal.labels),
                 serviceName = self.ServiceName,
                 template = self.ToPodTemplateSpec coreSet probeTimeout,
                 replicas = System.Nullable<int>(coreSet.CurrentCount))
        let statefulSet = V1StatefulSet(metadata = self.NamespacedMeta (self.PeerSetName coreSet),
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
            let name = self.PeerShortName coreSet i
            let dnsName = self.PeerDNSName coreSet i
            let spec = V1ServiceSpec(``type`` = "ExternalName",
                                     ports =
                                      [|V1ServicePort(name = "core",
                                                      port = CfgVal.httpPort);
                                        V1ServicePort(name = "history",
                                                      port = 80)|],
                                     externalName = dnsName)
            V1Service(metadata = self.NamespacedMeta name, spec = spec)
        self.MapAllPeers perPodService

    member self.ToCoreSetPersistentVolumeNames () : string list =
        let withPersistentVolumes = self.CoreSetList |> List.filter (fun coreSet -> coreSet.options.persistentVolume <> None)
        let getName (coreSet: CoreSet) =
            coreSet.options.persistentVolume.Value
        let names = List.map getName withPersistentVolumes
        names |> List.sort |> List.distinct

    member self.ToPersistentVolumeNames () : string list =
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

    member self.ToDynamicPersistentVolumeClaim (jobOrPeerName:string) : V1PersistentVolumeClaim =
        let ns = self.NamespaceProperty
        let pvcName = CfgVal.peristentVolumeClaimName ns jobOrPeerName
        let meta = self.NamespacedMeta pvcName
        let accessModes = [|"ReadWriteOnce"|]
        // EBS gp2 volumes are provisioned at "3 IOPS per GB". We want a sustained
        // 3000 IOPS performance level, which means we request a 1TiB volume.
        let requests = dict["storage", ResourceQuantity("1Ti")]
        let requirements = V1ResourceRequirements(requests = requests)
        let spec = V1PersistentVolumeClaimSpec(accessModes = accessModes,
                                               storageClassName = self.storageClass,
                                               resources = requirements)
        V1PersistentVolumeClaim(metadata = meta, spec = spec)

    member self.ToDynamicPersistentVolumeClaimForJob (jobNum:int) : V1PersistentVolumeClaim =
        self.ToDynamicPersistentVolumeClaim (self.JobName jobNum)

    member self.ToPersistentVolumeClaims () : V1PersistentVolumeClaim list =
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

    // Returns an Ingress object with rules that map URLs http://$ingressHost/peer-N/foo
    // to the per-Pod Service within the current networkCfg named peer-N (which then, via
    // DNS mapping, goes to the Pod itself). Exposing this to external traffic
    // requires that you enable the traefik Ingress controller on your k8s
    // cluster.
    member self.ToIngress () : Extensionsv1beta1Ingress =
        let httpPortStr = IntstrIntOrString(value = CfgVal.httpPort.ToString())
        let coreBackend (pn:string) =
            Extensionsv1beta1IngressBackend(serviceName = pn,
                                            servicePort = httpPortStr)
        let historyBackend (pn:string) : Extensionsv1beta1IngressBackend =
            Extensionsv1beta1IngressBackend(serviceName = pn,
                                            servicePort = IntstrIntOrString(value = "80"))
        let corePath (coreSet: CoreSet) (i:int) : Extensionsv1beta1HTTPIngressPath =
            let pn = self.PeerShortName coreSet i
            Extensionsv1beta1HTTPIngressPath(path = sprintf "/%s/core/" pn,
                                             backend = coreBackend pn)
        let historyPath (coreSet: CoreSet) (i:int) : Extensionsv1beta1HTTPIngressPath =
            let pn = self.PeerShortName coreSet i
            Extensionsv1beta1HTTPIngressPath(path = sprintf "/%s/history/" pn,
                                             backend = historyBackend pn)
        let corePaths = self.MapAllPeers corePath
        let historyPaths = self.MapAllPeers historyPath
        let rule = Extensionsv1beta1HTTPIngressRuleValue(paths = Array.concat [corePaths; historyPaths])
        let host = self.IngressHostName
        let rules = [|Extensionsv1beta1IngressRule(
                        host = host,
                        http = rule)|]
        let spec = Extensionsv1beta1IngressSpec(rules = rules)
        let annotation = Map.ofArray [|("traefik.ingress.kubernetes.io/rule-type", "PathPrefixStrip")|]
        let meta = V1ObjectMeta(name = self.IngressName,
                                namespaceProperty = self.NamespaceProperty,
                                annotations = annotation)
        Extensionsv1beta1Ingress(spec = spec, metadata = meta)
