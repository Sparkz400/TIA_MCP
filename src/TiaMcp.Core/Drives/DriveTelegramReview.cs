using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.Drives;

/// <summary>Explicitly typed supplied telegram observations, without native compatibility claims.</summary>
internal static class DriveTelegramReview
{
    internal static void Add(JsonObject report, JsonObject review, JsonObject request)
    {
        var observations = new JsonArray(); var changes = new JsonArray();
        var phases = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);
        var nodes = review["model"]!["nodes"]!.AsArray().OfType<JsonObject>().ToArray();
        var byPath = nodes.Where(n => n["objectPath"] is JsonValue v && v.TryGetValue<string>(out _)).ToLookup(n => (string)n["objectPath"]!, StringComparer.Ordinal);
        var byId = nodes.Where(n => n["nodeId"] is JsonValue v && v.TryGetValue<string>(out _)).ToLookup(n => (string)n["nodeId"]!, StringComparer.Ordinal);
        string Resolve(string? path)
        {
            if (path is null) return "notDeclared";
            var found = byPath[path].Take(2).ToArray();
            if (found.Length == 0) return "missing";
            if (found.Length != 1) return "ambiguousIdentity";
            if (found[0]["nodeId"] is not JsonValue id || !id.TryGetValue<string>(out string? key) || key is null) return "invalidIdentity";
            try { EngineeringIdentityGuard.Text(key, "nodeId", 8192); } catch (BridgeException) { return "invalidIdentity"; }
            if (byId[key].Count() != 1) return "ambiguousIdentity";
            if ((string?)found[0]["origin"] == "virtual") return "virtualObject";
            return (string?)found[0]["kind"] == "plc" ? "capturedPlc" : "wrongKind";
        }
        foreach (string phase in new[] { "before", "after" })
        {
            var snapshot = Object(request[phase]); var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            phases.Add(phase, rows); bool complete = Boolean(snapshot, "telegramsComplete");
            report["coverage"]![phase + "TelegramsComplete"] = complete;
            int index = -1;
            foreach (var item in Array(snapshot, "telegrams", 128))
            {
                index++; var row = Object(item);
                Fields(row, "id type number inputBytes outputBytes addressesComplete addresses unknownReason");
                string id = Text(row, "id", 256)!;
                if (!rows.TryAdd(id, row)) throw Error("Duplicate exact telegram ID.");
                string? type = Text(row, "type", 256, true), reason = Text(row, "unknownReason", 1024, true);
                if (type is not (null or "MainTelegram" or "TorqueTelegram" or "SafetyTelegram" or "EdgeTelegram" or "SupplementaryTelegram" or "AdditionalTelegram")) throw Error("Unknown telegram type; use null with a reason for unsupported metadata.");
                int? number = Integer(row, "number"), input = Integer(row, "inputBytes"), output = Integer(row, "outputBytes");
                bool addressesComplete = Boolean(row, "addressesComplete");
                if ((type is null || number is null || input is null || output is null || !addressesComplete) && reason is null) throw Error("Unknown telegram fields or incomplete address capture require a reason.");
                var addresses = new JsonArray(); var ids = new HashSet<string>(StringComparer.Ordinal); int addressIndex = -1;
                foreach (var entry in Array(row, "addresses", 16))
                {
                    addressIndex++; var address = Object(entry);
                    Fields(address, "id plcPath direction context startAddress addressUnit length lengthUnit unknownReason");
                    if (!ids.Add(Text(address, "id", 256)!)) throw Error("Duplicate exact address ID within telegram.");
                    string? plc = Text(address, "plcPath", 8192, true), direction = Text(address, "direction", 16, true), context = Text(address, "context", 256, true);
                    if (direction is not (null or "Input" or "Output")) throw Error("Unknown address direction.");
                    string unit = Text(address, "addressUnit", 16)!, lengthUnit = Text(address, "lengthUnit", 16)!;
                    if (unit is not ("bit" or "byte" or "unknown") || lengthUnit is not ("bit" or "byte" or "unknown")) throw Error("Use explicit bit, byte or unknown units.");
                    int? start = Integer(address, "startAddress"), length = Integer(address, "length");
                    string? unknown = Text(address, "unknownReason", 1024, true);
                    if ((plc is null || direction is null || context is null || start is null || length is null || unit == "unknown" || lengthUnit == "unknown") && unknown is null) throw Error("Unknown address fields require a reason.");
                    long? startBit = Bits(start, unit), lengthBits = Bits(length, lengthUnit);
                    int? size = direction == "Input" ? input : direction == "Output" ? output : null;
                    addresses.Add(new JsonObject {
                        ["sourceIndex"] = addressIndex, ["address"] = address.DeepClone(), ["plcResolution"] = Resolve(plc),
                        ["startBit"] = startBit, ["lengthBits"] = lengthBits,
                        ["endBitExclusive"] = startBit is not null && lengthBits is not null ? startBit + lengthBits : null,
                        ["directionalSizeComparison"] = size is null || lengthBits is null ? "unknown" : lengthBits == 8L * size ? "equalDeclaredSize" : "differentDeclaredSize",
                        ["nativeAddressVerified"] = false });
                }
                observations.Add(new JsonObject { ["phase"] = phase, ["sourceIndex"] = index, ["telegram"] = row.DeepClone(), ["addresses"] = addresses,
                    ["numberSemantics"] = number is null ? "unknown" : number == 999 && type == "MainTelegram" ? "freelyConfigurable" : "declaredNumber",
                    ["nativeCompatibilityVerified"] = false });
            }
            report["summary"]![phase + "TelegramCount"] = rows.Count;
        }
        foreach (string id in phases["before"].Keys.Union(phases["after"].Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            phases["before"].TryGetValue(id, out var before); phases["after"].TryGetValue(id, out var after);
            var fields = new JsonArray();
            if (before is not null && after is not null)
                foreach (string field in new[] { "type", "number", "inputBytes", "outputBytes", "addressesComplete", "addresses", "unknownReason" })
                    if (!JsonNode.DeepEquals(field == "addresses" ? OrderedAddresses(before) : before[field], field == "addresses" ? OrderedAddresses(after) : after[field])) fields.Add(field);
            changes.Add(new JsonObject { ["id"] = id, ["status"] = before is null ? "onlyAfter" : after is null ? "onlyBefore" : fields.Count == 0 ? "unchanged" : "changed",
                ["changedFields"] = fields, ["identityEqualityVerified"] = false, ["nativeCompatibilityVerified"] = false });
        }
        report["schemaVersion"] = "drive-profile-review.v2"; report["telegrams"] = observations; report["telegramChanges"] = changes;
        report["summary"]!["changedTelegramCount"] = changes.Count(r => (string?)r!["status"] != "unchanged");
        report["telegramPolicy"] = "Exact supplied telegram IDs correlate declarations only; numbers, types and addresses do not establish object identity or layout. Input/output sizes are explicitly bytes. Address and length units are independently supplied; only explicit bit/byte values normalize with exact Int64 arithmetic. Unknown units never become byte addresses. Declared size comparisons do not establish valid geometry. PLC resolution is saved context, not ownership, freshness or live wiring. Completeness is supplied. No telegram/encoder eligibility, signal layout, safety, rebind or motion qualification.";
    }
    private static JsonArray OrderedAddresses(JsonObject telegram) => new(telegram["addresses"]!.AsArray()
        .OrderBy(row => (string)row!["id"]!, StringComparer.Ordinal).Select(row => row!.DeepClone()).ToArray());
    private static long? Bits(int? value, string unit) => value is null || unit == "unknown" ? null : (long)value * (unit == "byte" ? 8 : 1);
    private static int? Integer(JsonObject row, string key)
    { if (row[key] is null) return null; if (row[key] is JsonValue v && v.TryGetValue<int>(out int n) && n >= 0) return n; throw Error("Expected nullable nonnegative Int32: " + key); }
    private static string? Text(JsonObject row, string key, int max, bool nullable = false)
    {
        if (nullable && row[key] is null) return null;
        if (row[key] is not JsonValue v || !v.TryGetValue<string>(out string? s) || s is null) throw Error("Expected text: " + key);
        try { EngineeringIdentityGuard.Text(s, key, max); return s; } catch (BridgeException e) { throw Error(e.Message); }
    }
    private static bool Boolean(JsonObject row, string key) => row[key] is JsonValue v && v.TryGetValue<bool>(out bool b) ? b : throw Error("Expected Boolean: " + key);
    private static JsonArray Array(JsonObject row, string key, int max) => row[key] is JsonArray a && a.Count <= max ? a : throw Error("Missing or oversized collection: " + key);
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw Error("Expected object.");
    private static void Fields(JsonObject row, string fields)
    { var keys = fields.Split(' '); if (row.Count != keys.Length || keys.Any(k => !row.ContainsKey(k))) throw Error("Unexpected telegram fields; expected " + fields); }
    private static BridgeException Error(string message) => new("InvalidDriveProfile", message);
}
