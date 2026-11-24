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

    // Served by review_reopen (ReviewRecordTools).
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    // Served by review_page (ReviewRecordTools).
    public CallToolResult Page(string recordRef, string section = "constraints", int offset = 0, int count = 50) => Run(s => s.Page(recordRef, section, offset, count));

    // Served by review_artifact_read (ReviewRecordTools).
    public CallToolResult ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(recordRef, name, offset, count));

    // Served by review_list (ReviewRecordTools).
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    private CallToolResult Run(Func<RuntimeHandoffWorkflowStore, object> action) =>
        ToolResult.Guard(() => action(new(options.ArtifactsDirectory, options.LibraryLimits)), "HandoffWorkflowFailed", "Public handoff input, retained integrity or publication failed. No native action occurred.", false);
}
