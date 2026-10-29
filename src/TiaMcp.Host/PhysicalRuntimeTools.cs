using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class PhysicalRuntimeTools(PhysicalRuntimeSessions sessions, PlcsimTargets targets, HostOptions options)
{
    [McpServerTool(Name="runtime_plcsim_bind",ReadOnly=false,Destructive=false,OpenWorld=false)]
    [Description("Retain a content-addressed binding of one PLCSIM Advanced instance this Host owns (instanceId, instanceName and the creationId it was created or adopted with), observed by the simulator worker: instance name, storage path, SHA-256 of its ownership marker and its usable controller IPv4 addresses. Optional downloadTicket (with projectName/plcPath) runs plc_online_compare against that same instance and records its FingerprintDataProvider SHA-256 when Siemens exposes one; otherwise the program fingerprint stays notObserved. Returns binding {path,sha256}: runtime_web_write, runtime_web_operating_mode and runtime_ua_write require it and refuse any endpoint that is not one of these addresses; runtime_handoff_review matches target/program identity against it. Never powers, writes or binds a physical PLC.")]
    public Task<CallToolResult> PlcsimBind(int instanceId,string instanceName,string creationId,CancellationToken cancellationToken,string? downloadTicket=null,string? projectName=null,string? plcPath=null) =>
        Run(() => targets.Bind(instanceId,instanceName,creationId,downloadTicket,projectName,plcPath,cancellationToken));

    [McpServerTool(Name="runtime_web_write",ReadOnly=false,Destructive=true,OpenWorld=true)]
    [Description("Write 1..16 values through an owned Web API session whose endpoint is a controller address of a runtime_plcsim_bind binding (bindingSha256); the simulator worker must still observe that exact owned instance. Physical PLCs and unbound endpoints are refused with RuntimeTargetNotOwnedPlcsim before anything is sent. Requires acknowledgeWrite=true, the session's write_value permission and an advertised PlcProgram.Write. writes:[{selectionId,expected,value}] use host-owned selections and simple JSON values; every expected must equal the current PlcProgram.Read exactly or nothing is written (preconditionMismatch). Each write is sent once, stops the sequence on failure, and is read back; readbackMatches reports a mismatch (the scan may overwrite assignments). Unknown outcomes are reported, never retried. No forcing, credentials or batch atomicity.")]
    public Task<CallToolResult> Write(string attemptId,string bindingSha256,WebRuntimeWriteItem[] writes,bool acknowledgeWrite,CancellationToken cancellationToken) =>
        Run(async () => { await Gate(attemptId,bindingSha256,acknowledgeWrite,cancellationToken); return await sessions.Write(attemptId,writes,cancellationToken,
            RuntimeQualification.AfterSend(options,"web.write.afterSend",new { attemptId, bindingSha256, writes })); });

    [McpServerTool(Name="runtime_web_operating_mode",ReadOnly=false,Destructive=true,OpenWorld=true)]
    [Description("Request RUN or STOP (mode: run/stop) through an owned Web API session whose endpoint is a controller address of a runtime_plcsim_bind binding (bindingSha256), rechecked against the simulator worker. Physical PLCs and unbound endpoints are refused with RuntimeTargetNotOwnedPlcsim before anything is sent. Requires acknowledgeModeChange=true, the change_operating_mode permission, an advertised Plc.RequestChangeOperatingMode and expectedMode (run/stop, different from mode) equal to the current Plc.ReadOperatingMode, otherwise nothing is sent. Boolean success only acknowledges the request; Plc.ReadOperatingMode is observed for up to about 5 s and readbackMatches reports the outcome. Never retried; the mode selector and CPU conditions may still refuse.")]
    public Task<CallToolResult> OperatingMode(string attemptId,string bindingSha256,string expectedMode,string mode,bool acknowledgeModeChange,CancellationToken cancellationToken) =>
        Run(async () => { await Gate(attemptId,bindingSha256,acknowledgeModeChange,cancellationToken); return await sessions.ChangeOperatingMode(attemptId,expectedMode,mode,cancellationToken); });

    private async Task Gate(string attemptId,string bindingSha256,bool acknowledged,CancellationToken cancellationToken)
    {
        if (!acknowledged) throw new BridgeException("RuntimeAcknowledgementRequired","Set the explicit acknowledgement flag; nothing was sent.");
        await targets.Require(bindingSha256,(await sessions.Status(attemptId,cancellationToken)).Context.Endpoint);
    }

    [McpServerTool(Name="runtime_web_credential_binding",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Build public credential binding for one canonical HTTPS origin, explicit leaf-certificate SHA256, username and authentication mode. Store a JSON string password through the local credential CLI; never pass passwords or tokens to tools. The pin is supplied trust, not a discovered CPU identity.")]
    public Task<CallToolResult> Binding(string endpoint,string peerCertificateSha256,string user,string mode="local") =>
        Run(() => Task.FromResult<object>(PhysicalRuntimeSessions.Binding(endpoint,peerCertificateSha256,user,mode)));

    [McpServerTool(Name="runtime_web_connect",ReadOnly=false,Destructive=false,OpenWorld=true)]
    [Description("Open one owned Siemens Web API session using a protected credential reference. Required attemptId is a new 32-lowercase-hex ID; reuse that ID to observe a lost reply without another login. Login specifies canonical endpoint, certificate pin, user, mode, vaultName and credential reference. Explicit leaf pin, hostname and validity are checked. Advertised API version, quantity/authentication profiles and session identity are validated; v2 status retains observations. No redirects, proxy, inventory-token adoption or automatic login retry. No TIA install required; physical qualification remains unverified.")]
    public Task<CallToolResult> Connect(string attemptId,RuntimeLoginSelection login,CancellationToken cancellationToken) =>
        Run(async () => await sessions.Connect(attemptId,login,cancellationToken));

    [McpServerTool(Name="runtime_web_browse",ReadOnly=true,Destructive=false,OpenWorld=true)]
    [Description("Browse immediate children or one exact symbol in an owned session. Set children=false and provide exact symbol to obtain a host-owned selectionId. Metadata and scope remain explicit; no recursive completeness or live program identity is inferred. At most 256 selected symbols per session.")]
    public Task<CallToolResult> Browse(string attemptId,CancellationToken cancellationToken,string? symbol=null,bool children=true) =>
        Run(() => sessions.Browse(attemptId,symbol,children,cancellationToken));

    [McpServerTool(Name="runtime_web_read",ReadOnly=true,Destructive=false,OpenWorld=true)]
    [Description("Read one host-owned selectionId after rechecking protected credential, advertised session identity, current permissions and symbol metadata. Representation is simple JSON or raw bytes; exact numeric JSON is retained. Returns protocol errors and host times, never source timestamps, sensor-quality guarantees or program-match proof. No writes or forcing.")]
    public Task<CallToolResult> Read(string attemptId,string selectionId,CancellationToken cancellationToken,string representation="simple") =>
        Run(async () => await sessions.ReadValue(attemptId,selectionId,representation,cancellationToken));

    [McpServerTool(Name="runtime_web_diagnostics",ReadOnly=true,Destructive=false,OpenWorld=true)]
    [Description("Read one CPU diagnostic kind in an owned session after rechecking protected credential and advertised session identity. kind: languages (Project.ReadLanguages), alarms (Alarms.Browse), diagnosticBuffer (DiagnosticBuffer.Browse), operatingMode (Plc.ReadOperatingMode), modeSelector (Plc.ReadModeSelectorState), systemTime (Plc.ReadSystemTime), timeSettings (Plc.ReadTimeSettings) or projectInformation (Project.ReadInformation). alarms and diagnosticBuffer require language (a tag from languages, e.g. en-US) and count 1..500; other kinds take neither. A method the session did not advertise returns unsupported without a request; provider denial stays explicit. Returns bounded raw JSON, entry count, truncation against count_current (null when unknown) and host request/receive times. No acknowledgment, filters, clock setting, mode change, lossless-history or program-identity claim.")]
    public Task<CallToolResult> Diagnostics(string attemptId,string kind,CancellationToken cancellationToken,string? language=null,int? count=null) =>
        Run(async () => await sessions.Diagnostics(attemptId,kind,language,count,cancellationToken));

    [McpServerTool(Name="runtime_web_status",ReadOnly=true,Destructive=false,OpenWorld=false)]
    [Description("Read retained local session state and observed profile without contacting the PLC. V1 records remain readable without inventing a profile. After Host restart the remote session outcome remains unknown; tokens are not persisted and no login is replayed. Unknown outcomes block another login to the same endpoint in this artifact root.")]
    public Task<CallToolResult> Status(string attemptId,CancellationToken cancellationToken) =>
        Run(async () => await sessions.Status(attemptId,cancellationToken));

    [McpServerTool(Name="runtime_web_disconnect",ReadOnly=false,Destructive=false,OpenWorld=true)]
    [Description("Close only this Host's owned Web API session with one bounded Api.Logout request. Invalidates local selections. Lost response stays unknown and is never automatically retried. Explicit acknowledgeUnknown=true releases only an unresolved local reservation, preserving abandonedRemoteUnknown without claiming remote logout; it permits a separately requested new attempt. Does not log out borrowed inventory sessions or modify the PLC program.")]
    public Task<CallToolResult> Disconnect(string attemptId,CancellationToken cancellationToken,bool acknowledgeUnknown=false) =>
        Run(async () => await sessions.Disconnect(attemptId,cancellationToken,acknowledgeUnknown));

    private static async Task<CallToolResult> Run(Func<Task<object>> action)
    {
        try { return Format(await action()); }
        catch (Exception error) { return Format(new { error = new { code = error is BridgeException e ? e.Code : "RuntimeOperationFailed",
            message = "Runtime operation refused or incomplete; no credentials or remote error text returned." } },true); }
    }
    private static CallToolResult Format(object value,bool error=false)
    {
        var json=JsonSerializer.SerializeToElement(value,BridgeProtocol.Json);
        return new() { IsError=error, StructuredContent=json, Content=[new TextContentBlock { Text=json.GetRawText() }] };
    }
}
