using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

internal sealed record TranslationArtifact(string Name, long Bytes, string Sha256);
internal sealed record TranslationDependency(string RecordRef, string RecordSha256);
internal sealed record TranslationRecord(int SchemaVersion, string Kind, string Id, DateTimeOffset CreatedUtc,
    JsonObject Metadata, TranslationArtifact[] Artifacts, TranslationDependency[] Inputs, string RecordSha256);

/// <summary>Write-once, receipt-last local storage. No caller supplied filesystem paths.</summary>
internal sealed class TranslationRetention
{
    internal const int JsonLimit = 64 * 1024 * 1024;
    internal const int WorkbookLimit = 100 * 1024 * 1024;
    internal static readonly string[] Kinds = ["capture", "review", "approval", "candidate", "reconciliation"];
    private readonly string root;
    private readonly Action<string>? publicationObserver;

    internal TranslationRetention(string artifactsDirectory, Action<string>? publicationObserver = null)
    {
        root = Path.Combine(Path.GetFullPath(artifactsDirectory), "translation-workflow");
        this.publicationObserver = publicationObserver;
        NoLinks(root);
        Directory.CreateDirectory(root);
        foreach (var kind in Kinds) Directory.CreateDirectory(PathFor(kind));
    }

    internal IDisposable Lock()
    {
        try { return new FileStream(PathFor("workflow.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BridgeException("TranslationStoreBusy", "Another offline translation publication is active. Retry the same request after it completes."); }
    }

    internal static string Reference(string kind, string id) => $"translation:{kind}:{id}";
    internal static string Reference(TranslationRecord record) => Reference(record.Kind, record.Id);
    internal static string ArtifactRef(TranslationRecord record, string name) => Reference(record) + ":" + name;
    internal static TranslationDependency Dependency(TranslationRecord record) => new(Reference(record), record.RecordSha256);

    internal static (string Kind, string Id, string? Name) ParseRef(string reference, bool artifact = false)
    {
        var p = reference?.Split(':') ?? [];
        Require(p.Length == (artifact ? 4 : 3) && p[0] == "translation" && Kinds.Contains(p[1]) &&
            Regex.IsMatch(p[2], "^[a-f0-9]{32}$", RegexOptions.CultureInvariant),
            "InvalidTranslationReference", "Use an exact Host-issued translation record or artifact handle.");
        if (artifact) Require(Regex.IsMatch(p[3], "^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant),
            "InvalidTranslationReference", "Invalid translation artifact handle.");
        return (p[1], p[2], artifact ? p[3] : null);
    }

    internal bool Exists(string kind, string id) => Directory.Exists(PathFor(kind, id));

    internal TranslationRecord Publish(string kind, string id, JsonObject metadata,
        IReadOnlyDictionary<string, byte[]> files, params TranslationRecord[] inputs)
    {
        ParseRef(Reference(kind, id));
        Require(!Exists(kind, id), "TranslationPublicationExists", "Publication already exists; reopen it. Incomplete publications are retained and never overwritten; prepare a new review to retry.");
        // Admission includes the prospective parent level. Never publish a
        // completed record that the bounded restart reader cannot reopen.
        foreach (var input in inputs)
            Require(Load(Reference(input), new HashSet<string>(StringComparer.Ordinal), 1).RecordSha256 == input.RecordSha256,
                "TranslationIntegrityFailure", "An input changed before publication.");
        var artifacts = files.Select(p =>
        {
            ParseRef(Reference(kind, id) + ":" + p.Key, true);
            Require(p.Value.Length <= (p.Key.EndsWith(".xlsx", StringComparison.Ordinal) ? WorkbookLimit : JsonLimit),
                "TranslationArtifactLimit", "Retained artifact exceeds its byte budget.");
            return new TranslationArtifact(p.Key, p.Value.Length, Sha256(p.Value));
        }).ToArray();
        var record = new TranslationRecord(1, kind, id, DateTimeOffset.UtcNow, (JsonObject)metadata.DeepClone(),
            artifacts, inputs.Select(Dependency).ToArray(), "");
        record = record with { RecordSha256 = RecordHash(record) };
        // The complete intended manifest is retained before the first data file. It is
        // not a completion receipt and cannot authorize candidate generation.
        CheckQuota(files.Values.Sum(b => (long)b.Length) + JsonBytes(record).Length * 2L);
        Directory.CreateDirectory(PathFor(kind, id));
        try
        {
            WriteNew(PathFor(kind, id, "intent.json"), JsonBytes(record));
            publicationObserver?.Invoke(kind + ":intent");
            foreach (var file in files)
            {
                WriteNew(PathFor(kind, id, file.Key), file.Value);
                publicationObserver?.Invoke(kind + ":" + file.Key);
            }
            publicationObserver?.Invoke(kind + ":before-receipt");
            WriteNew(PathFor(kind, id, "receipt.json"), JsonBytes(record));
            // A lost response after this point is recovered by reading the receipt.
            publicationObserver?.Invoke(kind + ":complete");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bool complete = File.Exists(PathFor(kind, id, "receipt.json"));
            throw new BridgeOperationException(complete ? "TranslationPublicationResponseLost" : "TranslationPublicationIncomplete",
                "Publication interrupted; reopen the returned record handle. Incomplete records are retained and never overwritten. No native action occurred.",
                JsonSerializer.SerializeToElement(new { recordRef = Reference(record), state = complete ? "complete" : "incomplete" }));
        }
        return record;
    }

    internal TranslationRecord Load(string reference) => Load(reference, new HashSet<string>(StringComparer.Ordinal), 0);

    private TranslationRecord Load(string reference, HashSet<string> active, int depth)
    {
        var (kind, id, _) = ParseRef(reference);
        Require(depth <= 64, "TranslationDependencyLimit", "Retained ancestry exceeds 64 levels. Start a new review from the current capture with an explicitly supplied proposal batch.");
        Require(active.Add(reference), "TranslationIntegrityFailure", "Invalid retained dependency cycle.");
        Require(Exists(kind, id), "TranslationNotFound", "Translation record does not exist.");
        string receipt = PathFor(kind, id, "receipt.json");
        Require(File.Exists(receipt), "TranslationPublicationIncomplete", "Publication has no completion receipt. Retained files are not a completed candidate. List the record to inspect publication state; use a new review for a retry.");
        var record = Parse<TranslationRecord>(Encoding.UTF8.GetString(ReadBounded(receipt, JsonLimit)));
        Require(record.SchemaVersion == 1 && record.Kind == kind && record.Id == id && RecordHash(record) == record.RecordSha256 &&
            record.Artifacts.Length <= 10 && record.Inputs.Length <= 3,
            "TranslationIntegrityFailure", "Retained translation receipt identity or digest differs.");
        Require(record.Artifacts.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() == record.Artifacts.Length,
            "TranslationIntegrityFailure", "Duplicate retained artifact.");
        foreach (var artifact in record.Artifacts) _ = Read(record, artifact.Name);
        foreach (var input in record.Inputs)
            Require(Load(input.RecordRef, active, depth + 1).RecordSha256 == input.RecordSha256,
                "TranslationIntegrityFailure", "Retained review input differs. Create a new capture and review.");
        active.Remove(reference);
        return record;
    }

    internal byte[] Read(TranslationRecord record, string name)
    {
        ParseRef(ArtifactRef(record, name), true);
        var artifact = record.Artifacts.SingleOrDefault(a => a.Name == name);
        Require(artifact is not null, "TranslationArtifactNotFound", "Artifact is not part of this completed publication.");
        byte[] bytes = ReadBounded(PathFor(record.Kind, record.Id, name), name.EndsWith(".xlsx", StringComparison.Ordinal) ? WorkbookLimit : JsonLimit);
        Require(bytes.LongLength == artifact.Bytes && Sha256(bytes) == artifact.Sha256,
            "TranslationIntegrityFailure", "Retained artifact differs from its immutable receipt. Create a new capture and review.");
        return bytes;
    }

    internal JsonObject List(string kind, int offset, int count)
    {
        Require(Kinds.Contains(kind), "InvalidTranslationReference", "Select capture, review, approval, candidate or reconciliation.");
        Require(count is > 0 and <= 100 && offset >= 0, "TranslationPageLimit", "List count must be 1..100 and offset nonnegative.");
        var directories = Directory.EnumerateDirectories(PathFor(kind)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Require(offset <= directories.Length, "InvalidTranslationCursor", "List offset is beyond the retained records.");
        var rows = new JsonArray();
        int responseBytes = 0;
        foreach (var id in directories.Skip(offset).Take(count))
        {
            string reference = Reference(kind, id!);
            JsonObject row;
            try { row = Summary(Load(reference)); }
            catch (Exception ex) when (ex is BridgeException or IOException or System.Text.Json.JsonException)
            {
                row = new JsonObject { ["recordRef"] = reference,
                    ["state"] = File.Exists(PathFor(kind, id!, "receipt.json")) ? "integrity-failed" : "incomplete",
                    ["errorCode"] = (ex as BridgeException)?.Code ?? "TranslationIntegrityFailure",
                    ["nativeImportAllowed"] = false };
            }
            int bytes = Encoding.UTF8.GetByteCount(row.ToJsonString());
            if (responseBytes + bytes > 192 * 1024) break;
            rows.Add(row); responseBytes += bytes;
        }
        return new JsonObject { ["kind"] = kind, ["rows"] = rows, ["total"] = directories.Length,
            ["nextOffset"] = offset + rows.Count < directories.Length ? offset + rows.Count : null,
            ["ordering"] = "record-id; discovery only, concurrent additions may require relisting" };
    }

    internal static JsonObject Summary(TranslationRecord record) => new()
    {
        ["recordRef"] = Reference(record), ["recordSha256"] = record.RecordSha256, ["kind"] = record.Kind,
        ["state"] = "complete", ["createdUtc"] = record.CreatedUtc, ["metadata"] = record.Metadata.DeepClone(),
        ["artifacts"] = new JsonArray(record.Artifacts.Select(a => (JsonNode)new JsonObject
        { ["artifactRef"] = ArtifactRef(record, a.Name), ["bytes"] = a.Bytes, ["sha256"] = a.Sha256 }).ToArray()),
        ["nativeQualified"] = false, ["nativeExecuted"] = false, ["nativeImportAllowed"] = false,
        ["nativeFreshnessVerified"] = false
    };

    internal static byte[] JsonBytes<T>(T value) => Encoding.UTF8.GetBytes(CanonicalJson.Serialize(Node(value)) + "\n");
    private static string RecordHash(TranslationRecord record) => HashJson(record with { RecordSha256 = "" });
    internal static byte[] ReadBounded(string path, int limit)
    {
        NoLinks(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Require(stream.Length <= limit, "TranslationArtifactLimit", "Artifact exceeds its bounded read limit.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { throw new BridgeException("TranslationIntegrityFailure", "A retained input is missing."); }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        NoLinks(path);
        string staging = path + ".pending";
        using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(staging, path, false);
    }

    private void CheckQuota(long additionalBytes)
    {
        long bytes = 0;
        int records = 0;
        void Walk(string directory)
        {
            NoLinks(directory);
            foreach (string file in Directory.EnumerateFiles(directory))
            { NoLinks(file); bytes = checked(bytes + new FileInfo(file).Length); }
            foreach (string child in Directory.EnumerateDirectories(directory)) { records++; Walk(child); }
        }
        Walk(root);
        Require(records < 10000 + Kinds.Length && bytes + additionalBytes <= 8L * 1024 * 1024 * 1024,
            "TranslationStorageLimit", "Offline retention is limited to 10,000 records and 8 GiB, including incomplete publications. Archive storage administratively before creating more records.");
    }

    private string PathFor(params string[] pieces)
    {
        string path = Path.Combine([root, .. pieces]);
        NoLinks(path);
        return path;
    }

    internal static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                "UnsafeTranslationStorage", "Translation storage never follows symbolic links or junctions."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
