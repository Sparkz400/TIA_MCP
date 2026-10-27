using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Tags;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private string MutationObjectName(EngineeringChange change, PlcSelection plc, string kind)
        {
            string nameKey = kind == "type" ? "typeName" : "tagTableName";
            string pathKey = kind == "type" ? "typePath" : "tagTablePath";
            if (string.IsNullOrWhiteSpace(change.Payload.Value<string>(pathKey))) return ChangeText(change.Payload, nameKey, 128);
            var selectors = (JObject)change.Selector.DeepClone();
            selectors["kind"] = kind; selectors["objectPath"] = change.Payload[pathKey];
            selectors["name"] = change.Payload[nameKey];
            selectors["scopePath"] = change.Payload["scopePath"] ?? selectors["scopePath"];
            selectors["namespace"] = change.Payload["namespace"];
            return ResolveObjectTarget(selectors).Identity.Value<string>("name");
        }

        private PlcType MutationType(EngineeringChange change, PlcSelection plc, string name)
        {
            var matches = MutationTypeGroup(change, plc).Types.Where(type => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) && (type.Namespace ?? "") == change.Namespace).ToArray();
            if (matches.Length != 1) throw new BridgeException("ObjectNotFound", "Exactly one existing type must match the selected name, namespace and group.");
            if (matches[0].IsKnowHowProtected) throw new BridgeException("ProtectedObject", "Protected data types cannot be edited.");
            return matches[0];
        }

        private void PrepareTypeEdit(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            change.ObjectNames = new[] { MutationObjectName(change, plc, "type") };
            PrepareMutationScope(change, plc, "type", change.ObjectNames);
            var type = MutationType(change, plc, change.ObjectNames[0]);
            PrepareTypeTargets(change, plc, (JArray)preview["effects"]);
            if (change.Kind == "type_rename")
            {
                string newName = ChangeText(change.Payload, "newName", 128);
                var budget = new ScanBudget(50000);
                if (ScopeTypes(ResolveSoftwareScope(plc, change.ScopePath), budget).Any(item => string.Equals(item.Item.Name, newName, StringComparison.OrdinalIgnoreCase) && (item.Item.Namespace ?? "") == change.Namespace))
                    throw new BridgeException("ObjectExists", "A type already uses newName in the selected namespace.");
                if (budget.Truncated) throw new BridgeException("TraversalLimit", "Type collision lookup is incomplete.");
                ValidateNamespaceOwnership(change, plc, "type", new[] { newName });
                ((JArray)preview["effects"]).Add(new JObject { ["renameTypeTo"] = newName });
                ((JArray)preview["warnings"]).Add("Native rename may affect dependent declarations. No source text is rewritten by the bridge; full PLC compilation is required.");
            }
            else preview["dependencies"] = CheckDeleteDependencies(change, plc, type, type.Name);
        }

        private void ApplyTypeEdit(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var type = MutationType(change, plc, change.ObjectNames[0]);
            if (change.Kind == "type_delete")
            {
                CheckDeleteDependencies(change, plc, type, type.Name);
                type.Delete();
                if (MutationTypeGroup(change, plc).Types.Any(item => item.Name == change.ObjectNames[0] && (item.Namespace ?? "") == change.Namespace))
                    throw new BridgeException("MutationVerificationFailed", "Deleted type still exists.");
            }
            else
            {
                type.Name = change.Payload.Value<string>("newName");
                if (type.Name != change.Payload.Value<string>("newName")) throw new BridgeException("MutationVerificationFailed", "Type rename read-back failed.");
            }
        }

        private PlcTagTable MutationTable(EngineeringChange change, PlcSelection plc) => MutationTagGroup(change, plc).TagTables.SingleOrDefault(table => table.Name == change.ObjectNames[0])
            ?? throw new BridgeException("ObjectNotFound", "Selected tag table does not exist in its prepared group.");

        private IEngineeringObject MutationTagMember(EngineeringChange change, PlcTagTable table)
        {
            string name = ChangeText(change.Payload, "memberName", 128);
            bool constant = change.Kind.StartsWith("constant_", StringComparison.Ordinal);
            IEngineeringObject member = constant ? (IEngineeringObject)table.UserConstants.FirstOrDefault(item => item.Name == name) : table.Tags.FirstOrDefault(item => item.Name == name);
            if (member is PlcTag tag && tag.IsSafety)
                throw new BridgeException("SafetyBoundary", "Safety tags are excluded from generic tag edits and deletion, including tags in standard PLC tables.");
            return member ?? throw new BridgeException("ObjectNotFound", "No editable " + (constant ? "user constant" : "tag") + " has the exact memberName. System constants are excluded.");
        }

        private void PrepareTagEdit(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            change.ObjectNames = new[] { MutationObjectName(change, plc, "tagTable") };
            PrepareMutationScope(change, plc, "tagTable", change.ObjectNames);
            var table = MutationTable(change, plc);
            var member = MutationTagMember(change, table);
            string path = Path.Combine(change.Directory, "before-tags.xml");
            table.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
            change.Backups.Add(new JObject { ["tableName"] = table.Name, ["artifact"] = ChangeArtifact(path) });
            if (change.Kind.EndsWith("_delete", StringComparison.Ordinal))
                preview["dependencies"] = CheckDeleteDependencies(change, plc, member, change.Payload.Value<string>("memberName"));
            else
            {
                var attrs = ChangeObject(change.Payload, "attributes");
                if (attrs.Count == 0) throw new BridgeException("InvalidRequest", "attributes must contain at least one editable field.");
                var allow = new HashSet<string>(new[] { "Name", "DataTypeName", change.Kind == "constant_set" ? "Value" : "LogicalAddress" }, StringComparer.Ordinal);
                if (attrs["Name"] != null) RejectTagNameCollision(change, plc, ChangeText(attrs, "Name", 128), member);
                change.AttributeTarget = member;
                ((JArray)preview["effects"]).Add(new JObject { ["memberName"] = change.Payload["memberName"], ["attributes"] = PrepareAttributes(member, attrs, allow) });
            }
        }

        private void ApplyTagEdit(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var table = MutationTable(change, plc);
            var member = MutationTagMember(change, table);
            if (change.Kind.EndsWith("_delete", StringComparison.Ordinal))
            {
                CheckDeleteDependencies(change, plc, member, change.Payload.Value<string>("memberName"));
                if (member is PlcTag tag) tag.Delete(); else ((PlcUserConstant)member).Delete();
                string name = change.Payload.Value<string>("memberName");
                if (table.Tags.Any(item => item.Name == name) || table.UserConstants.Any(item => item.Name == name))
                    throw new BridgeException("MutationVerificationFailed", "Deleted member still exists.");
            }
            else ApplyAttributes(member, ChangeObject(change.Payload, "attributes"), new HashSet<string>(new[] { "Name", "DataTypeName", change.Kind == "constant_set" ? "Value" : "LogicalAddress" }, StringComparer.Ordinal));
        }

        private static void RejectTagNameCollision(EngineeringChange change, PlcSelection plc, string name, IEngineeringObject except)
        {
            if (AllTagTables(ResolveSoftwareScope(plc, change.ScopePath).Tags).Any(item => item.Tags.Any(tag => !ReferenceEquals(tag, except) && string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                item.UserConstants.Any(constant => !ReferenceEquals(constant, except) && string.Equals(constant.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                item.SystemConstants.Any(constant => string.Equals(constant.Name, name, StringComparison.OrdinalIgnoreCase))))
                throw new BridgeException("ObjectExists", "A tag or constant already uses the requested name in the software scope.");
        }

        private void PrepareTagCreate(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            change.ObjectNames = new[] { MutationObjectName(change, plc, "tagTable") };
            PrepareMutationScope(change, plc, "tagTable", change.ObjectNames);
            var table = MutationTable(change, plc);
            TagCreate(change, plc, table, false);
            string path = Path.Combine(change.Directory, "before-tags.xml");
            table.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
            change.Backups.Add(new JObject { ["tableName"] = table.Name, ["artifact"] = ChangeArtifact(path) });
            string detail = change.Kind == "constant_create" ? "value" : "logicalAddress";
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = change.Kind, ["tableName"] = table.Name, ["name"] = change.Payload["name"], ["dataTypeName"] = change.Payload["dataTypeName"], [detail] = change.Payload[detail] });
        }

        // Prepare and apply share this so apply repeats the scope-wide collision check immediately before creation.
        private void TagCreate(EngineeringChange change, PlcSelection plc, PlcTagTable table, bool apply)
        {
            bool constant = change.Kind == "constant_create";
            string name = ChangeText(change.Payload, "name", 128), type = ChangeText(change.Payload, "dataTypeName", 256), detail = ChangeText(change.Payload, constant ? "value" : "logicalAddress", 1024);
            RejectTagNameCollision(change, plc, name, null);
            if (!apply) return;
            if (constant)
            {
                table.UserConstants.Create(name, type, detail);
                var created = table.UserConstants.Find(name) ?? throw new BridgeException("MutationVerificationFailed", "Created user constant is missing from its table.");
                if (created.DataTypeName != type || created.Value != detail) throw new BridgeException("MutationVerificationFailed", "Created user constant read-back differs from the request.");
            }
            else
            {
                table.Tags.Create(name, type, detail);
                var created = table.Tags.Find(name) ?? throw new BridgeException("MutationVerificationFailed", "Created tag is missing from its table.");
                if (created.IsSafety) throw new BridgeException("SafetyBoundary", "The created tag is a safety tag; generic tag creation excludes safety tags. Recover from the checkpoint.");
                if (created.DataTypeName != type || created.LogicalAddress != detail) throw new BridgeException("MutationVerificationFailed", "Created tag read-back differs from the request.");
            }
        }

        private static readonly HashSet<string> BlockHeaderAttributes = new HashSet<string>(new[] { "HeaderAuthor", "HeaderFamily", "HeaderName", "HeaderVersion" }, StringComparer.Ordinal);
        // OB names from the V21 Openness manual (OB export attributes, PriorityNumber write access); native attribute infos still decide writability.
        private static readonly HashSet<string> ObWriteAttributes = new HashSet<string>(BlockHeaderAttributes.Concat(new[] { "PriorityNumber", "CyclicTime", "PhaseOffset", "ProcessImagePartNumber" }), StringComparer.Ordinal);
        private static readonly HashSet<string> BlockNumberAttributes = new HashSet<string>(new[] { "AutoNumber", "Number" }, StringComparer.Ordinal);

        private void PrepareBlockEdit(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            var effects = (JArray)preview["effects"];
            change.BlockNames = new[] { MutationBlockName(change.Payload, plc) };
            PrepareMutationScope(change, plc, "block", change.BlockNames);
            PrepareBlockTargets(change, plc, false, true, effects);
            var block = FindBlockByName(plc, change.BlockNames[0], true, change);
            change.AttributeTarget = block.Item;
            effects.Add(new JObject { ["attributes"] = PrepareAttributes(block.Item, BlockEditAttributes(change, plc, block, out var allow), allow) });
        }

        private void ApplyBlockEdit(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            VerifyBlockTargets(change, plc, false);
            var block = FindBlockByName(plc, change.BlockNames[0], true, change);
            ApplyAttributes(block.Item, BlockEditAttributes(change, plc, block, out var allow), allow);
        }

        private static JObject BlockEditAttributes(EngineeringChange change, PlcSelection plc, BlockEntry block, out HashSet<string> allow)
        {
            if (change.Kind == "block_attributes_set")
            {
                var attrs = ChangeObject(change.Payload, "attributes");
                if (attrs.Count == 0) throw new BridgeException("InvalidRequest", "attributes must contain at least one editable field.");
                allow = block.Item is OB ? ObWriteAttributes : BlockHeaderAttributes;
                return attrs;
            }
            long number = change.Payload["number"]?.Type == JTokenType.Integer ? change.Payload.Value<long>("number") : 0;
            if (number < 1 || number > 65535) throw new BridgeException("InvalidRequest", "number must be an integer from 1 to 65535.");
            string kind = BlockNumberKind(block.Item);
            var budget = new ScanBudget(50000);
            if (AllBlocks(plc, budget).Any(item => item.Path != block.Path && item.Item.Number == number && BlockNumberKind(item.Item) == kind))
                throw new BridgeException("BlockNumberCollision", "Another " + kind + " block already uses number " + number + ".");
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Block-number collision check is incomplete.");
            allow = BlockNumberAttributes;
            // Ordered: the explicit Number applies only once auto-numbering is off.
            return new JObject { ["AutoNumber"] = false, ["Number"] = number };
        }

        private JObject CheckDeleteDependencies(EngineeringChange change, PlcSelection plc, IEngineeringObject target, string name)
        {
            try { DependencyRules.RequirePolicy(change.Payload.Value<string>("dependencyPolicy")); }
            catch (ArgumentException ex) { throw new BridgeException("DependencyPolicyRequired", ex.Message); }
            var budget = new ScanBudget(50000);
            var references = new JArray();
            int exported = 0;
            string path = Path.Combine(change.Directory, "dependency-scan.xml");
            string selectedOwnerPath = target is PlcBlock selectedBlock ? ObjectIdentity.ObjectPath(change.GroupPath, selectedBlock.Name, selectedBlock.Namespace) :
                target is PlcType selectedType ? ObjectIdentity.ObjectPath(change.GroupPath, selectedType.Name, selectedType.Namespace) :
                ObjectIdentity.ObjectPath(change.GroupPath, change.ObjectNames[0], "");
            Action<IEngineeringObject, string> inspect = (item, identity) =>
            {
                if ((target is PlcType || target is PlcBlock) && string.Equals(identity, selectedOwnerPath, StringComparison.Ordinal)) return;
                if (++exported > 2000) throw new BridgeException("DependencyCoverageIncomplete", "Deletion dependency scan exceeds 2000 exported objects.");
                try
                {
                    if (item is PlcBlock block) block.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    else if (item is PlcType type) type.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    else ((PlcTagTable)item).Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    var xml = EngineeringSources.ReadXml(File.ReadAllText(path));
                    // Exclude only the selected declaration in its exact native owner.
                    // Duplicate short names in other units/tables remain evidence.
                    if (item is PlcTagTable && (target is PlcTag || target is PlcUserConstant))
                        xml = DependencyRules.WithoutSelectedDeclaration(xml, identity, selectedOwnerPath, target is PlcTag ? "SW.Tags.PlcTag" : "SW.Tags.PlcUserConstant", name);
                    if (DependencyRules.Mentions(xml, name)) references.Add(new JObject { ["objectPath"] = identity, ["evidence"] = "Exported XML contains the symbol as a token; unresolved expressions and comments conservatively count as dependencies." });
                }
                catch (BridgeException) { throw; }
                catch (Exception ex) { throw new BridgeException("DependencyCoverageIncomplete", "Cannot inspect " + identity + ": " + ex.Message); }
                finally { if (File.Exists(path)) File.Delete(path); }
            };
            foreach (var scope in SoftwareScopes(plc, budget))
            {
                foreach (var block in ScopeBlocks(scope, budget)) inspect(block.Item, block.Path);
                foreach (var type in ScopeTypes(scope, budget)) inspect(type.Item, type.Path);
                foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget)) inspect(table.Item, table.Path);
            }
            if (budget.Truncated) throw new BridgeException("DependencyCoverageIncomplete", "The exported dependency index is truncated.");
            var result = new JObject { ["policy"] = "rejectReferenced", ["exportedObjects"] = exported, ["references"] = references,
                ["scope"] = "Exposed offline PLC scopes, blocks, types and tag tables. Conservative token matching; runtime pointer aliases and external consumers cannot be proven absent.", ["cascade"] = false };
            if (references.Count != 0) throw new BridgeException("DependencyExists", "Remove explicit dependencies before deleting this object; no cascade is performed.", result);
            return result;
        }

        private void PrepareGroupEdit(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            string scopePath = change.Payload.Value<string>("scopePath") ?? change.Selector.Value<string>("scopePath") ?? plc.Path;
            if (change.Payload["scopePath"] != null && change.Selector["scopePath"]?.Type == JTokenType.String && scopePath != change.Selector.Value<string>("scopePath"))
                throw new BridgeException("ScopeMismatch", "Top-level and payload scopePath disagree.");
            var scope = ResolveSoftwareScope(plc, scopePath);
            if (scope.Kind == "safetyUnit") throw new BridgeException("SafetyBoundary", "Generic group operations do not mutate safety units.");
            change.ScopePath = scopePath; change.Selector["scopePath"] = scopePath;
            change.GroupPath = ChangeText(change.Payload, "groupPath", 2048); change.Namespace = "";
            GroupEdit(change, plc, false);
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = change.Kind, ["groupKind"] = change.Payload["groupKind"], ["groupPath"] = change.GroupPath, ["name"] = change.Payload["name"] });
            ((JArray)preview["warnings"]).Add("Only native user groups are editable. Deletion requires an empty group; recursive deletion and cross-scope moves are not supported.");
        }

        private void GroupEdit(EngineeringChange change, PlcSelection plc, bool apply)
        {
            string kind = ChangeText(change.Payload, "groupKind", 32);
            bool create = change.Kind == "group_create", delete = change.Kind == "group_delete";
            string name = delete ? null : ChangeText(change.Payload, "name", 128);
            if (delete)
                try { DependencyRules.RequirePolicy(change.Payload.Value<string>("dependencyPolicy")); }
                catch (ArgumentException ex) { throw new BridgeException("DependencyPolicyRequired", ex.Message); }
            Action<int, int> empty = (objects, groups) => { try { DependencyRules.RequireEmpty(objects, groups); } catch (InvalidOperationException ex) { throw new BridgeException("GroupNotEmpty", ex.Message); } };
            if (kind == "blocks")
            {
                var group = MutationBlockGroup(change, plc);
                var parent = group.Parent as PlcBlockGroup;
                if (!create && !(group is PlcBlockUserGroup)) throw new BridgeException("GeneratedObject", "Only user groups may be renamed or deleted.");
                if (!delete && (create ? group.Groups : parent.Groups).Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("ObjectExists", "Sibling group name already exists.");
                if (delete) empty(group.Blocks.Count, group.Groups.Count);
                if (apply)
                {
                    string previous = group.Name;
                    if (create) { if (group.Groups.Create(name).Name != name) throw new BridgeException("MutationVerificationFailed", "Group create failed."); }
                    else if (delete) { ((PlcBlockUserGroup)group).Delete(); if (parent.Groups.Any(item => item.Name == previous)) throw new BridgeException("MutationVerificationFailed", "Deleted group still exists."); }
                    else { ((PlcBlockUserGroup)group).Name = name; if (group.Name != name) throw new BridgeException("MutationVerificationFailed", "Group rename failed."); }
                }
            }
            else if (kind == "types")
            {
                var group = MutationTypeGroup(change, plc);
                var parent = group.Parent as PlcTypeGroup;
                if (!create && !(group is PlcTypeUserGroup)) throw new BridgeException("GeneratedObject", "Only user groups may be renamed or deleted.");
                if (!delete && (create ? group.Groups : parent.Groups).Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("ObjectExists", "Sibling group name already exists.");
                if (delete) empty(group.Types.Count, group.Groups.Count);
                if (apply)
                {
                    string previous = group.Name;
                    if (create) { if (group.Groups.Create(name).Name != name) throw new BridgeException("MutationVerificationFailed", "Group create failed."); }
                    else if (delete) { ((PlcTypeUserGroup)group).Delete(); if (parent.Groups.Any(item => item.Name == previous)) throw new BridgeException("MutationVerificationFailed", "Deleted group still exists."); }
                    else { ((PlcTypeUserGroup)group).Name = name; if (group.Name != name) throw new BridgeException("MutationVerificationFailed", "Group rename failed."); }
                }
            }
            else if (kind == "tags")
            {
                var group = MutationTagGroup(change, plc);
                var parent = group.Parent as PlcTagTableGroup;
                if (!create && !(group is PlcTagTableUserGroup)) throw new BridgeException("GeneratedObject", "Only user groups may be renamed or deleted.");
                if (!delete && (create ? group.Groups : parent.Groups).Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("ObjectExists", "Sibling group name already exists.");
                if (delete) empty(group.TagTables.Count, group.Groups.Count);
                if (apply)
                {
                    string previous = group.Name;
                    if (create) { if (group.Groups.Create(name).Name != name) throw new BridgeException("MutationVerificationFailed", "Group create failed."); }
                    else if (delete) { ((PlcTagTableUserGroup)group).Delete(); if (parent.Groups.Any(item => item.Name == previous)) throw new BridgeException("MutationVerificationFailed", "Deleted group still exists."); }
                    else { ((PlcTagTableUserGroup)group).Name = name; if (group.Name != name) throw new BridgeException("MutationVerificationFailed", "Group rename failed."); }
                }
            }
            else throw new BridgeException("InvalidRequest", "groupKind must be blocks, types or tags.");
        }
    }
}
