using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Engineering;

/// <summary>Stable ordinal object-key ordering; array order and numeric representation are preserved.</summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 128 }))
            Write(writer, node, 0);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Sha256(JsonNode node) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(node))));

    private static void Write(Utf8JsonWriter writer, JsonNode? node, int depth)
    {
        if (depth > 128) throw new BridgeException("TraversalLimit", "Canonical JSON exceeds its depth limit.");
        switch (node)
        {
            case null: writer.WriteNullValue(); break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                { writer.WritePropertyName(pair.Key); Write(writer, pair.Value, depth + 1); }
                writer.WriteEndObject(); break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var value in array) Write(writer, value, depth + 1);
                writer.WriteEndArray(); break;
            default: node.WriteTo(writer); break;
        }
    }
}
