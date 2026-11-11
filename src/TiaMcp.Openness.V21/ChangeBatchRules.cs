#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // payload.items on the simple single-object kinds: every item is one ordinary payload of that kind, prepared with that
    // kind's own checks against the unchanged project, and all items share one fingerprint check, checkpoint archive,
    // compile pair and save. tag_set has its own batch (TagBatchRules), which also preflights every item again at apply.
    // Refusals name the item index in their message (the rules are also compiled into tests whose BridgeException has no details).
    internal static class ChangeBatchRules
    {
        internal const int MaxItems = 100;

        // Kinds whose items are independent of each other once no two items share a target or a new name. Deletions of blocks
        // and types (dependency scans), groups (nesting) and the import kinds (already multi-object) are not batched.
        internal static readonly string[] Kinds =
        {
            "constant_set", "tag_create", "constant_create", "tag_delete", "constant_delete",
            "tag_table_create", "tag_table_rename", "block_rename", "block_attributes_set", "type_rename",
        };

        internal static bool HasItems(JObject payload) => payload?["items"] != null && payload["items"].Type != JTokenType.Null;

        /// <summary>True for a batch of one of <see cref="Kinds"/>. A payload with items on any other kind but tag_set refuses.</summary>
        internal static bool IsBatch(string kind, JObject payload)
        {
            if (!HasItems(payload) || kind == "tag_set") return false;
            if (!Kinds.Contains(kind, StringComparer.Ordinal))
                throw new BridgeException("InvalidRequest", "payload.items is supported by tag_set, " + string.Join(", ", Kinds) + " only.");
            return true;
        }

        /// <summary>One payload per item. Beside items the batch payload may carry only scopePath, which every item inherits.</summary>
        internal static JObject[] Members(JObject payload)
        {
            var other = payload.Properties().Select(p => p.Name).FirstOrDefault(name => name != "items" && name != "scopePath");
            if (other != null) throw new BridgeException("InvalidRequest", "A payload with items may carry only items and scopePath; move " + other + " into each item.");
            if (!(payload["items"] is JArray array) || array.Count < 1 || array.Count > MaxItems)
                throw new BridgeException("InvalidRequest", "items must be an array of 1.." + MaxItems + " payloads of this kind.");
            string scope = payload["scopePath"]?.Type == JTokenType.String ? payload.Value<string>("scopePath") : null;
            if (payload["scopePath"] != null && payload["scopePath"].Type != JTokenType.Null && string.IsNullOrWhiteSpace(scope))
                throw new BridgeException("InvalidRequest", "scopePath must be a nonempty string.");
            var result = new JObject[array.Count];
            for (int index = 0; index < array.Count; index++)
            {
                if (!(array[index] is JObject item) || item.Count == 0) throw new BridgeException("InvalidRequest", "items[" + index + "] must be a nonempty object.");
                if (item["items"] != null) throw new BridgeException("InvalidRequest", "items[" + index + "] cannot itself carry items.");
                var member = (JObject)item.DeepClone();
                if (scope != null)
                {
                    if (member["scopePath"] != null && (member["scopePath"].Type != JTokenType.String || member.Value<string>("scopePath") != scope))
                        throw new BridgeException("ScopeMismatch", "items[" + index + "] names another scopePath than the batch.");
                    member["scopePath"] = scope;
                }
                result[index] = member;
            }
            return result;
        }

        /// <summary>The name an item gives an object, or null: a created or renamed tag, constant, tag table, block or type.</summary>
        internal static string ProducedName(string kind, JObject member)
        {
            switch (kind)
            {
                case "constant_set": return Text((member["attributes"] as JObject)?["Name"]);
                case "tag_create": case "constant_create": return Text(member["name"]);
                case "tag_table_create": return Text(member["tagTableName"]);
                case "tag_table_rename": case "block_rename": case "type_rename": return Text(member["newName"]);
                default: return null;
            }
        }

        /// <summary>Refuses a batch in which two items edit the same object or give two objects the same name. A name another item
        /// frees is still taken (the item's own prepare refuses it against the unchanged project), so the result never depends on
        /// the order of writes.</summary>
        /// <param name="items">Per item: the resolved identity of the object it edits (null when it creates one) and the name
        /// space plus name it produces (null when it produces none).</param>
        internal static void RequireDistinct(IEnumerable<(string Target, string Produced)> items)
        {
            var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var produced = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (var (target, name) in items)
            {
                if (target != null)
                {
                    if (targets.TryGetValue(target, out int first))
                        throw new BridgeException("InvalidRequest", "items[" + first + "] and items[" + index + "] edit the same object; merge them into one item. Nothing was written.");
                    targets.Add(target, index);
                }
                if (name != null)
                {
                    if (produced.TryGetValue(name, out int first))
                        throw new BridgeException("ObjectExists", "items[" + first + "] and items[" + index + "] request the same name. Nothing was written.");
                    produced.Add(name, index);
                }
                index++;
            }
        }

        private static string Text(JToken value) => value?.Type == JTokenType.String ? value.Value<string>() : null;
    }
}
