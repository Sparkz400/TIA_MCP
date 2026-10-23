using TiaMcp.Core.Engineering;
using System.Text.Json.Serialization;
using TiaMcp.Core.Security;

namespace TiaMcp.Core.EngineeringJobs;

public sealed record EngineeringArtifactReference(string RelativePath, string Sha256);
public sealed record EngineeringResourceEffect(string ResourceKey, string Kind, string Effect, string RecoveryStrategy)
{
    // Omission preserves every pre-extension local plan and journal hash.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EngineeringExternalEffect? External { get; init; }
}
public sealed record EngineeringExternalIdentity(string Provider, string Authority, string OwnerId, string? ResourceKind, string? ResourceId);
public sealed record EngineeringExternalState(string Presence, string Sha256);
public sealed record EngineeringExternalEffect(EngineeringExternalIdentity Identity, EngineeringExternalState Before, EngineeringExternalState After);
public sealed record EngineeringExternalObservation(string SchemaVersion, string OperationId, string PlanSha256, int Revision,
    string ResourceKey, string Phase, string ObservationId, EngineeringExternalState State, EngineeringArtifactReference StateArtifact);
public sealed record EngineeringOperationPlan(string Operation, NativeTargetObservation Target,
    IReadOnlyList<EngineeringResourceEffect> Resources, IReadOnlyList<EngineeringArtifactReference> Inputs,
    IReadOnlyList<string> RequiredProbes, IReadOnlyList<string> RequiredGates)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EngineeringCredentialRequirement>? Credentials { get; init; }
}
public sealed record EngineeringGateEvidence(string Gate, EngineeringArtifactReference Artifact);
public sealed record ResourceReconciliation(string ResourceKey, string Disposition, string? RecoveryLocation,
    EngineeringArtifactReference Evidence);
public sealed record EngineeringRecoveryDestination(string ResourceKey, string RecoveryLocation);
public sealed record EngineeringRecoveryResolution(string ResourceKey, string RecoveryLocation, string Disposition,
    EngineeringArtifactReference Evidence);
public sealed record EngineeringOperationRecord(string Id, int Revision, string State, string IdempotencyKey,
    string PlanSha256, EngineeringOperationPlan Plan, string? PreviousSha256, DateTimeOffset RecordedAt,
    IReadOnlyList<EngineeringArtifactReference> Checkpoints, IReadOnlyList<EngineeringGateEvidence> Evidence,
    IReadOnlyList<ResourceReconciliation> Reconciliation)
{
    public IReadOnlyList<EngineeringRecoveryDestination> RecoveryDestinations { get; init; } = [];
    public IReadOnlyList<EngineeringRecoveryResolution> RecoveryResolutions { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeTargetObservation? RecoveryTarget { get; init; }
}
