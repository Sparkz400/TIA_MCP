using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

/// <summary>The complete output schema of every tool whose schema tools/list does not carry inline (larger than
/// ToolSurface.InlineOutputSchemaBytes, or a review_* tool whose result depends on its record kind). The tool names the
/// URI in _meta["tiaMcp/outputSchema"]; a review_* tool names it as a template with {kind}.</summary>
[McpServerResourceType]
public sealed class OutputSchemaResources
{
    public const string Prefix = "tia://output-schemas/";
    public const string Template = Prefix + "{tool}";

    [McpServerResource(UriTemplate = Template, Name = "output-schema", Title = "Tool output schema", MimeType = "application/schema+json")]
    [Description("The complete JSON Schema of one tool's structured result, success and Host refusal envelope, as the tool's _meta[\"tiaMcp/outputSchema\"] names it: tia://output-schemas/{tool}, or tia://output-schemas/review_page.opcua for a review_* tool and record kind.")]
    public static string Schema(string tool) =>
        ToolSurface.OutputSchemaOf(tool) is { } schema
            ? JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true })
            : throw new McpProtocolException("ResourceNotFound: no output schema for " + tool + "; the tool's _meta[\"tiaMcp/outputSchema\"] names the available URI.",
                McpErrorCode.ResourceNotFound);

    /// <summary>completion/complete for the template: the tools and review_* tool.kind names that have a schema.</summary>
    internal static IEnumerable<string>? Completions(string? template, string argument) => (template, argument) switch
    {
        (Template, "tool") => ToolSurface.AllOutputSchemas.Value.Keys.Concat(ToolSurface.TypedSchemas.Keys).Order(StringComparer.Ordinal),
        _ => null,
    };
}
