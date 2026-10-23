using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.HmiComponents;

/// <summary>Explicit component/interface mappings and conservative supplied-content preservation review.</summary>
public static class HmiComponentReview
{
    public const int InputLimit = 4 * 1024 * 1024, FileLimit = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Request must contain 1 byte..4 MiB.");
        string text = Utf8.GetString(bytes);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in value.EnumerateObject()) { _ = Utf8.GetByteCount(p.Name); if (!names.Add(p.Name)) throw Error("Duplicate JSON field."); Unique(p.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var row in value.EnumerateArray()) Unique(row);
            else if (value.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(value.GetString()!);
        }
        Unique(doc.RootElement);
        return Object(JsonNode.Parse(text));
    }

    internal static JsonObject[] Files(JsonObject request)
    {
        var files = Array(request, "files", 32).Select(Object).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        foreach (var file in files)
        {
            Fields(file, "id fileName uploadRef sha256 bytes");
            if (!ids.Add(Text(file, "id", 128))) throw Error("Duplicate file ID.");
            string name = Text(file, "fileName", 256);
            if (PackageStorage.Relative(name) != name || name.Contains('/')) throw Error("File names must be single display names, not paths.");
            string reference = Text(file, "uploadRef", 39);
            if (reference.Length != 39 || !reference.StartsWith("upload:", StringComparison.Ordinal) || reference.Skip(7).Any(c => !"0123456789abcdef".Contains(c))) throw Error("Expected committed file upload reference.");
            EngineeringIdentityGuard.Sha256(Text(file, "sha256", 64), "file.sha256");
            if (file["bytes"] is not JsonValue v || !v.TryGetValue<int>(out int size) || size is < 1 or > FileLimit) throw Error("File size must be 1 byte..16 MiB.");
            total += size; if (total > 64L * 1024 * 1024) throw Error("Files exceed 64 MiB total, including repeated roles.");
        }
        return files;
    }

    public static JsonObject Build(JsonObject request, Func<string, JsonObject> readReview)
    {
        Fields(request, "schemaVersion operation reason beforeComponent afterComponent componentMatches destination outputs bindings files");
        if (Text(request, "schemaVersion") != "hmi-component-review-request.v1") throw Error("Unsupported request schema.");
        string operation = Text(request, "operation", 16); Text(request, "reason", 2000);
        if (operation is not ("install" or "update") || (request["beforeComponent"] is null) != (operation == "install")) throw Error("Install requires no prior component; update requires an exact prior component.");
        var files = Files(request).ToDictionary(f => Text(f, "id"), StringComparer.Ordinal);
        var after = Component(Object(request["afterComponent"]));
        var before = request["beforeComponent"] is null ? null : Component(Object(request["beforeComponent"]));
        var componentRows = new JsonArray(); var matchedBefore = new HashSet<string>(StringComparer.Ordinal); var matchedAfter = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Array(request, "componentMatches", 64))
        {
            var row = Object(value); Fields(row, "beforeObjectPath afterObjectPath reason");
            string oldPath = Text(row, "beforeObjectPath"), newPath = Text(row, "afterObjectPath"); Text(row, "reason", 2000);
            if (before is null || !before.Paths.Contains(oldPath) || !after.Paths.Contains(newPath) || !matchedBefore.Add(oldPath) || !matchedAfter.Add(newPath))
                throw Error("Component matches require unique explicitly selected before/after objects; installation has no prior match.");
            var oldSignatures = Signatures(before, oldPath); var newSignatures = Signatures(after, newPath);
            bool? Changed(string key) => oldSignatures is null || newSignatures is null ? null : (string?)oldSignatures[key] != (string?)newSignatures[key];
            componentRows.Add(new JsonObject { ["mapping"] = row.DeepClone(), ["status"] = oldSignatures is null || newSignatures is null ? "contentUnknown" : "explicitlyCompared",
                ["beforeSignatures"] = oldSignatures, ["afterSignatures"] = newSignatures, ["interfaceChanged"] = Changed("interface"), ["logicChanged"] = Changed("logic"),
                ["documentChanged"] = Changed("document"), ["nativeCompatibilityVerified"] = false });
        }
        foreach (string path in before?.Paths ?? []) if (!matchedBefore.Contains(path)) componentRows.Add(new JsonObject { ["status"] = "unmatchedBefore", ["objectPath"] = path, ["nativeCompatibilityVerified"] = false });
        foreach (string path in after.Paths) if (!matchedAfter.Contains(path)) componentRows.Add(new JsonObject { ["status"] = "unmatchedAfter", ["objectPath"] = path, ["nativeCompatibilityVerified"] = false });
        var destination = Object(request["destination"]); Fields(destination, "reviewId hmiPath family");
        string destinationId = Text(destination, "reviewId", 64); EngineeringIdentityGuard.Sha256(destinationId, "destination.reviewId");
        string hmiPath = Text(destination, "hmiPath"), family = Text(destination, "family", 16);
        if (family is not ("classic" or "unified")) throw Error("Select Classic or Unified explicitly.");
        var targetReview = readReview(destinationId); var target = new DiagnosticEvidence(targetReview);
        var byId = target.Nodes.Values.ToDictionary(n => Text(n, "nodeId"), StringComparer.Ordinal);
        if (!target.Nodes.TryGetValue(hmiPath, out var hmi) || (string?)hmi["kind"] != "hmi" || (string?)hmi["origin"] == "virtual" || (string?)hmi["metadata"]?["family"] != family)
            throw Error("Destination must identify the exact captured native HMI software and family.");
        var outputRows = new JsonArray(); var bindingRows = new JsonArray();
        var outputs = new Dictionary<string, JsonObject>(StringComparer.Ordinal); var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Array(request, "outputs", 256))
        {
            var row = Object(value); Fields(row, "id objectPath parentPath name kind action ownership baseline current proposed baselineComplete currentComplete proposedComplete reason");
            string id = Text(row, "id", 128), parent = Text(row, "parentPath"), name = Text(row, "name", 256), kind = Text(row, "kind", 64);
            string action = Text(row, "action", 16), ownership = Text(row, "ownership", 16); Text(row, "reason", 2000);
            string? objectPath = NullableText(row, "objectPath");
            if (!outputs.TryAdd(id, row)) throw Error("Duplicate output ID.");
            if (kind is not ("hmiTag" or "hmiScreen" or "hmiScreenItem" or "hmiScript" or "hmiResource")) throw Error("Unsupported output kind; do not infer native object support.");
            if (action is not ("create" or "update" or "remove" or "retain") || ownership is not ("generated" or "manual" or "unknown")) throw Error("Unknown action or ownership declaration.");
            if ((action == "create") != (objectPath is null)) throw Error("Creation has no native object path; existing-object actions require an exact captured path.");
            if (!selected.Add(objectPath ?? parent + "\0" + name)) throw Error("An output identity is selected more than once.");
            var conflicts = new JsonArray(); var gaps = new JsonArray();
            JsonObject? node = null;
            if (!target.Nodes.TryGetValue(parent, out var parentNode) || !Within(parentNode)) gaps.Add("Parent HMI ownership is not established by captured ancestry.");
            if (objectPath is not null)
            {
                if (!target.Nodes.TryGetValue(objectPath, out node) || !Within(node)) gaps.Add("Existing output is not captured under the selected HMI software.");
                else
                {
                    if ((string?)node["kind"] != kind || (string?)node["name"] != name || (string?)node["parentId"] != (string?)parentNode?["nodeId"])
                        conflicts.Add("Output kind, name or direct parent contradicts the exact captured object.");
                    if ((string?)node["metadata"]?["hmiIdentity"]?["family"] != family || (string?)node["metadata"]?["hmiIdentity"]?["hmiPath"] != hmiPath)
                        gaps.Add("Captured output lacks a matching HMI family/software identity.");
                }
            }
            else if (parentNode is not null && target.Nodes.Values.Any(n => (string?)n["parentId"] == (string?)parentNode["nodeId"] && string.Equals((string?)n["name"], name, StringComparison.OrdinalIgnoreCase)))
                conflicts.Add("A captured sibling already has this name or a case variant; creation requires collision review.");
            if (action == "create") gaps.Add("Captured inventory does not prove live name availability or native creation support; a new native identity must be observed after admitted creation.");
            var baseline = Content(row, "baseline"); var current = Content(row, "current"); var proposed = Content(row, "proposed");
            bool baselineComplete = Boolean(row, "baselineComplete"), currentComplete = Boolean(row, "currentComplete"), proposedComplete = Boolean(row, "proposedComplete");
            if (operation == "install" && action is "update" or "remove") conflicts.Add("Initial installation cannot silently update or remove an existing output.");
            if (action != "retain" && ownership != "generated") conflicts.Add("Manual or unknown ownership requires explicit human review; no automatic overwrite or removal is justified.");
            if (action is "update" or "remove")
            {
                if (!baselineComplete || !currentComplete || baseline.Count == 0 || current.Count == 0) gaps.Add("Complete declared baseline and current file sets are required to assess content drift.");
                else if (!JsonNode.DeepEquals(baseline, current)) conflicts.Add("Current supplied bytes differ from the generated baseline; possible manual edits or export variation require review.");
            }
            if (action is "create" or "update" && (!proposedComplete || proposed.Count == 0)) gaps.Add("A complete declared proposed file set has not been supplied.");
            if (action is "retain" or "remove" && proposed.Count != 0) conflicts.Add("Retain/remove must not silently publish proposed replacement bytes.");
            outputRows.Add(new JsonObject { ["id"] = id, ["selection"] = row.DeepClone(), ["capturedObject"] = node?.DeepClone(),
                ["baselineFiles"] = baseline, ["currentFiles"] = current, ["proposedFiles"] = proposed,
                ["conflicts"] = conflicts, ["gaps"] = gaps, ["preservationVerified"] = false, ["nativeActionAvailable"] = false,
                ["ownershipVerified"] = false, ["contentAssociationVerified"] = false, ["contentCompletenessVerified"] = false });
        }
        foreach (var output in outputRows.OfType<JsonObject>().Where(o => (string?)o["selection"]?["action"] == "create"))
        {
            var selectedOutput = output["selection"]!;
            if (outputs.Values.Any(other => (string?)other["id"] != (string?)output["id"] && (string?)other["action"] == "create" &&
                (string?)other["parentPath"] == (string?)selectedOutput["parentPath"] && string.Equals((string?)other["name"], (string?)selectedOutput["name"], StringComparison.OrdinalIgnoreCase)))
                output["conflicts"]!.AsArray().Add("Proposed outputs collide by parent and name or case variant.");
        }
        var bindingIds = new HashSet<string>(StringComparer.Ordinal); var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Array(request, "bindings", 1024))
        {
            var row = Object(value); Fields(row, "id outputId property beforeMember afterMember declaredHmiType reason");
            string id = Text(row, "id", 128), outputId = Text(row, "outputId", 128), property = Text(row, "property", 256); Text(row, "reason", 2000);
            if (!bindingIds.Add(id) || !properties.Add(outputId + "\0" + property)) throw Error("Duplicate binding ID or output property mapping.");
            if (!outputs.TryGetValue(outputId, out var output)) throw Error("Binding refers to an unknown output ID.");
            var oldMember = Member(row["beforeMember"]); var newMember = Member(row["afterMember"]);
            string[]? oldPath = oldMember?.Path, newPath = newMember?.Path;
            if (oldPath is null && newPath is null || before is null && oldPath is not null) throw Error("Binding requires a valid before/after component member selection.");
            var previous = oldPath is null ? null : Declaration(before!, oldMember!);
            var next = newPath is null ? null : Declaration(after, newMember!);
            string? declared = NullableText(row, "declaredHmiType", 256);
            var conflicts = new JsonArray(); var gaps = new JsonArray();
            if (previous is not null && (string?)previous["status"] != "capturedDeclaration") gaps.Add("Prior PLC member declaration is unavailable or ambiguous.");
            if (next is not null && (string?)next["status"] != "capturedDeclaration") gaps.Add("Proposed PLC member declaration is unavailable or ambiguous.");
            if (newPath is null && (string?)output["action"] != "remove") conflicts.Add("Removed PLC member still has a retained or proposed HMI output mapping.");
            if (newPath is not null && (string?)output["action"] == "remove") conflicts.Add("A removed HMI output cannot receive a proposed binding.");
            string? oldType = (string?)previous?["dataType"], newType = (string?)next?["dataType"];
            if (oldType is not null && newType is not null && oldType != newType) conflicts.Add("PLC declaration type text changed; native binding compatibility needs review.");
            if (newPath is not null && declared is null) gaps.Add("HMI property/port type was not supplied; no type compatibility is inferred.");
            if (newType is not null && declared is not null && newType != declared) conflicts.Add("Declared PLC and HMI type text differs; aliases and conversions are not inferred.");
            string? path = (string?)output["objectPath"];
            var capturedProperties = path is not null && target.Nodes.TryGetValue(path, out var outputNode) ? outputNode["metadata"]?["properties"] as JsonObject : null;
            if (capturedProperties is null || !capturedProperties.ContainsKey(property)) gaps.Add("Destination property/faceplate port is not established by captured metadata.");
            bindingRows.Add(new JsonObject { ["id"] = id, ["selection"] = row.DeepClone(), ["beforeDeclaration"] = previous, ["afterDeclaration"] = next,
                ["change"] = oldPath is null ? "added" : newPath is null ? "removed" : oldType != newType ? "typeTextChanged" : (oldMember!.ObjectPath != newMember!.ObjectPath || !oldPath.SequenceEqual(newPath)) ? "explicitMemberMapping" : "sameSelectedMember",
                ["capturedProperty"] = capturedProperties?[property]?.DeepClone(), ["conflicts"] = conflicts, ["gaps"] = gaps,
                ["nativeCompatibilityVerified"] = false, ["runtimeStorageVerified"] = false });
        }
        int Count(string field) => outputRows.Concat(bindingRows).Sum(r => r![field]!.AsArray().Count);
        return new JsonObject { ["schemaVersion"] = "hmi-component-review.v1", ["operation"] = operation, ["reason"] = request["reason"]!.DeepClone(),
            ["beforeComponent"] = before?.Summary, ["afterComponent"] = after.Summary, ["destination"] = destination.DeepClone(),
            ["destinationProject"] = targetReview["model"]?["project"]?.DeepClone(), ["components"] = componentRows, ["outputs"] = outputRows, ["bindings"] = bindingRows,
            ["summary"] = new JsonObject { ["outputCount"] = outputRows.Count, ["bindingCount"] = bindingRows.Count, ["conflictCount"] = Count("conflicts"), ["gapCount"] = Count("gaps"),
                ["componentComparisonCount"] = componentRows.Count, ["changedInterfaceCount"] = componentRows.Count(c => (bool?)c!["interfaceChanged"] == true),
                ["unresolvedComponentCount"] = componentRows.Count(c => (string?)c!["status"] != "explicitlyCompared") },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["wholeProjectPreservationVerified"] = false,
            ["componentMemberCoverageComplete"] = false, ["consumerCoverageComplete"] = false,
            ["limits"] = "Explicit supplied mappings and file sets only. Equal bytes/type text do not establish native compatibility, complete HMI content, genuine library GUID/version identity, runtime storage or safe regeneration. New native identities, live freshness, full dependencies, compile/reopen/recovery, manual ownership and visuals require independent native qualification. No scripts or generated content are executed; no object is created, changed or removed." };

        ComponentEvidence Component(JsonObject selector)
        {
            Fields(selector, "reviewId objectPaths"); string id = Text(selector, "reviewId", 64); EngineeringIdentityGuard.Sha256(id, "component.reviewId");
            var paths = Array(selector, "objectPaths", 64); if (paths.Count == 0) throw Error("Select at least one exact component block/type.");
            var selectedPaths = new HashSet<string>(StringComparer.Ordinal); var objects = new JsonArray();
            var review = readReview(id); var evidence = new DiagnosticEvidence(review);
            foreach (var value in paths)
            {
                string path = Text(new JsonObject { ["path"] = value?.DeepClone() }, "path");
                if (!selectedPaths.Add(path) || !evidence.Nodes.TryGetValue(path, out var node) || (string?)node["kind"] is not ("block" or "type") || (string?)node["origin"] == "virtual")
                    throw Error("Component paths must select unique exact captured PLC blocks/types; no name or library-version substitution.");
                objects.Add(new JsonObject { ["capturedObject"] = node.DeepClone(), ["document"] = evidence.Documents.TryGetValue(path, out var document)
                    ? new JsonObject { ["status"] = document["status"]?.DeepClone(), ["utf8Sha256"] = document["utf8Sha256"]?.DeepClone() } : null });
            }
            return new(evidence, selectedPaths, new JsonObject { ["selection"] = selector.DeepClone(), ["objects"] = objects,
                ["project"] = review["model"]?["project"]?.DeepClone(), ["libraryIdentityVerified"] = false, ["runtimeInstanceVerified"] = false, ["dependencyClosureVerified"] = false });
        }
        bool Within(JsonObject node)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(Text(node, "nodeId")))
            {
                if ((string?)node["origin"] == "virtual") return false;
                if ((string?)node["objectPath"] == hmiPath) return true;
                if ((string?)node["parentId"] is not string parent || !byId.TryGetValue(parent, out node!)) return false;
            }
            return false;
        }
        JsonArray Content(JsonObject output, string phase)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var rows = new List<JsonObject>();
            foreach (var value in Array(output, phase, 32))
            {
                if (value is not JsonValue v || !v.TryGetValue<string>(out string? id) || id is null || !files.TryGetValue(id, out var file)) throw Error("Content set refers to an unknown file ID.");
                if (!names.Add(Text(file, "fileName"))) throw Error("Content sets need unique filenames, including case variants.");
                rows.Add(new JsonObject { ["fileName"] = file["fileName"]!.DeepClone(), ["sha256"] = file["sha256"]!.DeepClone(), ["bytes"] = file["bytes"]!.DeepClone() });
            }
            return new JsonArray(rows.OrderBy(r => Text(r, "fileName"), StringComparer.Ordinal).Select(r => (JsonNode?)r).ToArray());
        }
    }
    private sealed record ComponentEvidence(DiagnosticEvidence Evidence, HashSet<string> Paths, JsonObject Summary);
    private sealed record MemberSelection(string ObjectPath, string[] Path);
    private static JsonObject? Signatures(ComponentEvidence source, string path)
    {
        if (source.Evidence.Xml(path) is null) return null;
        return ProjectReviewStore.XmlSignatures((string)source.Evidence.Documents[path]["xml"]!);
    }
    private static JsonObject Declaration(ComponentEvidence source, MemberSelection selection)
    {
        if (!source.Paths.Contains(selection.ObjectPath)) throw Error("A member selector must belong to the explicitly selected component block/type set.");
        var document = source.Evidence.Xml(selection.ObjectPath);
        var roots = document?.Root?.Elements().Where(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal) || e.Name.LocalName == "SW.Types.PlcStruct").ToArray();
        var root = roots?.Length == 1 ? roots[0] : null;
        var interfaces = root is null ? [] : DiagnosticEvidence.Children(root, "AttributeList").SelectMany(a => DiagnosticEvidence.Children(a, "Interface")).ToArray();
        var member = interfaces.Length == 1 ? interfaces[0] : null;
        foreach (string name in selection.Path)
        {
            var matches = member is null ? [] : DiagnosticEvidence.Members(member).Where(m => (string?)m.Attribute("Name") == name).ToArray();
            member = matches.Length == 1 ? matches[0] : null;
        }
        string? type = (string?)member?.Attribute("Datatype");
        return new JsonObject { ["objectPath"] = selection.ObjectPath, ["memberPath"] = new JsonArray(selection.Path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["status"] = member is null || string.IsNullOrWhiteSpace(type) ? "unavailableOrAmbiguous" : "capturedDeclaration", ["dataType"] = type,
            ["evidence"] = member is null ? null : source.Evidence.Evidence(selection.ObjectPath, member), ["nativeCompatibilityVerified"] = false };
    }
    private static MemberSelection? Member(JsonNode? value)
    {
        if (value is null) return null;
        var selector = Object(value); Fields(selector, "objectPath memberPath"); string objectPath = Text(selector, "objectPath");
        var path = Array(selector, "memberPath", 16); if (path.Count == 0) throw Error("Member paths require 1..16 exact components or null.");
        return new(objectPath, path.Select(p => Text(new JsonObject { ["value"] = p?.DeepClone() }, "value", 128)).ToArray());
    }
    internal static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw Error("Expected an object.");
    internal static void Fields(JsonObject value, string fields) { var names = fields.Split(' '); if (value.Count != names.Length || names.Any(n => !value.ContainsKey(n))) throw Error("Missing or unknown fields; expected: " + fields); }
    internal static JsonArray Array(JsonObject value, string field, int maximum) => value[field] is JsonArray rows && rows.Count <= maximum ? rows : throw Error("Missing or oversized " + field + " collection.");
    internal static string Text(JsonObject value, string field, int maximum = 8192)
    {
        if (value[field] is not JsonValue v || !v.TryGetValue<string>(out string? text) || text is null) throw Error("Expected exact text: " + field);
        EngineeringIdentityGuard.Text(text, field, maximum); return text;
    }
    private static string? NullableText(JsonObject value, string field, int maximum = 8192) => value[field] is null ? null : Text(value, field, maximum);
    internal static bool Boolean(JsonObject value, string field) => value[field] is JsonValue v && v.TryGetValue<bool>(out bool result) ? result : throw Error("Expected Boolean: " + field);
    internal static BridgeException Error(string message) => new("InvalidHmiComponentReview", message);
}
