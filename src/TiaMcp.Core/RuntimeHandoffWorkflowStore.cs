using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.PhysicalRuntime;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TiaMcp.Access.Tests")]

namespace TiaMcp.Core;

/// <summary>Retained public C03 observations and reviews, never live session or operation admission.</summary>
public sealed class RuntimeHandoffWorkflowStore
{
    private const string Prefix = "runtime-handoff";
    private const int PageBytes = 128 << 10, ReceiptBytes = 64 << 10, MaxEntries = 2048, MaxRecords = 128;
    private const long StoreBytes = 512L << 20;
    private static readonly string[] SourceNames = ["baseline.target.json", "baseline.program.json", "baseline.session.json",
        "current.target.json", "current.program.json", "current.session.json"];
    private sealed record Artifact(string Name, int Bytes, string Sha256);
    private sealed record Receipt(int SchemaVersion, string Id, Artifact[] Artifacts);
    private sealed record Record(string Reference, Receipt Receipt, byte[] ReceiptData, byte[] Request, byte[] Report,
        Dictionary<string, byte[]> Observations);
    private readonly string artifactsRoot, root, recordsRoot, observationsRoot;
    private readonly ArtifactUploadStore uploads;
    private readonly Action<string, string>? checkpoint;

    public RuntimeHandoffWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits = null)
        : this(artifactsDirectory, uploadLimits, null) { }
    internal RuntimeHandoffWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits, Action<string, string>? checkpoint)
    {
        artifactsRoot = Path.GetFullPath(artifactsDirectory); root = Path.Combine(artifactsRoot, Prefix);
        PackageStorage.EnsureNoLinks(root); Directory.CreateDirectory(root); PackageStorage.EnsureNoLinks(root);
        recordsRoot = PackageStorage.Under(root, "records"); observationsRoot = PackageStorage.Under(root, "observations");
        Directory.CreateDirectory(recordsRoot); Directory.CreateDirectory(observationsRoot);
        uploads = new(artifactsRoot, uploadLimits); this.checkpoint = checkpoint;
    }

    public object RetainObservation(string role, string uploadRef)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var upload = uploads.ResolveCommitted(uploadRef); byte[] bytes = Read(upload.Path, RuntimeHandoffReview.ObservationLimit);
        Require(Hash(bytes) == upload.Sha256, "HandoffIntegrityFailure", "Committed observation changed during reading.");
        var observation = RuntimeHandoffReview.ReadObservation(role, bytes);
        string hash = Hash(bytes), path = PackageStorage.Under(observationsRoot, hash + ".json");
        if (!File.Exists(path))
        {
            Inventory(bytes.Length, 1);
            string temporary = PackageStorage.Under(observationsRoot, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
            Write(temporary, bytes); checkpoint?.Invoke("observationWritten", temporary);
            File.Move(temporary, path); checkpoint?.Invoke("observationPublished", path);
        }
        Require(Read(path, RuntimeHandoffReview.ObservationLimit).AsSpan().SequenceEqual(bytes), "HandoffIntegrityFailure", "Retained observation differs from its original upload.");
        return new { schemaVersion = "runtime-handoff-observation-receipt.v1", role, artifact = ObservationReference(hash), bytes = bytes.Length,
            sourceKind = (string)observation["sourceKind"]!, nativeExecutable = false, nativeQualified = false,
            sourceAuthenticityVerified = false, liveFreshnessVerified = false, operationAdmitted = false, assertionsAdmitted = false };
    }

    public object Review(string uploadRef)
    {
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock"));
        var upload = uploads.ResolveCommitted(uploadRef); byte[] request = Read(upload.Path, RuntimeHandoffReview.InputLimit);
        Require(Hash(request) == upload.Sha256, "HandoffIntegrityFailure", "Committed request changed during reading.");
        byte[] report = Evaluate(request, out var observations);
        string id = Guid.NewGuid().ToString("N");
        var artifacts = new[] { new Artifact("request.json", request.Length, Hash(request)), new Artifact("report.json", report.Length, Hash(report)) }
            .Concat(SourceNames.Select(name => new Artifact(name, observations[name].Length, Hash(observations[name])))).ToArray();
        var receipt = new Receipt(1, id, artifacts); byte[] receiptData = JsonSerializer.SerializeToUtf8Bytes(receipt, BridgeProtocol.Json);
        Inventory(request.LongLength + report.LongLength + 2 * receiptData.Length, 5, 1);
        Directory.CreateDirectory(PathFor(id));
        Write(PathFor(id, "intent.json"), receiptData); checkpoint?.Invoke("intentWritten", PathFor(id));
        Write(PathFor(id, "request.json"), request); checkpoint?.Invoke("requestWritten", PathFor(id));
        Write(PathFor(id, "report.json"), report); checkpoint?.Invoke("reportWritten", PathFor(id));
        Require(Read(PathFor(id, "intent.json"), ReceiptBytes).AsSpan().SequenceEqual(receiptData) &&
            Read(PathFor(id, "request.json"), RuntimeHandoffReview.InputLimit).AsSpan().SequenceEqual(request) &&
            Read(PathFor(id, "report.json"), RuntimeHandoffReview.ReportLimit).AsSpan().SequenceEqual(report) &&
            Evaluate(request, out _).AsSpan().SequenceEqual(report), "HandoffIntegrityFailure", "Inputs or retained output changed before publication.");
        Write(PathFor(id, "receipt.json"), receiptData); checkpoint?.Invoke("receiptWritten", PathFor(id));
        return Summary(Load(Reference(id, receiptData)));
    }

    public object Reopen(string recordRef) => Summary(Load(recordRef));
    internal JsonObject ReadVerifiedReport(string recordRef) => JsonNode.Parse(Load(recordRef).Report)!.AsObject();
    public object Page(string recordRef, string section = "constraints", int offset = 0, int count = 50)
    {
        Require(offset >= 0 && count is > 0 and <= 200, "HandoffPageLimit", "Use a nonnegative offset and 1..200 rows.");
        Require(section is "constraints" or "permissions" or "baselineObservations" or "currentObservations",
            "HandoffInvalidSection", "Select constraints, permissions, baselineObservations or currentObservations.");
        var record = Load(recordRef); var report = JsonNode.Parse(record.Report)!.AsObject();
        var source = section.EndsWith("Observations", StringComparison.Ordinal)
            ? new JsonArray(report["observations"]![section == "baselineObservations" ? "baseline" : "current"]!.AsObject()
                .Select(p => (JsonNode)new JsonObject { ["role"] = p.Key, ["observation"] = p.Value!.DeepClone() }).ToArray())
            : report[section]!.AsArray();
        Require(offset <= source.Count, "HandoffPageLimit", "Offset exceeds the selected report array.");
        var (rows, next) = ReportPageRows.Read(source, offset, count, PageBytes - 4096, "Row exceeds page budget; download report.json or the original observation.");
        var result = new { recordRef, reportSha256 = record.Receipt.Artifacts[1].Sha256, section, offset, total = source.Count,
            rows, nextOffset = next == source.Count ? (int?)null : next, nativeQualified = false, operationAdmitted = false, assertionsAdmitted = false };
        Require(JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length <= PageBytes, "HandoffPageLimit", "Page exceeds 128 KiB; download the complete report.");
        return result;
    }

    public object ReadArtifact(string recordRef, string name, long offset = 0, int count = 65536)
    {
        Require(name is "request.json" or "report.json" or "receipt.json" || SourceNames.Contains(name, StringComparer.Ordinal),
            "HandoffArtifactMissing", "Select request.json, report.json, receipt.json or an exact baseline/current target/program/session observation name.");
        Require(offset >= 0 && count is > 0 and <= 65536, "HandoffPageLimit", "Use a nonnegative offset and 1..65536 bytes.");
        var record = Load(recordRef);
        byte[] bytes = name switch { "request.json" => record.Request, "report.json" => record.Report, "receipt.json" => record.ReceiptData, _ => record.Observations[name] };
        Require(offset <= bytes.Length, "HandoffPageLimit", "Offset exceeds the selected artifact.");
        int length = (int)Math.Min(count, bytes.Length - offset);
        return new { recordRef, name, sha256 = Hash(bytes), bytes = bytes.Length, offset, count = length, nextOffset = offset + length,
            complete = offset + length == bytes.Length, encoding = "base64", data = Convert.ToBase64String(bytes, (int)offset, length),
            nativeQualified = false, operationAdmitted = false, assertionsAdmitted = false };
    }

    public object List(int offset = 0, int count = 20)
    {
        Require(offset >= 0 && count is > 0 and <= 20, "HandoffPageLimit", "List 1..20 records at a nonnegative offset.");
        using var gate = PackageStorage.Lock(Path.Combine(root, "store.lock")); Inventory();
        var directories = Directory.EnumerateDirectories(recordsRoot).Order(StringComparer.Ordinal).ToArray();
        Require(offset <= directories.Length, "HandoffPageLimit", "Offset exceeds retained record inventory.");
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
            { state = "verificationFailed"; errorCode = e is BridgeException b ? b.Code : "HandoffVerificationFailed"; }
            return new { id, recordRef = reference, state, errorCode };
        }).ToArray();
        return new { rows, total = directories.Length, nextOffset = offset + rows.Length == directories.Length ? (int?)null : offset + rows.Length,
            retainedObservationFiles = Directory.EnumerateFiles(observationsRoot, "*.json").Count(),
            incompleteObservationFiles = Directory.EnumerateFiles(observationsRoot, "*.tmp").Count(),
            ordering = "record-id; relist after additions", nativeQualified = false, operationAdmitted = false, assertionsAdmitted = false };
    }

    private Record Load(string reference)
    {
        Require(reference is not null && reference.Length == Prefix.Length + 98, "HandoffInvalidReference", "Use the exact retained handoff handle.");
        var parts = reference.Split(':'); Require(parts.Length == 3 && parts[0] == Prefix, "HandoffInvalidReference", "Wrong handoff handle.");
        string id = PackageStorage.Id(parts[1]), hash = PackageStorage.Hash(parts[2]);
        Require(parts[2] == hash, "HandoffInvalidReference", "Handoff digests use canonical lowercase hex.");
        byte[] receiptData = Read(PathFor(id, "receipt.json"), ReceiptBytes);
        Require(Hash(receiptData) == hash && Read(PathFor(id, "intent.json"), ReceiptBytes).AsSpan().SequenceEqual(receiptData),
            "HandoffIntegrityFailure", "Receipt or publication intent changed.");
        var receipt = JsonSerializer.Deserialize<Receipt>(receiptData, BridgeProtocol.Json);
        Require(receipt is not null && receipt.SchemaVersion == 1 && receipt.Id == id && receipt.Artifacts is { Length: 8 } &&
            receipt.Artifacts.All(a => a is not null) && receipt.Artifacts.Select(a => a.Name).SequenceEqual(new[] { "request.json", "report.json" }.Concat(SourceNames)) &&
            JsonSerializer.SerializeToUtf8Bytes(receipt, BridgeProtocol.Json).AsSpan().SequenceEqual(receiptData),
            "HandoffIntegrityFailure", "Receipt fields, artifact order or serialization are invalid.");
        var entries = Directory.EnumerateFileSystemEntries(PathFor(id)).Take(5).ToArray();
        foreach (string entry in entries) PackageStorage.EnsureNoLinks(entry);
        Require(entries.Length == 4 && entries.Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(new[] { "intent.json", "receipt.json", "report.json", "request.json" }),
            "HandoffIntegrityFailure", "Unexpected record contents.");
        byte[] request = Read(PathFor(id, "request.json"), RuntimeHandoffReview.InputLimit), report = Read(PathFor(id, "report.json"), RuntimeHandoffReview.ReportLimit);
        Require(request.Length == receipt.Artifacts[0].Bytes && Hash(request) == receipt.Artifacts[0].Sha256 &&
            report.Length == receipt.Artifacts[1].Bytes && Hash(report) == receipt.Artifacts[1].Sha256,
            "HandoffIntegrityFailure", "Retained request or report differs from its receipt.");
        Require(Evaluate(request, out var observations).AsSpan().SequenceEqual(report), "HandoffIntegrityFailure", "Report differs from independently reopened inputs.");
        var contents = new Dictionary<string, byte[]>(observations, StringComparer.Ordinal) { ["request.json"] = request, ["report.json"] = report };
        foreach (var artifact in receipt.Artifacts)
            Require(contents[artifact.Name].Length == artifact.Bytes && Hash(contents[artifact.Name]) == artifact.Sha256,
                "HandoffIntegrityFailure", "Retained artifact differs from its receipt.");
        return new(reference, receipt, receiptData, request, report, observations);
    }

    private byte[] Evaluate(byte[] request, out Dictionary<string, byte[]> observations)
    {
        var packet = RuntimeHandoffReview.ReadRequest(request); observations = new(StringComparer.Ordinal);
        foreach (string name in SourceNames)
        {
            var parts = name.Split('.'); var reference = packet[parts[0]]?[parts[1]] as JsonObject;
            Require(reference is not null && reference["sha256"] is JsonValue h && h.TryGetValue<string>(out _), "InvalidRuntimeHandoff", "Expected an owned observation reference.");
            string hash = PackageStorage.Hash((string)reference["sha256"]!);
            Require(JsonNode.DeepEquals(reference, ObservationReference(hash)), "InvalidRuntimeHandoff", "Retain observations with runtime_handoff_observation before selecting their exact references.");
            observations.Add(name, ProjectReviewStore.ReadArtifact(artifactsRoot, reference, RuntimeHandoffReview.ObservationLimit));
        }
        return JsonSerializer.SerializeToUtf8Bytes(RuntimeHandoffReview.Build(artifactsRoot, request), BridgeProtocol.Json);
    }

    private static object Summary(Record record)
    {
        var report = JsonNode.Parse(record.Report)!.AsObject();
        return new { schemaVersion = "runtime-handoff-receipt.v1", recordRef = record.Reference, state = "complete",
            requestSha256 = record.Receipt.Artifacts[0].Sha256, reportSha256 = record.Receipt.Artifacts[1].Sha256,
            reviewId = (string)report["reviewId"]!, accessRecordId = (string)report["accessRecordId"]!, accessPhase = (string)report["accessPhase"]!,
            summary = report["summary"]!.DeepClone(), artifacts = record.Receipt.Artifacts, receiptName = "receipt.json",
            targetBinding = (string?)report["targetBinding"]?["state"] ?? "unbound",
            nativeExecutable = false, nativeQualified = false, sourceAuthenticityVerified = false, liveFreshnessVerified = false,
            operationAdmitted = false, assertionsAdmitted = false, admission = "unavailableWithoutQualifiedNativeEvidence" };
    }

    private void Inventory(long additionalBytes = 0, int additionalEntries = 0, int additionalRecords = 0)
    {
        long bytes = 0; int entries = 0, records = 0; var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            PackageStorage.EnsureNoLinks(path);
            Require(++entries + additionalEntries <= MaxEntries, "HandoffCapacity", "Storage exceeds 2048 entries, including incomplete publications.");
            if (Directory.Exists(path)) { if (directory == recordsRoot) records++; pending.Push(path); } else bytes += new FileInfo(path).Length;
            Require(bytes + additionalBytes <= StoreBytes && records + additionalRecords <= MaxRecords, "HandoffCapacity", "Storage exceeds 512 MiB or 128 records, including incomplete publications.");
        }
        Require(bytes + additionalBytes <= StoreBytes && records + additionalRecords <= MaxRecords && entries + additionalEntries <= MaxEntries,
            "HandoffCapacity", "Publication would exceed retained storage bounds.");
    }
    private static JsonObject ObservationReference(string hash) => new() { ["path"] = Prefix + "/observations/" + hash + ".json", ["sha256"] = hash };
    private string PathFor(string id, string? name = null) => PackageStorage.Under(recordsRoot, name is null ? id : id + "/" + name);
    private static string Reference(string id, byte[] receipt) => Prefix + ":" + id + ":" + Hash(receipt);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Read(string path, int limit)
    {
        PackageStorage.EnsureNoLinks(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length is > 0 && input.Length <= limit, "HandoffCapacity", "Input or retained artifact exceeds its byte bound or is empty.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }
    private static void Write(string path, byte[] bytes)
    { PackageStorage.EnsureNoLinks(path); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true); }
    private static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    { if (!condition) throw new BridgeException(code, message); }
}
