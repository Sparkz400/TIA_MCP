using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core;

internal static class ReportPageRows
{
    internal static (JsonArray Rows, int Next) Read(JsonArray source, int offset, int count, int byteBudget, string omissionReason)
    {
        var rows = new JsonArray(); int next = offset, used = 0;
        while (next < source.Count && rows.Count < count)
        {
            JsonObject row = new() { ["index"] = next, ["value"] = source[next]?.DeepClone() };
            int size = JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length;
            if (size > byteBudget)
            {
                row = new() { ["index"] = next, ["omitted"] = true, ["reason"] = omissionReason };
                size = JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length;
                if (size > byteBudget) throw new BridgeException("ArtifactCapacity", "Page budget cannot hold an omission row.");
            }
            if (used + size > byteBudget) break;
            rows.Add(row); used += size; next++;
        }
        return (rows, next);
    }
}
