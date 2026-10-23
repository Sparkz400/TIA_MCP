using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

internal sealed class DelegateBridge(Func<string, JsonObject, JsonObject> callback) : IBridgeClient
{
    public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        => Task.FromResult(JsonSerializer.SerializeToElement(callback(operation, JsonSerializer.SerializeToNode(arguments ?? new { }, BridgeProtocol.Json)!.AsObject()), BridgeProtocol.Json));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
