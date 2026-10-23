using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    internal static class HmiEngineeringRules
    {
        internal static readonly string[] Operations = { "scalar_property_set", "existing_binding_set", "tag_create", "item_properties_set", "item_place", "explicit_binding_set" };
        private static readonly string[] PrimitiveTypes = { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal" };

        internal static void RequireRequest(JObject payload)
        {
            if (payload == null || payload.Value<int?>("schemaVersion") != 1) throw new ArgumentException("HMI engineering requires payload.schemaVersion=1.");
            var permitted = new HashSet<string>(new[] { "schemaVersion", "operation", "target", "expectedContentHash", "properties", "name", "dataType", "placement", "bindings" }, StringComparer.Ordinal);
            foreach (var property in payload.Properties())
                if (!permitted.Contains(property.Name)) throw new ArgumentException("Unsupported HMI engineering field: " + property.Name);
            string operation = Text(payload, "operation");
            if (!Operations.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Unknown HMI engineering operation.");
            var target = payload["target"] as JObject ?? throw new ArgumentException("An exact HMI target identity is required.");
            HmiRecipeRules.RequireFamily(Text(target, "family"));
            foreach (string key in new[] { "projectCopyId", "sessionGeneration", "hmiPath", "objectPath" }) Text(target, key, 4096);
            string hash = Text(payload, "expectedContentHash");
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("expectedContentHash must be the full native-content SHA-256; metadataRevision is insufficient.");
            if (operation == "tag_create")
            {
                HmiRecipeRules.RequireName(Text(payload, "name"), "name");
                if (!PrimitiveTypes.Contains(Text(payload, "dataType"), StringComparer.Ordinal))
                    throw new ArgumentException("Only primitive internal HMI tags have a creation body; external PLC/UDT mappings require a separately qualified operation.");
                RejectFields(payload, "properties", "placement", "bindings");
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
                RejectFields(payload, "name", "dataType", "placement", "bindings");
            }
            else if (operation == "item_place")
            {
                var placement = payload["placement"] as JObject ?? throw new ArgumentException("An explicit placement rectangle is required.");
                if (placement.Count != 4) throw new ArgumentException("Placement requires exactly left, top, width and height.");
                foreach (string key in new[] { "left", "top", "width", "height" })
                    if (placement[key]?.Type != JTokenType.Integer) throw new ArgumentException("Placement requires integer " + key + ".");
                RejectFields(payload, "name", "dataType", "properties", "bindings");
            }
            else
            {
                var bindings = payload["bindings"] as JArray ?? throw new ArgumentException("An explicit binding array is required.");
                if (bindings.Count < 1 || bindings.Count > 200) throw new ArgumentException("At most 200 explicit bindings are accepted.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in bindings)
                {
                    var binding = token as JObject ?? throw new ArgumentException("A binding must be an object.");
                    if (binding.Properties().Any(p => !new[] { "property", "tagIdentity", "expectedValue", "dataType" }.Contains(p.Name, StringComparer.Ordinal)))
                        throw new ArgumentException("A binding contains unsupported fields; caller-provided evidence cannot authorize rewiring.");
                    if (!seen.Add(Text(binding, "property"))) throw new ArgumentException("Duplicate binding property.");
                    Text(binding, "dataType");
                    var tag = binding["tagIdentity"] as JObject ?? throw new ArgumentException("Binding requires an exact HMI tag identity, never only a name.");
                    if (Text(tag, "family") != Text(target, "family")) throw new ArgumentException("The binding target belongs to a different HMI family.");
                    foreach (string key in new[] { "projectCopyId", "sessionGeneration", "hmiPath", "objectPath" })
                        if (key == "objectPath") Text(tag, key, 4096);
                        else if (Text(tag, key, 4096) != Text(target, key, 4096)) throw new ArgumentException("A binding must belong to the exact same HMI/project/session.");
                    if (operation == "existing_binding_set" && binding["expectedValue"] == null) throw new ArgumentException("Changing an existing binding requires its observed expectedValue.");
                }
                RejectFields(payload, "name", "dataType", "properties", "placement");
            }
        }

        internal static JObject Gate(string family, string profile, string operation)
        {
            HmiRecipeRules.RequireFamily(family);
            if (!Operations.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Unknown HMI operation.");
            bool classicScalar = family == "classic" && operation == "scalar_property_set";
            string reason = family == "unified" && operation == "tag_create"
                ? "Unified tag export is documented, but the native export/readback dependency closure and unrelated-resource preservation, compiler, save/reopen and archive-recovery gates have not passed for this exact device/runtime profile."
                : family == "unified"
                    ? "Installed V21 Unified APIs expose typed properties/placement/bindings, but no screen Export method was found; a complete readback of events, dynamics, translations, resources and unknown payloads has not been established."
                    : classicScalar
                        ? "Classic screen export is documented. Exact BackColor property readback, full export/dependency coverage, unrelated-content preservation, compile, save/reopen and recovered-project equality have not passed for this exact device/runtime profile."
                        : "Classic has no typed screen-item or primitive tag Create route in the inspected V21 assembly; a family-specific preserving XML creation/binding adapter and its dependency closure remain required.";
            return new JObject { ["family"] = family, ["targetProfile"] = profile ?? "unknown", ["operation"] = operation,
                ["apiAvailability"] = family == "unified" || classicScalar ? "available" : "partial",
                ["bridgeImplementation"] = family == "unified" || classicScalar ? "guarded_body" : "unavailable",
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
        private static void RejectFields(JObject payload, params string[] fields)
        { foreach (string field in fields) if (payload[field] != null) throw new ArgumentException(field + " is not applicable to this operation."); }
    }
}
