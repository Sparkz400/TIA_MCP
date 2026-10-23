using System.Text.RegularExpressions;

namespace TiaMcp.Core.Engineering;

/// <summary>Exact captured selectors. These strings are not newly invented native IDs.</summary>
public sealed record CapturedObjectIdentity(string ProjectPath, string? PlcPath, string? ScopePath,
    string ObjectPath, string Kind);

/// <summary>A bridge observation plus the digest of its source manifest, not a native revision counter.</summary>
public sealed record EngineeringSnapshotIdentity(string ObservationId, string SourceSha256, int TiaVersion = 21);

public sealed record EngineeringObjectReference(CapturedObjectIdentity Object,
    EngineeringSnapshotIdentity Snapshot, string? ContentSha256 = null);

/// <summary>Pure guards. Equality establishes capture consistency, never current native freshness.</summary>
public static partial class EngineeringIdentityGuard
{
    public static void Validate(EngineeringObjectReference reference)
    {
        if (reference is null || reference.Object is null || reference.Snapshot is null)
            throw new BridgeException("InvalidIdentity", "Object and snapshot identities are required.");
        var obj = reference.Object;
        Text(obj.ProjectPath, "projectPath", 8192);
        Text(obj.ObjectPath, "objectPath", 8192);
        Text(obj.Kind, "kind", 64);
        if (obj.PlcPath is not null) Text(obj.PlcPath, "plcPath", 8192);
        if (obj.ScopePath is not null) Text(obj.ScopePath, "scopePath", 8192);
        Text(reference.Snapshot.ObservationId, "observationId", 256);
        Sha256(reference.Snapshot.SourceSha256, "sourceSha256");
        if (reference.ContentSha256 is not null) Sha256(reference.ContentSha256, "contentSha256");
        if (reference.Snapshot.TiaVersion is < 1 or > 100)
            throw new BridgeException("InvalidIdentity", "TIA version must be explicitly identified.");
    }

    public static void EnsureSameObject(EngineeringObjectReference expected, EngineeringObjectReference observed)
    {
        Validate(expected); Validate(observed);
        if (expected.Object != observed.Object)
            throw new BridgeException("IdentityMismatch", "The exact project, PLC, scope, object and kind must match.");
    }

    public static void EnsureSameSnapshot(EngineeringObjectReference expected, EngineeringObjectReference observed)
    {
        Validate(expected); Validate(observed);
        if (expected.Object.ProjectPath != observed.Object.ProjectPath || expected.Snapshot != observed.Snapshot)
            throw new BridgeException("StaleObservation", "References must belong to the same exact project and captured source manifest.");
    }

    public static void EnsureFresh(EngineeringObjectReference expected, EngineeringObjectReference observed)
    {
        EnsureSameObject(expected, observed);
        EnsureSameSnapshot(expected, observed);
        if (expected.ContentSha256 != observed.ContentSha256)
            throw new BridgeException("StaleContent", "Content identity differs, including known versus unobserved content.");
    }

    public static void Sha256(string value, string field)
    {
        if (value is null || value.Length != 64 || !DigestPattern().IsMatch(value))
            throw new BridgeException("InvalidIdentity", field + " must be a lowercase SHA-256 digest.");
    }

    internal static void Text(string value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new BridgeException("InvalidIdentity", field + " must be a bounded, nonblank exact selector without control characters.");
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                    throw new BridgeException("InvalidIdentity", field + " contains invalid Unicode.");
            }
            else if (char.IsLowSurrogate(value[index])) throw new BridgeException("InvalidIdentity", field + " contains invalid Unicode.");
        }
        // Do not trim, case-fold, normalize Unicode or rewrite vendor object paths.
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
