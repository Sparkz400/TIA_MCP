using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.AutomationMl;

namespace TiaMcp.Core;

/// <summary>Immutable offline AutomationML documents and reports. No native worker or project mutation.</summary>
public sealed class AutomationMlWorkflowStore
{
    private sealed record Artifact(string Name, int Bytes, string Sha256);
    private sealed record Receipt(int SchemaVersion, string Id, string Kind, string[] Inputs, Artifact[] Artifacts);
    private sealed record Record(string Reference, Receipt Receipt, Dictionary<string, byte[]> Files);
    private const int PageBytes = 128 << 10, MaxRecords = 128;
    private const long StoreBytes = 2L << 30;
    private readonly string root;
    private readonly ArtifactUploadStore uploads;
    public AutomationMlWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits = null)
    {
        root = Path.GetFullPath(Path.Combine(artifactsDirectory, "automationml-workflow"));
        PackageStorage.EnsureNoLinks(root); Directory.CreateDirectory(root);
        uploads = new(artifactsDirectory, uploadLimits);
    }
    public object Inspect(string xmlUploadRef)
    { var upload = uploads.ResolveCommitted(xmlUploadRef); return InspectFile(upload.Path, upload.Sha256); }
    /// <summary>Publishes a SHA-256-pinned file, such as a native CAx export inside the artifact root, as a document record.</summary>
    public object InspectFile(string path, string sha256)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var bytes = Read(path, AutomationMlDocument.MaxInputBytes);
        Require(Hash(bytes) == sha256, "AutomationMlInputChanged", "Input bytes changed during reading.");
        var document = AutomationMlDocument.Parse(bytes);
        return Summary(Publish("document", [], new() { ["original.aml"] = bytes,
            ["report.json"] = Encoding.UTF8.GetBytes(document.ToJson()) }));
    }
    public object Compare(string beforeRef, string afterRef)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var before = Document(beforeRef); var after = Document(afterRef);
        var report = AutomationMlDocument.Parse(before.Files["original.aml"]).CompareTo(AutomationMlDocument.Parse(after.Files["original.aml"]));
        return Summary(Publish("comparison", [beforeRef, afterRef], new() { ["report.json"] = Encoding.UTF8.GetBytes(AutomationMlDocument.Json(report)) }));
    }
    public object Reopen(string recordRef) => Summary(Load(recordRef));
    public object ReportPage(string recordRef, string section, int offset = 0, int count = 50)
    {
        Require(count is > 0 and <= 200 && offset >= 0 && section is { Length: > 0 and <= 1024 } && section.StartsWith('/'),
            "AutomationMlPageLimit", "Use an exact array pointer and 1..200 rows, within a 128 KiB response budget.");
        var (receipt, id) = LoadReceipt(recordRef);
        // A page needs only the stored report: its hash is checked against the receipt (once per file version, not per page).
        var reportArtifact = receipt.Artifacts.Single(a => a.Name == "report.json"); var entry = ReportOf(id, reportArtifact);
        lock (entry)
        {
            JsonNode? node = entry.Node;
            var parts = section[1..].Split('/');
            Require(parts.Length <= 12, "AutomationMlInvalidSection", "Array pointer is too deep.");
            foreach (var part in parts)
            {
                if (node is JsonObject obj) node = obj[part];
                else if (node is JsonArray array && int.TryParse(part, out int index) && index >= 0 && index < array.Count && index.ToString(CultureInfo.InvariantCulture) == part) node = array[index];
                else throw new BridgeException("AutomationMlInvalidSection", "Use an exact report array pointer, such as /objects.");
            }
            Require(node is JsonArray, "AutomationMlInvalidSection", "Selected report value is not an array.");
            var source = (JsonArray)node; Require(offset <= source.Count, "AutomationMlPageLimit", "Offset exceeds this array.");
            var (rows, next) = ReportPageRows.Read(source, offset, count, PageBytes - 4096, "Row exceeds page budget; download report.json.");
            var result = new { recordRef, reportSha256 = reportArtifact.Sha256,
                section, offset, total = source.Count, rows, nextOffset = next == source.Count ? (int?)null : next, nativeQualified = false };
            Require(JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length <= PageBytes,
                "AutomationMlPageLimit", "Encoded page exceeds 128 KiB; reduce count or download report.json.");
            return result;
        }
    }
    private sealed record ReportEntry(JsonNode Node, long Length, DateTime Written);
    // Parsed reports are immutable (write-once, hash-pinned); a changed length or write time is re-verified from the file.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReportEntry> Reports = new(StringComparer.Ordinal);
    private ReportEntry ReportOf(string id, Artifact artifact)
    {
        string path = PathFor(id, artifact.Name), key = path + "|" + artifact.Sha256; var info = new FileInfo(path);
        if (Reports.TryGetValue(key, out var hit) && info.Exists && info.Length == hit.Length && info.LastWriteTimeUtc == hit.Written) return hit;
        var bytes = Read(path, AutomationMlDocument.MaxReportBytes);
        Require(bytes.Length == artifact.Bytes && Hash(bytes) == artifact.Sha256, "AutomationMlIntegrityFailure", "Retained artifact changed.");
        var entry = new ReportEntry(JsonNode.Parse(bytes)!, info.Length, info.LastWriteTimeUtc);
        if (Reports.Count >= 4) Reports.Clear(); // shortcut: crude bound, reports reach 64 MiB; use an LRU if many records are paged at once.
        Reports[key] = entry; return entry;
    }
    public object ReadArtifact(string recordRef, string name, long offset = 0, int count = 65536)
    {
        // The whole record verifies once, at offset 0; later chunks check the receipt and use ArtifactStore's digest of the one file.
        var receipt = offset == 0 ? Load(recordRef).Receipt : LoadReceipt(recordRef).Receipt;
        Artifact? artifact = name == "receipt.json" ? new(name, Read(PathFor(receipt.Id, name), 65536).Length, recordRef.Split(':')[2])
            : receipt.Artifacts.SingleOrDefault(a => a.Name == name);
        Require(artifact is not null, "AutomationMlArtifactMissing", "Use an artifact name returned by this record.");
        var result = JsonSerializer.SerializeToElement(new ArtifactStore(root).Read(PathFor(receipt.Id, name), offset, count), BridgeProtocol.Json);
        Require(result.GetProperty("sha256").GetString() == artifact.Sha256 && result.GetProperty("bytes").GetInt64() == artifact.Bytes,
            "AutomationMlIntegrityFailure", "Artifact changed during transfer.");
        return new { sha256 = artifact.Sha256, bytes = artifact.Bytes, offset, count = result.GetProperty("count").GetInt32(),
            encoding = "base64", data = result.GetProperty("data").GetString(), nextOffset = result.GetProperty("nextOffset").GetInt64(),
            complete = result.GetProperty("complete").GetBoolean(), nativeQualified = false };
    }
    public object List(int offset = 0, int count = 20)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var directories = Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).Take(MaxRecords + 1).ToArray();
        Require(directories.Length <= MaxRecords && offset >= 0 && offset <= directories.Length && count is > 0 and <= 100,
            "AutomationMlPageLimit", "List 1..100 records within the retained inventory.");
        var rows = directories.Skip(offset).Take(count).Select(directory =>
        {
            PackageStorage.EnsureNoLinks(directory); string id = PackageStorage.Id(Path.GetFileName(directory)); string? reference = null; string state = "incomplete";
            try
            {
                string path = PathFor(id, "receipt.json");
                if (File.Exists(path)) { reference = "automationml:" + id + ":" + Hash(Read(path, 65536)); _ = Load(reference); state = "complete"; }
            }
            catch (Exception error) when (error is BridgeException or IOException or JsonException or UnauthorizedAccessException) { state = "integrity-failed"; }
            return new { id, recordRef = reference, state };
        }).ToArray();
        return new { rows, total = directories.Length, nextOffset = offset + rows.Length == directories.Length ? (int?)null : offset + rows.Length,
            ordering = "record-id; relist after additions", nativeQualified = false };
    }
    /// <summary>The verified original.aml of a retained document record, for a native import that reads by path.</summary>
    public (string Path, string Sha256) ResolveDocument(string reference)
    { var record = Document(reference); return (PathFor(record.Receipt.Id, "original.aml"), record.Receipt.Artifacts.Single(a => a.Name == "original.aml").Sha256); }
    internal byte[] ReadVerifiedDocument(string reference, Action<int>? accountBytes = null)
    { var record = Document(reference); foreach (var bytes in record.Files.Values) accountBytes?.Invoke(bytes.Length); return record.Files["original.aml"]; }
    private Record Document(string reference)
    { var record = Load(reference); Require(record.Receipt.Kind == "document", "AutomationMlInvalidReference", "Comparison inputs must be retained documents."); return record; }
    private Record Load(string reference, int depth = 0)
    {
        var (receipt, id) = LoadReceipt(reference, depth);
        var files = new Dictionary<string, byte[]>();
        foreach (var artifact in receipt.Artifacts)
        {
            var bytes = Read(PathFor(id, artifact.Name), artifact.Name == "original.aml" ? AutomationMlDocument.MaxInputBytes : AutomationMlDocument.MaxReportBytes);
            Require(bytes.Length == artifact.Bytes && Hash(bytes) == artifact.Sha256, "AutomationMlIntegrityFailure", "Retained artifact changed."); files.Add(artifact.Name, bytes);
        }
        foreach (string input in receipt.Inputs)
            Require(Load(input, depth + 1).Receipt.Kind == "document", "AutomationMlIntegrityFailure", "Comparison input is not a retained document.");
        return new(reference, receipt, files);
    }
    /// <summary>The receipt of a handle, verified against its digest and intent and checked for shape; no artifact or input is read.</summary>
    private (Receipt Receipt, string Id) LoadReceipt(string reference, int depth = 0)
    {
        Require(reference is { Length: 110 } && depth <= 1, "AutomationMlInvalidReference", "Use an exact Host-issued AutomationML record handle.");
        var parts = reference.Split(':'); Require(parts.Length == 3 && parts[0] == "automationml", "AutomationMlInvalidReference", "Wrong record handle.");
        string id = PackageStorage.Id(parts[1]), hash = PackageStorage.Hash(parts[2]);
        Require(parts[2] == hash, "AutomationMlInvalidReference", "Record digests use canonical lowercase hex.");
        byte[] receiptBytes;
        try { receiptBytes = Read(PathFor(id, "receipt.json"), 65536); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new BridgeException("AutomationMlRecordNotFound", "The retained record does not exist; storage evicts the oldest unreferenced records at 128. Inspect the document again."); }
        Require(Hash(receiptBytes) == hash && Read(PathFor(id, "intent.json"), 65536).AsSpan().SequenceEqual(receiptBytes), "AutomationMlIntegrityFailure", "Retained receipt changed.");
        var receipt = JsonSerializer.Deserialize<Receipt>(receiptBytes, BridgeProtocol.Json);
        Require(receipt is not null && receipt.SchemaVersion == 1 && receipt.Id == id && receipt.Kind is "document" or "comparison" && receipt.Inputs is not null,
            "AutomationMlIntegrityFailure", "Invalid retained record.");
        var names = receipt.Kind == "document" ? new[] { "original.aml", "report.json" } : ["report.json"];
        Require(receipt.Artifacts is not null && receipt.Artifacts.All(a => a is not null) &&
            receipt.Artifacts.Select(a => a.Name).Order().SequenceEqual(names.Order()) && receipt.Inputs.Length == (receipt.Kind == "document" ? 0 : 2),
            "AutomationMlIntegrityFailure", "Unexpected retained artifacts or inputs.");
        return (receipt, id);
    }
    private Record Publish(string kind, string[] inputs, Dictionary<string, byte[]> files)
    {
        Require(files.All(f => f.Value.Length <= AutomationMlDocument.MaxReportBytes), "AutomationMlCapacity", "An output exceeds 64 MiB.");
        long additional = files.Values.Sum(b => (long)b.Length) + 131072; long stored = 0; int entries = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PackageStorage.EnsureNoLinks(path); Require(++entries <= 10000, "AutomationMlCapacity", "Storage enumeration exceeded its bound.");
            if (Directory.Exists(path)) pending.Push(path); else stored += new FileInfo(path).Length;
            Require(stored + additional <= StoreBytes, "AutomationMlCapacity", "AutomationML storage exceeds 2 GiB.");
        }
        // At the cap the oldest incomplete, comparison or unreferenced document record is evicted; a document that a retained
        // comparison or composite review names stays, so a store full of referenced records still refuses.
        RecordRetention.MakeRoom(root, MaxRecords, Evictable, () => new BridgeException("AutomationMlCapacity", "AutomationML storage retains at most 128 records and every one is referenced."));
        string id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(PathFor(id));
        var receipt = new Receipt(1, id, kind, inputs, files.Select(f => new Artifact(f.Key, f.Value.Length, Hash(f.Value))).ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, BridgeProtocol.Json); Write(PathFor(id, "intent.json"), bytes);
        foreach (var file in files) Write(PathFor(id, file.Key), file.Value);
        foreach (string input in inputs) _ = Document(input);
        Write(PathFor(id, "receipt.json"), bytes); return new("automationml:" + id + ":" + Hash(bytes), receipt, files);
    }
    // Eviction rank: 0 incomplete, 1 comparison, 2 document that nothing retained names; -1 keeps the record.
    private int Evictable(string directory)
    {
        string receipt = Path.Combine(directory, "receipt.json");
        if (!File.Exists(receipt)) return 0;
        try
        {
            var parsed = JsonSerializer.Deserialize<Receipt>(Read(receipt, 65536), BridgeProtocol.Json);
            if (parsed?.Kind == "comparison") return 1;
            string needle = "automationml:" + Path.GetFileName(directory) + ":";
            return !RecordRetention.Mentions(root, "receipt.json", needle, 65536) &&
                !RecordRetention.Mentions(Path.Combine(Path.GetDirectoryName(root)!, "composite-engineering-reviews"), "request.json", needle, 16 << 20) ? 2 : -1;
        }
        catch (Exception error) when (error is BridgeException or IOException or JsonException or UnauthorizedAccessException) { return -1; }
    }
    private static object Summary(Record record)
    {
        var report = JsonNode.Parse(record.Files["report.json"])!.AsObject();
        var counts = report.Where(p => p.Value is JsonArray).ToDictionary(p => p.Key, p => ((JsonArray)p.Value!).Count);
        return new { recordRef = record.Reference, kind = record.Receipt.Kind, state = "complete", inputs = record.Receipt.Inputs,
            nativeQualified = false, importabilityCertified = false, impactScopeComplete = false,
            profile = (string?)report["profile"], schemaValid = (bool?)report["schemaValid"],
            sourceSha256 = (string?)report["sourceSha256"], beforeSha256 = (string?)report["beforeSha256"], afterSha256 = (string?)report["afterSha256"],
            hasErrors = (bool?)report["hasErrors"], matchingReliable = (bool?)report["matchingReliable"],
            sourceBytesChanged = (bool?)report["sourceBytesChanged"], counts, artifacts = record.Receipt.Artifacts, receiptName = "receipt.json" };
    }
    private string PathFor(string id, string? name = null) => PackageStorage.Under(root, name is null ? id : id + "/" + name);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Read(string path, int limit)
    { PackageStorage.EnsureNoLinks(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length <= limit, "AutomationMlCapacity", "Input or retained file exceeds its byte bound."); var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes; }
    private static void Write(string path, byte[] bytes) => PackageStorage.WriteNew(path, bytes);
    private static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    { if (!condition) throw new BridgeException(code, message); }
}
