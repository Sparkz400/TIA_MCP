using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

namespace TiaMcp.OpcUa;

/// <summary>An explicitly supplied artifact. Id identifies its provenance, not a namespace or search path.</summary>
public sealed record OpcUaNodeSetSource(string Id, byte[] Content);

/// <summary>Offline resolution and conservative impact analysis of selected, explicit NodeSet dependencies.</summary>
public static class OpcUaDependencyAnalysis
{
    public const int MaxFiles = 32;
    public const int MaxTotalBytes = 64 * 1024 * 1024;
    public const int MaxTotalNodes = 100000;
    public const int MaxDependencies = 200000;
    private const int MaxDiagnostics = 10000;
    private const int MaxTraversalSteps = 2000000;
    private const int MaxImpactRows = 50000;
    private const int MaxPathSteps = 200000;

    private sealed record Artifact(string Id, OpcUaModelReview.Model Model);
    private sealed record Node(Artifact Artifact, JsonObject Value);
    private sealed record Edge(string SourceId, string SourceNodeKey, string TargetNodeKey, string Kind, string Resolution, string[] Candidates);
    private sealed record Graph(Artifact[] Artifacts, string Hash, Dictionary<string, List<Node>> Nodes,
        Edge[] Edges, JsonArray ModelDependencies, JsonArray Diagnostics)
    {
        public Dictionary<string, Edge[]> Outgoing { get; } = Edges.GroupBy(e => e.SourceNodeKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    public static JsonObject Analyze(IReadOnlyList<OpcUaNodeSetSource> sources) => Bounded(Report(Build(sources)));

    public static JsonObject Compare(IReadOnlyList<OpcUaNodeSetSource> beforeSources,
        IReadOnlyList<OpcUaNodeSetSource> afterSources, JsonObject? consumerManifest = null)
    {
        var before = Build(beforeSources);
        var after = Build(afterSources);
        // Never choose a winning definition, or turn an ambiguous identity into a spurious addition/removal.
        var ambiguous = before.Nodes.Where(p => p.Value.Count != 1).Select(p => p.Key)
            .Union(after.Nodes.Where(p => p.Value.Count != 1).Select(p => p.Key)).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, JsonObject> Unique(Graph graph) => graph.Nodes.Where(p => !ambiguous.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value.Single().Value, StringComparer.Ordinal);
        var changes = OpcUaModelReview.NodeChanges(Unique(before), Unique(after));
        var result = Header("opcua-dependency-comparison.v1");
        result["beforeModelSetSha256"] = before.Hash;
        result["afterModelSetSha256"] = after.Hash;
        result["before"] = Report(before);
        result["after"] = Report(after);
        result["changes"] = changes;
        result["comparisonSkippedNodeKeys"] = JsonSerializer.SerializeToNode(ambiguous.Order(StringComparer.Ordinal));
        result["breakingChangeCount"] = changes.Count(c => Text(c!["severity"]) == "breaking");
        result["clientImpacts"] = ClientImpacts(consumerManifest, before, after, changes);
        result["unknownConsumers"] = true;
        result["artifactSetChanged"] = before.Hash != after.Hash;
        result["outcome"] = ambiguous.Count > 0 ? "ambiguousDefinitions" : changes.Count > 0 ? "changesDetected"
            : before.Hash != after.Hash ? "artifactChangesRequireReview" : "noChangesInReviewedFields";
        return Bounded(result);
    }

    private static Graph Build(IReadOnlyList<OpcUaNodeSetSource> sources)
    {
        if (sources.Count is < 1 or > MaxFiles) throw Error("OpcUaCapacity", "Supply 1 to 32 NodeSet files per snapshot.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var source in sources)
        {
            if (source is null || string.IsNullOrEmpty(source.Id) || source.Id.Length > 4096 || !ids.Add(source.Id))
                throw Error("OpcUaInvalidSources", "Source IDs must be unique, nonempty and at most 4096 characters.");
            if (source.Content is null) throw Error("OpcUaInvalidSources", "Source content is required.");
            bytes += source.Content.Length;
            if (bytes > MaxTotalBytes) throw Error("OpcUaCapacity", "NodeSet collection exceeds 64 MiB.");
        }
        var artifacts = new List<Artifact>();
        int nodeCount = 0, edgeCount = 0;
        foreach (var source in sources.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var model = OpcUaModelReview.Parse(source.Content, collectUnresolved: false, includeDefinitionDependencies: true);
            nodeCount += model.Nodes.Count;
            edgeCount += model.Dependencies.Length;
            if (nodeCount > MaxTotalNodes || edgeCount > MaxDependencies)
                throw Error("OpcUaCapacity", "NodeSet collection exceeds 100000 nodes or 200000 dependencies.");
            artifacts.Add(new(source.Id, model));
        }
        // Hashes form a sorted multiset: paths/order do not affect the binding; duplicate inputs still do.
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "opcua-model-set.v1\n" + string.Join("\n", artifacts.Select(a => a.Model.Hash).Order(StringComparer.Ordinal)) + "\n")));
        var nodes = artifacts.SelectMany(a => a.Model.Nodes.Select(n => new Node(a, n.Value)))
            .GroupBy(n => Text(n.Value["nodeKey"])!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var diagnostics = new JsonArray();
        foreach (var pair in nodes.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => p.Value.Count > 1))
            Diagnostic(diagnostics, new JsonObject { ["code"] = "AmbiguousNodeDefinition", ["nodeKey"] = pair.Key,
                ["candidateSourceIds"] = JsonSerializer.SerializeToNode(pair.Value.Select(n => n.Artifact.Id)) });
        var edges = new List<Edge>();
        foreach (var artifact in artifacts)
        foreach (var dependency in artifact.Model.Dependencies.OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Target.Key, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal))
        {
            string target = dependency.Target.Key;
            string[] candidates = nodes.TryGetValue(target, out var providers) ? providers.Select(p => p.Artifact.Id).ToArray() : [];
            string resolution = candidates.Length == 0 ? "missing" : candidates.Length > 1 ? "ambiguous"
                : nodes[dependency.Source].Count > 1 ? "ambiguousSource" : "resolved";
            var edge = new Edge(artifact.Id, dependency.Source, target, dependency.Kind, resolution, candidates);
            edges.Add(edge);
            if (resolution != "resolved")
            {
                var diagnostic = EdgeJson(edge);
                diagnostic["code"] = resolution == "missing"
                    ? dependency.Target.NamespaceUri == OpcUaNodeIdentity.StandardNamespace ? "StandardModelNotResolved" : "DependencyNodeNotProvided"
                    : resolution == "ambiguous" ? "AmbiguousDependencyNode" : "AmbiguousDependencySource";
                Diagnostic(diagnostics, diagnostic);
            }
        }
        var models = artifacts.SelectMany(a => a.Model.Models.Select(m => (Artifact: a, Model: m!)))
            .GroupBy(m => Text(m.Model["modelUri"])!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var pair in models.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => p.Value.Length > 1))
            Diagnostic(diagnostics, new JsonObject { ["code"] = "AmbiguousModelDefinition", ["modelUri"] = pair.Key,
                ["candidateSourceIds"] = JsonSerializer.SerializeToNode(pair.Value.Select(p => p.Artifact.Id)) });
        var modelDependencies = new JsonArray();
        foreach (var artifact in artifacts)
        foreach (var model in artifact.Model.Models)
        foreach (var required in model!["requiredModels"]!.AsArray())
        {
            if (modelDependencies.Count >= MaxDependencies) throw Error("OpcUaCapacity", "Too many required-model declarations.");
            string uri = Text(required!["modelUri"])!;
            if (uri.Length is 0 or > 4096 || !Uri.TryCreate(uri, UriKind.Absolute, out _))
                throw Error("OpcUaInvalidIdentity", "Required model URIs must be bounded absolute URIs.");
            var providers = models.GetValueOrDefault(uri);
            string resolution = providers is null ? "missing" : providers.Length > 1 ? "ambiguous" : "resolved";
            var row = new JsonObject { ["sourceId"] = artifact.Id, ["sourceModelUri"] = model["modelUri"]!.DeepClone(),
                ["requiredModel"] = required.DeepClone(), ["resolution"] = resolution,
                ["versionCompatibility"] = "notEvaluated", ["candidateSourceIds"] = JsonSerializer.SerializeToNode(
                    providers?.Select(p => p.Artifact.Id).ToArray() ?? []) };
            row["requiredModel"]!.AsObject().Remove("resolution");
            modelDependencies.Add(row);
            if (resolution != "resolved")
            {
                var diagnostic = (JsonObject)row.DeepClone();
                diagnostic["code"] = resolution == "missing" ? "RequiredModelNotProvided" : "AmbiguousRequiredModel";
                Diagnostic(diagnostics, diagnostic);
            }
        }
        return new(artifacts.ToArray(), hash, nodes, edges.ToArray(), modelDependencies, diagnostics);
    }

    private static JsonObject Report(Graph graph)
    {
        var result = Header("opcua-dependencies.v1");
        result["modelSetSha256"] = graph.Hash;
        result["sources"] = new JsonArray(graph.Artifacts.Select(a => (JsonNode)new JsonObject {
            ["id"] = a.Id, ["sha256"] = a.Model.Hash, ["bytes"] = a.Model.Bytes, ["nodeCount"] = a.Model.Nodes.Count,
            ["models"] = a.Model.Models.DeepClone(), ["envelopeSha256"] = a.Model.EnvelopeHash }).ToArray());
        result["nodeCount"] = graph.Nodes.Count;
        result["nodes"] = new JsonArray(graph.Nodes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject {
            ["nodeKey"] = p.Key, ["resolution"] = p.Value.Count == 1 ? "unique" : "ambiguous",
            ["sourceIds"] = JsonSerializer.SerializeToNode(p.Value.Select(n => n.Artifact.Id)) }).ToArray());
        result["dependencies"] = new JsonArray(graph.Edges.Select(e => (JsonNode)EdgeJson(e)).ToArray());
        result["requiredModels"] = graph.ModelDependencies.DeepClone();
        result["diagnostics"] = graph.Diagnostics.DeepClone();
        result["selectedDependenciesResolved"] = graph.Diagnostics.Count == 0;
        result["outcome"] = graph.Diagnostics.Count == 0 ? "selectedDependenciesResolved" : "unresolvedDependencies";
        return result;
    }

    private static JsonArray ClientImpacts(JsonObject? manifest, Graph before, Graph after, JsonArray changes)
    {
        var result = new JsonArray();
        if (manifest is null) return result;
        if (Encoding.UTF8.GetByteCount(manifest.ToJsonString()) > 1024 * 1024) throw Error("OpcUaCapacity", "Consumer manifest exceeds 1 MiB.");
        if (manifest.Any(p => p.Key is not ("schemaVersion" or "modelSetSha256" or "consumers")))
            throw Error("OpcUaInvalidConsumers", "Unknown model-set consumer manifest field.");
        if (Text(manifest["schemaVersion"]) != "opcua-consumers.v2" || Text(manifest["modelSetSha256"]) != before.Hash)
            throw Error("OpcUaStaleConsumers", "Consumer manifest must bind the exact before model-set SHA-256 and v2 schema.");
        if (manifest["consumers"] is not JsonArray consumers || consumers.Count > 1000)
            throw Error("OpcUaInvalidConsumers", "Expected at most 1000 consumers.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int totalKeys = 0, work = 0, rows = 0, pathSteps = 0;
        var changed = changes.GroupBy(c => Text(c!["nodeKey"])!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var item in consumers)
        {
            if (item is not JsonObject consumer || consumer.Any(p => p.Key is not ("id" or "nodeKeys")))
                throw Error("OpcUaInvalidConsumers", "Expected a consumer object with id and nodeKeys.");
            string id = Text(consumer["id"]) ?? "";
            if (id.Length is 0 or > 256 || !ids.Add(id) || consumer["nodeKeys"] is not JsonArray keys)
                throw Error("OpcUaInvalidConsumers", "Consumers require unique IDs and nodeKeys.");
            totalKeys += keys.Count;
            if (totalKeys > 20000) throw Error("OpcUaCapacity", "Consumer manifest exceeds 20000 declared dependencies.");
            var roots = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var value in keys)
            {
                string key = Text(value) ?? "";
                if (!before.Nodes.TryGetValue(key, out var providers) || providers.Count != 1 || !roots.Add(key))
                    throw Error("OpcUaInvalidConsumers", "Consumer keys must be unique and resolve unambiguously in the before set.");
            }
            // Traverse each snapshot independently. A union could invent a path present in neither snapshot.
            var oldPaths = Traverse(before, roots, ref work);
            var newPaths = Traverse(after, roots, ref work);
            var direct = new JsonArray();
            var indirect = new JsonArray();
            foreach (string key in oldPaths.Keys.Union(newPaths.Keys).Order(StringComparer.Ordinal))
            {
                if (!changed.TryGetValue(key, out var nodeChanges)) continue;
                if (roots.Contains(key))
                {
                    foreach (var change in nodeChanges) { CountRow(ref rows); direct.Add(ChangeSummary(change!)); }
                    continue;
                }
                bool old = oldPaths.ContainsKey(key);
                var paths = old ? oldPaths : newPaths;
                foreach (var change in nodeChanges)
                {
                    CountRow(ref rows);
                    var path = new List<Edge>();
                    string cursor = key;
                    while (paths[cursor] is Edge edge)
                    {
                        if (++pathSteps > MaxPathSteps) throw Error("OpcUaCapacity", "Impact paths exceed 200000 steps.");
                        path.Add(edge); cursor = edge.SourceNodeKey;
                    }
                    path.Reverse();
                    var impact = ChangeSummary(change!);
                    impact["consumedNodeKey"] = cursor;
                    impact["graphSnapshot"] = old ? "before" : "after";
                    impact["path"] = new JsonArray(path.Select(e => (JsonNode)EdgeJson(e)).ToArray());
                    indirect.Add(impact);
                }
            }
            var gaps = new JsonArray();
            foreach (var (graph, paths, snapshot) in new[] { (before, oldPaths, "before"), (after, newPaths, "after") })
            foreach (var key in paths.Keys.Order(StringComparer.Ordinal))
            foreach (var edge in graph.Outgoing.GetValueOrDefault(key) ?? [])
                if (edge.Resolution != "resolved")
                {
                    CountRow(ref rows);
                    var gap = EdgeJson(edge); gap["graphSnapshot"] = snapshot; gaps.Add(gap);
                }
            result.Add(new JsonObject { ["consumerId"] = id, ["directChanges"] = direct, ["indirectChanges"] = indirect,
                ["dependencyGaps"] = gaps, ["status"] = direct.Count > 0 ? "affected" : indirect.Count > 0 ? "potentiallyAffected" : "noKnownImpact",
                ["dependencyCoverage"] = "selectedExplicitEdgesOnly" });
        }
        return result;
    }

    private static Dictionary<string, Edge?> Traverse(Graph graph, IEnumerable<string> roots, ref int work)
    {
        var paths = new Dictionary<string, Edge?>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (string root in roots) { paths.Add(root, null); queue.Enqueue(root); }
        while (queue.TryDequeue(out var current))
        {
            if (++work > MaxTraversalSteps) throw Error("OpcUaCapacity", "Consumer traversal exceeds 2000000 steps; reduce scope.");
            foreach (var edge in graph.Outgoing.GetValueOrDefault(current) ?? [])
            {
                if (++work > MaxTraversalSteps) throw Error("OpcUaCapacity", "Consumer traversal exceeds 2000000 steps; reduce scope.");
                if (edge.Resolution == "resolved" && paths.TryAdd(edge.TargetNodeKey, edge)) queue.Enqueue(edge.TargetNodeKey);
            }
        }
        return paths;
    }

    private static JsonObject ChangeSummary(JsonNode change) => new() { ["nodeKey"] = change["nodeKey"]!.DeepClone(),
        ["field"] = change["field"]!.DeepClone(), ["severity"] = change["severity"]!.DeepClone() };
    private static JsonObject EdgeJson(Edge edge) => new() { ["sourceId"] = edge.SourceId, ["sourceNodeKey"] = edge.SourceNodeKey,
        ["targetNodeKey"] = edge.TargetNodeKey, ["kind"] = edge.Kind, ["resolution"] = edge.Resolution,
        ["candidateSourceIds"] = JsonSerializer.SerializeToNode(edge.Candidates) };
    private static JsonObject Header(string version) => new() { ["schemaVersion"] = version,
        ["schemaSha256"] = OpcUaModelReview.SchemaSha256, ["nativeQualified"] = false, ["targetProbed"] = false,
        ["tiaImportAccepted"] = false, ["plcMappingsResolved"] = false, ["compatibilityCertified"] = false,
        ["dependencyClosureComplete"] = false, ["requiredModelVersionsEvaluated"] = false,
        ["limitations"] = new JsonArray(
            "Only explicitly supplied files are searched; standard nodes are not implicit or downloaded.",
            "Selected edges: DataType, Reference target/type, ParentNodeId, MethodDeclarationId, Definition Field DataType and node RolePermissions.",
            "Reference targets are followed from the declaring node, regardless of IsForward; inverse relationships are not inferred.",
            "RequiredModels resolve by URI only; version compatibility and model dependency cycles are not evaluated.",
            "Values (including method arguments), vendor extensions, inherited permissions and obsolete Definition BaseType remain opaque.",
            "Indirect impacts are conservative review candidates, not proof of client breakage; one shortest path per changed node per consumer is shown, preferring the before snapshot.",
            "Set comparison reports node changes; artifact hashes and model metadata expose other changes for review. Keep the original inputs.") };
    private static void Diagnostic(JsonArray diagnostics, JsonObject value)
    {
        if (diagnostics.Count >= MaxDiagnostics) throw Error("OpcUaCapacity", "More than 10000 dependency diagnostics; reduce scope.");
        diagnostics.Add(value);
    }
    private static void CountRow(ref int rows)
    {
        if (++rows > MaxImpactRows) throw Error("OpcUaCapacity", "Consumer impacts and gaps exceed 50000 rows.");
    }
    private static JsonObject Bounded(JsonObject result)
    {
        if (Encoding.UTF8.GetByteCount(result.ToJsonString()) > OpcUaModelReview.MaxReportBytes)
            throw Error("OpcUaCapacity", "Dependency report exceeds 32 MiB; reduce scope.");
        return result;
    }
    private static string? Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static BridgeException Error(string code, string message) => new(code, message);
}
