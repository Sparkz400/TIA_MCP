using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Openness.V21;

internal static class ScaleQualification
{
    internal static int Run(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Choose a fresh scale output directory.");
        Directory.CreateDirectory(output);
        var measurements = new Dictionary<string, object>();
        var report = new Dictionary<string, object> {
            ["completed"] = false, ["nativeExecuted"] = false,
            ["scope"] = "Generated managed package and reference-graph fixtures; no native inventory, checkpoint or vendor qualification",
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["measurements"] = measurements
        };
        void Save() => File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        T Measure<T>(string name, Func<T> action)
        {
            long allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            T result = action(); watch.Stop();
            measurements[name] = new { milliseconds = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
            Save(); return result;
        }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        static void Refuse(string code, Action action)
        {
            try { action(); } catch (BridgeException error) when (error.Code == code) { return; }
            throw new Exception("Expected refusal: " + code);
        }
        static void RefuseGraph(string message, Action action)
        {
            try { action(); } catch (ArgumentException error) when (error.Message.Contains(message, StringComparison.Ordinal)) { return; }
            throw new Exception("Expected graph refusal: " + message);
        }
        Save();
        try
        {
            _ = LibraryReferenceReachability.RequireReachable(["/plc/blocks/Main"], ["/plc/blocks/Main"], [], [], true);
            string[] known = Enumerable.Range(0, 10000).Select(i => "/plc/blocks/B" + i.ToString("D5")).ToArray();
            var edges = Enumerable.Range(0, 999).Select(i => new LibraryReferenceEdge(known[i], known[i + 1])).ToArray();
            var reachable = Measure("graph1000NodesIn10000Objects", () => LibraryReferenceReachability.RequireReachable(known, [known[0]], edges, [known[999]], true));
            Require(reachable.SequenceEqual(known.Take(1000)), "Reference traversal lost or invented reachable objects.");
            var graphBytes = JsonSerializer.SerializeToUtf8Bytes(reachable);
            File.WriteAllBytes(Path.Combine(output, "reachable.json"), graphBytes);
            report["graph"] = new { knownObjects = known.Length, edges = edges.Length, reachable = reachable.Length, serializedBytes = graphBytes.Length, sha256 = Hash(graphBytes) };
            RefuseGraph("incomplete or truncated", () => LibraryReferenceReachability.RequireReachable(known, [known[0]], edges, [known[999]], false));
            var cycle = edges.Concat(new[] { new LibraryReferenceEdge(known[5000], known[5001]), new LibraryReferenceEdge(known[5001], known[5000]) }).ToArray();
            RefuseGraph("lack a trusted-root", () => LibraryReferenceReachability.RequireReachable(known, [known[0]], cycle, [known[5000]], true));
            RefuseGraph("object limit", () => LibraryReferenceReachability.RequireReachable(Enumerable.Range(0, 50001).Select(i => "/blocks/" + i), ["/blocks/0"], [], [], true));
            RefuseGraph("edge limit", () => LibraryReferenceReachability.RequireReachable(known, [known[0]], Enumerable.Repeat(edges[0], 200001), [], true));
            report["graphRefusals"] = new[] { "incomplete", "isolated cycle required use", "50001 objects", "200001 edges" };

            // Generated source bytes are deliberately not presented as Siemens/vendor contents.
            byte[] source = Encoding.UTF8.GetBytes("// generated scale fixture\n" + new string('x', 1000));
            byte[] inner = Zip(Enumerable.Range(0, 10000).Select(i => ($"source/Block_{i:D5}.scl", source)));
            byte[] middle = Zip(new[] { ("inner.zip", inner) });
            byte[] archive = Zip(new[] { ("middle.zip", middle) });
            string input = Path.Combine(output, "generated-three-level.zip"); File.WriteAllBytes(input, archive);
            var store = new PackageStore(Path.Combine(output, "store"), new PackageIntakeLimits { MaxPackages = 1 });
            var staged = Measure("stage10000SourcesThreeArchiveLevels", () => store.Stage(hostPath: input));
            Require(staged.State == "ready" && staged.Candidates == 10000 && staged.Files == 10003, "Staging silently truncated the package.");
            string manifestCopy = Path.Combine(output, "manifest.json"); File.Copy(staged.ManifestPath, manifestCopy);
            var manifestBytes = File.ReadAllBytes(manifestCopy);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestBytes, BridgeProtocol.Json)!;
            Require(manifest.Files.Count(f => f.Role != "original" && f.RelativePath.EndsWith(".scl", StringComparison.Ordinal) && f.Sha256 == Hash(source) && f.Bytes == source.Length) == 10000, "Extracted source hash/count mismatch.");
            var ids = Measure("paginateAll10000Candidates", () => {
                var seen = new HashSet<string>(StringComparer.Ordinal); int offset = 0; int pages = 0;
                while (true)
                {
                    var page = store.Inspect(staged.PackageId, count: 200, offset: offset); pages++;
                    Require(page.Total == 10000 && page.Offset == offset, "Pagination totals changed.");
                    foreach (PackageCandidate candidate in page.Items)
                        Require(seen.Add(candidate.CandidateId) && !candidate.ReadyForNativeInspection && candidate.Kind == "sourceFile", "Duplicate candidate or unsupported native readiness.");
                    if (page.NextOffset is not int next) break;
                    Require(next == offset + page.Items.Length && next > offset, "Invalid continuation offset."); offset = next;
                }
                Require(seen.Count == 10000 && pages == 50, "Pagination omitted candidates."); return seen.Count;
            });
            Refuse("InvalidRequest", () => store.Inspect(staged.PackageId, count: 201));
            Refuse("PackageLimitExceeded", () => store.Stage(hostPath: input));
            Measure("retainAndRefuseRelease", () => { store.Retain(staged.PackageId, "scale-evidence"); Refuse("PackageInUse", () => store.Release(staged.PackageId)); return true; });
            Require(Hash(File.ReadAllBytes(staged.ManifestPath)) == Hash(manifestBytes), "Retention changed the inventory manifest.");
            Measure("unretainAndRelease", () => { store.Unretain(staged.PackageId, "scale-evidence"); store.Release(staged.PackageId); return true; });
            Require(!File.Exists(staged.ManifestPath), "Explicit release did not release the owned package.");
            Require(Hash(File.ReadAllBytes(input)) == Hash(archive), "Package staging/release changed its source.");
            report["package"] = new { sources = ids, depth = 3, staged.Files, staged.InputBytes, staged.ExpandedBytes,
                manifestBytes = manifestBytes.Length, manifestSha256 = Hash(manifestBytes), inputSha256 = Hash(archive), sourceUnchanged = true,
                retentionRefusalPassed = true, packageCountQuotaPassed = true, explicitReleasePassed = true, fullPaginationPassed = true };

            string small = Path.Combine(output, "small.scl"); File.WriteAllBytes(small, new byte[3000]);
            var replacement = store.Stage(hostPath: small); // Release makes the one-package quota available again.
            Require(replacement.State == "ready", "Release did not free package count quota.");
            var quotaStore = new PackageStore(Path.Combine(output, "quota-store"), new PackageIntakeLimits { MaxInputBytes = 4096, MaxStoredBytes = 4096 });
            _ = quotaStore.Stage(hostPath: small);
            Refuse("PackageLimitExceeded", () => quotaStore.Stage(hostPath: small));
            string deep = Path.Combine(output, "generated-four-level.zip"); File.WriteAllBytes(deep, Zip(new[] { ("outer.zip", archive) }));
            Refuse("PackageLimitExceeded", () => new PackageStore(Path.Combine(output, "depth-store")).Stage(hostPath: deep));
            Refuse("PackageLimitExceeded", () => new PackageStore(Path.Combine(output, "entry-store"), new PackageIntakeLimits { MaxEntries = 10002 }).Stage(hostPath: input));
            report["packageRefusals"] = new[] { "page size 201", "retained release", "package count quota", "stored byte quota", "depth 4", "10003 entries with limit 10002" };
            report["completed"] = true;
        }
        catch (Exception error) { report["failure"] = error.ToString(); }
        Save(); Console.WriteLine(JsonSerializer.Serialize(report));
        return (bool)report["completed"] ? 0 : 1;
    }

    private static byte[] Zip(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in files) { using var stream = archive.CreateEntry(name).Open(); stream.Write(bytes); }
        return output.ToArray();
    }
}
