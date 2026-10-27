using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Core.WatchTables;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // One reviewed table/group lifecycle effect under the held watch lease (already freshness-checked by WatchApply).
        // The leased group's whole subtree must equal the exact expected subtree, and every peer must be unchanged, before and after save.
        private JObject WatchLifecycleApply(JObject args, JObject edit)
        {
            if (_watchSelector.Value<string>("targetKind") != "watchTableGroup") throw new BridgeException("InvalidInput", "Lifecycle edits require a group-scoped watch lease.");
            string operation = edit.Value<string>("operation") ?? "", name = ChangeText(edit, "name", 128);
            string newName = operation.StartsWith("rename", StringComparison.Ordinal) ? ChangeText(edit, "newName", 128) : null;
            string beforePath = _watchBefore["target"]["artifact"].Value<string>("path");
            if (ChangeFileHash(beforePath) != _watchBefore["target"].Value<string>("semanticSha256")) throw new BridgeException("ArtifactChanged", "Leased group capture changed.");
            string refusal;
            var expected = WatchGroupManifest.Expect(File.ReadAllBytes(beforePath), operation, name, newName, edit.Value<string>("renamedSemanticSha256"), out refusal);
            if (expected == null) throw new BridgeException(refusal, "The lifecycle operation does not fit the leased group subtree.");
            var selected = SelectPlc(_watchSelector);
            var groups = WatchGroups(selected.Software.WatchAndForceTableGroup, "root", 0).ToArray();
            var group = groups.Single(g => g.Item2 == (_watchSelector.Value<string>("group") ?? "root")).Item1;
            string claimed = operation == "createTable" ? name : operation == "renameTable" ? newName : null;
            if (claimed != null && groups.Any(g => g.Item1.WatchTables.Any(t => string.Equals(t.Name, claimed, StringComparison.OrdinalIgnoreCase)) ||
                g.Item1.ForceTables.Any(t => string.Equals(t.Name, claimed, StringComparison.OrdinalIgnoreCase))))
                throw new BridgeException("WatchTableCollision", "A watch or force table already occupies this name in the selected PLC.");
            var table = operation is "renameTable" or "deleteTable" ? group.WatchTables.Find(name) : null;
            var child = operation is "renameGroup" or "deleteGroup" ? group.Groups.Find(name) : null;
            if (operation is "renameTable" or "deleteTable" && table == null || operation is "renameGroup" or "deleteGroup" && child == null)
                throw new BridgeException("WatchTableNotFound", "The exact reviewed table or group is absent.");
            if (operation == "deleteGroup" && child.WatchTables.Count + child.ForceTables.Count + child.Groups.Count != 0)
                throw new BridgeException("WatchGroupNotEmpty", "Only an empty user group may be deleted.");
            _watchDispatched = true;
            WatchQualificationInterrupt(args, "import.before", selected.Project.Path.FullName, null);
            switch (operation)
            {
                case "createTable": group.WatchTables.Create(name); break;
                case "renameTable": table.Name = newName; break;
                case "deleteTable": table.Delete(); break;
                case "createGroup": group.Groups.Create(name); break;
                case "renameGroup": child.Name = newName; break;
                case "deleteGroup": child.Delete(); break;
            }
            WatchQualificationInterrupt(args, "import.after", selected.Project.Path.FullName, null);
            WatchLifecycleVerify(expected, "Native");
            WatchQualificationInterrupt(args, "save.before", selected.Project.Path.FullName, null);
            selected.Project.Save();
            WatchQualificationInterrupt(args, "save.after", selected.Project.Path.FullName, null);
            var after = WatchLifecycleVerify(expected, "Saved");
            after["saved"] = true; after["automaticReplay"] = false; return after;
        }
        private JObject WatchLifecycleVerify(string[] expected, string stage)
        {
            var after = WatchSnapshot(_watchSelector);
            if (after.Value<string>("peerSha256") != _watchBefore.Value<string>("peerSha256") || after["target"].Value<bool?>("isConsistent") != true ||
                !WatchGroupManifest.Matches(expected, File.ReadAllBytes(after["target"]["artifact"].Value<string>("path"))))
                throw new BridgeException("VerificationFailed", stage + " readback or peer preservation differs from the reviewed lifecycle effect.", after);
            return after;
        }
    }
}
