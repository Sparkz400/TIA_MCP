using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

/// <summary>
/// One list/reopen/page/artifact surface over the Host-owned, write-once record stores. Each call is dispatched to its
/// family's tool class, so the store, its revalidation and its error codes stay exactly those of the family.
/// </summary>
[McpServerToolType]
public sealed class ReviewRecordTools(IBridgeClient bridge, HostOptions options)
{
    /// <summary>Record kinds, advertised as the enum of the kind argument (ToolSurface).</summary>
    public static readonly string[] Kinds = ["automationml", "composite_engineering", "graph", "opcua", "runtime_handoff", "translation", "vci"];
    public static readonly string[] TranslationRecordTypes = ["capture", "review", "approval", "candidate", "reconciliation"];
    // Each kind's store enforces the shared bounds with its own error code: review_list 1..100 records, review_page 1..200 rows.

    [McpServerTool(Name = "review_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Discover retained records of one kind after a restart, including incomplete and integrity-failed publications; completed records are revalidated. count 1..100; offsets follow record-ID order, relist after additions. kind translation requires recordType (capture, review, approval, candidate or reconciliation). Never resumes, retries or replays interrupted work. Per-kind notes: docs/reference/tool-notes.md#review_list.")]
    public Task<CallToolResult> List(string kind, int offset = 0, int count = 20, string? recordType = null)
    {
        if ((kind == "translation") != (recordType is not null))
            return Refuse(kind == "translation" ? "kind translation requires recordType." : "recordType applies only to kind translation.");
        return kind switch
        {
            "automationml" => Sync(new AutomationMlTools(options).List(offset, count)),
            "composite_engineering" => Sync(new CompositeEngineeringTools(bridge, options).List(offset, count)),
            "graph" => Sync(new GraphTools(bridge, options).List(offset, count)),
            "opcua" => Sync(new OpcUaTools(options).List(offset, count)),
            "runtime_handoff" => Sync(new RuntimeHandoffTools(options).List(offset, count)),
            "translation" => Sync(new TranslationTools(options).List(recordType!, offset, count)),
            "vci" => new VciTools(options).List(offset, count),
            _ => UnknownKind(kind),
        };
    }

    [McpServerTool(Name = "review_reopen", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an exact retained record handle (recordRef) of the given kind after a restart. Verifies the receipt, every artifact and the complete retained input chain, rebuilding derived reports where the kind does; any change refuses. Returns bounded summaries, pageable sections and artifact names. Retained consistency only: no native call, freshness or qualification.")]
    public Task<CallToolResult> Reopen(string kind, string recordRef) => kind switch
    {
        "automationml" => Sync(new AutomationMlTools(options).Reopen(recordRef)),
        "composite_engineering" => Sync(new CompositeEngineeringTools(bridge, options).Reopen(recordRef)),
        "graph" => Sync(new GraphTools(bridge, options).Reopen(recordRef)),
        "opcua" => Sync(new OpcUaTools(options).Reopen(recordRef)),
        "runtime_handoff" => Sync(new RuntimeHandoffTools(options).Reopen(recordRef)),
        "translation" => Sync(new TranslationTools(options).Reopen(recordRef)),
        "vci" => new VciTools(options).Reopen(recordRef),
        _ => UnknownKind(kind),
    };

    [McpServerTool(Name = "review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page one array section of a retained record's report: 1..200 rows within a 128 KiB response; continue at nextOffset. section is a JSON pointer such as /objects for automationml and graph, a section named by review_reopen for opcua, and a name for composite_engineering (default mappings), runtime_handoff (default constraints) and translation (default edits; rows/proposals for reconciliations). Oversized rows are explicit omissions; read them with review_artifact_read. vci records have no report pages. Sections per kind: docs/reference/tool-notes.md#review_page.")]
    public Task<CallToolResult> Page(string kind, string recordRef, string? section = null, int offset = 0, int count = 50)
    {
        if (section is null && kind is "automationml" or "graph" or "opcua") return Refuse("kind " + kind + " requires section.");
        return kind switch
        {
            "automationml" => Sync(new AutomationMlTools(options).ReportPage(recordRef, section!, offset, count)),
            "composite_engineering" => Sync(new CompositeEngineeringTools(bridge, options).Page(recordRef, section ?? "mappings", offset, count)),
            "graph" => Sync(new GraphTools(bridge, options).ReportPage(recordRef, section!, offset, count)),
            "opcua" => Sync(new OpcUaTools(options).ReportPage(recordRef, section!, offset, count)),
            "runtime_handoff" => Sync(new RuntimeHandoffTools(options).Page(recordRef, section ?? "constraints", offset, count)),
            "translation" => Sync(new TranslationTools(options).ReviewPage(recordRef, section ?? "edits", offset, count)),
            "vci" => Refuse("vci records have no report pages; read their artifacts with review_artifact_read or files with vci_file_read."),
            _ => UnknownKind(kind),
        };
    }

    [McpServerTool(Name = "review_artifact_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Download one artifact of a retained record by the name its reopen or page returned (for example report.json or receipt.json), at most 64 KiB per call, with the complete SHA-256 and length; continue at nextOffset. Revalidates the record and its retained inputs; accepts no filesystem path. Unlike artifact_read, the bytes are checked against the record's receipt.")]
    public Task<CallToolResult> ArtifactRead(string kind, string recordRef, string name, long offset = 0, int count = 65536) => kind switch
    {
        "automationml" => Sync(new AutomationMlTools(options).ArtifactRead(recordRef, name, offset, count)),
        "composite_engineering" => Sync(new CompositeEngineeringTools(bridge, options).ArtifactRead(recordRef, name, offset, count)),
        "graph" => Sync(new GraphTools(bridge, options).ArtifactRead(recordRef, name, offset, count)),
        // OPC UA and translation artifacts are addressed as recordRef:name, the completeArtifactRef their pages return.
        "opcua" => Sync(new OpcUaTools(options).ReadArtifact(recordRef + ":" + name, offset, count)),
        "runtime_handoff" => Sync(new RuntimeHandoffTools(options).ArtifactRead(recordRef, name, offset, count)),
        "translation" => Sync(new TranslationTools(options).ReadArtifact(recordRef + ":" + name, offset, count)),
        "vci" => new VciTools(options).ArtifactRead(recordRef, name, offset, count),
        _ => UnknownKind(kind),
    };

    private static Task<CallToolResult> Sync(CallToolResult result) => Task.FromResult(result);
    private static Task<CallToolResult> Refuse(string message) => Task.FromResult(ToolResult.Error("InvalidRequest", message));
    private static Task<CallToolResult> UnknownKind(string kind) =>
        Refuse("Unknown record kind " + kind + ". Valid kinds: " + string.Join(", ", Kinds) + ".");
}
