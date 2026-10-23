#nullable disable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Validate raw JSON before Newtonsoft can coerce values or discard unknown
    // fields. Native resolution and graph validation remain separate later gates.
    internal static class TypedGraphicalPayload
    {
        internal static void Validate(JObject payload)
        {
            Object(payload, "payload", "model blockPath scopePath groupPath namespace instances");
            var model = Object(payload["model"], "model", "schemaVersion language operation networkId insertAfterNetworkId nodes connections operands creation");
            Integer(model, "schemaVersion", 1, 1, true);
            string language = Choice(model, "language", "LAD FBD", true);
            string operation = Choice(model, "operation", "insertNetwork replaceNetwork editNetwork createBlock", true);
            foreach (string property in new[] { "blockPath", "scopePath", "groupPath", "namespace" }) Text(payload, property, 4096, false, property == "namespace");
            if (operation != "createBlock") Text(payload, "blockPath", 4096, true);
            else if (payload["blockPath"] != null) Fail("createBlock cannot select an existing blockPath.");
            bool selected = operation == "replaceNetwork" || operation == "editNetwork";
            NativeId(model, "networkId", selected);
            NativeId(model, "insertAfterNetworkId", false);
            if (!selected && model["networkId"] != null) Fail("networkId is only valid for replaceNetwork/editNetwork.");
            if (operation != "insertNetwork" && model["insertAfterNetworkId"] != null) Fail("insertAfterNetworkId is only valid for insertion.");

            var nodes = Array(model, "nodes", 1, 1024, true);
            foreach (JToken token in nodes)
            {
                var node = Object(token, "node", operation == "editNetwork" ? "key kind nativeUid operand" : "key kind operand inputCount comparison dataType calleePath instance instruction nativeUid negated allowSharedInstance");
                Key(node, "key", true);
                string kinds = operation == "editNetwork" ? (language == "LAD" ? "contact coil" : "assign") : language == "LAD" ? "powerrail contact coil branch compare call timer counter" : "and or not assign compare call timer counter";
                string kind = Choice(node, "kind", kinds, true);
                Key(node, "operand", kind == "contact" || kind == "coil" || kind == "assign");
                Key(node, "instance", kind == "timer" || kind == "counter");
                Text(node, "calleePath", 4096, kind == "call");
                Text(node, "dataType", 256, kind == "compare");
                Choice(node, "comparison", "eq ne lt le gt ge", kind == "compare");
                Choice(node, "instruction", kind == "timer" ? "TON TOF TP" : kind == "counter" ? "CTU CTD CTUD" : "TON TOF TP CTU CTD CTUD", kind == "timer" || kind == "counter");
                Integer(node, "inputCount", 0, 32, false);
                NativeId(node, "nativeUid", operation == "editNetwork");
                if (node["nativeUid"] != null && (operation == "insertNetwork" || operation == "createBlock")) Fail("nativeUid is only valid for a selected edit or replacement.");
                Boolean(node, "negated"); Boolean(node, "allowSharedInstance");
            }
            Unique(nodes, "key", "node keys");

            var operands = Array(model, "operands", 0, 2048, true);
            foreach (JToken token in operands)
            {
                var operand = Object(token, "operand", "key scope ownerPath components dataType value");
                Key(operand, "key", true);
                string scope = Choice(operand, "scope", "local tag db constant", true);
                Text(operand, "ownerPath", 4096, scope == "tag" || scope == "db");
                Text(operand, "dataType", 256, scope == "constant");
                Text(operand, "value", 256, scope == "constant");
                Components(operand, scope != "constant");
                if (scope == "constant" && (operand["components"] != null || operand["ownerPath"] != null)) Fail("Constants cannot have symbolic components or owners.");
                if (scope != "constant" && operand["value"] != null) Fail("Symbolic operands cannot include a constant value.");
            }
            Unique(operands, "key", "operand keys");
            foreach (JToken token in Array(model, "connections", 0, 4096, true))
            {
                var wire = Object(token, "connection", "from to");
                foreach (string side in new[] { "from", "to" })
                {
                    var end = Object(wire[side], side, "node port");
                    string key = Text(end, "node", 129, true);
                    if (!Regex.IsMatch(key, @"\A\$?[A-Za-z_][A-Za-z0-9_.-]{0,127}\z")) Fail("Connection node must identify a logical node or $operand key.");
                    Text(end, "port", 128, true);
                }
            }

            if (operation == "createBlock")
            {
                var creation = Object(model["creation"], "creation", "name blockType interface");
                string name = Text(creation, "name", 80, true);
                if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]{0,79}\z")) Fail("New block name must be a simple identifier.");
                string kind = Choice(creation, "blockType", "FC FB", true);
                var declarations = Array(creation, "interface", 0, 500, true);
                foreach (JToken token in declarations)
                {
                    var declaration = Object(token, "declaration", "name section dataType");
                    string member = Text(declaration, "name", 128, true);
                    if (!Regex.IsMatch(member, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z")) Fail("Interface name must be a simple identifier.");
                    Choice(declaration, "section", kind == "FB" ? "Input Output InOut Static Temp Constant" : "Input Output InOut Temp Constant Return", true);
                    Text(declaration, "dataType", 256, true);
                }
                Unique(declarations, "name", "declaration names", true);
            }
            else if (model["creation"] != null) Fail("creation is only valid for createBlock.");

            var instances = Array(payload, "instances", 0, 128, false);
            foreach (JToken token in instances)
            {
                var instance = Object(token, "instance", "key kind blockPath components name");
                Key(instance, "key", true);
                string kind = Choice(instance, "kind", "existingGlobal existingMulti createGlobal createMulti", true);
                if (kind == "existingGlobal")
                { Text(instance, "blockPath", 4096, true); Forbidden(instance, "components name"); }
                else if (kind == "existingMulti")
                { Components(instance, true); Forbidden(instance, "blockPath name"); }
                else
                {
                    string name = Text(instance, "name", 80, true);
                    if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]{0,79}\z")) Fail("New instance name must be a simple identifier.");
                    Forbidden(instance, "blockPath components");
                }
            }
            Unique(instances, "key", "instance keys");
        }

        private static JObject Object(JToken token, string name, string allowed)
        {
            var value = token as JObject;
            if (value == null) Fail(name + " must be an object.");
            var fields = allowed.Split(' ');
            foreach (var property in value.Properties())
                if (!fields.Contains(property.Name, StringComparer.Ordinal)) Fail("Unknown " + name + " field: " + property.Name);
            return value;
        }
        private static JArray Array(JObject owner, string field, int minimum, int maximum, bool required)
        {
            var token = owner[field];
            if (token == null && !required) return new JArray();
            var value = token as JArray;
            if (value == null || value.Count < minimum || value.Count > maximum) Fail(field + " must be an array of " + minimum + ".." + maximum + " items.");
            return value;
        }
        private static string Text(JObject owner, string field, int maximum, bool required, bool allowEmpty = false)
        {
            var token = owner[field];
            if (token == null && !required) return null;
            if (token == null || token.Type != JTokenType.String) Fail(field + " must be a string.");
            string value = token.Value<string>();
            if (value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.IndexOf('\0') >= 0) Fail(field + " has an invalid length or value.");
            return value;
        }
        private static string Choice(JObject owner, string field, string allowed, bool required)
        {
            string value = Text(owner, field, 64, required);
            if (value != null && !allowed.Split(' ').Contains(value, StringComparer.Ordinal)) Fail(field + " must be one of: " + allowed);
            return value;
        }
        private static void Key(JObject owner, string field, bool required)
        {
            string value = Text(owner, field, 128, required);
            if (value != null && !Regex.IsMatch(value, @"\A[A-Za-z_][A-Za-z0-9_.-]{0,127}\z")) Fail(field + " must be a logical key.");
        }
        private static void NativeId(JObject owner, string field, bool required)
        {
            string value = Text(owner, field, 10, required);
            if (value != null && !Regex.IsMatch(value, field == "nativeUid" ? @"\A[0-9]+\z" : @"\A[0-9A-Fa-f]+\z")) Fail(field + " must be a native ID string (hexadecimal object ID or decimal UId).");
        }
        private static void Integer(JObject owner, string field, int minimum, int maximum, bool required)
        {
            var token = owner[field];
            if (token == null && !required) return;
            if (token == null || token.Type != JTokenType.Integer) Fail(field + " must be an integer.");
            long value;
            if (!long.TryParse(token.ToString(), out value) || value < minimum || value > maximum) Fail(field + " is outside the supported range.");
        }
        private static void Boolean(JObject owner, string field)
        { if (owner[field] != null && owner[field].Type != JTokenType.Boolean) Fail(field + " must be Boolean."); }
        private static void Components(JObject owner, bool required)
        {
            if (owner["components"] == null && !required) return;
            foreach (JToken component in Array(owner, "components", 1, 32, true))
                if (component.Type != JTokenType.String || string.IsNullOrWhiteSpace(component.Value<string>()) || component.Value<string>().Length > 128 || component.Value<string>().IndexOf('\0') >= 0) Fail("Every component must be a bounded nonempty string.");
        }
        private static void Unique(JArray array, string field, string label, bool ignoreCase = false)
        {
            var values = array.Cast<JObject>().Select(value => value.Value<string>(field)).ToArray();
            if (values.Distinct(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != values.Length) Fail("Duplicate " + label + ".");
        }
        private static void Forbidden(JObject owner, string names)
        { if (names.Split(' ').Any(name => owner[name] != null)) Fail("Fields do not belong to this instance kind: " + names); }
        private static void Fail(string message) => throw new BridgeException("InvalidTypedGraphicalPayload", message);
    }
}
