// Typed models for the subset of the Gateway API (gateway.networking.k8s.io/v1)
// that Supercluster needs to route to per-pod core/history endpoints.
//
// The official KubernetesClient package ships no Gateway API models (CRDs are
// out of scope for it), so these POCOs are hand-maintained to the upstream
// gateway-api v1 schema. Only the fields Supercluster uses are modelled.
// Usable with GenericClient<HTTPRoute> thanks to the [KubernetesEntity] attribute.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using k8s;
using k8s.Models;

namespace GatewayApiModels
{
    [KubernetesEntity(Group = "gateway.networking.k8s.io", ApiVersion = "v1", Kind = "HTTPRoute", PluralName = "httproutes")]
    public class HTTPRoute : IKubernetesObject<V1ObjectMeta>, ISpec<HTTPRouteSpec>
    {
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = "gateway.networking.k8s.io/v1";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "HTTPRoute";

        [JsonPropertyName("metadata")]
        public V1ObjectMeta Metadata { get; set; }

        [JsonPropertyName("spec")]
        public HTTPRouteSpec Spec { get; set; }

        [JsonPropertyName("status")]
        public HTTPRouteStatus Status { get; set; }
    }

    // Minimal route status: each parent (Gateway) the route attached to reports
    // conditions (notably "Accepted"). Used to wait for the gateway to admit the
    // route before the driver sends traffic.
    public class HTTPRouteStatus
    {
        [JsonPropertyName("parents")]
        public IList<RouteParentStatus> Parents { get; set; }
    }

    public class RouteParentStatus
    {
        [JsonPropertyName("conditions")]
        public IList<V1Condition> Conditions { get; set; }
    }

    [KubernetesEntity(Group = "gateway.networking.k8s.io", ApiVersion = "v1", Kind = "HTTPRouteList", PluralName = "httproutes")]
    public class HTTPRouteList : IKubernetesObject<V1ListMeta>, IItems<HTTPRoute>
    {
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = "gateway.networking.k8s.io/v1";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "HTTPRouteList";

        [JsonPropertyName("metadata")]
        public V1ListMeta Metadata { get; set; }

        [JsonPropertyName("items")]
        public IList<HTTPRoute> Items { get; set; }
    }

    public class HTTPRouteSpec
    {
        [JsonPropertyName("parentRefs")]
        public IList<ParentReference> ParentRefs { get; set; }

        [JsonPropertyName("hostnames")]
        public IList<string> Hostnames { get; set; }

        [JsonPropertyName("rules")]
        public IList<HTTPRouteRule> Rules { get; set; }
    }

    public class ParentReference
    {
        [JsonPropertyName("group")]
        public string Group { get; set; }

        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        [JsonPropertyName("namespace")]
        public string Namespace { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("sectionName")]
        public string SectionName { get; set; }

        [JsonPropertyName("port")]
        public int? Port { get; set; }
    }

    public class HTTPRouteRule
    {
        [JsonPropertyName("matches")]
        public IList<HTTPRouteMatch> Matches { get; set; }

        [JsonPropertyName("filters")]
        public IList<HTTPRouteFilter> Filters { get; set; }

        [JsonPropertyName("backendRefs")]
        public IList<HTTPBackendRef> BackendRefs { get; set; }
    }

    public class HTTPRouteMatch
    {
        [JsonPropertyName("path")]
        public HTTPPathMatch Path { get; set; }
    }

    public class HTTPPathMatch
    {
        // "Exact" | "PathPrefix" | "RegularExpression"
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }
    }

    public class HTTPRouteFilter
    {
        // "RequestHeaderModifier" | "URLRewrite" | ...
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("urlRewrite")]
        public HTTPURLRewriteFilter UrlRewrite { get; set; }
    }

    public class HTTPURLRewriteFilter
    {
        [JsonPropertyName("hostname")]
        public string Hostname { get; set; }

        [JsonPropertyName("path")]
        public HTTPPathModifier Path { get; set; }
    }

    public class HTTPPathModifier
    {
        // "ReplaceFullPath" | "ReplacePrefixMatch"
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("replaceFullPath")]
        public string ReplaceFullPath { get; set; }

        [JsonPropertyName("replacePrefixMatch")]
        public string ReplacePrefixMatch { get; set; }
    }

    public class HTTPBackendRef
    {
        [JsonPropertyName("group")]
        public string Group { get; set; }

        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("namespace")]
        public string Namespace { get; set; }

        [JsonPropertyName("port")]
        public int? Port { get; set; }

        [JsonPropertyName("weight")]
        public int? Weight { get; set; }
    }
}
