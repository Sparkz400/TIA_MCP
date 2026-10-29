using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.HmiComponents;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core;

public sealed partial class HmiComponentWorkflowStore
{
    // This authors one explicit Bool or integer-state mapping. The saved review is re-derived before
    // any selection is consumed; persisted summaries never become authority.
    private JsonObject AuthorBoundTextLists(JsonObject request, byte[] bytes, JsonObject input, JsonObject? retained, string? recordId, int depth, AuthorReadBudget budget,
        JsonObject? preparedReport = null)
    {
        Fields(request, "schemaVersion profile componentReviewId outputId bindingId previousRecordId template retainOutputIds");
        bool numeric = (string?)request["schemaVersion"] == "unified-text-list-author-request.v3";
        if (Text(request, "profile") != (numeric ? "unified-integer-state-text-list.v1" : "unified-bool-binding-text-list.v1")) throw Error("Unsupported component text-list generation profile.");
        string reviewId = Text(request, "componentReviewId", 64), outputId = Text(request, "outputId", 128), bindingId = Text(request, "bindingId", 128);
        EngineeringIdentityGuard.Sha256(reviewId, "componentReviewId");
        // Account original declared file bytes before expensive source revalidation.
        // These untrusted rows can only refuse early; Page still verifies everything.
        JsonObject report;
        if (preparedReport is not null) report = preparedReport;
        else
        {
            var preliminary = Object(Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(reviewId, ".json"), ReportLimit)))["report"]);
            budget.Bytes += bytes.Length + Array(preliminary, "files", 32).Sum(f => (long)Object(f)["bytes"]!);
            if (budget.Bytes > 128L * 1024 * 1024) throw Error("Authoring lineage exceeds the 128 MiB declared source-file and request budget.");
            var receipt = Page(reviewId);
            report = Object(Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Object(receipt["reportArtifact"]), ReportLimit)))["report"]);
        }
        if ((string?)report["schemaVersion"] != "hmi-component-review.v7" || (string?)report["destination"]?["family"] != "unified")
            throw Error("Select a retained v7 component review with an explicit Unified destination.");
        var output = report["outputs"]!.AsArray().OfType<JsonObject>().SingleOrDefault(o => (string?)o["id"] == outputId) ?? throw Error("Unknown selected output.");
        var binding = report["bindings"]!.AsArray().OfType<JsonObject>().SingleOrDefault(o => (string?)o["id"] == bindingId) ?? throw Error("Unknown selected binding.");
        var selection = Object(output["selection"]);
        if ((string?)binding["selection"]?["outputId"] != outputId || (string?)selection["kind"] != "hmiResource" ||
            (string?)selection["action"] is not ("create" or "update")) throw Error("Select one create/update hmiResource and its exact explicit binding.");
        var template = Object(request["template"]);
        if (!numeric) Fields(template, "fileBase textLibrary languages defaultLanguage falseText trueText");
        var intent = numeric ? HmiNumericTextLists.Intent(template, (string)selection["name"]!) : new JsonObject {
            ["schemaVersion"] = "unified-text-list-author-request.v1", ["fileBase"] = template["fileBase"]?.DeepClone(),
            ["textLibrary"] = template["textLibrary"]?.DeepClone(), ["languages"] = template["languages"]?.DeepClone(),
            ["defaultLanguage"] = template["defaultLanguage"]?.DeepClone(),
            ["texts"] = new JsonArray(
                new JsonObject { ["id"] = "FalseText", ["translations"] = template["falseText"]?.DeepClone() },
                new JsonObject { ["id"] = "TrueText", ["translations"] = template["trueText"]?.DeepClone() }),
            ["lists"] = new JsonArray(new JsonObject {
                ["name"] = selection["name"]!.DeepClone(), ["rangeType"] = "Bool",
                ["entries"] = new JsonArray(
                    new JsonObject { ["id"] = "False", ["type"] = "SingleValue", ["fromValue"] = 0, ["toValue"] = 0, ["isDefault"] = false, ["textId"] = "FalseText" },
                    new JsonObject { ["id"] = "True", ["type"] = "SingleValue", ["fromValue"] = 1, ["toValue"] = 1, ["isDefault"] = false, ["textId"] = "TrueText" }) }) };
        var candidates = UnifiedTextListAuthoring.Build(intent);
        // A set preflight (preparedReport) refuses only when the set itself is new.
        if (retained is null && preparedReport is null) UnifiedTextListAuthoring.RequireNativeImportable(intent);
        var conflicts = new JsonArray(); var gaps = new JsonArray();
        void Conflict(string message) => conflicts.Add(message);
        foreach (var row in output["conflicts"]!.AsArray()) Conflict("Selected output: " + (string)row!);
        foreach (var row in binding["conflicts"]!.AsArray()) Conflict("Selected binding: " + (string)row!);
        foreach (var row in output["gaps"]!.AsArray()) gaps.Add("Selected output: " + (string)row!);
        foreach (var row in binding["gaps"]!.AsArray()) gaps.Add("Selected binding: " + (string)row!);
        string expectedType = numeric ? Text(template, "dataType", 16) : "Bool";
        if ((string?)binding["afterDeclaration"]?["status"] != "capturedDeclaration" || (string?)binding["afterDeclaration"]?["dataType"] != expectedType ||
            (string?)binding["selection"]?["declaredHmiType"] != expectedType)
            Conflict(numeric ? "The captured after member and declared HMI type must exactly match the selected integer dataType; aliases and conversions are unsupported." :
                "The selected after member and declared HMI type must both be exact captured Bool declarations.");
        if ((string?)selection["ownership"] != "generated") Conflict("Selected output ownership is not declared generated.");
        var current = output["currentFiles"]!.AsArray();
        string? previousId = request["previousRecordId"] is null ? null : Text(request, "previousRecordId", 64);
        JsonObject? previous = null;
        JsonObject? priorIntent = null;
        if (previousId is not null)
        {
            previous = AuthorTextListsCore(null, previousId, depth + 1, budget);
            if (previous["files"]!.AsArray().Count != 2) Conflict("The predecessor did not publish a complete candidate pair.");
        }
        bool create = (string?)selection["action"] == "create";
        if (create)
        {
            if (previousId is not null || current.Count != 0 || output["baselineFiles"]!.AsArray().Count != 0)
                Conflict("Initial generation requires no predecessor and empty baseline/current sets.");
            if (!(bool)selection["currentComplete"]!) Conflict("Initial generation requires an explicitly complete empty current set.");
        }
        else
        {
            if (previous is null) Conflict("Regeneration requires an exact retained predecessor author record.");
            if (!(bool)selection["currentComplete"]! || !(bool)selection["baselineComplete"]!) Conflict("Regeneration requires complete declared current and baseline pairs.");
            if (previous is not null)
            {
                bool SamePair(JsonArray set) => set.Count == 2 && previous["files"]!.AsArray().All(p => set.Any(f =>
                    (string?)f!["fileName"] == (string?)p!["fileName"] && (int?)f["bytes"] == (int?)p["bytes"] && (string?)f["sha256"] == (string?)p["artifact"]?["sha256"]));
                if (!SamePair(current)) Conflict("Current supplied files differ from the retained generated pair; manual edits, missing files or export variation require review.");
                if (!SamePair(output["baselineFiles"]!.AsArray())) Conflict("The supplied baseline does not match the retained predecessor pair.");
                if (!previous["files"]!.AsArray().Select(f => (string)f!["fileName"]!).SequenceEqual(candidates.Select(c => c.Name)))
                    Conflict("This profile cannot rename generated files during regeneration.");
                priorIntent = (string?)previous["schemaVersion"] is "unified-text-list-author-record.v2" or "unified-text-list-author-record.v3" ? Object(previous["intent"]) :
                    Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Object(previous["input"]), InputLimit));
                var priorLists = priorIntent["lists"]!.AsArray();
                if (numeric)
                {
                    if (priorLists.Count != 1 || (string?)priorLists[0]!["name"] != (string?)selection["name"] ||
                        (string?)priorLists[0]!["rangeType"] != "Decimal" || (string?)priorIntent["textLibrary"] != (string?)intent["textLibrary"])
                        Conflict("Integer-state regeneration requires the same single Decimal list and text-library identity; broader replacement or rename is unsupported.");
                    if ((string?)previous["schemaVersion"] == "unified-text-list-author-record.v3" && (string?)previous["valueCoverage"]?["dataType"] != expectedType)
                        Conflict("Integer dataType changes require a separately supported conversion policy.");
                }
                else if (priorLists.Count != 1 || (string?)priorLists[0]!["name"] != (string?)selection["name"] ||
                    !JsonNode.DeepEquals(priorLists[0], intent["lists"]![0]) || (string?)priorIntent["textLibrary"] != (string?)intent["textLibrary"])
                    Conflict("The predecessor must have the same single Bool list, fixed entry identities and text-library name; broader replacement or rename is unsupported.");
                if ((string?)previous["schemaVersion"] is "unified-text-list-author-record.v2" or "unified-text-list-author-record.v3")
                {
                    var context = Object(previous["componentContext"]);
                    if ((string?)context["outputId"] != outputId || !JsonNode.DeepEquals(context["destination"], report["destination"]) &&
                        ((string?)context["destination"]?["hmiPath"] != (string?)report["destination"]?["hmiPath"] ||
                         (string?)context["destination"]?["family"] != (string?)report["destination"]?["family"]))
                        Conflict("Predecessor output/destination selection differs.");
                    if ((string?)context["afterComponent"]?["selection"]?["reviewId"] != (string?)report["beforeComponent"]?["selection"]?["reviewId"] ||
                        !JsonNode.DeepEquals(context["binding"]?["selection"]?["afterMember"], binding["selection"]?["beforeMember"]))
                        Conflict("Predecessor component and member do not match the explicit before selection.");
                }
                else gaps.Add("The v1 predecessor establishes candidate bytes only; it has no component lineage.");
            }
        }
        var preserve = new JsonArray(); var retainedIds = new HashSet<string>(StringComparer.Ordinal);
        var names = candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var idNode in Array(request, "retainOutputIds", 256))
        {
            string id = Text(new JsonObject { ["id"] = idNode?.DeepClone() }, "id", 128);
            if (!retainedIds.Add(id) || id == outputId) throw Error("Retained peer output IDs must be unique and separate from the generated output.");
            var peer = report["outputs"]!.AsArray().OfType<JsonObject>().SingleOrDefault(o => (string?)o["id"] == id) ?? throw Error("Unknown retained peer output.");
            if ((string?)peer["selection"]?["action"] != "retain") throw Error("Only explicit retain outputs can be included as preserved peers.");
            if (!(bool)peer["selection"]!["currentComplete"]! || peer["currentFiles"]!.AsArray().Count == 0)
                Conflict("Retained peer " + id + " lacks a complete nonempty current file set.");
            foreach (var row in peer["conflicts"]!.AsArray()) Conflict("Retained peer " + id + ": " + (string)row!);
            foreach (var row in peer["gaps"]!.AsArray()) gaps.Add("Retained peer " + id + ": " + (string)row!);
            foreach (var file in peer["currentFiles"]!.AsArray())
            {
                string name = (string)file!["fileName"]!;
                if (!names.Add(name)) Conflict("Generated/preserved filenames collide, including case variants: " + name);
                preserve.Add(new JsonObject { ["outputId"] = id, ["fileName"] = name, ["bytes"] = file["bytes"]!.DeepClone(),
                    ["artifact"] = Reference((string)file["sha256"]!, ".source") });
            }
        }
        var impacts = new JsonArray();
        foreach (var impact in report["dependencyImpacts"]!.AsArray().OfType<JsonObject>().Where(i => i["outputs"]!.AsArray().Any(o => (string?)o!["outputId"] == outputId)))
        {
            impacts.Add(impact["associationIndex"]!.DeepClone());
            if ((string?)impact["associationStatus"] is "different" or "conflicting") Conflict("An explicitly associated dependency impact contradicts the selected output.");
        }
        JsonObject? valueCoverage = null, dependencies = null;
        if (numeric)
        {
            valueCoverage = HmiNumericTextLists.Coverage(template, intent);
            if (!(bool)valueCoverage["allTypeValuesListed"]!)
            {
                gaps.Add("Explicit state intervals do not cover the entire selected integer type; a default entry does not prove native handling of unlisted values.");
                if (Boolean(template, "requireFullTypeCoverage")) Conflict("The template requires full integer-type coverage but leaves unlisted intervals.");
            }
            dependencies = HmiNumericTextLists.Dependencies(report, binding);
            if (!(bool)dependencies["allObservedSelectedIndicesMatched"]!)
                gaps.Add("Selected component/dependency observations are unavailable or have unmatched indices; complete dependency closure is not established.");
            if ((int)dependencies["contradictionCount"]! > 0)
                Conflict("Selected direct or saved-consumer dependency evidence contradicts this mapping, including associations omitted from the bounded impact page.");
        }
        var files = new JsonArray();
        if (conflicts.Count == 0)
        {
            foreach (var candidate in candidates)
            {
                var artifact = Reference(Convert.ToHexStringLower(SHA256.HashData(candidate.Bytes)), ".candidate");
                if (preparedReport is not null) { } // Coordinated preflight publishes nothing.
                else if (retained is null) Publish(candidate.Bytes, ".candidate", UnifiedTextListAuthoring.OutputLimit);
                else if (!ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, UnifiedTextListAuthoring.OutputLimit).AsSpan().SequenceEqual(candidate.Bytes))
                    throw new BridgeException("ArtifactChanged", "Regenerated candidate differs from retained intent.");
                files.Add(new JsonObject { ["fileName"] = candidate.Name, ["bytes"] = candidate.Bytes.Length, ["artifact"] = artifact });
            }
        }
        var record = new JsonObject {
            ["schemaVersion"] = numeric ? "unified-text-list-author-record.v3" : "unified-text-list-author-record.v2", ["input"] = input.DeepClone(), ["profile"] = request["profile"]!.DeepClone(),
            ["status"] = conflicts.Count == 0 ? "candidatesReady" : "conflict", ["previousRecordId"] = previousId,
            ["componentContext"] = new JsonObject { ["reviewRecordId"] = reviewId, ["outputId"] = outputId, ["bindingId"] = bindingId,
                ["afterComponent"] = report["afterComponent"]!.DeepClone(), ["destination"] = report["destination"]!.DeepClone(),
                ["binding"] = binding.DeepClone(), ["dependencyAssociationIndices"] = impacts },
            ["intent"] = intent, ["files"] = files, ["preservedFiles"] = preserve, ["conflicts"] = conflicts, ["gaps"] = gaps,
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["preservationVerified"] = false,
            ["consumerCoverageComplete"] = false, ["ownershipVerified"] = false,
            ["policy"] = "Offline candidates for one explicitly mapped Bool text list only. Retained peers are exact supplied bytes; preservation after import, native binding, source authenticity, complete dependency/consumer coverage and project ownership remain unverified. Conflicts suppress both candidates. No merge, rename, deletion, native import or runtime execution." };
        if (numeric)
        {
            record["valueCoverage"] = valueCoverage;
            record["dependencyCoverage"] = dependencies;
            record["stateChanges"] = HmiNumericTextLists.Changes(priorIntent, intent);
            record["policy"] = "Offline candidates for one explicit integer-state text list only. Exact captured integer declarations, bounded nonoverlapping intervals and supplied translations are not native enum, runtime storage or numeric import qualification. Missing intervals and dependency observations remain visible; defaults do not establish unlisted-value behavior. State additions/removals/changes are explicit authored intent. Current/baseline drift and relevant contradictions suppress candidates. Selected peer bytes are retained, not proof of native project preservation. No native import, merge, output rename, execution or complete dependency/consumer coverage.";
        }
        if (preparedReport is not null) return record;
        JsonObject recordArtifact;
        if (retained is null)
        {
            Publish(bytes, ".author-input", InputLimit);
            recordArtifact = Publish(JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json), ".author.json", ReportLimit);
        }
        else
        {
            if (!JsonNode.DeepEquals(record, retained)) throw new BridgeException("ArtifactChanged", "Bound authoring record differs from revalidated original evidence.");
            recordArtifact = Reference(recordId!, ".author.json");
        }
        var result = record.DeepClone().AsObject(); result["recordId"] = recordArtifact["sha256"]!.DeepClone(); result["recordArtifact"] = recordArtifact;
        return result;
    }
}
