#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // tag_set with payload.items: many tag edits prepared as one change, so they share one fingerprint check, one recovery
    // archive, one baseline and post-change compile and one save. Every rule here refuses the whole change before anything is written.
    // Refusals name the item index in their message (the rules are also compiled into tests whose BridgeException has no details).
    internal static class TagBatchRules
    {
        internal const int MaxItems = 500;
        internal static readonly string[] Attributes = { "Name", "DataTypeName", "LogicalAddress" };
        private static readonly string[] SingleFields = { "tagTableName", "tagTablePath", "memberName", "attributes" };
        private static readonly string[] ItemFields = { "tagTableName", "tagTablePath", "memberName", "attributes" };

        internal sealed class Item
        {
            internal int Index;
            internal string TableName, TablePath, MemberName;
            internal JObject Attributes;
            /// <summary>The requested Name, or null when the item does not rename.</summary>
            internal string NewName => Attributes.Value<string>("Name");
            internal string FinalName => NewName ?? MemberName;
        }

        internal static bool IsBatch(JObject payload) => payload?["items"] != null && payload["items"].Type != JTokenType.Null;

        /// <summary>The validated items of a batch payload. Shape only: targets, safety tags and name collisions need the project.</summary>
        internal static Item[] Items(JObject payload)
        {
            foreach (string field in SingleFields)
                if (payload[field] != null)
                    throw new BridgeException("InvalidRequest", "Give either items, or tagTableName/tagTablePath with memberName and attributes; " + field + " cannot accompany items.");
            // A safety tag is renamed by a single tag_set only (SafetyTagRenameRules); a batch never edits one.
            if (payload["safetyAcknowledged"] != null)
                throw new BridgeException("InvalidRequest", "safetyAcknowledged cannot accompany items: a batch never edits a safety tag. Rename a safety tag with a single tag_set.");
            if (!(payload["items"] is JArray array) || array.Count < 1 || array.Count > MaxItems)
                throw new BridgeException("InvalidRequest", "items must be an array of 1.." + MaxItems + " tag edits.");
            var result = new Item[array.Count];
            for (int index = 0; index < array.Count; index++)
            {
                if (!(array[index] is JObject row)) throw Invalid(index, "must be an object.");
                var unknown = row.Properties().Select(p => p.Name).FirstOrDefault(name => !ItemFields.Contains(name, StringComparer.Ordinal));
                if (unknown != null) throw Invalid(index, "has the unsupported field " + unknown + "; use tagTableName or tagTablePath, memberName and attributes.");
                string tableName = Text(row, "tagTableName", 128, index, true), tablePath = Text(row, "tagTablePath", 4096, index, true);
                if (tableName == null && tablePath == null) throw Invalid(index, "needs tagTableName or tagTablePath.");
                if (!(row["attributes"] is JObject attributes) || attributes.Count == 0) throw Invalid(index, "needs attributes with at least one editable field.");
                foreach (var attribute in attributes.Properties())
                {
                    if (!Attributes.Contains(attribute.Name, StringComparer.Ordinal))
                        throw new BridgeException("UnsupportedAttribute", "items[" + index + "]: attribute is outside the reviewed write subset: " + attribute.Name);
                    if (attribute.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace(attribute.Value.Value<string>()) || attribute.Value.Value<string>().Length > (attribute.Name == "LogicalAddress" ? 1024 : 256))
                        throw Invalid(index, "attribute " + attribute.Name + " must be a nonempty bounded string.");
                }
                if (attributes.Value<string>("Name")?.Length > 128) throw Invalid(index, "attribute Name exceeds 128 characters.");
                result[index] = new Item { Index = index, TableName = tableName, TablePath = tablePath, MemberName = Text(row, "memberName", 128, index, false), Attributes = (JObject)attributes.DeepClone() };
            }
            return result;
        }

        /// <summary>Refuses a batch that names one tag twice, gives two tags the same new name, or renames onto a name the software
        /// scope already holds. Rename chains and swaps (A to B while B moves away) are refused too: with them the result would
        /// depend on the order of writes, and memberName could no longer identify a tag by its name before the change.</summary>
        /// <param name="targets">Each item with its resolved tag-table path.</param>
        /// <param name="scopeNames">Every tag, user-constant and system-constant name of the software scope before the change.</param>
        internal static void RequireNoCollisions(IEnumerable<(Item Item, string TablePath)> targets, IEnumerable<string> scopeNames)
        {
            var existing = new HashSet<string>(scopeNames, StringComparer.OrdinalIgnoreCase);
            var members = new Dictionary<string, int>(StringComparer.Ordinal);
            var created = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (item, tablePath) in targets)
            {
                string member = tablePath + "\n" + item.MemberName;
                if (members.TryGetValue(member, out int first))
                    throw new BridgeException("InvalidRequest", "items[" + first + "] and items[" + item.Index + "] edit the same tag " + item.MemberName + "; merge their attributes into one item.");
                members.Add(member, item.Index);
                if (item.NewName == null) continue;
                if (created.TryGetValue(item.NewName, out first))
                    throw new BridgeException("ObjectExists", "items[" + first + "] and items[" + item.Index + "] request the same name " + item.NewName + ". Nothing was written.");
                created.Add(item.NewName, item.Index);
                // A tag may keep its name or change only its case; every other existing name is taken, also one this batch renames away.
                if (!string.Equals(item.NewName, item.MemberName, StringComparison.OrdinalIgnoreCase) && existing.Contains(item.NewName))
                    throw new BridgeException("ObjectExists", "items[" + item.Index + "]: a tag or constant already uses the requested name " + item.NewName +
                        " in the software scope. A batch cannot reuse a name another item renames away; apply that rename as a separate change first. Nothing was written.");
            }
        }

        private static string Text(JObject row, string name, int length, int index, bool optional)
        {
            var value = row[name];
            if (value == null || value.Type == JTokenType.Null) { if (optional) return null; throw Invalid(index, "needs " + name + "."); }
            if (value.Type != JTokenType.String || string.IsNullOrWhiteSpace(value.Value<string>()) || value.Value<string>().Length > length)
                throw Invalid(index, name + " must be a nonempty string of at most " + length + " characters.");
            return value.Value<string>();
        }

        private static BridgeException Invalid(int index, string message) =>
            new BridgeException("InvalidRequest", "items[" + index + "] " + message);
    }
}
