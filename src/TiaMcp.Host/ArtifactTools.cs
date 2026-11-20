using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class ArtifactTools(HostOptions options)
{
    [McpServerTool(Name = "artifact_read", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ArtifactChunk))]
    [Description("Transfer an exported artifact from the TIA host, including a VM. Reads at most 64 KiB as base64 per call, with file SHA-256 and next offset. Only files under the configured artifact directory are accessible; paths refer to the TIA host.")]
    public CallToolResult Read(string path, long offset = 0, int count = 65536)
    {
        try { return ToolResult.Of(new ArtifactStore(options.ArtifactsDirectory).Read(path, offset, count)); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or ArgumentException)
        { return ToolResult.Failure(error, "ArtifactReadFailed"); }
    }
}
