using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class ComponentQualificationTools(IBridgeClient bridge, HostOptions options)
{
    [McpServerTool(Name = "qualification_component_window", Destructive = true, OpenWorld = false)]
    [Description("Explicit isolated component qualification lifetime: begin returns the retained capability before start; status/owner-resume/recover/complete preserve durable ownership. Installation never resumes after Host restart. Complete requires exact peers and fresh original/restored observations; reconcile-recovery is separate baseline-preserved recovery evidence and never installation acceptance. Unavailable on normal Hosts.")]
    public Task<JsonElement> Window(JsonElement request, CancellationToken cancellationToken)
    {
        if (!options.ComponentQualification || request.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("An explicitly launched component qualification Host and object request are required.");
        return bridge.CallAsync("qualification_component_window", request, cancellationToken);
    }
}
