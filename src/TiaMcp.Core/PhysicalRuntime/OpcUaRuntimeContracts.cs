using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core.Security;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>Explicit online UA selection. Separate from offline NodeSet identities and Web API sessions.</summary>
public sealed record OpcUaRuntimeSelection(string Endpoint, string ApplicationUri, string CertificateSha256,
    string Authentication = "anonymous", string? User = null, string? VaultName = null, CredentialReference? Credential = null);
public sealed record OpcUaRuntimeNode(string NamespaceUri, string IdentifierType, string Identifier);
/// <summary>One gated owned-PLCSIM scalar write. Encoding, Expected and Value use runtime_ua_read's exact encoding and value forms.</summary>
public sealed record OpcUaRuntimeWriteItem(OpcUaRuntimeNode Node, string Encoding, JsonElement Expected, JsonElement Value);
/// <summary>One scalar input of a gated owned-PLCSIM method call, in runtime_ua_read's exact encoding and value forms.</summary>
public sealed record OpcUaRuntimeCallArgument(string Encoding, JsonElement Value);
public sealed record OpcUaRuntimeStatus(string SchemaVersion, string AttemptId, string HostId, OpcUaRuntimeSelection Selection,
    string State, string? ErrorCode, bool PeerVerified, DateTimeOffset UpdatedAt)
{
    public string SecurityPolicy => "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256";
    public string SecurityMode => "SignAndEncrypt";
    public bool PhysicalQualified => false;
    public bool ProgramIdentityVerified => false;
    public bool SecretsReturned => false;
    public string? OperationErrorCode { get; init; }
    /// <summary>The server's revised session timeout (the request asks for 600 s); a session unused for longer is retired locally. Absent in older records.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SessionTimeoutMs { get; init; }
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
/// <summary>A tool result too large for the reply: the complete result JSON is the hashed artifact (read it with artifact_read).</summary>
public sealed record OpcUaRuntimeSpill(string Artifact, string Sha256, long Bytes, int Count);
public sealed record OpcUaRuntimeReadResult(string AttemptId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<OpcUaRuntimeValue> Items)
{
    /// <summary>Set only on a tool reply whose Items moved to the artifact; never on a stored or captured result.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpcUaRuntimeSpill? Spill { get; init; }
}
public sealed record OpcUaRuntimeBrowseResult(string AttemptId, OpcUaRuntimeNode Parent,
    IReadOnlyList<OpcUaRuntimeReference> References, bool Truncated, bool ContinuationReleased)
{
    /// <summary>Set only on a tool reply whose References moved to the artifact.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpcUaRuntimeSpill? Spill { get; init; }
}
