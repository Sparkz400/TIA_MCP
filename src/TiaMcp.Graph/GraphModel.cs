namespace TiaMcp.Graph;

public sealed record GraphDiagnostic(string Severity, string Code, string Path, string Message);

/// <summary>XML is an inspection serialization; the document's original bytes are the preservation authority.</summary>
public sealed record GraphFragment(string Path, string Xml);
public sealed record GraphUnsupported(string Code, string Path, string Name, string Xml);
public sealed record GraphAction(int Ordinal, string? Event, string? Qualifier, bool? Interlock, GraphFragment Content);
public sealed record GraphCondition(string? ProgrammingLanguage, GraphFragment Content);
public sealed record GraphStep(int? Number, string? Name, bool? Init, bool? IsMissing,
    string? MaximumStepTime, string? WarningTime, IReadOnlyList<GraphAction> Actions,
    IReadOnlyList<GraphCondition> Interlocks, IReadOnlyList<GraphCondition> Supervisions, GraphFragment Content);
public sealed record GraphTransition(int? Number, string? Name, bool? IsMissing,
    string? ProgrammingLanguage, GraphFragment? Condition, GraphFragment Content);
public sealed record GraphBranch(int? Number, string? Type, int? Cardinality, GraphFragment Content);
public sealed record GraphEndpoint(string Kind, int? Number, int? In, int? Out, string Path);
public sealed record GraphConnection(int Ordinal, GraphEndpoint? From, GraphEndpoint? To, string? LinkType, GraphFragment Content);
public sealed record GraphSequence(int Ordinal, string Path, string? Title,
    IReadOnlyList<GraphStep> Steps, IReadOnlyList<GraphTransition> Transitions,
    IReadOnlyList<GraphBranch> Branches, IReadOnlyList<GraphConnection> Connections);
public sealed record GraphChart(int Ordinal, string Path, string Namespace,
    IReadOnlyList<GraphSequence> Sequences, IReadOnlyList<GraphFragment> PreOperations,
    IReadOnlyList<GraphFragment> PostOperations, IReadOnlyList<GraphFragment> AlarmSettings);
public sealed record GraphInspection(string Format, string Profile, string SourceSha256, int SourceBytes,
    bool NativeQualified, bool FullSchemaValidated, bool HasErrors, bool HasUnsupportedContent,
    IReadOnlyList<GraphChart> Graphs, IReadOnlyList<GraphDiagnostic> Diagnostics,
    IReadOnlyList<GraphUnsupported> Unsupported, string OriginalSourceBase64);

public sealed class GraphException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
