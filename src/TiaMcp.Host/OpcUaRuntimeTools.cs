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
public sealed class OpcUaRuntimeTools(OpcUaRuntimeSessions sessions, HostOptions options, PlcsimTargets targets)
{
    [McpServerTool(Name = "runtime_ua_write", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Write 1..16 scalar Value attributes via this Host's owned UA session whose endpoint is a controller address of a runtime_plcsim_bind binding (bindingSha256), routed through the PLCSIM Virtual Ethernet Adapter; physical PLCs and other endpoints or routes refused before sending. Requires acknowledgeWrite=true, no running acquisition. writes:[{node,encoding,expected,value}]; any expected mismatch writes nothing. Read back after; never retried. No arrays, structures or forcing.")]
    public Task<CallToolResult> Write(string attemptId, string bindingSha256, OpcUaRuntimeWriteItem[] writes, bool acknowledgeWrite, CancellationToken cancellationToken) =>
        Run(async () =>
        {
            if (!acknowledgeWrite) throw new BridgeException("RuntimeAcknowledgementRequired", "Set the explicit acknowledgement flag; nothing was sent.");
            await targets.Require(bindingSha256, "ua:" + attemptId, (await sessions.Store.Status(attemptId, cancellationToken)).Selection.Endpoint);
            return await sessions.Store.Write(attemptId, writes, cancellationToken,
                RuntimeQualification.AfterSend(options, "ua.write.afterSend", new { attemptId, bindingSha256, writes }));
        });

    [McpServerTool(Name = "runtime_ua_call", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Call one PLC-namespace server method (S7-1500: OPC_UA_ServerMethodPre/Post instance) via this Host's owned UA session under runtime_ua_write's owned-PLCSIM gate (bindingSha256, PLCSIM adapter route), else refused before sending. Requires acknowledgeCall=true and no running acquisition. objectNode/methodNode must be in the PLC namespace; standard server methods refused. inputs (0..16) must match InputArguments. A lost response is never retried; effect not read back.")]
    public Task<CallToolResult> Call(string attemptId, string bindingSha256, OpcUaRuntimeNode objectNode, OpcUaRuntimeNode methodNode, OpcUaRuntimeCallArgument[] inputs,
        bool acknowledgeCall, CancellationToken cancellationToken) =>
        Run(async () =>
        {
            if (!acknowledgeCall) throw new BridgeException("RuntimeAcknowledgementRequired", "Set the explicit acknowledgement flag; nothing was sent.");
            await targets.Require(bindingSha256, "ua:" + attemptId, (await sessions.Store.Status(attemptId, cancellationToken)).Selection.Endpoint);
            return await sessions.Store.Call(attemptId, objectNode, methodNode, inputs, cancellationToken);
        });

    [McpServerTool(Name = "runtime_ua_conditions", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read the server's current Alarms and Conditions once via this Host's owned UA session: temporary event subscription, one ConditionRefresh call (writes no process data), bounded Publish for windowMilliseconds 500..5000 and maximumEvents 1..256. No Acknowledge, Confirm, AddComment, enable/disable or shelving. Refuses during a running acquisition. S7-1500 needs alarmsAndConditions (opcua_server_settings_set). Nothing retained; for a timeline use runtime_ua_subscription_start.")]
    public Task<CallToolResult> Conditions(string attemptId, CancellationToken cancellationToken, int windowMilliseconds = 3000, int maximumEvents = 64) =>
        Run(async () => await sessions.Store.Conditions(attemptId, windowMilliseconds, maximumEvents, cancellationToken));

    [McpServerTool(Name = "runtime_ua_history_probe", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("HistoryRead boundary probe, not a history service. Reads Server/ServerCapabilities/HistoryServerCapabilities (AccessHistoryDataCapability, AccessHistoryEventsCapability), the node's AccessLevel/UserAccessLevel/Historizing, and sends one bounded HistoryReadRaw (last hour, one value, continuation released) whose status is returned and whose values never are. The product refuses HistoryRead: product.code is UaHistoryReadUnsupported, because the S7-1500 server profile has no historical-access facet. Local captures (runtime_ua_capture_*, runtime_ua_subscription_*) are the history this Host keeps.")]
    public Task<CallToolResult> HistoryProbe(string attemptId, OpcUaRuntimeNode node, CancellationToken cancellationToken) =>
        Run(async () => await sessions.Store.HistoryProbe(attemptId, node, cancellationToken));

    [McpServerTool(Name = "diagnostic_ua_compare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare two exact prebound UA polling/subscription prefixes offline. Supply beforeSnapshotRef, afterSnapshotRef and request:{reason,beforeAnchorSeconds,afterAnchorSeconds,windowStartSeconds,windowEndSeconds,signalMap:[{before,after}],allowChangedSource?}. One-to-one mappings with equal datatypes and units. Returns a hashed artifact (max 8 MiB). No services, replay, clock synchronization, causal or fixed-fault verdict. Export with diagnostic_ua_report_export.")]
    public Task<CallToolResult> CompareDiagnostic(string beforeSnapshotRef, string afterSnapshotRef, JsonElement request) => Run(() => Task.FromResult<object>(
        new DiagnosticUaComparisons(options.ArtifactsDirectory, Observations()).Compare(beforeSnapshotRef, afterSnapshotRef,
            System.Text.Json.Nodes.JsonNode.Parse(request.GetRawText())?.AsObject() ?? throw new BridgeException("InvalidRequest", "Expected comparison request object."))));

    [McpServerTool(Name = "diagnostic_ua_observe", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compose an offline diagnostic observation report from an exact immutable polling or subscription snapshotRef with a pre-acquisition diagnosticBindingId. Revalidates chain, binding and experiment; retains raw rows, exact values, gaps and overflow. No interpolation, type conversion, behavioral assertion or program/address proof. Max 10000 rows, 8 MiB report; oversize refuses. Hashed artifact for artifact_read. No session access or replay.")]
    public Task<CallToolResult> ObserveDiagnostic(string snapshotRef) => Run(() => Task.FromResult<object>(Observations().Observe(snapshotRef)));

    [McpServerTool(Name = "diagnostic_ua_report_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export a retained diagnostic_ua_observe or diagnostic_ua_compare reportId as json, markdown or html. Reopens original capture/experiment/binding evidence and recomputes summaries before publishing. Missing or changed originals and honestly rehashed report changes refuse. JSON preserves exact original bytes; escaped self-contained readable exports retain quality/timing/sequence uncertainty, unresolved hypotheses and complete original report JSON. The JSON can be opened in portable Explorer. No target access, replay, behavioral assertion, physical/native qualification or source authentication.")]
    public Task<CallToolResult> ExportDiagnostic(string reportId, string format) => Run(() => Task.FromResult<object>(
        new DiagnosticStore(options.ArtifactsDirectory).Read(reportId)["schemaVersion"]?.GetValue<string>() == "diagnostic-ua-comparison.v1"
            ? new DiagnosticUaComparisons(options.ArtifactsDirectory, Observations()).Export(reportId, format) : Observations().Export(reportId, format)));

    private DiagnosticUaObservations Observations() => new(options.ArtifactsDirectory, reference =>
        reference.StartsWith("capture:", StringComparison.Ordinal) ? sessions.Captures.DiagnosticSource(reference) :
        reference.StartsWith("ua-subscription:", StringComparison.Ordinal) ? sessions.Subscriptions.DiagnosticSource(reference) :
        throw new BridgeException("InvalidRequest", "Expected an immutable polling or subscription snapshot reference."));

    [McpServerTool(Name = "runtime_ua_client_identity", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Return this Host's OPC UA client public certificate, application URI, validity and storage mode for explicit server trust provisioning. With --ua-client-identity NAME, a pre-created Windows current-user DPAPI identity survives Host restarts; its private key is never returned. Without that option identity is ephemeral. No physical connection, automatic replacement or server trust change is performed.")]
    public Task<CallToolResult> ClientIdentity() => Run(() => Task.FromResult(sessions.Store.ClientIdentity()));
    [McpServerTool(Name = "runtime_ua_credential_binding", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(TiaMcp.Core.Security.CredentialBinding))]
    [Description("Build the protected username credential binding for one exact opc.tcp endpoint, server application URI and leaf SHA256. Store a JSON-string password through the local credential CLI, never through MCP arguments. Set authentication=username; omit credential while obtaining this binding.")]
    public Task<CallToolResult> Binding(OpcUaRuntimeSelection selection) => Run(() => Task.FromResult<object>(OpcUaRuntimeSessionStore.Binding(selection)));
    [McpServerTool(Name = "runtime_ua_connect", ReadOnly = false, Destructive = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(OpcUaRuntimeStatus))]
    [Description("Create one owned, read-only OPC UA session at an explicit endpoint. Require exact server application URI and leaf SHA256, valid certificate hostname/time/key usage, Basic256Sha256 SignAndEncrypt, anonymous or protected username authentication. Supply a new 32-lowercase-hex attemptId. Reusing an attempt observes its retained outcome without replay. Unresolved same-endpoint attempts block fresh IDs. Polling or subscriptions require their separate explicit start tools. No scans, downgrade, writes, methods or automatic reconnect. Physical qualification remains unverified.")]
    public Task<CallToolResult> Connect(string attemptId, OpcUaRuntimeSelection selection, CancellationToken cancellationToken) =>
        Run(async () => await targets.Connect("ua:" + attemptId, selection.Endpoint, () => sessions.Store.Connect(attemptId, selection, cancellationToken)));
    [McpServerTool(Name = "runtime_ua_browse", ReadOnly = true, Destructive = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(OpcUaRuntimeBrowseResult))]
    [Description("Browse bounded immediate hierarchical references in this Host's owned UA session, using exact namespace URI and canonical numeric/string/guid/opaque identifier. At most 256 references and 16 pages; truncation and continuation cleanup are explicit. A result over 128 KiB is not returned inline: references is empty and spill names the complete result as a hashed artifact for artifact_read. External server references are unsupported, not followed.")]
    public Task<CallToolResult> Browse(string attemptId, OpcUaRuntimeNode node, CancellationToken cancellationToken, int maximum = 256) =>
        Run(async () => { var r = await sessions.Store.Browse(attemptId, node, maximum, cancellationToken); return await Bounded(r, "browse", r.References.Count, x => r with { References = [], Spill = x }); });
    [McpServerTool(Name = "runtime_ua_read", ReadOnly = true, Destructive = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(OpcUaRuntimeReadResult))]
    [Description("Read 1..64 explicit UA node identities after rechecking namespace/server/start identity in the same request. Returns exact scalar or bounded array/matrix values (up to 4096 elements), raw status, Good/Uncertain/Bad quality, original timestamps and per-item errors; Int64/UInt64 as decimal strings. A result over 128 KiB spills to a hashed artifact for artifact_read. Structures unsupported. No fabricated quality/timestamps, device/program identity guarantee or writes.")]
    public Task<CallToolResult> Read(string attemptId, OpcUaRuntimeNode[] nodes, CancellationToken cancellationToken) =>
        Run(async () => { var r = await sessions.Store.Read(attemptId, nodes, cancellationToken); return await Bounded(r, "read", r.Items.Count, x => r with { Items = [], Spill = x }); });
    [McpServerTool(Name = "runtime_ua_status", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(OpcUaRuntimeStatus))]
    [Description("Read retained UA attempt state without network traffic. Host restart preserves hostEndedRemoteUnknown; session tokens are not persisted or adopted. A separately protected client certificate does not resume sessions or acquisition.")]
    public Task<CallToolResult> Status(string attemptId, CancellationToken cancellationToken) =>
        Run(async () => await sessions.Store.Status(attemptId, cancellationToken));
    [McpServerTool(Name = "runtime_ua_disconnect", ReadOnly = false, Destructive = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(OpcUaRuntimeStatus))]
    [Description("Boundedly close only this Host's owned UA session/channel. Unknown remote cleanup remains explicit. acknowledgeUnknown=true releases an unresolved local reservation as abandonedRemoteUnknown without claiming server logout, allowing a separately requested new attempt. No forced device action.")]
    public Task<CallToolResult> Disconnect(string attemptId, CancellationToken cancellationToken, bool acknowledgeUnknown = false) =>
        Run(async () => await sessions.Store.Disconnect(attemptId, acknowledgeUnknown, cancellationToken));
    [McpServerTool(Name = "runtime_ua_capture_start", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Start bounded polling of 1..64 namespace-URI UA nodes on this Host's existing encrypted owned attemptId. Supply a caller-retained 32-lowercase-hex captureId and request:{attemptId,nodes,intervalMilliseconds:100..60000,durationSeconds:1..3600,maximumSamples:1..100000,maximumBytes:1048576..67108864,maximumReconnectAttempts:0..3}. Same captureId never replays; changed intent refuses. One capture per session. No writes, replacement sessions, interpolation or atomic samples.")]
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
    private async Task<object> Bounded<T>(T result, string kind, int count, Func<OpcUaRuntimeSpill, T> spilled) where T : notnull =>
        UaResultSpill.Bounded(options.ArtifactsDirectory, result, kind, count, spilled, await sessions.Store.OldestLiveSession());
    private static async Task<CallToolResult> Run(Func<Task<object>> action)
    {
        try { return Format(await action()); }
        catch (Exception e)
        {
            // The client sees only the fixed code; the type (never the message, which may carry remote text) goes to stderr, not the MCP stdout.
            if (e is not BridgeException) Console.Error.WriteLine($"runtime_ua tool failed: {e.GetType().FullName}");
            return Format(new
            {
                error = new
                {
                    code = e is BridgeException b ? b.Code : "UaOperationFailed",
                    message = "OPC UA operation refused or incomplete; no remote error text or secret returned."
                }
            }, true);
        }
    }
    private static CallToolResult Format(object value, bool error = false)
    {
        var json = JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
