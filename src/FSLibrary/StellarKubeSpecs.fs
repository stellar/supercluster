// Copyright 2019 Stellar Development Foundation and contributors. Licensed
// under the Apache License, Version 2.0. See the COPYING file at the root
// of this distribution or at http://www.apache.org/licenses/LICENSE-2.0

module StellarKubeSpecs

open k8s
open k8s.Models

open StellarNetworkCfg
open StellarCoreCfg


// Returns a k8s V1Container object using the stellar-core image, and running
// stellar-core with the specified sub-command(s) or arguments, passing a final
// argument pair "--conf /cfg/$(STELLAR_CORE_PEER_SHORT_NAME).cfg". This is run
// in an _environment_ that has STELLAR_CORE_PEER_SHORT_NAME set to the Pod's
// name, via a fieldRef env var referencing the Pod's "metadata.name" field. For
// example, on Pod peer-1, stellar-core will run with --conf /cfg/peer-1.cfg.
//
// This is admittedly a bit convoluted, but it's the simplest mechanism I could
// figure out to furnish each stellar-core with a Pod-specific config file.
let CoreContainerForCommand (subCommands:string array) : V1Container =
    let peerNameFieldSel = V1ObjectFieldSelector(fieldPath = "metadata.name")
    let peerNameEnvVarSource = V1EnvVarSource(fieldRef = peerNameFieldSel)
    let peerNameEnvVar = V1EnvVar(name = CfgVal.peerNameEnvVarName,
                                  valueFrom = peerNameEnvVarSource)
    V1Container
        (name = "stellar-core-" + (Array.get subCommands 0),
         image = CfgVal.stellarCoreImageName,
         command = [| CfgVal.stellarCoreBinPath |], env = [| peerNameEnvVar |],
         volumeMounts = [| V1VolumeMount(name = CfgVal.dataVolumeName,
                                         mountPath = CfgVal.dataVolumePath)
                           V1VolumeMount(name = CfgVal.cfgVolumeName,
                                         mountPath = CfgVal.cfgVolumePath) |],
         args = Array.append subCommands [| "--conf"; CfgVal.peerNameEnvCfgFile |])


let WithReadinessProbe (container:V1Container) : V1Container =
    let httpPortStr = IntstrIntOrString(value = CfgVal.httpPort.ToString())
    let readyProbe = V1Probe(periodSeconds = System.Nullable<int>(5),
                             initialDelaySeconds = System.Nullable<int>(5),
                             httpGet = V1HTTPGetAction(path = "/info",
                                                       port = httpPortStr))
    container.ReadinessProbe <- readyProbe
    container


// Extend NetworkCfg type with methods for producing various Kubernetes objects.
type NetworkCfg with

    member self.Namespace() : V1Namespace =
        V1Namespace(spec = V1NamespaceSpec(),
                    metadata = V1ObjectMeta(name = self.NamespaceProperty()))

    member self.NamespacedMeta (name:string) : V1ObjectMeta =
        V1ObjectMeta(name = name, labels = CfgVal.labels,
                     namespaceProperty = self.NamespaceProperty())

    // Returns a ConfigMap dictionary that names "peer-0.cfg".."peer-N.cfg" to
    // TOML config file data for each such config. Mounting this ConfigMap on a
    // Pod will provide it access to _all_ the configs in the network, but it
    // only needs to figure out its own name to pick the file that contains its
    // config. This arrangement is a bit crude but makes it relatively easy to
    // get a node's config to it, and to browse the configuration of the entire
    // network eg. in the k8s dashboard.
    member self.ToConfigMap() : V1ConfigMap =
        let peerKeyValPair i = (CfgVal.peerCfgName i, (self.StellarCoreCfgForPeer i).ToString())
        let cfgs = Array.init self.NumPeers peerKeyValPair
        V1ConfigMap(metadata = V1ObjectMeta(name = CfgVal.cfgMapName,
                                            namespaceProperty = self.NamespaceProperty()),
                    data = Map.ofSeq cfgs)

    // Returns a PodTemplate that mounds the ConfigMap on /cfg and an empty data
    // volume on /data. Then initializes a local stellar-core database in
    // /data/stellar.db with buckets in /data/buckets and history archive in
    // /data/history, forces SCP on next startup, and runs.
    member self.ToPodTemplateSpec() : V1PodTemplateSpec =
        let cfgVol = V1Volume(name = CfgVal.cfgVolumeName,
                              configMap = V1ConfigMapVolumeSource(name = CfgVal.cfgMapName))
        let dataVol = V1Volume(name = CfgVal.dataVolumeName,
                               emptyDir = V1EmptyDirVolumeSource())

        let podSpec =
            V1PodSpec
                (initContainers = [| CoreContainerForCommand [| "new-db" |];
                                   CoreContainerForCommand [| "new-hist"; CfgVal.localHistName |];
                                   CoreContainerForCommand [| "force-scp" |] |],
                 containers = [| WithReadinessProbe (CoreContainerForCommand [| "run" |]) |],
                 volumes = [| cfgVol; dataVol |])
        V1PodTemplateSpec
                (spec = podSpec,
                 metadata = V1ObjectMeta(labels = CfgVal.labels,
                                         namespaceProperty = self.NamespaceProperty()))

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
    member self.ToStatefulSet (setName:string) : V1StatefulSet =
        let statefulSetSpec =
            V1StatefulSetSpec
                (selector = V1LabelSelector(matchLabels = CfgVal.labels),
                 serviceName = CfgVal.serviceName,
                 template = self.ToPodTemplateSpec(),
                 replicas = System.Nullable<int>(self.NumPeers))
        let statefulSet = V1StatefulSet(metadata = self.NamespacedMeta setName,
                                        spec = statefulSetSpec)
        ignore( statefulSet.Validate())
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
        let perPodService i =
            let name = CfgVal.peerShortName i
            let dnsName = CfgVal.peerDNSName self.networkNonce i
            let spec = V1ServiceSpec(``type`` = "ExternalName",
                                     ports = [|V1ServicePort(port = CfgVal.httpPort)|],
                                     externalName = dnsName)
            V1Service(metadata = self.NamespacedMeta name, spec = spec)
        Array.init self.NumPeers perPodService


    // Returns an Ingress object with rules that map URLs /$namespace/peer-N/foo
    // to the per-Pod Service within $namespace named peer-N (which then, via
    // DNS mapping, goes to the Pod itself). Exposing this to external traffic
    // requires that you enable the nginx Ingress controller on your k8s
    // cluster.
    member self.ToIngress () : Extensionsv1beta1Ingress =
        let httpPortStr = IntstrIntOrString(value = CfgVal.httpPort.ToString())
        let backend (pn:string) =
            Extensionsv1beta1IngressBackend(serviceName = pn,
                                            servicePort = httpPortStr)
        let path (i:int) =
            let pn = CfgVal.peerShortName i
            Extensionsv1beta1HTTPIngressPath(path = sprintf "/%s/%s/(.*)"
                                                    (self.NamespaceProperty()) pn,
                                             backend = backend pn)
        let paths = Array.init self.NumPeers path
        let rule = Extensionsv1beta1HTTPIngressRuleValue(paths = paths)
        let rules = [|Extensionsv1beta1IngressRule(http = rule)|]
        let spec = Extensionsv1beta1IngressSpec(rules = rules)
        let annotation = Map.ofArray [|("nginx.ingress.kubernetes.io/ssl-redirect", "false");
                                       ("nginx.ingress.kubernetes.io/rewrite-target", "/$1")|]
        let meta = V1ObjectMeta(name = "stellar-core-ingress",
                                namespaceProperty = self.NamespaceProperty(),
                                annotations = annotation)
        Extensionsv1beta1Ingress(spec = spec, metadata = meta)
