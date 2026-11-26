using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TiaMcp.Host;

/// <summary>#219: hashes taken when this Host started, compared with the files on disk now. A rebuilt or replaced binary shows as
/// changedOnDisk, so a new build is never mistaken for the one this Host (or its next worker start) is running.</summary>
public static class BuildIdentity
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private static readonly Dictionary<string, string?> AtStart = new(StringComparer.OrdinalIgnoreCase);

    public static void Capture(params string?[] paths)
    {
        lock (AtStart)
            foreach (string? path in paths.Append(Environment.ProcessPath))
                if (!string.IsNullOrWhiteSpace(path) && !AtStart.ContainsKey(path)) AtStart[path] = Hash(path);
    }

    public static JsonObject Report(string? workerPath, string? simulatorPath)
    {
        JsonObject File(string role, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new JsonObject { ["role"] = role, ["configured"] = false };
            string? before; lock (AtStart) AtStart.TryGetValue(path, out before);
            string? now = Hash(path);
            return new JsonObject
            {
                ["role"] = role,
                ["path"] = Path.GetFullPath(path),
                ["sha256AtHostStart"] = before,
                ["sha256Now"] = now,
                ["changedOnDisk"] = before is not null && now != before,
                ["presentNow"] = now is not null
            };
        }
        string? host = Environment.ProcessPath;
        var root = host is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(host));
        string? manifest = root is null ? null : Path.Combine(root, "manifest.json");
        var files = new JsonArray(File("host", host), File("engineeringWorker", workerPath), File("simulatorWorker", simulatorPath));
        bool changed = files.OfType<JsonObject>().Any(f => f["changedOnDisk"]?.GetValue<bool>() == true);
        return new JsonObject
        {
            ["schemaVersion"] = "host-build-identity.v1",
            ["hostProcessId"] = Environment.ProcessId,
            ["hostStartedAtUtc"] = Started.ToString("O"),
            ["productVersion"] = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["files"] = files,
            ["bundle"] = manifest is not null && System.IO.File.Exists(manifest) ? Bundle(root!, manifest) : null,
            ["runningMatchesDisk"] = !changed,
            ["note"] = changed
                ? "A binary changed on disk after this Host started. This Host still runs the build it started with; a worker started later would load the new file. Restart the Host to run one consistent build."
                : "The running Host and its configured workers match the files on disk. Compare sha256Now with a build's own hashes to tell which build that is; building or packaging elsewhere does not update this Host."
        };
    }

    /// <summary>The bundle's manifest hash and the source it was packaged from. sourceCommit is null for bundles packaged before it
    /// was recorded or without git; the random suffix of a bundle's folder name is not a commit.</summary>
    internal static JsonObject Bundle(string root, string manifest)
    {
        var bundle = new JsonObject { ["root"] = root, ["manifestSha256"] = Hash(manifest), ["sourceCommit"] = null, ["sourceTreeDirty"] = null, ["createdAtUtc"] = null };
        try
        {
            if (JsonNode.Parse(System.IO.File.ReadAllText(manifest)) is JsonObject parsed)
                foreach (string name in new[] { "sourceCommit", "sourceTreeDirty", "createdAtUtc", "productVersion" })
                    if (parsed[name] is JsonValue value) bundle[name] = value.DeepClone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { bundle["manifestReadError"] = e.Message; }
        if (bundle["sourceCommit"] is null) bundle["sourceCommitNote"] = "This bundle records no source commit (packaged before it was recorded, or without git). Its folder name's suffix is random, not a commit.";
        return bundle;
    }

    private static string? Hash(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
