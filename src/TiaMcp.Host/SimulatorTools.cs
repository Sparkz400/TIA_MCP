using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed class SimulatorClient(HostOptions options) : IAsyncDisposable
{
    public BridgeClient Bridge { get; } = new(new BridgeOptions(options.SimulatorWorkerPath, options.SimulatorApiDirectory, options.TimeoutSeconds, Disabled: options.NoNativeWorkers));
    public ValueTask DisposeAsync() => Bridge.DisposeAsync();
}

[McpServerToolType]
public sealed class SimulatorTools(SimulatorClient simulator, HostOptions options)
{
    [McpServerTool(Name = "sim_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Initialize the installed PLCSIM Advanced V8 API and report local simulated instances/runtime status. Initialization may start Siemens Runtime Manager. Does not activate a trial or connect to physical PLCs.")]
    public Task<CallToolResult> Status(CancellationToken cancellationToken) => Call("sim_status", new { }, cancellationToken);

    [McpServerTool(Name = "sim_create", Destructive = false, OpenWorld = false)]
    [Description("Create a uniquely named local PLCSIM Advanced instance owned by this session. Supply a fresh creationId (32 hex GUID characters) to inspect its durable receipt after a lost response; an existing ID is never replayed. Returns exact identity and a short-lived one-use downloadTicket for simulation_download. Does not activate a license trial. communicationInterface is Softbus (default) or TCPIP; TCPIP is refused with TcpIpModeRequired unless PLCSIM is already in TCP/IP single-adapter mode, and every later power-on, RUN, write and binding rechecks it. Network mode, adapters and IP addresses are operator settings and are never changed.")]
    public Task<CallToolResult> Create(CancellationToken cancellationToken, string cpuType = "CPU1511", string? creationId = null, string communicationInterface = "Softbus")
        => Call("sim_create", new { cpuType, creationId, communicationInterface, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "simulation") }, cancellationToken);

    [McpServerTool(Name = "sim_creation_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect a durable user-protected simulator creation receipt by the creationId supplied to sim_create. Reports the last recorded stage, exact registered identity and original worker exit. Does not adopt, power off or replay an instance creation; registration intent alone is not ownership proof.")]
    public Task<CallToolResult> CreationStatus(string creationId, CancellationToken cancellationToken)
        => Call("sim_creation_status", new { creationId, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "simulation") }, cancellationToken);

    [McpServerTool(Name = "sim_select", Destructive = false, OpenWorld = false)]
    [Description("Select an existing local simulated instance by its ID AND expected name. Optional creationId explicitly requests ownership adoption after the original worker exits; requires a signed ready receipt and matching native ID, name, CPU, storage and marker. No CPU state change or new download ticket. Without creationId the selection stays unowned.")]
    public Task<CallToolResult> Select(int instanceId, string expectedName, CancellationToken cancellationToken, string? creationId = null)
        => Call("sim_select", new { instanceId, expectedName, creationId, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "simulation") }, cancellationToken);

    [McpServerTool(Name = "sim_state", Destructive = true, OpenWorld = false)]
    [Description("Change only a selected PLCSIM instance: powerOn, powerOff, run or stop. Exact local simulator identity is rechecked. Never targets a physical PLC. SimulationConfigurationPasswordRequired requests the separate confidential configuration-protection password; user login requires username and password only if separately requested. This tool accepts no raw credentials; follow the returned guidance and server authentication instructions.")]
    public Task<CallToolResult> State(int instanceId, string action, CancellationToken cancellationToken, int timeoutMs = 30000)
        => Call("sim_state", new { instanceId, action, timeoutMs }, cancellationToken);

    [McpServerTool(Name = "sim_tags", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect exposed simulation symbols after a program is loaded. Returns bounded native tag metadata; this is separate from offline tag tables.")]
    public Task<CallToolResult> Tags(int instanceId, CancellationToken cancellationToken, string? filter = null, int maxItems = 200, bool refresh = true)
        => Call("sim_tags", new { instanceId, filter, maxItems, refresh }, cancellationToken);

    [McpServerTool(Name = "sim_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read typed values from an exact selected simulated instance. signals is an array of {symbol,type}; supported types include bool, int8..uint64, float, double, string, wstring. Returns identity, timestamps and quality.")]
    public Task<CallToolResult> Read(int instanceId, JsonElement signals, CancellationToken cancellationToken)
        => Call("sim_read", new { instanceId, signals = Array(signals) }, cancellationToken);

    [McpServerTool(Name = "sim_write", Destructive = true, OpenWorld = false)]
    [Description("Write typed simulation stimuli to a selected local simulated instance. writes is an array of {symbol,type,value}. Writes affect the simulated program and may be overwritten by its scan.")]
    public Task<CallToolResult> Write(int instanceId, JsonElement writes, CancellationToken cancellationToken)
        => Call("sim_write", new { instanceId, writes = Array(writes) }, cancellationToken);

    [McpServerTool(Name = "sim_test", Destructive = true, OpenWorld = false)]
    [Description("Execute bounded simulation stimulus/capture/assertion steps and save a report. signals:[{symbol,type}]; steps:[{delayMs,writes:[{symbol,type,value}],assertions:[{symbol,type,operator,expected,tolerance}]}]. Operators eq/ne/gt/gte/lt/lte. Polling is not scan-accurate; maximum duration/sample bounds are enforced by the adapter.")]
    public Task<CallToolResult> Test(int instanceId, JsonElement signals, JsonElement steps, CancellationToken cancellationToken, int intervalMs = 100)
        => Call("sim_test", new { instanceId, signals = Array(signals), steps = Array(steps), intervalMs, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "simulation-tests") }, cancellationToken);

    [McpServerTool(Name = "sim_release", Destructive = true, OpenWorld = false)]
    [Description("Release the adapter's simulation attachment. deleteOwned additionally removes only an instance created by this adapter; existing selected instances cannot be deleted through this tool.")]
    public Task<CallToolResult> Release(int instanceId, CancellationToken cancellationToken, bool deleteOwned = false)
        => Call("sim_release", new { instanceId, deleteOwned }, cancellationToken);

    private static JsonElement Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value : throw new McpException("An array is required.");
    private async Task<CallToolResult> Call(string operation, object args, CancellationToken cancellationToken)
    {
        string json;
        bool error;
        try { json = (await simulator.Bridge.CallAsync(operation, args, cancellationToken)).GetRawText(); error = false; }
        catch (BridgeException ex)
        { json = JsonSerializer.Serialize(new { error = new { code = ex.Code, message = ex.Message, details = (ex as BridgeOperationException)?.Details } }, BridgeProtocol.Json); error = true; }
        return new CallToolResult { IsError = error, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
