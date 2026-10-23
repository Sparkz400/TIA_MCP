using System.Text.Json.Nodes;

namespace TiaMcp.Core.Engineering;

/// <summary>Versioned context closure; supplied links never become saved graph evidence.</summary>
internal static class CompositeEngineeringConsumers
{
    private const int MaxAssociations = 10000, MaxCandidates = 4096, MaxSavedLinks = 2048, MaxDepth = 128;

    internal static JsonObject Build(string phase, string reviewId, JsonObject model,
        Dictionary<string, JsonObject> connections, Dictionary<string, JsonObject> technology,
        JsonObject combined, int edgeBudget, Action<JsonNode> charge)
    {
        var originalNodes = model["nodes"]!.AsArray(); var nodes = originalNodes.OfType<JsonObject>().ToArray();
        var byPath = nodes.Where(n => n["objectPath"] is JsonValue v && v.TryGetValue<string>(out _)).ToLookup(n => (string)n["objectPath"]!, StringComparer.Ordinal);
        var byId = nodes.Where(n => n["nodeId"] is JsonValue v && v.TryGetValue<string>(out _)).ToLookup(n => (string)n["nodeId"]!, StringComparer.Ordinal);
        string Resolve(string? path, string? plc = null)
        {
            if (path is null) return "notDeclared";
            if (!ValidIdentity(path)) return "invalidIdentity";
            var rows = byPath[path].Take(2).ToArray();
            if (rows.Length == 0) return "missing";
            if (rows.Length != 1) return "ambiguousIdentity";
            if (rows[0]["nodeId"] is not JsonValue id || !id.TryGetValue<string>(out string? text) || text is null || !ValidIdentity(text)) return "invalidIdentity";
            if (byId[text].Count() != 1) return "ambiguousIdentity";
            if ((string?)rows[0]["origin"] == "virtual") return "virtualObject";
            if (plc is not null && (string?)rows[0]["plcPath"] != plc) return rows[0]["plcPath"] is null ? "scopeUnknown" : "outOfScope";
            return "capturedObject";
        }
        var roots = new JsonArray(); var associations = new JsonArray(); var saved = new JsonArray(); var candidates = new JsonArray();
        var reached = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var pending = new SortedDictionary<int, HashSet<string>>();
        var bounds = new HashSet<string>(StringComparer.Ordinal); var adjacent = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        int omittedAssociations = 0, omittedCandidates = 0, visits = 0, rounds = 0;
        void Add(JsonArray array, JsonObject row) { charge(row); array.Add(row); }
        void Candidate(string path, int depth)
        {
            if (reached.TryGetValue(path, out var known))
            { if ((int)known["discoveryDepth"]! <= depth) return; known["discoveryDepth"] = depth; }
            else
            {
                if (reached.Count == MaxCandidates) { bounds.Add("maxCandidates"); omittedCandidates++; return; }
                var row = new JsonObject { ["objectPath"] = path, ["nodeId"] = byPath[path].Single()["nodeId"]!.DeepClone(),
                    ["discoveryDepth"] = depth, ["nativeImpactVerified"] = false };
                reached.Add(path, row); Add(candidates, row);
            }
            if (!pending.TryGetValue(depth, out var paths)) pending[depth] = paths = new(StringComparer.Ordinal);
            paths.Add(path);
        }
        void Link(string kind, string record, int index, string role, string? from, string? to, string sourceState, JsonNode evidence, string? plc = null)
        {
            if (associations.Count == MaxAssociations) { bounds.Add("maxAssociations"); omittedAssociations++; return; }
            string fromState = Resolve(from, plc), toState = Resolve(to, plc);
            bool eligible = sourceState == "declared" && fromState == "capturedObject" && toState == "capturedObject";
            var row = new JsonObject { ["kind"] = kind, ["recordRef"] = record, ["sourceIndex"] = index, ["role"] = role,
                ["fromObjectPath"] = from, ["toObjectPath"] = to, ["sourceState"] = sourceState,
                ["fromResolution"] = fromState, ["toResolution"] = toState, ["eligible"] = eligible,
                ["traversed"] = false, ["evidence"] = evidence.DeepClone(), ["nativeLinkVerified"] = false };
            Add(associations, row);
            if (eligible) { if (!adjacent.TryGetValue(from!, out var list)) adjacent[from!] = list = []; list.Add(row); }
        }
        // Parent IDs establish captured hardware context only; they do not certify slot or interface behavior.
        for (int index = 0; index < originalNodes.Count; index++)
        {
            if (originalNodes[index] is not JsonObject child) continue;
            if ((string?)child["kind"] != "hardware" || child["parentId"] is not JsonValue parentId) continue;
            if (!parentId.TryGetValue<string>(out string? parentKey) || parentKey is null) continue;
            var parent = byId[parentKey].Take(2).ToArray();
            if (parent.Length != 1 || (string?)parent[0]["kind"] != "hardware") continue;
            Link("capturedHardwareChild", reviewId, index, "parentToChild", (string?)parent[0]["objectPath"], (string?)child["objectPath"], "declared",
                new JsonObject { ["nodeId"] = child["nodeId"]?.DeepClone(), ["objectPath"] = child["objectPath"]?.DeepClone(), ["parentId"] = parentId.DeepClone() });
        }
        foreach (var (record, report) in connections.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var observations = report["observations"]!.AsArray().Where(o => (string?)o!["phase"] == phase)
                .ToDictionary(o => (string)o!["connection"]!["id"]!, StringComparer.Ordinal);
            int index = -1;
            foreach (var row in report["observations"]!.AsArray())
            {
                index++; if ((string?)row!["phase"] != phase) continue;
                bool declared = (string?)row["local"]!["status"] == "capturedHardwareAncestry" && (string?)row["partner"]!["status"] == "capturedHardwareAncestry";
                foreach (var (from, to) in new[] { ("local", "partner"), ("partner", "local") }) foreach (string field in new[] { "ownerPath", "interfacePath" })
                    Link("connectionPeerContext", record, index, from + "To" + to + "." + field,
                        (string?)row["connection"]![from]![field], (string?)row["connection"]![to]![field], declared ? "declared" : "unresolved", row);
            }
            index = -1;
            foreach (var row in report["consumers"]!.AsArray())
            {
                index++; if ((string?)row!["phase"] != phase) continue;
                string? id = (string?)row["evidence"]!["connectionId"], target = (string?)row["evidence"]!["objectPath"];
                JsonNode? observation = id is null ? null : observations.GetValueOrDefault(id);
                foreach (string endpoint in new[] { "local", "partner" }) foreach (string field in new[] { "ownerPath", "interfacePath" })
                    Link("connectionConsumer", record, index, endpoint + "." + field, (string?)observation?["connection"]?[endpoint]?[field], target,
                        (string?)row["connectionResolution"] == "suppliedConnection" && (string?)row["objectResolution"] == "capturedObject" &&
                        (string?)observation?[endpoint]?["status"] == "capturedHardwareAncestry" ? "declared" : "unresolved", row);
            }
        }
        foreach (var (record, report) in technology.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string plc = (string)report["identity"]!["plcPath"]!; int index = -1;
            if ((string?)combined["schemaVersion"] == "composite-engineering-review.v3" && report["encoderConnections"] is JsonArray encoders)
            {
                for (int encoderIndex = 0; encoderIndex < encoders.Count; encoderIndex++)
                {
                    var row = encoders[encoderIndex]!; if ((string?)row["phase"] != phase) continue;
                    string state = CompositeEngineeringTyped.EncoderState(row, report) == "resolvedDeclaration" ? "declared" : "unresolved";
                    if ((string?)row["encoder"]!["route"] == "startdriveTelegram")
                    {
                        var target = row["encoder"]!["target"]; var evidence = row["targetEvidence"]!;
                        bool listed = target is not null && combined["contexts"]![phase]!["drives"]!.AsArray().Any(d => (string?)d == (string?)target["recordId"]);
                        if (!listed || (string?)evidence["valueDomainComparison"] != "equalDeclarations" || (string?)evidence["externalEncoderNumberCheck"] == "differentNumber") state = "unresolved";
                        Link("technologyEncoder", record, encoderIndex, "telegramHardwareToOwner", (string?)evidence["driveHardwarePath"], (string?)report["technologyPath"], state, row, plc);
                    }
                    else foreach (var target in CompositeEngineeringTyped.Targets(row))
                        Link("technologyEncoder", record, encoderIndex, target.Role + "ToOwner", (string?)target.Selector["objectPath"], (string?)report["technologyPath"], state, row, plc);
                }
            }
            var relationships = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var row in report["relationships"]!.AsArray())
            {
                index++; if ((string?)row!["phase"] != phase) continue;
                var relationship = row["relationship"]!; relationships.Add((string)relationship["id"]!, row);
                Link("technologyRelationship", record, index, "targetToSource", (string?)relationship["targetPath"], (string?)relationship["sourcePath"],
                    (string)relationship["state"]!, row, plc);
            }
            if (report["consumers"] is not JsonArray consumers) continue;
            index = -1;
            foreach (var row in consumers)
            {
                index++; if ((string?)row!["phase"] != phase) continue;
                var consumer = row["consumer"]!; string kind = (string)consumer["subjectKind"]!;
                relationships.TryGetValue((string)consumer["subjectKey"]!, out var relationship);
                string? source = kind == "parameter" ? (string?)report["technologyPath"] : (string?)relationship?["relationship"]?["sourcePath"];
                string? target = (string?)consumer["objectPath"];
                bool exact = Resolve(target, plc) == "capturedObject" && (string?)byPath[target!].Single()["nodeId"] == (string?)consumer["nodeId"];
                bool declared = (string?)consumer["state"] == "declared" && (string?)row["subjectResolution"] == "suppliedDeclaration" &&
                    (string?)row["objectResolution"] == "capturedObject" && exact && (kind == "parameter" || (string?)relationship?["relationship"]?["state"] == "declared");
                Link("technologyConsumer", record, index, kind, source, target, declared ? "declared" : "unresolved", row, plc);
            }
        }
        var selections = combined["selections"]!.AsArray().ToDictionary(s => (string)s!["key"]!, StringComparer.Ordinal);
        foreach (var impact in combined["impacts"]!.AsArray().Where(i => (string?)i!["phase"] == phase))
            foreach (var keyNode in impact!["destinationKeys"]!.AsArray())
            {
                string key = (string)keyNode!; var selection = selections[key]!; var selector = selection["selector"]!;
                string kind = (string)selector["kind"]!; var evidence = selection["evidence"]!;
                void Root(string? path, string role, string? plc = null)
                {
                    string state = Resolve(path, plc); int depth = (int)impact["destinationDepths"]![key]!;
                    Add(roots, new() { ["selectorKey"] = key, ["amlImpactPath"] = impact["amlImpact"]!["path"]!.DeepClone(),
                        ["role"] = role, ["objectPath"] = path, ["declaredDepth"] = depth, ["resolution"] = state, ["nativeImpactVerified"] = false });
                    if (state == "capturedObject") Candidate(path!, depth);
                }
                if (kind == "model") Root((string)selector["objectPath"]!, "mappedObject");
                else if (kind == "driveParameter") Root((string)selection["profile"]!["selectionPath"]!, "driveSelectionContext");
                else if (kind == "connectionEndpoint")
                {
                    var endpoint = evidence["connection"]![(string)selector["endpoint"]!]!;
                    foreach (string field in new[] { "ownerPath", "interfacePath" }) Root((string?)endpoint[field], "connection." + field);
                }
                else if (kind == "technologyRelationship")
                {
                    string plc = (string)selection["profile"]!["identity"]!["plcPath"]!;
                    foreach (string field in new[] { "sourcePath", "targetPath" }) Root((string?)evidence["relationship"]![field], "technology." + field, plc);
                }
                else if ((string?)combined["schemaVersion"] == "composite-engineering-review.v3")
                {
                    if (kind is "driveTelegram" or "driveTelegramAddress")
                    {
                        Root((string?)selection["profile"]!["selectionPath"], "driveSelectionContext");
                        if (kind == "driveTelegramAddress") Root((string?)evidence["address"]!["address"]!["plcPath"], "telegramAddressPlcContext");
                    }
                    else if (kind == "technologyEncoder")
                    {
                        string plc = (string)selection["profile"]!["identity"]!["plcPath"]!;
                        Root((string?)selection["profile"]!["selectionPath"], "encoderOwnerContext", plc);
                        foreach (var target in CompositeEngineeringTyped.Targets(evidence)) Root((string?)target.Selector["objectPath"], "encoder." + target.Role, plc);
                        if ((string?)evidence["encoder"]!["route"] == "startdriveTelegram") Root((string?)evidence["targetEvidence"]!["driveHardwarePath"], "encoderTelegramHardwareContext", plc);
                    }
                }
            }
        var captured = new JsonObject { ["nodes"] = new JsonArray(nodes.Where(n => Resolve((string?)n["objectPath"]) == "capturedObject").Select(n => n.DeepClone()).ToArray()),
            ["edges"] = model["edges"]!.DeepClone() };
        var empty = new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() };
        JsonNode? excludedEdges = null;
        while (pending.Count > 0 && visits < edgeBudget)
        {
            var batch = pending.First(); int depth = batch.Key; pending.Remove(depth);
            var frontier = batch.Value.Where(path => (int)reached[path]["discoveryDepth"]! == depth).ToArray();
            if (frontier.Length == 0) continue;
            if (depth == MaxDepth) { bounds.Add("maxContextDepth"); break; }
            rounds++;
            foreach (string from in frontier.Order(StringComparer.Ordinal))
                if (adjacent.TryGetValue(from, out var links)) foreach (var link in links)
                {
                    if (visits == edgeBudget) { bounds.Add("maxImpactEdgeVisits"); break; }
                    visits++; link["traversed"] = true; Candidate((string)link["toObjectPath"]!, depth + 1);
                }
            if (visits < edgeBudget && saved.Count < MaxSavedLinks)
            {
                var changes = new JsonArray(frontier.Order(StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject {
                    ["path"] = p, ["afterObjectPath"] = p, ["change"] = "compositeContextRoot", ["changedFields"] = new JsonArray() }).ToArray());
                var observed = ProjectReviewStore.ObservedImpactPaths(reviewId, reviewId, empty, captured, changes, 1, MaxSavedLinks - saved.Count, edgeBudget - visits);
                var coverage = observed["coverage"]!; visits += (int)coverage["visitedEdges"]!;
                excludedEdges ??= coverage["excludedEdges"]![1]!["counts"]!.DeepClone();
                foreach (var reason in coverage["truncationReasons"]!.AsArray())
                    if ((string)reason! != "impactDepth") bounds.Add((string)reason!);
                foreach (var path in observed["paths"]!.AsArray())
                {
                    string from = (string)path!["changedObjectPath"]!, to = (string)path["affectedObjectPath"]!;
                    Add(saved, new() { ["fromObjectPath"] = from, ["toObjectPath"] = to, ["edges"] = path["edges"]!.DeepClone(), ["nativeImpactVerified"] = false });
                    Candidate(to, depth + 1);
                }
            }
            else if (visits == edgeBudget) bounds.Add("maxImpactEdgeVisits");
            else if (saved.Count == MaxSavedLinks) bounds.Add("maxSavedLinks");
        }
        if (pending.Count > 0 && visits == edgeBudget) bounds.Add("maxImpactEdgeVisits");
        var result = new JsonObject { ["phase"] = phase, ["reviewId"] = reviewId, ["rootBindings"] = roots,
            ["associations"] = associations, ["savedLinks"] = saved, ["candidates"] = candidates,
            ["coverage"] = new JsonObject { ["complete"] = false, ["truncated"] = bounds.Count > 0,
                ["truncationReasons"] = new JsonArray(bounds.Order(StringComparer.Ordinal).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["visitedEdges"] = visits, ["rounds"] = rounds, ["candidateCount"] = candidates.Count, ["omittedCandidateEncounters"] = omittedCandidates,
                ["omittedAssociations"] = omittedAssociations, ["excludedNodeCount"] = originalNodes.Count - captured["nodes"]!.AsArray().Count,
                ["savedEdgesInspected"] = excludedEdges is not null, ["excludedSavedEdges"] = excludedEdges,
                ["technologyRecordsWithoutConsumerDeclarations"] = new JsonArray(technology.Where(p => p.Value["consumers"] is not JsonArray)
                    .Select(p => p.Key).Order(StringComparer.Ordinal).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["limits"] = new JsonObject { ["edgeVisitsRemainingAtStart"] = edgeBudget, ["maxAssociations"] = MaxAssociations,
                    ["maxCandidates"] = MaxCandidates, ["maxSavedLinks"] = MaxSavedLinks, ["maxContextDepth"] = MaxDepth },
                ["policy"] = "Exact phase-local context only. Explicit AML mapping destinations seed saved objects or component selection context at their retained declared-witness depth; the 128-hop limit is not reset. Captured hardware children, declared connection consumers and same-PLC declared TO relationships/consumers expand candidates. Original saved dependency evidence stays separate; supplied links never become captured edges. Equal names, members or values do not establish identity or compatibility. Candidates are review context, not verified effects; unsupported/missing capture and bounds prevent whole-impact claims.",
                ["nativeImpactVerified"] = false, ["memberCompatibilityVerified"] = false } };
        charge(result["coverage"]!); return result;
    }
    private static bool ValidIdentity(string text)
    {
        try { EngineeringIdentityGuard.Text(text, "consumer identity", 8192); return true; }
        catch (BridgeException e) when (e.Code == "InvalidIdentity") { return false; }
    }
}
