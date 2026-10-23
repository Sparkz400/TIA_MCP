using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class OpcUaSubscriptionTools(OpcUaRuntimeSessions sessions)
{
    [McpServerTool(Name = "runtime_ua_subscription_start", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Start a bounded read-only subscription on this Host's existing encrypted owned UA attemptId. Supply a new retained lowercase 32-hex captureId and explicit namespace-URI nodes. Requested publishing/sampling intervals and queue settings are separate from server revisions. Retains item failures, original quality/type/timestamps and Publish/Republish sequence evidence. Repeated captureId observes its original intent without replay; changed intent refuses. No automatic reconnect, transfer, writes or independent PLC program/address association. Physical qualification remains unverified.")]
    public Task<CallToolResult> Start(string captureId, OpcUaSubscriptionRequest request, CancellationToken cancellationToken) =>
        Run(() => sessions.Subscriptions.Start(captureId, request, cancellationToken));

    [McpServerTool(Name = "runtime_ua_subscription_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the verified committed subscription prefix, original requested/revised settings, immutable snapshotRef and terminal/interrupted state without target access. Host restart never resumes acquisition. Unknown remote cleanup and unknown loss remain explicit; retained hashes prove byte consistency only.")]
    public Task<CallToolResult> Status(string captureId, CancellationToken cancellationToken) =>
        Run(() => sessions.Subscriptions.Status(captureId, cancellationToken));

    [McpServerTool(Name = "runtime_ua_subscription_stop", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Stop only this Host's owned UA subscription, retain the committed prefix and attempt bounded cleanup on its original session. Remote cleanup uncertainty remains explicit. Does not reconnect or transfer. Repeated stop after completion or restart reads retained state without acquisition replay; use runtime_ua_status/disconnect for the separate session lifecycle.")]
    public Task<CallToolResult> Stop(string captureId, CancellationToken cancellationToken) =>
        Run(() => sessions.Subscriptions.Stop(captureId, cancellationToken));

    [McpServerTool(Name = "runtime_ua_subscription_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page an immutable subscription snapshotRef with offset >=0 and count 1..200. Original notification metadata, revised settings, sequence/gap/overflow/duplicate and retransmission evidence remain distinct. No target access, reconnect or resumed acquisition.")]
    public Task<CallToolResult> Page(string snapshotRef, int offset = 0, int count = 50) =>
        Run(() => Task.FromResult(sessions.Subscriptions.Page(snapshotRef, offset, count)));

    [McpServerTool(Name = "runtime_ua_subscription_export", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Export a verified immutable UA subscription prefix as json or csv with companion JSON. Download returned hashed paths through artifact_read. Retains exact values and original status/timestamps, service/sequence observations and lifecycle uncertainty. No network access, interpolation, synchronized-sample guarantee or behavioral verdict.")]
    public Task<CallToolResult> Export(string snapshotRef, CancellationToken cancellationToken, string format = "json") =>
        Run(() => sessions.Subscriptions.Export(snapshotRef, format, cancellationToken));

    private static async Task<CallToolResult> Run(Func<Task<object>> action)
    {
        try { return Format(await action()); }
        catch (Exception e)
        {
            return Format(new { error = new { code = e is BridgeException b ? b.Code : "UaOperationFailed",
                message = "OPC UA subscription refused or incomplete; no remote error text or secret returned." } }, true);
        }
    }

    private static CallToolResult Format(object value, bool error = false)
    {
        var json = JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
