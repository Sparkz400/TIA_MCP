using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.VersionControl;

/// <summary>Caller-supplied evidence, not a native capability claim. Paths describe one complete observed package.</summary>
public sealed record VciExpectedFile(string RelativePath, string Role);
public sealed record VciProfileInput(string ProfileId, string Family, string ObjectKind, string NativeFormat,
    string EvidenceSha256, string EvidenceOrigin, IReadOnlyList<VciExpectedFile> ExpectedFiles,
    bool Complete = true, string? LibraryTypeGuid = null, string? LibraryVersionGuid = null);
public sealed record VciPackageInput(string PackageId, EngineeringObjectReference Object,
    VciProfileInput Profile, string? NativeStatus = null, string? NativeObjectIdentity = null);
public sealed record VciFileInput(string RelativePath, ReadOnlyMemory<byte> Bytes, bool IsLink = false);
public sealed record VciFileEvidence(string RelativePath, string Sha256, long Bytes);

public sealed record VciLimits(int MaxFiles = 20000, int MaxPackages = 1000, long MaxTotalBytes = 1073741824,
    int MaxPathCharacters = 255)
{
    internal void Validate()
    {
        if (MaxFiles is < 1 or > 20000 || MaxPackages is < 1 or > 1000 ||
            MaxTotalBytes is < 1 or > 1073741824 || MaxPathCharacters is < 1 or > 255)
            throw new BridgeException("VciInvalidLimits", "VCI limits must be positive and within the documented hard limits.");
    }
}

public sealed class VciPackageEvidence
{
    public string PackageId { get; }
    public EngineeringObjectReference Object { get; }
    public string ProfileId { get; }
    public string Family { get; }
    public string NativeFormat { get; }
    public string EvidenceSha256 { get; }
    public string EvidenceOrigin { get; }
    public string? LibraryTypeGuid { get; }
    public string? LibraryVersionGuid { get; }
    public bool ProfileComplete { get; }
    public IReadOnlyList<VciExpectedFile> ExpectedFiles { get; }
    public string? NativeStatus { get; }
    public string? NativeObjectIdentity { get; }
    public IReadOnlyList<string> Issues { get; }
    internal VciPackageEvidence(VciPackageInput input, IEnumerable<string> issues)
    {
        PackageId = input.PackageId; Object = input.Object; ProfileId = input.Profile.ProfileId;
        Family = input.Profile.Family; NativeFormat = input.Profile.NativeFormat;
        EvidenceSha256 = input.Profile.EvidenceSha256; EvidenceOrigin = input.Profile.EvidenceOrigin;
        ProfileComplete = input.Profile.Complete; LibraryTypeGuid = input.Profile.LibraryTypeGuid?.ToLowerInvariant();
        LibraryVersionGuid = input.Profile.LibraryVersionGuid?.ToLowerInvariant(); NativeStatus = input.NativeStatus;
        NativeObjectIdentity = input.NativeObjectIdentity;
        ExpectedFiles = Array.AsReadOnly(input.Profile.ExpectedFiles.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray());
        Issues = Array.AsReadOnly(issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal JsonObject Json() => new()
    {
        ["packageId"] = PackageId, ["object"] = JsonSerializer.SerializeToNode(Object, BridgeProtocol.Json),
        ["profileId"] = ProfileId, ["family"] = Family, ["nativeFormat"] = NativeFormat,
        ["evidenceSha256"] = EvidenceSha256, ["evidenceOrigin"] = EvidenceOrigin,
        ["profileComplete"] = ProfileComplete, ["libraryTypeGuid"] = LibraryTypeGuid,
        ["libraryVersionGuid"] = LibraryVersionGuid, ["nativeStatus"] = NativeStatus, ["nativeObjectIdentity"] = NativeObjectIdentity,
        ["expectedFiles"] = new JsonArray(ExpectedFiles.Select(x => (JsonNode)new JsonObject
            { ["relativePath"] = x.RelativePath, ["role"] = x.Role }).ToArray()),
        ["issues"] = new JsonArray(Issues.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
    };
}

/// <summary>An immutable, in-memory observation. No filesystem, Git, Siemens or process calls.</summary>
public sealed class VciManifest
{
    public const string SchemaVersion = "vci-manifest.v1";
    public string WorkspaceId { get; }
    public EngineeringSnapshotIdentity Snapshot { get; }
    public bool EnumerationComplete { get; }
    public IReadOnlyList<VciPackageEvidence> Packages { get; }
    public IReadOnlyList<VciFileEvidence> Files { get; }
    public string Sha256 { get; }
    private readonly JsonObject document;

    private VciManifest(string workspaceId, EngineeringSnapshotIdentity snapshot, bool complete,
        VciPackageEvidence[] packages, VciFileEvidence[] files)
    {
        WorkspaceId = workspaceId; Snapshot = snapshot; EnumerationComplete = complete;
        Packages = Array.AsReadOnly(packages); Files = Array.AsReadOnly(files);
        document = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion, ["workspaceId"] = workspaceId,
            ["snapshot"] = JsonSerializer.SerializeToNode(snapshot, BridgeProtocol.Json),
            ["enumerationComplete"] = complete, ["nativeQualified"] = false, ["targetProbed"] = false,
            ["packages"] = new JsonArray(packages.Select(p => (JsonNode)p.Json()).ToArray()),
            ["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
                { ["relativePath"] = f.RelativePath, ["sha256"] = f.Sha256, ["bytes"] = f.Bytes }).ToArray())
        };
        Sha256 = CanonicalJson.Sha256(document);
    }

    public JsonObject ToJson() => (JsonObject)document.DeepClone();

    public static VciManifest Capture(string workspaceId, EngineeringSnapshotIdentity snapshot,
        IEnumerable<VciPackageInput> packages, IEnumerable<VciFileInput> files,
        bool enumerationComplete, VciLimits? limits = null)
    {
        limits ??= new(); limits.Validate(); VciGuard.Text(workspaceId, "workspaceId");
        if (snapshot is null) throw new BridgeException("VciInvalidManifest", "Snapshot is required.");
        VciGuard.Text(snapshot.ObservationId, "observationId"); VciGuard.Hash(snapshot.SourceSha256);
        if (snapshot.TiaVersion != 21) throw new BridgeException("VciUnsupportedVersion", "Only V21 observations are currently modeled.");
        if (packages is null || files is null) throw new BridgeException("VciInvalidManifest", "Package and file inventories are required.");
        // Bound enumeration before sorting/hashing; inputs may be unbounded generators.
        var inputs = packages.Take(limits.MaxPackages + 1).ToArray();
        if (inputs.Length > limits.MaxPackages) throw new BridgeException("VciLimitExceeded", "Too many packages.");
        var inputFiles = files.Take(limits.MaxFiles + 1).ToArray();
        if (inputFiles.Length > limits.MaxFiles) throw new BridgeException("VciLimitExceeded", "Too many files.");
        var inventory = new Dictionary<string, VciFileEvidence>(StringComparer.Ordinal);
        var pathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var f in inputFiles)
        {
            if (f is null) throw new BridgeException("VciInvalidManifest", "Null file.");
            VciGuard.Path(f.RelativePath, limits.MaxPathCharacters);
            if (f.IsLink) throw new BridgeException("VciUnsafePath", "Links/reparse entries are not regular package files.");
            if (!pathKeys.Add(f.RelativePath.Normalize(NormalizationForm.FormC)))
                throw new BridgeException("VciPathCollision", "File destinations collide under Windows case/Unicode rules.");
            total += f.Bytes.Length;
            if (total > limits.MaxTotalBytes) throw new BridgeException("VciLimitExceeded", "File byte budget exceeded.");
            inventory.Add(f.RelativePath, new(f.RelativePath, Convert.ToHexStringLower(SHA256.HashData(f.Bytes.Span)), f.Bytes.Length));
        }
        // A file cannot also be a directory ancestor, including under Windows case folding.
        foreach (var path in inventory.Keys)
            foreach (var ancestor in VciGuard.Ancestors(path))
                if (pathKeys.Contains(ancestor.Normalize(NormalizationForm.FormC)))
                    throw new BridgeException("VciPathCollision", "A file is also a directory ancestor.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pathOwners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var objectOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var issues = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        long expectedCount = 0;
        string? projectPath = null;
        foreach (var p in inputs)
        {
            if (p is null) throw new BridgeException("VciInvalidManifest", "Null package.");
            VciGuard.Text(p.PackageId, "packageId");
            if (!ids.Add(p.PackageId)) throw new BridgeException("VciDuplicatePackage", "Package IDs must be unique.");
            EngineeringIdentityGuard.Validate(p.Object);
            projectPath ??= p.Object.Object.ProjectPath;
            if (projectPath != p.Object.Object.ProjectPath) throw new BridgeException("VciProjectMismatch", "One workspace observation cannot mix projects.");
            if (p.Object.Snapshot != snapshot) throw new BridgeException("VciSnapshotMismatch", "Every package must belong to this exact observation snapshot.");
            VciGuard.Profile(p.Profile, p.Object.Object.Kind, limits.MaxPathCharacters);
            expectedCount += p.Profile.ExpectedFiles.Count;
            if (expectedCount > limits.MaxFiles) throw new BridgeException("VciLimitExceeded", "Expected-file budget exceeded.");
            if (p.NativeStatus is not null) VciGuard.Text(p.NativeStatus, "nativeStatus");
            if (p.NativeObjectIdentity is not null) VciGuard.Text(p.NativeObjectIdentity, "nativeObjectIdentity");
            var row = new List<string>(); issues.Add(p.PackageId, row);
            if (!p.Profile.Complete) row.Add("profileIncomplete");
            foreach (var expected in p.Profile.ExpectedFiles)
            {
                if (!inventory.ContainsKey(expected.RelativePath)) row.Add("missingCompanion:" + expected.RelativePath);
                var key = expected.RelativePath.Normalize(NormalizationForm.FormC);
                if (!pathOwners.TryGetValue(key, out var owners)) pathOwners.Add(key, owners = []);
                owners.Add(p.PackageId);
            }
            var objectKey = VciGuard.ObjectKey(p.Object);
            if (!objectOwners.TryGetValue(objectKey, out var refs)) objectOwners.Add(objectKey, refs = []);
            refs.Add(p.PackageId);
        }
        foreach (var owners in pathOwners.Values.Concat(objectOwners.Values).Where(x => x.Count > 1))
            foreach (var owner in owners) issues[owner].Add("mappingCollision");
        // If a required spelling differs from an observed Windows-equivalent file, do not silently adopt it.
        foreach (var p in inputs)
            foreach (var f in p.Profile.ExpectedFiles)
                if (!inventory.ContainsKey(f.RelativePath) && pathKeys.Contains(f.RelativePath.Normalize(NormalizationForm.FormC)))
                    issues[p.PackageId].Add("pathSpellingCollision");
        return new(workspaceId, snapshot, enumerationComplete,
            inputs.OrderBy(p => p.PackageId, StringComparer.Ordinal).Select(p => new VciPackageEvidence(p, issues[p.PackageId])).ToArray(),
            inventory.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray());
    }
}

internal static class VciGuard
{
    internal static void Text(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
            throw new BridgeException("VciInvalidManifest", name + " must be bounded nonempty text.");
    }
    internal static void Hash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new BridgeException("VciInvalidHash", "SHA-256 must be 64 lowercase hexadecimal characters.");
    }
    internal static string ObjectKey(EngineeringObjectReference value) =>
        CanonicalJson.Sha256(JsonSerializer.SerializeToNode(value.Object, BridgeProtocol.Json)!);
    internal static IEnumerable<string> Ancestors(string path)
    {
        for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1)) yield return path[..index];
    }
    internal static void Path(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.StartsWith('/') || value.Contains('\\'))
            throw new BridgeException("VciUnsafePath", "Use a bounded relative path with forward slashes.");
        try { _ = value.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException ex) { throw new BridgeException("VciUnsafePath", "Path contains invalid Unicode.", ex); }
        foreach (var segment in value.Split('/'))
        {
            var stem = segment.Split('.')[0].TrimEnd(' ', '.');
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => char.IsControl(c) || ":<>\"|?*".Contains(c)) ||
                new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new BridgeException("VciUnsafePath", "Unsafe Windows path segment.");
        }
    }
    internal static void Profile(VciProfileInput p, string kind, int maxPath)
    {
        if (p is null) throw new BridgeException("VciInvalidProfile", "An explicit observed profile is required.");
        Text(p.ProfileId, "profileId"); Text(p.NativeFormat, "nativeFormat"); Hash(p.EvidenceSha256);
        if (p.EvidenceOrigin is not ("authoredFixture" or "nativeExport")) throw new BridgeException("VciInvalidProfile", "Profile origin must be explicit.");
        if (!StringComparer.Ordinal.Equals(p.ObjectKind, kind)) throw new BridgeException("VciInvalidProfile", "Profile object kind does not match exact target kind.");
        if (p.ExpectedFiles is null || p.ExpectedFiles.Count is < 1 or > 20000) throw new BridgeException("VciInvalidProfile", "Expected files are required and bounded.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in p.ExpectedFiles)
        {
            if (f is null) throw new BridgeException("VciInvalidProfile", "Null expected file.");
            Path(f.RelativePath, maxPath); Text(f.Role, "role");
            if (!paths.Add(f.RelativePath.Normalize(NormalizationForm.FormC))) throw new BridgeException("VciPathCollision", "Profile file paths collide.");
            if (f.Role is not ("source" or "resources" or "script" or "metadata" or "document" or "libraryVersion" or "auxiliary"))
                throw new BridgeException("VciInvalidProfile", "Unknown file role.");
        }
        foreach (var f in p.ExpectedFiles)
            if (Ancestors(f.RelativePath).Any(a => paths.Contains(a.Normalize(NormalizationForm.FormC))))
                throw new BridgeException("VciPathCollision", "A profile file is also a directory ancestor.");
        bool One(string role, string suffix) => p.ExpectedFiles.Count(f => f.Role == role) == 1 &&
            p.ExpectedFiles.Single(f => f.Role == role).RelativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        bool roles = p.Family switch
        {
            "plcSimaticSd" => One("source", ".s7dcl") && One("resources", ".s7res"),
            "unifiedScript" => One("script", ".js") && One("metadata", ".yml"),
            "nativeDocument" => p.ExpectedFiles.Any(f => f.Role == "document"),
            "libraryDocument" => p.ExpectedFiles.Any(f => f.Role is "document" or "source") && p.ExpectedFiles.Count(f => f.Role == "libraryVersion") == 1 &&
                (!p.ExpectedFiles.Any(f => f.RelativePath.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase)) || (One("source", ".s7dcl") && One("resources", ".s7res"))),
            _ => false
        };
        if (!roles) throw new BridgeException("VciInvalidProfile", "Observed profile must declare its complete required role set.");
        if (p.Family == "libraryDocument")
        {
            if (!Guid.TryParseExact(p.LibraryTypeGuid, "D", out var type) || type == Guid.Empty ||
                !Guid.TryParseExact(p.LibraryVersionGuid, "D", out var version) || version == Guid.Empty)
                throw new BridgeException("VciInvalidProfile", "Library profiles require exact nonempty type and version GUIDs.");
        }
        else if (p.LibraryTypeGuid is not null || p.LibraryVersionGuid is not null)
            throw new BridgeException("VciInvalidProfile", "Library identities require a library profile.");
    }
}
