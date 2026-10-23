using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Immutable offline artifacts. This assembly has no Siemens dependency.</summary>
public sealed class DiagnosticStore(string artifactsRoot)
{
    private string DirectoryPath => Path.Combine(Path.GetFullPath(artifactsRoot), "trace-diagnostics");
    public const int MaxBytes = 8 * 1024 * 1024;

    public JsonObject Save(JsonObject document)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, BridgeProtocol.Json);
        var receipt = Publish(bytes, ".json");
        receipt["schemaVersion"] = document["schemaVersion"]?.DeepClone();
        return receipt;
    }

    public JsonObject RetainUpload(string uploadRef)
    {
        var uploaded = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        var bytes = ProjectReviewStore.ReadArtifact(artifactsRoot,
            new JsonObject { ["path"] = uploaded.Path, ["sha256"] = uploaded.Sha256 }, MaxBytes);
        return Publish(bytes, ".input")["artifact"]!.DeepClone().AsObject();
    }

    internal JsonObject Publish(byte[] bytes, string extension)
    {
        if (bytes.Length > MaxBytes) throw new BridgeException("ArtifactCapacity", "Diagnostic artifact exceeds 8 MiB.");
        for (var dir = new DirectoryInfo(DirectoryPath); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("ArtifactPathDenied", "Diagnostic storage cannot traverse links.");
        Directory.CreateDirectory(DirectoryPath);
        // Serialize quota checks and publication across host processes sharing this artifact root.
        // Keep the zero-byte lock file: deleting it would let another process lock a new inode.
        string lockPath = Path.Combine(DirectoryPath, ".write.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new BridgeException("ArtifactPathDenied", "Diagnostic storage lock cannot be a link.");
        using var publicationLock = AcquireLock(lockPath);
        string id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string path = Path.Combine(DirectoryPath, id + extension);
        PackageStorage.EnsureNoLinks(path);
        var artifact = new JsonObject { ["path"] = path, ["sha256"] = id, ["bytes"] = bytes.Length };
        if (!File.Exists(path))
        {
            if (new DirectoryInfo(DirectoryPath).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 256L * 1024 * 1024)
                throw new BridgeException("ArtifactCapacity", "Diagnostic retention limit is 256 MiB; archive selected artifacts explicitly.");
            // Concurrent identical writers may race. Only an intact matching artifact is reusable.
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes); stream.Flush(true);
            }
            catch (IOException) when (File.Exists(path)) { _ = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, MaxBytes); }
        }
        _ = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, MaxBytes);
        return new JsonObject { ["id"] = id, ["artifact"] = artifact };
    }

    private static FileStream AcquireLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BridgeException("ArtifactBusy", "Another diagnostic artifact publication holds the storage lock. No document was published by this attempt."); }
    }

    public JsonObject Read(string id, string? schema = null)
    {
        var result = JsonNode.Parse(ReadBytes(id))?.AsObject() ?? throw new BridgeException("InvalidRequest", "Expected a diagnostic document.");
        if (schema is not null && D.Text(result, "schemaVersion") != schema)
            throw new BridgeException("InvalidRequest", "Unexpected diagnostic artifact schema.");
        return result;
    }

    internal byte[] ReadBytes(string id)
    {
        if (!Regex.IsMatch(id ?? "", "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant))
            throw new BridgeException("InvalidRequest", "Diagnostic ID must be an exact SHA-256 hash.");
        return ProjectReviewStore.ReadArtifact(artifactsRoot,
            new JsonObject { ["path"] = Path.Combine(DirectoryPath, id + ".json"), ["sha256"] = id }, MaxBytes);
    }
}

internal static class D
{
    internal static string Text(JsonNode? node, string field) => node?[field]?.GetValue<string>() ?? "";
    internal static JsonArray Rows(JsonNode? node) => node as JsonArray ?? new JsonArray();
    internal static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    internal static string Required(JsonNode node, string field, int max = 4096)
    {
        string value = Text(node, field);
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new BridgeException("InvalidRequest", field + " is required and must contain at most " + max + " characters.");
        return value;
    }
    internal static string[] Path(JsonNode? value)
    {
        if (value is not JsonArray array || array.Count is < 1 or > 16)
            throw new BridgeException("InvalidRequest", "memberPath must contain 1..16 exact member names.");
        var path = array.Select(n => n?.GetValue<string>() ?? "").ToArray();
        if (path.Any(s => s.Length is < 1 or > 128 || s.Any(char.IsControl)))
            throw new BridgeException("InvalidRequest", "Invalid member name.");
        return path;
    }
    internal static void Bound(JsonObject input, int bytes = 65536)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(input.ToJsonString()) > bytes)
            throw new BridgeException("InvalidRequest", "Diagnostic request exceeds its byte limit.");
    }
}

public sealed class DiagnosticResolutionException(string message, JsonObject details) : BridgeException("InstanceResolutionIncomplete", message)
{
    public JsonObject Details { get; } = details;
}
