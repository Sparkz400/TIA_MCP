using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class RuntimeCaptureTools(RuntimeCaptures captures)
{
    [McpServerTool(Name="runtime_capture_start",ReadOnly=false,Destructive=false,OpenWorld=true)]
    [Description("Start bounded read-only Web polling for explicit host-owned selections. Supply caller-retained 32-hex captureId and request with attemptId, selectionIds, intervalMilliseconds (100..60000), durationSeconds (1..3600), optional maximumSamples (1..100000), maximumBytes (1..64 MiB), reviewId, representation (simple or raw). Optional association:{caseId,mappings:[{selectionId,signalId,reason}],assertedProgramIdentity} requires reviewId and maps every selection in request order to a unique diagnostic-case signal. Saved source instance/declarations are re-resolved; unresolved or mismatched datatypes refuse. Retains explicit caller mappings, not verified online address equivalence or program identity. Reusing the same ID observes intent without source reread or restart. Optional handoffRef requires reviewId and a retained consistent runtime handoff matching the actual session owner/context/subject/mode/credential. Bound original evidence is revalidated before every read; changes stop polling. This is evidence association only, never target/program/permission proof or operation admission. At most one capture per session and four workers. No writes, reconnect/login or scan synchronization.")]
    public Task<CallToolResult> Start(string captureId,RuntimeCaptureRequest request,CancellationToken cancellationToken) => Run(()=>captures.Start(captureId,request,cancellationToken));
    [McpServerTool(Name="runtime_capture_status",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Reopen the verified committed capture prefix by captureId without target access. Returns a hash-bound snapshotRef, plan, quality limits, terminal/interrupted state and unknown tail/loss. Host restart never resumes acquisition. Local hashes prove byte consistency, not CPU identity or source authenticity.")]
    public Task<CallToolResult> Status(string captureId,CancellationToken cancellationToken) => Run(()=>captures.Status(captureId,cancellationToken));
    [McpServerTool(Name="runtime_capture_stop",ReadOnly=false,Destructive=false,OpenWorld=false)]
    [Description("Cancel this Host's owned polling capture, retain partial evidence and publish its final prefix. Does not send logout or change PLC values. Cancellation during identity verification can invalidate the session; explicit disconnect and a fresh connection attempt may then be required. Repeating after completion/restart is local observation only.")]
    public Task<CallToolResult> Stop(string captureId,CancellationToken cancellationToken) => Run(()=>captures.Stop(captureId,cancellationToken));
    [McpServerTool(Name="runtime_capture_page",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Read an immutable hash-bound capture snapshotRef in pages of 1..200 rows within 256 KiB. Read results are exact JSON strings to preserve 64-bit values in browser clients; errors and skipped-poll gap rows remain explicit. Oversized rows are marked omitted and remain available in exports. No network activity or automatic recovery.")]
    public Task<CallToolResult> Page(string snapshotRef,int offset=0,int count=50) => Run(()=>Task.FromResult(captures.Page(snapshotRef,offset,count)));
    [McpServerTool(Name="runtime_capture_analyze",ReadOnly=false,Destructive=false,OpenWorld=false)]
    [Description("Analyze one hash-bound capture snapshotRef against its retained associated diagnostic case. units maps every captured case signal ID to an explicit unit; Bool requires '1', numeric prediction units must match exactly, no conversion. Case must remain available; captures with handoffRef also require unchanged original handoff/access/model dependencies. Legacy captures do not require the original saved review. Streams all committed rows and groups sequential reads by polling cycle without carry-forward or interpolation. Exact supported scalar comparisons, per-read time evidence, errors, unknowns, skipped/dropped polls and bounded examples/observed changes are retained. Raw/nonrepresentable values remain unknown. All verdicts stay unresolved: address/program identity and atomicity are unverified. Optional versioned handoff evidence remains separate from sample quality; all assertion verdicts remain unresolved. No network or native access. Export returned id with diagnostic_report_export.")]
    public Task<CallToolResult> Analyze(string snapshotRef,JsonElement units)=>Run(()=>Task.FromResult(captures.Analyze(snapshotRef,JsonNode.Parse(units.GetRawText())!.AsObject())));
    [McpServerTool(Name="diagnostic_experiment_acquire_runtime",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Attach an existing immutable runtime snapshotRef to its exact retained diagnostic experimentId; this does not start acquisition. request:{reason,units} supplies a reason and explicit units for every captured case signal. Revalidates the original experiment, saved source, capture association and committed snapshot chain. Retains exact raw read JSON, separate read times, errors, gaps, terminal/truncation state and sequential analysis in a distinct diagnostic-runtime-acquisition.v1 record. No interpolation or conversion to atomic trace samples. Observation counts remain descriptive; all hypothesis verdicts stay unresolved because online address/program identity and atomicity are unverified. Bounded to 10000 original rows, 3 MiB of original plan/segment bytes and an 8 MiB acquisition record; oversized input refuses rather than silently truncating. No target access, reconnect, native authority or fixed-fault claim.")]
    public Task<CallToolResult> AcquireDiagnostic(string experimentId,string snapshotRef,JsonElement request)
        =>Run(()=>Task.FromResult(captures.AcquireDiagnostic(experimentId,snapshotRef,
            JsonNode.Parse(request.GetRawText()) as JsonObject??throw new BridgeException("InvalidRequest","Expected a request object."))));
    [McpServerTool(Name="diagnostic_runtime_acquisition_export",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Export a retained diagnostic runtime acquisitionId as json, markdown or html. Reopens and rederives its original experiment, associated immutable capture snapshot and sequential analysis before returning hashed artifacts for artifact_read. Readable bounded previews include complete acquisition JSON, exact source references, per-read timing, errors/gaps, terminal state and unresolved identity/quality limits. HTML is escaped and self-contained without scripts or external resources. Existing retention and 8 MiB output bounds apply. No network acquisition, source authentication, synchronized clocks, causal inference or native qualification.")]
    public Task<CallToolResult> ExportDiagnosticAcquisition(string acquisitionId,string format)
        =>Run(()=>Task.FromResult(captures.ExportDiagnosticAcquisition(acquisitionId,format)));
    [McpServerTool(Name="runtime_capture_export",ReadOnly=false,Destructive=false,OpenWorld=false)]
    [Description("Export a verified capture snapshotRef as json or csv. CSV includes companion JSON retaining plan, context, quality and terminal state. Values remain exact JSON strings; exports do not interpolate failures/gaps or prove program identity. Download returned hashed files through artifact_read and open capture JSON in Explorer. Global retained data limit is 512 MiB; no eviction or target access.")]
    public Task<CallToolResult> Export(string snapshotRef,CancellationToken cancellationToken,string format="json") => Run(()=>captures.Export(snapshotRef,format,cancellationToken));
    private static async Task<CallToolResult> Run(Func<Task<object>> action)
    {
        object result;bool failed=false;
        try{result=await action();}catch(Exception error){failed=true;result=new {error=new{code=error is BridgeException e?e.Code:"RuntimeCaptureFailed",message="Capture unavailable or incomplete. No remote error text, credentials or automatic restart."}};}
        var json=JsonSerializer.SerializeToElement(result,BridgeProtocol.Json);
        return new(){IsError=failed,StructuredContent=json,Content=[new TextContentBlock{Text=json.GetRawText()}]};
    }
}
