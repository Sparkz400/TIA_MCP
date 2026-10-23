using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TiaMcp.CompositeEngineering.Tests")]

namespace TiaMcp.Core;

/// <summary>Immutable supplied C02 reviews; reopening revalidates every referenced input.</summary>
public sealed class CompositeEngineeringWorkflowStore
{
    private const string Prefix = "composite-engineering";
    private const int PageBytes = 128 << 10, ReceiptBytes = 64 << 10, MaxRecords = 128, MaxEntries = 1024;
    private const long StoreBytes = 512L << 20;
    private sealed record Artifact(string Name, int Bytes, string Sha256);
    private sealed record Receipt(int SchemaVersion, string Id, Artifact[] Artifacts);
    private sealed record Record(string Reference, Receipt Receipt, byte[] ReceiptData, byte[] Request, byte[] Report);
    private readonly string artifactsRoot, root;
    private readonly ArtifactUploadStore uploads;
    private readonly Action<string, string>? checkpoint;

    public CompositeEngineeringWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits = null)
        : this(artifactsDirectory, uploadLimits, null) { }

    // Internal pause points let process tests interrupt a flushed publication.
    internal CompositeEngineeringWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits, Action<string, string>? checkpoint)
    {
        artifactsRoot = Path.GetFullPath(artifactsDirectory); root = Path.Combine(artifactsRoot, "composite-engineering-reviews");
        PackageStorage.EnsureNoLinks(root); Directory.CreateDirectory(root); PackageStorage.EnsureNoLinks(root);
        uploads = new(artifactsRoot, uploadLimits);
        this.checkpoint = checkpoint;
    }

    public object Review(string uploadRef)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var upload = uploads.ResolveCommitted(uploadRef); byte[] request = Read(upload.Path, CompositeEngineeringReview.InputLimit);
        Require(Hash(request) == upload.Sha256, "CompositeInputChanged", "Committed request changed during reading.");
        byte[] report = Evaluate(request);
        string id = Guid.NewGuid().ToString("N");
        var receipt = new Receipt(1, id, [new("request.json", request.Length, Hash(request)), new("report.json", report.Length, Hash(report))]);
        byte[] receiptData = JsonSerializer.SerializeToUtf8Bytes(receipt, BridgeProtocol.Json);
        Inventory(request.LongLength + report.LongLength + 2 * receiptData.Length, 5, 1);
        Directory.CreateDirectory(PathFor(id));
        Write(PathFor(id, "intent.json"), receiptData);
        checkpoint?.Invoke("intentWritten", PathFor(id));
        Write(PathFor(id, "request.json"), request); checkpoint?.Invoke("requestWritten", PathFor(id));
        Write(PathFor(id, "report.json"), report); checkpoint?.Invoke("reportWritten", PathFor(id));
        Require(Read(PathFor(id, "request.json"), CompositeEngineeringReview.InputLimit).AsSpan().SequenceEqual(request) &&
            Read(PathFor(id, "report.json"), CompositeEngineeringReview.ReportLimit).AsSpan().SequenceEqual(report) &&
            Evaluate(request).AsSpan().SequenceEqual(report), "CompositeIntegrityFailure", "Inputs or retained output changed before publication.");
        Write(PathFor(id, "receipt.json"), receiptData);
        checkpoint?.Invoke("receiptWritten", PathFor(id));
        return Summary(Load(Reference(id, receiptData)));
    }

    public object Reopen(string recordRef) => Summary(Load(recordRef));

    public object Page(string recordRef, string section = "mappings", int offset = 0, int count = 50)
    {
        Require(offset >= 0 && count is > 0 and <= 200, "CompositePageLimit", "Use a nonnegative offset and 1..200 rows.");
        Require(section is "selections" or "mappings" or "correspondences" or "impacts" or "coverageGaps" or
            "beforeComponents" or "afterComponents" or "amlChanges" or "beforeAmlImpacts" or "afterAmlImpacts" or
            "consumerContexts" or "consumerCoverage" or "beforeConsumerRoots" or "afterConsumerRoots" or
            "beforeConsumerAssociations" or "afterConsumerAssociations" or "beforeConsumerSavedLinks" or "afterConsumerSavedLinks" or
            "beforeConsumerCandidates" or "afterConsumerCandidates",
            "CompositeInvalidSection", "Select a documented complete report array.");
        var record = Load(recordRef); var report = JsonNode.Parse(record.Report)!.AsObject();
        bool consumers = section is "consumerContexts" or "consumerCoverage" || section.Contains("Consumer", StringComparison.Ordinal);
        Require(!consumers || report["consumerContexts"] is JsonArray, "CompositeInvalidSection", "Consumer context sections require a retained v2 or v3 request; v1 records keep their original evaluation.");
        var source = (section switch {
            "beforeComponents" => report["contexts"]!["before"]!["components"], "afterComponents" => report["contexts"]!["after"]!["components"],
            "amlChanges" => report["amlComparison"]!["changes"], "beforeAmlImpacts" => report["amlComparison"]!["beforeImpacts"],
            "afterAmlImpacts" => report["amlComparison"]!["afterImpacts"],
            "consumerCoverage" => new JsonArray(report["consumerContexts"]!.AsArray().Select(c => (JsonNode)new JsonObject { ["phase"] = c!["phase"]!.DeepClone(), ["coverage"] = c["coverage"]!.DeepClone() }).ToArray()),
            "beforeConsumerRoots" => report["consumerContexts"]![0]!["rootBindings"], "afterConsumerRoots" => report["consumerContexts"]![1]!["rootBindings"],
            "beforeConsumerAssociations" => report["consumerContexts"]![0]!["associations"], "afterConsumerAssociations" => report["consumerContexts"]![1]!["associations"],
            "beforeConsumerSavedLinks" => report["consumerContexts"]![0]!["savedLinks"], "afterConsumerSavedLinks" => report["consumerContexts"]![1]!["savedLinks"],
            "beforeConsumerCandidates" => report["consumerContexts"]![0]!["candidates"], "afterConsumerCandidates" => report["consumerContexts"]![1]!["candidates"],
            _ => report[section] })!.AsArray();
        Require(offset <= source.Count, "CompositePageLimit", "Offset exceeds this report array.");
        var (rows, next) = ReportPageRows.Read(source, offset, count, PageBytes - 4096, "Row exceeds page budget; download report.json.");
        var result = new { recordRef, reportSha256 = record.Receipt.Artifacts[1].Sha256, section, offset, total = source.Count,
            rows, nextOffset = next == source.Count ? (int?)null : next, nativeQualified = false, wholeImpactAnalysisComplete = false };
        Require(JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length <= PageBytes, "CompositePageLimit", "Page exceeds 128 KiB; reduce count or download the report.");
        return result;
    }

    public object ReadArtifact(string recordRef, string name, long offset = 0, int count = 65536)
    {
        Require(name is "request.json" or "report.json" or "receipt.json", "CompositeArtifactMissing", "Select request.json, report.json or receipt.json.");
        Require(offset >= 0 && count is > 0 and <= 65536, "CompositePageLimit", "Use a nonnegative offset and 1..65536 bytes.");
        var record = Load(recordRef);
        byte[] bytes = name switch { "request.json" => record.Request, "report.json" => record.Report, _ => record.ReceiptData };
        Require(offset <= bytes.Length, "CompositePageLimit", "Offset exceeds the selected artifact.");
        int length = (int)Math.Min(count, bytes.Length - offset);
        return new { recordRef, name, sha256 = Hash(bytes), bytes = bytes.Length, offset, count = length, nextOffset = offset + length,
            complete = offset + length == bytes.Length, encoding = "base64", data = Convert.ToBase64String(bytes, (int)offset, length), nativeQualified = false };
    }

    public object List(int offset = 0, int count = 20)
    {
        Require(offset >= 0 && count is > 0 and <= 20, "CompositePageLimit", "List 1..20 records at a nonnegative offset.");
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock")); Inventory();
        var directories = Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).ToArray();
        Require(offset <= directories.Length, "CompositePageLimit", "Offset exceeds retained record inventory.");
        var rows = directories.Skip(offset).Take(count).Select(directory =>
        {
            string id = Path.GetFileName(directory), state = "incomplete"; string? reference = null, errorCode = null;
            try
            {
                PackageStorage.Id(id); PackageStorage.EnsureNoLinks(directory);
                if (File.Exists(PathFor(id, "receipt.json")))
                { reference = Reference(id, Read(PathFor(id, "receipt.json"), ReceiptBytes)); _ = Load(reference); state = "complete"; }
            }
            catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            { state = "verificationFailed"; errorCode = e is BridgeException b ? b.Code : "CompositeVerificationFailed"; }
            return new { id, recordRef = reference, state, errorCode };
        }).ToArray();
        return new { rows, total = directories.Length, nextOffset = offset + rows.Length == directories.Length ? (int?)null : offset + rows.Length,
            ordering = "record-id; relist after additions", nativeQualified = false };
    }

    private Record Load(string reference)
    {
        Require(reference is not null && reference.Length == Prefix.Length + 98, "CompositeInvalidReference", "Use the exact retained composite handle.");
        var parts = reference.Split(':'); Require(parts.Length == 3 && parts[0] == Prefix, "CompositeInvalidReference", "Wrong composite handle.");
        string id = PackageStorage.Id(parts[1]), hash = PackageStorage.Hash(parts[2]);
        Require(parts[2] == hash, "CompositeInvalidReference", "Composite digests use canonical lowercase hex.");
        byte[] receiptData = Read(PathFor(id, "receipt.json"), ReceiptBytes);
        Require(Hash(receiptData) == hash && Read(PathFor(id, "intent.json"), ReceiptBytes).AsSpan().SequenceEqual(receiptData),
            "CompositeIntegrityFailure", "Receipt or publication intent changed.");
        var receipt = JsonSerializer.Deserialize<Receipt>(receiptData, BridgeProtocol.Json);
        Require(receipt is not null && receipt.SchemaVersion == 1 && receipt.Id == id && receipt.Artifacts is { Length: 2 } &&
            receipt.Artifacts.All(a => a is not null) && receipt.Artifacts.Select(a => a.Name).SequenceEqual(new[] { "request.json", "report.json" }) &&
            JsonSerializer.SerializeToUtf8Bytes(receipt, BridgeProtocol.Json).AsSpan().SequenceEqual(receiptData),
            "CompositeIntegrityFailure", "Receipt fields, artifact order or serialization are invalid.");
        var entries = Directory.EnumerateFileSystemEntries(PathFor(id)).Take(5).ToArray();
        foreach (string entry in entries) PackageStorage.EnsureNoLinks(entry);
        Require(entries.Length == 4 && entries.Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(new[] { "intent.json", "receipt.json", "report.json", "request.json" }),
            "CompositeIntegrityFailure", "Unexpected record contents.");
        byte[] request = Read(PathFor(id, "request.json"), CompositeEngineeringReview.InputLimit), report = Read(PathFor(id, "report.json"), CompositeEngineeringReview.ReportLimit);
        foreach (var (artifact, bytes) in new[] { (receipt.Artifacts[0], request), (receipt.Artifacts[1], report) })
            Require(bytes.Length == artifact.Bytes && Hash(bytes) == artifact.Sha256, "CompositeIntegrityFailure", "Retained artifact differs from its receipt.");
        Require(Evaluate(request).AsSpan().SequenceEqual(report), "CompositeIntegrityFailure", "Report differs from independently reopened inputs.");
        return new(reference, receipt, receiptData, request, report);
    }

    private byte[] Evaluate(byte[] request) => JsonSerializer.SerializeToUtf8Bytes(CompositeEngineeringReview.Build(artifactsRoot, request), BridgeProtocol.Json);

    private static object Summary(Record record)
    {
        var report = JsonNode.Parse(record.Report)!.AsObject();
        var counts = report.Where(p => p.Value is JsonArray).ToDictionary(p => p.Key, p => ((JsonArray)p.Value!).Count);
        return new { recordRef = record.Reference, state = "complete", requestSha256 = record.Receipt.Artifacts[0].Sha256,
            reportSha256 = record.Receipt.Artifacts[1].Sha256, beforeReviewId = (string)report["contexts"]!["before"]!["reviewId"]!,
            afterReviewId = (string)report["contexts"]!["after"]!["reviewId"]!, sourceBytesChanged = (bool)report["amlComparison"]!["sourceBytesChanged"]!,
            counts, artifacts = record.Receipt.Artifacts, receiptName = "receipt.json", nativeExecutable = false, nativeQualified = false,
            sourceAuthenticityVerified = false, liveFreshnessVerified = false, wholeImpactAnalysisComplete = false };
    }

    private void Inventory(long additionalBytes = 0, int additionalEntries = 0, int additionalRecords = 0)
    {
        long bytes = 0; int entries = 0, records = 0; var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            PackageStorage.EnsureNoLinks(path);
            Require(++entries + additionalEntries <= MaxEntries, "CompositeCapacity", "Storage exceeds 1024 entries, including incomplete publications.");
            if (Directory.Exists(path)) { if (directory == root) records++; pending.Push(path); } else bytes += new FileInfo(path).Length;
            Require(bytes + additionalBytes <= StoreBytes && records + additionalRecords <= MaxRecords, "CompositeCapacity", "Storage exceeds 512 MiB or 128 records, including incomplete publications.");
        }
        Require(bytes + additionalBytes <= StoreBytes && records + additionalRecords <= MaxRecords && entries + additionalEntries <= MaxEntries,
            "CompositeCapacity", "Publication would exceed retained storage bounds.");
    }
    private string PathFor(string id, string? name = null) => PackageStorage.Under(root, name is null ? id : id + "/" + name);
    private static string Reference(string id, byte[] receipt) => Prefix + ":" + id + ":" + Hash(receipt);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Read(string path, int limit)
    {
        PackageStorage.EnsureNoLinks(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length is > 0 && input.Length <= limit, "CompositeCapacity", "Input or retained artifact exceeds its byte bound or is empty.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }
    private static void Write(string path, byte[] bytes)
    { PackageStorage.EnsureNoLinks(path); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true); }
    private static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    { if (!condition) throw new BridgeException(code, message); }
}
