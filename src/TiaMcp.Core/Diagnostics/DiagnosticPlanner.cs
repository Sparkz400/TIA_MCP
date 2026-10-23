using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Ranks captured evidence; never silently chooses equipment or asserts online observability.</summary>
public sealed class DiagnosticPlanner(string artifactsRoot)
{
    public JsonObject Plan(JsonObject request)
    {
        D.Bound(request);
        request = DiagnosticReportContext.Complete(request);
        string reviewId = D.Required(request, "reviewId", 64), report = D.Required(request, "report", 8000);
        var testSuiteEvidence = ResolveTestSuiteFinding(request, reviewId);
        var review = new ProjectReviewStore(artifactsRoot).Read(reviewId);
        var alarmEvidence = request.ContainsKey("alarmContext") ? ProjectReviewStore.AlarmContext(reviewId, review, request["alarmContext"]) : null;
        var source = new DiagnosticEvidence(review);
        string templateId = D.Text(request, "templateId");
        var template = DiagnosticTemplates.All().OfType<JsonObject>().SingleOrDefault(t => D.Text(t, "id") == templateId);
        if (templateId.Length > 0 && template is null) throw new BridgeException("InvalidRequest", "Unknown diagnostic templateId; trace_capabilities lists supported role templates.");
        var adjacent = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var edge in D.Rows(review["model"]?["edges"]).OfType<JsonObject>())
        {
            if (D.Text(edge["evidence"], "resolution") != "resolved" || D.Text(edge["evidence"], "origin") is not ("nativeConfirmed" or "parsedSupported")) continue;
            string left = D.Text(edge["source"], "objectPath"), right = D.Text(edge["target"], "objectPath");
            if (!source.Nodes.ContainsKey(left) || !source.Nodes.ContainsKey(right)) continue;
            foreach (string endpoint in new[] { left, right })
            { if (!adjacent.TryGetValue(endpoint, out var rows)) adjacent[endpoint] = rows = []; if (rows.Count < 100) rows.Add(edge); }
        }
        var terms = D.Rows(request["searchTerms"]).Select(t => t!.GetValue<string>()).ToArray();
        if (terms.Length is < 1 or > 12 || terms.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 128))
            throw new BridgeException("InvalidRequest", "Supply 1..12 literal searchTerms drawn from the report or known project context.");
        var hypotheses = D.Rows(request["hypotheses"]);
        if (hypotheses.Count is < 1 or > 12) throw new BridgeException("InvalidRequest", "Supply 1..12 explicit hypotheses, separate from observations.");
        var hypothesisIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in hypotheses)
        {
            if (h is not JsonObject || !hypothesisIds.Add(D.Required(h, "id", 64))) throw new BridgeException("InvalidRequest", "Hypothesis IDs must be unique.");
            D.Required(h, "statement", 2000);
        }
        var candidates = new List<JsonObject>();
        foreach (var node in source.Nodes.Values.Where(n => D.Text(n, "kind") == "block"))
        {
            string path = D.Text(node, "objectPath");
            source.Documents.TryGetValue(path, out var doc);
            var reasons = new JsonArray(); int score = 0;
            foreach (string term in terms.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (node.ToJsonString().Contains(term, StringComparison.OrdinalIgnoreCase))
                { score += 10; reasons.Add(new JsonObject { ["term"] = term, ["origin"] = "capturedMetadata" }); }
                if (D.Text(doc, "status") == "exported" && D.Text(doc, "xml").Contains(term, StringComparison.OrdinalIgnoreCase))
                { score += 3; reasons.Add(new JsonObject { ["term"] = term, ["origin"] = "literalXmlMatch", ["utf8Sha256"] = doc!["utf8Sha256"]?.DeepClone() }); }
                if (adjacent.TryGetValue(path, out var edges))
                {
                    var match = edges.FirstOrDefault(e => source.Nodes[D.Text(e["source"], "objectPath") == path ? D.Text(e["target"], "objectPath") : D.Text(e["source"], "objectPath")]
                        .ToJsonString().Contains(term, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) { score += 2; reasons.Add(new JsonObject { ["term"] = term, ["origin"] = "capturedAdjacentRelationship", ["edge"] = match.DeepClone() }); }
                }
            }
            if (score == 0) continue;
            candidates.Add(new JsonObject { ["objectPath"] = path, ["name"] = node["name"]?.DeepClone(), ["score"] = score,
                ["plcPath"] = node["plcPath"]?.DeepClone(), ["scopePath"] = node["scopePath"]?.DeepClone(),
                ["reasons"] = reasons, ["sourceStatus"] = doc?["status"]?.DeepClone() ?? JsonValue.Create("notCaptured"),
                ["metadata"] = node["metadata"]?.DeepClone() });
        }
        string blockPath = D.Text(request, "blockPath");
        if (blockPath.Length > 0 && (!source.Nodes.TryGetValue(blockPath, out var chosen) || D.Text(chosen, "kind") != "block"))
            throw new BridgeException("ObjectNotFound", "blockPath must identify an exact captured block.");
        var instances = blockPath.Length == 0 ? new JsonArray() : source.Instances(blockPath);
        JsonObject? selected = null;
        if (request["instance"] is not null && request["instance"] is not JsonObject)
            throw new BridgeException("InvalidRequest", "instance must be an explicit selector object.");
        if (request["instance"] is JsonObject selector)
        {
            string dbPath = D.Required(selector, "dbPath");
            if (selector["memberPath"] is not JsonArray memberArray || memberArray.Count > 8)
                throw new BridgeException("InvalidRequest", "instance.memberPath must be an explicit array of at most 8 member names (empty for a root instance).");
            var memberNames = memberArray.Count == 0 ? [] : D.Path(memberArray);
            var matches = instances.OfType<JsonObject>().Where(i => D.Text(i, "dbPath") == dbPath
                && D.Rows(i["memberPath"]).Select(n => n!.GetValue<string>()).SequenceEqual(memberNames)).ToArray();
            if (matches.Length != 1) throw new DiagnosticResolutionException("Selected instance lacks a unique captured native type relation, embedded declaration and explicit supported call chain.",
                new JsonObject { ["reviewId"] = reviewId, ["blockPath"] = blockPath, ["requestedInstance"] = selector.DeepClone(), ["resolvedAlternatives"] = instances.DeepClone(), ["traversal"] = source.Traversal() });
            selected = matches[0];
        }
        if (request["signals"] is not null && request["signals"] is not JsonArray) throw new BridgeException("InvalidRequest", "signals must be an array.");
        var requests = (JsonArray)D.Rows(request["signals"]).DeepClone();
        JsonObject? automaticSelection = null;
        if (request["autoSignals"] is not null)
        {
            if (request["autoSignals"] is not JsonObject auto || selected is null)
                throw new BridgeException("InstanceResolutionIncomplete", "autoSignals requires an explicitly resolved instance and an options object.");
            automaticSelection = new DiagnosticSignalSelector(source, selected, hypotheses).Select(auto);
            foreach (var proposed in D.Rows(automaticSelection["signals"])) requests.Add(proposed!.DeepClone());
        }
        var signals = ResolveSignals(source,reviewId,selected,requests,hypothesisIds);
        if (signals.Count > 0) TraceMeasurements.ValidatePredictions(hypotheses, signals);
        var gaps = new JsonArray(ProjectReviewStore.SourceEvidencePolicy, "Saved evidence is sequential and may be stale; online program identity is unknown.",
            "Report extraction/ranking is lexical. Automatic selection uses conservative compile-unit/network dependencies and simple explicit call bindings, not full program semantics.",
            "Relationship ranking inspects at most 100 captured adjacent edges per node and reports one matching edge per term. Missing HMI/alarm/library/I/O relationships are not inferred.",
            "Only exact native root type edges and bounded explicit XML local calls with exact qualified type names are resolved. Cross-unit aliases, arrays, indirect access and opaque languages remain unresolved.",
            "Signals are caller-proposed or derived from bounded dependency evidence and checked against exported declarations; every signal needs native target observability validation.",
            "Settings, resources, firmware, licenses and sampling effects are not probed. Native trace lifecycle is unavailable.");
        var questions = new JsonArray();
        if (blockPath.Length == 0) questions.Add("Select the exact candidate block/CPU from project evidence.");
        if (selected is null) questions.Add("Identify the exact instance DB and nested member path; alternatives are retained.");
        if (selected is not null && (D.Rows(selected["callSites"]).Count == 0 || D.Rows(selected["rootCallSites"]).Count == 0)) questions.Add("Capture the missing root/callee call site before associating this storage with the reported execution context.");
        if (signals.Count == 0) questions.Add("Select a minimal signal set justified by the hypotheses and captured references.");
        if (request["context"]?["timing"] is null) questions.Add("What is the shortest relevant fault duration or task interval? This changes sampling requirements.");
        if (automaticSelection?["truncated"]?.GetValue<bool>() == true) questions.Add("The automatic signal budget/depth was exhausted; review omitted coverage before preparing a capture.");
        var plan = new JsonObject { ["schemaVersion"] = "diagnostic-case.v1", ["reviewId"] = reviewId, ["report"] = report,
            ["template"] = template?.DeepClone(),
            ["lineage"] = request["lineage"]?.DeepClone(),
            ["context"] = request["context"]?.DeepClone(), ["observations"] = request["observations"]?.DeepClone(),
            ["hypotheses"] = hypotheses.DeepClone(), ["project"] = review["model"]?["project"]?.DeepClone(),
            ["sourceRevision"] = new JsonObject { ["reviewId"] = reviewId, ["documents"] = new JsonArray(source.Documents.Values.Select(d => (JsonNode?)new JsonObject {
                ["objectPath"] = d["objectPath"]?.DeepClone(), ["status"] = d["status"]?.DeepClone(), ["utf8Sha256"] = d["utf8Sha256"]?.DeepClone() }).ToArray()), ["onlineVerified"] = false },
            ["candidates"] = new JsonArray(candidates.OrderByDescending(c => c["score"]!.GetValue<int>()).ThenBy(c => D.Text(c, "objectPath"), StringComparer.Ordinal).Take(25).Select(c => (JsonNode?)c).ToArray()),
            ["candidateCount"] = candidates.Count, ["rankingTruncated"] = candidates.Count > 25,
            ["blockPath"] = blockPath, ["instances"] = instances, ["selectedInstance"] = selected?.DeepClone(),
            ["signals"] = signals, ["questions"] = questions, ["coverageGaps"] = gaps, ["nativeExecutable"] = false,
            ["automaticSelection"] = automaticSelection,
            ["coverage"] = review["capture"]?.DeepClone(), ["instanceTraversal"] = source.Traversal(),
            ["qualification"] = "offlinePlanningOnly" };
        if (testSuiteEvidence is not null) plan["testSuiteEvidence"] = testSuiteEvidence;
        if (alarmEvidence is not null) plan["alarmEvidence"] = alarmEvidence;
        if (request.ContainsKey("alarmBindings"))
        {
            if (alarmEvidence is null) throw new BridgeException("InvalidRequest", "alarmBindings requires an explicit alarmContext.");
            plan["alarmBindings"] = DiagnosticAlarmBindings.Resolve(source, alarmEvidence, selected, signals, request["alarmBindings"]);
            if (plan["alarmBindings"]!["allSelectedMatched"]?.GetValue<bool>() != true)
                questions.Add("Resolve the selected alarm operand binding gaps before watch or trace handoff.");
        }
        var receipt = new DiagnosticStore(artifactsRoot).Save(plan);
        receipt["plan"] = plan;
        return receipt;
    }

    private JsonObject? ResolveTestSuiteFinding(JsonObject request, string reviewId)
    {
        if (request["testSuiteFinding"] is null) return null;
        if (request["testSuiteFinding"] is not JsonObject selector || selector.Count != 3 ||
            selector.Any(p => p.Key is not ("relativePath" or "sha256" or "index")) ||
            selector["index"] is not JsonValue indexValue || !indexValue.TryGetValue<int>(out int index))
            throw new BridgeException("InvalidRequest", "testSuiteFinding requires only receipt relativePath, sha256 and integer index.");
        var evidence = new ProjectReviewStore(artifactsRoot).ReadTestSuiteFinding(
            D.Required(selector, "relativePath"), D.Required(selector, "sha256", 64), index);
        var row = evidence["finding"]!; var reference = row["finding"]!["resolvedObject"];
        if (D.Text(evidence, "reviewId") != reviewId || D.Text(row, "binding") != "exactCapturedSource" || reference is null)
            throw new BridgeException("TestSuiteFindingUnresolved", "The selected finding must bind to this exact saved review; cross-observation candidates remain unresolved.");
        string path = D.Text(reference["object"], "objectPath");
        if (D.Text(reference["object"], "kind") != "block")
            throw new BridgeException("TestSuiteFindingUnresolved", "The selected finding does not identify a captured block; no parent block is inferred.");
        string selected = D.Text(request, "blockPath");
        if (selected.Length > 0 && selected != path)
            throw new BridgeException("TestSuiteFindingUnresolved", "blockPath differs from the explicitly selected finding.");
        request["blockPath"] = path;
        return evidence;
    }

    internal static DiagnosticEvidence Revalidate(JsonObject plan, JsonObject review)
    {
        try
        {
            string reviewId = D.Required(plan, "reviewId", 64);
            var source = new DiagnosticEvidence(review);
            var documents = new JsonArray(source.Documents.Values.Select(d => (JsonNode?)new JsonObject {
                ["objectPath"] = d["objectPath"]?.DeepClone(), ["status"] = d["status"]?.DeepClone(), ["utf8Sha256"] = d["utf8Sha256"]?.DeepClone() }).ToArray());
            void Same(JsonNode? retained, JsonNode? derived)
            {
                if (!JsonNode.DeepEquals(retained, derived)) throw new BridgeException("CaptureMismatch", "Diagnostic evidence differs from its retained saved source.");
            }
            Same(plan["project"], review["model"]?["project"]);
            Same(plan["sourceRevision"], new JsonObject { ["reviewId"] = reviewId, ["documents"] = documents, ["onlineVerified"] = false });
            Same(plan["nativeExecutable"], JsonValue.Create(false));
            Same(plan["qualification"], JsonValue.Create("offlinePlanningOnly"));
            var selected = plan["selectedInstance"] as JsonObject;
            if (plan["selectedInstance"] is not null)
            {
                if (selected is null || source.Instances(D.Required(plan, "blockPath")).Count(i => JsonNode.DeepEquals(i, selected)) != 1)
                    throw new BridgeException("CaptureMismatch", "Diagnostic instance and call bindings do not resolve uniquely from the retained source.");
            }
            if (plan["signals"] is not JsonArray signals) throw new BridgeException("CaptureMismatch", "Malformed diagnostic signal manifest.");
            var hypotheses = D.Rows(plan["hypotheses"]).Select(h => D.Required(h!, "id", 64)).ToHashSet(StringComparer.Ordinal);
            var resolved = ResolveSignals(source, reviewId, selected, signals, hypotheses);
            // Hashes prove retained bytes, not declarations. Recompute every derived storage field.
            foreach (var pair in signals.Zip(resolved))
                foreach (string field in new[] { "storagePath", "memberPath", "datatype", "symbolComponents", "namespace", "scopePath",
                    "resolution", "unavailableReason", "traceability", "evidence", "onlineAddress" })
                    Same(pair.First?[field], pair.Second?[field]);
            if (plan.ContainsKey("alarmEvidence"))
            {
                if (plan["alarmEvidence"] is not JsonObject alarm) throw new BridgeException("CaptureMismatch", "Malformed retained alarm context.");
                Same(alarm, ProjectReviewStore.AlarmContext(reviewId, review, alarm["selector"]));
            }
            if (plan.ContainsKey("alarmBindings"))
            {
                if (plan["alarmBindings"] is not JsonObject bindings || plan["alarmEvidence"] is not JsonObject alarm)
                    throw new BridgeException("CaptureMismatch", "Malformed retained alarm binding context.");
                Same(bindings, DiagnosticAlarmBindings.Resolve(source, alarm, selected, signals, bindings["selectors"]));
            }
            return source;
        }
        catch (BridgeException e) when (e.Code != "CaptureMismatch")
        { throw new BridgeException("CaptureMismatch", "Retained diagnostic selectors cannot be re-resolved: " + e.Code); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or FormatException)
        { throw new BridgeException("CaptureMismatch", "Malformed retained diagnostic source evidence."); }
    }

    internal static JsonArray ResolveSignals(DiagnosticEvidence source, string reviewId, JsonObject? selected, JsonArray requests, HashSet<string> hypothesisIds)
    {
        var signals = new JsonArray();
        if (requests.Count > 64) throw new BridgeException("InvalidRequest", "At most 64 requested signals; this is a host budget, not a device limit.");
        if (requests.Count > 0 && selected is null) throw new BridgeException("InstanceResolutionIncomplete", "Resolve and explicitly select the instance before resolving signal storage.");
        var signalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in requests)
        {
            if (item is not JsonObject signal) throw new BridgeException("InvalidRequest", "Expected a signal request.");
            string id = D.Required(signal, "id", 64);
            if (!signalIds.Add(id)) throw new BridgeException("InvalidRequest", "Duplicate signal ID.");
            string reason = D.Required(signal, "reason", 2000), role = D.Required(signal, "role", 64);
            var links = D.Rows(signal["hypothesisIds"]).Select(n => n!.GetValue<string>()).ToArray();
            if (links.Length == 0 || links.Any(h => !hypothesisIds.Contains(h))) throw new BridgeException("InvalidRequest", "Each signal must reference known hypotheses.");
            string storage = D.Text(signal, "storagePath"); if (storage.Length == 0) storage = D.Text(selected, "dbPath");
            var memberPath = D.Path(signal["memberPath"]);
            if (!source.Nodes.TryGetValue(storage, out var storageNode) || D.Text(storageNode, "plcPath") != D.Text(selected, "plcPath"))
                throw new BridgeException("ObjectNotFound", "Signal storage must be an exact captured DB in the selected CPU.");
            bool tag = D.Text(storageNode, "kind") == "tag";
            var member = tag ? null : source.Member(storage, memberPath);
            string datatype = tag ? D.Text(storageNode["metadata"], "dataType") : (string?)member?.Attribute("Datatype") ?? "";
            if (signal["expectedDatatype"] is not null && D.Text(signal, "expectedDatatype") != datatype)
                throw new BridgeException("MeasurementLineageMismatch", "Reused hypothesis has a changed or unavailable signal type. Review a new case instead.");
            string? unavailable = tag ? (memberPath.Length == 1 && memberPath[0] == D.Text(storageNode, "name") ? null : "Tag selector must match its exact captured name.")
                : member is null ? "No exact exported DB member; TEMP, indirect, arrays and omitted/protected source are not resolved." : null;
            if (tag && (!ScalarTypes.Contains(datatype, StringComparer.OrdinalIgnoreCase) || storageNode["metadata"]?["isSafety"]?.GetValue<bool>() == true
                || storageNode["metadata"]?["externalAccessible"]?.GetValue<bool>() == false || storageNode["metadata"]?["externalVisible"]?.GetValue<bool>() == false))
                unavailable = "Tag type, safety classification or captured access settings are outside the supported observable subset.";
            if (member is not null)
            {
                if (member.AncestorsAndSelf().Any(e => e.Name.LocalName == "Section" && (string?)e.Attribute("Name") is "Temp" or "InOut"))
                    unavailable = "TEMP and FB IN_OUT storage cannot be assumed traceable; resolve actual caller storage separately.";
                if (!ScalarTypes.Contains(datatype, StringComparer.OrdinalIgnoreCase)) unavailable = "Type is outside the scalar planning subset; no array/structure/indirect expansion is inferred.";
                if (member.AncestorsAndSelf().Where(e => e.Name.LocalName == "Member").Any(e =>
                    (string?)e.Attribute("Accessibility") == "Private" || e.Elements().Where(a => a.Name.LocalName == "AttributeList").Descendants().Any(a =>
                        (string?)a.Attribute("Name") is "ExternalAccessible" or "ExternalVisible" && a.Value == "false")))
                    unavailable = "Captured access attributes restrict this member; backend/target access must be resolved.";
            }
            signals.Add(new JsonObject { ["id"] = id, ["role"] = role, ["reason"] = reason, ["hypothesisIds"] = D.Strings(links),
                ["storagePath"] = storage, ["memberPath"] = D.Strings(memberPath), ["datatype"] = datatype,
                ["symbolComponents"] = D.Strings(tag ? [DiagnosticEvidence.QualifiedName(storageNode)] : new[] { DiagnosticEvidence.QualifiedName(storageNode) }.Concat(memberPath)),
                ["namespace"] = storageNode["metadata"]?["namespace"]?.DeepClone(), ["scopePath"] = storageNode["scopePath"]?.DeepClone(),
                ["resolution"] = unavailable is null ? "capturedDeclaration" : "unavailable", ["unavailableReason"] = unavailable,
                ["traceability"] = unavailable is null ? "requiresTargetValidation" : "unavailable",
                ["evidence"] = tag ? new JsonObject { ["origin"] = "savedNativeTagMetadata", ["reviewId"] = reviewId, ["objectPath"] = storage,
                    ["nodeId"] = storageNode["nodeId"]?.DeepClone(), ["logicalAddress"] = storageNode["metadata"]?["logicalAddress"]?.DeepClone(), ["liveVerified"] = false }
                    : member is null ? null : source.Evidence(storage, member),
                ["onlineAddress"] = null, ["inclusionOrigin"] = D.Text(signal, "selectionOrigin") == "boundedDependencyEvidence" ? "boundedDependencyEvidence" : "callerProposedWithDeclarationEvidence",
                ["selectionEvidence"] = signal["selectionEvidence"]?.DeepClone() });
        }
        return signals;
    }

    internal static readonly string[] ScalarTypes = ["Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal", "Time", "LTime"];
}
