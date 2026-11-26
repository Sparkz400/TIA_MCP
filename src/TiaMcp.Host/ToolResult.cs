using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Core;

namespace TiaMcp.Host;

/// <summary>
/// The one place that builds a tool result. The MCP specification asks a server that returns structuredContent to also send the
/// serialized JSON as a text block, so older clients and this repository's own scripts keep working; both carry the same bytes,
/// serialized and parsed once.
/// </summary>
internal static class ToolResult
{
    // Shown for a file or permission failure instead of the exception text, which names absolute paths of the Host machine.
    internal const string FileFailure = "A retained or supplied file could not be read or written. No native action occurred.";

    /// <summary>The refusals the offline tools turn into a coded result; any other exception is a defect and stays a protocol error.</summary>
    internal static bool IsRefusal(Exception error) =>
        error is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or System.Xml.XmlException;

    internal static CallToolResult Of(object value, bool isError = false, JsonSerializerOptions? options = null) =>
        Of(JsonSerializer.SerializeToElement(value, options ?? BridgeProtocol.Json), isError);

    internal static CallToolResult Of(JsonElement element, bool isError = false) => new()
    {
        IsError = isError,
        StructuredContent = element,
        Content = [new TextContentBlock { Text = element.GetRawText() }]
    };

    /// <summary>For a worker response or other text that is already JSON.</summary>
    internal static CallToolResult FromJson(string json, bool isError = false)
    {
        using var document = JsonDocument.Parse(json);
        return Of(document.RootElement.Clone(), isError);
    }

    /// <summary>A coded refusal. A BridgeException keeps its own code and message; a file or permission failure never exposes its path.</summary>
    internal static CallToolResult Failure(Exception error, string fallbackCode, string? fixedMessage = null, bool? nativeAccessAttempted = null) =>
        Error((error as BridgeException)?.Code ?? (error is OperationCanceledException ? "OperationCancelled" : fallbackCode),
            error is BridgeException ? error.Message : fixedMessage ?? (error is IOException or UnauthorizedAccessException ? FileFailure : error.Message),
            (error as BridgeOperationException)?.Details, nativeAccessAttempted);

    internal static CallToolResult Error(string code, string message, JsonElement? details = null, bool? nativeAccessAttempted = null)
    {
        var body = new JsonObject { ["code"] = code, ["message"] = message };
        if (details is { } extra) body["details"] = JsonNode.Parse(extra.GetRawText());
        if (nativeAccessAttempted is { } native) body["nativeAccessAttempted"] = native;
        return Of(new JsonObject { ["error"] = body }, true);
    }

    internal static CallToolResult Guard(Func<object> action, string fallbackCode, string? fixedMessage = null, bool? nativeAccessAttempted = null)
    {
        try { return Of(action()); }
        catch (Exception error) when (IsRefusal(error)) { return Failure(error, fallbackCode, fixedMessage, nativeAccessAttempted); }
    }

    internal static async Task<CallToolResult> GuardAsync(Func<Task<object>> action, string fallbackCode, string? fixedMessage = null, bool? nativeAccessAttempted = null)
    {
        try { return Of(await action()); }
        catch (Exception error) when (IsRefusal(error)) { return Failure(error, fallbackCode, fixedMessage, nativeAccessAttempted); }
    }
}
