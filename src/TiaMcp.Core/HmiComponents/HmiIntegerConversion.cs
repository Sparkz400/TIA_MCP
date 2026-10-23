using System.Text.Json.Nodes;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core.HmiComponents;

/// <summary>Value-preserving offline type changes for explicit 8/16/32-bit state lists.</summary>
internal static class HmiIntegerConversion
{
    internal const string TypeChangeConflict = "PLC declaration type text changed; native binding compatibility needs review.";

    private static (long Min, long Max) Domain(string type) => type switch {
        "SInt" => (-128, 127), "USInt" => (0, 255), "Int" => (-32768, 32767),
        "UInt" => (0, 65535), "DInt" => (-2147483648, 2147483647), "UDInt" => (0, 4294967295),
        _ => throw Error("Integer conversion supports exact SInt/USInt/Int/UInt/DInt/UDInt types only.") };

    internal static JsonObject Evaluate(JsonObject selection, JsonObject? prior, JsonObject binding, JsonObject intent, string expectedType)
    {
        Fields(selection, "profile fromType toType");
        if (Text(selection, "profile", 128) != "integer-value-preserving.v1") throw Error("Unsupported explicit integer conversion profile.");
        string from = Text(selection, "fromType", 16), to = Text(selection, "toType", 16);
        var source = Domain(from); var target = Domain(to); var conflicts = new JsonArray();
        if (from == to) conflicts.Add("Conversion must explicitly change the integer type.");
        if (prior is null || (string?)prior["profile"] != "unified-integer-state-text-list.v1")
            conflicts.Add("Conversion requires an exact previous integer-state output.");
        if ((string?)prior?["componentContext"]?["binding"]?["afterDeclaration"]?["dataType"] != from ||
            (string?)binding["beforeDeclaration"]?["status"] != "capturedDeclaration" ||
            (string?)binding["beforeDeclaration"]?["dataType"] != from ||
            (string?)binding["afterDeclaration"]?["status"] != "capturedDeclaration" ||
            (string?)binding["afterDeclaration"]?["dataType"] != to || expectedType != to ||
            (string?)binding["selection"]?["declaredHmiType"] != to)
            conflicts.Add("Conversion types must match the exact predecessor, captured before/after declarations and explicit template/HMI type.");
        // State ranges, identity, defaults, language order and translations remain
        // byte-independent semantic equals; no remapping or fallback inference.
        if (!JsonNode.DeepEquals(prior?["intent"], intent))
            conflicts.Add("Value-preserving conversion requires unchanged complete intent, including state intervals, defaults, translations and output identities.");
        var intervals = new JsonArray(); bool fit = prior is not null;
        if (prior?["intent"]?["lists"]?[0]?["entries"] is JsonArray entries)
            foreach (var entry in entries)
            {
                long low = (long)entry!["fromValue"]!, high = (long)entry["toValue"]!;
                bool represented = low >= source.Min && high <= source.Max && low <= high && low >= target.Min && high <= target.Max;
                fit &= represented;
                intervals.Add(new JsonObject { ["stateId"] = entry["id"]!.DeepClone(), ["fromValue"] = low, ["toValue"] = high,
                    ["targetRepresentable"] = represented });
            }
        else fit = false;
        if (!fit) conflicts.Add("Every original listed interval must fit both exact types; overflow, negative-to-unsigned loss and implicit state removal refuse.");
        return new JsonObject {
            ["selection"] = selection.DeepClone(), ["sourceMinimum"] = source.Min, ["sourceMaximum"] = source.Max,
            ["targetMinimum"] = target.Min, ["targetMaximum"] = target.Max, ["listedIntervals"] = intervals,
            ["allListedValuesRepresentable"] = fit, ["fullSourceDomainRepresentable"] = source.Min >= target.Min && source.Max <= target.Max,
            ["status"] = conflicts.Count == 0 ? "checkedListedValues" : "conflict", ["conflicts"] = conflicts,
            ["sourceBindingConflicts"] = binding["conflicts"]!.DeepClone(),
            ["unlistedValuesVerified"] = false, ["runtimeConversionVerified"] = false, ["nativeCompatibilityVerified"] = false };
    }
}
