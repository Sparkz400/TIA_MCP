using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Diagnostics;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class OpcUaRuntimeTools(OpcUaRuntimeSessions sessions, HostOptions options)
{
    [McpServerTool(Name = "diagnostic_ua_observe", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compose an offline diagnostic observation report from an exact immutable polling or subscription snapshotRef with a pre-acquisition diagnosticBindingId. Revalidates the original capture chain, binding, saved experiment and declarations. Retains complete raw rows and original plan/segment bytes, exact scalar strings/status/timestamps, gaps, duplicates, overflow and cleanup uncertainty. Per-signal counts describe unique observed values only; no interpolation, type conversion, simultaneous samples, behavioral assertion or online program/address proof. At most 10000 rows, 3 MiB original chain and 8 MiB report; oversized prefixes refuse without truncation. Returns a hashed artifact for artifact_read. No session access or acquisition replay.")]
    public Task<CallToolResult> ObserveDiagnostic(string snapshotRef) => Run(() => Task.FromResult<object>(Observations().Observe(snapshotRef)));

    [McpServerTool(Name = "diagnostic_ua_report_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export a retained diagnostic_ua_observe reportId as json, markdown or html. Reopens original capture/experiment/binding evidence and recomputes summaries before publishing. Missing or changed originals and honestly rehashed report changes refuse. JSON preserves exact original bytes; escaped self-contained readable exports retain quality/timing/sequence uncertainty, unresolved hypotheses and complete original report JSON. The JSON can be opened in portable Explorer. No target access, replay, behavioral assertion, physical/native qualification or source authentication.")]
    public Task<CallToolResult> ExportDiagnostic(string reportId, string format) => Run(() => Task.FromResult<object>(Observations().Export(reportId, format)));

    private DiagnosticUaObservations Observations() => new(options.ArtifactsDirectory, reference =>
        reference.StartsWith("capture:", StringComparison.Ordinal) ? sessions.Captures.DiagnosticSource(reference) :
        reference.StartsWith("ua-subscription:", StringComparison.Ordinal) ? sessions.Subscriptions.DiagnosticSource(reference) :
        throw new BridgeException("InvalidRequest", "Expected an immutable polling or subscription snapshot reference."));

    [McpServerTool(Name = "runtime_ua_client_identity", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Return this Host's ephemeral OPC UA client public certificate and application URI for explicit server trust provisioning. Private keys never leave Host memory. A Host restart creates a new client identity; no physical connection is performed.")]
    public Task<CallToolResult> ClientIdentity() => Run(() => Task.FromResult(sessions.Store.ClientIdentity()));
    [McpServerTool(Name = "runtime_ua_credential_binding", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Build the protected username credential binding for one exact opc.tcp endpoint, server application URI and leaf SHA256. Store a JSON-string password through the local credential CLI, never through MCP arguments. Set authentication=username; omit credential while obtaining this binding.")]
    public Task<CallToolResult> Binding(OpcUaRuntimeSelection selection) => Run(() => Task.FromResult<object>(OpcUaRuntimeSessionStore.Binding(selection)));
    [McpServerTool(Name = "runtime_ua_connect", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Create one owned, read-only OPC UA session at an explicit endpoint. Require exact server application URI and leaf SHA256, valid certificate hostname/time/key usage, Basic256Sha256 SignAndEncrypt, anonymous or protected username authentication. Supply a new 32-lowercase-hex attemptId. Reusing an attempt observes its retained outcome without replay. Unresolved same-endpoint attempts block fresh IDs. Polling or subscriptions require their separate explicit start tools. No scans, downgrade, writes, methods or automatic reconnect. Physical qualification remains unverified.")]
    public Task<CallToolResult> Connect(string attemptId, OpcUaRuntimeSelection selection, CancellationToken cancellationToken) =>
        Run(async () => await sessions.Store.Connect(attemptId, selection, cancellationToken));
    [McpServerTool(Name = "runtime_ua_browse", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Browse bounded immediate hierarchical references in this Host's owned UA session, using exact namespace URI and canonical numeric/string/guid/opaque identifier. At most 256 references and 16 pages; truncation and continuation cleanup are explicit. External server references are unsupported, not followed.")]
    public Task<CallToolResult> Browse(string attemptId, OpcUaRuntimeNode node, CancellationToken cancellationToken, int maximum = 256) =>
        Run(async () => await sessions.Store.Browse(attemptId, node, maximum, cancellationToken));
    [McpServerTool(Name = "runtime_ua_read", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read 1..64 explicit UA node identities after rechecking namespace/server/start identity. Returns exact scalar or bounded array/matrix type/value encoding, raw status and Good/Uncertain/Bad quality, nullable original source/server timestamps and per-item errors. Int64/UInt64 use exact decimal strings. Arrays retain observed row-major dimensions and up to 4096 exact elements; structures remain unsupported. No fabricated quality/timestamps, device/program identity guarantee or writes.")]
    public Task<CallToolResult> Read(string attemptId, OpcUaRuntimeNode[] nodes, CancellationToken cancellationToken) =>
        Run(async () => await sessions.Store.Read(attemptId, nodes, cancellationToken));
    [McpServerTool(Name = "runtime_ua_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read retained UA attempt state without network traffic. Host restart preserves hostEndedRemoteUnknown; session tokens and private keys are not persisted or adopted.")]
    public Task<CallToolResult> Status(string attemptId, CancellationToken cancellationToken) =>
        Run(async () => await sessions.Store.Status(attemptId, cancellationToken));
    [McpServerTool(Name = "runtime_ua_disconnect", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Boundedly close only this Host's owned UA session/channel. Unknown remote cleanup remains explicit. acknowledgeUnknown=true releases an unresolved local reservation as abandonedRemoteUnknown without claiming server logout, allowing a separately requested new attempt. No forced device action.")]
    public Task<CallToolResult> Disconnect(string attemptId, CancellationToken cancellationToken, bool acknowledgeUnknown = false) =>
        Run(async () => await sessions.Store.Disconnect(attemptId, acknowledgeUnknown, cancellationToken));
    [McpServerTool(Name = "runtime_ua_capture_start", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Start bounded sequential polling of 1..64 explicit namespace-URI UA nodes on this Host's existing encrypted owned attemptId. Supply caller-retained 32-lowercase-hex captureId and request:{attemptId,nodes,intervalMilliseconds:100..60000,durationSeconds:1..3600,maximumSamples:1..100000,maximumBytes:1048576..67108864}. Every read rechecks session identity and credential. Retains original decoded type/value/status/quality/source/server times and picoseconds as exact JSON; skipped polling slots and failed reads remain explicit. Same captureId observes its original intent without replay; changed intent refuses. At most one capture per session and four workers. No subscriptions, reconnect, writes, interpolation, atomic samples, source authentication or independent program/address association.")]
    public Task<CallToolResult> CaptureStart(string captureId, OpcUaCaptureRequest request, CancellationToken cancellationToken) =>
        Run(() => sessions.Captures.Start(captureId, request, cancellationToken));
    [McpServerTool(Name = "runtime_ua_capture_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen the verified committed UA capture prefix without target access. Returns immutable snapshotRef, original plan, terminal/interrupted state and unknown tail/loss. Host restart never resumes polling. Local hashes verify byte consistency, not device/program identity or original-source authenticity.")]
    public Task<CallToolResult> CaptureStatus(string captureId, CancellationToken cancellationToken) =>
        Run(() => sessions.Captures.Status(captureId, cancellationToken));
    [McpServerTool(Name = "runtime_ua_capture_stop", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Cancel this Host's owned UA polling capture and retain its final committed prefix. Does not reconnect or close the session; cancellation during a UA call can leave remoteSessionUnknown. Use explicit runtime_ua_status/disconnect for session cleanup. Repeated stop after completion/restart is local observation only.")]
    public Task<CallToolResult> CaptureStop(string captureId, CancellationToken cancellationToken) =>
        Run(() => sessions.Captures.Stop(captureId, cancellationToken));
    [McpServerTool(Name = "runtime_ua_capture_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page one immutable UA capture snapshotRef, offset >=0 and count 1..200. Exact per-node read JSON preserves 64-bit decimal strings, quality, status and nullable source/server timestamps. Oversized rows are visibly omitted from pages and remain in exports. No network access or resumed acquisition.")]
    public Task<CallToolResult> CapturePage(string snapshotRef, int offset = 0, int count = 50) =>
        Run(() => Task.FromResult(sessions.Captures.Page(snapshotRef, offset, count)));
    [McpServerTool(Name = "runtime_ua_capture_export", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Export an immutable verified UA capture snapshotRef as json or csv with companion JSON. Original decoded read payloads, exact UA node/session selections, gaps, terminal state and quality/timing limits remain retained. Download hashed artifacts through artifact_read. No subscription data, synchronized samples, behavioral verdicts or native qualification. Separate 512 MiB retained capture-store limit; no eviction.")]
    public Task<CallToolResult> CaptureExport(string snapshotRef, CancellationToken cancellationToken, string format = "json") =>
        Run(() => sessions.Captures.Export(snapshotRef, format, cancellationToken));
    private static async Task<CallToolResult> Run(Func<Task<object>> action)
    {
        try { return Format(await action()); }
        catch (Exception e) { return Format(new { error = new { code = e is BridgeException b ? b.Code : "UaOperationFailed",
            message = "OPC UA operation refused or incomplete; no remote error text or secret returned." } }, true); }
    }
    private static CallToolResult Format(object value, bool error = false)
    {
        var json = JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
