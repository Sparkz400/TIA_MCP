using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Exact original bytes beside decoded observations; never a synthesized trace measurement.</summary>
internal static class RuntimeCaptureAcquisitionSource
{
    internal const int MaximumRows = 10000, MaximumOriginalBytes = 3 * 1024 * 1024;

    internal static JsonObject Read(string root, RuntimeCaptureStore captures, string snapshotRef)
    {
        var snapshot = captures.ReadSnapshot(snapshotRef);
        if (snapshot.Progress.Rows > MaximumRows)
            throw new BridgeException("ArtifactCapacity", "Acquisition retains at most 10,000 complete rows; use the original runtime capture export for larger captures.");
        var parts = snapshotRef.Split(':'); // ReadSnapshot has validated the complete reference and chain.
        int count = int.Parse(parts[2], CultureInfo.InvariantCulture);
        string expected = parts[3]; int originalBytes = 0;
        var originals = new List<JsonNode>();
        byte[] Read(string relative, string hash, int maximum)
        {
            byte[] bytes = ProjectReviewStore.ReadArtifact(root,
                new JsonObject { ["path"] = Path.Combine(Path.GetFullPath(root), relative), ["sha256"] = hash }, maximum);
            originalBytes = checked(originalBytes + bytes.Length);
            if (originalBytes > MaximumOriginalBytes)
                throw new BridgeException("ArtifactCapacity", "Acquisition original chain exceeds 3 MiB; the complete runtime capture remains available without diagnostic truncation.");
            originals.Add(new JsonObject { ["relativePath"] = relative.Replace('\\', '/'), ["sha256"] = hash,
                ["bytes"] = bytes.Length, ["base64"] = Convert.ToBase64String(bytes) });
            return bytes;
        }
        for (int index = count; index > 0; index--)
        {
            byte[] bytes = Read($"runtime-captures/{parts[1]}/{index.ToString("D6", CultureInfo.InvariantCulture)}.segment", expected, 1 << 20);
            using var segment = JsonDocument.Parse(bytes);
            expected = segment.RootElement.GetProperty("previousSha256").GetString()!;
        }
        _ = Read($"runtime-captures/{parts[1]}/plan.json", expected, 128 << 10);
        originals.Reverse();
        var rows = new JsonArray();
        foreach (var row in snapshot.Rows) rows.Add(JsonSerializer.SerializeToNode(row, BridgeProtocol.Json));
        if (rows.Count != snapshot.Progress.Rows)
            throw new BridgeException("RuntimeCaptureIntegrityFailure", "The selected committed prefix changed while reading.");
        return new JsonObject { ["snapshotRef"] = snapshotRef, ["originalFiles"] = new JsonArray(originals.ToArray()),
            ["originalByteCount"] = originalBytes, ["plan"] = JsonSerializer.SerializeToNode(snapshot.Plan, BridgeProtocol.Json),
            ["recordedProgress"] = JsonSerializer.SerializeToNode(snapshot.Progress, BridgeProtocol.Json),
            ["rows"] = rows, ["rowsTruncated"] = false,
            ["chainSha256"] = parts[3], ["planSha256"] = expected, ["segmentCount"] = count,
            ["currentHeadConsulted"] = false, ["uncommittedDataUnknown"] = snapshot.Progress.State == "running" };
    }

    internal static string Id(JsonObject value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json)));
}
