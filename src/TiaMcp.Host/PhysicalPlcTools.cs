using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

// Registered only when the Host is started with --physical-plc-online (and then in the "physical" tool group). Every tool
// works in a worker-owned instance only, names its route explicitly and never answers a login or password challenge.
[McpServerToolType]
public sealed class PhysicalPlcTools(IBridgeClient bridge, HostOptions options)
{
    private const string Unqualified = " Not qualified on a physical PLC yet; check every result in the TIA Portal.";

    [McpServerTool(Name = "plc_physical_targets", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the routes TIA offers to the selected PLC: connection modes, PG/PC interfaces (with isPlcsim) and target interfaces, plus the IPv4 addresses the project configures for the CPU. Goes online to nothing. Worker-owned instance only." + Unqualified)]
    public Task<CallToolResult> Targets(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null)
        => Call("plc_physical_targets", new { projectName, plcPath }, cancellationToken);

    [McpServerTool(Name = "plc_physical_online_compare", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Go online to a real PLC, run PlcSoftware.CompareToOnline and go offline again; writes nothing to the PLC. Explicit route: pcInterface (pcInterfaceNumber, default 1) and targetInterface from plc_physical_targets, mode (default PN/IE) and expectedAddress, an address the project configures for this CPU; PLCSIM interfaces are refused. Login challenges are never answered; a TLS certificate is trusted only when its SHA-256 equals trustedCertificateSha256." + Unqualified)]
    public Task<CallToolResult> OnlineCompare(string pcInterface, string expectedAddress, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null,
        int pcInterfaceNumber = 1, string? targetInterface = null, string mode = "PN/IE", string? trustedCertificateSha256 = null, int maxItems = 500)
        => Call("plc_physical_online_compare", new { projectName, plcPath, pcInterface, pcInterfaceNumber, targetInterface, mode, expectedAddress, trustedCertificateSha256, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "plc_physical_station_upload", Destructive = false, OpenWorld = true)]
    [Description("Upload the station of a real PLC at expectedAddress into a new project under the host's uploads artifacts, in an empty worker-owned instance (project_close or tia_start first). Never uploads into an open project; writes nothing to the PLC. Route, TLS and login rules as plc_physical_online_compare; missing products are reported, not uploaded." + Unqualified)]
    public Task<CallToolResult> StationUpload(string pcInterface, string expectedAddress, CancellationToken cancellationToken, int pcInterfaceNumber = 1, string mode = "PN/IE",
        string? trustedCertificateSha256 = null)
        => Call("plc_physical_station_upload", new
        {
            pcInterface,
            pcInterfaceNumber,
            mode,
            expectedAddress,
            trustedCertificateSha256,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "uploads")
        }, cancellationToken);

    [McpServerTool(Name = "plc_physical_download", Destructive = true, OpenWorld = true)]
    [Description("Download the selected PLC's software (softwareScope changes or all; hardware:true adds the hardware configuration) to a REAL PLC. This can stop the PLC and move the machine: confirm a safe state first. Requires acknowledgeLiveTarget:true, expectedPlcName, the route as in plc_physical_online_compare and a saved project. A stop needs acknowledgeStop, a DB reinitialization also acknowledgeReinitialize; any other question, password or login stops it. Notes: docs/reference/tool-notes.md." + Unqualified)]
    public Task<CallToolResult> Download(string pcInterface, string expectedAddress, string expectedPlcName, bool acknowledgeLiveTarget, CancellationToken cancellationToken,
        string? projectName = null, string? plcPath = null, int pcInterfaceNumber = 1, string? targetInterface = null, string mode = "PN/IE",
        string softwareScope = "changes", bool hardware = false, bool acknowledgeStop = false, bool acknowledgeReinitialize = false, bool startAfterDownload = false,
        string? trustedCertificateSha256 = null)
    {
        if (softwareScope is not ("changes" or "all")) throw new McpException("softwareScope must be changes or all.");
        return Call("plc_physical_download", new
        {
            projectName,
            plcPath,
            pcInterface,
            pcInterfaceNumber,
            targetInterface,
            mode,
            expectedAddress,
            expectedPlcName,
            acknowledgeLiveTarget,
            softwareScope,
            hardware,
            acknowledgeStop,
            acknowledgeReinitialize,
            startAfterDownload,
            trustedCertificateSha256
        }, cancellationToken);
    }

    private async Task<CallToolResult> Call(string operation, object? arguments, CancellationToken cancellationToken)
    {
        try { return ToolResult.Of(await bridge.CallAsync(operation, arguments, cancellationToken)); }
        catch (BridgeException error) { return ToolResult.Failure(error, "BridgeError"); }
    }

    private static int Limit(int value) => value is >= 1 and <= 2000 ? value : throw new McpException("maxItems must be between 1 and 2000.");
}
