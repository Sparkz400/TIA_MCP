#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Indexing
{
    // The explorer consumes these read-only observations. HMI family adapters and
    // typed instruction readers remain owned by their own workstreams.
    internal interface IProjectModelProvider
    {
        string ProviderId { get; }
        bool Supports(JObject node, string facet);
        JObject Inspect(ProjectModelProviderRequest request);
    }

    internal sealed class ProjectModelProviderRequest
    {
        private readonly JObject _reference, _node, _options;
        internal JObject Reference => (JObject)_reference.DeepClone();
        internal JObject Node => (JObject)_node.DeepClone();
        internal JObject Options => (JObject)_options.DeepClone();
        internal string ArtifactDirectory => _options.Value<string>("artifactDirectory");
        internal string Facet { get; }
        internal int MaxItems { get; }
        internal int MaxBytes { get; }
        internal ProjectModelProviderRequest(JObject reference, JObject node, string facet, int maxItems = 200, int maxBytes = 65536, JObject options = null)
        {
            if (reference == null || (string)reference["schemaVersion"] != "project-model.v1"
                || new[] { "snapshotId", "sessionGeneration", "projectPath", "nodeId", "objectPath" }.Any(field => string.IsNullOrEmpty((string)reference[field]))
                || node == null || (string)node["origin"] != "native"
                || (string)reference["nodeId"] != (string)node["nodeId"] || (string)reference["objectPath"] != (string)node["objectPath"])
                throw new ProjectModelStore.ModelException("InvalidSelection", "Providers require the exact native node reference.");
            if (maxItems < 1 || maxItems > 200 || maxBytes < 1024 || maxBytes > 65536)
                throw new ProjectModelStore.ModelException("InvalidRequest", "Provider request exceeds bounded item or byte budgets.");
            if (options != null && (options.Properties().Any(p => p.Name != "artifactDirectory" && p.Name != "maxItems") || Encoding.UTF8.GetByteCount(options.ToString(Formatting.None)) > 8192))
                throw new ProjectModelStore.ModelException("InvalidRequest", "Provider options permit only bounded artifactDirectory and maxItems read options.");
            _reference = (JObject)reference.DeepClone(); _node = (JObject)node.DeepClone(); _options = options == null ? new JObject() : (JObject)options.DeepClone(); Facet = facet; MaxItems = maxItems; MaxBytes = maxBytes;
        }
    }

    internal sealed class ProjectModelProviders
    {
        private readonly IProjectModelProvider[] _providers;
        internal ProjectModelProviders(IEnumerable<IProjectModelProvider> providers = null)
        {
            _providers = (providers ?? new IProjectModelProvider[0]).ToArray();
            if (_providers.Any(p => p == null || string.IsNullOrEmpty(p.ProviderId)) || _providers.Select(p => p.ProviderId).Distinct(StringComparer.Ordinal).Count() != _providers.Length)
                throw new ArgumentException("Provider identifiers must be present and unique.");
        }

        internal JObject Inspect(ProjectModelProviderRequest request)
        {
            var applicable = _providers.Where(p => p.Supports(request.Node, request.Facet)).ToArray();
            if (applicable.Length == 0)
            {
                var unavailable = Describe(request.Reference, request.Facet);
                Validate(unavailable, request, unavailable.Value<string>("provider"));
                return unavailable;
            }
            if (applicable.Length > 1) throw new ProjectModelStore.ModelException("AmbiguousProvider", "Multiple providers claim this exact node/facet; explicitly select a qualified family provider.");
            JObject response;
            try { response = applicable[0].Inspect(request); }
            catch (ProjectModelStore.ModelException) { throw; }
            catch (Exception error)
            {
                response = Describe(request.Reference, request.Facet);
                response["provider"] = applicable[0].ProviderId; response["status"] = "unavailable";
                response["reason"] = "Provider failed: " + error.GetType().Name;
            }
            Validate(response, request, applicable[0].ProviderId);
            return (JObject)response.DeepClone();
        }

        internal static JObject Describe(JObject reference, string facet)
        {
            bool hmi = (facet ?? "").StartsWith("hmi", StringComparison.OrdinalIgnoreCase);
            string provider = hmi ? "hmi" : "typedLadFbd";
            string reason = hmi ? "HMI family provider is not installed or qualified in this explorer build. Device metadata does not establish HMI content coverage."
                : "Typed LAD/FBD preview provider is not installed or qualified in this explorer build. Raw native XML is not a typed instruction preview.";
            string path = (string)reference?["objectPath"];
            return new JObject { ["schemaVersion"] = "project-model-provider.v1", ["provider"] = provider, ["reference"] = reference?.DeepClone(),
                ["facet"] = facet, ["status"] = "unsupported", ["reason"] = reason, ["capturedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = hmi ? "hmiContent" : "graphicalNetwork" },
                ["coverage"] = new JArray(new JObject { ["provider"] = provider, ["scope"] = path ?? "selectedObject", ["status"] = "unsupported",
                    ["visited"] = 0, ["returned"] = 0, ["warnings"] = new JArray(reason), ["continuationAvailable"] = false }),
                ["attachments"] = new JArray(), ["engineeringVerified"] = false, ["runtimeVerified"] = false };
        }

        internal static void Validate(JObject response, ProjectModelProviderRequest request, string providerId)
        {
            if (response == null || (string)response["schemaVersion"] != "project-model-provider.v1" || (string)response["provider"] != providerId
                || !JToken.DeepEquals(response["reference"], request.Reference) || (string)response["facet"] != request.Facet)
                throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Provider response must preserve version, owner identity and requested facet.");
            string status = (string)response["status"];
            if (!new[] { "available", "partial", "unsupported", "unavailable", "protected", "unresolved" }.Contains(status))
                throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Provider status is not recognized.");
            var revision = response["contentRevision"] as JObject;
            if (revision == null || (string)revision["scope"] == "exposedMetadata" || (string)revision["scope"] == "metadata"
                || revision["includesContent"]?.Value<bool>() == false || (string)revision["value"] != null && (string)revision["algorithm"] != "sha256")
                throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Content revisions must be independent of metadata revisions and name their hash algorithm.");
            if (response["coverage"] is not JArray || response["attachments"] is not JArray attachments || response["capturedAtUtc"] == null)
                throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Provider response requires coverage, timestamp and bounded attachments.");
            if (attachments.Count > request.MaxItems || Encoding.UTF8.GetByteCount(response.ToString(Formatting.None)) > request.MaxBytes)
                throw new ProjectModelStore.ModelException("ModelCapacity", "Provider response exceeds its requested budgets.");
            if (status != "available" && status != "partial" && attachments.Count != 0)
                throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Unavailable providers cannot publish content attachments.");
            foreach (var attachment in attachments.OfType<JObject>())
            {
                if ((string)attachment["kind"] != "hmiScreen" && (string)attachment["kind"] != "ladFbdNetwork")
                    throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Unsupported preview attachment kind.");
                if ((string)attachment["source"]?["objectPath"] != (string)request.Reference["objectPath"]
                    || !JToken.DeepEquals(attachment["source"]?["contentRevision"], revision) || attachment["source"]?["capturedAtUtc"] == null)
                    throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Preview attachment must preserve exact source and applicable content revision.");
                if (attachment["content"] is not JObject || attachment["coverage"] is not JObject)
                    throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Preview requires structured content and explicit coverage; arbitrary HTML/scripts are not accepted.");
            }
            if (attachments.OfType<JObject>().Count() != attachments.Count) throw new ProjectModelStore.ModelException("InvalidProviderResponse", "Preview attachments must be objects.");
        }
    }
}
