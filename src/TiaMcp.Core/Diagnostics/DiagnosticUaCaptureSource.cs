using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Retain the exact prefix already validated by its acquisition backend.</summary>
public static class DiagnosticUaCaptureSource
{
    public const int MaximumRows = 10000, MaximumOriginalBytes = 3 << 20;

    public static JsonObject ReadPolling(string root, PhysicalRuntime.RuntimeCaptureStore store, string snapshotRef)
    {
        var snapshot = store.ReadSnapshot(snapshotRef);
        if (snapshot.Plan.UaContext is null) throw new BridgeException("InvalidRequest", "Expected a UA polling capture.");
        return Retain(root, "polling", snapshotRef, snapshot.Plan, snapshot.Progress, snapshot.Rows);
    }

    public static JsonObject Retain(string root, string mode, string snapshotRef, object plan, object progress, IEnumerable<object> rows)
    {
        string prefix = mode switch { "polling" => "capture", "subscription" => "ua-subscription",
            _ => throw new BridgeException("InvalidRequest", "Expected polling or subscription capture.") };
        var parts = snapshotRef.Split(':');
        if (parts.Length != 4 || parts[0] != prefix || !Regex.IsMatch(parts[1], "\\A[a-f0-9]{32}\\z") ||
            !Regex.IsMatch(parts[3], "\\A[a-f0-9]{64}\\z") || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
            parts[2] != count.ToString(CultureInfo.InvariantCulture) || count < 0)
            throw new BridgeException("InvalidRequest", "Expected an exact immutable UA snapshot reference.");
        if (count > MaximumRows + 1) throw Capacity();
        string directory = mode == "polling" ? "runtime-ua-acquisition/runtime-captures" : "runtime-ua-subscriptions";
        var originals = new List<JsonNode>(); int bytesRead = 0; string expected = parts[3];
        var decoded = new List<JsonObject>();
        JsonObject Read(string name, string hash, int maximum)
        {
            string relative = directory + "/" + parts[1] + "/" + name;
            byte[] bytes = ProjectReviewStore.ReadArtifact(root,
                new JsonObject { ["path"] = Path.Combine(Path.GetFullPath(root), relative), ["sha256"] = hash }, maximum);
            bytesRead = checked(bytesRead + bytes.Length);
            if (bytesRead > MaximumOriginalBytes) throw Capacity();
            originals.Add(new JsonObject { ["relativePath"] = relative, ["sha256"] = hash,
                ["bytes"] = bytes.Length, ["base64"] = Convert.ToBase64String(bytes) });
            return JsonNode.Parse(bytes)!.AsObject();
        }
        for (int i = count; i > 0; i--)
        {
            var segment = Read(i.ToString("D6", CultureInfo.InvariantCulture) + ".segment", expected, 1 << 20);
            decoded.Add(segment); expected = D.Required(segment, "previousSha256", 64);
        }
        var originalPlan = Read("plan.json", expected, 128 << 10);
        originals.Reverse(); decoded.Reverse();
        var observedRows = new JsonArray();
        foreach (object row in rows)
        {
            if (observedRows.Count >= MaximumRows) throw Capacity();
            observedRows.Add(JsonSerializer.SerializeToNode(row, BridgeProtocol.Json));
        }
        var originalRows = new JsonArray(decoded.SelectMany(s => s["rows"]!.AsArray()).Select(r => r!.DeepClone()).ToArray());
        var progressJson = JsonSerializer.SerializeToNode(progress, BridgeProtocol.Json)!;
        if (!JsonNode.DeepEquals(originalPlan, JsonSerializer.SerializeToNode(plan, BridgeProtocol.Json)) ||
            !JsonNode.DeepEquals(originalRows, observedRows) || observedRows.Count != progressJson["rows"]!.GetValue<long>() ||
            count > 0 && !JsonNode.DeepEquals(decoded[^1]["progress"], progressJson))
            throw new BridgeException("UaDiagnosticSourceMismatch", "Original chain bytes differ from the backend-validated plan, progress or rows.");
        return new JsonObject { ["snapshotRef"] = snapshotRef, ["acquisitionMode"] = mode,
            ["originalFiles"] = new JsonArray(originals.ToArray()), ["originalByteCount"] = bytesRead,
            ["plan"] = originalPlan, ["recordedProgress"] = progressJson, ["rows"] = observedRows,
            ["rowsTruncated"] = false, ["chainSha256"] = parts[3], ["planSha256"] = expected, ["segmentCount"] = count,
            ["currentHeadConsulted"] = false, ["uncommittedDataUnknown"] = D.Text(progressJson, "state") == "running" };
    }

    private static BridgeException Capacity() => new("ArtifactCapacity", "Diagnostic observations retain at most 10,000 complete rows and 3 MiB of original chain bytes. Use raw capture export for larger prefixes; no diagnostic rows are silently truncated.");
}
