using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class TestSuiteScopeProbeTools(IBridgeClient bridge, HostOptions options)
{
    [McpServerTool(Name = "qualification_testsuite_scope_probe", Destructive = true, OpenWorld = false)]
    [Description("Isolated scope investigation only: prepare one packaged style fixture in this worker's newly created scratch and show its scope editor, or read its public prerequisites. Does not run any executor or enable production profiles. Unavailable on normal Hosts.")]
    public async Task<CallToolResult> Probe(string action, string projectPath, CancellationToken cancellationToken)
    {
        // Typed result like TiaTools.Call: a thrown BridgeException would reach the client as the SDK's generic message without its code.
        try
        {
            if (!options.TestSuiteScopeProbe || action is not ("prepare" or "read"))
                throw new BridgeException("InvalidRequest", "An isolated scope-probe Host and prepare/read action are required.");
            var result = await bridge.CallAsync("testsuite_scope_probe", new { action, projectPath,
                artifactDirectory = Path.Combine(options.ArtifactsDirectory, "scope-probe") }, cancellationToken);
            return Result(result.GetRawText(), false);
        }
        catch (BridgeException error)
        {
            return Result(JsonSerializer.Serialize(new { error = new { code = error.Code, message = error.Message } }, BridgeProtocol.Json), true);
        }
    }

    private static CallToolResult Result(string json, bool isError) =>
        new() { IsError = isError, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
}
