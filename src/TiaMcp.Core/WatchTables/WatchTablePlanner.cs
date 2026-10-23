using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.WatchTables;

/// <summary>Pure, bounded table intent from captured evidence. No native document is generated.</summary>
public static class WatchTablePlanner
{
    private static readonly HashSet<string> ScalarTypes = new(StringComparer.OrdinalIgnoreCase)
    { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal", "Time", "LTime", "Char", "WChar", "Date", "Time_Of_Day" };
    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal) { "interface", "callGraph", "instanceDb", "selectedNetwork", "ioMap" };
    private const int MaxCandidates = 10000;

    public static JsonObject Plan(WatchTablePlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EngineeringIdentityGuard.Validate(request.ExpectedSource);
        EngineeringIdentityGuard.Validate(request.CapturedSource);
        EngineeringIdentityGuard.EnsureFresh(request.ExpectedSource, request.CapturedSource);
        SameCapture(request, request.Instance.Storage);
        Path(request.Instance.MemberPath, true);
        Text(request.TableName, 128, "tableName");
        if (request.MaxRows is < 1 or > MaxCandidates || request.DuplicatePolicy is not ("preserve" or "collapseExact"))
            Fail("InvalidRequest", "Invalid row budget or duplicate policy.");
        if (request.Candidates.Count > MaxCandidates || request.ExternalStorage.Count > 1000 || request.AddressObservations.Count > MaxCandidates || request.TemplateBindings.Count > MaxCandidates)
            Fail("WatchTableLimitExceeded", "Planner input count exceeds host limits.");
        long inputBytes = 0;
        void Account(JsonNode? value) { if (value is null) return; inputBytes += Encoding.UTF8.GetByteCount(value.ToJsonString()); if (inputBytes > 16 * 1024 * 1024) Fail("WatchTableLimitExceeded", "Combined captured inputs exceed the 16 MiB host budget."); }
        Account(request.DiagnosticCase); Account(Node(request.Template));
        foreach(var binding in request.TemplateBindings) Account(Node(binding));
        foreach(var observation in request.AddressObservations) Account(Node(observation));
        var external = new Dictionary<string, WatchTableExternalStorage>(StringComparer.Ordinal);
        foreach (var allowance in request.ExternalStorage)
        {
            Account(Node(allowance));
            SameCapture(request, allowance.Storage); Text(allowance.Reason, 2000, "external storage reason");
            if (allowance.Storage.Object.ObjectPath == request.Instance.Storage.Object.ObjectPath || !external.TryAdd(allowance.Storage.Object.ObjectPath, allowance))
                Fail("InvalidRequest", "Duplicate or redundant external storage allowance.");
        }
        var candidates = new List<JsonObject>();
        var sourceGaps = new JsonArray();
        if (request.DiagnosticCase is not null) AddDiagnostic(request, external, candidates, sourceGaps);
        else if (request.DiagnosticCaseSha256 is not null) Fail("InvalidRequest", "Diagnostic hash has no case.");
        foreach (var c in request.Candidates)
        {
            Account(Node(c));
            if (!Sources.Contains(c.SourceKind)) Fail("InvalidRequest", "Unknown captured candidate source kind.");
            SameCapture(request, c.SourceObject); SameCapture(request, c.Storage);
            if (c.SourceKind is "interface" or "callGraph" or "selectedNetwork") EngineeringIdentityGuard.EnsureFresh(request.CapturedSource, c.SourceObject);
            if (c.SourceKind == "instanceDb") EngineeringIdentityGuard.EnsureFresh(request.Instance.Storage, c.SourceObject);
            if (c.SourceKind == "selectedNetwork") Text(c.NetworkId, 256, "networkId");
            ValidateStorage(request, external, c.Storage, c.MemberPath);
            Text(c.Id, 64, "candidate id"); Text(c.Role, 64, "role"); Text(c.Reason, 2000, "reason");
            if (c.Resolution is not ("capturedDeclaration" or "unavailable" or "ambiguous")) Fail("InvalidRequest", "Unknown candidate resolution.");
            if (c.StorageClass is not ("instanceMember" or "globalDbMember" or "plcTag" or "actualParameter" or "temp" or "inOutAlias" or "indirect" or "unknown"))
                Fail("InvalidRequest", "Unknown captured storage class.");
            Evidence(c.Evidence);
            if (S(c.Evidence, "observationId") != request.CapturedSource.Snapshot.ObservationId || S(c.Evidence, "sourceSha256") != request.CapturedSource.Snapshot.SourceSha256 || S(c.Evidence, "objectPath") != c.SourceObject.Object.ObjectPath)
                Fail("CaptureMismatch", "Normalized candidate selection evidence differs from captured source identity.");
            if (c.Resolution == "capturedDeclaration") Text(c.Datatype, 128, "datatype");
            if (c.Resolution == "capturedDeclaration" && (S(c.Evidence["declaration"], "storagePath") != c.Storage.Object.ObjectPath ||
                S(c.Evidence["declaration"], "datatype") != c.Datatype || !JsonNode.DeepEquals(c.Evidence["declaration"]?["memberPath"], Strings(c.MemberPath)) ||
                c.Storage.ContentSha256 is string hash && S(c.Evidence["declaration"], "contentSha256") != hash))
                Fail("CaptureMismatch", "Resolved normalized candidate needs matching captured declaration evidence.");
            Path(c.SymbolComponents ?? [], true);
            bool transient = c.StorageClass is "temp" or "inOutAlias" or "indirect" or "unknown";
            candidates.Add(new JsonObject { ["id"] = c.Id, ["sourceKind"] = c.SourceKind, ["sourceObject"] = Node(c.SourceObject),
                ["storage"] = Node(c.Storage), ["memberPath"] = Strings(c.MemberPath), ["datatype"] = c.Datatype,
                ["role"] = c.Role, ["reason"] = c.Reason, ["resolution"] = transient && c.Resolution == "capturedDeclaration" ? "unavailable" : c.Resolution,
                ["storageClass"] = c.StorageClass,
                ["unavailableReason"] = transient ? "Captured storage class has no established persistent exact declaration: " + c.StorageClass : c.UnavailableReason, ["symbolComponents"] = Strings(c.SymbolComponents ?? []),
                ["evidence"] = c.Evidence.DeepClone(), ["networkId"] = c.NetworkId, ["hypothesisIds"] = new JsonArray() });
        }
        if (candidates.Count > MaxCandidates) Fail("WatchTableLimitExceeded", "Combined candidate count exceeds host budget.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var storageTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (!ids.Add(S(c, "id"))) Fail("DuplicateCandidateId", "Candidate IDs must be unique across sources.");
            string key = StorageKey(c), datatype = S(c, "datatype");
            if (S(c, "resolution") == "capturedDeclaration")
            {
                if (storageTypes.TryGetValue(key, out string? prior) && !string.Equals(prior, datatype, StringComparison.OrdinalIgnoreCase))
                    Fail("ConflictingSignalType", "The same exact storage/member has conflicting captured types.");
                storageTypes[key] = datatype;
            }
        }
        var observations = ValidateObservations(request, candidates);
        var addressWarnings = AddressWarnings(observations.Values);
        var selected = ApplyTemplate(request, candidates, sourceGaps);
        var rows = new JsonArray(); var gaps = new JsonArray(); var duplicates = new JsonArray();
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        int omitted = 0;
        foreach (var c in selected)
        {
            string id = S(c, "id"), resolution = S(c, "resolution"), datatype = S(c, "datatype");
            if (resolution != "capturedDeclaration" || !ScalarTypes.Contains(datatype))
            {
                gaps.Add(Gap(c, resolution == "ambiguous" ? "AmbiguousSignal" : "UnresolvedSignal", S(c, "unavailableReason") is { Length: > 0 } why ? why : "Captured declaration/type is not a supported scalar."));
                continue;
            }
            var addressKey = observations.TryGetValue(id, out var observed) ? (JsonObject)observed.DeepClone() : null;
            if (addressKey is not null) { addressKey.Remove("candidateId"); addressKey.Remove("observedAtUtc"); addressKey.Remove("evidence"); }
            string key = StorageKey(c) + "|" + datatype.ToUpperInvariant() + "|" + CanonicalJson.Serialize(new JsonObject { ["symbols"] = c["symbolComponents"]?.DeepClone(), ["address"] = addressKey });
            if (first.TryGetValue(key, out string? original))
            {
                duplicates.Add(new JsonObject { ["candidateId"] = id, ["firstCandidateId"] = original, ["policy"] = request.DuplicatePolicy });
                if (request.DuplicatePolicy == "collapseExact")
                {
                    var row = rows.OfType<JsonObject>().Single(r => S(r, "candidateId") == original);
                    row["selectionReasons"]!.AsArray().Add(new JsonObject { ["candidateId"] = id, ["reason"] = c["reason"]!.DeepClone(), ["evidence"] = c["evidence"]!.DeepClone(), ["candidate"] = c.DeepClone() });
                    continue;
                }
            }
            if (rows.Count == request.MaxRows) { omitted++; gaps.Add(Gap(c, "RowBudgetExceeded", "Explicit planner row budget exhausted; candidate is retained.")); continue; }
            first.TryAdd(key, id);
            rows.Add(new JsonObject { ["ordinal"] = rows.Count, ["candidateId"] = id, ["candidate"] = c.DeepClone(),
                ["displayFormat"] = "Undef", ["modifyIntention"] = false, ["modifyValue"] = null,
                ["onlineAddress"] = null, ["suppliedAddressObservation"] = observations.TryGetValue(id, out var observation) ? observation.DeepClone() : null,
                ["selectionReasons"] = new JsonArray(new JsonObject { ["candidateId"] = id, ["reason"] = c["reason"]!.DeepClone(), ["evidence"] = c["evidence"]!.DeepClone() }) });
        }
        var plan = new JsonObject { ["schemaVersion"] = "watch-table-plan.v1", ["tableName"] = request.TableName,
            ["source"] = Node(request.CapturedSource), ["instance"] = Node(request.Instance),
            ["diagnosticCaseSha256"] = request.DiagnosticCaseSha256,
            ["diagnosticSourceRevisionSha256"] = request.DiagnosticCase?["sourceRevision"] is JsonNode revision ? CanonicalJson.Sha256(revision) : null,
            ["template"] = Node(request.Template),
            ["templateBindings"] = Node(request.TemplateBindings), ["externalStorage"] = Node(request.ExternalStorage),
            ["candidates"] = new JsonArray(candidates.Select(c => (JsonNode?)c.DeepClone()).ToArray()),
            ["rows"] = rows, ["unresolvedSuggestions"] = gaps, ["sourceGaps"] = sourceGaps, ["duplicates"] = duplicates,
            ["addressObservations"] = new JsonArray(observations.OrderBy(k => k.Key, StringComparer.Ordinal).Select(p => (JsonNode?)p.Value.DeepClone()).ToArray()),
            ["addressWarnings"] = addressWarnings,
            ["candidateCount"] = candidates.Count, ["omittedByBudget"] = omitted,
            ["sourceTruncated"] = SourceTruncated(request.DiagnosticCase), ["truncated"] = omitted > 0 || SourceTruncated(request.DiagnosticCase),
            ["maxRows"] = request.MaxRows, ["duplicatePolicy"] = request.DuplicatePolicy,
            ["nativeExecutable"] = false, ["nativeDocumentGenerated"] = false, ["qualification"] = "offlinePlanningOnly",
            ["pendingNativeGates"] = new JsonArray("currentOwnerAdmission", "genuineNativeEntryGrammar", "exactTargetSymbolAndProfileValidation", "independentNativeReadback", "preservationCompileApplicability", "saveReopenRecovery") };
        Bound(plan, 16 * 1024 * 1024);
        plan["planSha256"] = CanonicalJson.Sha256(plan);
        return plan;
    }

    private static void AddDiagnostic(WatchTablePlanningRequest r, Dictionary<string, WatchTableExternalStorage> external, List<JsonObject> output, JsonArray gaps)
    {
        JsonObject d = r.DiagnosticCase!; Bound(d, 4 * 1024 * 1024);
        if (r.DiagnosticCaseSha256 != CanonicalJson.Sha256(d)) Fail("DiagnosticCaseMismatch", "Diagnostic case hash does not match supplied immutable content.");
        if (S(d, "schemaVersion") != "diagnostic-case.v1" || S(d, "qualification") != "offlinePlanningOnly" || d["nativeExecutable"]?.GetValue<bool>() != false)
            Fail("InvalidRequest", "Expected an offline DiagnosticPlanner case.");
        if (S(d, "reviewId") != r.CapturedSource.Snapshot.ObservationId || S(d["sourceRevision"], "reviewId") != S(d, "reviewId") || S(d, "blockPath") != r.CapturedSource.Object.ObjectPath)
            Fail("CaptureMismatch", "Diagnostic case source review/block differs from captured reference.");
        string project = S(d["project"], "path"); if (project.Length == 0) project = S(d["project"], "projectPath");
        if (project != r.CapturedSource.Object.ProjectPath) Fail("ProjectMismatch", "Diagnostic case project path differs from exact captured project.");
        JsonNode? instance = d["selectedInstance"];
        if (S(instance, "dbPath") != r.Instance.Storage.Object.ObjectPath || S(instance, "blockPath") != r.CapturedSource.Object.ObjectPath ||
            S(instance, "plcPath") != r.CapturedSource.Object.PlcPath || S(instance, "resolution") != "capturedStorageOnly" ||
            !JsonNode.DeepEquals(instance?["memberPath"], Strings(r.Instance.MemberPath)))
            Fail("InstanceResolutionIncomplete", "Diagnostic selected instance differs or is unresolved.");
        if (d["signals"] is not JsonArray signals || signals.Count > 64) Fail("InvalidRequest", "Diagnostic signals must be a bounded manifest of at most 64 rows.");
        var documents = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (d["sourceRevision"]?["documents"] is not JsonArray docs || docs.Count > 50000) Fail("InvalidRequest", "Source revision document inventory is required and bounded.");
        foreach (JsonNode? n in d["sourceRevision"]!["documents"]!.AsArray())
            if (n is not JsonObject doc || !documents.TryAdd(S(doc, "objectPath"), doc)) Fail("CaptureMismatch", "Duplicate/malformed captured document.");
        foreach (JsonNode? n in d["signals"]!.AsArray())
        {
            if (n is not JsonObject c) { Fail("InvalidRequest", "Malformed diagnostic signal."); continue; }
            string id = S(c, "id"), storage = S(c, "storagePath"); Text(id, 64, "signal id");
            EngineeringObjectReference reference = storage == r.Instance.Storage.Object.ObjectPath ? r.Instance.Storage :
                external.TryGetValue(storage, out var allowed) ? allowed.Storage : throw new BridgeException("ExternalStorageNotSelected", "A signal references external storage without exact explicit allowance.");
            string[] memberPath = ReadPath(c["memberPath"]); ValidateStorage(r, external, reference, memberPath);
            if (c["onlineAddress"] is not null) Fail("InvalidRequest", "Diagnostic onlineAddress must remain null.");
            Text(S(c, "reason"), 2000, "reason"); Text(S(c, "role"), 64, "role");
            string resolution = S(c, "resolution"); if (resolution is not ("capturedDeclaration" or "unavailable")) Fail("InvalidRequest", "Unknown diagnostic resolution.");
            if (resolution == "capturedDeclaration")
            {
                if (c["evidence"] is not JsonObject evidence) Fail("CaptureMismatch", "Resolved signal lacks captured evidence.");
                JsonObject e = c["evidence"]!.AsObject(); Evidence(e);
                if (S(e, "objectPath") != storage) Fail("CaptureMismatch", "Signal evidence names different storage.");
                if (S(e, "origin") == "savedNativeXml")
                {
                    if (!documents.TryGetValue(storage, out var doc) || S(doc, "status") != "exported" || S(doc, "utf8Sha256") != S(e, "utf8Sha256") ||
                        !Digest(S(e, "utf8Sha256")) || reference.ContentSha256 is string content && content != S(e, "utf8Sha256"))
                        Fail("CaptureMismatch", "Signal declaration and captured document hashes differ.");
                }
                else if (S(e, "origin") != "savedNativeTagMetadata" || S(e, "reviewId") != S(d, "reviewId") || S(e, "nodeId").Length == 0)
                    Fail("CaptureMismatch", "Unsupported or mixed captured signal evidence.");
            }
            var row = (JsonObject)c.DeepClone(); row["sourceKind"] = "diagnostic"; row["storage"] = Node(reference); row["sourceObject"] = Node(r.CapturedSource);
            if (row["symbolComponents"] is not null) Path(ReadPath(row["symbolComponents"]), false);
            output.Add(row);
        }
        foreach (string key in new[] { "coverageGaps", "questions", "automaticSelection", "instanceTraversal", "coverage" })
            if (d[key] is not null) gaps.Add(new JsonObject { ["origin"] = key, ["evidence"] = d[key]!.DeepClone() });
    }

    private static Dictionary<string, JsonObject> ValidateObservations(WatchTablePlanningRequest r, List<JsonObject> candidates)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var byId = candidates.ToDictionary(c => S(c, "id"), StringComparer.Ordinal);
        foreach (var o in r.AddressObservations)
        {
            SameCapture(r, o.Storage); Path(o.MemberPath, false); Evidence(o.Evidence);
            if (!byId.TryGetValue(o.CandidateId, out var c) || S(c, "resolution") != "capturedDeclaration" || !JsonNode.DeepEquals(c["storage"], Node(o.Storage)) ||
                !JsonNode.DeepEquals(c["memberPath"], Strings(o.MemberPath)) || S(c, "datatype") != o.Datatype)
                Fail("AddressObservationMismatch", "Address observation does not match one exact resolved signal and content reference.");
            if (!DateTimeOffset.TryParse(o.ObservedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) || !o.ObservedAtUtc.EndsWith('Z'))
                Fail("InvalidRequest", "Address observation requires an explicit UTC timestamp.");
            if (o.Kind == "symbolic")
            { Path(o.SymbolComponents ?? [], false); if (o.StartBit is not null || o.Area is not null || o.WidthBits is not null || o.DbNumber is not null) Fail("InvalidRequest", "Symbolic observation contains absolute address fields."); }
            else if (o.Kind == "absolute")
            {
                if (o.OptimizedDb != false || o.Area is not ("I" or "Q" or "M" or "DB") || o.StartBit is null or < 0 or > int.MaxValue || o.WidthBits is null or < 1 or > 64 ||
                    o.StartBit + o.WidthBits > int.MaxValue || (o.Area == "DB" ? o.DbNumber is null or < 1 or > 65535 : o.DbNumber is not null) || o.SymbolComponents is not null)
                    Fail("InvalidAddressObservation", "Absolute range needs nonoptimized evidence and explicit bounded area/width/DB identity.");
                int width = Width(o.Datatype); if (width == 0 || o.WidthBits != width || (width > 1 && o.StartBit % 8 != 0))
                    Fail("InvalidAddressObservation", "Absolute width/alignment does not match the supported scalar type.");
            }
            else Fail("InvalidRequest", "Observation kind must be symbolic or absolute.");
            var node = Node(o)!.AsObject(); node["verification"] = "suppliedOfflineObservationNotNativeQualified";
            if (!result.TryAdd(o.CandidateId, node)) Fail("InvalidRequest", "Duplicate address observation for candidate.");
        }
        return result;
    }

    private static List<JsonObject> ApplyTemplate(WatchTablePlanningRequest r, List<JsonObject> candidates, JsonArray gaps)
    {
        if (r.Template is null) { if (r.TemplateBindings.Count > 0) Fail("InvalidRequest", "Template bindings supplied without a template."); return candidates; }
        Text(r.Template.Id, 128, "template id"); Text(r.Template.Version, 64, "template version");
        if (r.Template.Slots.Count is < 1 or > 10000 || r.TemplateBindings.Count != r.Template.Slots.Count) Fail("TemplateMappingMissing", "Every template slot needs one explicit mapping.");
        var slots = new HashSet<string>(StringComparer.Ordinal); var bindings = new Dictionary<string, WatchTableTemplateBinding>(StringComparer.Ordinal);
        foreach (var b in r.TemplateBindings) { Text(b.Reason, 2000, "template binding reason"); if (!bindings.TryAdd(b.SlotId, b)) Fail("InvalidRequest", "Duplicate template slot binding."); }
        var byId = candidates.ToDictionary(c => S(c, "id"), StringComparer.Ordinal); var used = new HashSet<string>(StringComparer.Ordinal); var result = new List<JsonObject>();
        foreach (var slot in r.Template.Slots)
        {
            Text(slot.Id, 64, "slot id"); Text(slot.Role, 64, "slot role"); Text(slot.Datatype, 128, "slot datatype");
            if (!slots.Add(slot.Id) || !bindings.TryGetValue(slot.Id, out var binding) || !byId.TryGetValue(binding.CandidateId, out var candidate) || !used.Add(binding.CandidateId))
                Fail("TemplateMappingMissing", "Template mapping is duplicate, absent or unresolved.");
            var b = bindings[slot.Id]; var c = byId[b.CandidateId];
            if (S(c, "datatype") != slot.Datatype || S(c, "role") != slot.Role) Fail("TemplateTypeMismatch", "Mapped role/type differs from reusable template contract.");
            result.Add(c);
        }
        foreach (var c in candidates.Where(c => !used.Contains(S(c, "id")))) gaps.Add(Gap(c, "NotMappedByTemplate", "Candidate retained but not selected by explicit template mapping."));
        return result;
    }

    private static bool SourceTruncated(JsonObject? diagnostic) => diagnostic?["automaticSelection"]?["truncated"]?.GetValue<bool>() == true || diagnostic?["instanceTraversal"]?["truncated"]?.GetValue<bool>() == true || diagnostic?["rankingTruncated"]?.GetValue<bool>() == true;
    private static JsonObject AddressWarnings(IEnumerable<JsonObject> observations)
    {
        var warnings = new JsonArray();
        foreach (var area in observations.Where(o => S(o,"kind") == "absolute").GroupBy(o => S(o,"area") + ":" + o["dbNumber"]?.ToJsonString(), StringComparer.Ordinal))
        {
            var active = new List<JsonObject>();
            foreach(var row in area.OrderBy(o => o["startBit"]!.GetValue<long>()).ThenBy(o => S(o,"candidateId"),StringComparer.Ordinal))
            {
                long start = row["startBit"]!.GetValue<long>();
                active.RemoveAll(o => o["startBit"]!.GetValue<long>() + o["widthBits"]!.GetValue<int>() <= start);
                foreach(var peer in active)
                {
                    if(warnings.Count == 1000) return new JsonObject { ["items"] = warnings, ["truncated"] = true };
                    warnings.Add(new JsonObject { ["code"] = "OverlappingSuppliedRanges", ["leftCandidateId"] = S(peer,"candidateId"), ["rightCandidateId"] = S(row,"candidateId"), ["area"] = area.Key });
                }
                active.Add(row);
            }
        }
        return new JsonObject { ["items"] = warnings, ["truncated"] = false };
    }

    private static void SameCapture(WatchTablePlanningRequest r, EngineeringObjectReference reference)
    {
        EngineeringIdentityGuard.Validate(reference); EngineeringIdentityGuard.EnsureSameSnapshot(r.CapturedSource, reference);
        if (reference.Object.PlcPath != r.CapturedSource.Object.PlcPath || string.IsNullOrEmpty(reference.Object.PlcPath)) Fail("PlcMismatch", "All storage/source references need the exact selected PLC.");
    }
    private static void ValidateStorage(WatchTablePlanningRequest r, Dictionary<string, WatchTableExternalStorage> external, EngineeringObjectReference reference, IReadOnlyList<string> path)
    {
        Path(path, false); SameCapture(r, reference);
        if (reference.Object.ObjectPath == r.Instance.Storage.Object.ObjectPath)
        {
            EngineeringIdentityGuard.EnsureFresh(r.Instance.Storage, reference);
            if (!path.Take(r.Instance.MemberPath.Count).SequenceEqual(r.Instance.MemberPath, StringComparer.Ordinal))
                Fail("InstanceMismatch", "Signal member belongs to another nested instance.");
        }
        else if (!external.TryGetValue(reference.Object.ObjectPath, out var selected)) Fail("ExternalStorageNotSelected", "Select exact external storage and reason explicitly.");
        else EngineeringIdentityGuard.EnsureFresh(external[reference.Object.ObjectPath].Storage, reference);
    }
    private static string StorageKey(JsonObject c) => CanonicalJson.Serialize(new JsonObject { ["storage"] = c["storage"]!.DeepClone(), ["memberPath"] = c["memberPath"]!.DeepClone() });
    private static JsonObject Gap(JsonObject c, string code, string reason) => new() { ["candidateId"] = S(c, "id"), ["code"] = code, ["reason"] = reason, ["candidate"] = c.DeepClone() };
    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, BridgeProtocol.Json);
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    private static string S(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";
    private static string[] ReadPath(JsonNode? node) => node is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").ToArray() : throw new BridgeException("InvalidRequest", "Expected explicit component array.");
    private static void Path(IReadOnlyList<string> values, bool allowEmpty)
    { if (values.Count > 32 || !allowEmpty && values.Count == 0) Fail("InvalidRequest", "Component count outside bounds."); foreach (string value in values) Text(value, 256, "component"); }
    private static void Text(string? value, int max, string field) { if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) Fail("InvalidRequest", "Invalid " + field + "."); }
    private static bool Digest(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Evidence(JsonObject e) { if (e.Count == 0) Fail("CaptureMismatch", "Captured evidence is required."); Bound(e, 65536); }
    private static void Bound(JsonNode n, int bytes) { if (Encoding.UTF8.GetByteCount(n.ToJsonString()) > bytes) Fail("WatchTableLimitExceeded", "Captured payload exceeds byte budget."); }
    private static int Width(string type) => type.ToUpperInvariant() switch { "BOOL" => 1, "BYTE" or "SINT" or "USINT" or "CHAR" => 8, "WORD" or "INT" or "UINT" or "WCHAR" or "DATE" => 16, "DWORD" or "DINT" or "UDINT" or "REAL" or "TIME" or "TIME_OF_DAY" => 32, "LWORD" or "LINT" or "ULINT" or "LREAL" or "LTIME" => 64, _ => 0 };
    private static void Fail(string code, string message) => throw new BridgeException(code, message);
}
