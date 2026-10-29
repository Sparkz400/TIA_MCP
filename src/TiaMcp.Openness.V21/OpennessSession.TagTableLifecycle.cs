using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Tags;

namespace TiaMcp.Openness.V21
{
    // Tag-table lifecycle beside tag_table_import_xml, mirroring the watch-table lifecycle: create an empty table, rename
    // one, or delete an empty user table. Members are never created, moved or deleted here.
    internal sealed partial class OpennessSession
    {
        private void PrepareTagTableLifecycle(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            bool create = change.Kind == "tag_table_create";
            change.ObjectNames = new[] { create ? ChangeText(change.Payload, "tagTableName", 128) : MutationObjectName(change, plc, "tagTable") };
            PrepareMutationScope(change, plc, "tagTable", change.ObjectNames);
            var table = TagTableLifecycleCheck(change, plc);
            if (table != null)
            {
                string path = Path.Combine(change.Directory, "before-tags.xml");
                table.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                change.Backups.Add(new JObject { ["tableName"] = table.Name, ["artifact"] = ChangeArtifact(path) });
            }
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = change.Kind, ["scopePath"] = change.ScopePath, ["groupPath"] = change.GroupPath,
                ["tagTableName"] = change.ObjectNames[0], ["newName"] = change.Payload["newName"]?.DeepClone() });
            ((JArray)preview["warnings"]).Add("Only an empty table is created or deleted, and the default tag table is never deleted. Renaming keeps the table's tags; references resolve by tag name, which compilation checks.");
        }

        // Prepare and apply share this, so apply repeats the scope-wide name and emptiness checks immediately before dispatch.
        private PlcTagTable TagTableLifecycleCheck(EngineeringChange change, PlcSelection plc)
        {
            string name = change.ObjectNames[0], newName = change.Kind == "tag_table_rename" ? ChangeText(change.Payload, "newName", 128) : null;
            var table = change.Kind == "tag_table_create" ? null : MutationTagGroup(change, plc).TagTables.SingleOrDefault(item => item.Name == name);
            string refusal = TagTableLifecycleRules.Refusal(change.Kind, name, newName, table != null, table?.IsDefault == true,
                table == null ? 0 : table.Tags.Count + table.UserConstants.Count, AllTagTables(ResolveSoftwareScope(plc, change.ScopePath).Tags).Select(item => item.Name));
            if (refusal != null) throw new BridgeException(refusal, refusal == "TagTableNotEmpty" ? "Only an empty tag table may be deleted; delete or move its tags and user constants first."
                : refusal == "DefaultTagTable" ? "The default tag table cannot be deleted." : refusal == "ObjectExists" ? "A tag table in this software scope already uses the name."
                : refusal == "InvalidRequest" ? "newName must differ from the current name." : "The exact tag table is absent from its prepared group.");
            return table;
        }

        private void ApplyTagTableLifecycle(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var table = TagTableLifecycleCheck(change, plc);
            var group = MutationTagGroup(change, plc);
            string name = change.ObjectNames[0], expected = change.Kind == "tag_table_rename" ? change.Payload.Value<string>("newName") : name;
            int members = table == null ? 0 : table.Tags.Count + table.UserConstants.Count;
            if (change.Kind == "tag_table_create") table = group.TagTables.Create(name);
            else if (change.Kind == "tag_table_rename") table.Name = expected;
            else table.Delete();
            var after = group.TagTables.SingleOrDefault(item => item.Name == expected);
            // A created table is empty, a renamed one keeps its members and no table keeps the old name.
            bool verified = change.Kind == "tag_table_delete" ? after == null
                : after != null && after.Tags.Count + after.UserConstants.Count == members &&
                  (change.Kind == "tag_table_create" || string.Equals(name, expected, StringComparison.OrdinalIgnoreCase) || group.TagTables.All(item => item.Name != name));
            if (!verified) throw new BridgeException("MutationVerificationFailed", "The tag table read back differs from the requested " + change.Kind + ".");
            if (after != null)
            {
                string path = Path.Combine(change.Directory, "after-tags.xml");
                after.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                change.Readbacks.Add(new JObject { ["role"] = "tag-table-readback", ["tableName"] = after.Name, ["artifact"] = ChangeArtifact(path) });
            }
        }
    }
}
