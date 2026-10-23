using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.WatchTables;

public sealed record WatchTableInstance(EngineeringObjectReference Storage, IReadOnlyList<string> MemberPath);
public sealed record WatchTableExternalStorage(EngineeringObjectReference Storage, string Reason);
public sealed record WatchTableTemplateSlot(string Id, string Role, string Datatype);
public sealed record WatchTableTemplate(string Id, string Version, IReadOnlyList<WatchTableTemplateSlot> Slots);
public sealed record WatchTableTemplateBinding(string SlotId, string CandidateId, string Reason);

/// <summary>Explicit normalized captured evidence, not a language/instance resolver.</summary>
public sealed record WatchTableCandidate(
    string Id, string SourceKind, EngineeringObjectReference SourceObject,
    EngineeringObjectReference Storage, IReadOnlyList<string> MemberPath,
    string Datatype, string Role, string Reason, string Resolution, JsonObject Evidence,
    IReadOnlyList<string>? SymbolComponents = null, string? UnavailableReason = null,
    string? NetworkId = null, string StorageClass = "unknown");

/// <summary>Supplied offline observation; never promoted to a verified live address.</summary>
public sealed record WatchTableAddressObservation(
    string CandidateId, EngineeringObjectReference Storage, IReadOnlyList<string> MemberPath,
    string Datatype, string Kind, string ObservedAtUtc, JsonObject Evidence,
    IReadOnlyList<string>? SymbolComponents = null, string? Area = null,
    long? StartBit = null, int? WidthBits = null, int? DbNumber = null, bool? OptimizedDb = null);

public sealed record WatchTablePlanningRequest
{
    public required EngineeringObjectReference ExpectedSource { get; init; }
    public required EngineeringObjectReference CapturedSource { get; init; }
    public required WatchTableInstance Instance { get; init; }
    public required string TableName { get; init; }
    public JsonObject? DiagnosticCase { get; init; }
    public string? DiagnosticCaseSha256 { get; init; }
    public IReadOnlyList<WatchTableCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<WatchTableExternalStorage> ExternalStorage { get; init; } = [];
    public IReadOnlyList<WatchTableAddressObservation> AddressObservations { get; init; } = [];
    public WatchTableTemplate? Template { get; init; }
    public IReadOnlyList<WatchTableTemplateBinding> TemplateBindings { get; init; } = [];
    public string DuplicatePolicy { get; init; } = "preserve";
    public int MaxRows { get; init; } = 64;
}
