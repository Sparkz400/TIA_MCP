using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class TestSuiteScopeProbeTools(IBridgeClient bridge, HostOptions options)
{
    [McpServerTool(Name = "qualification_testsuite_scope_probe", Destructive = true, OpenWorld = false)]
    [Description("Isolated scope investigation only: prepare one packaged style fixture in this worker's newly created scratch and show its scope editor, or read its public prerequisites. Does not run any executor or enable production profiles. Unavailable on normal Hosts.")]
    public Task<JsonElement> Probe(string action, string projectPath, CancellationToken cancellationToken)
    {
        if (!options.TestSuiteScopeProbe || action is not ("prepare" or "read"))
            throw new InvalidOperationException("An isolated scope-probe Host and prepare/read action are required.");
        return bridge.CallAsync("testsuite_scope_probe", new { action, projectPath,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "scope-probe") }, cancellationToken);
    }
}
