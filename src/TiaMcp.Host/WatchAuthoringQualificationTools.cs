using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class WatchAuthoringQualificationTools(IBridgeClient bridge, HostOptions options)
{
    private void RequireQualification()
    {
        if (!options.ComponentQualification || options.WatchQualificationPoint is null)
            throw new InvalidOperationException("An explicit isolated watch qualification Host is required.");
    }
    [McpServerTool(Name = "qualification_watch_authoring_source_negative", Destructive = true, OpenWorld = false)]
    [Description("Qualification only: checkpoint one exact owned scratch, exercise the native watch stale-source guard against a saved temporary FB HeaderAuthor change, and restore the original HeaderAuthor in finally. Requires the complete reviewed source binding. Retains original/changed/restored exports and checkpoint; never imports a watch table or retries.")]
    public Task<CallToolResult> SourceNegative(JsonElement sourceBinding, CancellationToken cancellationToken)
    {
        RequireQualification();
        return Call("qualification_watch_authoring_source_negative", new { sourceBinding,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "watch-source-negative") }, cancellationToken);
    }

    [McpServerTool(Name = "qualification_watch_authoring_source_reconcile", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Qualification only: reconcile a lost/failed watch source-negative response solely from a fresh native observation matching the retained complete original baseline. Never retries the source edit or restores an archive; a still-changed project remains owned.")]
    public Task<CallToolResult> Reconcile(string admissionId, CancellationToken cancellationToken)
    {
        RequireQualification();
        return Call("qualification_watch_authoring_source_reconcile", new { admissionId }, cancellationToken);
    }

    // Refusals such as ResourceBusy or InvalidOperationState reach the client typed, as from every engineering tool.
    private async Task<CallToolResult> Call(string operation, object arguments, CancellationToken cancellationToken)
    {
        string json; bool failed = false;
        try { json = (await bridge.CallAsync(operation, arguments, cancellationToken)).GetRawText(); }
        catch (BridgeException error)
        {
            failed = true;
            json = JsonSerializer.Serialize(new { error = new { code = error.Code, message = error.Message,
                details = (error as BridgeOperationException)?.Details } }, BridgeProtocol.Json);
        }
        return new CallToolResult { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
