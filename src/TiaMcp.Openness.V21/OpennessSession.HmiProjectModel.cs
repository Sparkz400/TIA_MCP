using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Indexing;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        partial void RegisterProjectModelProviders(List<IProjectModelProvider> providers)
        {
            providers.Add(new HmiProjectModelProvider(this));
        }

        partial void CaptureHmiProjectModel(Project project, string hmiPath, string parentId, ModelCapture capture)
        {
            string[] sections = { "connections", "tags", "screens", "alarms", "scripts", "resources" };
            var paths = capture.Nodes.OfType<JObject>().Where(n => n.Value<string>("objectPath") != null)
                .ToDictionary(n => n.Value<string>("objectPath"), n => n.Value<string>("nodeId"), StringComparer.Ordinal);
            // Reserve half for retained nodes; every native traversal and inserted node consumes the shared budget.
            int allowance = capture.Remaining / (sections.Length * 2);
            foreach (string section in sections)
            {
                if (capture.Remaining < 2 || allowance < 1 || capture.Budget.Truncated)
                {
                    capture.ProviderCoverage.Add(ModelCoverage("hmi." + section, hmiPath, "partial", 0, 0, new JArray("Shared native traversal budget exhausted before this section.")));
                    break;
                }
                JObject observed;
                int visited = 0;
                try
                {
                    observed = HmiInspect(new JObject { ["projectName"] = project.Name, ["hmiPath"] = hmiPath, ["section"] = section,
                        ["limit"] = 200, ["maxVisited"] = allowance, ["maxBytes"] = 262144 });
                    visited = observed["coverage"]?.Value<int?>("visited") ?? allowance;
                    for (int i = 0; i < visited && capture.Budget.Take(); i++) { }
                    var rows = (observed["items"] as JArray ?? new JArray()).OfType<JObject>().OrderBy(r => r["identity"]?.Value<string>("objectPath"), StringComparer.Ordinal).ToArray();
                    foreach (var row in rows)
                    {
                        if (capture.Remaining == 0 || capture.Budget.Truncated) break;
                        var identity = row["identity"] as JObject;
                        if (identity == null) continue;
                        string path = identity.Value<string>("objectPath"), parentPath = row.Value<string>("parentPath");
                        string owner = HmiModelParent(parentPath, hmiPath, parentId, paths, capture, 0);
                        var metadata = (JObject)row.DeepClone();
                        metadata.Remove("identity");
                        var stableIdentity = (JObject)identity.DeepClone(); stableIdentity.Remove("sessionGeneration");
                        metadata["hmiIdentity"] = stableIdentity;
                        metadata["observedParentPath"] = parentPath;
                        metadata["scriptsExecuted"] = false;
                        // An export hash belongs to a detail observation, not the metadata revision.
                        if (metadata["source"] is JObject source) { source.Remove("exportSha256"); source.Remove("artifact"); }
                        string id = capture.Add(path, owner, identity.Value<string>("kind"), row.Value<string>("name"), null, hmiPath, metadata);
                        if (id != null) paths[path] = id;
                    }
                    string state = observed["coverage"]?.Value<string>("status") ?? "unavailable";
                    if (state == "truncated" || observed["nextOffset"]?.Type == JTokenType.Integer) state = "partial";
                    if (state == "failed") state = "unavailable";
                    var coverage = ModelCoverage("hmi." + section, hmiPath, state, visited, rows.Length,
                        new JArray((observed["diagnostics"] as JArray ?? new JArray()).Select(d => d.ToString(Formatting.None))));
                    coverage["nativeCoverage"] = observed["coverage"]?.DeepClone();
                    coverage["continuationAvailable"] = observed["nextOffset"]?.Type == JTokenType.Integer;
                    coverage["nextOffset"] = observed["nextOffset"]?.DeepClone();
                    capture.ProviderCoverage.Add(coverage);
                }
                catch (Exception error)
                {
                    // Failed lookup can consume its entire native allowance before throwing.
                    if (visited == 0) for (int i = 0; i < allowance && capture.Budget.Take(); i++) { }
                    capture.ProviderCoverage.Add(ModelCoverage("hmi." + section, hmiPath, "unavailable", allowance, 0, new JArray(error.Message)));
                }
            }
            capture.ProviderCoverage.Add(ModelCoverage("hmi.items", hmiPath, "partial", 0, 0,
                new JArray("Screen items and original Classic XML are loaded through selected-screen details; this metadata capture does not claim item completeness.")));
        }

        private static string HmiModelParent(string path, string hmiPath, string rootId, Dictionary<string, string> paths, ModelCapture capture, int depth)
        {
            if (path == null || path == hmiPath || !path.StartsWith(hmiPath + "/", StringComparison.Ordinal) || depth > 64) return rootId;
            if (paths.TryGetValue(path, out var known)) return known;
            int slash = path.LastIndexOf('/');
            string parent = HmiModelParent(path.Substring(0, slash), hmiPath, rootId, paths, capture, depth + 1);
            string id = capture.Add(path, parent, "hmiNavigation", Uri.UnescapeDataString(path.Substring(slash + 1)), null, hmiPath,
                new JObject { ["semantics"] = "Virtual HMI navigation ancestor inferred from an observed object's path; not a native destination." }, false);
            if (id != null) paths[path] = id;
            return id ?? rootId;
        }

        private sealed class HmiProjectModelProvider : IProjectModelProvider
        {
            private readonly OpennessSession _session;
            internal HmiProjectModelProvider(OpennessSession session) { _session = session; }
            public string ProviderId => "hmi.v21";
            public bool Supports(JObject node, string facet) => (node.Value<string>("kind") ?? "").StartsWith("hmi", StringComparison.OrdinalIgnoreCase) && (facet == "hmiDetails" || facet == "hmiPreview");
            public JObject Inspect(ProjectModelProviderRequest request)
            {
                var result = ProjectModelProviders.Describe(request.Reference, request.Facet);
                result["provider"] = ProviderId;
                if (request.Facet == "hmiPreview")
                {
                    result["reason"] = "Native screen rendering is not exposed by this provider. Original native attributes, item geometry and unresolved dynamics are available in details; project scripts are never executed.";
                    result["coverage"] = new JArray(ModelCoverage(ProviderId, request.Node.Value<string>("objectPath"), "unsupported", 0, 0,
                        new JArray(result.Value<string>("reason"))));
                    return result;
                }
                var node = request.Node;
                var args = _session.HmiModelArguments(request.Reference, node);
                args["section"] = node.Value<string>("kind") == "hmi" ? "screens" : HmiInspectionRules.Section(node.Value<string>("kind"));
                if (node.Value<string>("kind") == "hmiScreen" && HmiProjectModelRules.ScreenItemsSupported(
                    node["metadata"]?["hmiIdentity"]?.Value<string>("family"), node["metadata"]?.Value<string>("screenRole")))
                { args["section"] = "items"; args["screenPath"] = node["objectPath"]; args["includeScreenOwner"] = true; }
                if (node.Value<string>("kind") == "hmiScreenItem") args["screenPath"] = HmiOwningScreenPath(node);
                args["limit"] = request.MaxItems; args["maxBytes"] = Math.Max(4096, request.MaxBytes / 2);
                args["artifactDirectory"] = request.ArtifactDirectory;
                var observed = _session.HmiInspect(args);
                string state = observed["coverage"]?.Value<string>("status");
                result["status"] = HmiProjectModelRules.ProviderState(state, (observed["items"] as JArray)?.Count ?? 0);
                result["reason"] = "Bounded native HMI observation; exact artifact and property evidence are retained independently from explorer metadata.";
                result["observation"] = observed;
                result["coverage"] = new JArray(new JObject { ["provider"] = ProviderId, ["scope"] = node["objectPath"], ["status"] = HmiProjectModelRules.CoverageState(state),
                    ["visited"] = observed["coverage"]?["visited"], ["returned"] = (observed["items"] as JArray)?.Count ?? 0,
                    ["warnings"] = HmiProjectModelRules.Warnings(observed["diagnostics"]), ["continuationAvailable"] = observed["nextOffset"]?.Type == JTokenType.Integer });
                result["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = "Complete HMI content; metadataRevision and individual exports do not certify all dependencies." };
                if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > request.MaxBytes)
                {
                    result.Remove("observation"); result["status"] = "partial";
                    result["reason"] = "Observation exceeds explorer detail budget. Use hmi_inspect with this exact identity and a narrower section or larger byte budget.";
                }
                return result;
            }
        }

        private JObject HmiModelArguments(JObject reference, JObject node)
        {
            var identity = node["metadata"]?["hmiIdentity"] as JObject;
            var args = identity == null ? new JObject { ["hmiPath"] = node["objectPath"], ["family"] = node["metadata"]?["family"] } : (JObject)identity.DeepClone();
            // A model node carries identity schema v2, while inspection/reference
            // requests use schema v1. These version numbers have different roles.
            args["schemaVersion"] = 1;
            args["projectPath"] = reference["projectPath"]?.DeepClone();
            args["sessionGeneration"] = _hmiSessionGeneration;
            args["projectName"] = _projectModels.Header(reference.Value<string>("snapshotId"))["project"]["name"];
            args["maxVisited"] = 5000;
            return args;
        }

        private static string HmiOwningScreenPath(JObject node)
        {
            string identityPath = node["metadata"]?["hmiIdentity"]?.Value<string>("screenPath");
            if (identityPath != null) return identityPath;
            string path = node.Value<string>("objectPath") ?? "";
            foreach (string marker in new[] { "/exportItems/", "/items/" })
            { int position = path.LastIndexOf(marker, StringComparison.Ordinal); if (position >= 0) return path.Substring(0, position); }
            return null;
        }

        partial void ReadHmiProjectModelRelationships(JObject snapshot, JObject reference, JObject node, JObject args, ref JObject result)
        {
            if (!(node.Value<string>("kind") ?? "").StartsWith("hmi", StringComparison.OrdinalIgnoreCase)) return;
            var request = HmiModelArguments(reference, node);
            request["artifactDirectory"] = args["artifactDirectory"]?.DeepClone();
            // Include native alarm foreign keys so incoming tag relationships and
            // selected alarm nodes use the same bounded evidence as the HMI tool.
            request["includeAlarms"] = true;
            if (node.Value<string>("kind") == "hmiScreen") request["screenPath"] = node["objectPath"];
            if (node.Value<string>("kind") == "hmiScreenItem") request["screenPath"] = HmiOwningScreenPath(node);
            request["limit"] = 200; request["maxBytes"] = 262144;
            var observed = HmiReferences(request);
            var nodes = snapshot["nodes"].OfType<JObject>().Where(n => n.Value<string>("objectPath") != null).ToDictionary(n => n.Value<string>("objectPath"), StringComparer.Ordinal);
            var edges = new JArray();
            foreach (JObject relation in observed["relationships"] as JArray ?? new JArray())
            {
                string sourcePath = relation.Value<string>("sourcePath"), targetPath = (relation["target"] as JObject)?.Value<string>("objectPath");
                JObject source = null, target = null;
                if (sourcePath != null) nodes.TryGetValue(sourcePath, out source);
                if (targetPath != null) nodes.TryGetValue(targetPath, out target);
                bool resolved = relation.Value<string>("resolution") == "resolved" && source != null && target != null;
                string nativeKind = relation.Value<string>("relation") ?? "hmiBinding";
                edges.Add(new JObject { ["edgeId"] = "hmi-" + ProjectIndexStore.Hash(HmiProjectModelRules.EdgeKey(relation)),
                    ["source"] = source == null ? null : ModelEndpoint(source), ["target"] = target == null ? null : ModelEndpoint(target),
                    ["kind"] = nativeKind == "hmiFaceplateTypeVersion" ? "libraryVersion" : nativeKind == "hmiScreenNavigation" ? "screenNavigation" : "hmiBinding",
                    ["evidence"] = new JObject { ["origin"] = relation["evidence"], ["resolution"] = resolved ? "resolved" : "unresolved",
                        ["sourcePath"] = sourcePath, ["location"] = relation["sourceProperty"], ["capturedAtUtc"] = observed["observedAtUtc"],
                        ["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = "nativeHmiRelationships" }, ["native"] = relation.DeepClone() },
                    ["unresolved"] = resolved ? null : new JObject { ["reason"] = "Native relationship or exact captured endpoint is unresolved; no name-based substitution.", ["native"] = relation.DeepClone() } });
            }
            snapshot["edges"] = edges;
            result = ProjectModelStore.QueryRelationships(snapshot, node.Value<string>("nodeId"), (args["kinds"] as JArray)?.Values<string>().ToArray(), args.Value<string>("direction") ?? "both",
                IndexInteger(args, "depth", 1, 1, 5), IndexInteger(args, "maxNodes", 100, 1, 500), IndexInteger(args, "maxEdges", 200, 1, 1000));
            result["reference"] = reference.DeepClone(); result["nativeCoverage"] = observed["coverage"]?.DeepClone();
            result["scriptsExecuted"] = false;
            ((JArray)result["coverage"]).Add(ModelCoverage("hmi.relationships", node.Value<string>("objectPath"), "partial", 0, edges.Count,
                new JArray("Dynamic and incomplete mappings remain unresolved. Content observed separately after metadata verification.")));
            HmiProjectModelRules.RetainUncaptured(result, edges, node.Value<string>("objectPath"), (args["kinds"] as JArray)?.Values<string>().ToArray(),
                args.Value<string>("direction") ?? "both", IndexInteger(args, "maxEdges", 200, 1, 1000));
        }
    }
}
