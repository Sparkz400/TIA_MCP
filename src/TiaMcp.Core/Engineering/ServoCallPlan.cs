using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Core.Engineering;

/// <summary>#214: plan one caller FB that calls a selected servo wrapper FB once per reviewed drive, as one multi-instance with
/// one fault Bool, wired to the drive's own status/control tags from drive_inventory. Nothing is inferred from names: the wrapper's
/// parameters are mapped explicitly and checked against its native interface, reset/SS1 expressions are taken as confirmed, and
/// any other writer of a selected drive's control tag blocks the plan. Read-only; the plan carries a source_upsert payload.</summary>
public static partial class ServoCallPlan
{
    public static async Task<JsonObject> Plan(IBridgeClient bridge, JsonObject request, string exportDirectory, CancellationToken cancellationToken)
    {
        string plc = Text(request["plcPath"]) ?? throw Invalid("plcPath is required.");
        string caller = Identifier(request, "callerName"), wrapper = Text(request["wrapperName"]) ?? throw Invalid("wrapperName is required.");
        var map = request["parameters"] as JsonObject ?? throw Invalid("parameters must map status, control, reset, ss1 and fault to wrapper parameter names.");
        string P(string role) => Text(map[role]) ?? throw Invalid("parameters." + role + " is required.");
        string status = P("status"), control = P("control"), reset = P("reset"), ss1 = P("ss1"), fault = P("fault");
        string resetExpression = Expression(request, "resetExpression"), ss1Expression = Expression(request, "ss1Expression");
        string faultPattern = Text(request["faultNaming"]) ?? "{instance}_Fault";
        if (!faultPattern.Contains("{instance}", StringComparison.Ordinal)) throw Invalid("faultNaming must contain {instance}.");
        var drives = request["drives"] as JsonArray ?? throw Invalid("drives must list the reviewed drives.");
        if (drives.Count is 0 or > 64) throw Invalid("drives must list 1..64 drives.");

        var blockers = new JsonArray();
        var inventory = await DriveInventory.Capture(bridge, Text(request["projectName"]), cancellationToken).ConfigureAwait(false);
        foreach (var b in inventory["blockers"]?.AsArray() ?? []) blockers.Add(b!.DeepClone());

        // The wrapper's native interface decides which parameters exist, in which section, with which type.
        var read = Parse(await bridge.CallAsync("block_read", new JsonObject { ["plcPath"] = plc, ["blockName"] = wrapper, ["artifactDirectory"] = exportDirectory }, cancellationToken).ConfigureAwait(false));
        var members = Interface(Text(read["xml"]) ?? throw new BridgeException("ResponseTooLarge", "The wrapper's XML export is not inline; select a smaller wrapper."));
        (string Section, string Type)? Member(string name) => members.TryGetValue(name, out var m) ? m : null;
        void Require(string role, string name, string[] sections, string? type)
        {
            var m = Member(name);
            if (m is null) blockers.Add(Blocker("wrapperParameter", role, "The wrapper has no parameter " + name + "."));
            else if (!sections.Contains(m.Value.Section)) blockers.Add(Blocker("wrapperParameter", role, name + " is in section " + m.Value.Section + ", not " + string.Join("/", sections) + "."));
            else if (type is not null && !string.Equals(m.Value.Type, type, StringComparison.OrdinalIgnoreCase)) blockers.Add(Blocker("wrapperParameter", role, name + " has type " + m.Value.Type + ", not " + type + "."));
        }
        Require("reset", reset, ["Input"], "Bool"); Require("ss1", ss1, ["Input"], "Bool"); Require("fault", fault, ["Output"], "Bool");
        Require("status", status, ["Input", "InOut"], null); Require("control", control, ["Output", "InOut"], null);

        var rows = new JsonArray(); var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in drives.OfType<JsonObject>())
        {
            string path = Text(entry["hardwarePath"]) ?? throw Invalid("Each drive needs hardwarePath.");
            string instance = Identifier(entry, "instanceName");
            if (!paths.Add(path)) blockers.Add(Blocker("duplicateDrive", path, "A drive is listed twice; each drive gets exactly one call."));
            if (!instances.Add(instance)) blockers.Add(Blocker("duplicateInstance", instance, "Instance names must be unique."));
            string faultName = faultPattern.Replace("{instance}", instance, StringComparison.Ordinal);
            if (!IdentifierPattern().IsMatch(faultName) || !instances.Add(faultName)) blockers.Add(Blocker("faultName", faultName, "The fault Bool name is invalid or collides."));
            var drive = inventory["drives"]?.AsArray().OfType<JsonObject>().FirstOrDefault(d => Text(d["hardwarePath"]) == path);
            if (drive is null) { blockers.Add(Blocker("missingDrive", path, "drive_inventory does not list this drive.")); continue; }
            // The main (non-safety) telegram's ranges, each with exactly one tag of the selected PLC.
            var main = drive["driveObjects"]!.AsArray().OfType<JsonObject>().SelectMany(o => o["telegrams"]!.AsArray().OfType<JsonObject>())
                .Where(t => Text(t["type"]) != "SafetyTelegram").ToArray();
            JsonObject? Tag(bool input)
            {
                var tags = main.SelectMany(t => t["addresses"]!.AsArray().OfType<JsonObject>())
                    .Where(a => Text(a["direction"])?.StartsWith(input ? "In" : "Out", StringComparison.OrdinalIgnoreCase) == true)
                    .SelectMany(a => a["tags"]!.AsArray().OfType<JsonObject>()).Where(t => Text(t["plcPath"]) == plc).ToArray();
                if (tags.Length == 1) return tags[0];
                blockers.Add(Blocker(input ? "statusTag" : "controlTag", path, tags.Length + " tags of the selected PLC lie on the drive's " + (input ? "input" : "output") + " telegram range; exactly one is required."));
                return null;
            }
            var statusTag = Tag(true); var controlTag = Tag(false);
            if (statusTag is not null && Member(status) is { } s && !string.Equals(s.Type, Text(statusTag["dataType"]), StringComparison.OrdinalIgnoreCase))
                blockers.Add(Blocker("typeMismatch", path, "Status tag " + Text(statusTag["name"]) + " is " + Text(statusTag["dataType"]) + ", wrapper " + status + " is " + s.Type + "."));
            if (controlTag is not null && Member(control) is { } c && !string.Equals(c.Type, Text(controlTag["dataType"]), StringComparison.OrdinalIgnoreCase))
                blockers.Add(Blocker("typeMismatch", path, "Control tag " + Text(controlTag["name"]) + " is " + Text(controlTag["dataType"]) + ", wrapper " + control + " is " + c.Type + "."));
            var writers = new JsonArray();
            if (controlTag is not null)
            {
                var trace = Parse(await bridge.CallAsync("symbol_trace", new JsonObject { ["plcPath"] = plc, ["symbol"] = Text(controlTag["name"]), ["maxItems"] = 500, ["artifactDirectory"] = exportDirectory }, cancellationToken).ConfigureAwait(false));
                foreach (var writer in trace["writers"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    string actor = Text(writer["actor"]?["name"]) ?? Text(writer["actor"]?["blockPath"]) ?? "?";
                    writers.Add(actor);
                    if (!string.Equals(actor, caller, StringComparison.Ordinal))
                        blockers.Add(Blocker("duplicateControlWriter", path, actor + " already writes control tag " + Text(controlTag["name"]) + "; remove or replace that write explicitly first."));
                }
                if (trace["completeness"]?["scopeComplete"]?.GetValue<bool>() == false)
                    blockers.Add(Blocker("writerCoverage", path, "The writer trace of " + Text(controlTag["name"]) + " is incomplete."));
            }
            rows.Add(new JsonObject { ["hardwarePath"] = path, ["instanceName"] = instance, ["faultName"] = faultName,
                ["statusTag"] = statusTag?["name"]?.DeepClone(), ["controlTag"] = controlTag?["name"]?.DeepClone(), ["existingControlWriters"] = writers });
        }

        var result = new JsonObject { ["schemaVersion"] = "servo-call-plan.v1", ["plcPath"] = plc, ["callerName"] = caller, ["wrapperName"] = wrapper,
            ["parameters"] = map.DeepClone(), ["resetExpression"] = resetExpression, ["ss1Expression"] = ss1Expression, ["drives"] = rows,
            ["blockers"] = blockers, ["writeReady"] = blockers.Count == 0,
            ["intent"] = "Reset and SS1 expressions are applied as given and treated as confirmed design; the plan does not judge machine safety behavior." };
        if (blockers.Count == 0)
        {
            var source = Source(caller, wrapper, rows, members, status, control, reset, ss1, fault, resetExpression, ss1Expression);
            result["source"] = source;
            result["change"] = new JsonObject { ["kind"] = "source_upsert", ["plcPath"] = plc,
                ["payload"] = new JsonObject { ["language"] = "SCL", ["source"] = source, ["expectedBlockNames"] = new JsonArray(caller) } };
        }
        return result;
    }

    internal static string Source(string caller, string wrapper, JsonArray rows, Dictionary<string, (string Section, string Type)> members,
        string status, string control, string reset, string ss1, string fault, string resetExpression, string ss1Expression)
    {
        string Arrow(string name) => members[name].Section == "Output" ? " => " : " := ";
        var text = new StringBuilder();
        text.Append("FUNCTION_BLOCK \"").Append(caller).Append("\"\nVERSION : 0.1\n   VAR_OUTPUT\n");
        foreach (var r in rows.OfType<JsonObject>()) text.Append("      ").Append(Text(r["faultName"])).Append(" : Bool;\n");
        text.Append("   END_VAR\n   VAR\n");
        foreach (var r in rows.OfType<JsonObject>()) text.Append("      ").Append(Text(r["instanceName"])).Append(" : \"").Append(wrapper).Append("\";\n");
        text.Append("   END_VAR\n\nBEGIN\n");
        foreach (var r in rows.OfType<JsonObject>())
            text.Append("   #").Append(Text(r["instanceName"])).Append('(')
                .Append(status).Append(Arrow(status)).Append('"').Append(Text(r["statusTag"])).Append("\", ")
                .Append(reset).Append(" := ").Append(resetExpression).Append(", ")
                .Append(ss1).Append(" := ").Append(ss1Expression).Append(", ")
                .Append(control).Append(Arrow(control)).Append('"').Append(Text(r["controlTag"])).Append("\", ")
                .Append(fault).Append(" => #").Append(Text(r["faultName"])).Append(");\n");
        return text.Append("END_FUNCTION_BLOCK\n").ToString();
    }

    /// <summary>Interface members of a native block export by name: section (Input, Output, InOut, Static, ...) and data type.</summary>
    internal static Dictionary<string, (string Section, string Type)> Interface(string xml)
    {
        var result = new Dictionary<string, (string Section, string Type)>(StringComparer.Ordinal);
        foreach (var section in XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Section"))
            foreach (var member in section.Elements().Where(e => e.Name.LocalName == "Member"))
                result[(string?)member.Attribute("Name") ?? ""] = ((string?)section.Attribute("Name") ?? "", ((string?)member.Attribute("Datatype") ?? "").Trim('"'));
        return result;
    }

    // Confirmed expressions are inserted as SCL; this only keeps them to one expression without statements or comments.
    private static string Expression(JsonObject request, string key)
    {
        string text = Text(request[key]) ?? throw Invalid(key + " is required (use TRUE/FALSE for a constant).");
        if (text.Length is 0 or > 512 || text.IndexOfAny([';', '\n', '\r']) >= 0 || text.Contains("//", StringComparison.Ordinal) || text.Contains("(*", StringComparison.Ordinal))
            throw Invalid(key + " must be one SCL expression without ';', comments or line breaks.");
        return text;
    }

    private static string Identifier(JsonObject node, string key)
    {
        string text = Text(node[key]) ?? throw Invalid(key + " is required.");
        return IdentifierPattern().IsMatch(text) ? text : throw Invalid(key + " must be a plain SCL identifier.");
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    private static partial Regex IdentifierPattern();

    private static JsonObject Parse(System.Text.Json.JsonElement element) => JsonNode.Parse(element.GetRawText())!.AsObject();
    private static JsonObject Blocker(string kind, string? target, string message) => new() { ["kind"] = kind, ["target"] = target, ["message"] = message };
    private static BridgeException Invalid(string message) => new("InvalidRequest", message);
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;
}
