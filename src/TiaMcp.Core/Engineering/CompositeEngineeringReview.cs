using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.AutomationMl;

namespace TiaMcp.Core.Engineering;

/// <summary>Declared cross-domain associations bound to independently reopened retained inputs.</summary>
public static class CompositeEngineeringReview
{
    public const int InputLimit = 4 << 20, ReportLimit = 32 << 20;
    private const int EvidenceLimit = 128 << 20;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Context(string Phase, string ReviewId, JsonObject Review, string AmlRef, AutomationMlDocument Aml,
        Dictionary<string, JsonObject> Connections, Dictionary<string, JsonObject> Drives, Dictionary<string, JsonObject> Technology);
    private sealed record Selection(JsonObject Selector, string State, JsonNode? Evidence, JsonObject? Profile = null);

    public static JsonObject Build(string artifactsRoot, byte[] bytes)
    {
        var request = Parse(bytes);
        Fields(request, "schemaVersion before after mappings correspondences allowDifferentProjectCopy");
        string version = Text(request, "schemaVersion");
        Require(version is "composite-engineering-request.v1" or "composite-engineering-request.v2" or "composite-engineering-request.v3", "Unsupported request schema.");
        bool typed = version == "composite-engineering-request.v3", consumersEnabled = version != "composite-engineering-request.v1";
        Require(request["allowDifferentProjectCopy"] is JsonValue allow && allow.TryGetValue<bool>(out _), "Expected Boolean project-copy admission.");
        var mappings = Rows(request, "mappings", 4096); var correspondences = Rows(request, "correspondences", 4096);
        int count = 0;
        foreach (string phase in new[] { "before", "after" })
        {
            var c = Object(request[phase]); Fields(c, "reviewId automationMlRef connections drives technology");
            foreach (string domain in new[] { "connections", "drives", "technology" }) count += Rows(c, domain, 64).Count;
        }
        Require(count <= 64, "At most 64 component references across both phases.");
        long resolvedBytes = 0, reportBytes = 0;
        var before = Load("before"); var after = Load("after");
        bool crossCopy = new[] { "host", "projectPath" }.Any(k => before.Review["model"]!["project"]?[k] is not JsonValue b ||
            !b.TryGetValue<string>(out string? text) || string.IsNullOrWhiteSpace(text) || !JsonNode.DeepEquals(b, after.Review["model"]!["project"]?[k]));
        Require(!crossCopy || (bool)request["allowDifferentProjectCopy"]!, "Different or incomplete project-copy declarations require explicit admission.");
        var selections = new Dictionary<string, Selection>(StringComparer.Ordinal);
        var results = new JsonArray(); var claims = new JsonArray(); var gaps = new JsonArray();
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
        var assignments = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in mappings)
        {
            var mapping = Object(item); Fields(mapping, "id phase purpose reason source target");
            string id = Text(mapping, "id", 256), phase = Text(mapping, "phase"), purpose = Text(mapping, "purpose");
            Require(ids.Add(id), "Duplicate mapping ID."); Text(mapping, "reason", 1024);
            Require(purpose is "hardware" or "communication" or "parameter" or "dependent" or "telegram" or "encoder", "Unknown mapping purpose.");
            var source = Resolve(Object(mapping["source"]), phase); var target = Resolve(Object(mapping["target"]), phase);
            string from = Key(source.Selector), to = Key(target.Selector);
            Require(from != to && edgeKeys.Add(phase + "\n" + purpose + "\n" + from + "\n" + to), "Self or duplicate mapping.");
            string state = source.State != "resolvedDeclaration" || target.State != "resolvedDeclaration" ? "unresolvedSelection" :
                Supported(purpose, source, target) ? "declaredAssociation" : "unsupportedSemantics";
            var constraints = new JsonArray();
            if (typed)
            {
                string? typedState = CompositeEngineeringTyped.Mapping(purpose, source.Selector, source.Evidence, source.Profile,
                    target.Selector, target.Evidence, target.Profile, constraints);
                if (source.State == "resolvedDeclaration" && target.State == "resolvedDeclaration" && typedState is not null) state = typedState;
            }
            var mappingResult = new JsonObject { ["mapping"] = mapping.DeepClone(), ["state"] = state,
                ["sourceState"] = source.State, ["targetState"] = target.State, ["nativeVerified"] = false };
            if (typed) mappingResult["constraints"] = constraints;
            Add(results, mappingResult);
            if (state != "declaredAssociation") continue;
            if (!edges.TryGetValue(from, out var targets)) edges.Add(from, targets = []);
            targets.Add(to);
            if (purpose != "dependent")
            {
                string assignment = phase + "\n" + purpose + "\n" + to;
                if (!assignments.TryGetValue(assignment, out var sources)) assignments.Add(assignment, sources = new(StringComparer.Ordinal));
                sources.Add(from);
            }
        }
        foreach (var pair in assignments.Where(p => p.Value.Count > 1))
            Add(gaps, new JsonObject { ["kind"] = "conflictingAssignment", ["targetKey"] = pair.Key, ["sourceKeys"] = Strings(pair.Value.Order(StringComparer.Ordinal)) });
        var usedBefore = new HashSet<string>(StringComparer.Ordinal); var usedAfter = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in correspondences)
        {
            var claim = Object(item); Fields(claim, "id kind reason before after");
            Require(ids.Add(Text(claim, "id", 256)), "Duplicate mapping or correspondence ID.");
            string kind = Text(claim, "kind"); Require(kind is "continuity" or "replacement", "Unknown correspondence kind."); Text(claim, "reason", 1024);
            var left = Resolve(Object(claim["before"]), "before"); var right = Resolve(Object(claim["after"]), "after");
            Require(Text(left.Selector, "kind") == Text(right.Selector, "kind"), "Cross-domain correspondence is not identity continuity.");
            Require(usedBefore.Add(Key(left.Selector)) && usedAfter.Add(Key(right.Selector)), "Conflicting correspondence; each side may occur once.");
            bool profileChanged = !JsonNode.DeepEquals(left.Profile, right.Profile);
            string state = left.State != "resolvedDeclaration" || right.State != "resolvedDeclaration" ? "unresolvedSelection" :
                crossCopy ? "crossCopyDeclaration" : profileChanged && kind == "continuity" ? "profileMismatch" : "declaredCorrespondence";
            Add(claims, new JsonObject { ["correspondence"] = claim.DeepClone(), ["state"] = state, ["profileChanged"] = profileChanged,
                ["beforeState"] = left.State, ["afterState"] = right.State, ["identityEqualityVerified"] = false });
        }
        var comparison = JsonSerializer.SerializeToNode(before.Aml.CompareTo(after.Aml), BridgeProtocol.Json)!.AsObject();
        ReportCharge(comparison);
        var impacts = new JsonArray();
        var startsByOwner = new Dictionary<(string Phase, string Path), HashSet<string>>();
        foreach (var pair in selections.Where(p => Text(p.Value.Selector, "kind").StartsWith("aml", StringComparison.Ordinal)))
        {
            string? path = (string?)pair.Value.Evidence?[Text(pair.Value.Selector, "kind") == "amlObject" ? "path" : "owner"];
            if (path is null) continue;
            var owner = (Text(pair.Value.Selector, "phase"), path);
            if (!startsByOwner.TryGetValue(owner, out var starts)) startsByOwner.Add(owner, starts = new(StringComparer.Ordinal));
            starts.Add(pair.Key);
        }
        int visitedEdges = 0;
        foreach (var context in new[] { before, after })
        {
            foreach (var impact in comparison[context.Phase + "Impacts"]!.AsArray())
            {
                string path = Text(impact!, "path");
                // Attributes and links retain their exact owner; owning-object impact includes their declared mappings.
                startsByOwner.TryGetValue((context.Phase, path), out var starts);
                var reached = new HashSet<string>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal);
                var declaredDepths = new Dictionary<string, int>(StringComparer.Ordinal);
                bool cycle = false, exhausted = false;
                void Walk(string key, int depth)
                {
                    if (exhausted) return;
                    if (depth > 128) { exhausted = true; return; }
                    if (active.Contains(key)) { cycle = true; return; }
                    if (!reached.Add(key)) return;
                    if (consumersEnabled) declaredDepths[key] = depth;
                    active.Add(key);
                    if (edges.TryGetValue(key, out var targets)) foreach (string target in targets)
                    {
                        if (++visitedEdges > 10000) { exhausted = true; break; }
                        Walk(target, depth + 1);
                    }
                    active.Remove(key);
                }
                if (starts is not null) foreach (string start in starts) Walk(start, 0);
                var destinations = reached.Where(k => selections.ContainsKey(k) &&
                    !Text(selections[k].Selector, "kind").StartsWith("aml", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
                var declaredImpact = new JsonObject { ["phase"] = context.Phase, ["amlImpact"] = impact!.DeepClone(),
                    ["state"] = destinations.Length == 0 ? "unmapped" : "declaredMappedImpact", ["destinationKeys"] = Strings(destinations),
                    ["declaredCycle"] = cycle, ["traversalExhausted"] = exhausted, ["nativeImpactVerified"] = false };
                if (consumersEnabled) declaredImpact["destinationDepths"] =
                    new JsonObject(destinations.Select(key => new KeyValuePair<string, JsonNode?>(key, JsonValue.Create(declaredDepths[key]))));
                Add(impacts, declaredImpact);
            }
        }
        if (crossCopy) Add(gaps, new JsonObject { ["kind"] = "crossCopyDeclaration", ["reason"] = "Saved project declarations differ or are incomplete; no project identity equality is established." });
        if ((bool)comparison["wholeDocumentReviewRequired"]!) Add(gaps, new JsonObject { ["kind"] = "wholeDocumentReviewRequired", ["reason"] = "Original AML bytes changed, including possible opaque content." });
        Add(gaps, new JsonObject { ["kind"] = "declaredMappingGraphOnly", ["reason"] = "Closure follows accepted explicit mapping edges only. Unmapped native and component consumers remain unassessed." });
        var result = new JsonObject { ["schemaVersion"] = "composite-engineering-review.v1", ["contexts"] = new JsonObject { ["before"] = ContextSummary(before, typed), ["after"] = ContextSummary(after, typed) },
            ["selections"] = new JsonArray(selections.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject {
                ["key"] = p.Key, ["selector"] = p.Value.Selector.DeepClone(), ["state"] = p.Value.State,
                ["evidence"] = p.Value.Evidence?.DeepClone(), ["profile"] = p.Value.Profile?.DeepClone() }).ToArray()),
            ["mappings"] = results, ["correspondences"] = claims, ["amlComparison"] = comparison, ["impacts"] = impacts, ["coverageGaps"] = gaps,
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false,
            ["liveFreshnessVerified"] = false, ["wholeImpactAnalysisComplete"] = false };
        if (consumersEnabled)
        {
            result["schemaVersion"] = typed ? "composite-engineering-review.v3" : "composite-engineering-review.v2";
            gaps.Single(g => (string?)g!["kind"] == "declaredMappingGraphOnly")!["reason"] =
                "Primary impacts follow explicit mapping edges. Separate consumerContexts add bounded saved/supplied review candidates, not effects or complete consumers.";
            var consumers = new JsonArray();
            foreach (var context in new[] { before, after })
            {
                var consumer = CompositeEngineeringConsumers.Build(context.Phase, context.ReviewId, context.Review["model"]!.AsObject(),
                    context.Connections, context.Technology, result, Math.Max(0, 10000 - visitedEdges), ReportCharge);
                visitedEdges += (int)consumer["coverage"]!["visitedEdges"]!;
                consumers.Add(consumer);
            }
            result["consumerContexts"] = consumers;
            Add(gaps, new JsonObject { ["kind"] = "consumerContextOnly", ["reason"] = typed
                ? "Version 3 adds exact supplied telegram/encoder references. Context candidates and consistent declarations do not prove native eligibility, wiring, signal geometry, effects or complete consumers."
                : "Version 2 expands bounded exact saved and supplied component context separately. Context candidates do not prove native effects, typed member/telegram compatibility or complete consumers." });
        }
        Require(JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length <= ReportLimit, "Combined report exceeds 32 MiB.");
        return result;

        Context Load(string phase)
        {
            var c = Object(request[phase]); string reviewId = Text(c, "reviewId"), amlRef = Text(c, "automationMlRef");
            EngineeringIdentityGuard.Sha256(reviewId, "reviewId");
            var review = new ProjectReviewStore(artifactsRoot).Read(reviewId); Charge(JsonSerializer.SerializeToUtf8Bytes(review, BridgeProtocol.Json).Length);
            byte[] aml = new AutomationMlWorkflowStore(artifactsRoot).ReadVerifiedDocument(amlRef, Charge);
            Dictionary<string, JsonObject> Records(string domain, Func<string, JsonObject> read)
            {
                var records = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var item in Rows(c, domain, 64))
                {
                    string recordId = Text(new JsonObject { ["ref"] = item?.DeepClone() }, "ref");
                    Require(!records.ContainsKey(recordId), "Duplicate component reference in phase.");
                    var envelope = read(recordId);
                    Require(Text(envelope, "reviewId") == reviewId, "Component does not name this phase's exact saved review.");
                    records.Add(recordId, envelope["report"]!.AsObject());
                }
                return records;
            }
            return new(phase, reviewId, review, amlRef, AutomationMlDocument.Parse(aml),
                Records("connections", id => new ConnectionWorkflowStore(artifactsRoot).ReadVerified(id, Charge)),
                Records("drives", id => new DriveProfileWorkflowStore(artifactsRoot).ReadVerified(id, Charge)),
                Records("technology", id => new TechnologyProfileWorkflowStore(artifactsRoot).ReadVerified(id, Charge)));
        }
        void Charge(int size) { resolvedBytes += size; Require(resolvedBytes <= EvidenceLimit, "Resolved input exceeds 128 MiB."); }
        void ReportCharge(JsonNode row) { reportBytes += JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length; Require(reportBytes <= ReportLimit, "Combined report exceeds 32 MiB."); }
        void Add(JsonArray rows, JsonObject row) { ReportCharge(row); rows.Add(row); }
        Selection Resolve(JsonObject selector, string phase)
        {
            Require(phase is "before" or "after" && Text(selector, "phase") == phase, "Selector phase differs from mapping phase.");
            var c = phase == "before" ? before : after; string kind = Text(selector, "kind");
            string key = Key(selector); if (selections.TryGetValue(key, out var known)) return known;
            JsonNode? evidence = null; JsonObject? profile = null; string state = "unresolved";
            if (kind is "amlObject" or "amlAttribute" or "amlLink")
            {
                Fields(selector, "kind phase recordRef path"); Require(Text(selector, "recordRef") == c.AmlRef, "AML selector is outside its phase context."); string path = Text(selector, "path");
                object? selected = kind switch { "amlObject" => c.Aml.Objects.SingleOrDefault(r => r.Path == path),
                    "amlAttribute" => c.Aml.Attributes.SingleOrDefault(r => r.Path == path), _ => c.Aml.Links.SingleOrDefault(r => r.Path == path) };
                if (selected is not null)
                {
                    evidence = JsonSerializer.SerializeToNode(selected, BridgeProtocol.Json); state = "resolvedDeclaration";
                    if (kind == "amlLink" && (evidence!["targetA"] is null || evidence["targetB"] is null)) state = "unresolved";
                    if (c.Aml.AmbiguousIdPaths.Contains(path)) state = "ambiguous";
                }
            }
            else if (kind == "model")
            {
                Fields(selector, "kind phase reviewId nodeId objectPath"); Require(Text(selector, "reviewId") == c.ReviewId, "Model selector is outside its phase context.");
                string nodeId = Text(selector, "nodeId"), path = Text(selector, "objectPath");
                var rows = c.Review["model"]!["nodes"]!.AsArray().Where(n => (string?)n?["nodeId"] == nodeId && (string?)n?["objectPath"] == path && (string?)n?["origin"] != "virtual").ToArray();
                if (rows.Length == 1) { evidence = rows[0]; state = "resolvedDeclaration"; } else if (rows.Length > 1) state = "ambiguous";
                if (typed && (c.Review["model"]!["nodes"]!.AsArray().Count(n => (string?)n?["nodeId"] == nodeId) > 1 ||
                    c.Review["model"]!["nodes"]!.AsArray().Count(n => (string?)n?["objectPath"] == path) > 1)) state = "ambiguous";
            }
            else
            {
                var records = kind switch { "connectionEndpoint" => c.Connections, "driveParameter" => c.Drives, "technologyRelationship" => c.Technology,
                    "driveTelegram" or "driveTelegramAddress" when typed => c.Drives, "technologyEncoder" when typed => c.Technology, _ => throw Error("Unknown selector kind.") };
                string recordRef = Text(selector, "recordRef"); Require(records.TryGetValue(recordRef, out var report), "Component selector is outside its phase context.");
                if (kind == "connectionEndpoint")
                {
                    Fields(selector, "kind phase recordRef id endpoint"); string id = Text(selector, "id", 256), endpoint = Text(selector, "endpoint"); Require(endpoint is "local" or "partner", "Unknown endpoint side.");
                    var row = report!["observations"]!.AsArray().SingleOrDefault(r => (string?)r?["phase"] == phase && (string?)r?["connection"]?["id"] == id);
                    if (row is not null) { evidence = row; state = (string?)row[endpoint]?["status"] == "capturedHardwareAncestry" ? "resolvedDeclaration" : "unresolved"; }
                }
                else if (kind == "driveParameter")
                {
                    Fields(selector, "kind phase recordRef name number arrayIndex"); string name = Text(selector, "name", 256);
                    int number = Integer(selector, "number", 0, int.MaxValue), index = Integer(selector, "arrayIndex", -1, 32767);
                    var row = report!["observations"]!.AsArray().SingleOrDefault(r => (string?)r?["phase"] == phase && (string?)r?["parameter"]?["name"] == name);
                    if (row is not null) { evidence = row; state = (int)row["parameter"]!["number"]! == number && (int)row["parameter"]!["arrayIndex"]! == index ? "resolvedDeclaration" : "selectorMismatch"; }
                }
                else if (kind == "technologyRelationship")
                {
                    Fields(selector, "kind phase recordRef id"); string id = Text(selector, "id", 256);
                    var row = report!["relationships"]!.AsArray().SingleOrDefault(r => (string?)r?["phase"] == phase && (string?)r?["relationship"]?["id"] == id);
                    if (row is not null) { evidence = row; state = (string?)row["relationship"]?["state"] == "disconnected" ? "disconnected" :
                        (string?)row["relationship"]?["state"] == "declared" && (string?)row["sourceResolution"] == "capturedObject" && (string?)row["targetResolution"] == "capturedObject" ? "resolvedDeclaration" : "unresolved"; }
                }
                else if (kind is "driveTelegram" or "driveTelegramAddress")
                {
                    Fields(selector, kind == "driveTelegram" ? "kind phase recordRef id" : "kind phase recordRef id addressId");
                    string id = Text(selector, "id", 256), addressId = kind == "driveTelegramAddress" ? Text(selector, "addressId", 256) : "";
                    var row = (report!["telegrams"] as JsonArray)?.SingleOrDefault(r => (string?)r?["phase"] == phase && (string?)r?["telegram"]?["id"] == id);
                    if (report["telegrams"] is null) state = "unsupportedVersion";
                    else if (row is not null)
                    {
                        if (kind == "driveTelegram") { evidence = row; state = "resolvedDeclaration"; }
                        else
                        {
                            var address = row["addresses"]!.AsArray().SingleOrDefault(a => (string?)a?["address"]?["id"] == addressId);
                            if (address is not null) { evidence = new JsonObject { ["telegram"] = row.DeepClone(), ["address"] = address.DeepClone() }; state = "resolvedDeclaration"; }
                        }
                    }
                }
                else if (kind == "technologyEncoder")
                {
                    Fields(selector, "kind phase recordRef id"); string id = Text(selector, "id", 256);
                    var row = (report!["encoderConnections"] as JsonArray)?.SingleOrDefault(r => (string?)r?["phase"] == phase && (string?)r?["encoder"]?["id"] == id);
                    if (report["encoderConnections"] is null) state = "unsupportedVersion";
                    else if (row is not null)
                    {
                        evidence = row; state = CompositeEngineeringTyped.EncoderState(row, report);
                        if (state == "resolvedDeclaration" && (string?)row["encoder"]!["route"] == "startdriveTelegram" &&
                            !c.Drives.ContainsKey((string)row["encoder"]!["target"]!["recordId"]!)) state = "unresolved";
                    }
                }
                if (kind is "driveParameter" or "technologyRelationship" or "driveTelegram" or "driveTelegramAddress" or "technologyEncoder")
                    profile = new JsonObject { ["identity"] = report!["identity"]!.DeepClone(),
                        ["valueDomain"] = report[phase + "ValueDomain"]!.DeepClone(), ["selectionPath"] = report[kind.StartsWith("drive", StringComparison.Ordinal) ? "hardwarePath" : "technologyPath"]!.DeepClone() };
            }
            ReportCharge(new JsonObject { ["key"] = key, ["selector"] = selector.DeepClone(), ["state"] = state, ["evidence"] = evidence?.DeepClone(), ["profile"] = profile?.DeepClone() });
            var selection = new Selection(selector.DeepClone().AsObject(), state, evidence?.DeepClone(), profile); selections.Add(key, selection); return selection;
        }
    }
    private static bool Supported(string purpose, Selection source, Selection target) => purpose switch
    {
        "dependent" => (string?)source.Selector["kind"] is "model" or "connectionEndpoint" or "driveParameter" or "technologyRelationship" &&
            (string?)target.Selector["kind"] is "model" or "connectionEndpoint" or "driveParameter" or "technologyRelationship",
        "hardware" => (string?)source.Selector["kind"] == "amlObject" && (string?)source.Evidence?["scope"] == "instance" &&
            (string?)source.Evidence?["kind"] is "InternalElement" or "ExternalInterface" && (string?)target.Selector["kind"] == "model" && (string?)target.Evidence?["kind"] == "hardware",
        "communication" => (string?)source.Selector["kind"] == "amlLink" && (string?)target.Selector["kind"] == "connectionEndpoint",
        "parameter" => (string?)source.Selector["kind"] == "amlAttribute" && (string?)target.Selector["kind"] == "driveParameter",
        _ => false
    };
    private static JsonObject ContextSummary(Context c, bool typed)
    {
        var components = new JsonArray();
        foreach (var (kind, records) in new[] { ("connection", c.Connections), ("drive", c.Drives), ("technology", c.Technology) })
            foreach (var pair in records)
            {
                var component = new JsonObject { ["kind"] = kind, ["recordRef"] = pair.Key,
                ["schemaVersion"] = pair.Value["schemaVersion"]!.DeepClone(), ["afterKind"] = pair.Value["afterKind"]!.DeepClone(),
                ["summary"] = pair.Value["summary"]!.DeepClone(), ["coverage"] = pair.Value["coverage"]!.DeepClone(), ["limits"] = pair.Value["limits"]!.DeepClone() };
                if (typed) foreach (string field in new[] { "telegramPolicy", "encoderPolicy", "encoderCoverage" })
                    if (pair.Value[field] is JsonNode value) component[field] = value.DeepClone();
                components.Add(component);
            }
        return new() { ["reviewId"] = c.ReviewId, ["projectDeclaration"] = c.Review["model"]!["project"]!.DeepClone(),
            ["automationMlRef"] = c.AmlRef, ["automationMlSha256"] = c.Aml.SourceSha256, ["connections"] = Strings(c.Connections.Keys),
            ["drives"] = Strings(c.Drives.Keys), ["technology"] = Strings(c.Technology.Keys), ["components"] = components };
    }
    private static string Key(JsonObject value) => CanonicalJson.Serialize(value);
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    private static JsonObject Parse(byte[] bytes)
    {
        Require(bytes.Length is > 0 and <= InputLimit, "Request must contain 1 byte..4 MiB."); string text = Utf8.GetString(bytes);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        void Unique(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in node.EnumerateObject()) { Require(names.Add(p.Name), "Duplicate JSON field."); Unique(p.Value); } }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Unique(item);
            else if (node.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(node.GetString()!);
        }
        Unique(document.RootElement); return Object(JsonNode.Parse(text));
    }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Error("Expected object.");
    private static void Fields(JsonObject node, string fields) { var expected = fields.Split(' '); Require(node.Count == expected.Length && expected.All(node.ContainsKey), "Missing or unknown fields; expected: " + fields); }
    private static JsonArray Rows(JsonObject node, string key, int max) => node[key] is JsonArray rows && rows.Count <= max ? rows : throw Error("Invalid or oversized " + key + ".");
    private static string Text(JsonNode node, string key, int max = 8192) => node[key] is JsonValue v && v.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl) ? text : throw Error("Invalid exact " + key + ".");
    private static int Integer(JsonObject node, string key, int min, int max) => node[key] is JsonValue v && v.TryGetValue<int>(out int value) && value >= min && value <= max ? value : throw Error("Invalid " + key + ".");
    private static void Require(bool condition, string message) { if (!condition) throw Error(message); }
    private static BridgeException Error(string message) => new("InvalidCompositeEngineeringReview", message);
}
