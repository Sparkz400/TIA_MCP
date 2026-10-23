using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static partial class Program
{
    private static Task JournalReplacementRetry()
    {
        var transient = new IOException("old file still open", unchecked((int)0x80070497));
        int calls = 0, delays = 0;
        JournalFileReplacement.Replace(() => { if (++calls < 3) throw transient; },
            milliseconds => { Check.Equal(10, milliseconds); delays++; });
        Check.Equal(3, calls);
        Check.Equal(2, delays);

        foreach (var failure in new Exception[] { transient,
            new IOException("rename failed", unchecked((int)0x80070498)),
            new IOException("partial replacement", unchecked((int)0x80070499)),
            new IOException("sharing", unchecked((int)0x80070020)),
            new IOException("lock", unchecked((int)0x80070021)),
            new UnauthorizedAccessException(), new InvalidOperationException() })
        {
            calls = delays = 0;
            Exception? caught = null;
            try { JournalFileReplacement.Replace(() => { calls++; throw failure; }, _ => delays++); }
            catch (Exception error) { caught = error; }
            Check.Equal(true, ReferenceEquals(failure, caught));
            Check.Equal(ReferenceEquals(failure, transient) ? 10 : 1, calls);
            Check.Equal(ReferenceEquals(failure, transient) ? 9 : 0, delays);
        }

        calls = 0;
        var interrupted = new OperationCanceledException();
        try
        {
            JournalFileReplacement.Replace(() => { calls++; throw transient; }, _ => throw interrupted);
            throw new Exception("Interrupted delay must propagate.");
        }
        catch (OperationCanceledException error) { Check.Equal(true, ReferenceEquals(interrupted, error)); }
        Check.Equal(1, calls);
        return Task.CompletedTask;
    }

    private static Task ArtifactTransfer()
    {
        var root = Path.Combine(Path.GetTempPath(), "tia-artifact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Enumerable.Range(0, 70000).Select(i => (byte)(i % 255)).ToArray();
            File.WriteAllBytes(Path.Combine(root, "export.xml"), data);
            var store = new ArtifactStore(root);
            var first = JsonSerializer.SerializeToElement(store.Read("export.xml"));
            Check.Equal(65536, first.GetProperty("count").GetInt32());
            Check.Equal(false, first.GetProperty("complete").GetBoolean());
            Check.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), first.GetProperty("sha256").GetString());
            var last = JsonSerializer.SerializeToElement(store.Read("export.xml", 65536));
            Check.Equal(true, last.GetProperty("complete").GetBoolean());
            Check.Equal(4464, Convert.FromBase64String(last.GetProperty("data").GetString()!).Length);
            var empty = JsonSerializer.SerializeToElement(store.Read("export.xml", data.Length));
            Check.Equal(0, empty.GetProperty("count").GetInt32());
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    private static async Task ArtifactBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "tia-artifact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "data.xml"), "fixture");
            var store = new ArtifactStore(root);
            foreach (var path in new[] { "../data.xml", root + "-sibling/data.xml", root })
                await Check.BridgeError("ArtifactPathDenied", () => { store.Read(path); return Task.CompletedTask; });
            foreach (var range in new[] { (-1L, 1), (0L, 0), (0L, 65537), (8L, 1) })
                await Check.BridgeError("InvalidRequest", () => { store.Read("data.xml", range.Item1, range.Item2); return Task.CompletedTask; });
            foreach (var missing in new[] { "missing.json", "missing-directory/journal.json" })
                await Check.BridgeError("ArtifactUnavailable", () => { store.Read(missing); return Task.CompletedTask; });
            if (OperatingSystem.IsWindows())
            {
                using var locked = new FileStream(Path.Combine(root, "data.xml"), FileMode.Open, FileAccess.Read, FileShare.None);
                await Check.BridgeError("ArtifactUnavailable", () => { store.Read("data.xml"); return Task.CompletedTask; });
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task ArtifactJournalReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "tia-artifact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "journal.json");
        var records = new[] { new Newtonsoft.Json.Linq.JObject { ["data"] = new string('a', 4096) },
            new Newtonsoft.Json.Linq.JObject { ["data"] = new string('b', 8192) } };
        var versions = records.Select(record => record.ToString(Newtonsoft.Json.Formatting.Indented)).ToArray();
        File.WriteAllText(path, versions[0]);
        Task writer = Task.CompletedTask;
        try
        {
            writer = Task.Run(() => {
                for (int index = 0; index < 300; index++)
                {
                    TiaMcp.Openness.V21.LibraryJournalStore.Write(path, records[index % 2]);
                }
            });
            var store = new ArtifactStore(root);
            int reads = 0;
            for (int index = 0; index < 600; index++)
            {
                try
                {
                    var result = JsonSerializer.SerializeToElement(store.Read(path));
                    var bytes = Convert.FromBase64String(result.GetProperty("data").GetString()!);
                    Check.Equal(true, versions.Contains(System.Text.Encoding.UTF8.GetString(bytes)));
                    Check.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.GetProperty("sha256").GetString());
                    reads++;
                }
                catch (BridgeException error) when (error.Code == "ArtifactUnavailable") { }
            }
            await writer;
            Check.Equal(true, reads > 0);
        }
        finally { try { await writer; } finally { Directory.Delete(root, true); } }
    }
}
