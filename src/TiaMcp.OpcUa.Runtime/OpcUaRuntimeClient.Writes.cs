using System.Globalization;
using System.Text.Json;
using Opc.Ua;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

internal sealed partial class OpcUaRuntimeClient
{
    /// <summary>Gated owned-PLCSIM write: every exact precondition holds before one Write service call, then an independent readback.</summary>
    internal async Task<object> Write(string attemptId, IReadOnlyList<OpcUaRuntimeWriteItem> writes, CancellationToken ct)
    {
        var nodes = writes.Select(w => w.Node).ToArray();
        static bool Holds(OpcUaRuntimeValue read, string encoding, JsonElement wanted) => read.Outcome == "value" && read.Encoding == encoding &&
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(read.Value), wanted);
        var before = await Read(attemptId, nodes, ct);
        if (writes.Where((w, i) => !Holds(before.Items[i], w.Encoding, w.Expected)).Any())
            return new { attemptId, state = "preconditionMismatch", dispatched = false, before };
        // Every value converts to the node's observed built-in type before anything is sent.
        var values = new WriteValueCollection(writes.Select((w, i) => new WriteValue { NodeId = Resolve(w.Node), AttributeId = Attributes.Value,
            Value = new DataValue(WriteVariant(before.Items[i].BuiltInType, w.Encoding, w.Value)) }).ToList());
        var response = await Session.WriteAsync(null, values, ct);
        if (response.Results.Count != writes.Count) throw Error("UaResponseInvalid");
        var after = await Read(attemptId, nodes, ct);
        var matches = writes.Select((w, i) => Holds(after.Items[i], w.Encoding, w.Value)).ToArray();
        var result = new { attemptId, state = response.Results.All(s => StatusCode.IsGood(s)) && matches.All(m => m) ? "written" : "incomplete", dispatched = true,
            before, writeStatusCodes = response.Results.Select(s => $"0x{s.Code:x8}").ToArray(), after, readbackMatches = matches,
            note = "Readback is one later observation; the PLCSIM program scan may overwrite assigned values. Assignment is not forcing." };
        CheckOutput(result);
        return result;
    }

    // Only the scalar forms runtime_ua_read reports; the value must round-trip to exactly the supplied canonical text.
    private static Variant WriteVariant(string? builtIn, string encoding, JsonElement value)
    {
        try
        {
            string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            const NumberStyles integer = NumberStyles.AllowLeadingSign;
            var invariant = CultureInfo.InvariantCulture;
            object? parsed = builtIn switch {
                "Boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
                "String" => text,
                "SByte" => sbyte.Parse(text!, integer, invariant), "Byte" => byte.Parse(text!, integer, invariant),
                "Int16" => short.Parse(text!, integer, invariant), "UInt16" => ushort.Parse(text!, integer, invariant),
                "Int32" => int.Parse(text!, integer, invariant), "UInt32" => uint.Parse(text!, integer, invariant),
                "Int64" => long.Parse(text!, integer, invariant), "UInt64" => ulong.Parse(text!, integer, invariant),
                "Float" => float.Parse(text!, NumberStyles.Float, invariant), "Double" => double.Parse(text!, NumberStyles.Float, invariant),
                _ => null };
            var (kind, data, _) = Scalar(parsed);
            if (parsed is null || kind != encoding || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), value)) throw Error("UaWriteValueInvalid");
            return new Variant(parsed);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentNullException or InvalidOperationException) { throw Error("UaWriteValueInvalid"); }
    }
}
