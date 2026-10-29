using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.HmiComponents;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core;

public sealed partial class HmiComponentWorkflowStore
{
    public JsonObject GenerateTextListSet(string? uploadRef = null, string? recordId = null) =>
        GenerateTextListSetCore(uploadRef, recordId, 0, new AuthorReadBudget());

    private JsonObject GenerateTextListSetCore(string? uploadRef, string? recordId, int depth, AuthorReadBudget budget)
    {
        if (depth > 16) throw Error("Coordinated generation exceeds 16 predecessor sets.");
        if ((uploadRef is null) == (recordId is null)) throw Error("Select exactly one committed uploadRef or retained set recordId.");
        JsonObject? retained = null; JsonObject input; byte[] bytes;
        if (recordId is not null)
        {
            EngineeringIdentityGuard.Sha256(recordId, "recordId");
            retained = Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(recordId, ".text-set.json"), ReportLimit)));
            input = Object(retained["input"]);
            string hash = Text(input, "sha256", 64); EngineeringIdentityGuard.Sha256(hash, "input.sha256");
            if (!JsonNode.DeepEquals(input, Reference(hash, ".text-set-input"))) throw Error("Expected retained coordinated input.");
            bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, input, InputLimit);
        }
        else
        {
            var source = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef!);
            bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = source.Path, ["sha256"] = source.Sha256 }, InputLimit);
            input = Reference(Convert.ToHexStringLower(SHA256.HashData(bytes)), ".text-set-input");
        }
        var request = Parse(bytes);
        if ((string?)request["schemaVersion"] is "unified-text-list-set-request.v2" or "unified-text-list-set-request.v3")
            return EvolveTextListSet(request, bytes, input, retained, recordId, depth, budget);
        Fields(request, "schemaVersion componentReviewId previousRecordId outputs retainOutputIds requireObservedDependencies");
        if (Text(request, "schemaVersion") != "unified-text-list-set-request.v1") throw Error("Unsupported coordinated generation request.");
        bool requireDependencies = Boolean(request, "requireObservedDependencies");
        string reviewId = Text(request, "componentReviewId", 64); EngineeringIdentityGuard.Sha256(reviewId, "componentReviewId");
        var selected = Array(request, "outputs", 16);
        if (selected.Count < 2) throw Error("Coordinated generation requires 2..16 explicit outputs.");
        var retainedIds = Array(request, "retainOutputIds", 256).Select(n => Text(new() { ["id"] = n?.DeepClone() }, "id", 128)).ToArray();
        if (retainedIds.Distinct(StringComparer.Ordinal).Count() != retainedIds.Length) throw Error("Repeated retained output selection.");
        string? previousId = request["previousRecordId"] is null ? null : Text(request, "previousRecordId", 64);
        JsonObject? previous = previousId is null ? null : GenerateTextListSetCore(null, previousId, depth + 1, budget);
        if (previous is not null && (string?)previous["schemaVersion"] != "unified-text-list-set-record.v1")
            throw Error("v1 regeneration requires a v1 predecessor; use explicit v2 evolution for newer sets.");
        var preliminary = Object(Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(reviewId, ".json"), ReportLimit)))["report"]);
        budget.Bytes += bytes.Length + Array(preliminary, "files", 32).Sum(f => (long)Object(f)["bytes"]!);
        if (budget.Bytes > 128L * 1024 * 1024) throw Error("Coordinated lineage exceeds 128 MiB of declared original files and requests.");
        var review = Page(reviewId);
        var report = Object(Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Object(review["reportArtifact"]), ReportLimit)))["report"]);
        if ((string?)report["schemaVersion"] != "hmi-component-review.v7" || (string?)report["destination"]?["family"] != "unified")
            throw Error("Select one retained v7 Unified component review.");
        var conflicts = new JsonArray(); var gaps = new JsonArray();
        void Conflict(string message) => conflicts.Add(message);
        var outputIds = new HashSet<string>(StringComparer.Ordinal); var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preparations = new List<(JsonObject Request, byte[] Bytes, JsonObject Record)>();
        var outputs = new JsonArray(); var preserved = new JsonArray();
        JsonNode? languages = null; string? defaultLanguage = null;
        long outputBytes = 0;
        foreach (var item in selected)
        {
            var row = Object(item); Fields(row, "outputId bindingId profile template previousAuthorRecordId");
            string outputId = Text(row, "outputId", 128), bindingId = Text(row, "bindingId", 128), profile = Text(row, "profile", 128);
            if (!outputIds.Add(outputId) || !bindingIds.Add(bindingId) || retainedIds.Contains(outputId, StringComparer.Ordinal))
                throw Error("Generated outputs and bindings must be unique and separate from retained outputs.");
            string version = profile switch {
                "unified-bool-binding-text-list.v1" => "v2", "unified-integer-state-text-list.v1" => "v3",
                _ => throw Error("Only established Bool and integer-state paired text-list profiles are supported.") };
            string? priorAuthor = row["previousAuthorRecordId"] is null ? null : Text(row, "previousAuthorRecordId", 64);
            if (previous is not null)
            {
                if (priorAuthor is not null) throw Error("A previous set supplies all predecessor authors; do not override individual lineage.");
                var prior = previous["outputs"]!.AsArray().OfType<JsonObject>().SingleOrDefault(p => (string?)p["outputId"] == outputId);
                priorAuthor = (string?)prior?["authorRecordId"];
                if (prior is null || (string?)prior["bindingId"] != bindingId || (string?)prior["profile"] != profile)
                    Conflict("Set regeneration cannot add/remove outputs or change binding/profile identities: " + outputId);
            }
            var childRequest = new JsonObject {
                ["schemaVersion"] = "unified-text-list-author-request." + version, ["profile"] = profile,
                ["componentReviewId"] = reviewId, ["outputId"] = outputId, ["bindingId"] = bindingId,
                ["previousRecordId"] = priorAuthor, ["template"] = row["template"]?.DeepClone(), ["retainOutputIds"] = new JsonArray() };
            byte[] childBytes = JsonSerializer.SerializeToUtf8Bytes(childRequest, BridgeProtocol.Json);
            var childInput = Reference(Convert.ToHexStringLower(SHA256.HashData(childBytes)), ".author-input");
            var child = AuthorBoundTextLists(childRequest, childBytes, childInput, null, null, depth, budget, report);
            preparations.Add((childRequest, childBytes, child));
            foreach (var conflict in child["conflicts"]!.AsArray()) Conflict(outputId + ": " + (string)conflict!);
            foreach (var gap in child["gaps"]!.AsArray()) gaps.Add(outputId + ": " + (string)gap!);
            var intent = Object(child["intent"]);
            if (retained is null) UnifiedTextListAuthoring.RequireNativeImportable(intent);
            if (!libraries.Add((string)intent["textLibrary"]!)) Conflict("Text-library identities collide across outputs, including case variants.");
            if (!lists.Add((string)intent["lists"]![0]!["name"]!)) Conflict("Text-list identities collide across outputs, including case variants.");
            if (languages is null) { languages = intent["languages"]; defaultLanguage = (string)intent["defaultLanguage"]!; }
            else if (!JsonNode.DeepEquals(languages, intent["languages"]) || defaultLanguage != (string?)intent["defaultLanguage"])
                Conflict("All coordinated outputs require identical explicit language order and default language.");
            foreach (var candidate in UnifiedTextListAuthoring.Build(intent))
            {
                outputBytes += candidate.Bytes.Length;
                if (!names.Add(candidate.Name)) Conflict("Candidate filenames collide across outputs, including case variants: " + candidate.Name);
            }
            var binding = Object(child["componentContext"]!["binding"]);
            var dependencies = CoordinatedDependencies(report, binding);
            if ((int)dependencies["contradictionCount"]! > 0) Conflict(outputId + ": Original selected dependency associations contradict this mapping.");
            if (!(bool)dependencies["allObservedSelectedIndicesMatched"]!)
            {
                gaps.Add(outputId + ": Selected dependency indices are unavailable or unmatched; complete closure remains unverified.");
                if (requireDependencies) Conflict(outputId + ": Observed selected dependency coverage is required.");
            }
            var paths = new JsonArray(report["consumerPaths"]!.AsArray().Where(p =>
                new[] { ("beforeComponent", "beforeMember"), ("afterComponent", "afterMember") }.Any(s =>
                    (string?)p!["scope"] == s.Item1 && (string?)p["reviewId"] == (string?)report[s.Item1]?["selection"]?["reviewId"] &&
                    ((string?)p["rootObjectPath"] == (string?)binding["selection"]?[s.Item2]?["objectPath"] ||
                     (string?)p["consumerObjectPath"] == (string?)binding["selection"]?[s.Item2]?["objectPath"]))).Select(p => p!.DeepClone()).ToArray());
            outputs.Add(new JsonObject {
                ["outputId"] = outputId, ["bindingId"] = bindingId, ["profile"] = profile, ["authorRecordId"] = null,
                ["componentContext"] = child["componentContext"]!.DeepClone(), ["intent"] = intent.DeepClone(),
                ["dependencyCoverage"] = dependencies, ["consumerPaths"] = paths,
                ["valueCoverage"] = child["valueCoverage"]?.DeepClone(), ["stateChanges"] = child["stateChanges"]?.DeepClone(),
                ["conflicts"] = child["conflicts"]!.DeepClone(), ["gaps"] = child["gaps"]!.DeepClone() });
        }
        var declaredOutputs = report["outputs"]!.AsArray().OfType<JsonObject>().ToArray();
        var declaredBindings = report["bindings"]!.AsArray().OfType<JsonObject>().ToArray();
        var uncoveredOutputs = declaredOutputs.Where(o => !outputIds.Contains((string)o["id"]!) && !retainedIds.Contains((string)o["id"]!, StringComparer.Ordinal)).ToArray();
        var uncoveredBindings = declaredBindings.Where(b => !bindingIds.Contains((string)b["id"]!)).ToArray();
        if (uncoveredOutputs.Length > 0) Conflict("Every declared review output must be generated or explicitly retained.");
        if (uncoveredBindings.Length > 0) Conflict("Every declared review binding must be selected exactly once; unused bindings cannot be silently omitted.");
        foreach (string id in retainedIds)
        {
            var peer = declaredOutputs.SingleOrDefault(o => (string?)o["id"] == id) ?? throw Error("Unknown retained output.");
            if ((string?)peer["selection"]?["action"] != "retain") throw Error("Selected peers must be explicit retain outputs.");
            if (!(bool)peer["selection"]!["currentComplete"]! || peer["currentFiles"]!.AsArray().Count == 0)
                Conflict(id + ": Retention requires a complete nonempty supplied current file set.");
            foreach (var conflict in peer["conflicts"]!.AsArray()) Conflict(id + ": " + (string)conflict!);
            foreach (var gap in peer["gaps"]!.AsArray()) gaps.Add(id + ": " + (string)gap!);
            foreach (var file in peer["currentFiles"]!.AsArray())
            {
                string name = (string)file!["fileName"]!;
                if (!names.Add(name)) Conflict("Generated/preserved filenames collide, including case variants: " + name);
                preserved.Add(new JsonObject { ["outputId"] = id, ["fileName"] = name, ["bytes"] = file["bytes"]!.DeepClone(),
                    ["artifact"] = Reference((string)file["sha256"]!, ".source") });
                outputBytes += (int)file["bytes"]!;
            }
        }
        if (selected.Count * 2 + preserved.Count > 32 || outputBytes > 64L * 1024 * 1024)
            throw new BridgeException("ArtifactCapacity", "A coordinated set is bounded to 32 generated/preserved files and 64 MiB total.");
        if (previous is not null)
        {
            if ((string?)previous["status"] != "candidatesReady") Conflict("The predecessor set did not publish complete candidates.");
            if (!previous["outputs"]!.AsArray().Select(o => (string)o!["outputId"]!).ToHashSet(StringComparer.Ordinal).SetEquals(outputIds))
                Conflict("Set output membership cannot change during regeneration.");
            if (!JsonNode.DeepEquals(previous["preservedFiles"], preserved))
                Conflict("Retained peer membership, filenames and exact bytes must match the predecessor set.");
        }
        var memberCoverage = SetMemberCoverage(report, bindingIds);
        if (memberCoverage["observations"]!.AsArray().Any(m => m!["bindingIds"]!.AsArray().Count == 0))
            gaps.Add("Captured unselected member declarations remain outside the generated mapping.");
        gaps.Add("Declared output/binding coverage is bounded to this supplied review. Unobserved project content and omitted/truncated consumer or dependency evidence remain unqualified.");
        var files = new JsonArray();
        bool ready = conflicts.Count == 0;
        if (ready)
        {
            // Only after the whole set passes preflight are immutable child records
            // published. The final set record is the completion marker; partial
            // publication leaves no completed set and does not modify prior evidence.
            for (int index = 0; index < preparations.Count; index++)
            {
                var prepared = preparations[index]; var child = prepared.Record;
                byte[] childRecordBytes = JsonSerializer.SerializeToUtf8Bytes(child, BridgeProtocol.Json);
                string childId = Convert.ToHexStringLower(SHA256.HashData(childRecordBytes));
                outputs[index]!["authorRecordId"] = childId;
                foreach (var file in child["files"]!.AsArray())
                {
                    var row = file!.DeepClone().AsObject(); row["outputId"] = outputs[index]!["outputId"]!.DeepClone(); files.Add(row);
                }
                if (retained is null)
                {
                    foreach (var candidate in UnifiedTextListAuthoring.Build(Object(child["intent"]))) Publish(candidate.Bytes, ".candidate", UnifiedTextListAuthoring.OutputLimit);
                    Publish(prepared.Bytes, ".author-input", InputLimit);
                    Publish(childRecordBytes, ".author.json", ReportLimit);
                }
                else
                {
                    var actual = AuthorTextListsCore(null, childId, depth, budget); actual.Remove("recordId"); actual.Remove("recordArtifact");
                    if (!JsonNode.DeepEquals(actual, child)) throw new BridgeException("ArtifactChanged", "Coordinated child differs from revalidated original evidence.");
                }
            }
        }
        var record = new JsonObject {
            ["schemaVersion"] = "unified-text-list-set-record.v1", ["input"] = input.DeepClone(), ["componentReviewId"] = reviewId,
            ["previousRecordId"] = previousId, ["status"] = ready ? "candidatesReady" : "conflict",
            ["outputs"] = outputs, ["files"] = files, ["preservedFiles"] = preserved, ["conflicts"] = conflicts, ["gaps"] = gaps,
            ["coverage"] = new JsonObject {
                ["declaredOutputIds"] = new JsonArray(declaredOutputs.Select(o => o["id"]!.DeepClone()).ToArray()),
                ["declaredBindingIds"] = new JsonArray(declaredBindings.Select(o => o["id"]!.DeepClone()).ToArray()),
                ["uncoveredOutputIds"] = new JsonArray(uncoveredOutputs.Select(o => o["id"]!.DeepClone()).ToArray()),
                ["uncoveredBindingIds"] = new JsonArray(uncoveredBindings.Select(o => o["id"]!.DeepClone()).ToArray()),
                ["allDeclaredOutputsCovered"] = uncoveredOutputs.Length == 0,
                ["allDeclaredBindingsCovered"] = uncoveredBindings.Length == 0, ["members"] = memberCoverage,
                ["sourceConsumerCoverage"] = report["consumerCoverage"]!.DeepClone(), ["sourceDependencyImpactCoverage"] = report["dependencyImpactCoverage"]!.DeepClone(),
                ["consumerCoverageComplete"] = false, ["dependencyClosureVerified"] = false, ["componentMemberCoverageComplete"] = false },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["preservationVerified"] = false, ["ownershipVerified"] = false,
            ["policy"] = "Coordinated offline candidates for 2..16 explicit paired Bool/integer lists, one supplied v7 review, fixed output/binding/profile/file/list/library identities during regeneration. Every declared output and binding must be accounted for. Any relevant conflict suppresses all candidates and child records. Selected retained peer bytes remain exact. Completion is the final immutable set record; interrupted publication may leave unreferenced immutable artifacts. No native import, merge, rename, removal, project completeness, authenticated source, native dependency closure or preservation qualification." };
        byte[] recordBytes = JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json);
        if (recordBytes.Length > ReportLimit) throw new BridgeException("ArtifactCapacity", "Coordinated record exceeds 16 MiB.");
        JsonObject artifact;
        if (retained is null)
        {
            Publish(bytes, ".text-set-input", InputLimit);
            artifact = Publish(recordBytes, ".text-set.json", ReportLimit);
        }
        else
        {
            if (!JsonNode.DeepEquals(record, retained)) throw new BridgeException("ArtifactChanged", "Coordinated set differs from its revalidated originals, members, child authors or lineage.");
            artifact = Reference(recordId!, ".text-set.json");
        }
        var result = record.DeepClone().AsObject(); result["recordId"] = artifact["sha256"]!.DeepClone(); result["recordArtifact"] = artifact;
        return result;
    }

    private static JsonObject CoordinatedDependencies(JsonObject report, JsonObject binding)
    {
        var roots = new HashSet<(string Scope, string Path)>();
        foreach (var (scope, field) in new[] { ("beforeComponent", "beforeMember"), ("afterComponent", "afterMember") })
        {
            string? path = (string?)binding["selection"]?[field]?["objectPath"];
            if (path is null) continue;
            roots.Add((scope, path));
            foreach (var consumer in report["consumerPaths"]!.AsArray())
                if ((string?)consumer!["scope"] == scope && (string?)consumer["reviewId"] == (string?)report[scope]?["selection"]?["reviewId"] &&
                    ((string?)consumer["rootObjectPath"] == path || (string?)consumer["consumerObjectPath"] == path))
                    foreach (var node in consumer["objectPaths"]!.AsArray()) roots.Add((scope, (string)node!));
        }
        var selected = new HashSet<(string Scope, string Path)>(roots);
        var associations = new JsonArray(); int contradictions = 0;
        var rows = report["dependencyAssociations"]!.AsArray();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]!; var choice = row["selection"]!; string scope = (string)choice["scope"]!;
            string owner = (string)choice["objectPath"]!, target = (string)choice["targetObjectPath"]!;
            if (!roots.Contains((scope, owner)) && !roots.Contains((scope, target))) continue;
            selected.Add((scope, owner)); selected.Add((scope, target));
            if ((string?)row["status"] is "different" or "conflicting") contradictions++;
            associations.Add(new JsonObject { ["associationIndex"] = i, ["association"] = row.DeepClone() });
        }
        var coverage = new JsonArray(report["dependencyCoverage"]!.AsArray().Where(r => selected.Contains(((string)r!["scope"]!, (string)r["objectPath"]!))).Select(r => r!.DeepClone()).ToArray());
        var missing = selected.Where(s => !coverage.Any(c => (string?)c!["scope"] == s.Scope && (string?)c["objectPath"] == s.Path)).OrderBy(s => s.Scope, StringComparer.Ordinal).ThenBy(s => s.Path, StringComparer.Ordinal);
        return new JsonObject { ["selectedScopes"] = coverage, ["associations"] = associations, ["contradictionCount"] = contradictions,
            ["unobservedScopes"] = new JsonArray(missing.Select(s => (JsonNode?)new JsonObject { ["scope"] = s.Scope, ["objectPath"] = s.Path }).ToArray()),
            ["allObservedSelectedIndicesMatched"] = coverage.Count == selected.Count && coverage.Count > 0 && coverage.All(c => (bool?)c!["allObservedIndicesMatched"] == true),
            ["dependencyClosureVerified"] = false, ["consumerCoverageComplete"] = false,
            ["sourceImpactCoverage"] = report["dependencyImpactCoverage"]!.DeepClone(), ["sourceConsumerCoverage"] = report["consumerCoverage"]!.DeepClone(),
            ["policy"] = "All original associations touching exact selected members or nodes on their bounded same-scope saved consumer paths. Missing dependency observations for those nodes stay explicit; no transitive graph completion, foreign-snapshot join or native closure." };
    }

    private JsonObject SetMemberCoverage(JsonObject report, HashSet<string> bindingIds)
    {
        var observations = new JsonArray(); var unavailable = new JsonArray(); bool truncated = false;
        foreach (string scope in new[] { "beforeComponent", "afterComponent" })
        {
            if (report[scope] is null) continue;
            var selection = Object(report[scope]!["selection"]);
            var source = new DiagnosticEvidence(new ProjectReviewStore(artifactsRoot).Read((string)selection["reviewId"]!));
            foreach (var pathNode in selection["objectPaths"]!.AsArray())
            {
                string path = (string)pathNode!;
                var roots = source.Xml(path)?.Root?.Elements().Where(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal) || e.Name.LocalName == "SW.Types.PlcStruct").ToArray();
                var interfaces = roots?.Length == 1 ? DiagnosticEvidence.Children(roots[0], "AttributeList").SelectMany(a => DiagnosticEvidence.Children(a, "Interface")).ToArray() : [];
                if (interfaces.Length != 1) { unavailable.Add(new JsonObject { ["scope"] = scope, ["objectPath"] = path }); continue; }
                void Visit(XElement parent, string[] prefix)
                {
                    foreach (var member in DiagnosticEvidence.Members(parent))
                    {
                        if (observations.Count >= 4096 || prefix.Length >= 16) { truncated = true; return; }
                        string[] memberPath = [.. prefix, (string?)member.Attribute("Name") ?? ""];
                        var ids = report["bindings"]!.AsArray().OfType<JsonObject>().Where(b => bindingIds.Contains((string)b["id"]!) &&
                            (string?)b["selection"]?[scope == "beforeComponent" ? "beforeMember" : "afterMember"]?["objectPath"] == path &&
                            b["selection"]?[scope == "beforeComponent" ? "beforeMember" : "afterMember"]?["memberPath"] is JsonArray parts &&
                            parts.Select(p => (string)p!).SequenceEqual(memberPath)).Select(b => b["id"]!.DeepClone()).ToArray();
                        observations.Add(new JsonObject { ["scope"] = scope, ["reviewId"] = selection["reviewId"]!.DeepClone(), ["objectPath"] = path,
                            ["memberPath"] = new JsonArray(memberPath.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                            ["dataType"] = (string?)member.Attribute("Datatype"), ["bindingIds"] = new JsonArray(ids),
                            ["evidence"] = source.Evidence(path, member) });
                        Visit(member, memberPath);
                    }
                }
                Visit(interfaces[0], []);
            }
        }
        return new JsonObject { ["observations"] = observations, ["unavailableObjects"] = unavailable, ["truncated"] = truncated,
            ["maximumObservations"] = 4096, ["maximumDepth"] = 16, ["componentMemberCoverageComplete"] = false };
    }
}
