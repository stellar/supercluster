// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarKubeSpecs

open StellarCoreCfg
open k8s.Models

open StellarMissionContext
open StellarNetworkCfg
open StellarCoreSet
open StellarShellCmd
open System.Text.RegularExpressions

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

let CoreContainerVolumeMounts (peerOrJobNames:string array) (configOpt:ConfigOption) : V1VolumeMount array =
    let arr = [| V1VolumeMount(name = CfgVal.dataVolumeName,
                                      mountPath = CfgVal.dataVolumePath) |]

    match configOpt with
        | NoConfigFile -> arr
        | SharedJobConfigFile -> Array.append arr [| V1VolumeMount(name = CfgVal.jobCfgVolumeName,
                                                         readOnlyProperty = System.Nullable<bool>(true),
                                                         mountPath = CfgVal.jobCfgVolumePath) |]
        | PeerSpecificConfigFile -> Array.append arr ( Array.map (fun n -> 
                                        V1VolumeMount(name = CfgVal.cfgVolumeName n,
                                            readOnlyProperty = System.Nullable<bool>(true),
                                            mountPath = CfgVal.cfgVolumePath n)) 
            peerOrJobNames )

let makeResourceRequirements (cpuReqMili:int)
                             (memReqMebi:int)
                             (cpuLimMili:int)
                             (memLimMebi:int) : V1ResourceRequirements =
    let requests = dict["cpu", ResourceQuantity(sprintf "%dm" cpuReqMili);
                        "memory", ResourceQuantity(sprintf "%dMi" memReqMebi)]
    let limits = dict["cpu", ResourceQuantity(sprintf "%dm" cpuLimMili);
                      "memory", ResourceQuantity(sprintf "%dMi" memLimMebi)]
    V1ResourceRequirements(requests = requests,
                           limits = limits)

let PgResourceRequirements : V1ResourceRequirements =
    // Postgres needs 1 vCPU and 1GB RAM.
    makeResourceRequirements 1000 1024 1000 1024

let HistoryResourceRequirements: V1ResourceRequirements =
    // Nginx needs 0.01 vCPU and 32MB RAM. It's small.
    makeResourceRequirements 10 32 10 32

let PrometheusExporterSidecarResourceRequirements: V1ResourceRequirements =
    // The prometheus exporter sidecar needs 0.01 vCPU and 64MB RAM.
    makeResourceRequirements 10 64 10 64

let SimulatePubnetCoreResourceRequirements: V1ResourceRequirements =
    // Running simulate-pubnet _needs_ a ways over 200MB RSS per node, and
    // depending on queue backups it can spike over 300MB; we have 64GB limit
    // for quota so to be generous we give each node 400MB limit and run only
    // 100 nodes (despite survey showing many more).
    //
    // We also have a 100vCPU quota but only really 72 cores to play with, so
    // to keep some spare room for other jobs without stressing the workers we
    // want to stay under 50vCPU, again divided 100 ways across our simulated
    // nodes.
    //
    // So we allocate a 64MB RAM request and 400MB RAM limit to each, and a
    // 0.025vCPU request and 0.5vCPU limit to each.
    makeResourceRequirements 25 64 500 400

let ParallelCatchupCoreResourceRequirements: V1ResourceRequirements =
    // When doing parallel catchup, we give each container
    // 256MB RAM and 0.1 vCPUs, bursting to 1vCPU and 600MB
    makeResourceRequirements 100 256 1000 600

let NonParallelCatchupCoreResourceRequirements: V1ResourceRequirements =
    // When doing non-parallel catchup, we give each container
    // 6000MB RAM and 1 vCPU, bursting to 8000MB and 2 vCPUs 
    makeResourceRequirements 1000 6000 2000 8000

let UpgradeCoreResourceRequirements: V1ResourceRequirements =
    // When doing upgrade tests, we give each container
    // 256MB RAM and 1 vCPU, bursting to 4vCPU and 8000MB
    makeResourceRequirements 1000 256 4000 8000

let SmallTestCoreResourceRequirements: V1ResourceRequirements =
    // When running most missions, there are few core nodes, so each
    // gets 0.1 vCPUs with bursting to 1vCPU and 256MB RAM guaranteed.
    makeResourceRequirements 100 256 1000 256

let AcceptanceTestCoreResourceRequirements: V1ResourceRequirements =
    // When running acceptance tests we need to give a single core a very large
    // amount of memory because these tests are memory-intensive. 4 vCPU and 4GB
    // RAM required.
    makeResourceRequirements 4000 4096 4000 4096

let PgContainerVolumeMounts : V1VolumeMount array =
    [| V1VolumeMount(name = CfgVal.dataVolumeName,
                     mountPath = CfgVal.dataVolumePath) |]

let HistoryContainerVolumeMounts : V1VolumeMount array =
    [| V1VolumeMount(name = CfgVal.historyCfgVolumeName,
                     mountPath = CfgVal.historyCfgVolumePath);
       V1VolumeMount(name = CfgVal.dataVolumeName,
                     mountPath = CfgVal.dataVolumePath) |]

let HistoryContainer =
    V1Container
        (name = "history",
         image = "index.docker.io/library/nginx:latest",
         command = [| "nginx" |],
         args = [| "-c"; CfgVal.historyCfgFilePath; |],
         resources = HistoryResourceRequirements,
         volumeMounts = HistoryContainerVolumeMounts )

let PostgresContainer =
    let passwordEnvVar = V1EnvVar(name = "POSTGRES_PASSWORD",
                                  value = CfgVal.pgPassword)
    V1Container
        (name = "postgres",
         env = [| passwordEnvVar; |],
         ports = [| V1ContainerPort(containerPort = 5432, name = "postgres") |],
         image = "index.docker.io/library/postgres:9.5.22",
         resources = PgResourceRequirements,
         volumeMounts = PgContainerVolumeMounts)

let PrometheusExporterSidecarContainer =
    V1Container(name = "prom-exp",
                ports = [| V1ContainerPort(containerPort = CfgVal.prometheusExporterPort,
                                           name = "prom-exp") |],
                image = "index.docker.io/library/stellar-core-prometheus-exporter:latest",
                resources = PrometheusExporterSidecarResourceRequirements)

let cfgFileArgs (configOpt:ConfigOption) : ShWord array =
    match configOpt with
        | NoConfigFile -> [| |]
        | SharedJobConfigFile -> Array.map ShWord.OfStr [| "--conf"; CfgVal.jobCfgFilePath |]
        | PeerSpecificConfigFile -> [| ShWord.OfStr "--conf";
                                       CfgVal.peerNameEnvCfgFileWord |]

let CoreContainerForCommand (imageName:string) (configOpt:ConfigOption)
                            (cr:CoreResources)
                            (command:string array) (initCommands:ShCmd array) (peerOrJobNames:string array) : V1Container =

    let peerNameFieldSel = V1ObjectFieldSelector(fieldPath = "metadata.name")
    let peerNameEnvVarSource = V1EnvVarSource(fieldRef = peerNameFieldSel)
    let peerNameEnvVar = V1EnvVar(name = CfgVal.peerNameEnvVarName,
                                  valueFrom = peerNameEnvVarSource)
    let asanOptionsEnvVar = V1EnvVar(name = CfgVal.asanOptionsEnvVarName,
                              value = CfgVal.asanOptionsEnvVarValue)
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
    let killPs = ShCmd.OfStrs [| "killall5"; "-2"; |]

    // Regardless of success or failure, get status and cleanup after core's run
    let allCmds = ShAnd (Array.append initCommands [| ShCmd cmdWords; |])
    let allCmdsAndCleanup = ShSeq [| allCmds; exitStatusDef; killPs; exit; |]

    let res =
        match cr with
            | SmallTestResources -> SmallTestCoreResourceRequirements
            | AcceptanceTestResources -> AcceptanceTestCoreResourceRequirements
            | SimulatePubnetResources -> SimulatePubnetCoreResourceRequirements
            | ParallelCatchupResources -> ParallelCatchupCoreResourceRequirements
            | NonParallelCatchupResources -> NonParallelCatchupCoreResourceRequirements
            | UpgradeResources -> UpgradeCoreResourceRequirements

    V1Container
        (name = containerName, image = imageName,
         command = [| "/bin/sh" |],
         args = [| "-x"; "-c"; allCmdsAndCleanup.ToString() |],
         env = [| peerNameEnvVar; asanOptionsEnvVar |],
         resources = res,
         securityContext = V1SecurityContext(capabilities = V1Capabilities(add = [|"NET_ADMIN"|])),
         volumeMounts = CoreContainerVolumeMounts peerOrJobNames configOpt)

let WithLivenessProbe (container:V1Container) (probeTimeout:int) : V1Container =
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

        let ignoreError cmd : ShCmd Option = 
            match cmd with
            | None -> None
            | Some(cmd) -> 
                let t = ShCmd.OfStr "true"
                Some (ShCmd.ShOr [|cmd; t|])

        let setPgHost: ShCmd Option = 
            match opts.dbType with
              | Postgres -> Some (ShCmd.ExDefVar "PGHOST" CfgVal.pgHost)
              | _ -> None

        let setPgUser: ShCmd Option = 
            match opts.dbType with
              | Postgres -> Some (ShCmd.ExDefVar "PGUSER" CfgVal.pgUser)
              | _ -> None

        let createDbs: ShCmd Option array = 
            match opts.dbType with
              | Postgres -> [| for i in 0 .. 9 -> Some(ShCmd.OfStrs [| "createdb"; "test" + i.ToString() |]) |]
              | _ -> [||]

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

        // If the container is restarted, we want to ignore the error that says the history archive already exists.
        // This will allow us to bring the node up again, and notice that its ledgerNum isn't where we expect it to be.
        // We should get one of the InconsistentPeersException, and the previous container logs will contain the information
        // we want.  
        let newHistIgnoreError = ignoreError newHist   
        

        let initialCatchup = runCoreIf init.initialCatchup [| "catchup"; "current/0" |]
        let forceScp = runCoreIf init.forceScp [| "force-scp" |]
        
        let cmds = Array.choose id (Array.append ([| waitForDB; setPgUser; setPgHost; waitForTime; newDb; newHistIgnoreError; initialCatchup; forceScp |]) createDbs)

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

    member self.GetJobPodTemplateSpec (jobName:string) (command: string array) (image:string) (useConfigFile:bool) : V1PodTemplateSpec =
        let cfgOpt = (if useConfigFile
                      then SharedJobConfigFile
                      else NoConfigFile)

        let jobCfgVol = V1Volume(name = CfgVal.jobCfgVolumeName,
                                 configMap = V1ConfigMapVolumeSource(name = self.JobCfgMapName))
        let dataVol = V1Volume(name = CfgVal.dataVolumeName,
                               emptyDir = V1EmptyDirVolumeSource())

        let res = self.missionContext.coreResources
        let containers  =
            match self.jobCoreSetOptions with
                | None -> [| CoreContainerForCommand image cfgOpt res command [| |] [|jobName|] |]
                | Some(opts) ->
                let initCmds = self.getInitCommands cfgOpt opts
                let coreContainer = CoreContainerForCommand image cfgOpt res command initCmds [|jobName|]
                match opts.dbType with
                    | Postgres -> [| coreContainer; PostgresContainer |]
                    | _ -> [| coreContainer |]

        V1PodTemplateSpec
                (spec = V1PodSpec (containers = containers,
                                   volumes = [| jobCfgVol; dataVol |],
                                   restartPolicy = "Never",
                                   shareProcessNamespace = System.Nullable<bool>(true)),
                 metadata = V1ObjectMeta(labels = CfgVal.labels,
                                         namespaceProperty = self.NamespaceProperty))

    member self.GetJobFor (jobNum:int) (command: string array) (image:string) (useConfigFile:bool) : V1Job =
        let jobName = self.JobName jobNum
        let template = self.GetJobPodTemplateSpec jobName command image useConfigFile
        V1Job(spec = V1JobSpec(template = template, backoffLimit = System.Nullable<int>(3)),
              metadata = self.NamespacedMeta jobName)

    // Returns a PodTemplate that mounts the ConfigMap on /cfg and an empty data
    // volume on /data. Then initializes a local stellar-core database in
    // /data/stellar.db with buckets in /data/buckets and history archive in
    // /data/history, forces SCP on next startup, and runs.
    member self.ToPodTemplateSpec (coreSet: CoreSet) : V1PodTemplateSpec =

        // We cannot limit _individual_ peers within a CoreSet to only mount
        // their peer-specific ConfigMap, as we're producing a PodTemplateSpec
        // here that will be used for _all_ peers in the same CoreSet. However,
        // we can limit them to only mount ConfigMaps for the set of peers in
        // the single CoreSet we're building a PodTemplateSpec for, rather than
        // all the peers in the Formation.
        //
        // This is fairly important to try to limit, as it appears to create
        // heavy 'watch' load on the Kubernetes controller to have a large
        // number of ConfigMap volume mounts.
        let peerCfgVolume i =
            let peerName = self.PodName coreSet i
            if self.IsJobMode
                then V1Volume(name = CfgVal.jobCfgVolumeName,
                         configMap = V1ConfigMapVolumeSource(name = self.JobCfgMapName))
                else
                    V1Volume(name = CfgVal.cfgVolumeName peerName.StringName,
                         configMap = V1ConfigMapVolumeSource(name = self.PeerCfgMapName coreSet i))

        let peerName i = (self.PodName coreSet i).StringName
        let peerCfgVolumes = Array.mapi (fun i _ -> peerCfgVolume i) coreSet.keys
        let peerNames = Array.mapi (fun i _ -> peerName i) coreSet.keys
        let historyCfgVolume = V1Volume(name = CfgVal.historyCfgVolumeName,
                                        configMap = V1ConfigMapVolumeSource(name = self.HistoryCfgMapName))
        let dataVol = V1Volume(name = CfgVal.dataVolumeName,
                               emptyDir = V1EmptyDirVolumeSource(medium = "Memory"))
        let imageName = coreSet.options.image

        let cfgOpt = PeerSpecificConfigFile
        let volumes = Array.append peerCfgVolumes [| dataVol; historyCfgVolume |]
        let initCommands = self.getInitCommands cfgOpt coreSet.options

        let runCmd = [| "run" |]
        let runCmdWithOpts =
            match coreSet.options.simulateApplyUsec with
            | 0 -> runCmd
            | _ -> Array.append runCmd [| "--simulate-apply-per-op"; coreSet.options.simulateApplyUsec.ToString() |]

        let firstProtocolWithDefaultForceSCP = 14
        let getCoreVersion (coreVersion: string) =
            let m = Regex.Match(coreVersion, "[0-9]([0-9]?).[0-9]([0-9]?).[0-9]([0-9]?)")
            if m.Success then Some(m.Value)
            else None

        let imageProtoVersion =
            match (getCoreVersion coreSet.options.image) with
            | None -> firstProtocolWithDefaultForceSCP
            | Some v -> int (v.Split '.').[0]

        let runCmdWithOpts =
            match coreSet.options.initialization.forceScp with
            | true -> runCmdWithOpts
            | false -> if (imageProtoVersion >= firstProtocolWithDefaultForceSCP)
                       then Array.append runCmdWithOpts [| "--wait-for-consensus" |]
                       else runCmdWithOpts

        let usePostgres = (coreSet.options.dbType = Postgres)
        let exportToPrometheus = self.missionContext.exportToPrometheus

        let res = self.missionContext.coreResources
        let containers = [| WithLivenessProbe
                                (CoreContainerForCommand imageName
                                     cfgOpt res runCmdWithOpts initCommands peerNames)
                                self.missionContext.probeTimeout;
                            HistoryContainer; |]
        let containers =
            if usePostgres
            then Array.append containers [| PostgresContainer |]
            else containers
        let containers =
            if exportToPrometheus
            then Array.append containers [| PrometheusExporterSidecarContainer |]
            else containers
        let annotations =
            if exportToPrometheus
            then Map.ofList [("prometheus.io/scrape", "true")]
            else Map.empty
        let podSpec =
            V1PodSpec
                (containers = containers,
                 volumes = volumes)
        V1PodTemplateSpec
                (spec = podSpec,
                 metadata = V1ObjectMeta(labels = CfgVal.labels,
                                         annotations = annotations,
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
    member self.ToStatefulSet (coreSet: CoreSet) : V1StatefulSet =
        let statefulSetSpec =
            V1StatefulSetSpec
                (selector = V1LabelSelector(matchLabels = CfgVal.labels),
                 serviceName = self.ServiceName,
                 podManagementPolicy = "Parallel",
                 template = self.ToPodTemplateSpec coreSet,
                 replicas = System.Nullable<int>(coreSet.CurrentCount))
        let statefulSet = V1StatefulSet(metadata = self.NamespacedMeta (self.StatefulSetName coreSet).StringName,
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
            let name = self.PodName coreSet i
            let dnsName = self.PeerDnsName coreSet i
            let ports = [|V1ServicePort(name = "core",
                                        port = CfgVal.httpPort);
                          V1ServicePort(name = "history",
                                        port = 80)|]
            let ports =
                if self.missionContext.exportToPrometheus
                then Array.append ports [| V1ServicePort( name = "prom-exp",
                                                          port = CfgVal.prometheusExporterPort) |]
                else ports
            let spec = V1ServiceSpec(``type`` = "ExternalName",
                                     ports = ports,
                                     externalName = dnsName.StringName)
            V1Service(metadata = self.NamespacedMeta name.StringName, spec = spec)
        self.MapAllPeers perPodService

    // Returns an Ingress object with rules that map URLs http://$ingressHost/peer-N/foo
    // to the per-Pod Service within the current networkCfg named peer-N (which then, via
    // DNS mapping, goes to the Pod itself). Exposing this to external traffic
    // requires that you enable the traefik Ingress controller on your k8s
    // cluster.
    member self.ToIngress () : Extensionsv1beta1Ingress =
        let httpPortStr = IntstrIntOrString(value = CfgVal.httpPort.ToString())
        let coreBackend (pn:PodName) =
            Extensionsv1beta1IngressBackend(serviceName = pn.StringName,
                                            servicePort = httpPortStr)
        let historyBackend (pn:PodName) : Extensionsv1beta1IngressBackend =
            Extensionsv1beta1IngressBackend(serviceName = pn.StringName,
                                            servicePort = IntstrIntOrString(value = "80"))
        let corePath (coreSet: CoreSet) (i:int) : Extensionsv1beta1HTTPIngressPath =
            let pn = self.PodName coreSet i
            Extensionsv1beta1HTTPIngressPath(path = sprintf "/%s/core/" pn.StringName,
                                             backend = coreBackend pn)
        let historyPath (coreSet: CoreSet) (i:int) : Extensionsv1beta1HTTPIngressPath =
            let pn = self.PodName coreSet i
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
