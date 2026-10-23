using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Core;

namespace TiaMcp.OpcUa;

public sealed record OpcUaReportCursor(string RecordRef, string ReportSha256, string Section, int Offset);

/// <summary>Host-owned, write-once offline NodeSet review. No bridge, native API or network dependency.</summary>
public sealed class OpcUaWorkflowStore
{
    private sealed record Artifact(string Name, int Bytes, string Sha256);
    private sealed record Receipt(int SchemaVersion, string Id, string Kind, DateTimeOffset CreatedUtc,
        string[] Inputs, Artifact[] Artifacts);
    private sealed record Record(string Ref, Receipt Receipt, Dictionary<string, byte[]> Files);
    public const int PageBytes = 128 * 1024;
    public const int MaxRecords = 1000;
    public const long MaxStoredBytes = 2L * 1024 * 1024 * 1024;
    private const int ManifestBytes = 1024 * 1024;
    private readonly string root;
    private readonly ArtifactUploadStore uploads;
    private static readonly string[] Kinds = ["model", "comparison", "dependencies", "dependency-comparison"];
    private static readonly JsonSerializerOptions Json = new(BridgeProtocol.Json)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public OpcUaWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits = null)
    {
        root = Path.Combine(Path.GetFullPath(artifactsDirectory), "opcua-workflow");
        NoLinks(root);
        Directory.CreateDirectory(root);
        uploads = new(artifactsDirectory, uploadLimits);
    }

    public JsonObject Inspect(string nodeSetUploadRef)
    {
        using var gate = Lock();
        byte[] bytes = Uploaded(nodeSetUploadRef, OpcUaModelReview.MaxBytes);
        var report = OpcUaModelReview.Inspect(bytes);
        return Summary(Publish("model", report, new() { ["original.xml"] = bytes }, []));
    }

    public JsonObject Compare(string beforeRef, string afterRef, string? consumerManifestUploadRef = null)
    {
        using var gate = Lock();
        var before = Kind(Load(beforeRef), "model");
        var after = Kind(Load(afterRef), "model");
        var files = new Dictionary<string, byte[]>();
        var consumers = Consumers(consumerManifestUploadRef, files);
        var report = OpcUaModelReview.Compare(before.Files["original.xml"], after.Files["original.xml"], consumers);
        return Summary(Publish("comparison", report, files, [before.Ref, after.Ref]));
    }

    public JsonObject Dependencies(string[] modelRefs)
    {
        using var gate = Lock();
        Require(modelRefs is { Length: > 0 and <= OpcUaDependencyAnalysis.MaxFiles } &&
            modelRefs.Distinct(StringComparer.Ordinal).Count() == modelRefs.Length,
            "OpcUaInvalidSources", "Supply 1..32 distinct Host model handles. Duplicate bytes under distinct handles remain ambiguous definitions.");
        var models = new List<Record>();
        long totalBytes = 0;
        foreach (string reference in modelRefs)
        {
            var model = Kind(Load(reference), "model");
            totalBytes += model.Files["original.xml"].Length;
            Require(totalBytes <= OpcUaDependencyAnalysis.MaxTotalBytes, "OpcUaCapacity", "Explicit model set exceeds 64 MiB.");
            models.Add(model);
        }
        var report = OpcUaDependencyAnalysis.Analyze(Sources(models));
        return Summary(Publish("dependencies", report, new(), models.Select(m => m.Ref).ToArray()));
    }

    public JsonObject CompareDependencies(string beforeSetRef, string afterSetRef, string? consumerManifestUploadRef = null)
    {
        using var gate = Lock();
        var before = Kind(Load(beforeSetRef), "dependencies");
        var after = Kind(Load(afterSetRef), "dependencies");
        var files = new Dictionary<string, byte[]>();
        var consumers = Consumers(consumerManifestUploadRef, files);
        var report = OpcUaDependencyAnalysis.Compare(Sources(before.Receipt.Inputs.Select(Load)),
            Sources(after.Receipt.Inputs.Select(Load)), consumers);
        return Summary(Publish("dependency-comparison", report, files, [before.Ref, after.Ref]));
    }

    public JsonObject Reopen(string recordRef) => Summary(Load(recordRef));

    public JsonObject ReportPage(string recordRef, string section, int pageSize = 100, OpcUaReportCursor? cursor = null)
    {
        Require(pageSize is > 0 and <= 500, "OpcUaPageLimit", "Report pages accept 1..500 rows with a 128 KiB row budget.");
        var record = Load(recordRef);
        string reportHash = record.Receipt.Artifacts.Single(a => a.Name == "report.json").Sha256;
        var sections = Sections(Report(record));
        Require(section is not null && sections.ContainsKey(section), "OpcUaInvalidSection", "Use an exact array section from the record summary.");
        Require(cursor is null || cursor.RecordRef == recordRef && cursor.ReportSha256 == reportHash && cursor.Section == section,
            "OpcUaStaleCursor", "Cursor must bind this exact immutable report and section.");
        int offset = cursor?.Offset ?? 0;
        var all = sections[section];
        Require(offset >= 0 && offset <= all.Count, "OpcUaInvalidCursor", "Cursor offset lies outside this section.");
        var rows = new JsonArray();
        int bytes = 0;
        for (int i = offset; i < all.Count && rows.Count < pageSize; ++i)
        {
            byte[] raw = Bytes(all[i]);
            JsonNode? row = all[i]?.DeepClone();
            if (raw.Length > PageBytes)
                row = new JsonObject { ["omitted"] = true, ["reason"] = "rowExceedsPageByteLimit", ["index"] = i,
                    ["bytes"] = raw.Length, ["sha256"] = Hash(raw), ["completeArtifactRef"] = record.Ref + ":report.json" };
            int length = Bytes(row).Length;
            if (bytes + length > PageBytes) break;
            rows.Add(row); bytes += length;
        }
        int next = offset + rows.Count;
        return new JsonObject { ["recordRef"] = record.Ref, ["reportSha256"] = reportHash, ["section"] = section,
            ["offset"] = offset, ["total"] = all.Count, ["rows"] = rows,
            ["nextCursor"] = next == all.Count ? null : JsonSerializer.SerializeToNode(new OpcUaReportCursor(record.Ref, reportHash, section, next), Json),
            ["completeArtifactRef"] = record.Ref + ":report.json", ["nativeQualified"] = false,
            ["compatibilityCertified"] = false, ["dependencyClosureComplete"] = false };
    }

    public object ReadArtifact(string artifactRef, long offset = 0, int count = 65536)
    {
        var (reference, _, _, name) = ParseRef(artifactRef, true);
        Require(count is > 0 and <= 65536 && offset >= 0, "OpcUaPageLimit", "Artifact reads accept 1..65536 bytes and a nonnegative offset.");
        var record = Load(reference);
        Require(record.Files.TryGetValue(name!, out var bytes), "OpcUaArtifactNotFound", "This artifact is absent from the completed record.");
        Require(offset <= bytes.LongLength, "OpcUaInvalidCursor", "Offset exceeds artifact length.");
        int length = (int)Math.Min(count, bytes.LongLength - offset);
        return new { artifactRef, sha256 = Hash(bytes), bytes = bytes.Length, offset, count = length,
            data = Convert.ToBase64String(bytes, (int)offset, length), nextOffset = offset + length,
            complete = offset + length == bytes.Length, nativeQualified = false };
    }

    public JsonObject List(int offset = 0, int count = 20)
    {
        Require(count is > 0 and <= 100 && offset >= 0, "OpcUaPageLimit", "List accepts 1..100 records and a nonnegative offset.");
        NoLinks(root);
        var directories = Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).ToArray();
        Require(directories.Length <= MaxRecords && offset <= directories.Length, "OpcUaStorageLimit", "Store or list offset exceeds its bound.");
        var rows = new JsonArray();
        int bytes = 0;
        foreach (string directory in directories.Skip(offset).Take(count))
        {
            string id = Path.GetFileName(directory);
            Require(Id(id), "OpcUaIntegrityFailure", "Unexpected directory in the managed OPC UA store.");
            JsonObject row;
            string? reference = null;
            try
            {
                byte[] intent = ReadBounded(PathFor(id, "intent.json"), ManifestBytes);
                reference = "opcua:" + id + ":" + Hash(intent);
                var record = Load(reference);
                row = new JsonObject { ["recordRef"] = reference, ["state"] = "complete", ["kind"] = record.Receipt.Kind,
                    ["createdUtc"] = record.Receipt.CreatedUtc };
            }
            catch (Exception error) when (error is BridgeException or IOException or JsonException or UnauthorizedAccessException)
            {
                row = new JsonObject { ["recordRef"] = reference, ["publicationId"] = id,
                    ["state"] = File.Exists(PathFor(id, "receipt.json")) ? "integrity-failed" : "incomplete",
                    ["errorCode"] = (error as BridgeException)?.Code ?? "OpcUaIntegrityFailure" };
            }
            int length = Bytes(row).Length;
            if (bytes + length > PageBytes) break;
            rows.Add(row); bytes += length;
        }
        return new JsonObject { ["rows"] = rows, ["total"] = directories.Length,
            ["nextOffset"] = offset + rows.Count == directories.Length ? null : offset + rows.Count,
            ["ordering"] = "publication-id; discovery only, relist after concurrent additions", ["nativeQualified"] = false };
    }

    private byte[] Uploaded(string reference, int limit)
    {
        var resolved = uploads.ResolveCommitted(reference);
        byte[] bytes = ReadBounded(resolved.Path, limit);
        Require(Hash(bytes) == resolved.Sha256, "OpcUaIntegrityFailure", "Committed upload changed before capture.");
        return bytes;
    }

    private JsonObject? Consumers(string? reference, Dictionary<string, byte[]> files)
    {
        if (reference is null) return null;
        byte[] bytes = Uploaded(reference, ManifestBytes);
        files.Add("consumers.json", bytes);
        return Object(bytes);
    }

    private static OpcUaNodeSetSource[] Sources(IEnumerable<Record> records)
    {
        var models = records.Select(r => Kind(r, "model")).ToArray();
        Require(models.Sum(r => (long)r.Files["original.xml"].Length) <= OpcUaDependencyAnalysis.MaxTotalBytes,
            "OpcUaCapacity", "Explicit model set exceeds 64 MiB.");
        return models.Select(r => new OpcUaNodeSetSource(r.Ref, r.Files["original.xml"])).ToArray();
    }

    private Record Publish(string kind, JsonObject report, Dictionary<string, byte[]> files, string[] inputs)
    {
        files.Add("report.json", Bytes(report));
        Require(files["report.json"].Length <= OpcUaModelReview.MaxReportBytes, "OpcUaCapacity", "Report exceeds 32 MiB.");
        foreach (string input in inputs) _ = Load(input);
        string id = Guid.NewGuid().ToString("N");
        string directory = PathFor(id);
        Require(!Directory.Exists(directory), "OpcUaPublicationExists", "Publication already exists; outputs are never overwritten.");
        var receipt = new Receipt(1, id, kind, DateTimeOffset.UtcNow, inputs,
            files.Select(p => new Artifact(p.Key, p.Value.Length, Hash(p.Value))).ToArray());
        byte[] receiptBytes = Bytes(receipt);
        string reference = "opcua:" + id + ":" + Hash(receiptBytes);
        Quota(files.Values.Sum(b => (long)b.Length) + 2L * receiptBytes.Length);
        Directory.CreateDirectory(directory);
        try
        {
            WriteNew(PathFor(id, "intent.json"), receiptBytes);
            foreach (var file in files) WriteNew(PathFor(id, file.Key), file.Value);
            WriteNew(PathFor(id, "receipt.json"), receiptBytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new BridgeOperationException("OpcUaPublicationInterrupted", "Retained publication was interrupted. List or reopen its handle; completed files are immutable. Retry using a new publication.",
                JsonSerializer.SerializeToElement(new { recordRef = reference }));
        }
        return new(reference, receipt, files);
    }

    private Record Load(string reference) => Load(reference, new(StringComparer.Ordinal), 0);

    private Record Load(string reference, Dictionary<string, Record> cache, int depth)
    {
        var (_, id, digest, _) = ParseRef(reference);
        Require(depth <= 2, "OpcUaIntegrityFailure", "Retained dependency graph exceeds two levels.");
        if (cache.TryGetValue(reference, out var cached)) return cached;
        Require(File.Exists(PathFor(id, "receipt.json")), "OpcUaPublicationIncomplete", "No completed receipt exists for this handle. List retained publications; retry creates a new record.");
        byte[] raw = ReadBounded(PathFor(id, "receipt.json"), ManifestBytes);
        Require(Hash(raw) == digest && ReadBounded(PathFor(id, "intent.json"), ManifestBytes).AsSpan().SequenceEqual(raw),
            "OpcUaIntegrityFailure", "Receipt or manifest differs from the Host-issued handle.");
        var receipt = JsonSerializer.Deserialize<Receipt>(raw, Json);
        Require(receipt is not null && receipt.SchemaVersion == 1 && receipt.Id == id && Kinds.Contains(receipt.Kind) &&
            receipt.Inputs is not null && receipt.Artifacts is { Length: >= 1 and <= 2 } &&
            receipt.Artifacts.All(a => a is not null) && receipt.Artifacts.Select(a => a.Name).Distinct().Count() == receipt.Artifacts.Length,
            "OpcUaIntegrityFailure", "Malformed retained receipt.");
        int inputCount = receipt.Kind == "model" ? 0 : receipt.Kind == "dependencies" ? receipt.Inputs.Length : 2;
        Require(receipt.Inputs.Length == inputCount && (receipt.Kind != "dependencies" || inputCount is > 0 and <= 32),
            "OpcUaIntegrityFailure", "Unexpected retained input count.");
        var expectedNames = receipt.Kind == "model" ? new[] { "report.json", "original.xml" }
            : receipt.Kind == "dependencies" ? new[] { "report.json" } : new[] { "report.json", "consumers.json" };
        Require(receipt.Artifacts.Any(a => a.Name == "report.json") &&
            receipt.Artifacts.All(a => expectedNames.Contains(a.Name)) &&
            (receipt.Kind != "model" || receipt.Artifacts.Any(a => a.Name == "original.xml")),
            "OpcUaIntegrityFailure", "Unexpected retained artifacts.");
        var files = new Dictionary<string, byte[]>();
        foreach (var artifact in receipt.Artifacts)
        {
            int limit = artifact.Name == "original.xml" ? OpcUaModelReview.MaxBytes : artifact.Name == "consumers.json" ? ManifestBytes : OpcUaModelReview.MaxReportBytes;
            Require(artifact.Bytes >= 0 && artifact.Bytes <= limit, "OpcUaIntegrityFailure", "Retained artifact length exceeds its bound.");
            byte[] bytes = ReadBounded(PathFor(id, artifact.Name), limit);
            Require(bytes.Length == artifact.Bytes && Hash(bytes) == artifact.Sha256, "OpcUaIntegrityFailure", "Retained artifact differs from its immutable receipt.");
            files.Add(artifact.Name, bytes);
        }
        foreach (string input in receipt.Inputs)
            _ = Kind(Load(input, cache, depth + 1), receipt.Kind == "dependency-comparison" ? "dependencies" : "model");
        var record = new Record(reference, receipt, files);
        cache.Add(reference, record);
        return record;
    }

    private static JsonObject Summary(Record record)
    {
        var report = Report(record);
        JsonNode? Bounded(JsonNode? node, string path)
        {
            if (node is JsonArray array) return new JsonObject { ["section"] = path, ["rowCount"] = array.Count };
            if (node is JsonObject obj) return new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Bounded(p.Value, path.Length == 0 ? p.Key : path + "." + p.Key))));
            return node?.DeepClone();
        }
        var result = new JsonObject { ["recordRef"] = record.Ref, ["recordSha256"] = record.Ref.Split(':')[2],
            ["kind"] = record.Receipt.Kind, ["state"] = "complete", ["createdUtc"] = record.Receipt.CreatedUtc,
            ["inputs"] = JsonSerializer.SerializeToNode(record.Receipt.Inputs, Json), ["summary"] = Bounded(report, ""),
            ["artifacts"] = new JsonArray(record.Receipt.Artifacts.Select(a => (JsonNode)new JsonObject {
                ["artifactRef"] = record.Ref + ":" + a.Name, ["bytes"] = a.Bytes, ["sha256"] = a.Sha256 }).ToArray()),
            ["nativeQualified"] = false, ["nativeFreshnessVerified"] = false, ["compatibilityCertified"] = false,
            ["dependencyClosureComplete"] = false };
        Require(Bytes(result).Length <= PageBytes, "OpcUaCapacity", "Report summary exceeds its response budget; retained report remains available by handle.");
        return result;
    }

    private static Dictionary<string, JsonArray> Sections(JsonObject report)
    {
        var result = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        void Visit(JsonObject obj, string prefix)
        {
            foreach (var (key, value) in obj)
            {
                string path = prefix + key;
                if (value is JsonArray array) result.Add(path, array);
                else if (value is JsonObject child) Visit(child, path + ".");
            }
        }
        Visit(report, "");
        return result;
    }

    private static JsonObject Report(Record record) => Object(record.Files["report.json"]);
    private static JsonObject Object(byte[] bytes) => JsonNode.Parse(bytes, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
        new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject ?? throw new BridgeException("OpcUaInvalidJson", "Expected a JSON object.");
    private static Record Kind(Record record, string kind)
    { Require(record.Receipt.Kind == kind, "OpcUaInvalidReference", "Expected a Host-issued " + kind + " handle."); return record; }
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Id(string value) => Regex.IsMatch(value, "\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant);

    private static (string Reference, string Id, string Hash, string? Name) ParseRef(string reference, bool artifact = false)
    {
        var parts = reference?.Split(':') ?? [];
        Require(parts.Length == (artifact ? 4 : 3) && parts[0] == "opcua" && Id(parts[1]) &&
            Regex.IsMatch(parts[2], "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant) &&
            (!artifact || parts[3] is "original.xml" or "consumers.json" or "report.json"),
            "OpcUaInvalidReference", "Use an exact Host-issued OPC UA record or artifact handle; filesystem paths are not accepted.");
        return (string.Join(':', parts.Take(3)), parts[1], parts[2], artifact ? parts[3] : null);
    }

    private IDisposable Lock()
    {
        try { return new FileStream(PathFor("workflow.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BridgeException("OpcUaStoreBusy", "Another offline OPC UA publication is active. Retry later."); }
    }

    private void Quota(long additionalBytes)
    {
        long bytes = 0;
        int records = 0;
        NoLinks(root);
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            NoLinks(directory);
            Require(++records < MaxRecords && !Directory.EnumerateDirectories(directory).Any(), "OpcUaStorageLimit", "OPC UA store permits 1000 flat records.");
            foreach (string file in Directory.EnumerateFiles(directory))
            { NoLinks(file); bytes = checked(bytes + new FileInfo(file).Length); }
        }
        Require(bytes + additionalBytes <= MaxStoredBytes, "OpcUaStorageLimit", "OPC UA retention exceeds 2 GiB, including incomplete records. Archive administratively before publishing more.");
    }

    private string PathFor(params string[] parts)
    { string path = Path.Combine([root, .. parts]); NoLinks(path); return path; }
    private static byte[] ReadBounded(string path, int limit)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length <= limit, "OpcUaCapacity", "Input or retained artifact exceeds its byte limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        NoLinks(path);
        using (var stream = new FileStream(path + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(path + ".pending", path, false);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "OpcUaUnsafeStorage", "OPC UA storage never follows symbolic links or junctions."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string code, string message)
    { if (!condition) throw new BridgeException(code, message); }
}
