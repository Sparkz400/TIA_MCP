using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

namespace TiaMcp.Host;

internal static class ProjectExplorerArtifacts
{
    internal static JsonElement CreateViewer(JsonElement export, string artifactsRoot)
    {
        var result = JsonNode.Parse(export.GetRawText())!.AsObject();
        var artifact = result["artifact"]?.AsObject() ?? throw new BridgeException("InvalidArtifact", "Worker export did not return its snapshot artifact.");
        var path = artifact["path"]!.GetValue<string>();
        // Apply the same host ownership and reparse-point checks as artifact transfer.
        var check = JsonSerializer.SerializeToElement(new ArtifactStore(artifactsRoot).Read(path, 0, 1), BridgeProtocol.Json);
        if (check.GetProperty("sha256").GetString() != artifact["sha256"]?.GetValue<string>()
            || check.GetProperty("bytes").GetInt64() != artifact["bytes"]?.GetValue<long>())
            throw new BridgeException("ArtifactChanged", "Snapshot no longer matches the worker export manifest. No engineering operation was retried.");
        if (check.GetProperty("bytes").GetInt64() > 40L * 1024 * 1024) throw new BridgeException("ArtifactCapacity", "Snapshot exceeds the portable viewer limit.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != check.GetProperty("sha256").GetString())
            throw new BridgeException("ArtifactChanged", "Snapshot changed during export. No engineering operation was retried.");
        var snapshot = JsonNode.Parse(bytes) ?? throw new BridgeException("InvalidArtifact", "Missing snapshot JSON.");
        var assets = FindAssets();
        var html = Render(File.ReadAllText(Path.Combine(assets, "index.template.html")), assets, snapshot.ToJsonString());
        var output = Path.Combine(Path.GetDirectoryName(path)!, "explorer-" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(output, html, new UTF8Encoding(false));
        var body = File.ReadAllBytes(output);
        result["snapshotArtifact"] = artifact.DeepClone();
        result["artifact"] = JsonSerializer.SerializeToNode(new { path = output, bytes = body.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(body)), mimeType = "text/html", portable = true, networkRequired = false });
        return JsonSerializer.SerializeToElement(result);
    }

    // The UI package owns the exact embedding layout; all substituted project text
    // stays in an inert JSON script and is read with JSON.parse, never as markup.
    internal static string Render(string template, string assets, string json)
    {
        var safe = json.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
        var script = string.Join("\n", new[] { "model.js", "client.js", "explorer.js" }.Select(file => File.ReadAllText(Path.Combine(assets, file))));
        return template.Replace("__EXPLORER_STYLE__", File.ReadAllText(Path.Combine(assets, "style.css")))
            .Replace("__EXPLORER_SCRIPT__", script).Replace("__PROJECT_MODEL_JSON__", safe);
    }

    internal static string FindAssets()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "ui", "project-explorer");
            if (File.Exists(Path.Combine(path, "index.html"))) return path;
        }
        throw new BridgeException("ExplorerAssetsUnavailable", "This installation has no explorer UI assets. Use the JSON artifact or install the complete release bundle.");
    }
}
