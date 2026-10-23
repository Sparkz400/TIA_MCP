using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;

internal static class PaginationQualification
{
    internal static int Run(string originalManifest, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Choose a fresh output directory.");
        Directory.CreateDirectory(output);
        byte[] bytes = File.ReadAllBytes(originalManifest);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(bytes, BridgeProtocol.Json)!;
        if (manifest.Candidates.Length != 10000 || manifest.State != "ready" ||
            !Guid.TryParseExact(manifest.PackageId, "N", out var packageId)) throw new ArgumentException("Expected retained 10000-candidate scale manifest.");
        string artifacts = Path.Combine(output, "metadata-fixture");
        var store = new PackageStore(artifacts);
        string directory = Path.Combine(artifacts, "library-intake", "packages", packageId.ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), bytes);
        File.WriteAllText(Path.Combine(directory, "state.json"), "{\"state\":\"ready\"}");
        var pages = new List<PackagePage>();
        var samples = new List<object>();
        int offset = 0;
        while (true)
        {
            long allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            // Each MCP request constructs a new store; cache reuse must not rely on one instance.
            var page = new PackageStore(artifacts).Inspect(manifest.PackageId, offset: offset, count: 200);
            watch.Stop(); samples.Add(new { milliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated });
            // Only the fixture location varies across runs. Preserve all other actual output fields.
            pages.Add(page with { ManifestPath = "<metadata-fixture>/manifest.json" });
            if (page.NextOffset is not int next) break;
            if (next != offset + page.Items.Length) throw new Exception("Wrong continuation offset.");
            offset = next;
        }
        var actual = pages.SelectMany(p => p.Items).Cast<PackageCandidate>().ToArray();
        if (pages.Count != 50 || actual.Length != 10000 || actual.Select(c => c.CandidateId).Distinct().Count() != 10000 ||
            JsonSerializer.Serialize(actual, BridgeProtocol.Json) != JsonSerializer.Serialize(manifest.Candidates, BridgeProtocol.Json))
            throw new Exception("Pagination changed or lost candidates.");
        var serialized = JsonSerializer.SerializeToUtf8Bytes(pages, BridgeProtocol.Json);
        File.WriteAllBytes(Path.Combine(output, "pages.json"), serialized);
        var result = new { completed = true, nativeExecuted = false,
            scope = "Metadata-only fixture copied from retained actual package-store manifest; no staging or native execution",
            inputSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), candidates = actual.Length, pages = pages.Count,
            outputSha256 = Convert.ToHexStringLower(SHA256.HashData(serialized)), outputBytes = serialized.Length,
            samples, processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Pagination qualification passed: 10000 exact candidates across 50 pages."); return 0;
    }
}
