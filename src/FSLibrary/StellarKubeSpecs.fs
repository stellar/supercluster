// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarKubeSpecs

open k8s.Models

open StellarNetworkCfg
open StellarCoreCfg
open StellarCoreSet
open StellarShellCmd

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

let PostgresContainer =
    V1Container
        (name = "postgres",
         ports = [| V1ContainerPort(containerPort = 5432, name = "postgres") |],
         image = "postgres:9.5",
         volumeMounts = ContainerVolumeMounts)

let resourceRequirements (q:NetworkQuotas) (numContainers:int) : V1ResourceRequirements =
    let cpuReq = q.ContainerCpuReqMili numContainers
    let memReq = q.ContainerMemReqMebi numContainers
    let cpuLim = q.ContainerCpuLimMili numContainers
    let memLim = q.ContainerMemLimMebi numContainers
    let requests = dict["cpu", ResourceQuantity(sprintf "%dm" cpuReq);
                        "memory", ResourceQuantity(sprintf "%dMi" memReq)]
    let limits = dict["cpu", ResourceQuantity(sprintf "%dm" cpuLim);
                      "memory", ResourceQuantity(sprintf "%dMi" memLim)]
    V1ResourceRequirements(requests = requests,
                           limits = limits)


let cfgFileArgs (configOpt:ConfigOption) : ShWord array =
    match configOpt with
        | NoConfigFile -> [| |]
        | SharedJobConfigFile -> Array.map ShWord.OfStr [| "--conf"; CfgVal.jobCfgFile |]
        | PeerSpecificConfigFile -> [| ShWord.OfStr "--conf";
                                       CfgVal.peerNameEnvCfgFileWord |]

let CoreContainerForCommand (q:NetworkQuotas) (numContainers:int) (configOpt:ConfigOption)
                            (command:string array) (initCommands:ShCmd array) : V1Container =

    let peerNameFieldSel = V1ObjectFieldSelector(fieldPath = "metadata.name")
    let peerNameEnvVarSource = V1EnvVarSource(fieldRef = peerNameFieldSel)
    let peerNameEnvVar = V1EnvVar(name = CfgVal.peerNameEnvVarName,
                                  valueFrom = peerNameEnvVarSource)
    let cfgWords = cfgFileArgs configOpt
    let containerName = CfgVal.stellarCoreContainerName (Array.get command 0)
    let cmdWords = Array.concat [ [| ShWord.OfStr CfgVal.stellarCoreBinPath |];
                                  Array.map ShWord.OfStr command;
                                  cfgWords ]

    let toShPieces word = ShPieces [| word; |]
    let statusName = ShName "CORE_EXIT_STATUS"
    let exitStatusDef = ShDef (statusName, toShPieces ShSpecialLastExit )
    let exit = ShCmd (Array.map toShPieces [| ShBare "exit"; ShVar statusName;|])

    // Kill any outstanding processes, such as PG
    let killPs = ShCmd.OfStrs [| "killall5"; "-INT"; |]

    // Regardless of success or failure, get status and cleanup after core's run
    let allCmds = ShAnd (Array.append initCommands [| ShCmd cmdWords; |])
    let allCmdsAndCleanup = ShSeq [| allCmds; exitStatusDef; killPs; exit; |]

    V1Container
        (name = containerName, image = CfgVal.stellarCoreImageName,
         command = [| "/bin/sh" |],
         args = [| "-x"; "-c"; allCmdsAndCleanup.ToString() |],
         env = [| peerNameEnvVar|],
         resources = resourceRequirements q numContainers,
         volumeMounts = ContainerVolumeMounts)

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

    member self.getInitCommands (configOpt:ConfigOption) (opts:CoreSetOptions) : ShCmd array =
        let cfgWords = cfgFileArgs configOpt
        let runCore args =
            let cmdAndArgs = (Array.map ShWord.OfStr
                               (Array.append [| CfgVal.stellarCoreBinPath |] args))
            ShCmd (Array.append cmdAndArgs cfgWords)
        let runCoreIf flag args = if flag then Some (runCore args) else None
        let waitForDB: ShCmd Option =
          match opts.dbType with
          | Postgres ->
              let pgIsReady = [| "pg_isready";
                                 "-h"; CfgVal.pgHost;
                                 "-d"; CfgVal.pgDb;
                                 "-U"; CfgVal.pgUser |]
              let sleep2 = [| "sleep"; "2" |]
              Some (ShCmd.Until pgIsReady sleep2)
          | _ -> None

        let init = opts.initialization
        let newDb = runCoreIf init.newDb [| "new-db" |]
        let newHist = runCoreIf init.newHist [| "new-hist"; CfgVal.localHistName |]
        let initialCatchup = runCoreIf init.initialCatchup [| "catchup"; "current/0" |]
        let forceScp = runCoreIf init.forceScp [| "force-scp" |]

        let cmds = Array.choose id [| waitForDB; newDb; newHist; initialCatchup; forceScp |]

        let restoreDBStep coreSet i : ShCmd array =
          let dnsName = self.PeerDNSName coreSet i
          [| ShCmd.OfStrs [| "curl"; "-sf"; "-o"; CfgVal.databasePath; CfgVal.databaseBackupURL dnsName |];
             ShCmd.OfStrs [| "curl"; "-sf"; "-o"; CfgVal.bucketsDownloadPath; CfgVal.bucketsBackupURL dnsName |];
             ShCmd.OfStrs [| "tar"; "xf"; CfgVal.bucketsDownloadPath; "-C"; CfgVal.dataVolumePath |] |]

        match init.fetchDBFromPeer with
          | None -> cmds
          | Some(coreSet, i) ->
              let coreSet = self.FindCoreSet coreSet
              match coreSet.options.dbType with
                  | Postgres -> cmds // PG does not support that yet
                  | _ -> Array.append cmds (restoreDBStep coreSet i)

    member self.GetJobPodTemplateSpec (jobName:string) (command: string array) : V1PodTemplateSpec =
        let cfgOpt = (if self.jobCoreSetOptions.IsNone
                      then NoConfigFile
                      else SharedJobConfigFile)
        let maxPeers = 1
        let imageName = CfgVal.stellarCoreImageName

        let cfgVol = V1Volume(name = CfgVal.cfgVolumeName,
                              configMap = V1ConfigMapVolumeSource(name = self.CfgMapName))
        let ns = self.NamespaceProperty
        let pvcName = CfgVal.peristentVolumeClaimName ns jobName
        let dataVol = V1Volume(name = CfgVal.dataVolumeName,
                               persistentVolumeClaim = V1PersistentVolumeClaimVolumeSource(claimName = pvcName))

        let containers  =
            match self.jobCoreSetOptions with
                | None -> [| CoreContainerForCommand self.quotas maxPeers cfgOpt command [| |] |]
                | Some(opts) ->
                let initCmds = self.getInitCommands cfgOpt opts
                let coreContainer = CoreContainerForCommand self.quotas maxPeers cfgOpt command initCmds
                match opts.dbType with
                    | Postgres -> [| coreContainer; PostgresContainer |]
                    | _ -> [| coreContainer |]

        V1PodTemplateSpec
                (spec = V1PodSpec (containers = containers,
                                   volumes = [| cfgVol; dataVol |],
                                   restartPolicy = "Never",
                                   shareProcessNamespace = System.Nullable<bool>(true)),
                 metadata = V1ObjectMeta(labels = CfgVal.labels,
                                         namespaceProperty = ns))

    member self.GetJobFor (jobNum:int) (command: string array) : V1Job =
        let jobName = self.JobName jobNum
        let template = self.GetJobPodTemplateSpec jobName command
        V1Job(spec = V1JobSpec(template = template),
              metadata = self.NamespacedMeta jobName)

    // Returns a PodTemplate that mounts the ConfigMap on /cfg and an empty data
    // volume on /data. Then initializes a local stellar-core database in
    // /data/stellar.db with buckets in /data/buckets and history archive in
    // /data/history, forces SCP on next startup, and runs.
    member self.ToPodTemplateSpec (coreSet: CoreSet) probeTimeout : V1PodTemplateSpec =
        let cfgVol = V1Volume(name = CfgVal.cfgVolumeName,
                              configMap = V1ConfigMapVolumeSource(name = self.CfgMapName))
        let dataVol = V1Volume(name = CfgVal.dataVolumeName,
                               emptyDir = V1EmptyDirVolumeSource(medium = "Memory"))
        let imageName =
            match coreSet.options.image with
            | None -> CfgVal.stellarCoreImageName
            | Some(x) -> x

        let cfgOpt = PeerSpecificConfigFile

        let volumes = [| cfgVol; dataVol |]
        let maxPeers = max 1 self.MaxPeerCount
        let initCommands = self.getInitCommands cfgOpt coreSet.options

        let runCmd = [| "run" |]
        let runCmdWithOpts =
            match coreSet.options.simulateApplyMu with
            | 0 -> runCmd
            | _ -> Array.append runCmd [| "--simulate-apply-per-op"; coreSet.options.simulateApplyMu.ToString() |]

        let containers = [| WithReadinessProbe
                                (CoreContainerForCommand self.quotas maxPeers cfgOpt runCmdWithOpts initCommands)
                                probeTimeout;
                            HistoryContainer; |]
        let containers  =
            match coreSet.options.dbType with
                | Postgres -> Array.append containers [| PostgresContainer |]
                | _ -> containers

        let podSpec =
            V1PodSpec
                (containers = containers,
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
        let annotation = Map.ofArray [|("traefik.ingress.kubernetes.io/rule-type", "PathPrefixStrip");
                                       ("kubernetes.io/ingress.class", "private")|]
        let meta = V1ObjectMeta(name = self.IngressName,
                                namespaceProperty = self.NamespaceProperty,
                                annotations = annotation)
        Extensionsv1beta1Ingress(spec = spec, metadata = meta)
