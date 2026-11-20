using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static class StoreRetentionTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tia-retention-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static Task PublishContentLeavesNoTemporaryFiles()
    {
        var root = NewRoot();
        try
        {
            string folder = Path.Combine(root, "retained");
            Directory.CreateDirectory(folder);
            // Residue of a crashed publication: under the writer lock nothing is in flight, so it is swept and does not count against the cap.
            File.WriteAllBytes(Path.Combine(folder, "stale.tmp"), new byte[4096]);
            byte[] content = Encoding.UTF8.GetBytes("retained content");
            var artifact = PackageStorage.PublishContent(root, "retained", content, ".bin", 1024, 1024, "Test");
            Check.Equal(Hex(content), artifact["sha256"]!.GetValue<string>());
            Check.Equal(0, Directory.EnumerateFiles(folder, "*.tmp").Count());

            // A failed publication removes its own temporary file: the destination name is taken by a directory.
            byte[] blocked = Encoding.UTF8.GetBytes("blocked content");
            Directory.CreateDirectory(Path.Combine(folder, Hex(blocked) + ".bin"));
            Check.Equal(true, Throws(() => PackageStorage.PublishContent(root, "retained", blocked, ".bin", 1024, 1024, "Test")));
            Check.Equal(0, Directory.EnumerateFiles(folder, "*.tmp").Count());
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (BridgeException) { return true; }
    }

    internal static Task RecordRetentionEvictsByRankThenAge()
    {
        var root = NewRoot();
        try
        {
            string Make(string name, int ageDays)
            {
                string path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "x.json"), "{}");
                Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
                return path;
            }
            string oldComplete = Make("a", 10), incomplete = Make("b", 1), kept = Make("c", 20);
            int Rank(string directory) => directory == incomplete ? 0 : directory == kept ? -1 : 1;
            var full = () => new BridgeException("Full", "Full.");

            // Below the cap nothing is touched; at the cap the incomplete record goes first although it is the newest.
            RecordRetention.MakeRoom(root, 4, Rank, full);
            Check.Equal(3, Directory.EnumerateDirectories(root).Count());
            RecordRetention.MakeRoom(root, 3, Rank, full);
            Check.Equal(false, Directory.Exists(incomplete));
            Check.Equal(true, Directory.Exists(oldComplete) && Directory.Exists(kept));
            // Then the oldest evictable one; a record that is kept makes a full store refuse.
            RecordRetention.MakeRoom(root, 2, Rank, full);
            Check.Equal(false, Directory.Exists(oldComplete));
            Check.Equal(true, Throws(() => RecordRetention.MakeRoom(root, 1, Rank, full)));
            Check.Equal(true, Directory.Exists(kept));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task ExportRetentionPrunesOnlyOldBlockExports()
    {
        var root = NewRoot();
        try
        {
            var now = DateTime.UtcNow;
            string Make(string name, TimeSpan age, int bytes = 10)
            {
                string path = Path.Combine(root, name);
                File.WriteAllBytes(path, new byte[bytes]);
                File.SetLastWriteTimeUtc(path, now - age);
                return path;
            }
            string oldXml = Make("block-" + Guid.NewGuid().ToString("N") + ".xml", TimeSpan.FromDays(30)),
                oldIndex = Make("block-export-" + Guid.NewGuid().ToString("N") + ".json", TimeSpan.FromDays(30)),
                recentTxt = Make("block-" + Guid.NewGuid().ToString("N") + ".txt", TimeSpan.FromHours(3)),
                justWritten = Make("block-" + Guid.NewGuid().ToString("N") + ".xml", TimeSpan.FromMinutes(1)),
                other = Make("cax-export.aml", TimeSpan.FromDays(60));
            ExportRetention.Prune(root, TimeSpan.FromDays(14), 1L << 30, now, throttle: false);
            Check.Equal(false, File.Exists(oldXml) || File.Exists(oldIndex));
            Check.Equal(true, File.Exists(recentTxt) && File.Exists(justWritten) && File.Exists(other));

            // Over the size bound the oldest go first, but never a file younger than an hour.
            string older = Make("block-" + Guid.NewGuid().ToString("N") + ".xml", TimeSpan.FromDays(2), 100);
            ExportRetention.Prune(root, TimeSpan.FromDays(14), 105, now, throttle: false);
            Check.Equal(false, File.Exists(older));
            Check.Equal(true, File.Exists(recentTxt) && File.Exists(justWritten));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task ArtifactResolveCheckedDoesNotOpenTheFile()
    {
        var root = NewRoot();
        try
        {
            var store = new ArtifactStore(root);
            string path = Path.Combine(root, "exports", "a.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[8]);
            Check.Equal(Path.GetFullPath(path), store.ResolveChecked("exports/a.xml"));
            Check.Equal("exports" + Path.DirectorySeparatorChar + "a.xml", store.Relative(store.ResolveChecked("exports/a.xml")));
            // An exclusive lock would fail a hashing read, but the path check does not open the file.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Check.Equal(Path.GetFullPath(path), store.ResolveChecked("exports/a.xml"));
            Check.Equal("ArtifactUnavailable", ThrowsCode(() => store.ResolveChecked("exports/missing.xml")));
            Check.Equal("ArtifactPathDenied", ThrowsCode(() => store.ResolveChecked("../outside.xml")));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    private static string ThrowsCode(Action action)
    {
        try { action(); }
        catch (BridgeException error) { return error.Code; }
        return "";
    }

    private static string Aml(int index) =>
        "<CAEXFile FileName=\"synthetic.aml\" SchemaVersion=\"2.15\"><InstanceHierarchy Name=\"Plant" + index + "\" ID=\"plant-" + index + "\">" +
        "<InternalElement Name=\"Device\" ID=\"device-" + index + "\"><Attribute Name=\"Address\"><Value>" + index + "</Value></Attribute></InternalElement>" +
        "</InstanceHierarchy></CAEXFile>";

    internal static Task AutomationMlStoreEvictsAtTheRecordCap()
    {
        var root = NewRoot();
        try
        {
            var store = new AutomationMlWorkflowStore(root);
            string source = Path.Combine(root, "source.aml");
            string? first = null, last = null;
            for (int i = 0; i < 130; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Aml(i));
                File.WriteAllBytes(source, bytes);
                var result = JsonSerializer.SerializeToElement(store.InspectFile(source, Hex(bytes)), BridgeProtocol.Json);
                last = result.GetProperty("recordRef").GetString()!;
                first ??= last;
            }
            // Before this change the 129th record was refused for good; now the oldest unreferenced one makes room.
            Check.Equal(128, Directory.EnumerateDirectories(Path.Combine(root, "automationml-workflow")).Count());
            Check.Equal(true, Throws(() => store.Reopen(first!)));
            store.Reopen(last!);
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task AutomationMlChunkedReadsVerifyOnceAndStillCatchChanges()
    {
        var root = NewRoot();
        try
        {
            var store = new AutomationMlWorkflowStore(root);
            var text = new StringBuilder("<CAEXFile FileName=\"synthetic.aml\" SchemaVersion=\"2.15\"><InstanceHierarchy Name=\"Plant\" ID=\"plant\">");
            for (int i = 0; i < 6000; i++) text.Append("<InternalElement Name=\"Device").Append(i).Append("\" ID=\"d").Append(i).Append("\"/>");
            text.Append("</InstanceHierarchy></CAEXFile>");
            byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
            Check.Equal(true, bytes.Length > 3 * 65536);
            string source = Path.Combine(root, "source.aml");
            File.WriteAllBytes(source, bytes);
            var record = JsonSerializer.SerializeToElement(store.InspectFile(source, Hex(bytes)), BridgeProtocol.Json);
            string reference = record.GetProperty("recordRef").GetString()!;

            using var buffer = new MemoryStream();
            long offset = 0; bool complete = false;
            while (!complete)
            {
                var chunk = JsonSerializer.SerializeToElement(store.ReadArtifact(reference, "original.aml", offset), BridgeProtocol.Json);
                buffer.Write(Convert.FromBase64String(chunk.GetProperty("data").GetString()!));
                offset = chunk.GetProperty("nextOffset").GetInt64(); complete = chunk.GetProperty("complete").GetBoolean();
            }
            Check.Equal(Hex(bytes), Hex(buffer.ToArray()));

            // A continuation does not reload the record, but a file replaced after the first chunk is still refused.
            string stored = Directory.EnumerateFiles(Path.Combine(root, "automationml-workflow"), "original.aml", SearchOption.AllDirectories).Single();
            store.ReadArtifact(reference, "original.aml", 0);
            var written = File.GetLastWriteTimeUtc(stored);
            byte[] changed = (byte[])bytes.Clone(); changed[^2] ^= 1;
            File.WriteAllBytes(stored, changed); File.SetLastWriteTimeUtc(stored, written.AddSeconds(2));
            Check.Equal("AutomationMlIntegrityFailure", ThrowsCode(() => store.ReadArtifact(reference, "original.aml", 65536)));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task AutomationMlReportPagesReuseTheParsedReport()
    {
        var root = NewRoot();
        try
        {
            var store = new AutomationMlWorkflowStore(root);
            byte[] bytes = Encoding.UTF8.GetBytes(Aml(1));
            string source = Path.Combine(root, "source.aml");
            File.WriteAllBytes(source, bytes);
            string reference = JsonSerializer.SerializeToElement(store.InspectFile(source, Hex(bytes)), BridgeProtocol.Json).GetProperty("recordRef").GetString()!;
            var page = JsonSerializer.SerializeToElement(store.ReportPage(reference, "/objects", 0, 1), BridgeProtocol.Json);
            Check.Equal(true, page.GetProperty("total").GetInt32() >= 1);
            var again = JsonSerializer.SerializeToElement(store.ReportPage(reference, "/objects", 0, 1), BridgeProtocol.Json);
            Check.Equal(page.GetRawText(), again.GetRawText());
            // A replaced report is not served from the parsed copy.
            string report = Directory.EnumerateFiles(Path.Combine(root, "automationml-workflow"), "report.json", SearchOption.AllDirectories).Single();
            var written = File.GetLastWriteTimeUtc(report);
            File.WriteAllText(report, "{\"objects\":[]}"); File.SetLastWriteTimeUtc(report, written.AddSeconds(2));
            Check.Equal("AutomationMlIntegrityFailure", ThrowsCode(() => store.ReportPage(reference, "/objects", 0, 1)));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
