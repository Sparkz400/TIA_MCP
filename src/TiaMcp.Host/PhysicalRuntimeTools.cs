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
    [Description("Retain a content-addressed binding of one PLCSIM Advanced instance this Host owns (instanceId, instanceName, creationId): name, storage, ownership-marker hash and controller IPv4 addresses; optional downloadTicket records its program fingerprint when Siemens exposes one. Returns binding {path,sha256}, which runtime_web_write, runtime_web_operating_mode and runtime_ua_write require to reach only those addresses through the PLCSIM adapter. Never powers, writes or binds a physical PLC.")]
    public Task<CallToolResult> PlcsimBind(int instanceId,string instanceName,string creationId,CancellationToken cancellationToken,string? downloadTicket=null,string? projectName=null,string? plcPath=null) =>
        Run(() => targets.Bind(instanceId,instanceName,creationId,downloadTicket,projectName,plcPath,cancellationToken));

    [McpServerTool(Name="runtime_web_write",ReadOnly=false,Destructive=true,OpenWorld=true)]
    [Description("Write 1..16 values through an owned Web API session bound to an owned PLCSIM instance (bindingSha256) and routed through the PLCSIM adapter; physical PLCs and other routes refuse before sending. Requires acknowledgeWrite=true and write_value. writes:[{selectionId,expected,value}]: nothing is written unless every expected equals the current value. Each write is sent once and read back, stopping at the first failure; never retried. No forcing or batch atomicity.")]
    public Task<CallToolResult> Write(string attemptId,string bindingSha256,WebRuntimeWriteItem[] writes,bool acknowledgeWrite,CancellationToken cancellationToken) =>
        Run(async () => { await Gate(attemptId,bindingSha256,acknowledgeWrite,cancellationToken); return await sessions.Write(attemptId,writes,cancellationToken,
            RuntimeQualification.AfterSend(options,"web.write.afterSend",new { attemptId, bindingSha256, writes })); });

    [McpServerTool(Name="runtime_web_operating_mode",ReadOnly=false,Destructive=true,OpenWorld=true)]
    [Description("Request RUN or STOP (mode) through an owned Web API session bound to an owned PLCSIM instance (bindingSha256) and routed through the PLCSIM adapter; physical PLCs and other routes refuse before sending. Requires acknowledgeModeChange=true, the change_operating_mode permission and expectedMode equal to the current mode; otherwise nothing is sent. Observes the mode for about 5 s and reports readbackMatches. Never retried.")]
    public Task<CallToolResult> OperatingMode(string attemptId,string bindingSha256,string expectedMode,string mode,bool acknowledgeModeChange,CancellationToken cancellationToken) =>
        Run(async () => { await Gate(attemptId,bindingSha256,acknowledgeModeChange,cancellationToken); return await sessions.ChangeOperatingMode(attemptId,expectedMode,mode,cancellationToken); });

    [McpServerTool(Name="runtime_web_alarm_acknowledge",ReadOnly=false,Destructive=true,OpenWorld=true)]
    [Description("Acknowledge one alarm (Alarms.Acknowledge) through an owned Web API session bound to an owned PLCSIM instance (bindingSha256); physical PLCs, unbound endpoints and other routes refuse before sending. Requires acknowledgeAlarm=true, the acknowledge_alarms permission, language and an alarmId currently listed as not_acknowledged; otherwise nothing is sent. Sent once, never retried, then read back. Not verified against a real PLC or PLCSIM.")]
    public Task<CallToolResult> AlarmAcknowledge(string attemptId,string bindingSha256,string language,string alarmId,bool acknowledgeAlarm,CancellationToken cancellationToken) =>
        Run(async () => { await Gate(attemptId,bindingSha256,acknowledgeAlarm,cancellationToken); return await sessions.AcknowledgeAlarm(attemptId,language,alarmId,cancellationToken); });

    [McpServerTool(Name="runtime_web_backup",ReadOnly=false,Destructive=true,OpenWorld=true)]
    [Description("Create a CPU configuration backup (Plc.CreateBackup) through an owned Web API session bound to an owned PLCSIM instance (bindingSha256); physical PLCs and other routes refuse before sending. Requires acknowledgeBackup=true, the backup_plc permission and an observed STOP mode (never changed). Stores the file (at most 256 MiB) as a content-addressed artifact for artifact_read; treat it as confidential. No restore. Not verified against a real PLC or PLCSIM.")]
    public Task<CallToolResult> Backup(string attemptId,string bindingSha256,bool acknowledgeBackup,CancellationToken cancellationToken) =>
        Run(async () => { await Gate(attemptId,bindingSha256,acknowledgeBackup,cancellationToken); return await sessions.Backup(attemptId,cancellationToken); });

    [McpServerTool(Name="runtime_web_download",ReadOnly=false,Destructive=false,OpenWorld=true)]
    [Description("Download service data (kind serviceData, needs hwid) or profiling data (kind profiling) through an owned Web API session that advertised the method and holds download_service_data or read_value. Stores the file (at most 64 MiB) as a content-addressed artifact for artifact_read; both exports load the CPU and may contain project information, so schedule them deliberately and treat them as confidential. Not verified against a real PLC or PLCSIM.")]
    public Task<CallToolResult> Download(string attemptId,string kind,CancellationToken cancellationToken,long? hwid=null) =>
        Run(async () => await sessions.Download(attemptId,kind,hwid,cancellationToken));

    private async Task Gate(string attemptId,string bindingSha256,bool acknowledged,CancellationToken cancellationToken)
    {
        if (!acknowledged) throw new BridgeException("RuntimeAcknowledgementRequired","Set the explicit acknowledgement flag; nothing was sent.");
        await targets.Require(bindingSha256,"web:"+attemptId,(await sessions.Status(attemptId,cancellationToken)).Context.Endpoint);
    }

    [McpServerTool(Name="runtime_web_credential_binding",ReadOnly=true,Destructive=false,OpenWorld=false, UseStructuredContent = true, OutputSchemaType = typeof(TiaMcp.Core.Security.CredentialBinding))]
    [Description("Build public credential binding for one canonical HTTPS origin, explicit leaf-certificate SHA256, username and authentication mode. Store a JSON string password through the local credential CLI; never pass passwords or tokens to tools. The pin is supplied trust, not a discovered CPU identity.")]
    public Task<CallToolResult> Binding(string endpoint,string peerCertificateSha256,string user,string mode="local") =>
        Run(() => Task.FromResult<object>(PhysicalRuntimeSessions.Binding(endpoint,peerCertificateSha256,user,mode)));

    [McpServerTool(Name="runtime_web_connect",ReadOnly=false,Destructive=false,OpenWorld=true, UseStructuredContent = true, OutputSchemaType = typeof(RuntimeSessionStatus))]
    [Description("Open one owned Siemens Web API session from a protected credential reference. attemptId is a new 32-lowercase-hex ID; reuse it to observe a lost reply without another login. Supply canonical endpoint, certificate pin, user, mode, vaultName and credential reference; leaf pin, hostname, validity, API version and session identity are checked. No redirects, proxy or automatic login retry. Physical qualification remains unverified.")]
    public Task<CallToolResult> Connect(string attemptId,RuntimeLoginSelection login,CancellationToken cancellationToken) =>
        Run(async () => await targets.Connect("web:"+attemptId,login.Endpoint,() => sessions.Connect(attemptId,login,cancellationToken)));

    [McpServerTool(Name="runtime_web_browse",ReadOnly=true,Destructive=false,OpenWorld=true)]
    [Description("Browse immediate children or one exact symbol in an owned session. Set children=false and provide exact symbol to obtain a host-owned selectionId. Metadata and scope remain explicit; no recursive completeness or live program identity is inferred. At most 256 selected symbols per session.")]
    public Task<CallToolResult> Browse(string attemptId,CancellationToken cancellationToken,string? symbol=null,bool children=true) =>
        Run(() => sessions.Browse(attemptId,symbol,children,cancellationToken));

    [McpServerTool(Name="runtime_web_read",ReadOnly=true,Destructive=false,OpenWorld=true, UseStructuredContent = true, OutputSchemaType = typeof(WebRuntimeReadResult))]
    [Description("Read one host-owned selectionId after rechecking protected credential, advertised session identity, current permissions and symbol metadata. Representation is simple JSON or raw bytes; exact numeric JSON is retained. Returns protocol errors and host times, never source timestamps, sensor-quality guarantees or program-match proof. No writes or forcing.")]
    public Task<CallToolResult> Read(string attemptId,string selectionId,CancellationToken cancellationToken,string representation="simple") =>
        Run(async () => await sessions.ReadValue(attemptId,selectionId,representation,cancellationToken));

    [McpServerTool(Name="runtime_web_diagnostics",ReadOnly=true,Destructive=false,OpenWorld=true, UseStructuredContent = true, OutputSchemaType = typeof(WebRuntimeDiagnosticResult))]
    [Description("Read one CPU diagnostic kind in an owned Web API session after rechecking credential and session identity: languages, alarms, diagnosticBuffer, syslog, operatingMode, modeSelector, systemTime, timeSettings or projectInformation. alarms and diagnosticBuffer need language and count 1..500; syslog count 1..20. Unadvertised methods return unsupported without a request. Returns bounded raw JSON with truncation state. No acknowledgment, clock setting or mode change. Details: docs/reference/tool-notes.md#runtime_web_diagnostics.")]
    public Task<CallToolResult> Diagnostics(string attemptId,string kind,CancellationToken cancellationToken,string? language=null,int? count=null) =>
        Run(async () => await sessions.Diagnostics(attemptId,kind,language,count,cancellationToken));

    [McpServerTool(Name="runtime_web_status",ReadOnly=true,Destructive=false,OpenWorld=false, UseStructuredContent = true, OutputSchemaType = typeof(RuntimeSessionStatus))]
    [Description("Read retained local session state and observed profile without contacting the PLC. V1 records remain readable without inventing a profile. After Host restart the remote session outcome remains unknown; tokens are not persisted and no login is replayed. Unknown outcomes block another login to the same endpoint in this artifact root.")]
    public Task<CallToolResult> Status(string attemptId,CancellationToken cancellationToken) =>
        Run(async () => await sessions.Status(attemptId,cancellationToken));

    [McpServerTool(Name="runtime_web_disconnect",ReadOnly=false,Destructive=false,OpenWorld=true, UseStructuredContent = true, OutputSchemaType = typeof(RuntimeSessionStatus))]
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
