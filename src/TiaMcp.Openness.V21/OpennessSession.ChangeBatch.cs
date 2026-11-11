using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // payload.items on the kinds of ChangeBatchRules: each item is prepared as its own member change with that kind's
        // ordinary checks, and the members apply one after the other under the parent's single checkpoint, compile and save.
        private JObject PrepareChangeBatch(EngineeringChange change, Project project)
        {
            var payloads = ChangeBatchRules.Members(change.Payload);
            var preview = new JObject { ["effects"] = new JArray(), ["warnings"] = new JArray() };
            var rows = new JArray();
            var members = new List<EngineeringChange>();
            for (int index = 0; index < payloads.Length; index++)
            {
                var member = new EngineeringChange
                {
                    Id = change.Id, Kind = change.Kind, Payload = payloads[index], Directory = Path.Combine(change.Directory, "items", index.ToString("000")),
                    ProjectPath = change.ProjectPath, ProjectName = change.ProjectName, Selector = (JObject)change.Selector.DeepClone(),
                    State = "preparing", PreparedProcessId = change.PreparedProcessId
                };
                Directory.CreateDirectory(member.Directory);
                JObject row;
                try { row = PrepareEngineeringMutation(member, project); }
                catch (Exception error) { throw BatchItemFailure(error, index, "The whole change is refused; nothing was written."); }
                row["index"] = index;
                row["payload"] = PreviewPayload(member);
                rows.Add(row);
                members.Add(member);
                foreach (var backup in member.Backups) { var copy = (JObject)backup.DeepClone(); copy["index"] = index; change.Backups.Add(copy); }
            }
            var scopes = members.Select(m => m.ScopePath).Distinct(StringComparer.Ordinal).ToArray();
            if (scopes.Length != 1 || scopes[0] == null) throw new BridgeException("ScopeMismatch", "The items span multiple software scopes; split the change per scope. Nothing was written.");
            RequireIndependentItems(members);
            change.ScopePath = scopes[0];
            change.Selector["scopePath"] = change.ScopePath;
            var groups = members.Select(m => m.GroupPath).Distinct(StringComparer.Ordinal).ToArray();
            change.GroupPath = groups.Length == 1 ? groups[0] : change.ScopePath;
            var namespaces = members.Select(m => m.Namespace ?? "").Distinct(StringComparer.Ordinal).ToArray();
            change.Namespace = namespaces.Length == 1 ? namespaces[0] : "";
            // The union: target blocks for the fingerprint, and the change's own objects for the compile comparison.
            change.BlockNames = members.SelectMany(m => m.BlockNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            change.ObjectNames = members.SelectMany(m => m.ObjectNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            change.BatchMembers = members;
            preview["items"] = rows;
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = change.Kind, ["itemCount"] = members.Count, ["items"] = "preview.items lists every item with its own effects." });
            ((JArray)preview["warnings"]).Add("All " + members.Count + " items share one checkpoint archive, one compile and one save. A refusal on any item at preparation refuses the whole change. " +
                "At apply each item repeats its own checks when its turn comes; a failure after the first write leaves the whole change recovery_required.");
            return preview;
        }

        private void ApplyChangeBatch(EngineeringChange change, Project project)
        {
            // Group preflight: every item is prepared once more, with all of its kind's checks and without backup exports,
            // against the project as it is now, before the first item is written. The stale-revision fingerprint was just
            // checked, but it does not cover everything a kind's own checks read (a tag's safety flag, a dependency scan).
            var probes = new List<EngineeringChange>();
            for (int index = 0; index < change.BatchMembers.Count; index++)
            {
                var member = change.BatchMembers[index];
                var probe = new EngineeringChange
                {
                    Id = member.Id, Kind = member.Kind, Payload = (JObject)change.Payload["items"][index].DeepClone(), Directory = member.Directory,
                    ProjectPath = member.ProjectPath, ProjectName = member.ProjectName, Selector = (JObject)change.Selector.DeepClone(),
                    State = "preparing", PreparedProcessId = member.PreparedProcessId, PreflightOnly = true
                };
                if (probe.Payload["scopePath"] == null) probe.Payload["scopePath"] = change.ScopePath;
                try { PrepareEngineeringMutation(probe, project); }
                catch (Exception error) { throw BatchItemFailure(error, index, "The preflight of all items refused the change before any item was written."); }
                if (probe.ScopePath != change.ScopePath)
                    throw new BridgeException("PreviewStale", "items[" + index + "] resolves to another software scope than at preparation. No item was written.");
                probes.Add(probe);
            }
            RequireIndependentItems(probes);
            change.Stages.Add("batch_items_preflighted");
            for (int index = 0; index < change.BatchMembers.Count; index++)
            {
                try { ApplyEngineeringMutation(change.BatchMembers[index], project); }
                // Native TIA failures are wrapped too: the caller must learn which item failed and how many were written.
                catch (Exception error)
                {
                    throw BatchItemFailure(error, index, index == 0 ? "No item was written before it." : "Items 0.." + (index - 1) + " were already written; recover from the checkpoint.");
                }
            }
            // Every kind's apply reads its own write back before it returns.
            change.Readbacks.Add(new JObject { ["role"] = "batch-items-applied", ["verifiedItems"] = change.BatchMembers.Count });
            change.Stages.Add("batch_items_verified");
        }

        private static BridgeException BatchItemFailure(Exception error, int index, string outcome)
        {
            var bridge = error as BridgeException;
            var details = bridge?.Details != null ? (JObject)bridge.Details.DeepClone() : new JObject();
            details["index"] = index;
            // ChangeApply journals a native exception's chain only for an unwrapped exception, so it is kept here.
            if (bridge == null) details["nativeException"] = NativeFailureText.Chain(error);
            return new BridgeException(bridge?.Code ?? "EngineeringError", "items[" + index + "]: " + error.Message + " " + outcome, details);
        }

        // The cross-item rules: no object edited twice, no name or block number produced twice, no renamed group inside another.
        private static void RequireIndependentItems(List<EngineeringChange> members)
        {
            bool numbers = members.Count > 0 && members[0].Kind == "block_number_set";
            ChangeBatchRules.RequireDistinct(members.Select(m => (BatchTarget(m), BatchProduced(m))), numbers ? "BlockNumberCollision" : "ObjectExists", numbers ? "block number" : "name");
            ChangeBatchRules.RequireUnnested(members.Select(m => ChangeBatchRules.GroupTarget(m.Kind, m.Payload)).ToArray());
        }

        // The resolved identity of the object a member edits; creating kinds edit none.
        private static string BatchTarget(EngineeringChange member)
        {
            switch (member.Kind)
            {
                case "constant_set": case "tag_delete": case "constant_delete":
                    return member.GroupPath + "/" + member.ObjectNames[0] + "\n" + member.Payload.Value<string>("memberName");
                case "block_rename": case "block_attributes_set": case "block_number_set": return "block\n" + member.Namespace + "\n" + member.BlockNames[0];
                case "type_rename": return "type\n" + member.Namespace + "\n" + member.ObjectNames[0];
                case "tag_table_rename": return "tagTable\n" + member.ObjectNames[0];
                case "group_rename": return ChangeBatchRules.GroupTarget(member.Kind, member.Payload);
                default: return null;
            }
        }

        // Blocks and types are named per namespace; tags and constants share one name space per software scope, tag tables another.
        // Groups are named per parent group, and a block number is taken per block kind.
        private static string BatchProduced(EngineeringChange member)
        {
            if (member.Kind == "block_number_set") return member.BlockNumberKey;
            if (member.Kind == "group_create" || member.Kind == "group_rename") return ChangeBatchRules.GroupProduced(member.Kind, member.Payload);
            string name = ChangeBatchRules.ProducedName(member.Kind, member.Payload);
            if (name == null) return null;
            return member.Kind == "block_rename" || member.Kind == "type_rename" ? member.Namespace + "\n" + name : name;
        }
    }
}
