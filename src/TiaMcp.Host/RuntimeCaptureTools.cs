using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class RuntimeCaptureTools(RuntimeCaptures captures)
{
    [McpServerTool(Name = "runtime_capture_start", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Start bounded read-only polling under a caller-retained 32-hex captureId; reusing it observes the same intent. Web: request {attemptId, selectionIds, intervalMilliseconds 100..60000, durationSeconds 1..3600, optional maximumSamples, maximumBytes, reviewId, representation}; optional association or handoffRef bind evidence only, never target or program proof. source:'plcsim' polls an owned PLCSIM Advanced instance (simulator {...}); unowned refused. No writes, reconnect or login. Details: docs/reference/tool-notes.md#runtime_capture_start.")]
    public Task<CallToolResult> Start(string captureId, RuntimeCaptureRequest request, CancellationToken cancellationToken) => Run(() => captures.Start(captureId, request, cancellationToken));
    [McpServerTool(Name = "runtime_capture_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen the verified committed capture prefix by captureId without target access. Returns a hash-bound snapshotRef, plan, quality limits, terminal/interrupted state and unknown tail/loss. Host restart never resumes acquisition. Local hashes prove byte consistency, not CPU identity or source authenticity.")]
    public Task<CallToolResult> Status(string captureId, CancellationToken cancellationToken) => Run(() => captures.Status(captureId, cancellationToken));
    [McpServerTool(Name = "runtime_capture_stop", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Cancel this Host's owned polling capture, retain partial evidence and publish its final prefix. Does not send logout or change PLC values; a cycle-synchronous PLCSIM capture restores its recorded operating mode. Cancellation during identity verification can invalidate the session; explicit disconnect and a fresh connection attempt may then be required. Repeating after completion/restart is local observation only.")]
    public Task<CallToolResult> Stop(string captureId, CancellationToken cancellationToken) => Run(() => captures.Stop(captureId, cancellationToken));
    [McpServerTool(Name = "runtime_capture_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read an immutable hash-bound capture snapshotRef in pages of 1..200 rows within 256 KiB. Read results are exact JSON strings to preserve 64-bit values in browser clients; errors and skipped-poll gap rows remain explicit. Oversized rows are marked omitted and remain available in exports. No network activity or automatic recovery.")]
    public Task<CallToolResult> Page(string snapshotRef, int offset = 0, int count = 50) => Run(() => Task.FromResult(captures.Page(snapshotRef, offset, count)));
    [McpServerTool(Name = "runtime_capture_analyze", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Analyze one hash-bound capture snapshotRef against its retained diagnostic case. units maps every captured case signal ID to an explicit unit (Bool '1'; numeric units must match, no conversion). Groups sequential reads by polling cycle without interpolation; retains per-read time evidence, errors, unknowns and skipped polls. All verdicts stay unresolved: address/program identity and atomicity are unverified. No network or native access. Export the returned id with diagnostic_report_export.")]
    public Task<CallToolResult> Analyze(string snapshotRef, JsonElement units) => Run(() => Task.FromResult(captures.Analyze(snapshotRef, JsonNode.Parse(units.GetRawText())!.AsObject())));
    [McpServerTool(Name = "diagnostic_experiment_acquire_runtime", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Attach an existing immutable runtime capture snapshotRef to its retained diagnostic experimentId; starts no acquisition. request:{reason,units} with explicit units for every captured case signal. Revalidates experiment, saved source, association and snapshot chain; retains exact raw reads, gaps and sequential analysis as a diagnostic-runtime-acquisition.v1 record. Verdicts stay unresolved; oversized input refuses. No target access, reconnect or fixed-fault claim.")]
    public Task<CallToolResult> AcquireDiagnostic(string experimentId, string snapshotRef, JsonElement request)
        => Run(() => Task.FromResult(captures.AcquireDiagnostic(experimentId, snapshotRef,
            JsonNode.Parse(request.GetRawText()) as JsonObject ?? throw new BridgeException("InvalidRequest", "Expected a request object."))));
    [McpServerTool(Name = "diagnostic_runtime_acquisition_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export a retained diagnostic runtime acquisitionId as json, markdown or html after rederiving its experiment, capture snapshot and analysis; returns hashed artifacts for artifact_read with source references, per-read timing, errors, gaps and unresolved identity/quality limits. HTML is escaped and self-contained without scripts. 8 MiB output bound. No network acquisition, clock synchronization, causal inference or native qualification.")]
    public Task<CallToolResult> ExportDiagnosticAcquisition(string acquisitionId, string format)
        => Run(() => Task.FromResult(captures.ExportDiagnosticAcquisition(acquisitionId, format)));
    [McpServerTool(Name = "diagnostic_runtime_acquisition_compare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare two retained diagnostic runtime acquisitions of the same experimentId (for example before and after diagnostic_plcsim_write) against the case's hypothesis predictions. request:{reason}; the two must come from distinct captures and are rederived from their original chains. Reports matched/contradicted/unknown cycles and pattern changes per hypothesis; persists a diagnostic-runtime-acquisition-comparison.v1 record. Verdicts stay unresolved. No target access, causation or fix proof.")]
    public Task<CallToolResult> CompareDiagnosticAcquisitions(string beforeAcquisitionId, string afterAcquisitionId, JsonElement request)
        => Run(() => Task.FromResult(captures.CompareDiagnosticAcquisitions(beforeAcquisitionId, afterAcquisitionId,
            JsonNode.Parse(request.GetRawText()) as JsonObject ?? throw new BridgeException("InvalidRequest", "Expected a request object."))));
    [McpServerTool(Name = "diagnostic_plcsim_write", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Write 1..16 signals of a retained diagnostic_plan caseId to a PLCSIM Advanced instance this Host owns (instanceId, instanceName, creationId). writes:[{signalId,symbol,type,datatype,expected,value,reason}]; requires acknowledgeWrite=true. Writes nothing unless every signal reads its expected value; each write is sent once and read back, stopping at the first failure. Output-area signals and instances under capture refuse. Never writes physical PLCs or unowned instances; no forcing or retries.")]
    public Task<CallToolResult> WriteDiagnosticPlcsim(string caseId, int instanceId, string instanceName, string creationId, DiagnosticPlcsimWrite[] writes, bool acknowledgeWrite, CancellationToken cancellationToken)
        => Run(async () =>
        {
            if (!acknowledgeWrite) throw new BridgeException("RuntimeAcknowledgementRequired", "Set the explicit acknowledgement flag; nothing was sent.");
            return await captures.WriteDiagnosticPlcsim(caseId, instanceId, instanceName, creationId, writes, cancellationToken);
        }, "Write refused before dispatch, or its outcome is unknown; see the code. Never retried and never sent to a physical PLC.");
    [McpServerTool(Name = "runtime_capture_export", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Export a verified capture snapshotRef as json or csv. CSV includes companion JSON retaining plan, context, quality and terminal state. Values remain exact JSON strings; exports do not interpolate failures/gaps or prove program identity. Download returned hashed files through artifact_read and open capture JSON in Explorer. Global retained data limit is 512 MiB; no eviction or target access.")]
    public Task<CallToolResult> Export(string snapshotRef, CancellationToken cancellationToken, string format = "json") => Run(() => captures.Export(snapshotRef, format, cancellationToken));
    private static async Task<CallToolResult> Run(Func<Task<object>> action, string message = "Capture unavailable or incomplete. No remote error text, credentials or automatic restart.")
    {
        object result; bool failed = false;
        try { result = await action(); } catch (Exception error) { failed = true; result = new { error = new { code = error is BridgeException e ? e.Code : "RuntimeCaptureFailed", message } }; }
        var json = JsonSerializer.SerializeToElement(result, BridgeProtocol.Json);
        return new() { IsError = failed, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
