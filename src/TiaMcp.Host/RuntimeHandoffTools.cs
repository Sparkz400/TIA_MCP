using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class RuntimeHandoffTools(HostOptions options)
{
    [McpServerTool(Name = "runtime_handoff_observation", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Retain exact public runtime-handoff-target.v1, runtime-handoff-program.v1 or runtime-handoff-session.v1 bytes from a committed upload. role: target/program/session. Returns a canonical artifact path/digest for handoff requests; source upload release cannot remove it. 1 byte..256 KiB. Supplied/synthetic observations only; no authenticated producer, live identity, effective rights or session/operation admission. Exact-byte retries return the same observation reference.")]
    public CallToolResult Observation(string role, string uploadRef) => Run(s => s.RetainObservation(role, uploadRef));

    [McpServerTool(Name = "runtime_handoff_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Retain an offline security/runtime review from a committed runtime-handoff-request.v1 upload (1 byte..128 KiB). Select exact saved review/access/owner references and six observation references returned by runtime_handoff_observation. Compare separate CPU/program/context declarations, baseline/current scoped allowed/denied permissions and invalidation. Unknown required evidence stays unknown. No live admission, native action or capture assertion. Each invocation creates a new record; discover lost responses with runtime_handoff_list.")]
    public CallToolResult Review(string uploadRef) => Run(s => s.Review(uploadRef));

    [McpServerTool(Name = "runtime_handoff_reopen", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an exact retained security/runtime handoff handle after restart. Verify intent, receipt, original request/report, all six observations and saved access/model dependencies; rebuild the report. Missing or changed evidence refuses. Matching declarations never create a live session or authorize reads, writes, mode changes, download or assertions.")]
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    [McpServerTool(Name = "runtime_handoff_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page a revalidated handoff report: constraints/permissions/baselineObservations/currentObservations. 1..200 rows within a 128 KiB structured result. Oversized observation rows are explicitly omitted; complete original bytes and report.json remain downloadable. Permission outcomes are separate from public grant declarations. No operation or assertion admission.")]
    public CallToolResult Page(string recordRef, string section = "constraints", int offset = 0, int count = 50) => Run(s => s.Page(recordRef, section, offset, count));

    [McpServerTool(Name = "runtime_handoff_artifact_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Download exact request.json, report.json, receipt.json or baseline.target.json/baseline.program.json/baseline.session.json/current.target.json/current.program.json/current.session.json. 1..65536 bytes per call with exact length and digest. Every call revalidates the complete evidence chain. No arbitrary path, network access, source authentication or native authority.")]
    public CallToolResult ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(recordRef, name, offset, count));

    [McpServerTool(Name = "runtime_handoff_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Discover 1..20 retained handoff records, including incomplete or verification-failed publications, plus retained/pending observation-file counts. Completed candidates revalidate original dependencies. Limits: 128 review records, 512 MiB and 2048 entries including incomplete files. Record-ID ordering; relist after additions. Never resumes interrupted work or replays an unknown native mutation.")]
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    private CallToolResult Run(Func<RuntimeHandoffWorkflowStore, object> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory, options.LibraryLimits)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            failed = true; result = new { error = new { code = e is BridgeException b ? b.Code : "HandoffWorkflowFailed",
                message = e is BridgeException ? e.Message : "Public handoff input, retained integrity or publication failed. No native action occurred.", nativeAccessAttempted = false } };
        }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
