using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.AutomationMl;

namespace TiaMcp.Core.Engineering;

/// <summary>Native producer for one composite phase: worker captures bound to one saved review, retained through the existing component workflows.</summary>
public static class CompositeEngineeringCapture
{
    // Two captured phases stay within the composite bound of 64 component references.
    private const int RecordSlots = 32;

    public static async Task<JsonObject> Capture(IBridgeClient bridge, string artifactsRoot, string reviewId, PackageIntakeLimits? limits, CancellationToken cancellationToken)
    {
        var review = new ProjectReviewStore(artifactsRoot).Read(reviewId);
        var project = review["model"]!["project"]!;
        string? projectName = Text(project["name"]);
        string projectPath = Text(project["projectPath"]) ?? throw new BridgeException("InvalidCompositeCapture", "The saved review declares no project path to bind native captures to.");
        var uploads = new ArtifactUploadStore(artifactsRoot, limits);
        var phase = new JsonObject { ["reviewId"] = reviewId, ["automationMlRef"] = null, ["connections"] = new JsonArray(), ["drives"] = new JsonArray(), ["technology"] = new JsonArray() };
        var captures = new JsonArray(); int slots = RecordSlots;

        var (aml, amlError) = await Native("cax_export", new() { ["maxItems"] = 500, ["artifactDirectory"] = Path.Combine(artifactsRoot, "exports") });
        var amlCapture = Row("automationMl", "cax_export", amlError);
        if (aml is not null) phase["automationMlRef"] = Retain(amlCapture, "project", () => ProjectReviewStore.ReadArtifact(artifactsRoot, aml["artifact"]!.AsObject(), AutomationMlDocument.MaxInputBytes),
            "capture.aml", upload => (string)JsonSerializer.SerializeToNode(new AutomationMlWorkflowStore(artifactsRoot, limits).Inspect(upload), BridgeProtocol.Json)!["recordRef"]!);

        var (connections, connectionError) = await Native("connection_inspect", new() { ["maxItems"] = 512 });
        var connectionCapture = Row("connections", "connection_inspect", connectionError);
        if (connections is not null)
        {
            var snapshot = connections["reviewSnapshot"]!;
            // A single observed state: identical before/after with identity matches, so every connection compares unchanged.
            Component("connections", connectionCapture, "project", new JsonObject { ["schemaVersion"] = "connection-review-request.v1", ["reviewId"] = reviewId, ["afterKind"] = "observation",
                ["before"] = snapshot.DeepClone(), ["after"] = snapshot.DeepClone(), ["matches"] = new JsonArray(snapshot["connections"]!.AsArray()
                    .Select(c => (JsonNode)new JsonObject { ["beforeId"] = c?["id"]?.DeepClone(), ["afterId"] = c?["id"]?.DeepClone() }).ToArray()) },
                upload => (string)new ConnectionWorkflowStore(artifactsRoot).Review(reviewId, upload)["recordId"]!);
        }

        var (drives, driveError) = await Native("drive_observe", new() { ["maxItems"] = 2000 });
        var driveCapture = Row("drives", "drive_observe", driveError);
        foreach (var drive in drives?["drives"]?.AsArray() ?? new JsonArray())
            foreach (var item in drive!["driveObjects"]!.AsArray())
                Component("drives", driveCapture, (string)drive["hardwarePath"]! + "#DO" + item!["driveObjectNumber"], new JsonObject { ["schemaVersion"] = "drive-profile-review-request.v2",
                    ["reviewId"] = reviewId, ["hardwarePath"] = drive["hardwarePath"]!.DeepClone(), ["afterKind"] = "observation", ["before"] = item["snapshot"]!.DeepClone(), ["after"] = item["snapshot"]!.DeepClone() },
                    upload => (string)new DriveProfileWorkflowStore(artifactsRoot).Review(reviewId, upload)["recordId"]!);

        // Technology objects come from the saved review, so every record names a captured TO of this exact review.
        var objects = review["model"]!["nodes"]!.AsArray().OfType<JsonObject>().Where(n => Text(n["kind"]) == "technologyObject" && Text(n["origin"]) != "virtual")
            .Select(n => Text(n["objectPath"])).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
        JsonObject? technologyCapture = null; bool technologyTruncated = false;
        foreach (string path in objects)
        {
            if (slots == 0) { technologyTruncated = true; break; }
            var (read, error) = await Native("technology_read", new() { ["technologyPath"] = path, ["maxItems"] = 2000 });
            if (error is not null && technologyCapture is null && Absent(error)) { technologyCapture = Row("technology", "technology_read", error); break; }
            technologyCapture ??= Row("technology", "technology_read");
            if (error is not null) { Failed(technologyCapture, path, error); continue; }
            Component("technology", technologyCapture, path, TechnologyRequest(reviewId, path, read!),
                upload => (string)new TechnologyProfileWorkflowStore(artifactsRoot).Review(reviewId, upload)["recordId"]!);
        }
        if (technologyCapture is null) { technologyCapture = Row("technology", "technology_read"); if (objects.Length == 0) { technologyCapture["status"] = "notCaptured"; technologyCapture["message"] = "The saved review contains no technology objects."; } }
        if (technologyTruncated) technologyCapture["truncated"] = true;

        return new JsonObject { ["schemaVersion"] = "composite-engineering-capture.v1", ["reviewId"] = reviewId, ["projectPath"] = projectPath, ["phase"] = phase, ["captures"] = captures,
            ["composable"] = phase["automationMlRef"] is not null,
            ["usage"] = "Use phase as the before or after context of a composite-engineering-request; without a native AML capture supply automationMlRef from automationml_inspect.",
            ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["wholeProjectCaptured"] = false };

        async Task<(JsonObject? Response, BridgeOperationException? Error)> Native(string operation, JsonObject arguments)
        {
            arguments["projectName"] = projectName;
            try
            {
                var response = JsonNode.Parse((await bridge.CallAsync(operation, arguments, cancellationToken)).GetRawText())!.AsObject();
                if (Text(response["project"]?["path"]) != projectPath)
                    throw new BridgeException("ProjectMismatch", "A native capture answered for a different project than the saved review; no phase was produced.");
                return (response, null);
            }
            // A completed worker refusal is retained; lost transport stays terminal and aborts the capture.
            catch (BridgeOperationException error) { return (null, error); }
        }
        JsonObject Row(string domain, string operation, BridgeOperationException? error = null)
        {
            var row = new JsonObject { ["domain"] = domain, ["operation"] = operation, ["status"] = error is null ? "native" : Absent(error) ? "unavailable" : "failed",
                ["records"] = new JsonArray(), ["truncated"] = false };
            if (error is not null) { row["errorCode"] = error.Code; row["message"] = error.Message; }
            captures.Add(row); return row;
        }
        void Component(string field, JsonObject capture, string source, JsonObject request, Func<string, string> publish)
        {
            if (slots == 0) { capture["truncated"] = true; return; }
            if (Retain(capture, source, () => JsonSerializer.SerializeToUtf8Bytes(request, BridgeProtocol.Json), field + ".json", publish) is string id) { phase[field]!.AsArray().Add(id); slots--; }
        }
        string? Retain(JsonObject capture, string source, Func<byte[]> read, string name, Func<string, string> publish)
        {
            string? upload = null;
            try
            {
                upload = Upload(uploads, read(), name); string id = publish(upload);
                capture["records"]!.AsArray().Add(new JsonObject { ["source"] = source, ["status"] = "retained", ["recordRef"] = id }); return id;
            }
            catch (BridgeException error) { Failed(capture, source, error); return null; }
            finally { if (upload is not null) uploads.Release(upload["upload:".Length..]); }
        }
    }

    // UnknownOperation: this worker has no such operation; UnsupportedCapability: the product option (e.g. Startdrive) is absent.
    private static bool Absent(BridgeException error) => error.Code is "UnknownOperation" or "UnsupportedCapability";
    private static void Failed(JsonObject capture, string source, BridgeException error) =>
        capture["records"]!.AsArray().Add(new JsonObject { ["source"] = source, ["status"] = "failed", ["errorCode"] = error.Code, ["message"] = error.Message });

    private static string Upload(ArtifactUploadStore uploads, byte[] bytes, string name)
    {
        var begun = uploads.Begin(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        try
        {
            for (int offset = 0; offset < bytes.Length; offset += begun.MaxChunkBytes)
                uploads.Write(begun.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(begun.MaxChunkBytes, bytes.Length - offset)));
            return uploads.Commit(begun.UploadId).ArtifactRef!;
        }
        catch { uploads.Abort(begun.UploadId); throw; }
    }

    /// <summary>technology_read output as one observed technology-profile-review-request.v1 state.</summary>
    private static JsonObject TechnologyRequest(string reviewId, string path, JsonObject read)
    {
        var parameters = new JsonArray(read["parameters"]!.AsArray().OfType<JsonObject>().Select(p => (JsonNode)new JsonObject {
            ["name"] = p["name"]?.DeepClone(), ["nativeType"] = p["type"]?.DeepClone(),
            ["access"] = p["writable"] is JsonValue w && w.TryGetValue<bool>(out bool writable) ? writable ? "readWrite" : "readOnly" : "unknown",
            ["value"] = p["valueCaptured"] is JsonValue c && c.TryGetValue<bool>(out bool captured) && captured
                ? new JsonObject { ["kind"] = p["valueComplete"] is JsonValue v && v.TryGetValue<bool>(out bool complete) && complete ? "scalar" : "opaque", ["text"] = Text(p["value"]) ?? p["value"]?.ToJsonString() ?? "null" }
                : new JsonObject { ["kind"] = "unavailable", ["reason"] = Text(p["valueStatus"]) ?? "notCaptured" },
            ["metadata"] = new JsonObject { ["valueStatus"] = p["valueStatus"]?.DeepClone(), ["valueAccess"] = p["valueAccess"]?.DeepClone() } }).ToArray());
        var relationships = new JsonArray(); string prefix = path + "/hardwareConnections/";
        foreach (var row in read["hardwareConnections"]!.AsArray().OfType<JsonObject>().Where(r => Text(r["status"]) != "notExposed"))
        {
            string observation = Text(row["observationPath"]) ?? ""; string id = observation.StartsWith(prefix, StringComparison.Ordinal) ? observation[prefix.Length..] : observation;
            // Direct field and attribute values only: a channel reference nests its owning module, which is not a second target.
            var references = row.Select(p => p.Value).Concat((row["attributes"] as JsonArray ?? new JsonArray()).Select(a => a?["value"])).OfType<JsonObject>()
                .Where(r => Text(r["valueSemantics"]) == "engineeringReference" && Text(r["resolution"]) == "resolved" && Text(r["kind"]) is "hardware" or "channel").ToArray();
            var targets = references.Select(r => Text(Text(r["kind"]) == "channel" ? r["parent"]?["hardwarePath"] : r["hardwarePath"])).Distinct().ToArray();
            bool? connected = row["isConnected"] is JsonValue c && c.TryGetValue<bool>(out bool value) ? value : null;
            string state = connected == true && targets is [string] ? "declared" : connected == false ? "disconnected" : "unknown";
            var metadata = new JsonObject { ["role"] = row["role"]?.DeepClone(), ["service"] = row["service"]?.DeepClone(), ["observationPath"] = observation,
                ["target"] = (references.FirstOrDefault(r => Text(r["kind"]) == "channel") ?? references.FirstOrDefault())?.DeepClone() };
            if (Text(row["role"]) == "sensor" && int.TryParse(id[(id.LastIndexOf('/') + 1)..], out int sensor)) metadata["sensorIndex"] = sensor;
            if (row["channelIndex"] is JsonValue index && index.TryGetValue<int>(out int channelIndex)) metadata["channelIndex"] = channelIndex;
            relationships.Add(new JsonObject { ["id"] = id, ["kind"] = "hardware", ["sourcePath"] = path, ["targetPath"] = state == "declared" ? targets[0] : null,
                ["targetMember"] = null, ["state"] = state, ["metadata"] = metadata, ["reason"] = state != "unknown" ? null
                    : $"Observation {Text(row["status"])}: isConnected {connected?.ToString() ?? "unknown"}, {targets.Length} resolved hardware/channel references." });
        }
        var info = read["technologyObject"]!;
        var snapshot = new JsonObject {
            ["identity"] = new JsonObject { ["plcPath"] = read["plcPath"]?.DeepClone(), ["orderNumber"] = null, ["firmware"] = null, ["technologyType"] = Text(info["systemLibraryType"]),
                ["technologyVersion"] = Text(info["systemLibraryVersion"]), ["options"] = new JsonArray(), ["unknownReason"] = "technology_read exposes no CPU order number or firmware." },
            ["valueDomain"] = "offline", ["parametersComplete"] = read["parametersComplete"]?.DeepClone(),
            ["relationshipsComplete"] = read["hardwareConnectionCoverage"]?["complete"] is JsonValue done && done.TryGetValue<bool>(out bool all) && all,
            ["parameters"] = parameters, ["relationships"] = relationships };
        return new JsonObject { ["schemaVersion"] = "technology-profile-review-request.v1", ["reviewId"] = reviewId, ["technologyPath"] = path,
            ["afterKind"] = "observation", ["before"] = snapshot, ["after"] = snapshot.DeepClone() };
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
}
