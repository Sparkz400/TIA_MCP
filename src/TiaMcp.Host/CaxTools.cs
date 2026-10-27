using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "cax_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export the open project, or one exact device by hardwarePath (a device path from device_inventory), through Siemens CaxProvider into a new AML file. Returns AML and transfer-result log hashes and the native transfer messages (maxItems here, up to 50000 in the retained log), then publishes the AML as an AutomationML document record (as automationml_inspect would) for automationml_report_page/compare and the cax_import amlRef. Does not save or change the project; unsaved in-memory edits are exported.")]
    public async Task<CallToolResult> CaxExport(CancellationToken cancellationToken, string? hardwarePath = null, string? projectName = null, int maxItems = 500)
    {
        var result = await Call("cax_export", new { projectName, hardwarePath, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);
        if (result.IsError == true || result.StructuredContent is not JsonElement content) return result;
        var json = JsonNode.Parse(content.GetRawText())!.AsObject();
        try
        {
            string path = json["artifact"]!["path"]!.GetValue<string>(), sha256 = json["artifact"]!["sha256"]!.GetValue<string>();
            new ArtifactStore(options.ArtifactsDirectory).Read(path, 0, 1); // Artifact-root and link boundary before the Host opens a native file.
            json["automationml"] = JsonSerializer.SerializeToNode(new AutomationMlWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits).InspectFile(path, sha256), BridgeProtocol.Json);
        }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // The native export and its hashes stay valid; only the store publication failed.
            json["automationml"] = new JsonObject { ["error"] = new JsonObject { ["code"] = (error as BridgeException)?.Code ?? "AutomationMlWorkflowFailed",
                ["message"] = error is BridgeException ? error.Message : "AutomationML publication failed. The native export is retained." } };
        }
        return Result(json.ToJsonString(BridgeProtocol.Json), false);
    }

    // The worker reads the AML file by path; resolve it at the Host trust boundary to a retained record or a file inside the artifact root.
    private JsonElement CaxImportPayload(JsonElement payload)
    {
        var args = JsonNode.Parse(ObjectArguments(payload).GetRawText())!.AsObject();
        try
        {
            if (args["amlRef"] is not null)
            {
                if (args["path"] is not null || args["expectedSha256"] is not null) throw new BridgeException("InvalidRequest", "Use amlRef, or path plus expectedSha256, not both.");
                var (path, sha256) = new AutomationMlWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits).ResolveDocument(args["amlRef"]!.GetValue<string>());
                args["path"] = path; args["expectedSha256"] = sha256;
            }
            else
            {
                string path = args["path"]?.GetValue<string>() ?? throw new BridgeException("InvalidRequest", "cax_import requires amlRef, or path plus expectedSha256.");
                var read = JsonSerializer.SerializeToElement(new ArtifactStore(options.ArtifactsDirectory).Read(path, 0, 1), BridgeProtocol.Json);
                args["path"] = Path.Combine(Path.GetFullPath(options.ArtifactsDirectory), read.GetProperty("relativePath").GetString()!);
            }
        }
        catch (Exception error) when (error is BridgeException or InvalidOperationException)
        { throw new McpException(error is BridgeException bridge ? bridge.Code + ": " + bridge.Message : "amlRef and path must be strings."); }
        return JsonSerializer.SerializeToElement(args, BridgeProtocol.Json);
    }
}
