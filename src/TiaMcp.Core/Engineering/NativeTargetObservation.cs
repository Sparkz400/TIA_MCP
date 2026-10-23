namespace TiaMcp.Core.Engineering;

/// <summary>Facts supplied by an admitted native adapter, never by selector equality alone.</summary>
public sealed record NativeTargetObservation(EngineeringObjectReference Reference, string ProjectFilePath, string SessionEpoch,
    string AdapterSha256, string ProfileSha256, string HardwareIdentity, string? NativeIdentity,
    bool Offline, IReadOnlyList<TargetProbe> Probes);

public sealed record TargetProbe(string Name, string State, string EvidenceSha256);

public static class NativeTargetGuard
{
    private static readonly string[] States = ["observed", "empty", "absent", "blocked", "failed", "unknown"];

    public static void Validate(NativeTargetObservation value)
    {
        if (value is null) throw new BridgeException("InvalidIdentity", "A native observation is required.");
        EngineeringIdentityGuard.Validate(value.Reference);
        _ = EngineeringJobs.EngineeringResourceKey.ForPath("project", value.ProjectFilePath);
        Text(value.SessionEpoch); Text(value.HardwareIdentity);
        if (value.NativeIdentity is not null) Text(value.NativeIdentity);
        EngineeringIdentityGuard.Sha256(value.AdapterSha256, "adapterSha256");
        EngineeringIdentityGuard.Sha256(value.ProfileSha256, "profileSha256");
        if (value.Probes is null || value.Probes.Count > 256 || value.Probes.Any(p => p is null) ||
            value.Probes.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.Probes.Count)
            throw new BridgeException("InvalidIdentity", "Probe names must be unique and bounded.");
        foreach (var probe in value.Probes)
        {
            Text(probe.Name);
            if (!States.Contains(probe.State)) throw new BridgeException("InvalidIdentity", "Unknown probe state.");
            EngineeringIdentityGuard.Sha256(probe.EvidenceSha256, "probeEvidence");
        }
    }

    /// <summary>Reacquisition permits a new observation ID, but no changed epoch, profile or content.</summary>
    public static void EnsureFresh(NativeTargetObservation expected, NativeTargetObservation observed,
        IEnumerable<string> requiredProbes)
    {
        EnsureOwner(expected, observed);
        if (expected.SessionEpoch != observed.SessionEpoch)
            throw new BridgeException("StaleNativeTarget", "Reprepare after reconnect, owner, binary, profile or offline-state change.");
        if (expected.Reference.ContentSha256 is null || observed.Reference.ContentSha256 is null ||
            expected.Reference.ContentSha256 != observed.Reference.ContentSha256 ||
            expected.Reference.Snapshot.SourceSha256 != observed.Reference.Snapshot.SourceSha256)
            throw new BridgeException("StaleContent", "Native content and preservation manifest must be independently reacquired.");
        foreach (var name in requiredProbes)
            if (!observed.Probes.Any(p => p.Name == name && p.State == "observed"))
                throw new BridgeException("TargetPrerequisiteMissing", "Required target probe is not observed: " + name);
    }

    /// <summary>Recovery reviews may observe changed content and a new epoch, but must retain the exact owner/profile.</summary>
    public static void EnsureRecoveryTarget(NativeTargetObservation original, NativeTargetObservation recovery)
    {
        EnsureOwner(original, recovery);
        if (original.Reference.ContentSha256 is null || recovery.Reference.ContentSha256 is null)
            throw new BridgeException("StaleContent", "Recovery requires independently observed content identities.");
    }

    private static void EnsureOwner(NativeTargetObservation expected, NativeTargetObservation observed)
    {
        Validate(expected); Validate(observed);
        EngineeringIdentityGuard.EnsureSameObject(expected.Reference, observed.Reference);
        if (EngineeringJobs.EngineeringResourceKey.ForPath("project", expected.ProjectFilePath) != EngineeringJobs.EngineeringResourceKey.ForPath("project", observed.ProjectFilePath) ||
            expected.AdapterSha256 != observed.AdapterSha256 || expected.ProfileSha256 != observed.ProfileSha256 ||
            expected.HardwareIdentity != observed.HardwareIdentity || expected.NativeIdentity != observed.NativeIdentity ||
            !observed.Offline || !expected.Offline || expected.Reference.Snapshot.TiaVersion != observed.Reference.Snapshot.TiaVersion)
            throw new BridgeException("StaleNativeTarget", "Reprepare after owner, binary, profile or offline-state change.");
    }

    private static void Text(string value) => EngineeringIdentityGuard.Text(value, "native identity", 8192);
}
