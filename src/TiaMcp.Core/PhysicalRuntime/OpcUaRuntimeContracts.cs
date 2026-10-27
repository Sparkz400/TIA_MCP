using System.Text.Json;
using TiaMcp.Core.Security;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>Explicit online UA selection. Separate from offline NodeSet identities and Web API sessions.</summary>
public sealed record OpcUaRuntimeSelection(string Endpoint, string ApplicationUri, string CertificateSha256,
    string Authentication = "anonymous", string? User = null, string? VaultName = null, CredentialReference? Credential = null);
public sealed record OpcUaRuntimeNode(string NamespaceUri, string IdentifierType, string Identifier);
/// <summary>One gated owned-PLCSIM scalar write. Encoding, Expected and Value use runtime_ua_read's exact encoding and value forms.</summary>
public sealed record OpcUaRuntimeWriteItem(OpcUaRuntimeNode Node, string Encoding, JsonElement Expected, JsonElement Value);
public sealed record OpcUaRuntimeStatus(string SchemaVersion, string AttemptId, string HostId, OpcUaRuntimeSelection Selection,
    string State, string? ErrorCode, bool PeerVerified, DateTimeOffset UpdatedAt)
{
    public string SecurityPolicy => "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256";
    public string SecurityMode => "SignAndEncrypt";
    public bool PhysicalQualified => false;
    public bool ProgramIdentityVerified => false;
    public bool SecretsReturned => false;
    public string? OperationErrorCode { get; init; }
}
public sealed record OpcUaRuntimeValue(OpcUaRuntimeNode Node, string Outcome, string? BuiltInType,
    OpcUaRuntimeNode? DataType, int? ValueRank, uint[]? ArrayDimensions, string StatusCode, string Quality,
    DateTimeOffset? SourceTimestamp, DateTimeOffset? ServerTimestamp, ushort SourcePicoseconds,
    ushort ServerPicoseconds, string? Encoding, object? Value, string? ErrorCode);
/// <summary>Flat row-major values with observed dimensions, separate from server-declared maxima.</summary>
public sealed record OpcUaRuntimeArray(int[]? Dimensions, string ElementEncoding, object?[]? Elements);
public sealed record OpcUaRuntimeReference(OpcUaRuntimeNode? Node, string NodeClass, string BrowseName,
    string? BrowseNameNamespaceUri, string DisplayName, bool IsForward, OpcUaRuntimeNode? ReferenceType,
    OpcUaRuntimeNode? TypeDefinition, string? UnsupportedReason);
public sealed record OpcUaRuntimeReadResult(string AttemptId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<OpcUaRuntimeValue> Items);
public sealed record OpcUaRuntimeBrowseResult(string AttemptId, OpcUaRuntimeNode Parent,
    IReadOnlyList<OpcUaRuntimeReference> References, bool Truncated, bool ContinuationReleased);
