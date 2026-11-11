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

        // Kinds whose items are independent of each other once no two items share a target or a new name. Three kinds need a
        // stricter rule to be independent: block_number_set (no two items may take one number of one block kind),
        // group_create (no two items may create one name under one parent; a parent another item creates does not exist yet
        // and refuses at that item's own prepare) and group_rename (as before, and no item's group may lie inside another
        // item's, whose rename would move its path). Deletions of blocks, types and groups (dependency scans, nesting) and
        // the import kinds (already multi-object) are not batched.
        internal static readonly string[] Kinds =
        {
            "constant_set", "tag_create", "constant_create", "tag_delete", "constant_delete",
            "tag_table_create", "tag_table_rename", "block_rename", "block_attributes_set", "type_rename",
            "block_number_set", "group_create", "group_rename",
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
        /// <param name="producedCode">The refusal code for two items producing the same thing.</param>
        /// <param name="producedWhat">What they produce, for the message: a name or a block number.</param>
        internal static void RequireDistinct(IEnumerable<(string Target, string Produced)> items, string producedCode = "ObjectExists", string producedWhat = "name")
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
                        throw new BridgeException(producedCode, "items[" + first + "] and items[" + index + "] request the same " + producedWhat + ". Nothing was written.");
                    produced.Add(name, index);
                }
                index++;
            }
        }

        /// <summary>The group a group_rename item edits, as group kind and path; null for any other kind.</summary>
        internal static string GroupTarget(string kind, JObject member)
        {
            string path = Text(member["groupPath"]);
            return kind == "group_rename" && path != null ? GroupKey(member, path.TrimEnd('/')) : null;
        }

        /// <summary>The sibling name a group item produces: group kind, parent path and name. group_create names its parent
        /// in groupPath; group_rename names the group itself, whose parent is the path without its last segment.</summary>
        internal static string GroupProduced(string kind, JObject member)
        {
            string path = Text(member["groupPath"])?.TrimEnd('/'), name = Text(member["name"]);
            if ((kind != "group_create" && kind != "group_rename") || path == null || name == null) return null;
            if (kind == "group_rename") path = path.Substring(0, Math.Max(0, path.LastIndexOf('/')));
            return GroupKey(member, path) + "\n" + name;
        }

        /// <summary>Refuses a group_rename batch in which one item's group lies inside another item's: renaming the outer
        /// group changes the inner group's path, so the inner item would no longer resolve after the first write.</summary>
        /// <param name="targets">Per item its <see cref="GroupTarget"/>.</param>
        internal static void RequireUnnested(IReadOnlyList<string> targets)
        {
            for (int outer = 0; outer < targets.Count; outer++)
                for (int inner = 0; inner < targets.Count; inner++)
                    if (outer != inner && targets[outer] != null && targets[inner] != null && targets[inner].StartsWith(targets[outer] + "/", StringComparison.OrdinalIgnoreCase))
                        throw new BridgeException("InvalidRequest", "items[" + inner + "] renames a group inside the group that items[" + outer + "] renames; rename nested groups in separate changes. Nothing was written.");
        }

        private static string GroupKey(JObject member, string path) => "group\n" + Text(member["groupKind"]) + "\n" + path;

        private static string Text(JToken value) => value?.Type == JTokenType.String ? value.Value<string>() : null;
    }
}
