using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    internal static class HmiEngineeringRules
    {
        internal static readonly string[] Operations = { "scalar_property_set", "existing_binding_set", "tag_create", "item_properties_set", "item_place", "explicit_binding_set", "external_tag_create", "classic_xml_import", "connection_create", "object_create", "object_properties_set",
            "screen_create", "item_create", "dynamization_set", "event_handler_set" };
        private static readonly string[] ScreenOperations = { "screen_create", "item_create", "dynamization_set", "event_handler_set" };
        // Each documents typed Left/Top (Int32) and Width/Height (UInt32) setters in V21.
        internal static readonly string[] ItemTypes = { "HmiRectangle", "HmiText", "HmiButton", "HmiIOField", "HmiBar", "HmiGauge", "HmiSlider", "HmiToggleSwitch" };
        internal static readonly string[] DynamizedProperties = { "ProcessValue", "Visible", "Enabled" };
        internal const int MaxScriptLength = 16384;
        // Documented typed V21 setters per Unified composition. Tag, alarm-class and screen names are
        // resolved natively at prepare; Part.Property addresses a documented part's setter.
        internal static readonly Dictionary<string, string[]> FamilyProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["DiscreteAlarms"] = new[] { "RaisedStateTag", "RaisedStateTagBitNumber", "TriggerMode", "AlarmClass", "Priority", "Origin", "Area" },
            ["AnalogAlarms"] = new[] { "RaisedStateTag", "Condition", "AlarmClass", "Priority", "Origin", "Area" },
            ["DataLogs"] = new[] { "Settings.LogMaxSize", "Settings.StorageFolder" },
            ["AlarmLogs"] = new[] { "Settings.LogMaxSize", "Settings.StorageFolder" },
            ["RuntimeSettings"] = new[] { "StartScreen", "ScreenResolution" }
        };
        private static readonly Dictionary<string, long> IntegerProperties = new Dictionary<string, long>(StringComparer.Ordinal)
            { ["RaisedStateTagBitNumber"] = 63, ["Priority"] = byte.MaxValue, ["Settings.LogMaxSize"] = uint.MaxValue };
        private static readonly string[] PrimitiveTypes = { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal" };

        internal static void RequireRequest(JObject payload)
        {
            if (payload == null || payload.Value<int?>("schemaVersion") != 1) throw new ArgumentException("HMI engineering requires payload.schemaVersion=1.");
            var permitted = new HashSet<string>(new[] { "schemaVersion", "operation", "target", "expectedContentHash", "properties", "name", "dataType", "placement", "bindings", "connection", "plcTag", "xml", "composition", "itemType", "eventType", "script" }, StringComparer.Ordinal);
            foreach (var property in payload.Properties())
                if (!permitted.Contains(property.Name)) throw new ArgumentException("Unsupported HMI engineering field: " + property.Name);
            string operation = Text(payload, "operation");
            if (!Operations.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Unknown HMI engineering operation.");
            if (operation != "classic_xml_import" && payload["xml"] != null) throw new ArgumentException("xml is not applicable to this operation.");
            if (operation != "object_create" && payload["composition"] != null) throw new ArgumentException("composition is not applicable to this operation.");
            if (operation != "item_create" && payload["itemType"] != null) throw new ArgumentException("itemType is not applicable to this operation.");
            if (operation != "event_handler_set" && (payload["eventType"] != null || payload["script"] != null)) throw new ArgumentException("eventType and script are not applicable to this operation.");
            var target = payload["target"] as JObject ?? throw new ArgumentException("An exact HMI target identity is required.");
            HmiRecipeRules.RequireFamily(Text(target, "family"));
            if (ScreenOperations.Contains(operation) && Text(target, "family") != "unified")
                throw new ArgumentException(operation + " uses documented Unified screen compositions; Classic screens use classic_xml_import.");
            foreach (string key in new[] { "projectCopyId", "sessionGeneration", "hmiPath", "objectPath" }) Text(target, key, 4096);
            string hash = Text(payload, "expectedContentHash");
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("expectedContentHash must be the full native-content SHA-256; metadataRevision is insufficient.");
            if (operation == "tag_create")
            {
                HmiRecipeRules.RequireName(Text(payload, "name"), "name");
                if (!PrimitiveTypes.Contains(Text(payload, "dataType"), StringComparer.Ordinal))
                    throw new ArgumentException("Only primitive internal HMI tags have a creation body; external PLC/UDT mappings require a separately qualified operation.");
                RejectFields(payload, "properties", "placement", "bindings", "connection", "plcTag");
            }
            else if (operation == "external_tag_create")
            {
                if (Text(target, "family") != "unified")
                    throw new ArgumentException("Only Unified documents symbolic PlcTag access on integrated connections; Classic external tags need a separately qualified preserving XML adapter.");
                HmiRecipeRules.RequireName(Text(payload, "name"), "name");
                if (!PrimitiveTypes.Contains(Text(payload, "dataType"), StringComparer.Ordinal))
                    throw new ArgumentException("External tag creation is limited to primitive scalar PLC declarations; arrays, strings and whole UDT/struct values are not mapped.");
                var connection = payload["connection"] as JObject ?? throw new ArgumentException("An exact HMI connection identity is required, never only a name.");
                RequireSameHmi(target, connection, "connection");
                if (Text(connection, "kind") != "hmiConnection") throw new ArgumentException("connection must identify an hmiConnection.");
                var plcTag = payload["plcTag"] as JObject ?? throw new ArgumentException("An exact PLC tag identity is required, never only a symbol name.");
                if (plcTag.Properties().Any(p => !new[] { "projectPath", "plcPath", "scopePath", "objectPath", "kind", "name", "namespace", "memberPath" }.Contains(p.Name, StringComparer.Ordinal)))
                    throw new ArgumentException("plcTag contains unsupported fields; caller-provided evidence cannot authorize a PLC binding.");
                string kind = Text(plcTag, "kind");
                if (kind == "block")
                {
                    // Same unquoted dotted spelling as a root tag; indexed or quoted components have no proven derivation.
                    var members = plcTag["memberPath"] as JArray ?? throw new ArgumentException("A global DB identity requires the exact memberPath of one scalar member.");
                    if (members.Count < 1 || members.Count > 32 || members.Any(m => m.Type != JTokenType.String || !UnquotedComponent((string)m)))
                        throw new ArgumentException("memberPath must list 1 to 32 unquoted member names; array elements, quoted names and indexed paths are not mapped.");
                }
                else if (kind != "tag" || plcTag["memberPath"] != null) throw new ArgumentException("plcTag must identify a root PLC tag-table tag, or a global DB with an exact memberPath.");
                string plcPath = Text(plcTag, "plcPath", 4096);
                Text(plcTag, "objectPath", 4096);
                if (plcTag["scopePath"] != null && Text(plcTag, "scopePath", 4096) != plcPath)
                    throw new ArgumentException("Only root PLC tag tables and root global DBs have a documented symbolic HMI spelling; software-unit declarations are not mapped.");
                RejectFields(payload, "properties", "placement", "bindings");
            }
            else if (operation == "connection_create")
            {
                if (Text(target, "family") != "unified") throw new ArgumentException("connection_create uses the documented Unified HmiConnectionComposition.Create; Classic connections use classic_xml_import.");
                HmiRecipeRules.RequireName(Text(payload, "name"), "name");
                RejectFields(payload, "properties", "dataType", "placement", "bindings", "connection", "plcTag");
            }
            else if (operation == "object_create" || operation == "object_properties_set")
            {
                if (Text(target, "family") != "unified") throw new ArgumentException(operation + " uses documented Unified compositions; Classic objects use classic_xml_import.");
                string[] allowed;
                if (operation == "object_create")
                {
                    HmiRecipeRules.RequireName(Text(payload, "name"), "name");
                    string composition = Text(payload, "composition");
                    if (composition == "RuntimeSettings" || !FamilyProperties.TryGetValue(composition, out allowed))
                        throw new ArgumentException("composition must document Create(name): DiscreteAlarms, AnalogAlarms, DataLogs or AlarmLogs.");
                }
                else
                {
                    // The exact native family is checked against its own allowlist at prepare.
                    allowed = FamilyProperties.Values.SelectMany(v => v).ToArray();
                    if (payload["properties"] == null) throw new ArgumentException("properties is required.");
                    RejectFields(payload, "name");
                }
                var properties = payload["properties"];
                if (properties != null && (!(properties is JObject values) || values.Count < 1 || values.Count > 8)) throw new ArgumentException("properties must be an object with 1 to 8 allowlisted entries.");
                foreach (var p in (properties as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    if (!allowed.Contains(p.Name, StringComparer.Ordinal)) throw new ArgumentException("Property is outside the family-specific allowlist: " + p.Name);
                    if (IntegerProperties.TryGetValue(p.Name, out long max))
                    { if (p.Value.Type != JTokenType.Integer || !(((JValue)p.Value).Value is long value) || value < 0 || value > max) throw new ArgumentException(p.Name + " must be an integer from 0 through " + max + "."); }
                    else Text((JObject)p.Parent, p.Name);
                }
                RejectFields(payload, "dataType", "placement", "bindings", "connection", "plcTag");
            }
            else if (operation == "screen_create")
            {
                HmiRecipeRules.RequireName(Text(payload, "name"), "name");
                // The size must equal an existing screen's size of the same HMI; that native set is the allowlist.
                var size = payload["properties"] as JObject;
                if (size == null || size.Count != 2 || new[] { "Width", "Height" }.Any(k => size[k]?.Type != JTokenType.Integer || (long)size[k] < 1 || (long)size[k] > uint.MaxValue))
                    throw new ArgumentException("screen_create requires properties with exactly Width and Height (positive integers) copied from an existing screen of the same HMI.");
                RejectFields(payload, "dataType", "placement", "bindings", "connection", "plcTag");
            }
            else if (operation == "event_handler_set")
            {
                string eventType = Text(payload, "eventType", 64);
                if (eventType == "None" || !UnquotedComponent(eventType)) throw new ArgumentException("eventType must name one documented native event type, such as Tapped or Loaded.");
                // Untrusted script text: bounded and stored verbatim, never executed, evaluated or syntax-checked.
                if (payload["script"]?.Type != JTokenType.String) throw new ArgumentException("script must be an explicit string.");
                string script = (string)payload["script"];
                if (script.Trim().Length == 0 || script.Length > MaxScriptLength || script.Any(c => char.IsControl(c) && c != '\t' && c != '\r' && c != '\n'))
                    throw new ArgumentException("script must be 1 to " + MaxScriptLength + " characters without control characters other than tab, CR and LF.");
                RejectFields(payload, "name", "properties", "dataType", "placement", "bindings", "connection", "plcTag");
            }
            else if (operation == "scalar_property_set" || operation == "item_properties_set")
            {
                var properties = payload["properties"] as JObject ?? throw new ArgumentException("properties is required.");
                if (properties.Count < 1 || properties.Count > (operation == "scalar_property_set" ? 1 : 3)) throw new ArgumentException("The operation's property count is outside its allowlist.");
                foreach (var p in properties.Properties())
                {
                    if (target.Value<string>("family") == "classic")
                    {
                        if (operation != "scalar_property_set" || p.Name != "BackColor" || p.Value.Type != JTokenType.String)
                            throw new ArgumentException("Classic permits only a screen BackColor scalar candidate with preserving native XML readback.");
                        HmiPreservationRules.ClassicColor((string)p.Value);
                    }
                    else if (p.Name == "Enabled" || p.Name == "Visible")
                    { if (p.Value.Type != JTokenType.Boolean) throw new ArgumentException(p.Name + " must be a Boolean."); }
                    else if (p.Name == "TabIndex")
                    { if (p.Value.Type != JTokenType.Integer || (long)p.Value < 0 || (long)p.Value > uint.MaxValue) throw new ArgumentException("TabIndex must be a bounded nonnegative integer."); }
                    else throw new ArgumentException("Property is outside the family-specific allowlist: " + p.Name);
                }
                RejectFields(payload, "name", "dataType", "placement", "bindings", "connection", "plcTag");
            }
            else if (operation == "classic_xml_import")
            {
                if (Text(target, "family") != "classic") throw new ArgumentException("classic_xml_import is the Classic preserving XML route; Unified has no documented screen export to verify it.");
                if (payload["xml"]?.Type != JTokenType.String) throw new ArgumentException("xml must be the complete supplied or edited native export document.");
                try { HmiPreservationRules.ClassicImportRoot(HmiPreservationRules.ReadXml((string)payload["xml"])); }
                catch (System.Xml.XmlException error) { throw new ArgumentException("xml is not a well-formed native export: " + error.Message); }
                RejectFields(payload, "properties", "name", "dataType", "placement", "bindings", "connection", "plcTag");
            }
            else if (operation == "item_place" || operation == "item_create")
            {
                var placement = payload["placement"] as JObject ?? throw new ArgumentException("An explicit placement rectangle is required.");
                if (placement.Count != 4) throw new ArgumentException("Placement requires exactly left, top, width and height.");
                foreach (string key in new[] { "left", "top", "width", "height" })
                    if (placement[key]?.Type != JTokenType.Integer) throw new ArgumentException("Placement requires integer " + key + ".");
                if (operation == "item_create")
                {
                    HmiRecipeRules.RequireName(Text(payload, "name"), "name");
                    if (!ItemTypes.Contains(Text(payload, "itemType"), StringComparer.Ordinal)) throw new ArgumentException("itemType must be one of: " + string.Join(", ", ItemTypes) + ".");
                }
                else RejectFields(payload, "name");
                RejectFields(payload, "dataType", "properties", "bindings", "connection", "plcTag");
            }
            else
            {
                var bindings = payload["bindings"] as JArray ?? throw new ArgumentException("An explicit binding array is required.");
                if (bindings.Count < 1 || bindings.Count > (operation == "dynamization_set" ? 1 : 200)) throw new ArgumentException("At most 200 explicit bindings, or one tag dynamization, are accepted.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in bindings)
                {
                    var binding = token as JObject ?? throw new ArgumentException("A binding must be an object.");
                    if (binding.Properties().Any(p => !new[] { "property", "tagIdentity", "expectedValue", "dataType" }.Contains(p.Name, StringComparer.Ordinal)))
                        throw new ArgumentException("A binding contains unsupported fields; caller-provided evidence cannot authorize rewiring.");
                    if (!seen.Add(Text(binding, "property"))) throw new ArgumentException("Duplicate binding property.");
                    Text(binding, "dataType");
                    var tag = binding["tagIdentity"] as JObject ?? throw new ArgumentException("Binding requires an exact HMI tag identity, never only a name.");
                    RequireSameHmi(target, tag, "binding");
                    if (operation == "existing_binding_set" && binding["expectedValue"] == null) throw new ArgumentException("Changing an existing binding requires its observed expectedValue.");
                    if (operation == "dynamization_set")
                    {
                        // Create-only: an existing dynamization is never replaced, so no expectedValue applies.
                        string property = Text(binding, "property");
                        if (!DynamizedProperties.Contains(property, StringComparer.Ordinal) || binding["expectedValue"] != null)
                            throw new ArgumentException("dynamization_set creates one tag dynamization of ProcessValue, Visible or Enabled; script dynamizations and replacing existing ones are not offered.");
                        if (property != "ProcessValue" && Text(binding, "dataType") != "Bool") throw new ArgumentException(property + " can only be dynamized by a Bool tag.");
                    }
                }
                RejectFields(payload, "name", "dataType", "properties", "placement", "connection", "plcTag");
            }
        }

        internal static JObject Gate(string family, string profile, string operation)
        {
            HmiRecipeRules.RequireFamily(family);
            if (!Operations.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Unknown HMI operation.");
            bool classicScalar = family == "classic" && operation == "scalar_property_set";
            bool classicXml = operation == "classic_xml_import", body = family == "unified" ? !classicXml : classicScalar || classicXml;
            string reason = classicXml
                ? family == "classic"
                    ? "Classic per-object export and owning-composition Import(Override) are documented. Exact supplied-document readback, whole-HMI unrelated-content preservation, import dependency closure, compile, save/reopen and recovered-project equality have not passed for this exact device/runtime profile."
                    : "Document import is the Classic route; installed V21 Unified exposes no screen export to verify a preserving import."
                : family == "unified" && operation == "external_tag_create"
                ? "The Unified external-tag body proves the integrated connection's PLC owner through native CommunicationManagement endpoints and the exact root PLC tag/type before writing. Tag-table export dependency closure, unrelated-resource preservation, compiler, save/reopen, archive-recovery and PLC communication gates have not passed for this exact device/runtime profile."
                : family != "unified" && (operation == "connection_create" || operation == "object_create" || operation == "object_properties_set" || ScreenOperations.Contains(operation))
                ? "Unified-only operation: the inspected Classic V21 surface has no typed connection, alarm, log, runtime-settings, screen, screen-item, dynamization or event-handler creation/setter route; Classic objects use the preserving classic_xml_import."
                : ScreenOperations.Contains(operation)
                ? "HmiScreenComposition.Create(name), HmiScreenItemBaseComposition.Create<T>(name), DynamizationBaseComposition.Create<TagDynamization>(propertyName) and the object's EventHandlers Create(eventType) are documented. Bodies create one new screen (size copied from an existing screen), allowlisted item (rectangle within the screen), tag dynamization or event handler (script stored verbatim, never executed) with exact typed readback, and may add only that object's own typed-readback subtree. Created-subtree readback, unrelated-content preservation, compile, save/reopen, archive-recovery and visual gates have not passed for this exact device/runtime profile."
                : operation == "connection_create"
                ? "HmiConnectionComposition.Create(name) is documented, but Partner/Station/Node are read-only. The body creates only the software connection of one valid native CommunicationManagement endpoint pair of that name with exactly one partner PLC, then verifies the derived Partner and a driver. That derivation, unrelated-content preservation, compile, save/reopen and archive-recovery gates have not passed for this exact device/runtime profile."
                : operation == "object_create" || operation == "object_properties_set"
                ? "Discrete/analog alarm and data/alarm log compositions document Create(name); alarms, logs and runtime settings document the allowlisted typed setters, with name references resolved natively and precise typed readback. Created-subtree readback, unrelated-content preservation, compile, save/reopen and archive-recovery gates have not passed for this exact device/runtime profile."
                : family == "unified" && operation == "tag_create"
                ? "Unified tag export is documented, but the native export/readback dependency closure and unrelated-resource preservation, compiler, save/reopen and archive-recovery gates have not passed for this exact device/runtime profile."
                : family == "unified"
                    ? "Installed V21 Unified APIs expose typed properties/placement/bindings, but no screen Export method was found; a complete readback of events, dynamics, translations, resources and unknown payloads has not been established."
                    : classicScalar
                        ? "Classic screen export is documented. Exact BackColor property readback, full export/dependency coverage, unrelated-content preservation, compile, save/reopen and recovered-project equality have not passed for this exact device/runtime profile."
                        : "Classic has no typed screen-item or primitive tag Create route in the inspected V21 assembly; a family-specific preserving XML creation/binding adapter and its dependency closure remain required.";
            return new JObject { ["family"] = family, ["targetProfile"] = profile ?? "unknown", ["operation"] = operation,
                ["apiAvailability"] = body ? "available" : family == "unified" ? "unavailable" : "partial",
                ["bridgeImplementation"] = body ? "guarded_body" : "unavailable",
                ["nativeQualification"] = "unverified", ["available"] = false, ["reasonCode"] = "HmiPreservationUnavailable", ["reason"] = reason,
                ["requiredGates"] = RequiredGates(operation),
                ["engineeringVerified"] = false, ["preservationVerified"] = false, ["persistenceVerified"] = false, ["recoveryVerified"] = false, ["runtimeVerified"] = false };
        }

        internal static JArray RequiredGates(string operation)
        {
            if (!Operations.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Unknown HMI operation.");
            var gates = new JArray("exactProfileFixture", "cleanBaselineCompile", "completePreservationScope", "dependencyClosure",
                "preciseReadback", "unrelatedContentPreserved", "nativeCompile", "saveReopen", "archiveRecoveryEquality",
                "staleTargetRejected", "staleDependencyRejected", "idempotentApply", "interruptedWriteRecovery", "visualInspection");
            switch (operation)
            {
                case "scalar_property_set": gates.Add("exactScalarPropertyDelta"); break;
                case "item_properties_set": gates.Add("exactItemPropertyDelta"); break;
                case "tag_create": gates.Add("newTagNamespaceUnique"); gates.Add("internalTagIsolation"); break;
                case "item_place": gates.Add("exactRectangleWithinScreen"); gates.Add("faceplateTypeIdentityPreserved"); break;
                case "existing_binding_set": gates.Add("existingBindingGuard"); gates.Add("exactTypedBindingOwnership"); break;
                case "explicit_binding_set": gates.Add("exactTypedBindingOwnership"); gates.Add("bindingDependencyClosure"); break;
                case "external_tag_create": gates.Add("newTagNamespaceUnique"); gates.Add("exactConnectionOwnership"); gates.Add("exactPlcDeclarationType"); break;
                case "classic_xml_import": gates.Add("exactDocumentDelta"); gates.Add("objectIdentityPreserved"); break;
                case "connection_create": gates.Add("newConnectionNamespaceUnique"); gates.Add("exactConnectionOwnership"); break;
                case "object_create": gates.Add("newObjectNamespaceUnique"); gates.Add("exactObjectPropertyDelta"); gates.Add("referenceDependencyClosure"); break;
                case "object_properties_set": gates.Add("exactObjectPropertyDelta"); gates.Add("referenceDependencyClosure"); break;
                case "screen_create": gates.Add("newScreenNamespaceUnique"); gates.Add("exactScreenSizeReadback"); break;
                case "item_create": gates.Add("newItemNamespaceUnique"); gates.Add("exactRectangleWithinScreen"); break;
                case "dynamization_set": gates.Add("newDynamizationOnly"); gates.Add("exactTypedBindingOwnership"); gates.Add("bindingDependencyClosure"); break;
                case "event_handler_set": gates.Add("newEventHandlerOnly"); gates.Add("exactScriptTextReadback"); gates.Add("scriptNeverExecuted"); break;
            }
            return gates;
        }

        internal static string Text(JObject value, string key, int max = 256)
        {
            if (value?[key]?.Type != JTokenType.String) throw new ArgumentException(key + " must be an explicit string.");
            string result = (string)value[key];
            if (string.IsNullOrWhiteSpace(result) || result.Length > max || result.Any(char.IsControl)) throw new ArgumentException("Invalid " + key + ".");
            return result;
        }
        private static void RequireSameHmi(JObject target, JObject other, string role)
        {
            if (Text(other, "family") != Text(target, "family")) throw new ArgumentException("The " + role + " belongs to a different HMI family.");
            foreach (string key in new[] { "projectCopyId", "sessionGeneration", "hmiPath", "objectPath" })
                if (key == "objectPath") Text(other, key, 4096);
                else if (Text(other, key, 4096) != Text(target, key, 4096)) throw new ArgumentException("A " + role + " must belong to the exact same HMI/project/session.");
        }
        private static bool UnquotedComponent(string name)
            => name.Length > 0 && name.Length <= 128 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');
        private static void RejectFields(JObject payload, params string[] fields)
        { foreach (string field in fields) if (payload[field] != null) throw new ArgumentException(field + " is not applicable to this operation."); }
    }
}
