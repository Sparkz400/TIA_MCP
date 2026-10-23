using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

[McpServerResourceType]
public sealed class ProjectExplorerResources
{
    public const string Uri = "ui://tia-project-explorer/view.html";
    public const string Mime = "text/html;profile=mcp-app";

    internal static bool Supported(McpServer server)
    {
        var capabilities = JsonSerializer.SerializeToElement(server.ClientCapabilities);
        return capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("extensions", out var extensions)
            && extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("io.modelcontextprotocol/ui", out var ui)
            && ui.ValueKind == JsonValueKind.Object && ui.TryGetProperty("mimeTypes", out var types) && types.ValueKind == JsonValueKind.Array
            && types.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.String && type.GetString() == Mime);
    }

    [McpServerResource(UriTemplate = Uri, Name = "project-explorer", Title = "TIA project explorer", MimeType = Mime)]
    [Description("Read-only, sandboxed project-model.v1 viewer. No external network resources. Requires negotiated MCP Apps support; portable HTML export is available to every host.")]
    public ReadResourceResult Read(McpServer server)
    {
        if (!Supported(server)) throw new McpException("MCP Apps support was not negotiated. Use project_model_export with format html for a portable viewer.");
        var assets = ProjectExplorerArtifacts.FindAssets();
        var html = ProjectExplorerArtifacts.Render(File.ReadAllText(Path.Combine(assets, "index.template.html")), assets, "null");
        return new ReadResourceResult { Contents = [new TextResourceContents {
            Uri = Uri, MimeType = Mime, Text = html,
            Meta = new JsonObject { ["ui"] = new JsonObject { ["csp"] = new JsonObject {
                ["connectDomains"] = new JsonArray(), ["resourceDomains"] = new JsonArray(), ["frameDomains"] = new JsonArray()
            } } }
        }] };
    }

    internal static void Configure(IMcpRequestFilterBuilder filters)
    {
        filters.AddListToolsFilter(next => async (context, token) =>
        {
            var result = await next(context, token);
            // Clone before annotating: descriptors may be shared between clients.
            result = JsonSerializer.Deserialize<ListToolsResult>(JsonSerializer.Serialize(result))!;
            foreach (var tool in result.Tools)
            {
                tool.Meta ??= new JsonObject();
                if (Supported(context.Server) && tool.Name is "project_model" or "project_model_capture")
                    tool.Meta["ui"] = new JsonObject { ["resourceUri"] = Uri };
                else if (!tool.Name.StartsWith("project_model", StringComparison.Ordinal) && tool.Name != "artifact_read")
                    tool.Meta["ui"] = new JsonObject { ["visibility"] = new JsonArray("model") };
            }
            return result;
        });
        filters.AddListResourcesFilter(next => async (context, token) =>
        {
            var result = await next(context, token);
            if (!Supported(context.Server)) result.Resources = result.Resources.Where(resource => resource.Uri != Uri).ToList();
            return result;
        });
    }
}
