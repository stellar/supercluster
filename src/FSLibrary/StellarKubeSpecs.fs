// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarKubeSpecs

open StellarCoreCfg
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

    // Pass a single '--conf /cfg-job/stellar-core.cfg' argument, with content derived from
    // NetworkCfg.jobCoreSetOptions
    | SharedJobConfigFile

    // Pass a single '--conf /cfg-${STELLAR_CORE_PEER_SHORT_NAME}/stellar-core.cfg' argument,
    // where the container is run in an environment with
    // STELLAR_CORE_PEER_SHORT_NAME set to self.PeerShortName so that the
    // container picks up a peer-specific config.
    | PeerSpecificConfigFile

let CoreContainerVolumeMounts (peerOrJobNames:string array) : V1VolumeMount array =
    let arr =
        Array.map (fun n -> V1VolumeMount(name = CfgVal.cfgVolumeName n,
                                          readOnlyProperty = System.Nullable<bool>(true),
                                          mountPath = CfgVal.cfgVolumePath n))
            peerOrJobNames
    Array.append arr [| V1VolumeMount(name = CfgVal.dataVolumeName,
                                      mountPath = CfgVal.dataVolumePath) |]

let PgContainerVolumeMounts : V1VolumeMount array =
    [| V1VolumeMount(name = CfgVal.dataVolumeName,
                     mountPath = CfgVal.dataVolumePath) |]

let HistoryContainerVolumeMounts : V1VolumeMount array =
    [| V1VolumeMount(name = CfgVal.historyCfgVolumeName,
                     mountPath = CfgVal.historyCfgVolumePath);
       V1VolumeMount(name = CfgVal.dataVolumeName,
                     mountPath = CfgVal.dataVolumePath) |]

// Note: for the time being we use the same resource-requirements calculation
// for each container in our pod, be it core, history or postgres. We just
// multiply the number of pods by the number of containers-in-each-pod to get a
// total container count, which is used as a divisor in these calculations. A
// fancier calculation would deduct different amounts for nginx and postgres
// (the former needs very little RAM, the latter quite a lot). But this seems to
// work for now.
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

let HistoryContainer (q:NetworkQuotas) (numContainers:int) =
    V1Container
        (name = "history",
         image = "nginx",
         command = [| "nginx" |],
         args = [| "-c"; CfgVal.historyCfgFilePath; |],
         resources = resourceRequirements q numContainers,
         volumeMounts = HistoryContainerVolumeMounts )

let PostgresContainer (q:NetworkQuotas) (numContainers:int) =
    V1Container
        (name = "postgres",
         ports = [| V1ContainerPort(containerPort = 5432, name = "postgres") |],
         image = "postgres:9.5",
         resources = resourceRequirements q numContainers,
         volumeMounts = PgContainerVolumeMounts)

let PrometheusExporterSidecarContainer (q:NetworkQuotas) (numContainers:int) =
    V1Container(name = "prom-exp",
                ports = [| V1ContainerPort(containerPort = CfgVal.prometheusExporterPort,
                                           name = "prom-exp") |],
                image = "stellar/stellar-core-prometheus-exporter:latest",
                resources = resourceRequirements q numContainers)

let cfgFileArgs (configOpt:ConfigOption) : ShWord array =
    match configOpt with
        | NoConfigFile -> [| |]
        | SharedJobConfigFile -> Array.map ShWord.OfStr [| "--conf"; CfgVal.jobCfgFilePath |]
        | PeerSpecificConfigFile -> [| ShWord.OfStr "--conf";
                                       CfgVal.peerNameEnvCfgFileWord |]

let CoreContainerForCommand (q:NetworkQuotas) (imageName:string) (numContainers:int) (configOpt:ConfigOption)
                            (command:string array) (initCommands:ShCmd array) (peerOrJobNames:string array) : V1Container =

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
        (name = containerName, image = imageName,
         command = [| "/bin/sh" |],
         args = [| "-x"; "-c"; allCmdsAndCleanup.ToString() |],
         env = [| peerNameEnvVar|],
         resources = resourceRequirements q numContainers,
         securityContext = V1SecurityContext(capabilities = V1Capabilities(add = [|"NET_ADMIN"|])),
         volumeMounts = CoreContainerVolumeMounts peerOrJobNames)

let WithLivenessProbe (container:V1Container) probeTimeout : V1Container =
    let httpPortStr = IntstrIntOrString(value = CfgVal.httpPort.ToString())
    let liveProbe = V1Probe(periodSeconds = System.Nullable<int>(1),
                            initialDelaySeconds = System.Nullable<int>(120),
                            failureThreshold = System.Nullable<int>(60),
                            timeoutSeconds = System.Nullable<int>(probeTimeout),
                            httpGet = V1HTTPGetAction(path = "/info",
                                                      port = httpPortStr))
    container.LivenessProbe <- liveProbe
    container

// Extend NetworkCfg type with methods for producing various Kubernetes objects.
type NetworkCfg with

    member self.Namespace : V1Namespace =
        V1Namespace(spec = V1NamespaceSpec(),
                    metadata = V1ObjectMeta(name = self.NamespaceProperty))

    member self.NamespacedMeta (name:string) : V1ObjectMeta =
        V1ObjectMeta(name = name, labels = CfgVal.labels,
                     namespaceProperty = self.NamespaceProperty)

    member self.HistoryConfigMap() : V1ConfigMap =
        let cfgmapname = self.HistoryCfgMapName
        let filename = CfgVal.historyCfgFileName
        let filedata = (sprintf "
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
        V1ConfigMap(metadata = self.NamespacedMeta cfgmapname,
                    data = Map.empty.Add(filename, filedata))

    member self.JobConfigMap(opts:CoreSetOptions) : V1ConfigMap =
        let cfgmapname = self.JobCfgMapName
        let filename = CfgVal.jobCfgFileName
        let filedata = (self.StellarCoreCfgForJob opts).ToString()
        V1ConfigMap(metadata = self.NamespacedMeta cfgmapname,
                    data = Map.empty.Add(filename, filedata))

    // Returns an array of ConfigMaps, each of which is a volume named
    // peer-0-cfg .. peer-N-cfg, to be mounted on /peer-0-cfg .. /peer-N-cfg,
    // and each containing a stellar-core.cfg TOML file for each peer.
    // Mounting these ConfigMaps on a PodTemplate will provide Pods instantiated
    // from the template with access to all the configs in the network, but each
    // only needs to figure out its own name to pick the volume that contains its
    // config. This arrangement is a bit crude but makes it relatively easy to
    // get a node's config to it, and to browse the configuration of the entire
    // network eg. in the k8s dashboard.
    member self.ToConfigMaps() : V1ConfigMap array =
        let peerCfgMap (coreSet:CoreSet) (i:int) =
            let cfgmapname = (self.PeerCfgMapName coreSet i)
            let filename = CfgVal.peerCfgFileName
            let filedata = (self.StellarCoreCfg (coreSet, i)).ToString()
            V1ConfigMap(metadata = self.NamespacedMeta cfgmapname,
                        data = Map.empty.Add(filename, filedata))
        let cfgs = Array.append (self.MapAllPeers peerCfgMap) [| self.HistoryConfigMap() |]
        match self.jobCoreSetOptions with
            | None -> cfgs
            | Some(opts) -> Array.append cfgs [| self.JobConfigMap(opts) |]

    member self.getInitCommands (configOpt:ConfigOption) (opts:CoreSetOptions) : ShCmd array =
        let cfgWords = cfgFileArgs configOpt
        let runCore args =
            let cmdAndArgs = (Array.map ShWord.OfStr
                               (Array.append [| CfgVal.stellarCoreBinPath |] args))
            ShCmd (Array.append cmdAndArgs cfgWords)
        let nonSimulation = opts.simulateApplyUsec = 0
        let runCoreIf flag args = if flag && nonSimulation then Some (runCore args) else None
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
        let waitForTime : ShCmd Option =
            match opts.syncStartupDelay with
            | None -> None
            | Some(n) ->
                let now:int64 = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                let deadline = now + int64(n)
                let getTime = ShCmd.DefVarSub "NOW" [| "date"; "+%s" |]
                let checkTime = ShCmd.OfStrs [|"test"; "${NOW}"; "-ge"; deadline.ToString()|]
                let getAndCheckTime = ShCmd.ShSeq [| getTime; checkTime  |]
                let sleep = ShCmd.OfStrs [| "sleep"; "1" |]
                Some (ShCmd.ShUntil(getAndCheckTime, sleep))
        let init = opts.initialization
        let newDb = runCoreIf init.newDb [| "new-db" |]
        let newHist = runCoreIf (opts.localHistory && init.newHist) [| "new-hist"; CfgVal.localHistName |]
        let initialCatchup = runCoreIf init.initialCatchup [| "catchup"; "current/0" |]
        let forceScp = runCoreIf init.forceScp [| "force-scp" |]

        let cmds = Array.choose id [| waitForDB; waitForTime; newDb; newHist; initialCatchup; forceScp |]

        let restoreDBStep coreSet i : ShCmd array =
          let dnsName = self.PeerDnsName coreSet i
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

    member self.GetJobPodTemplateSpec (jobName:string) (command: string array) (image:string) : V1PodTemplateSpec =
        let cfgOpt = (if self.jobCoreSetOptions.IsNone
                      then NoConfigFile
                      else SharedJobConfigFile)
        let maxPeers = 1

        let jobCfgVol = V1Volume(name = CfgVal.cfgVolumeName jobName,
                                 configMap = V1ConfigMapVolumeSource(name = self.JobCfgMapName))
        let pvcName = CfgVal.peristentVolumeClaimName jobName
        let dataVol = V1Volume(name = CfgVal.dataVolumeName,
                               persistentVolumeClaim = V1PersistentVolumeClaimVolumeSource(claimName = pvcName))

        let containers  =
            match self.jobCoreSetOptions with
                | None -> [| CoreContainerForCommand self.quotas image maxPeers cfgOpt command [| |] [|jobName|] |]
                | Some(opts) ->
                let initCmds = self.getInitCommands cfgOpt opts
                let numContainers =
                    match opts.dbType with
                        | Postgres -> maxPeers * 2
                        | _ -> maxPeers
                let coreContainer = CoreContainerForCommand self.quotas image numContainers cfgOpt command initCmds [|jobName|]
                match opts.dbType with
                    | Postgres -> [| coreContainer; PostgresContainer self.quotas numContainers |]
                    | _ -> [| coreContainer |]

        V1PodTemplateSpec
                (spec = V1PodSpec (containers = containers,
                                   volumes = [| jobCfgVol; dataVol |],
                                   restartPolicy = "Never",
                                   shareProcessNamespace = System.Nullable<bool>(true)),
                 metadata = V1ObjectMeta(labels = CfgVal.labels,
                                         namespaceProperty = self.NamespaceProperty))

    member self.GetJobFor (jobNum:int) (command: string array) (image:string) : V1Job =
        let jobName = self.JobName jobNum
        let template = self.GetJobPodTemplateSpec jobName command image
        V1Job(spec = V1JobSpec(template = template),
              metadata = self.NamespacedMeta jobName)

    // Returns a PodTemplate that mounts the ConfigMap on /cfg and an empty data
    // volume on /data. Then initializes a local stellar-core database in
    // /data/stellar.db with buckets in /data/buckets and history archive in
    // /data/history, forces SCP on next startup, and runs.
    member self.ToPodTemplateSpec (coreSet: CoreSet) probeTimeout : V1PodTemplateSpec =
        let peerCfgVolumes = self.MapAllPeers (fun cs i ->
            let peerName = self.PeerShortName cs i
            V1Volume(name = CfgVal.cfgVolumeName peerName.StringName,
                     configMap = V1ConfigMapVolumeSource(name = self.PeerCfgMapName cs i)))
        let peerNames = self.MapAllPeers self.PeerShortName |> Array.map (fun x -> x.StringName)
        let historyCfgVolume = V1Volume(name = CfgVal.historyCfgVolumeName,
                                        configMap = V1ConfigMapVolumeSource(name = self.HistoryCfgMapName))
        let dataVol = V1Volume(name = CfgVal.dataVolumeName,
                               emptyDir = V1EmptyDirVolumeSource(medium = "Memory"))
        let imageName = coreSet.options.image

        let cfgOpt = PeerSpecificConfigFile
        let volumes = Array.append peerCfgVolumes [| dataVol; historyCfgVolume |]
        let maxPeers = max 1 self.MaxPeerCount
        let initCommands = self.getInitCommands cfgOpt coreSet.options

        let runCmd = [| "run" |]
        let runCmdWithOpts =
            match coreSet.options.simulateApplyUsec with
            | 0 -> runCmd
            | _ -> Array.append runCmd [| "--simulate-apply-per-op"; coreSet.options.simulateApplyUsec.ToString() |]

        let usePostgres = (coreSet.options.dbType = Postgres)
        let numBaseContainers = 2 // core and history
        let numPrometheusContainers = if self.exportToPrometheus then 1 else 0
        let numPostgresContainers = if usePostgres then 1 else 0
        let containersPerPod = numBaseContainers + numPrometheusContainers + numPostgresContainers

        let numContainers = maxPeers * containersPerPod
        let containers = [| WithLivenessProbe
                                (CoreContainerForCommand self.quotas imageName numContainers
                                     cfgOpt runCmdWithOpts initCommands peerNames)
                                probeTimeout;
                            HistoryContainer self.quotas numContainers; |]
        let containers =
            if usePostgres
            then Array.append containers [| PostgresContainer self.quotas numContainers |]
            else containers
        let containers =
            if self.exportToPrometheus
            then Array.append containers [| PrometheusExporterSidecarContainer self.quotas numContainers |]
            else containers
        assert(containersPerPod = containers.Length)
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
                 podManagementPolicy = "Parallel",
                 template = self.ToPodTemplateSpec coreSet probeTimeout,
                 replicas = System.Nullable<int>(coreSet.CurrentCount))
        let statefulSet = V1StatefulSet(metadata = self.NamespacedMeta (self.StatefulSetName coreSet),
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
            let dnsName = self.PeerDnsName coreSet i
            let ports = [|V1ServicePort(name = "core",
                                        port = CfgVal.httpPort);
                          V1ServicePort(name = "history",
                                        port = 80)|]
            let ports =
                if self.exportToPrometheus
                then Array.append ports [| V1ServicePort( name = "prom-exp",
                                                          port = CfgVal.prometheusExporterPort) |]
                else ports
            let spec = V1ServiceSpec(``type`` = "ExternalName",
                                     ports = ports,
                                     externalName = dnsName.StringName)
            V1Service(metadata = self.NamespacedMeta name.StringName, spec = spec)
        self.MapAllPeers perPodService

    member self.ToDynamicPersistentVolumeClaim (jobOrPeerName:string) : V1PersistentVolumeClaim =
        let pvcName = CfgVal.peristentVolumeClaimName jobOrPeerName
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
        let coreBackend (pn:PeerShortName) =
            Extensionsv1beta1IngressBackend(serviceName = pn.StringName,
                                            servicePort = httpPortStr)
        let historyBackend (pn:PeerShortName) : Extensionsv1beta1IngressBackend =
            Extensionsv1beta1IngressBackend(serviceName = pn.StringName,
                                            servicePort = IntstrIntOrString(value = "80"))
        let corePath (coreSet: CoreSet) (i:int) : Extensionsv1beta1HTTPIngressPath =
            let pn = self.PeerShortName coreSet i
            Extensionsv1beta1HTTPIngressPath(path = sprintf "/%s/core/" pn.StringName,
                                             backend = coreBackend pn)
        let historyPath (coreSet: CoreSet) (i:int) : Extensionsv1beta1HTTPIngressPath =
            let pn = self.PeerShortName coreSet i
            Extensionsv1beta1HTTPIngressPath(path = sprintf "/%s/history/" pn.StringName,
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
