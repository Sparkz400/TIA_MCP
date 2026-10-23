using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TechnologyObjects;

/// <summary>Typed supplied encoder routes and exact retained telegram references; no native admission.</summary>
internal static class TechnologyEncoderReview
{
    internal static void Add(JsonObject report, JsonObject review, JsonObject request, Func<string, JsonObject> drive)
    {
        var declarations = Object(request["encoders"]); Fields(declarations, "before after");
        var nodes = review["model"]!["nodes"]!.AsArray().OfType<JsonObject>().ToArray();
        var byPath = nodes.ToLookup(n => (string?)n["objectPath"], StringComparer.Ordinal);
        var byId = nodes.ToLookup(n => (string?)n["nodeId"], StringComparer.Ordinal);
        string plc = (string)request["before"]!["identity"]!["plcPath"]!;
        string Resolve(JsonObject selector, string kind)
        {
            Fields(selector, "nodeId objectPath"); string id = Text(selector, "nodeId", 8192)!, path = Text(selector, "objectPath", 8192)!;
            var paths = byPath[path].Take(2).ToArray(); var ids = byId[id].Take(2).ToArray();
            if (paths.Length > 1 || ids.Length > 1) return "ambiguous";
            if (paths.Length == 0 && ids.Length == 0) return "missing";
            if (paths.Length != 1 || ids.Length != 1 || !ReferenceEquals(paths[0], ids[0])) return "identityMismatch";
            var node = paths[0]; if ((string?)node["origin"] == "virtual") return "virtualObject";
            if ((string?)node["kind"] != kind) return "wrongKind";
            if (kind == "plc") return path == plc ? "capturedObject" : "differentPlc";
            if (node["plcPath"] is null) return "scopeUnknown";
            return (string?)node["plcPath"] == plc ? "capturedObject" : "differentPlc";
        }
        var observations = new JsonArray(); var changes = new JsonArray(); var coverage = new JsonObject();
        long reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, BridgeProtocol.Json).Length;
        void Add(JsonArray array, JsonObject row)
        {
            reportBytes += JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length;
            if (reportBytes > 16L * 1024 * 1024) throw new BridgeException("ArtifactCapacity", "Encoder evidence exceeds the 16 MiB report bound.");
            array.Add(row);
        }
        var owner = byPath[(string)request["technologyPath"]!].Single(n => (string?)n["origin"] != "virtual");
        coverage["ownerResolution"] = Resolve(new JsonObject { ["nodeId"] = owner["nodeId"]!.DeepClone(), ["objectPath"] = owner["objectPath"]!.DeepClone() }, "technologyObject");
        var states = new List<Dictionary<string, JsonObject>>();
        foreach (string phase in new[] { "before", "after" })
        {
            var declaration = Object(declarations[phase]); Fields(declaration, "complete observations");
            coverage[phase + "Complete"] = Boolean(declaration, "complete");
            if (declaration["observations"] is not JsonArray rows || rows.Count > 256) throw Invalid("Supply 0..256 encoder observations per phase.");
            var state = new Dictionary<string, JsonObject>(StringComparer.Ordinal); states.Add(state); int index = -1;
            foreach (var item in rows)
            {
                index++; var row = Object(item); Fields(row, "id provider interfaceName state route sensorIndex connectOption target unknownReason");
                if (!state.TryAdd(Text(row, "id", 256)!, row)) throw Invalid("Duplicate exact encoder observation ID.");
                Text(row, "provider", 256); Text(row, "interfaceName", 256);
                string status = Text(row, "state", 32)!, route = Text(row, "route", 32)!;
                string? option = Text(row, "connectOption", 32, true), reason = Text(row, "unknownReason", 1024, true);
                int? sensor = Integer(row, "sensorIndex", 2);
                if (status is not ("connected" or "disconnected" or "unknown")) throw Invalid("Unknown encoder state.");
                if (route is not ("module" or "splitModules" or "channel" or "addresses" or "dbMember" or "startdriveTelegram" or "unknown")) throw Invalid("Unknown encoder route.");
                if (option is not (null or "Default" or "AllowAllModules")) throw Invalid("Unknown connect option.");
                bool unknown = status == "unknown" || sensor is null || option is null || route == "unknown";
                if ((status == "disconnected" || route == "unknown") && row["target"] is not null) throw Invalid("Disconnected or unknown-route observation cannot declare a target.");
                var resolved = new JsonObject(); var contradictions = new JsonArray();
                if (status == "disconnected" && sensor is > 0) contradictions.Add("disconnectedWithSensorIndex");
                if (status == "connected" && sensor == 0) contradictions.Add("connectedWithZeroSensorIndex");
                if (row["target"] is null) { resolved["state"] = status == "disconnected" ? "declaredDisconnected" : "notCaptured"; unknown |= status != "disconnected"; }
                else
                {
                    var target = Object(row["target"]); resolved["state"] = "suppliedTarget";
                    switch (route)
                    {
                        case "module": resolved["moduleResolution"] = Resolve(target, "hardware"); break;
                        case "splitModules":
                            Fields(target, "inputModule outputModule");
                            resolved["inputModuleResolution"] = Resolve(Object(target["inputModule"]), "hardware");
                            resolved["outputModuleResolution"] = Resolve(Object(target["outputModule"]), "hardware"); break;
                        case "channel":
                            Fields(target, "module number type direction"); resolved["moduleResolution"] = Resolve(Object(target["module"]), "hardware");
                            int? channelNumber = Integer(target, "number"); string? channelType = Text(target, "type", 256, true), channelDirection = Text(target, "direction", 256, true);
                            unknown |= channelNumber is null || channelType is null || channelDirection is null;
                            resolved["channelResolution"] = "notVerified"; break;
                        case "addresses":
                            Fields(target, "plc input output"); resolved["plcResolution"] = Resolve(Object(target["plc"]), "plc");
                            foreach (string direction in new[] { "input", "output" })
                            {
                                var address = Object(target[direction]); Fields(address, "value unit"); int? value = Integer(address, "value"); string unit = Text(address, "unit", 16)!;
                                if (unit is not ("bit" or "byte" or "unknown")) throw Invalid("Use explicit bit, byte or unknown address units.");
                                unknown |= value is null || unit == "unknown";
                                resolved[direction + "BitAddress"] = value is null || unit == "unknown" ? null : (long)value * (unit == "byte" ? 8 : 1);
                            }
                            break;
                        case "dbMember":
                            Fields(target, "block member"); resolved["blockResolution"] = Resolve(Object(target["block"]), "block"); Text(target, "member", 8192);
                            resolved["memberResolution"] = "notVerified"; break;
                        case "startdriveTelegram":
                            Fields(target, "recordId phase telegramId"); string recordId = Text(target, "recordId", 64)!; EngineeringIdentityGuard.Sha256(recordId, "drive.recordId");
                            if (Text(target, "phase", 16) != phase) throw Invalid("Telegram selector must name the same phase as its encoder observation.");
                            string telegramId = Text(target, "telegramId", 256)!; var envelope = drive(recordId);
                            if ((string?)envelope["reviewId"] != (string?)request["reviewId"]) throw Invalid("Encoder telegram must belong to this exact saved review.");
                            var driveReport = envelope["report"]!;
                            var telegram = (driveReport["telegrams"] as JsonArray)?.SingleOrDefault(r => (string?)r!["phase"] == phase && (string?)r["telegram"]!["id"] == telegramId);
                            resolved["telegramResolution"] = driveReport["telegrams"] is null ? "unsupportedVersion" : telegram is null ? "missing" : "retainedDeclaration";
                            resolved["driveHardwarePath"] = driveReport["hardwarePath"]!.DeepClone();
                            var hardware = byPath[(string)driveReport["hardwarePath"]!].Single(n => (string?)n["origin"] != "virtual");
                            resolved["driveHardwareResolution"] = Resolve(new JsonObject { ["nodeId"] = hardware["nodeId"]!.DeepClone(), ["objectPath"] = hardware["objectPath"]!.DeepClone() }, "hardware");
                            resolved["driveIdentity"] = driveReport["identity"]!.DeepClone();
                            resolved["driveValueDomain"] = driveReport[phase + "ValueDomain"]!.DeepClone();
                            resolved["telegramEvidence"] = telegram?.DeepClone();
                            resolved["telegramChange"] = (driveReport["telegramChanges"] as JsonArray)?.SingleOrDefault(r => (string?)r!["id"] == telegramId)?.DeepClone();
                            resolved["valueDomainComparison"] = JsonNode.DeepEquals(request[phase]!["valueDomain"], driveReport[phase + "ValueDomain"]) ? "equalDeclarations" : "differentDeclarations";
                            int? number = (int?)telegram?["telegram"]?["number"];
                            resolved["externalEncoderNumberCheck"] = (string?)request[phase]!["identity"]!["technologyType"] != "TO_ExternalEncoder" ? "outsideDocumentedProfile" : number is null ? "unknown" : number is 81 or 83 ? "documentedNumber" : "differentNumber";
                            break;
                    }
                }
                if (unknown && reason is null) throw Invalid("Unknown encoder state, route, sensor, option or target fields require a reason.");
                Add(observations, new JsonObject { ["phase"] = phase, ["sourceIndex"] = index, ["encoder"] = row.DeepClone(), ["targetEvidence"] = resolved,
                    ["contradictions"] = contradictions, ["nativeConnectionVerified"] = false, ["targetCompatibilityVerified"] = false });
            }
            report["summary"]![phase + "EncoderCount"] = rows.Count;
        }
        foreach (string id in states[0].Keys.Union(states[1].Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            states[0].TryGetValue(id, out var before); states[1].TryGetValue(id, out var after); var fields = new JsonArray();
            if (before is not null && after is not null)
                foreach (string field in new[] { "provider", "interfaceName", "state", "route", "sensorIndex", "connectOption", "target", "unknownReason" })
                    if (!JsonNode.DeepEquals(field == "target" ? ComparableTarget(before) : before[field], field == "target" ? ComparableTarget(after) : after[field])) fields.Add(field);
            Add(changes, new JsonObject { ["id"] = id, ["status"] = before is null ? "onlyAfter" : after is null ? "onlyBefore" : fields.Count == 0 ? "unchanged" : "changed", ["changedFields"] = fields, ["identityEqualityVerified"] = false });
        }
        report["schemaVersion"] = "technology-profile-review.v4"; report["encoderConnections"] = observations; report["encoderChanges"] = changes; report["encoderCoverage"] = coverage;
        report["summary"]!["changedEncoderCount"] = changes.Count(r => (string?)r!["status"] != "unchanged");
        if ((string?)request["afterKind"] == "proposal" && (int)report["summary"]!["changedCount"]! + (int)report["summary"]!["changedEncoderCount"]! > 500) throw Invalid("At most 500 changed parameter, relationship and encoder proposal records.");
        report["encoderPolicy"] = "Exact supplied IDs correlate observations only. Provider/interface labels and completeness are supplied. Saved path and node ID must resolve together in the declared PLC; channel/DB member existence is unverified. Addresses normalize only explicitly declared bit/byte units, never readback-unit assumptions. Retained telegrams bind exact record, saved review, phase and row ID; matching numbers or addresses do not establish wiring. V21 documents 81/83 for S7-1500 TO_ExternalEncoder; the number check alone establishes no CPU, firmware, version, license, geometry or route eligibility. No native connection, mutation authority, safe motion or complete impact proof.";
    }
    private static JsonNode? ComparableTarget(JsonObject row)
    { var target = row["target"]?.DeepClone(); if ((string?)row["route"] == "startdriveTelegram" && target is JsonObject value) value.Remove("phase"); return target; }
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw Invalid("Expected encoder object.");
    private static void Fields(JsonObject row, string fields)
    { var names = fields.Split(' '); if (row.Count != names.Length || names.Any(n => !row.ContainsKey(n))) throw Invalid("Unexpected encoder fields: " + fields); }
    private static string? Text(JsonObject row, string key, int max, bool nullable = false)
    { if (nullable && row[key] is null) return null; if (row[key] is not JsonValue v || !v.TryGetValue<string>(out string? s) || s is null) throw Invalid("Expected encoder text: " + key); try { EngineeringIdentityGuard.Text(s, key, max); return s; } catch (BridgeException e) { throw Invalid(e.Message); } }
    private static int? Integer(JsonObject row, string key, int max = int.MaxValue)
    { if (row[key] is null) return null; if (row[key] is JsonValue v && v.TryGetValue<int>(out int n) && n >= 0 && n <= max) return n; throw Invalid("Encoder integer out of range: " + key); }
    private static bool Boolean(JsonObject row, string key) => row[key] is JsonValue v && v.TryGetValue<bool>(out bool b) ? b : throw Invalid("Expected encoder Boolean: " + key);
    private static BridgeException Invalid(string message) => new("InvalidTechnologyProfile", message);
}
