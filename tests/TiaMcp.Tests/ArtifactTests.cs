using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static partial class Program
{
    private static Task JournalReplacementRetry()
    {
        var transient = new IOException("old file still open", unchecked((int)0x80070497));
        int calls = 0;
        var delays = new List<int>();
        JournalFileReplacement.Replace(() => { if (++calls < 3) throw transient; }, delays.Add);
        Check.Equal(3, calls);
        Check.Equal("10,20", string.Join(",", delays));

        // A reader without delete sharing makes File.Replace fail with a sharing or lock violation before anything moves.
        var sharing = new IOException("sharing", unchecked((int)0x80070020));
        var locked = new IOException("lock", unchecked((int)0x80070021));
        foreach (var retried in new[] { sharing, locked })
        {
            calls = 0; delays.Clear();
            JournalFileReplacement.Replace(() => { if (++calls < 5) throw retried; }, delays.Add);
            Check.Equal(5, calls);
            Check.Equal("10,20,30,40", string.Join(",", delays));
        }

        foreach (var failure in new Exception[] { transient, sharing, locked,
            new IOException("rename failed", unchecked((int)0x80070498)),
            new IOException("partial replacement", unchecked((int)0x80070499)),
            new UnauthorizedAccessException(), new InvalidOperationException() })
        {
            calls = 0; delays.Clear();
            Exception? caught = null;
            try { JournalFileReplacement.Replace(() => { calls++; throw failure; }, delays.Add); }
            catch (Exception error) { caught = error; }
            Check.Equal(true, ReferenceEquals(failure, caught));
            bool retryable = failure is IOException io && (io.HResult == unchecked((int)0x80070497) || io.HResult == unchecked((int)0x80070020) || io.HResult == unchecked((int)0x80070021));
            Check.Equal(retryable ? 20 : 1, calls);
            Check.Equal(retryable ? 1450 : 0, delays.Sum());
        }

        calls = 0;
        var interrupted = new OperationCanceledException();
        try
        {
            JournalFileReplacement.Replace(() => { calls++; throw sharing; }, _ => throw interrupted);
            throw new Exception("Interrupted delay must propagate.");
        }
        catch (OperationCanceledException error) { Check.Equal(true, ReferenceEquals(interrupted, error)); }
        Check.Equal(1, calls);

        // Real file: a reader holding the journal without delete sharing for a moment no longer fails the replacement.
        var root = Path.Combine(Path.GetTempPath(), "tia-journal-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string journal = Path.Combine(root, "journal.json"), next = journal + ".new";
            File.WriteAllText(journal, "old"); File.WriteAllText(next, "new");
            var reader = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Task.Delay(150).ContinueWith(_ => reader.Dispose());
            JournalFileReplacement.Replace(next, journal);
            release.Wait();
            Check.Equal("new", File.ReadAllText(journal));
            Check.Equal(false, File.Exists(next));
        }
        finally { Directory.Delete(root, true); }
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

    private static Task ArtifactDigestReuse()
    {
        var root = Path.Combine(Path.GetTempPath(), "tia-artifact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "export.xml");
            byte[] first = new byte[70000], second = new byte[70000], third = new byte[70000];
            Array.Fill(first, (byte)1); Array.Fill(second, (byte)2); Array.Fill(third, (byte)3);
            string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
            string Read(long offset) => JsonSerializer.SerializeToElement(new ArtifactStore(root).Read("export.xml", offset)).GetProperty("sha256").GetString()!;
            File.WriteAllBytes(path, first);
            Check.Equal(Sha(first), Read(0));
            // Same length and restored write time: the continuation chunk reuses the sequence digest instead of rehashing.
            var written = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, second); File.SetLastWriteTimeUtc(path, written);
            Check.Equal(Sha(first), Read(65536));
            // A new sequence always hashes, and any change of length or write time rehashes a continuation.
            Check.Equal(Sha(second), Read(0));
            File.WriteAllBytes(path, third); File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
            Check.Equal(Sha(third), Read(65536));
            File.WriteAllBytes(path, first[..69999]); File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
            Check.Equal(Sha(first[..69999]), Read(65536));
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
