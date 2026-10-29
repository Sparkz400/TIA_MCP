using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure decisions for tag_table_create, tag_table_rename and tag_table_delete; null admits the change.
    // Names are unique per software scope across every tag-table group, compared case-insensitively like TIA.
    internal static class TagTableLifecycleRules
    {
        internal static string Refusal(string kind, string name, string newName, bool exists, bool isDefault, int members, IEnumerable<string> scopeTableNames)
        {
            var names = new HashSet<string>(scopeTableNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            switch (kind)
            {
                case "tag_table_create": return names.Contains(name) ? "ObjectExists" : null;
                case "tag_table_rename":
                    if (!exists) return "ObjectNotFound";
                    if (string.Equals(name, newName, StringComparison.Ordinal)) return "InvalidRequest";
                    // A case-only rename of the same table is not a collision.
                    return names.Contains(newName) && !string.Equals(name, newName, StringComparison.OrdinalIgnoreCase) ? "ObjectExists" : null;
                case "tag_table_delete":
                    if (!exists) return "ObjectNotFound";
                    if (isDefault) return "DefaultTagTable";
                    return members != 0 ? "TagTableNotEmpty" : null;
                default: return "UnsupportedChange";
            }
        }
    }
}
