using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Engineering;

/// <summary>RFC 8785 (JCS) canonical JSON, byte-identical to the Openness worker's: ordinal (UTF-16) key order,
/// ECMAScript number text, minimal string escapes, no whitespace. Integer literals keep their exact digits
/// beyond 2^53, where JCS would round them through a double.</summary>
public static class CanonicalJson
{
    /// <summary>Refusal for a pending review prepared before RFC 8785 (1 November 2026): prepare it again.</summary>
    public const string FormChanged = "CanonicalFormChanged";
    // Set only while Compatible or Current re-derives a retained artifact the way an earlier build wrote it;
    // differed records whether that re-derivation serialized anything the two forms write differently.
    [ThreadStatic] private static bool legacy, differed;

    public static string Serialize(JsonNode node)
    {
        if (!legacy) return Rfc8785(node);
        string earlier = LegacySerialize(node);
        if (!differed)
            try { differed = Rfc8785(node) != earlier; } catch (BridgeException) { differed = true; }
        return earlier;
    }

    /// <summary>Exact JSON equality, number text included (<c>1</c> differs from <c>1.0</c>): for diffs that report
    /// a changed observation, unlike a hash, which compares numbers by value.</summary>
    public static bool ExactlyEqual(JsonNode a, JsonNode b) => LegacySerialize(a) == LegacySerialize(b);

    public static string Sha256(JsonNode node) => Hex(Serialize(node));

    /// <summary>A stored hash verifies against either form: builds before RFC 8785 (1 November 2026) wrote
    /// <see cref="LegacySha256"/>, so journal revisions that span the upgrade still chain.</summary>
    public static bool Matches(string? stored, JsonNode node)
    {
        if (stored is null) return false;
        try { if (stored == Hex(Rfc8785(node))) return true; }
        catch (BridgeException) { } // an earlier build also hashed lone surrogates; its value is checked below
        return stored == LegacySha256(node);
    }

    /// <summary>Reopens durable state (a job's review, a retained report or manifest, an admitted import) that
    /// re-derives its hashes or bytes. If that fails, it is re-derived once more in the pre-RFC 8785 form on this
    /// thread, so state an earlier build wrote verifies exactly as written. The first refusal stands when both
    /// fail, and also when the content serializes the same in both forms: then the form did not cause it.
    /// <paramref name="read"/> must only read.</summary>
    public static T Compatible<T>(Func<T> read)
    {
        if (legacy) return read();
        try { return read(); }
        catch (BridgeException first)
        {
            if (!PassesLegacy(read, out T value)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
            return value;
        }
    }

    /// <summary>Reopens a pending review whose approval must bind the current form. A review that verifies only in
    /// the pre-RFC 8785 form, and only because its content serializes differently there, is refused with
    /// <see cref="FormChanged"/>; any other failure keeps its own code.</summary>
    public static T Current<T>(Func<T> read, string message)
    {
        if (legacy) return read();
        try { return read(); }
        catch (BridgeException) { if (!PassesLegacy(read, out _)) throw; } // not a filter: the first read's locks are released first
        throw new BridgeException(FormChanged, message);
    }

    private static bool PassesLegacy<T>(Func<T> read, out T value)
    {
        legacy = true; differed = false;
        try { value = read(); return differed; } // passing without any difference means the first failure was not the form
        catch (BridgeException) { value = default!; return false; }
        finally { legacy = false; differed = false; }
    }

    private static string Rfc8785(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var output = new StringBuilder();
        Write(output, node, 0);
        return output.ToString();
    }

    /// <summary>The pre-RFC 8785 form: ordinal keys through System.Text.Json's default encoder (non-ASCII and
    /// + &lt; &gt; &amp; ' ` escaped as \uXXXX) with number text as written. Only for checking stored values and for
    /// identities that stored records compare (external resource keys, credential bindings).</summary>
    public static string LegacySha256(JsonNode node) => Hex(LegacySerialize(node));

    public static string LegacySerialize(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 128 }))
            WriteLegacy(writer, node, 0);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Write(StringBuilder output, JsonNode? node, int depth)
    {
        if (depth > 128) throw new BridgeException("TraversalLimit", "Canonical JSON exceeds its depth limit.");
        switch (node)
        {
            case null: output.Append("null"); break;
            case JsonObject obj:
                output.Append('{'); bool first = true;
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) output.Append(','); first = false;
                    AppendString(output, pair.Key); output.Append(':'); Write(output, pair.Value, depth + 1);
                }
                output.Append('}'); break;
            case JsonArray array:
                output.Append('[');
                for (int i = 0; i < array.Count; i++) { if (i > 0) output.Append(','); Write(output, array[i], depth + 1); }
                output.Append(']'); break;
            default:
                var value = node.AsValue();
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String: AppendString(output, Text(value)); break;
                    case JsonValueKind.Number: Number(output, value.ToJsonString()); break;
                    case JsonValueKind.True: output.Append("true"); break;
                    case JsonValueKind.False: output.Append("false"); break;
                    case JsonValueKind.Null: output.Append("null"); break;
                    default: throw Invalid("Canonical JSON accepts only JSON values.");
                }
                break;
        }
    }

    private static string Text(JsonValue value)
    {
        try { return value.TryGetValue(out string? text) ? text : JsonSerializer.Deserialize<string>(value.ToJsonString())!; }
        catch (Exception error) when (error is InvalidOperationException or JsonException or ArgumentException)
        { throw Invalid("Canonical JSON refuses a string that is not valid UTF-16."); }
    }

    private static void AppendString(StringBuilder output, string value)
    { if (!CanonicalJsonText.TryAppendString(output, value)) throw Invalid("Canonical JSON refuses a lone surrogate."); }

    // An integer literal is written exactly, so two different integers never share a hash; any other number is a double.
    private static void Number(StringBuilder output, string text)
    {
        if (text.AsSpan().IndexOfAny('.', 'e', 'E') < 0) { output.Append(BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
        // The worker reads the same literal with the same exact conversion (its runtime's double.Parse misrounds some).
        double value = CanonicalJsonText.ParseNumber(text) ?? throw Invalid("Canonical JSON refuses a number outside the double range.");
        output.Append(CanonicalJsonText.Number(value));
    }

    private static BridgeException Invalid(string message) => new("InvalidRequest", message);

    private static void WriteLegacy(Utf8JsonWriter writer, JsonNode? node, int depth)
    {
        if (depth > 128) throw new BridgeException("TraversalLimit", "Canonical JSON exceeds its depth limit.");
        switch (node)
        {
            case null: writer.WriteNullValue(); break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                { writer.WritePropertyName(pair.Key); WriteLegacy(writer, pair.Value, depth + 1); }
                writer.WriteEndObject(); break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var value in array) WriteLegacy(writer, value, depth + 1);
                writer.WriteEndArray(); break;
            default: node.WriteTo(writer); break;
        }
    }
}
