using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Core;
using TiaMcp.Host;

namespace TiaMcp.Tests;

internal static class ProjectExplorerArtifactTests
{
    internal static Task ManifestIntegrity()
    {
        string root = Scratch("manifest"); string path = Path.Combine(root, "snapshot.json");
        File.WriteAllText(path, "{\"schemaVersion\":\"project-model.v1\",\"name\":\"Original\"}");
        var export = Manifest(path);
        var changedHash = export.DeepClone().AsObject(); changedHash["artifact"]!["sha256"] = new string('a', 64);
        Expect("ArtifactChanged", () => ProjectExplorerArtifacts.CreateViewer(Element(changedHash), root));
        var changedSize = export.DeepClone().AsObject(); changedSize["artifact"]!["bytes"] = 1;
        Expect("ArtifactChanged", () => ProjectExplorerArtifacts.CreateViewer(Element(changedSize), root));
        // A same-size replacement after the worker result must not be silently repackaged.
        File.WriteAllText(path, File.ReadAllText(path).Replace("Original", "Replaced"));
        Expect("ArtifactChanged", () => ProjectExplorerArtifacts.CreateViewer(Element(export), root));
        Check.Equal(0, Directory.GetFiles(root, "*.html").Length);
        var fresh = Manifest(path);
        var result = ProjectExplorerArtifacts.CreateViewer(Element(fresh), root);
        string htmlPath = result.GetProperty("artifact").GetProperty("path").GetString()!;
        Check.Equal(true, File.Exists(htmlPath));
        Check.Equal(false, result.GetProperty("artifact").GetProperty("networkRequired").GetBoolean());
        Check.Equal(fresh["artifact"]!["sha256"]!.GetValue<string>(), result.GetProperty("snapshotArtifact").GetProperty("sha256").GetString());
        Check.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(htmlPath))), result.GetProperty("artifact").GetProperty("sha256").GetString());
        string siblingRoot = Scratch("outside"); string outside = Path.Combine(siblingRoot, "snapshot.json"); File.WriteAllText(outside, "{}");
        Expect("ArtifactPathDenied", () => ProjectExplorerArtifacts.CreateViewer(Element(Manifest(outside)), root));
        return Task.CompletedTask;
    }

    internal static Task InertEmbedding()
    {
        string assets = ProjectExplorerArtifacts.FindAssets();
        string payload = "</script><script>globalThis.injected=true</script><img src=https://attacker.invalid/x onerror=alert(1)> & \"外部\"\u2028\u2029";
        // Construct literal hostile JSON rather than relying on the serializer's default escaping.
        string json = "{\"schemaVersion\":\"project-model.v1\",\"label\":" + JsonSerializer.Serialize(payload, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "}";
        string html = ProjectExplorerArtifacts.Render(File.ReadAllText(Path.Combine(assets, "index.template.html")), assets, json);
        var data = Regex.Match(html, "<script id=\"project-model\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        Check.Equal(true, data.Success);
        Check.Equal(false, data.Groups[1].Value.Contains('<'));
        Check.Equal(false, data.Groups[1].Value.Contains('>'));
        using var document = JsonDocument.Parse(data.Groups[1].Value);
        Check.Equal(payload, document.RootElement.GetProperty("label").GetString());
        Check.Equal(false, html.Contains("<script>globalThis.injected=true</script>", StringComparison.Ordinal));
        Check.Equal(2, Regex.Matches(html, "<script(?:\\s|>)", RegexOptions.IgnoreCase).Count);
        Check.Equal(2, Regex.Matches(html, "</script>", RegexOptions.IgnoreCase).Count);
        Check.Equal(false, Regex.IsMatch(html, "<(?:script|link|iframe)\\b[^>]*\\b(?:src|href)\\s*=", RegexOptions.IgnoreCase));
        Check.Equal(true, html.Contains("connect-src 'none'", StringComparison.Ordinal));
        Check.Equal(true, html.Contains("object-src 'none'", StringComparison.Ordinal));
        Check.Equal(false, html.Contains("__PROJECT_MODEL_JSON__", StringComparison.Ordinal));
        Check.Equal(false, html.Contains("__EXPLORER_SCRIPT__", StringComparison.Ordinal));
        Check.Equal(false, html.Contains("__EXPLORER_STYLE__", StringComparison.Ordinal));
        foreach (string file in new[] { "model.js", "client.js", "explorer.js", "style.css" })
            Check.Equal(true, html.Contains(File.ReadAllText(Path.Combine(assets, file)), StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    internal static async Task RelocatedAssetDiscovery()
    {
        string root = Scratch("relocation with spaces Ω");
        string relocatedBin = Path.Combine(root, "runtime", "tools"); Directory.CreateDirectory(relocatedBin);
        foreach (string source in Directory.GetFiles(AppContext.BaseDirectory))
            if (Path.GetExtension(source) is ".dll" or ".json" or ".exe") File.Copy(source, Path.Combine(relocatedBin, Path.GetFileName(source)));
        string originalAssets = ProjectExplorerArtifacts.FindAssets();
        string relocatedAssets = Path.Combine(root, "ui", "project-explorer"); Directory.CreateDirectory(relocatedAssets);
        foreach (string file in new[] { "index.html", "index.template.html", "model.js", "client.js", "explorer.js", "style.css" })
            File.Copy(Path.Combine(originalAssets, file), Path.Combine(relocatedAssets, file));
        string assembly = Path.Combine(relocatedBin, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        start.ArgumentList.Add(assembly); start.ArgumentList.Add("--probe-assets");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start relocated artifact probe.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new InvalidOperationException("Relocated asset probe timed out."); }
        string output = (await stdout).Trim(); string error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Relocated asset probe failed: " + error);
        string found = JsonSerializer.Deserialize<string>(output)!;
        string expected = Path.GetFullPath(relocatedAssets);
        // A relocated process can expose Windows' extended path spelling when
        // runner TEMP is long. Compare the same exact directory in that format.
        if (OperatingSystem.IsWindows() && found.StartsWith(@"\\?\", StringComparison.Ordinal))
            expected = expected.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + expected[2..] : @"\\?\" + expected;
        Check.Equal(expected, Path.GetFullPath(found));
        string template = File.ReadAllText(Path.Combine(found, "index.template.html"));
        string rendered = ProjectExplorerArtifacts.Render(template, found, "{\"schemaVersion\":\"project-model.v1\"}");
        Check.Equal(false, rendered.Contains("__EXPLORER_", StringComparison.Ordinal));
        Check.Equal(true, rendered.Contains("connect-src 'none'", StringComparison.Ordinal));
    }

    private static JsonObject Manifest(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new JsonObject { ["artifact"] = new JsonObject { ["path"] = path, ["bytes"] = bytes.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) } };
    }
    private static JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);
    private static string Scratch(string name)
    {
        // Retain small evidence files; no recursive filesystem deletion in this test.
        string directory = Path.Combine(Path.GetTempPath(), "tia-project-explorer-tests", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (BridgeException error) { Check.Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected artifact error: " + code);
    }
}
