using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.UnifiedRuntime;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class UnifiedRuntimeTools(UnifiedRuntimeSessions sessions)
{
    [McpServerTool(Name = "runtime_unified_credential_binding", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Build the public credential binding for a WinCC Unified PC Runtime GraphQL login: endpoint https://<host name>/graphql/ (not localhost or 127.0.0.1, which the server refuses), the lowercase SHA-256 of the Runtime web server's leaf certificate, and the UMC user. Store the password as a JSON string through the local credential CLI (--credential-store create --binding <file>); never pass passwords or tokens to tools. The pin is supplied trust.")]
    public CallToolResult Binding(string endpoint, string peerCertificateSha256, string user) =>
        Run(() => UnifiedRuntimeSessions.Binding(endpoint, peerCertificateSha256, user));

    [McpServerTool(Name = "runtime_unified_connect", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Log in to a WinCC Unified PC Runtime GraphQL server with a protected credential reference (login: endpoint, peerCertificateSha256, user, vaultName, credential). attemptId is a new 32-lowercase-hex ID; repeating it with the same login returns the open session. Checks the certificate pin, host name and validity, reads the user's UMC language and the server's alarm fields (schema introspection). Needs a running runtime project and a user with GraphQL read access (read/write access to acknowledge). Sessions live in this Host's memory only.")]
    public Task<CallToolResult> Connect(string attemptId, UnifiedLoginSelection login, CancellationToken cancellationToken) =>
        RunAsync(async () => await sessions.Connect(attemptId, login, cancellationToken));

    [McpServerTool(Name = "runtime_unified_alarms", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read active (kind=active) or logged (kind=logged) alarms of the connected Unified runtime. languages defaults to the UMC user's language and is never left to the server's en-US default (which fails on a runtime without English); pass e.g. [\"de-DE\"] when the user has none. filterString is the server's alarm filter (QCL WHERE part, e.g. \"path LIKE '*::Tag01:*'\"; state, stateMachine and a few other properties cannot be filtered); filterLanguage defaults to the first language. startTime/endTime (ISO 8601) apply to logged alarms. Each active row carries acknowledge {path, instanceID}: path is the fully qualified System::Tag:Alarm name acknowledgement needs; name is only the configured alarm name. Translatable texts are keyed by language.")]
    public Task<CallToolResult> Alarms(string attemptId, CancellationToken cancellationToken, string kind = "active", string[]? languages = null,
        string? filterString = null, string? filterLanguage = null, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null, int maxItems = 200) =>
        RunAsync(async () => await sessions.Alarms(attemptId, kind, languages, filterString, filterLanguage, startTime, endTime, maxItems, cancellationToken));

    [McpServerTool(Name = "runtime_unified_alarm_acknowledge", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Acknowledge 1..64 active alarms of a WinCC Unified PC Runtime on this PC: alarms [{path, instanceId}] from runtime_unified_alarms rows' acknowledge objects (instanceId 0 or omitted acknowledges every active instance of that alarm). A plain alarm name is refused (UnifiedAlarmPathRequired): the server needs System::Tag:Alarm. Requires acknowledgeWrite:true after the user asked for it, and an endpoint whose host name resolves only to this PC (UnifiedWriteRouteRefused otherwise). Returns the server's per-alarm results and item error codes; never resets, shelves or disables alarms.")]
    public Task<CallToolResult> Acknowledge(string attemptId, UnifiedAlarmIdentifier[] alarms, bool acknowledgeWrite, CancellationToken cancellationToken) =>
        RunAsync(async () => await sessions.Acknowledge(attemptId, alarms, acknowledgeWrite, cancellationToken));

    [McpServerTool(Name = "runtime_unified_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read this Host's state of one Unified GraphQL session (endpoint, user, UMC language, idle logoff, alarm fields seen) without contacting the server.")]
    public CallToolResult Status(string attemptId) => Run(() => sessions.Status(attemptId));

    [McpServerTool(Name = "runtime_unified_disconnect", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Drop this Host's Unified GraphQL session and its token. The server ends the session after its idle time (autoLoggOffSec).")]
    public Task<CallToolResult> Disconnect(string attemptId, CancellationToken cancellationToken) =>
        RunAsync(async () => await sessions.Disconnect(attemptId, cancellationToken));

    private static CallToolResult Run(Func<object> action)
    {
        try { return Format(action()); }
        catch (BridgeException error) { return Failure(error); }
    }

    private static async Task<CallToolResult> RunAsync(Func<Task<object>> action)
    {
        try { return Format(await action()); }
        catch (BridgeException error) { return Failure(error); }
    }

    private static CallToolResult Failure(BridgeException error) =>
        Format(new { error = new { code = error.Code, message = error.Message, details = (error as BridgeOperationException)?.Details } }, true);

    private static CallToolResult Format(object value, bool error = false)
    {
        var json = JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
