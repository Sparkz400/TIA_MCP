using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>One symbol's outcome of a batched read: its result, or the fixed code of why none could be produced.</summary>
public sealed record WebRuntimeBatchItem(WebRuntimeReadResult? Result, string? ErrorCode);

public sealed partial class WebRuntimeClient
{
    // The CPU refused or ignored a JSON-RPC batch: not an error of the session, so it never escapes.
    private sealed class BatchRefused : Exception;
    private const int MaximumSymbolsPerBatch = 16;
    private bool batchTentative;

    /// <summary>The current metadata check and read of one symbol: a symbol whose metadata changed since selection is never read.</summary>
    public async Task<WebRuntimeReadResult> CurrentAsync(WebRuntimeSymbol selection, CancellationToken cancellationToken)
    {
        var current = await BrowseAsync(selection.Symbol, false, cancellationToken);
        var refreshed = WebRuntimeProtocol.Symbol(Context, selection.Symbol, selection.Representation, current[0]);
        if (refreshed.MetadataSha256 != selection.MetadataSha256) throw Error("RuntimeSymbolMetadataChanged");
        return await ReadAsync(refreshed, cancellationToken);
    }

    /// <summary>Reads every selection as <see cref="CurrentAsync"/> would, but with the metadata Browse and the Read of up to 16 symbols
    /// in one JSON-RPC batch request on the pinned keep-alive connection. A CPU that does not answer a batch with a batch is read
    /// symbol by symbol instead. Request-level failures (timeout, transport, expired session) throw; symbol-level ones are items.</summary>
    public async Task<WebRuntimeBatchItem[]> ReadBatchAsync(IReadOnlyList<WebRuntimeSymbol> selections, CancellationToken cancellationToken)
    {
        Active();
        var items = new WebRuntimeBatchItem[selections.Count];
        // Every symbol is two requests; a CPU that limits parallel requests is never offered more than that in one batch.
        int perBatch = (int)Math.Clamp((Profile.MaximumParallelRequests ?? 2 * MaximumSymbolsPerBatch) / 2, 1, MaximumSymbolsPerBatch);
        for (int from = 0; from < selections.Count; from += perBatch)
        {
            int count = Math.Min(perBatch, selections.Count - from);
            bool tentative = false;
            if (batchSupport >= 0 && await TryBatch(selections, from, count, items, cancellationToken)) continue;
            // A first batch that died on the wire (a CPU that drops what it does not understand) is only tentatively unsupported:
            // if reading symbol by symbol fails too, it was an outage, and a batch is tried again later.
            tentative = batchSupport == -1 && batchTentative;
            try
            {
                for (int i = from; i < from + count; i++)
                {
                    try { items[i] = new(await CurrentAsync(selections[i], cancellationToken), null); }
                    catch (BridgeException failure) when (failure.Code is "RuntimeSymbolMetadataChanged" or "RuntimeBrowseFailed" or "RuntimeMetadataInvalid" or "RuntimeResponseInvalid" or "RuntimeProviderRejected")
                    { items[i] = new(null, failure.Code); }
                }
            }
            catch when (tentative) { batchSupport = 0; batchTentative = false; throw; }
            batchTentative = false;
        }
        return items;
    }

    private async Task<bool> TryBatch(IReadOnlyList<WebRuntimeSymbol> selections, int from, int count, WebRuntimeBatchItem[] items, CancellationToken cancellationToken)
    {
        var browseIds = new string[count]; var reads = new WebRuntimeReadRequest[count]; var bodies = new List<JsonElement>(2 * count);
        for (int i = 0; i < count; i++)
        {
            var selection = selections[from + i];
            browseIds[i] = Id(); reads[i] = WebRuntimeProtocol.Read(Context, selection, Id());
            bodies.Add(WebRuntimeProtocol.Browse(browseIds[i], selection.Symbol, false)); bodies.Add(reads[i].Body);
        }
        var batch = JsonSerializer.SerializeToElement(bodies);
        DateTimeOffset start = DateTimeOffset.UtcNow; long tick = Stopwatch.GetTimestamp();
        try
        {
            await Exchange(batch, bytes =>
            {
                using var document = ParseBatch(bytes);
                var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                        !byId.TryAdd(id.GetString()!, element)) throw Error("RuntimeResponseInvalid");
                }
                if (byId.Count != bodies.Count) throw Error("RuntimeResponseInvalid");
                for (int i = 0; i < count; i++)
                {
                    if (!byId.TryGetValue(browseIds[i], out var browse) || !byId.TryGetValue(reads[i].RequestId, out var read)) throw Error("RuntimeResponseInvalid");
                    var selection = selections[from + i];
                    try
                    {
                        var metadata = WebRuntimeProtocol.BrowseResult(Encoding.UTF8.GetBytes(browse.GetRawText()), browseIds[i], false);
                        var refreshed = WebRuntimeProtocol.Symbol(Context, selection.Symbol, selection.Representation, metadata[0]);
                        if (refreshed.MetadataSha256 != selection.MetadataSha256) throw Error("RuntimeSymbolMetadataChanged");
                        items[from + i] = new(WebRuntimeProtocol.ReadResult(Context, reads[i], Encoding.UTF8.GetBytes(read.GetRawText()),
                            start, DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(tick)), null);
                    }
                    catch (BridgeException failure) { items[from + i] = new(null, failure.Code); }
                }
                return true;
            }, cancellationToken, keepAlive: true);
            batchSupport = 1;
            return true;
        }
        catch (BatchRefused) { batchSupport = -1; batchTentative = false; return false; }
        // Until a batch has been answered once, a refusal or a dropped connection may be the CPU not taking one.
        catch (BridgeException failure) when (failure.Code is "RuntimeHttpFailure" or "RuntimeTransportFailure" && batchSupport == 0)
        { batchSupport = -1; batchTentative = true; return false; }
        // Too big as one request or one reply (large arrays): this group is read symbol by symbol.
        catch (BridgeException failure) when (failure.Code is "RuntimeRequestLimit" or "RuntimeResponseLimit") { return false; }
    }

    private static JsonDocument ParseBatch(ReadOnlyMemory<byte> bytes)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw Error("RuntimeResponseInvalid"); }
        if (document.RootElement.ValueKind != JsonValueKind.Array) { document.Dispose(); throw new BatchRefused(); }
        return document;
    }
}
