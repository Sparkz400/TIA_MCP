using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Engineering;

/// <summary>Large source_upsert/block_import_xml inputs by reference. The Host resolves a committed upload or a file under its
/// artifact directory, inlines the exact text as the payload's source/xml, and records the reference with the bytes' SHA-256 in
/// payload.inputReference. The worker then hashes the inlined text into the preview exactly like inline input, so a later change to
/// the referenced file cannot alter a prepared change.</summary>
public static class ChangeInputReference
{
    /// <summary>The worker's inline limit for source and XML text.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private static readonly Dictionary<string, (string Reference, string Inline)> Kinds = new(StringComparer.Ordinal)
    {
        ["source_upsert"] = ("sourceRef", "source"),
        ["block_import_xml"] = ("xmlRef", "xml"),
    };

    /// <summary>Returns the payload unchanged unless it names a reference for its kind; a reference for another kind is refused.</summary>
    public static JsonElement Resolve(string kind, JsonElement payload, string artifactsRoot, Func<string, (string Path, string FileName, string Sha256)> resolveUpload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return payload;
        var args = JsonNode.Parse(payload.GetRawText())!.AsObject();
        var supplied = Kinds.Values.Where(k => args.ContainsKey(k.Reference)).ToArray();
        if (supplied.Length == 0) return payload;
        if (!Kinds.TryGetValue(kind, out var names) || supplied.Any(k => k.Reference != names.Reference))
            throw new BridgeException("InvalidRequest", "sourceRef is accepted only by source_upsert and xmlRef only by block_import_xml.");
        if (args.ContainsKey(names.Inline)) throw new BridgeException("InvalidRequest", $"Use {names.Inline} or {names.Reference}, not both.");
        if (args.ContainsKey("inputReference")) throw new BridgeException("InvalidRequest", "inputReference is recorded by the Host and cannot be supplied.");
        string reference = args[names.Reference] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new BridgeException("InvalidRequest", names.Reference + " must be a non-empty string.");
        var (bytes, origin) = Read(reference, artifactsRoot, resolveUpload);
        args.Remove(names.Reference);
        args[names.Inline] = Decode(bytes, names.Reference);
        args["inputReference"] = new JsonObject
        {
            ["field"] = names.Inline, ["reference"] = reference, ["origin"] = origin, ["bytes"] = bytes.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
        return JsonSerializer.SerializeToElement(args, BridgeProtocol.Json);
    }

    private static (byte[] Bytes, string Origin) Read(string reference, string artifactsRoot, Func<string, (string Path, string FileName, string Sha256)> resolveUpload)
    {
        if (reference.StartsWith("upload:", StringComparison.Ordinal))
        {
            var upload = resolveUpload(reference);
            byte[] data = ReadBounded(upload.Path);
            // The upload store verified this hash at commit; a mismatch now means the stored bytes changed.
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), upload.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ArtifactChanged", "The committed upload no longer matches its SHA-256.");
            return (data, "upload");
        }
        // ArtifactStore enforces the artifact-directory boundary and refuses links and junctions.
        var probe = new ArtifactStore(artifactsRoot).Read(reference, 0, 1);
        string full = Path.Combine(Path.GetFullPath(artifactsRoot), probe.RelativePath);
        byte[] bytes = ReadBounded(full);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), probe.Sha256, StringComparison.Ordinal))
            throw new BridgeException("ArtifactChanged", "The referenced file changed while it was read.");
        return (bytes, "artifactDirectory");
    }

    private static byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new BridgeException("ArtifactUnavailable", "The referenced file does not exist.");
        if (info.Length > MaxBytes) throw new BridgeException("InputTooLarge", $"Referenced input exceeds {MaxBytes / (1024 * 1024)} MiB.");
        return File.ReadAllBytes(path);
    }

    private static string Decode(byte[] bytes, string field)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) span = span[3..];
        try { return new UTF8Encoding(false, true).GetString(span); }
        catch (DecoderFallbackException) { throw new BridgeException("InvalidRequest", field + " must reference UTF-8 text."); }
    }
}
