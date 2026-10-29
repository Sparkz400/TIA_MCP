using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.HmiComponents;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core;

public sealed partial class HmiComponentWorkflowStore
{
    // v2 retains its own transition and candidates. It never rewrites or fabricates
    // a v1 set or an older single-output author record.
    private JsonObject EvolveTextListSet(JsonObject request, byte[] bytes, JsonObject input,
        JsonObject? retained, string? recordId, int depth, AuthorReadBudget budget)
    {
        bool conversions = (string?)request["schemaVersion"] == "unified-text-list-set-request.v3";
        Fields(request, "schemaVersion componentReviewId previousRecordId outputs removeOutputIds retainOutputIds requireObservedDependencies");
        string reviewId = Text(request, "componentReviewId", 64), previousId = Text(request, "previousRecordId", 64);
        EngineeringIdentityGuard.Sha256(reviewId, "componentReviewId");
        EngineeringIdentityGuard.Sha256(previousId, "previousRecordId");
        bool requireDependencies = Boolean(request, "requireObservedDependencies");
        var selections = Array(request, "outputs", 16);
        string[] Ids(string key, int limit)
        {
            var ids = Array(request, key, limit).Select(n => Text(new() { ["id"] = n?.DeepClone() }, "id", 128)).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw Error("Repeated " + key + " selection.");
            return ids;
        }
        string[] removeIds = Ids("removeOutputIds", 16), retainIds = Ids("retainOutputIds", 256);
        var previous = GenerateTextListSetCore(null, previousId, depth + 1, budget);
        if (!conversions && (string?)previous["schemaVersion"] == "unified-text-list-set-record.v3")
            throw Error("Use explicit v3 evolution for a v3 predecessor.");
        var preliminary = Object(Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(reviewId, ".json"), ReportLimit)))["report"]);
        budget.Bytes += bytes.Length + Array(preliminary, "files", 32).Sum(f => (long)Object(f)["bytes"]!);
        if (budget.Bytes > 128L * 1024 * 1024) throw Error("Coordinated lineage exceeds 128 MiB of declared original files and requests.");
        var page = Page(reviewId);
        var report = Object(Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Object(page["reportArtifact"]), ReportLimit)))["report"]);
        if ((string?)report["schemaVersion"] != "hmi-component-review.v7" || (string?)report["destination"]?["family"] != "unified")
            throw Error("Evolution requires one exact retained v7 Unified review.");
        var priorOutputs = previous["outputs"]!.AsArray().OfType<JsonObject>().ToDictionary(o => (string)o["outputId"]!, StringComparer.Ordinal);
        var declaredOutputs = report["outputs"]!.AsArray().OfType<JsonObject>().ToDictionary(o => (string)o["id"]!, StringComparer.Ordinal);
        var declaredBindings = report["bindings"]!.AsArray().OfType<JsonObject>().ToDictionary(o => (string)o["id"]!, StringComparer.Ordinal);
        var accountedPrior = new HashSet<string>(StringComparer.Ordinal);
        var accountedOutputs = new HashSet<string>(StringComparer.Ordinal);
        var accountedBindings = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new JsonArray(); var gaps = new JsonArray();
        var outputs = new JsonArray(); var removed = new JsonArray(); var preserved = new JsonArray();
        var proposals = new JsonArray(); var effects = new JsonArray();
        var candidateBytes = new List<(string Name, byte[] Bytes)>();
        JsonNode? languages = null; string? defaultLanguage = null; long totalBytes = 0;
        void Conflict(string message) => conflicts.Add(message);
        if ((string?)previous["status"] != "candidatesReady") Conflict("The predecessor set did not publish complete candidates.");
        var previousReport = Object(Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot,
            Reference((string)previous["componentReviewId"]!, ".json"), ReportLimit)))["report"]);
        if (!JsonNode.DeepEquals(previousReport["afterComponent"]?["selection"], report["beforeComponent"]?["selection"]) ||
            (string?)previousReport["destination"]?["hmiPath"] != (string?)report["destination"]?["hmiPath"] ||
            (string?)previousReport["destination"]?["family"] != (string?)report["destination"]?["family"])
            Conflict("Exact predecessor after-component scope and destination must match this evolution, including empty predecessor sets.");
        void AddEvidence(string id, JsonObject output, JsonObject? binding, bool explicitConversion = false)
        {
            foreach (var row in output["conflicts"]!.AsArray()) Conflict(id + ": " + (string)row!);
            foreach (var row in output["gaps"]!.AsArray()) gaps.Add(id + ": " + (string)row!);
            if (binding is null) return;
            foreach (var row in binding["conflicts"]!.AsArray())
                if (!explicitConversion || (string?)row != HmiIntegerConversion.TypeChangeConflict) Conflict(id + ": " + (string)row!);
            foreach (var row in binding["gaps"]!.AsArray()) gaps.Add(id + ": " + (string)row!);
        }
        JsonObject Output(string id)
        {
            if (!accountedOutputs.Add(id)) throw Error("A current review output was selected more than once.");
            return declaredOutputs.GetValueOrDefault(id) ?? throw Error("Unknown current review output: " + id);
        }
        JsonObject Binding(string id, string outputId)
        {
            if (!accountedBindings.Add(id)) throw Error("A binding was selected more than once.");
            var binding = declaredBindings.GetValueOrDefault(id) ?? throw Error("Unknown binding: " + id);
            if ((string?)binding["selection"]?["outputId"] != outputId) throw Error("Binding must select the exact current review output.");
            return binding;
        }
        JsonObject Prior(string id)
        {
            if (!accountedPrior.Add(id)) throw Error("A predecessor output was selected more than once.");
            return priorOutputs.GetValueOrDefault(id) ?? throw Error("Unknown predecessor output: " + id);
        }
        JsonArray PriorFiles(string id) => new(previous["files"]!.AsArray().Where(f => (string?)f!["outputId"] == id).Select(f => f!.DeepClone()).ToArray());
        static bool Pair(JsonArray supplied, JsonArray wanted) => supplied.Count == wanted.Count && wanted.All(p => supplied.Any(f =>
            (string?)f!["fileName"] == (string?)p!["fileName"] && (int?)f["bytes"] == (int?)p["bytes"] &&
            (string?)f["sha256"] == (string?)p["artifact"]?["sha256"]));
        void CheckPrior(string id, JsonObject output, JsonObject binding, JsonObject prior, string priorId)
        {
            var selection = output["selection"]!;
            if (!(bool)selection["baselineComplete"]! || !(bool)selection["currentComplete"]! ||
                !Pair(output["baselineFiles"]!.AsArray(), PriorFiles(priorId)) || !Pair(output["currentFiles"]!.AsArray(), PriorFiles(priorId)) ||
                PriorFiles(priorId).Count != 2)
                Conflict(id + ": Complete baseline and current pairs must equal the exact predecessor files; missing/stale/manual bytes refuse evolution.");
            var context = prior["componentContext"]!;
            if ((string?)context["afterComponent"]?["selection"]?["reviewId"] != (string?)report["beforeComponent"]?["selection"]?["reviewId"] ||
                !JsonNode.DeepEquals(context["binding"]?["selection"]?["afterMember"], binding["selection"]?["beforeMember"]))
                Conflict(id + ": Exact predecessor component/member must match the supplied before selection.");
            if ((string?)context["destination"]?["hmiPath"] != (string?)report["destination"]?["hmiPath"] ||
                (string?)context["destination"]?["family"] != (string?)report["destination"]?["family"])
                Conflict(id + ": Predecessor destination differs.");
            if ((string?)binding["beforeDeclaration"]?["status"] != "capturedDeclaration" ||
                (string?)binding["beforeDeclaration"]?["dataType"] != (string?)context["binding"]?["afterDeclaration"]?["dataType"])
                Conflict(id + ": Exact captured predecessor declaration/type is required.");
            if ((string?)selection["name"] != (string?)prior["intent"]?["lists"]?[0]?["name"])
                Conflict(id + ": The captured current output name must equal the predecessor list identity.");
            // Saved destination identities may change only through an independent
            // observation; the supplied output must actually be in that inventory.
            if (output["capturedObject"] is null) Conflict(id + ": Current output is not captured in the selected destination.");
        }
        JsonObject Dependencies(string id, JsonObject binding)
        {
            var coverage = CoordinatedDependencies(report, binding);
            if ((int)coverage["contradictionCount"]! > 0) Conflict(id + ": Original selected dependency associations contradict this transition.");
            if (!(bool)coverage["allObservedSelectedIndicesMatched"]!)
            {
                gaps.Add(id + ": Selected dependencies are unavailable/unmatched; closure remains unverified.");
                if (requireDependencies) Conflict(id + ": Observed selected dependency indices are required.");
            }
            return coverage;
        }
        JsonArray Consumers(JsonObject binding) => new(report["consumerPaths"]!.AsArray().Where(p =>
            new[] { ("beforeComponent", "beforeMember"), ("afterComponent", "afterMember") }.Any(s =>
                (string?)p!["scope"] == s.Item1 && (string?)p["reviewId"] == (string?)report[s.Item1]?["selection"]?["reviewId"] &&
                ((string?)p["rootObjectPath"] == (string?)binding["selection"]?[s.Item2]?["objectPath"] ||
                 (string?)p["consumerObjectPath"] == (string?)binding["selection"]?[s.Item2]?["objectPath"]))).Select(p => p!.DeepClone()).ToArray());
        JsonObject Context(string id, string bindingId, JsonObject binding) => new() {
            ["reviewRecordId"] = reviewId, ["outputId"] = id, ["bindingId"] = bindingId,
            ["afterComponent"] = report["afterComponent"]!.DeepClone(), ["destination"] = report["destination"]!.DeepClone(),
            ["binding"] = binding.DeepClone() };
        JsonObject Comparison(JsonObject output, JsonArray proposed) => new() {
            ["baselineFiles"] = output["baselineFiles"]!.DeepClone(), ["currentFiles"] = output["currentFiles"]!.DeepClone(),
            ["suppliedProposedFiles"] = output["proposedFiles"]!.DeepClone(), ["proposedFiles"] = proposed.DeepClone(),
            ["suppliedProposedStatus"] = output["proposedFiles"]!.AsArray().Count == 0 && !(bool)output["selection"]!["proposedComplete"]! ? "notSupplied" :
                (bool)output["selection"]!["proposedComplete"]! && Pair(output["proposedFiles"]!.AsArray(), proposed) ? "exact" : "conflict" };
        foreach (var item in selections)
        {
            var row = Object(item);
            Fields(row, "outputId previousOutputId operation bindingId bindingAction profile listName template" + (conversions && row.ContainsKey("typeConversion") ? " typeConversion" : ""));
            string id = Text(row, "outputId", 128), op = Text(row, "operation", 16), bindingId = Text(row, "bindingId", 128),
                bindingAction = Text(row, "bindingAction", 16), profile = Text(row, "profile", 128), listName = Text(row, "listName", 256);
            if (op is not ("add" or "update" or "rename") || bindingAction is not ("preserve" or "rebind")) throw Error("Unknown explicit evolution operation or binding action.");
            string? priorId = row["previousOutputId"] is null ? null : Text(row, "previousOutputId", 128);
            if ((op == "add") != (priorId is null)) throw Error("Only additions omit previousOutputId.");
            var output = Output(id); var binding = Binding(bindingId, id); var selection = output["selection"]!;
            var prior = priorId is null ? null : Prior(priorId);
            var conversion = row["typeConversion"] is null ? null : Object(row["typeConversion"]);
            AddEvidence(id, output, binding, conversion is not null);
            if ((string?)selection["kind"] != "hmiResource" || (string?)selection["ownership"] != "generated" ||
                (string?)selection["action"] != (op == "add" ? "create" : "update"))
                Conflict(id + ": Evolution requires the exact generated hmiResource create/update action.");
            bool numeric = profile == "unified-integer-state-text-list.v1";
            if (!numeric && profile != "unified-bool-binding-text-list.v1") throw Error("Only established Bool/integer paired formats are supported.");
            var template = Object(row["template"]);
            var intent = EvolutionIntent(template, listName, numeric);
            var built = UnifiedTextListAuthoring.Build(intent);
            if (retained is null) UnifiedTextListAuthoring.RequireNativeImportable(intent);
            string expectedType = numeric ? Text(template, "dataType", 16) : "Bool";
            if ((string?)binding["afterDeclaration"]?["status"] != "capturedDeclaration" ||
                (string?)binding["afterDeclaration"]?["dataType"] != expectedType ||
                (string?)binding["selection"]?["declaredHmiType"] != expectedType)
                Conflict(id + ": Captured after declaration and declared HMI type must exactly equal the template type.");
            JsonObject? conversionEvidence = null;
            if (conversion is not null)
            {
                conversionEvidence = HmiIntegerConversion.Evaluate(conversion, prior, binding, intent, expectedType);
                foreach (var reason in conversionEvidence["conflicts"]!.AsArray()) Conflict(id + ": " + (string)reason!);
                if (!numeric || op != "update" || bindingAction != "preserve")
                    Conflict(id + ": Explicit integer conversion requires an existing numeric update and preserved binding/output identities.");
            }
            if (op == "add")
            {
                if (priorOutputs.ContainsKey(id) || bindingAction != "preserve" || binding["selection"]?["beforeMember"] is not null ||
                    output["baselineFiles"]!.AsArray().Count != 0 || output["currentFiles"]!.AsArray().Count != 0 ||
                    !(bool)selection["baselineComplete"]! || !(bool)selection["currentComplete"]! || (string?)selection["name"] != listName)
                    Conflict(id + ": Addition requires a new identity, complete empty baseline/current sets and an explicit added member binding.");
            }
            else
            {
                CheckPrior(id, output, binding, prior!, priorId!);
                if ((string?)prior!["profile"] != profile || conversion is null && (string?)prior["componentContext"]?["binding"]?["afterDeclaration"]?["dataType"] != expectedType)
                    Conflict(id + ": Profile and exact type are fixed across evolution; conversions are unsupported.");
                bool sameIdentity = id == priorId && (string?)prior["intent"]?["fileBase"] == (string?)intent["fileBase"] &&
                    (string?)prior["intent"]?["textLibrary"] == (string?)intent["textLibrary"] &&
                    (string?)prior["intent"]?["lists"]?[0]?["name"] == listName;
                if (op == "update" && !sameIdentity || op == "rename" && sameIdentity)
                    Conflict(id + ": Identity changes require explicit rename; rename must change an output/file/list/library identity.");
                bool sameBinding = bindingId == (string?)prior["bindingId"] &&
                    JsonNode.DeepEquals(binding["selection"]?["beforeMember"], binding["selection"]?["afterMember"]) &&
                    (string?)binding["selection"]?["property"] == (string?)prior["componentContext"]?["binding"]?["selection"]?["property"];
                if (bindingAction == "preserve" && !sameBinding || bindingAction == "rebind" && sameBinding)
                    Conflict(id + ": Member/property/binding identity changes require explicit rebind; rebind must change a selection.");
            }
            if (!libraries.Add((string)intent["textLibrary"]!)) Conflict("Proposed text-library identities collide, including case variants.");
            if (!lists.Add(listName)) Conflict("Proposed text-list identities collide, including case variants.");
            if (languages is null) { languages = intent["languages"]; defaultLanguage = (string)intent["defaultLanguage"]!; }
            else if (!JsonNode.DeepEquals(languages, intent["languages"]) || defaultLanguage != (string?)intent["defaultLanguage"])
                Conflict("All proposed outputs require identical explicit language order and default.");
            var proposed = new JsonArray();
            foreach (var file in built)
            {
                if (!names.Add(file.Name)) Conflict("Proposed filenames collide, including case variants: " + file.Name);
                totalBytes += file.Bytes.Length; candidateBytes.Add((file.Name, file.Bytes));
                var fileRow = new JsonObject { ["outputId"] = id, ["fileName"] = file.Name, ["bytes"] = file.Bytes.Length,
                    ["artifact"] = Reference(Convert.ToHexStringLower(SHA256.HashData(file.Bytes)), ".candidate") };
                proposed.Add(fileRow.DeepClone()); proposals.Add(fileRow);
            }
            var comparison = Comparison(output, proposed);
            if ((string?)comparison["suppliedProposedStatus"] == "conflict")
                Conflict(id + ": Independently supplied proposed files must be complete and equal the derived pair.");
            JsonObject? valueCoverage = numeric ? HmiNumericTextLists.Coverage(template, intent) : null;
            if (numeric && !(bool)valueCoverage!["allTypeValuesListed"]!)
            {
                gaps.Add(id + ": Explicit numeric states leave unlisted values; defaults do not prove native handling.");
                if (Boolean(template, "requireFullTypeCoverage")) Conflict(id + ": Required full integer coverage has gaps.");
            }
            outputs.Add(new JsonObject { ["outputId"] = id, ["previousOutputId"] = priorId, ["operation"] = op, ["bindingId"] = bindingId,
                ["bindingAction"] = bindingAction, ["profile"] = profile, ["componentContext"] = Context(id, bindingId, binding),
                ["intent"] = intent, ["comparison"] = comparison, ["dependencyCoverage"] = Dependencies(id, binding), ["consumerPaths"] = Consumers(binding),
                ["valueCoverage"] = valueCoverage, ["stateChanges"] = numeric ? HmiNumericTextLists.Changes(prior?["intent"] as JsonObject, intent) : null });
            if (conversions) outputs[^1]!["typeConversion"] = conversionEvidence;
            effects.Add(new JsonObject { ["operation"] = op, ["previousOutputId"] = priorId, ["outputId"] = id,
                ["bindingAction"] = bindingAction, ["currentObjectPath"] = selection["objectPath"]?.DeepClone(),
                ["previousListName"] = prior?["intent"]?["lists"]?[0]?["name"]?.DeepClone(), ["proposedListName"] = listName,
                ["retireFiles"] = priorId is null ? new JsonArray() : PriorFiles(priorId), ["proposedFiles"] = proposed.DeepClone(),
                ["destructiveEffectPreviewOnly"] = priorId is not null, ["nativeExecutable"] = false });
        }
        foreach (string id in removeIds)
        {
            var prior = Prior(id); var output = Output(id);
            var matching = declaredBindings.Values.Where(b => (string?)b["selection"]?["outputId"] == id).ToArray();
            if (matching.Length != 1) throw Error("Removal requires one exact before-only binding for its current review output.");
            string bindingId = (string)matching[0]["id"]!; var binding = Binding(bindingId, id); var selection = output["selection"]!;
            AddEvidence(id, output, binding); CheckPrior(id, output, binding, prior, id);
            if (bindingId != (string?)prior["bindingId"] ||
                (string?)binding["selection"]?["property"] != (string?)prior["componentContext"]?["binding"]?["selection"]?["property"])
                Conflict(id + ": Removal must select the exact predecessor binding identity and property.");
            if ((string?)selection["action"] != "remove" || (string?)selection["kind"] != "hmiResource" || (string?)selection["ownership"] != "generated" ||
                binding["selection"]?["afterMember"] is not null || output["proposedFiles"]!.AsArray().Count != 0 || !(bool)selection["proposedComplete"]!)
                Conflict(id + ": Removal requires generated hmiResource remove, a before-only binding and a complete empty proposed set.");
            removed.Add(new JsonObject { ["outputId"] = id, ["bindingId"] = bindingId, ["priorOutput"] = prior.DeepClone(),
                ["comparison"] = Comparison(output, new()), ["binding"] = binding.DeepClone(), ["dependencyCoverage"] = Dependencies(id, binding),
                ["consumerPaths"] = Consumers(binding) });
            effects.Add(new JsonObject { ["operation"] = "remove", ["previousOutputId"] = id, ["outputId"] = null,
                ["currentObjectPath"] = selection["objectPath"]?.DeepClone(), ["retireFiles"] = PriorFiles(id), ["proposedFiles"] = new JsonArray(),
                ["destructiveEffectPreviewOnly"] = true, ["nativeExecutable"] = false });
        }
        foreach (string id in retainIds)
        {
            var peer = Output(id); AddEvidence(id, peer, null);
            if ((string?)peer["selection"]?["action"] != "retain" || !(bool)peer["selection"]!["currentComplete"]! || peer["currentFiles"]!.AsArray().Count == 0)
                Conflict(id + ": Retained peers require complete nonempty current bytes and retain action.");
            foreach (var file in peer["currentFiles"]!.AsArray())
            {
                string name = (string)file!["fileName"]!;
                if (!names.Add(name)) Conflict("Proposed/preserved filenames collide, including case variants: " + name);
                totalBytes += (int)file["bytes"]!;
                preserved.Add(new JsonObject { ["outputId"] = id, ["fileName"] = name, ["bytes"] = file["bytes"]!.DeepClone(), ["artifact"] = Reference((string)file["sha256"]!, ".source") });
            }
        }
        if (!JsonNode.DeepEquals(previous["preservedFiles"], preserved)) Conflict("Retained peer membership, ordering, names and exact bytes must remain unchanged.");
        var vacatedPaths = selections.Select(o => (string?)declaredOutputs[(string)o!["outputId"]!]["selection"]?["objectPath"])
            .Concat(removeIds.Select(id => (string?)declaredOutputs[id]["selection"]?["objectPath"])).Where(p => p is not null).ToHashSet(StringComparer.Ordinal);
        var destinationEvidence = new DiagnosticEvidence(new ProjectReviewStore(artifactsRoot).Read((string)report["destination"]!["reviewId"]!));
        foreach (var output in outputs.OfType<JsonObject>())
        {
            string parent = (string)declaredOutputs[(string)output["outputId"]!]["selection"]!["parentPath"]!;
            if (!destinationEvidence.Nodes.TryGetValue(parent, out var parentNode)) continue;
            if (destinationEvidence.Nodes.Values.Any(n => (string?)n["parentId"] == (string?)parentNode["nodeId"] &&
                !vacatedPaths.Contains((string?)n["objectPath"]) &&
                string.Equals((string?)n["name"], (string?)output["intent"]?["lists"]?[0]?["name"], StringComparison.OrdinalIgnoreCase)))
                Conflict((string)output["outputId"]! + ": Proposed list name collides with a captured destination object outside the evolved/removed outputs.");
        }
        var uncoveredPrior = priorOutputs.Keys.Except(accountedPrior, StringComparer.Ordinal).ToArray();
        var uncoveredOutputs = declaredOutputs.Keys.Except(accountedOutputs, StringComparer.Ordinal).ToArray();
        var uncoveredBindings = declaredBindings.Keys.Except(accountedBindings, StringComparer.Ordinal).ToArray();
        if (uncoveredPrior.Length > 0) Conflict("Every predecessor generated output must be evolved or explicitly removed.");
        if (uncoveredOutputs.Length > 0) Conflict("Every declared current review output must be evolved, removed or explicitly retained.");
        if (uncoveredBindings.Length > 0) Conflict("Every declared binding must be selected once; retained peer bindings are outside this profile.");
        if (proposals.Count + preserved.Count > 32 || totalBytes > 64L * 1024 * 1024)
            throw new BridgeException("ArtifactCapacity", "Evolution is bounded to 32 proposed/preserved files and 64 MiB.");
        // A swap is safe only as a whole-set preview. Record every cross-output
        // reuse explicitly; no temporary native rename or deletion is inferred.
        var nameTransfers = new JsonArray();
        foreach (var proposed in proposals)
            foreach (var old in previous["files"]!.AsArray().Where(f => string.Equals((string?)f!["fileName"], (string?)proposed!["fileName"], StringComparison.OrdinalIgnoreCase)))
                if ((string?)old!["outputId"] != (string?)outputs.OfType<JsonObject>().Single(o => (string?)o["outputId"] == (string?)proposed!["outputId"])["previousOutputId"])
                    nameTransfers.Add(new JsonObject { ["fileName"] = proposed!["fileName"]!.DeepClone(), ["fromOutputId"] = old["outputId"]!.DeepClone(),
                        ["toOutputId"] = proposed["outputId"]!.DeepClone(), ["previewOnly"] = true });
        var members = SetMemberCoverage(report, accountedBindings);
        gaps.Add("Exact declared predecessor/current scope only. Unselected members, unobserved project content, omitted/truncated consumers and dependencies remain unqualified.");
        if (conversions) foreach (var effect in effects)
        {
            effect!["typeConversion"] = selections.FirstOrDefault(row => (string?)row!["outputId"] == (string?)effect["outputId"])?["typeConversion"]?.DeepClone();
            effect["plcValueConversionPerformed"] = false;
        }
        bool ready = conflicts.Count == 0;
        var record = new JsonObject {
            ["schemaVersion"] = conversions ? "unified-text-list-set-record.v3" : "unified-text-list-set-record.v2", ["input"] = input.DeepClone(), ["componentReviewId"] = reviewId,
            ["previousRecordId"] = previousId, ["previousRecordArtifact"] = previous["recordArtifact"]!.DeepClone(),
            ["status"] = ready ? "candidatesReady" : "conflict", ["outputs"] = outputs, ["removedOutputs"] = removed,
            ["files"] = ready ? proposals.DeepClone() : new JsonArray(), ["preservedFiles"] = preserved,
            ["effectsPreview"] = effects, ["filenameTransfers"] = nameTransfers, ["conflicts"] = conflicts, ["gaps"] = gaps,
            ["coverage"] = new JsonObject {
                ["declaredOutputIds"] = new JsonArray(declaredOutputs.Keys.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["declaredBindingIds"] = new JsonArray(declaredBindings.Keys.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["uncoveredPredecessorOutputIds"] = new JsonArray(uncoveredPrior.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["uncoveredOutputIds"] = new JsonArray(uncoveredOutputs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["uncoveredBindingIds"] = new JsonArray(uncoveredBindings.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["allPredecessorOutputsCovered"] = uncoveredPrior.Length == 0, ["allDeclaredOutputsCovered"] = uncoveredOutputs.Length == 0,
                ["allDeclaredBindingsCovered"] = uncoveredBindings.Length == 0, ["members"] = members,
                ["sourceConsumerCoverage"] = report["consumerCoverage"]!.DeepClone(), ["sourceDependencyImpactCoverage"] = report["dependencyImpactCoverage"]!.DeepClone(),
                ["consumerCoverageComplete"] = false, ["dependencyClosureVerified"] = false, ["componentMemberCoverageComplete"] = false },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["preservationVerified"] = false, ["ownershipVerified"] = false,
            ["policy"] = "Explicit offline set evolution over exact prior v1/v2 records. Add/update/rename/remove and same-type member rebinding are complete declared-scope previews only. Baseline/current files must equal the predecessor pair; optional independent proposed pairs must match. Conflicts suppress all published candidates. Retained peers remain exact. No merge, implicit conversion, native rename/deletion/import, project completeness, ownership or preservation qualification. Final immutable record marks completion; interruption may leave unreferenced immutable artifacts." };
        if (conversions) record["policy"] = "Explicit offline v3 set evolution. Integer conversions require the integer-value-preserving.v1 profile, exact captured before/after types, unchanged state entries/translations, exact predecessor/current pairs and preserved binding/output identities. Only listed intervals are representable; unlisted values, PLC conversion, runtime compatibility and native import remain unqualified. Original binding conflicts remain retained; all other conflicts suppress every candidate. No implicit conversion, truncation, wrapping, clamping or native execution.";
        byte[] recordBytes = JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json);
        if (recordBytes.Length > ReportLimit) throw new BridgeException("ArtifactCapacity", "Evolution record exceeds 16 MiB.");
        JsonObject artifact;
        if (retained is null)
        {
            if (ready) foreach (var file in candidateBytes) Publish(file.Bytes, ".candidate", UnifiedTextListAuthoring.OutputLimit);
            Publish(bytes, ".text-set-input", InputLimit);
            artifact = Publish(recordBytes, ".text-set.json", ReportLimit);
        }
        else
        {
            if (ready) foreach (var file in candidateBytes)
                if (!ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(Convert.ToHexStringLower(SHA256.HashData(file.Bytes)), ".candidate"), UnifiedTextListAuthoring.OutputLimit).AsSpan().SequenceEqual(file.Bytes))
                    throw new BridgeException("ArtifactChanged", "Evolved candidate differs from original intent.");
            if (!JsonNode.DeepEquals(record, retained)) throw new BridgeException("ArtifactChanged", "Evolution differs from revalidated originals, comparisons or complete predecessor lineage.");
            artifact = Reference(recordId!, ".text-set.json");
        }
        var result = record.DeepClone().AsObject(); result["recordId"] = artifact["sha256"]!.DeepClone(); result["recordArtifact"] = artifact;
        return result;
    }

    private static JsonObject EvolutionIntent(JsonObject template, string listName, bool numeric)
    {
        if (numeric) return HmiNumericTextLists.Intent(template, listName);
        Fields(template, "fileBase textLibrary languages defaultLanguage falseText trueText");
        return new JsonObject {
            ["schemaVersion"] = "unified-text-list-author-request.v1", ["fileBase"] = template["fileBase"]?.DeepClone(),
            ["textLibrary"] = template["textLibrary"]?.DeepClone(), ["languages"] = template["languages"]?.DeepClone(),
            ["defaultLanguage"] = template["defaultLanguage"]?.DeepClone(),
            ["texts"] = new JsonArray(
                new JsonObject { ["id"] = "FalseText", ["translations"] = template["falseText"]?.DeepClone() },
                new JsonObject { ["id"] = "TrueText", ["translations"] = template["trueText"]?.DeepClone() }),
            ["lists"] = new JsonArray(new JsonObject {
                ["name"] = listName, ["rangeType"] = "Bool", ["entries"] = new JsonArray(
                    new JsonObject { ["id"] = "False", ["type"] = "SingleValue", ["fromValue"] = 0, ["toValue"] = 0, ["isDefault"] = false, ["textId"] = "FalseText" },
                    new JsonObject { ["id"] = "True", ["type"] = "SingleValue", ["fromValue"] = 1, ["toValue"] = 1, ["isDefault"] = false, ["textId"] = "TrueText" }) }) };
    }
}
