using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Graph;

namespace TiaMcp.Core;

/// <summary>Immutable offline GRAPH documents and reports. No native worker or project mutation.</summary>
public sealed class GraphWorkflowStore
{
    private sealed record Artifact(string Name, int Bytes, string Sha256);
    private sealed record Receipt(int SchemaVersion, string Id, string Kind, string[] Inputs, Artifact[] Artifacts);
    private sealed record Record(string Reference, Receipt Receipt, Dictionary<string, byte[]> Files);
    private const int PageBytes = 128 << 10, MaxRecords = 128;
    private const long StoreBytes = 2L << 30;
    private readonly string root, artifactsRoot;
    private readonly ArtifactUploadStore uploads;
    public GraphWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits = null)
    {
        root = Path.GetFullPath(Path.Combine(artifactsDirectory, "graph-workflow"));
        PackageStorage.EnsureNoLinks(root); Directory.CreateDirectory(root);
        uploads = new(artifactsDirectory, uploadLimits); artifactsRoot = Path.GetFullPath(artifactsDirectory);
    }
    public object Inspect(string xmlUploadRef)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var upload = uploads.ResolveCommitted(xmlUploadRef); var bytes = Read(upload.Path, GraphDocument.MaxInputBytes);
        Require(Hash(bytes) == upload.Sha256, "GraphInputChanged", "Committed upload changed during reading.");
        return Summary(PublishDocument(bytes));
    }
    /// <summary>Retain a native export inside the Host artifact directory as a document record, bound to the exporter's SHA-256.</summary>
    public object Retain(string path, string sha256)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        return Summary(PublishDocument(ArtifactBytes(path, sha256)));
    }
    /// <summary>Worker payload for a graph_import change. The exact retained (graphRef) or artifact-directory (path) document is refused
    /// unless the offline reader finds one chart without errors, unsupported grammar or the empty Interlock/Supervision shape TIA rejects.</summary>
    public JsonObject ImportPayload(JsonElement payload)
    {
        string? Text(string name) => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string? graphRef = Text("graphRef"), path = Text("path");
        Require(graphRef is null != path is null, "InvalidRequest", "graph_import requires exactly one of graphRef or path.");
        byte[] bytes;
        using (PackageStorage.Lock(Path.Combine(root, "store.lock"))) bytes = graphRef is null ? ArtifactBytes(path!, null) : Document(graphRef).Files["original.xml"];
        GraphInspection inspection;
        try { inspection = GraphDocument.Parse(bytes).Inspection; }
        catch (GraphException error) { throw new BridgeException(error.Code, error.Message); }
        string? refused = inspection.Diagnostics.Where(d => d.Severity == "error" || d.Code == "EmptyNativeCondition").Select(d => d.Code + " at " + d.Path)
            .Concat(inspection.Unsupported.Where(u => u.Code is not ("OpaqueEnvelope" or "OpaqueContent")).Select(u => u.Code + " at " + u.Path))
            .FirstOrDefault() ?? (inspection.Graphs.Count == 1 ? null : "expected exactly one GRAPH chart");
        Require(refused is null, "GraphImportRefused", "The GRAPH document is not importable: " + refused +
            ". Structural errors, unsupported grammar and empty Interlock/Supervision modules without FlgNet (rejected by TIA V21 on import) are refused.");
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var result = new JsonObject { ["xml"] = reader.ReadToEnd(), ["sourceSha256"] = Hash(bytes), [graphRef is null ? "path" : "graphRef"] = graphRef ?? path };
        if (payload.TryGetProperty("expectedBlockNames", out var names)) result["expectedBlockNames"] = JsonNode.Parse(names.GetRawText());
        return result;
    }
    public object Compare(string beforeRef, string afterRef)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var before = Document(beforeRef); var after = Document(afterRef);
        var report = GraphDocument.Parse(before.Files["original.xml"]).CompareTo(GraphDocument.Parse(after.Files["original.xml"]));
        return Summary(Publish("comparison", [beforeRef, afterRef], new() { ["report.json"] = Encoding.UTF8.GetBytes(report.ToJson()) }));
    }
    /// <summary>Apply typed GRAPH edits to a retained document. The edited bytes become a new document whose only input is the source,
    /// with a comparison record of source and result. No native worker or project change; graph_import applies the result.</summary>
    public object Edit(string sourceRef, JsonElement operations)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        Require(operations.ValueKind == JsonValueKind.Array, "GraphEditInput", "operations must be an array of typed edits.");
        var before = GraphDocument.Parse(Document(sourceRef).Files["original.xml"]);
        byte[] bytes = GraphEditor.Apply(before, GraphEditor.Parse(operations.GetRawText()));
        var edited = PublishDocument(bytes, [sourceRef]);
        var comparison = Publish("comparison", [sourceRef, edited.Reference],
            new() { ["report.json"] = Encoding.UTF8.GetBytes(before.CompareTo(GraphDocument.Parse(bytes)).ToJson()) });
        return new { document = Summary(edited), comparison = Summary(comparison), nativeQualified = false };
    }
    public object Reopen(string recordRef) => Summary(Load(recordRef));
    public object ReportPage(string recordRef, string section, int offset = 0, int count = 50)
    {
        Require(count is > 0 and <= 200 && offset >= 0 && section is { Length: > 0 and <= 1024 } && section.StartsWith('/'),
            "GraphPageLimit", "Use an exact array pointer and 1..200 rows, within a 128 KiB response budget.");
        var record = Load(recordRef); JsonNode? node = JsonNode.Parse(record.Files["report.json"]);
        var parts = section[1..].Split('/');
        Require(parts.Length <= 12, "GraphInvalidSection", "Array pointer is too deep.");
        foreach (var part in parts)
        {
            if (node is JsonObject obj) node = obj[part];
            else if (node is JsonArray array && int.TryParse(part, out int index) && index >= 0 && index < array.Count && index.ToString() == part) node = array[index];
            else throw new BridgeException("GraphInvalidSection", "Use an exact report array pointer, such as /graphs/0/sequences/0/steps.");
        }
        Require(node is JsonArray, "GraphInvalidSection", "Selected report value is not an array.");
        var source = (JsonArray)node; Require(offset <= source.Count, "GraphPageLimit", "Offset exceeds this array.");
        var (rows, next) = ReportPageRows.Read(source, offset, count, PageBytes - 4096, "Row exceeds page budget; download report.json.");
        var result = new { recordRef, reportSha256 = record.Receipt.Artifacts.Single(a => a.Name == "report.json").Sha256,
            section, offset, total = source.Count, rows, nextOffset = next == source.Count ? (int?)null : next, nativeQualified = false };
        Require(JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length <= PageBytes,
            "GraphPageLimit", "Encoded page exceeds 128 KiB; reduce count or download report.json.");
        return result;
    }
    public object ReadArtifact(string recordRef, string name, long offset = 0, int count = 65536)
    {
        var record = Load(recordRef);
        Artifact? artifact = name == "receipt.json" ? new(name, Read(PathFor(record.Receipt.Id, name), 65536).Length, recordRef.Split(':')[2])
            : record.Receipt.Artifacts.SingleOrDefault(a => a.Name == name);
        Require(artifact is not null, "GraphArtifactMissing", "Use an artifact name returned by this record.");
        var result = JsonSerializer.SerializeToElement(new ArtifactStore(root).Read(PathFor(record.Receipt.Id, name), offset, count), BridgeProtocol.Json);
        Require(result.GetProperty("sha256").GetString() == artifact.Sha256 && result.GetProperty("bytes").GetInt64() == artifact.Bytes,
            "GraphIntegrityFailure", "Artifact changed during transfer.");
        return new { sha256 = artifact.Sha256, bytes = artifact.Bytes, offset, count = result.GetProperty("count").GetInt32(),
            encoding = "base64", data = result.GetProperty("data").GetString(), nextOffset = result.GetProperty("nextOffset").GetInt64(),
            complete = result.GetProperty("complete").GetBoolean(), nativeQualified = false };
    }
    public object List(int offset = 0, int count = 20)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var directories = Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).Take(MaxRecords + 1).ToArray();
        Require(directories.Length <= MaxRecords && offset >= 0 && offset <= directories.Length && count is > 0 and <= 50,
            "GraphPageLimit", "List 1..50 records within the retained inventory.");
        var rows = directories.Skip(offset).Take(count).Select(directory =>
        {
            PackageStorage.EnsureNoLinks(directory); string id = PackageStorage.Id(Path.GetFileName(directory)); string? reference = null; string state = "incomplete";
            try
            {
                string path = PathFor(id, "receipt.json");
                if (File.Exists(path)) { reference = "graph:" + id + ":" + Hash(Read(path, 65536)); _ = Load(reference); state = "complete"; }
            }
            catch (Exception error) when (error is BridgeException or IOException or JsonException or UnauthorizedAccessException) { state = "integrity-failed"; }
            return new { id, recordRef = reference, state };
        }).ToArray();
        return new { rows, total = directories.Length, nextOffset = offset + rows.Length == directories.Length ? (int?)null : offset + rows.Length,
            ordering = "record-id; relist after additions", nativeQualified = false };
    }
    private Record Document(string reference)
    { var record = Load(reference); Require(record.Receipt.Kind == "document", "GraphInvalidReference", "Comparison inputs must be retained documents."); return record; }
    private Record Load(string reference, int depth = 0)
    {
        Require(reference is { Length: 103 } && depth <= 1, "GraphInvalidReference", "Use an exact Host-issued GRAPH record handle.");
        var parts = reference.Split(':'); Require(parts.Length == 3 && parts[0] == "graph", "GraphInvalidReference", "Wrong record handle.");
        string id = PackageStorage.Id(parts[1]), hash = PackageStorage.Hash(parts[2]);
        Require(parts[2] == hash, "GraphInvalidReference", "Record digests use canonical lowercase hex.");
        var receiptBytes = Read(PathFor(id, "receipt.json"), 65536);
        Require(Hash(receiptBytes) == hash && Read(PathFor(id, "intent.json"), 65536).AsSpan().SequenceEqual(receiptBytes), "GraphIntegrityFailure", "Retained receipt changed.");
        var receipt = JsonSerializer.Deserialize<Receipt>(receiptBytes, BridgeProtocol.Json);
        Require(receipt is not null && receipt.SchemaVersion == 1 && receipt.Id == id && receipt.Kind is "document" or "comparison" && receipt.Inputs is not null,
            "GraphIntegrityFailure", "Invalid retained record.");
        var names = receipt.Kind == "document" ? new[] { "original.xml", "report.json", "viewer.html" } : ["report.json"];
        Require(receipt.Artifacts is not null && receipt.Artifacts.All(a => a is not null) &&
            receipt.Artifacts.Select(a => a.Name).Order().SequenceEqual(names.Order()) && (receipt.Kind == "document" ? receipt.Inputs.Length <= 1 : receipt.Inputs.Length == 2),
            "GraphIntegrityFailure", "Unexpected retained artifacts or inputs.");
        var files = new Dictionary<string, byte[]>();
        foreach (var artifact in receipt.Artifacts)
        {
            var bytes = Read(PathFor(id, artifact.Name), artifact.Name == "original.xml" ? GraphDocument.MaxInputBytes : GraphDocument.MaxReportBytes);
            Require(bytes.Length == artifact.Bytes && Hash(bytes) == artifact.Sha256, "GraphIntegrityFailure", "Retained artifact changed."); files.Add(artifact.Name, bytes);
        }
        // Direct inputs are verified; an edited document's own source lineage is verified when that document is opened itself.
        foreach (string input in depth == 0 ? receipt.Inputs : [])
            Require(Load(input, depth + 1).Receipt.Kind == "document", "GraphIntegrityFailure", "Record input is not a retained document.");
        return new(reference, receipt, files);
    }
    private Record Publish(string kind, string[] inputs, Dictionary<string, byte[]> files)
    {
        Require(files.All(f => f.Value.Length <= GraphDocument.MaxReportBytes), "GraphCapacity", "An output exceeds 96 MiB.");
        long additional = files.Values.Sum(b => (long)b.Length) + 131072; long stored = 0; int entries = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PackageStorage.EnsureNoLinks(path); Require(++entries <= 10000, "GraphCapacity", "Storage enumeration exceeded its bound.");
            if (Directory.Exists(path)) pending.Push(path); else stored += new FileInfo(path).Length;
            Require(stored + additional <= StoreBytes, "GraphCapacity", "GRAPH storage exceeds 2 GiB.");
        }
        Require(Directory.EnumerateDirectories(root).Take(MaxRecords).Count() < MaxRecords, "GraphCapacity", "GRAPH storage retains at most 128 records.");
        string id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(PathFor(id));
        var receipt = new Receipt(1, id, kind, inputs, files.Select(f => new Artifact(f.Key, f.Value.Length, Hash(f.Value))).ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, BridgeProtocol.Json); Write(PathFor(id, "intent.json"), bytes);
        foreach (var file in files) Write(PathFor(id, file.Key), file.Value);
        foreach (string input in inputs) _ = Document(input);
        Write(PathFor(id, "receipt.json"), bytes); return new("graph:" + id + ":" + Hash(bytes), receipt, files);
    }
    private Record PublishDocument(byte[] bytes, string[]? inputs = null)
    {
        var document = GraphDocument.Parse(bytes);
        return Publish("document", inputs ?? [], new() { ["original.xml"] = bytes,
            ["report.json"] = Encoding.UTF8.GetBytes(document.ToJson()), ["viewer.html"] = Encoding.UTF8.GetBytes(document.ToHtml()) });
    }
    private byte[] ArtifactBytes(string path, string? sha256)
    {
        // ArtifactStore enforces the Host artifact-directory boundary and refuses links before the bytes are read.
        var probe = JsonSerializer.SerializeToElement(new ArtifactStore(artifactsRoot).Read(path, 0, 1), BridgeProtocol.Json);
        var bytes = Read(Path.Combine(artifactsRoot, probe.GetProperty("relativePath").GetString()!), GraphDocument.MaxInputBytes);
        Require(Hash(bytes) == (sha256 ?? probe.GetProperty("sha256").GetString()), "GraphInputChanged", "Artifact changed during reading.");
        return bytes;
    }
    private static object Summary(Record record)
    {
        var report = JsonNode.Parse(record.Files["report.json"])!.AsObject();
        var counts = report.Where(p => p.Value is JsonArray).ToDictionary(p => p.Key, p => ((JsonArray)p.Value!).Count);
        return new { recordRef = record.Reference, kind = record.Receipt.Kind, state = "complete", inputs = record.Receipt.Inputs,
            nativeQualified = false, behavioralEquivalenceCertified = false, fullSchemaValidated = false,
            sourceSha256 = (string?)report["sourceSha256"], beforeSha256 = (string?)report["beforeSha256"], afterSha256 = (string?)report["afterSha256"],
            hasErrors = (bool?)report["hasErrors"], hasUnsupportedContent = (bool?)report["hasUnsupportedContent"], matchingReliable = (bool?)report["matchingReliable"],
            sourceBytesChanged = (bool?)report["sourceBytesChanged"], counts, artifacts = record.Receipt.Artifacts, receiptName = "receipt.json" };
    }
    private string PathFor(string id, string? name = null) => PackageStorage.Under(root, name is null ? id : id + "/" + name);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Read(string path, int limit)
    { PackageStorage.EnsureNoLinks(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length <= limit, "GraphCapacity", "Input or retained file exceeds its byte bound."); var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes; }
    private static void Write(string path, byte[] bytes)
    { PackageStorage.EnsureNoLinks(path); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true); }
    private static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    { if (!condition) throw new BridgeException(code, message); }
}
