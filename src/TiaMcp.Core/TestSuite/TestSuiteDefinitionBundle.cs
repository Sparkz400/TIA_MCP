using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TestSuite;

/// <summary>
/// Retains the exact reviewed definition closure, including opaque original bytes. This validates
/// artifact identity, not native grammar, effective scope, licensing or execution authorization.
/// </summary>
public sealed class TestSuiteDefinitionBundle
{
    public const int MaxOriginalBytes = 32 * 1024 * 1024;
    public const int MaxBundleBytes = 64 * 1024 * 1024;
    private const int MaxDefinitionBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions StrictJson = new(BridgeProtocol.Json)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };
    private sealed record Entry(TestSuiteDefinitionManifest Manifest, string OriginalBytesBase64);
    private sealed record Document(int SchemaVersion, string PlanSha256, Entry[] Definitions);
    private readonly byte[] retained;
    private readonly TestSuiteDefinitionCapture[] definitions;

    public string PlanSha256 { get; }
    public string Sha256 { get; }
    public int DefinitionCount => definitions.Length;
    public long OriginalByteCount { get; }
    public byte[] Bytes => retained.ToArray();

    private TestSuiteDefinitionBundle(string planSha256, TestSuiteDefinitionCapture[] definitions, byte[] retained, long originalByteCount)
    {
        PlanSha256 = planSha256;
        this.definitions = definitions;
        this.retained = retained;
        OriginalByteCount = originalByteCount;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(retained));
    }

    /// <summary>Input order is immaterial; retained order is the independently validated required closure.</summary>
    public static TestSuiteDefinitionBundle Capture(TestSuiteRunPlan run, IReadOnlyList<TestSuiteDefinitionCapture> captures)
    {
        TestSuiteRunPlanner.Validate(run);
        if (captures is null || captures.Count != run.Definitions.Length || captures.Any(c => c is null))
            throw Invalid("Retain exactly one original capture for every required definition, including exclusions and dependencies.");
        var manifests = captures.Select(c => c.Manifest).ToArray();
        if (manifests.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != manifests.Length)
            throw Invalid("Duplicate original definitions are ambiguous.");
        long originalBytes = manifests.Sum(m => (long)m.OriginalBytes);
        if (originalBytes > MaxOriginalBytes) throw Limit("Original definitions exceed the 32 MiB aggregate limit.");
        var byId = captures.Select((c, i) => (Capture: c, Manifest: manifests[i])).ToDictionary(x => x.Manifest.Id, StringComparer.Ordinal);
        var ordered = new TestSuiteDefinitionCapture[run.Definitions.Length];
        long encodedSize = 256;
        var entries = new Entry[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            var expected = run.Definitions[i];
            if (!byId.TryGetValue(expected.Id, out var item) ||
                TestSuiteStyleOperation.CanonicalHash(item.Manifest) != TestSuiteStyleOperation.CanonicalHash(expected))
                throw Invalid("Original capture manifest differs from the reviewed definition: " + expected.Id);
            // Check projected storage before creating all base64 strings or the complete JSON tree.
            encodedSize += Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(JsonSerializer.SerializeToNode(item.Manifest, BridgeProtocol.Json)!)) + 64;
            string base64 = Convert.ToBase64String(item.Capture.OriginalBytes);
            foreach (char c in base64) encodedSize += JavaScriptEncoder.Default.WillEncode(c) ? 6 : 1;
            if (encodedSize > MaxBundleBytes) throw Limit("Definition bundle exceeds the 64 MiB encoded limit.");
            ordered[i] = item.Capture;
            entries[i] = new(item.Manifest, base64);
        }
        var document = new Document(1, run.PlanSha256, entries);
        byte[] bytes = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(JsonSerializer.SerializeToNode(document, BridgeProtocol.Json)!));
        if (bytes.Length > MaxBundleBytes) throw Limit("Definition bundle exceeds the 64 MiB encoded limit.");
        return new(run.PlanSha256, ordered, bytes, originalBytes);
    }

    /// <summary>Reopens a retained canonical bundle and revalidates actual original bytes against the review.</summary>
    public static TestSuiteDefinitionBundle Restore(TestSuiteRunPlan run, byte[] bytes)
    {
        TestSuiteRunPlanner.Validate(run);
        if (bytes is null || bytes.Length == 0) throw Invalid("A retained definition bundle is required.");
        if (bytes.Length > MaxBundleBytes) throw Limit("Definition bundle exceeds the 64 MiB encoded limit.");
        var frozen = bytes.ToArray();
        Document document;
        try { document = JsonSerializer.Deserialize<Document>(frozen, StrictJson) ?? throw Invalid("The bundle must be a JSON object."); }
        catch (JsonException e) { throw new BridgeException("InvalidDefinitionBundle", "The bundle must use the exact versioned JSON schema.", e); }
        if (document.SchemaVersion != 1 || document.PlanSha256 != run.PlanSha256 || document.Definitions is null || document.Definitions.Length != run.Definitions.Length)
            throw Invalid("Bundle version, plan identity or required definition count differs from the review.");
        var captures = new TestSuiteDefinitionCapture[document.Definitions.Length];
        long originalBytes = 0;
        for (int i = 0; i < captures.Length; i++)
        {
            var entry = document.Definitions[i];
            if (entry?.Manifest is null || entry.OriginalBytesBase64 is null)
                throw Invalid("Each bundle entry needs an exact manifest and original bytes.");
            var expected = run.Definitions[i];
            if (TestSuiteStyleOperation.CanonicalHash(entry.Manifest) != TestSuiteStyleOperation.CanonicalHash(expected))
                throw Invalid("Ordered bundle manifests differ from the reviewed required closure.");
            if (entry.OriginalBytesBase64.Length > 4L * ((MaxDefinitionBytes + 2L) / 3L))
                throw Limit("An original definition exceeds the 16 MiB limit.");
            byte[] original;
            try { original = Convert.FromBase64String(entry.OriginalBytesBase64); }
            catch (FormatException e) { throw new BridgeException("InvalidDefinitionBundle", "Original definition bytes must use base64 encoding.", e); }
            originalBytes += original.Length;
            if (originalBytes > MaxOriginalBytes) throw Limit("Original definitions exceed the 32 MiB aggregate limit.");
            if (original.Length != expected.OriginalBytes || Convert.ToHexStringLower(SHA256.HashData(original)) != expected.OriginalSha256)
                throw Invalid("Original definition bytes differ from the reviewed length or SHA-256: " + expected.Id);
            captures[i] = new(expected.Id, expected.Family, expected.Name, expected.Extension, expected.Reference, original,
                expected.ProfileId, expected.RequiredScope, expected.Dependencies, expected.ScopeComplete, expected.EffectsReviewed, expected.UnresolvedSyntax);
        }
        var restored = Capture(run, captures);
        if (!frozen.AsSpan().SequenceEqual(restored.retained))
            throw Invalid("Retain the exact canonical bundle; duplicate fields, reordered entries and alternate encodings are refused.");
        return restored;
    }

    public void EnsureMatches(TestSuiteRunPlan run)
    {
        TestSuiteRunPlanner.Validate(run);
        if (run.PlanSha256 != PlanSha256) throw Invalid("The bundle belongs to a different reviewed run plan.");
    }

    public byte[] OriginalBytes(string definitionId)
    {
        for (int i = 0; i < definitions.Length; i++)
            if (definitions[i].Manifest.Id == definitionId) return definitions[i].OriginalBytes;
        throw Invalid("The definition is outside the retained required closure.");
    }

    private static BridgeException Invalid(string message) => new("InvalidDefinitionBundle", message);
    private static BridgeException Limit(string message) => new("DefinitionBundleLimit", message);
}
