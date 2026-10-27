using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure combined PLC/UDT/HMI recipe planning. Native observations come from the library
    // adapter; this class never mutates and never authorizes the closed HMI preservation gate.
    internal static class PlcHmiIntegrationPlanner
    {
        internal const string InstallAction = "plc_hmi_component_plan", UpdateAction = "plc_hmi_update_plan";
        internal const string BreakError = "HmiBindingWouldBreak", IncompleteError = "HmiImpactIncomplete";
        internal static readonly string[] Profiles = { "unified-comfort", "unified-pc" };
        private static readonly HashSet<string> Scalars = new HashSet<string>(
            "Bool Byte Word DWord LWord SInt USInt Int UInt DInt UDInt LInt ULInt Real LReal Char WChar Time LTime Date Time_Of_Day LTime_Of_Day Date_And_Time DTL".Split(' '), StringComparer.OrdinalIgnoreCase);

        internal static bool IsRecipe(JObject payload) => payload?.Value<string>("action") is string action && (action == InstallAction || action == UpdateAction);

        internal static void ValidatePayload(JObject payload)
        {
            string action = payload?.Value<string>("action");
            if (action == InstallAction)
            {
                Fields(payload, "payload", "schemaVersion action planOnly source selection target integration hmi expectedCompilerOutcomes");
                PlcLibraryIntegrationPlanner.ValidatePayload(PlcPayload(payload));
                var hmi = Hmi(payload, "family profile hmiPath connection tagTable tags screenItems");
                Text(hmi, "connection", 256); Text(hmi, "tagTable", 256);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in Array(hmi, "tags", 1, 64))
                {
                    var tag = token as JObject; Fields(tag, "hmi tag", "name dataType source");
                    if (!names.Add(Name(tag, "name"))) Fail("InvalidIntegrationRecipe", "HMI tag names must be unique.");
                    if (!Scalars.Contains(Text(tag, "dataType", 64))) Fail("UnsupportedIntegrationType", "HMI tag mappings are limited to scalar PLC types.");
                    var source = Obj(tag, "source");
                    string kind = Text(source, "kind", 32);
                    if (kind == "plcTag") { Fields(source, "tag source", "kind tagPath"); Text(source, "tagPath"); }
                    else if (kind == "instanceMember")
                    {
                        Fields(source, "tag source", "kind members");
                        foreach (var member in Array(source, "members", 1, 16)) if (member.Type != JTokenType.String || !Regex.IsMatch((string)member, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z")) Fail("InvalidIntegrationRecipe", "instanceMember components must be simple native member names.");
                    }
                    else Fail("InvalidIntegrationRecipe", "Tag source kind must be plcTag or instanceMember.");
                }
                foreach (var token in hmi["screenItems"] == null ? new JArray() : Array(hmi, "screenItems", 0, 32))
                {
                    var item = token as JObject; Fields(item, "screen item", "screenPath itemName rectangle tag");
                    Text(item, "screenPath"); Name(item, "itemName");
                    if (!names.Contains(Text(item, "tag", 256))) Fail("InvalidIntegrationRecipe", "Screen items may only bind a tag declared by this recipe.");
                    var rectangle = Obj(item, "rectangle"); Fields(rectangle, "rectangle", "left top width height");
                    foreach (string key in new[] { "left", "top", "width", "height" }) if (rectangle[key]?.Type != JTokenType.Integer) Fail("InvalidIntegrationRecipe", "rectangle requires integer " + key + ".");
                }
            }
            else if (action == UpdateAction)
            {
                Fields(payload, "payload", "schemaVersion action planOnly source selection target hmi expectedCompilerOutcomes");
                Header(payload);
                var source = Obj(payload, "source"); Fields(source, "source", "libraryId revision"); Text(source, "libraryId", 64); Text(source, "revision", 64);
                var target = Obj(payload, "target"); Fields(target, "target", "projectName plcPath"); Text(target, "projectName", 128); Text(target, "plcPath");
                var selection = Array(payload, "selection", 1, 1)[0] as JObject; Fields(selection, "selection", "typeGuid versionGuid"); Guid(selection, "typeGuid"); Guid(selection, "versionGuid");
                Hmi(payload, "family profile");
            }
            else Fail("InvalidIntegrationRecipe", "Combined recipes require action plc_hmi_component_plan or plc_hmi_update_plan.");
            var expected = Obj(payload, "expectedCompilerOutcomes"); Fields(expected, "expectedCompilerOutcomes", "plc hmi");
            foreach (string side in new[] { "plc", "hmi" })
            {
                var outcome = Obj(expected, side); Fields(outcome, side + " compiler outcome", "errors maxWarnings");
                if (outcome["errors"]?.Type != JTokenType.Integer || outcome.Value<long>("errors") != 0) Fail("InvalidIntegrationRecipe", "Expected compiler errors must be exactly 0; an accepted error is never a success outcome.");
                if (outcome["maxWarnings"]?.Type != JTokenType.Integer || outcome.Value<long>("maxWarnings") < 0 || outcome.Value<long>("maxWarnings") > 1000) Fail("InvalidIntegrationRecipe", "maxWarnings must be an integer 0..1000.");
            }
        }

        // The PLC part is the unchanged existing recipe; it keeps every PLC planner guard.
        internal static JObject PlcPayload(JObject payload)
        {
            var plc = (JObject)payload.DeepClone();
            plc.Remove("hmi"); plc.Remove("expectedCompilerOutcomes"); plc["action"] = "plc_component_plan";
            return plc;
        }

        // Flattens exported Openness interface members. A quoted UDT member expands from the members
        // the export inlines, else one level from the supplied exports, where nested UDTs stay opaque.
        // Arrays are never expanded.
        internal static JArray Members(XDocument export, IDictionary<string, XDocument> types = null)
        {
            var rows = new JArray();
            var sections = export.Descendants().Where(e => e.Name.LocalName == "Sections").Take(1).SelectMany(s => s.Elements().Where(e => e.Name.LocalName == "Section"));
            foreach (var section in sections.Where(s => (string)s.Attribute("Name") != "Temp" && (string)s.Attribute("Name") != "Constant"))
                Add(rows, section, new string[0], types, 0);
            return rows;
        }

        private static void Add(JArray rows, XElement parent, string[] prefix, IDictionary<string, XDocument> types, int depth)
        {
            if (depth > 8 || rows.Count > 10000) Fail(IncompleteError, "Interface member flattening exceeded its depth or size bound.");
            foreach (var member in parent.Elements().Where(e => e.Name.LocalName == "Member"))
            {
                string name = (string)member.Attribute("Name"), type = (string)member.Attribute("Datatype") ?? "";
                var path = prefix.Concat(new[] { name }).ToArray();
                bool scalar = Scalars.Contains(type);
                rows.Add(new JObject { ["path"] = new JArray(path), ["dataType"] = type, ["scalar"] = scalar });
                var inline = member.Elements().Where(e => e.Name.LocalName == "Sections").SelectMany(e => e.Elements().Where(x => x.Name.LocalName == "Section")).ToList();
                if (member.Elements().Any(e => e.Name.LocalName == "Member")) Add(rows, member, path, types, depth + 1);
                // Native FB/DB exports inline the UDT members they were compiled against.
                else if (inline.Count > 0) foreach (var section in inline) Add(rows, section, path, null, depth + 1);
                else if (type.Length > 2 && type[0] == '"' && type[type.Length - 1] == '"' && types != null && types.TryGetValue(type.Substring(1, type.Length - 2), out var udt))
                    foreach (var section in udt.Descendants().Where(e => e.Name.LocalName == "Section")) Add(rows, section, path, null, depth + 1);
            }
        }

        // Splits a member path at the first member declared with the selected UDT type.
        internal static string[][] SplitAtType(JArray members, string[] components, string typeName)
        {
            string quoted = "\"" + typeName + "\"";
            for (int length = 1; length <= components.Length; length++)
            {
                var prefix = components.Take(length).ToArray();
                var row = Find(members, prefix);
                if (row == null) return null;
                if (row.Value<string>("dataType") == quoted) return new[] { prefix, components.Skip(length).ToArray() };
            }
            return null;
        }

        internal static JObject BuildInstall(JObject payload, JObject plcPlan, JObject observed)
        {
            ValidatePayload(payload);
            var hmi = (JObject)payload["hmi"]; var blockers = new JArray(plcPlan["blockers"].Select(b => b.DeepClone()));
            if (observed.Value<string>("family") != "unified" || observed.Value<string>("profile") != hmi.Value<string>("profile"))
                Fail("UnsupportedHmiProfile", "The observed HMI family/profile differs from the selected Unified target profile.");
            string plcPath = payload["target"].Value<string>("plcPath");
            if (observed["connection"]?.Value<string>("plcPath") != plcPath)
                Fail("HmiConnectionPlcMismatch", "The selected integrated connection's proven native partner is not the recipe's target PLC.");
            if (observed["tagTable"]?.Value<bool?>("exists") != true) Fail("ObjectNotFound", "The exact HMI tag table does not exist.");
            var conflicts = observed["existingTagNames"] as JArray ?? new JArray();
            if (conflicts.Count != 0) Fail("HmiNameConflict", "Requested HMI tag names already exist: " + string.Join(", ", conflicts));
            var instanceMembers = observed["instanceMembers"] as JArray ?? new JArray();
            string db = payload["integration"]["instanceDb"]?.Value<string>("name");
            var tags = new JArray(); var steps = new JArray(plcPlan["steps"].Select(s => s.DeepClone()));
            foreach (JObject tag in (JArray)hmi["tags"])
            {
                string name = tag.Value<string>("name"), dataType = tag.Value<string>("dataType"); var source = (JObject)tag["source"];
                var row = new JObject { ["name"] = name, ["dataType"] = dataType, ["source"] = source.DeepClone() };
                if (source.Value<string>("kind") == "plcTag")
                {
                    var plcTag = observed["plcTags"]?[source.Value<string>("tagPath")] as JObject;
                    if (plcTag == null) Fail("IntegrationTagMissing", "Mapped PLC tag is not a unique root tag of the target PLC: " + source.Value<string>("tagPath"));
                    RequireType(name, dataType, plcTag.Value<string>("dataType"));
                    row["plcSymbol"] = plcTag.Value<string>("name");
                    steps.Add(new JObject { ["order"] = steps.Count + 1, ["kind"] = "hmi_engineering", ["operation"] = "external_tag_create", ["requiresFreshPreparation"] = true, ["requiresFreshIdentity"] = true,
                        ["request"] = new JObject { ["hmiPath"] = hmi["hmiPath"].DeepClone(), ["tagTable"] = hmi["tagTable"].DeepClone(), ["connection"] = hmi["connection"].DeepClone(), ["name"] = name, ["dataType"] = dataType, ["plcTagPath"] = source["tagPath"].DeepClone() },
                        ["mustWaitFor"] = "PLC installation readback; resolve exact hmi_inspect identities for the tag table, connection and PLC tag." });
                }
                else
                {
                    var members = source["members"].Select(m => (string)m).ToArray();
                    if (db == null) Fail("UnsupportedIntegrationType", "instanceMember HMI tags need a global instanceDb; a multi-instance member has no qualified HMI symbol.");
                    var member = Find(instanceMembers, members);
                    if (member == null) Fail("IntegrationMappingMismatch", "Instance member does not exist in the pinned FB/UDT interface: " + string.Join(".", members));
                    if (member.Value<bool>("scalar") != true) Fail("UnsupportedIntegrationType", "Only scalar instance members can be mapped: " + string.Join(".", members));
                    RequireType(name, dataType, member.Value<string>("dataType"));
                    row["plcSymbol"] = db + "." + string.Join(".", members);
                    blockers.Add(new JObject { ["code"] = "HmiMemberMappingUnqualified", ["tag"] = name, ["message"] = "external_tag_create maps root PLC tags only; DB/UDT member tags have no qualified creation operation." });
                }
                tags.Add(row);
            }
            var screens = new JArray();
            foreach (JObject item in hmi["screenItems"] as JArray ?? new JArray())
            {
                var screen = (observed["screens"] as JArray ?? new JArray()).OfType<JObject>().SingleOrDefault(s => s.Value<string>("screenPath") == item.Value<string>("screenPath"));
                if (screen == null || screen.Value<bool?>("exists") != true) Fail("ObjectNotFound", "Screen does not exist: " + item.Value<string>("screenPath"));
                if ((screen["itemNames"] as JArray ?? new JArray()).Any(n => string.Equals((string)n, item.Value<string>("itemName"), StringComparison.OrdinalIgnoreCase)))
                    Fail("HmiNameConflict", "Screen item already exists: " + item.Value<string>("itemName"));
                var r = (JObject)item["rectangle"];
                try { TiaMcp.Model.HmiRecipeRules.RequirePlacement(r.Value<long>("left"), r.Value<long>("top"), r.Value<long>("width"), r.Value<long>("height"), screen.Value<long>("width"), screen.Value<long>("height")); }
                catch (ArgumentException error) { Fail("HmiPlacementInvalid", error.Message); }
                screens.Add(item.DeepClone());
                blockers.Add(new JObject { ["code"] = "HmiScreenItemCreationUnavailable", ["item"] = item["itemName"].DeepClone(), ["message"] = "No qualified operation creates a bound Unified screen item." });
            }
            blockers.Add(new JObject { ["code"] = "HmiPreservationGateClosed", ["message"] = "Unified production apply stays behind the closed preservation gate; HMI steps can only be prepared." });
            return Plan(payload, blockers, steps, new JObject { ["plc"] = plcPlan, ["hmi"] = new JObject { ["target"] = observed.DeepClone(), ["tags"] = tags, ["screenItems"] = screens } });
        }

        internal static JObject BuildUpdate(JObject payload, JObject observed)
        {
            ValidatePayload(payload);
            if (observed.Value<bool?>("complete") != true) Fail(IncompleteError, "Native HMI binding observation is incomplete.");
            var before = (JArray)observed["before"]; var after = (JArray)observed["after"];
            var changes = new JArray();
            foreach (JObject row in before)
            {
                var other = Find(after, Path(row));
                if (other == null) changes.Add(Change(row, null, "removed"));
                else if (other.Value<string>("dataType") != row.Value<string>("dataType")) changes.Add(Change(row, other, "typeChanged"));
            }
            foreach (JObject row in after) if (Find(before, Path(row)) == null) changes.Add(Change(null, row, "added"));
            var rows = new JArray(); var gaps = new JArray(observed["unresolved"]?.Select(u => u.DeepClone()) ?? Enumerable.Empty<JToken>());
            string profile = payload["hmi"].Value<string>("profile");
            foreach (JObject binding in (JArray)observed["bindings"])
            {
                var remainder = binding["remainder"].Select(c => (string)c).ToArray();
                var row = (JObject)binding.DeepClone(); string status;
                if (binding.Value<string>("profile") != profile) { gaps.Add(new JObject { ["tagPath"] = binding["tagPath"].DeepClone(), ["reason"] = "Bound HMI has profile " + binding.Value<string>("profile") + ", outside the selected profile." }); continue; }
                if (remainder.Length == 0)
                    status = JToken.DeepEquals(before, after) ? "unchanged" : "structureChanged";
                else
                {
                    var old = Find(before, remainder); var updated = Find(after, remainder);
                    if (old == null) { gaps.Add(new JObject { ["tagPath"] = binding["tagPath"].DeepClone(), ["reason"] = "Binding does not resolve in the installed type version." }); continue; }
                    status = updated == null ? "memberRemoved" : updated.Value<string>("dataType") != old.Value<string>("dataType") ? "typeChanged" : "unchanged";
                    row["beforeDataType"] = old["dataType"].DeepClone(); row["afterDataType"] = updated?["dataType"].DeepClone();
                    if (status == "unchanged" && !string.Equals(binding.Value<string>("dataType"), old.Value<string>("dataType"), StringComparison.OrdinalIgnoreCase))
                    { gaps.Add(new JObject { ["tagPath"] = binding["tagPath"].DeepClone(), ["reason"] = "HMI tag type differs from the installed declaration before the update." }); continue; }
                }
                row["status"] = status; row["breaking"] = status != "unchanged";
                if (status != "unchanged") row["requiredAction"] = "Rebind or retype this HMI tag and its consumers in a separate prepared HMI change; automatic rewiring is not performed.";
                rows.Add(row);
            }
            var report = new JObject { ["type"] = observed["type"].DeepClone(), ["memberChanges"] = changes, ["bindings"] = rows, ["unresolved"] = gaps,
                ["breakingCount"] = rows.Count(r => r.Value<bool>("breaking")), ["affectedConsumerCount"] = rows.Where(r => r.Value<bool>("breaking")).Sum(r => (r["consumers"] as JArray)?.Count ?? 0),
                ["hmiTargets"] = observed["hmiTargets"]?.DeepClone(), ["nativeMutationDispatched"] = false, ["automaticRewiringAllowed"] = false };
            if (gaps.Count != 0) Fail(IncompleteError, "The impact of this update on HMI bindings cannot be established completely; no update is planned.", report);
            if (report.Value<int>("breakingCount") != 0) Fail(BreakError, "The selected type version would break " + report.Value<int>("breakingCount") + " HMI binding(s); see the impact report.", report);
            var update = new JObject { ["kind"] = "library_update_project", ["payload"] = new JObject { ["schemaVersion"] = 1, ["source"] = payload["source"].DeepClone(), ["selection"] = payload["selection"].DeepClone(), ["target"] = payload["target"].DeepClone(), ["updateScope"] = "entireSelectedPlc" } };
            var steps = new JArray(new JObject { ["order"] = 1, ["kind"] = "library_update_project", ["prepareArguments"] = update, ["requiresFreshPreparation"] = true, ["mustWaitFor"] = "Fresh HMI impact preview immediately before update preparation; binding changes after a type update require a new preview." });
            return Plan(payload, new JArray(), steps, new JObject { ["impact"] = report });
        }

        private static JObject Plan(JObject payload, JArray blockers, JArray steps, JObject body)
        {
            var plan = new JObject { ["schemaVersion"] = 1, ["action"] = payload["action"].DeepClone(), ["status"] = "planned_only", ["applySupported"] = false, ["applyError"] = PlcLibraryIntegrationPlanner.ApplyError,
                ["nativeMutationDispatched"] = false, ["qualification"] = "offline_planner_only_native_qualification_required",
                ["expectedCompilerOutcomes"] = payload["expectedCompilerOutcomes"].DeepClone(), ["blockers"] = blockers, ["steps"] = steps,
                ["qualificationGates"] = new JArray("Each step freshly prepared and reviewed; steps are not atomic", "PLC and HMI compile results within expectedCompilerOutcomes", "Unified preservation gate passed for every HMI step", "Native readback of HMI tag connection/PlcTag after PLC readback", "Save/fresh reopen and first-checkpoint recovery", "PLC communication on the matching HMI runtime") };
            foreach (var property in body.Properties()) plan[property.Name] = property.Value;
            return plan;
        }

        private static JObject Change(JObject before, JObject after, string kind) => new JObject { ["path"] = (before ?? after)["path"].DeepClone(), ["change"] = kind, ["beforeDataType"] = before?["dataType"].DeepClone(), ["afterDataType"] = after?["dataType"].DeepClone() };
        private static string[] Path(JObject row) => row["path"].Select(c => (string)c).ToArray();
        private static JObject Find(JArray members, string[] path) => members.OfType<JObject>().FirstOrDefault(m => Path(m).SequenceEqual(path, StringComparer.Ordinal));
        private static void RequireType(string tag, string requested, string observed)
        {
            if (!string.Equals(requested, observed, StringComparison.OrdinalIgnoreCase)) Fail("OperandTypeMismatch", "HMI tag " + tag + " type " + requested + " differs from the exact PLC declaration type " + observed + "; conversions are not inferred.");
        }
        private static JObject Hmi(JObject payload, string fields)
        {
            var hmi = Obj(payload, "hmi"); Fields(hmi, "hmi", fields);
            if (Text(hmi, "family", 16) != "unified") Fail("HmiFamilyNotQualified", "Combined recipes are limited to WinCC Unified; Classic binding integration has no preserving adapter.");
            if (!Profiles.Contains(Text(hmi, "profile", 32))) Fail("UnsupportedHmiProfile", "profile must be unified-comfort or unified-pc.");
            if (fields.Contains("hmiPath")) Text(hmi, "hmiPath");
            return hmi;
        }
        private static void Header(JObject payload)
        {
            if (payload["schemaVersion"]?.Type != JTokenType.Integer || payload.Value<int>("schemaVersion") != 1) Fail("InvalidIntegrationRecipe", "schemaVersion must be integer 1.");
            if (payload["planOnly"]?.Type != JTokenType.Boolean || payload.Value<bool>("planOnly") != true) Fail("InvalidIntegrationRecipe", "Combined recipes require planOnly=true. Combined application is unavailable.");
        }
        private static string Name(JObject value, string key) { string name = Text(value, key, 128); try { TiaMcp.Model.HmiRecipeRules.RequireName(name, key); } catch (ArgumentException error) { Fail("InvalidIntegrationRecipe", error.Message); } return name; }
        private static void Guid(JObject value, string key) { if (!System.Guid.TryParseExact(Text(value, key, 36), "D", out var id) || id == System.Guid.Empty) Fail("InvalidIntegrationRecipe", key + " must be a nonempty canonical GUID."); }
        private static JObject Obj(JObject value, string key) { if (value?[key] is JObject result) return result; Fail("InvalidIntegrationRecipe", key + " must be an object."); return null; }
        private static JArray Array(JObject value, string key, int min, int max) { if (value?[key] is JArray a && a.Count >= min && a.Count <= max) return a; Fail("InvalidIntegrationRecipe", key + " must be a bounded array."); return null; }
        private static string Text(JObject value, string key, int max = 4096) { var t = value?[key]; if (t?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t) && ((string)t).Length <= max && !((string)t).Any(char.IsControl)) return (string)t; Fail("InvalidIntegrationRecipe", key + " must be a nonempty bounded string."); return null; }
        private static void Fields(JObject value, string context, string fields) { var allowed = new HashSet<string>(fields.Split(' '), StringComparer.Ordinal); if (value == null || value.Properties().Any(p => !allowed.Contains(p.Name))) Fail("InvalidIntegrationRecipe", context + " must be an object with only documented fields."); }
        private static void Fail(string code, string message, JObject details = null) => throw new PlcIntegrationPlanException(code, message, details);
    }
}
