using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
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
            // A safety tag takes one edit only, asked for explicitly: a single tag_set that renames it (SafetyTagRenameRules).
            bool rename = SafetyTagRenameRules.Requested(change.Kind, change.Payload);
            if (member is PlcTag tag && tag.IsSafety && !rename)
                throw new BridgeException("SafetyBoundary", "Safety tags are excluded from generic tag edits and deletion, including tags in standard PLC tables. " +
                    "Only a rename is available: a single tag_set with safetyAcknowledged=true and attributes containing only Name.");
            if (rename && member is PlcTag standard && !standard.IsSafety)
                throw new BridgeException("InvalidRequest", "safetyAcknowledged=true selects a safety tag, and " + name + " is a standard tag. Omit safetyAcknowledged.");
            return member ?? throw new BridgeException("ObjectNotFound", "No editable " + (constant ? "user constant" : "tag") + " has the exact memberName. System constants are excluded.");
        }

        private void PrepareTagEdit(EngineeringChange change, JObject preview)
        {
            if (IsTagBatch(change)) { PrepareTagBatch(change, preview); return; }
            var plc = SelectPlc(change.Selector);
            change.ObjectNames = new[] { MutationObjectName(change, plc, "tagTable") };
            PrepareMutationScope(change, plc, "tagTable", change.ObjectNames);
            var table = MutationTable(change, plc);
            var member = MutationTagMember(change, table);
            if (!change.PreflightOnly)
            {
                string path = Path.Combine(change.Directory, "before-tags.xml");
                table.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                change.Backups.Add(new JObject { ["tableName"] = table.Name, ["artifact"] = ChangeArtifact(path) });
            }
            if (change.Kind.EndsWith("_delete", StringComparison.Ordinal))
                preview["dependencies"] = CheckDeleteDependencies(change, plc, member, change.Payload.Value<string>("memberName"));
            else
            {
                var attrs = ChangeObject(change.Payload, "attributes");
                if (attrs.Count == 0) throw new BridgeException("InvalidRequest", "attributes must contain at least one editable field.");
                var allow = TagEditAttributes(change, plc, member, attrs);
                if (attrs["Name"] != null) RejectTagNameCollision(change, plc, ChangeText(attrs, "Name", 128), member);
                if (member is PlcTag safetyTag && safetyTag.IsSafety)
                {
                    preview["safetyBefore"] = ReadSafety(plc.Software);
                    preview["safetyTagRename"] = new JObject { ["memberName"] = safetyTag.Name, ["newName"] = attrs.Value<string>("Name"),
                        ["dataTypeName"] = safetyTag.DataTypeName, ["logicalAddress"] = safetyTag.LogicalAddress };
                    ((JArray)preview["warnings"]).Add("Renames a safety tag. Blocks of the safety program that use it must be compiled again, and the safety program signatures can change. " +
                        "Nothing here validates, accepts or approves the safety program; address and data type are not changed.");
                }
                if (change.Kind == "tag_set" && attrs["LogicalAddress"] != null) RejectFIoAddresses(plc, new[] { ChangeText(attrs, "LogicalAddress", 1024) });
                change.AttributeTarget = member;
                ((JArray)preview["effects"]).Add(new JObject { ["memberName"] = change.Payload["memberName"], ["attributes"] = PrepareAttributes(member, attrs, allow) });
            }
        }

        private void ApplyTagEdit(EngineeringChange change)
        {
            if (IsTagBatch(change)) { ApplyTagBatch(change); return; }
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
            else
            {
                var attrs = ChangeObject(change.Payload, "attributes");
                if (change.Kind == "tag_set" && attrs["LogicalAddress"] != null) RejectFIoAddresses(plc, new[] { ChangeText(attrs, "LogicalAddress", 1024) });
                var safetyTag = member is PlcTag candidate && candidate.IsSafety ? candidate : null;
                string address = safetyTag?.LogicalAddress, dataType = safetyTag?.DataTypeName;
                ApplyAttributes(member, attrs, TagEditAttributes(change, plc, member, attrs));
                if (safetyTag != null)
                {
                    // Read back from the table by the new name: still a safety tag, on the same address with the same data type.
                    string renamedTo = attrs.Value<string>("Name"), previous = change.Payload.Value<string>("memberName");
                    var renamed = table.Tags.Find(renamedTo);
                    if (renamed == null || renamed.Name != renamedTo || (!string.Equals(previous, renamedTo, StringComparison.OrdinalIgnoreCase) && table.Tags.Find(previous) != null))
                        throw new BridgeException("MutationVerificationFailed", "The safety tag did not read back under its new name alone. Recover from the checkpoint.");
                    if (!renamed.IsSafety || renamed.LogicalAddress != address || renamed.DataTypeName != dataType)
                        throw new BridgeException("SafetyVerificationFailed", "The renamed tag is no longer a safety tag on its address with its data type. Recover from the checkpoint.");
                    change.Readbacks.Add(new JObject { ["role"] = "safety-tag-rename", ["memberName"] = previous, ["name"] = renamed.Name,
                        ["dataTypeName"] = renamed.DataTypeName, ["logicalAddress"] = renamed.LogicalAddress, ["isSafety"] = true });
                    change.Stages.Add("safety_tag_rename_verified");
                }
            }
        }

        /// <summary>The attributes a tag or constant edit may set. A safety tag takes a rename only, and only with the safety offline
        /// program writable: its address and data type belong to the F-I/O configuration.</summary>
        private static HashSet<string> TagEditAttributes(EngineeringChange change, PlcSelection plc, IEngineeringObject member, JObject attrs)
        {
            if (!(member is PlcTag tag) || !tag.IsSafety)
                return new HashSet<string>(new[] { "Name", "DataTypeName", change.Kind == "constant_set" ? "Value" : "LogicalAddress" }, StringComparer.Ordinal);
            SafetyTagRenameRules.RequireNameOnly(attrs);
            EnsureSafetyWritable(plc.Software);
            return new HashSet<string>(new[] { "Name" }, StringComparer.Ordinal);
        }

        private static readonly HashSet<string> TagBatchAllow = new HashSet<string>(TagBatchRules.Attributes, StringComparer.Ordinal);
        private sealed class TagBatchTarget { internal TagBatchRules.Item Item; internal string TablePath, TableName; internal PlcTagTable Table; internal PlcTag Tag; }

        // tag_set with payload.items (TagBatchRules): many tag edits under one fingerprint check, checkpoint, compile pair and save.
        private static bool IsTagBatch(EngineeringChange change)
        {
            if (!TagBatchRules.IsBatch(change.Payload)) return false;
            if (change.Kind != "tag_set") throw new BridgeException("InvalidRequest", "payload.items is supported by tag_set only.");
            return true;
        }

        // Resolves every item to its table and tag and runs every refusal without writing. Prepare and apply share it, so apply
        // repeats the safety-tag, name-collision and F-I/O checks for all items immediately before the first write.
        private List<TagBatchTarget> ResolveTagBatch(EngineeringChange change, PlcSelection plc, bool prepare)
        {
            var items = TagBatchRules.Items(change.Payload);
            string payloadScope = change.Payload.Value<string>("scopePath"), selectorScope = change.Selector.Value<string>("scopePath");
            if (!string.IsNullOrWhiteSpace(payloadScope) && !string.IsNullOrWhiteSpace(selectorScope) && payloadScope != selectorScope)
                throw new BridgeException("ScopeMismatch", "Top-level and payload scopePath selectors disagree.");
            string explicitScope = prepare ? (string.IsNullOrWhiteSpace(payloadScope) ? selectorScope : payloadScope) : change.ScopePath;
            var budget = new ScanBudget(50000);
            var tables = new List<Tuple<SoftwareScope, TagTableEntry>>();
            foreach (var scope in SoftwareScopes(plc, budget))
            {
                if (!string.IsNullOrWhiteSpace(explicitScope) && scope.Path != explicitScope) continue;
                foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget)) tables.Add(Tuple.Create(scope, table));
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Tag-table lookup is incomplete; no preview can be prepared.");
            var targets = new List<TagBatchTarget>();
            SoftwareScope selected = null;
            foreach (var item in items)
            {
                // Apply resolves by the table path the preview recorded.
                string path = prepare ? item.TablePath : change.TagBatchTablePaths[item.Index];
                var matches = tables.Where(t => (path == null || t.Item2.Path == path) &&
                    (item.TableName == null || string.Equals(t.Item2.Item.Name, item.TableName, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
                if (matches.Length == 0)
                    throw new BridgeException("ObjectNotFound", "items[" + item.Index + "]: no tag table matches " + (item.TablePath ?? item.TableName) + " in the selected scope. Nothing was written.", new JObject { ["index"] = item.Index });
                if (matches.Length > 1)
                    throw new BridgeException("AmbiguousTarget", "items[" + item.Index + "]: tagTableName " + item.TableName + " matches more than one tag table. Specify scopePath or tagTablePath.", new JObject { ["index"] = item.Index });
                if (selected != null && !ReferenceEquals(selected, matches[0].Item1))
                    throw new BridgeException("ScopeMismatch", "items[" + item.Index + "]: tag tables span multiple software scopes; split the change per scope.", new JObject { ["index"] = item.Index });
                selected = matches[0].Item1;
                var table = matches[0].Item2;
                if (selected.Kind == "safetyUnit") throw new BridgeException("SafetyBoundary", "Safety-unit mutations require the explicit safety import workflow.", new JObject { ["index"] = item.Index });
                if (ObjectIdentity.IsGeneratedPath(table.Path)) throw new BridgeException("GeneratedObject", "Generated system-group objects are not eligible for direct mutation.", new JObject { ["index"] = item.Index });
                var tag = table.Item.Tags.Find(item.MemberName);
                if (tag == null || tag.Name != item.MemberName)
                    throw new BridgeException("ObjectNotFound", "items[" + item.Index + "]: no editable tag has the exact memberName " + item.MemberName + " in " + table.Item.Name + ". Nothing was written.",
                        new JObject { ["index"] = item.Index, ["tagTablePath"] = table.Path, ["memberName"] = item.MemberName });
                if (tag.IsSafety)
                    throw new BridgeException("SafetyBoundary", "items[" + item.Index + "]: " + item.MemberName + " is a safety tag. Safety tags are excluded from generic tag edits, including tags in standard PLC tables; the whole change is refused and nothing was written.",
                        new JObject { ["index"] = item.Index, ["tagTablePath"] = table.Path, ["memberName"] = item.MemberName });
                targets.Add(new TagBatchTarget { Item = item, TablePath = table.Path, TableName = table.Item.Name, Table = table.Item, Tag = tag });
            }
            TagBatchRules.RequireNoCollisions(targets.Select(t => (t.Item, t.TablePath)), ScopeSymbolNames(selected.Tags));
            try { RejectFIoAddresses(plc, targets.Select(t => t.Item.Attributes.Value<string>("LogicalAddress")).Where(address => address != null)); }
            catch (BridgeException refusal) when (refusal.Code == "SafetyBoundary" && refusal.Details?["logicalAddress"] != null)
            {
                // Like every other refusal of a batch, this one names its item.
                var item = targets.First(t => t.Item.Attributes.Value<string>("LogicalAddress") == refusal.Details.Value<string>("logicalAddress")).Item;
                var details = (JObject)refusal.Details.DeepClone();
                details["index"] = item.Index; details["memberName"] = item.MemberName;
                throw new BridgeException("SafetyBoundary", "items[" + item.Index + "]: " + refusal.Message, details);
            }
            if (prepare) change.ScopePath = selected.Path;
            return targets;
        }

        private void PrepareTagBatch(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            var targets = ResolveTagBatch(change, plc, true);
            change.Namespace = "";
            change.GroupPath = change.ScopePath + "/tags";
            change.Selector["scopePath"] = change.ScopePath;
            change.ObjectNames = targets.Select(t => t.TableName).Distinct(StringComparer.Ordinal).ToArray();
            change.TagBatchTablePaths = targets.Select(t => t.TablePath).ToArray();
            int number = 0;
            foreach (var table in targets.GroupBy(t => t.TablePath, StringComparer.Ordinal))
            {
                string path = Path.Combine(change.Directory, "before-tags-" + number++ + ".xml");
                table.First().Table.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                change.Backups.Add(new JObject { ["tableName"] = table.First().TableName, ["tablePath"] = table.Key, ["artifact"] = ChangeArtifact(path) });
            }
            // Every item is listed; PrepareAttributes also refuses an unwritable attribute or an unconvertible value here.
            preview["items"] = new JArray(targets.Select(t => new JObject { ["index"] = t.Item.Index, ["tagTableName"] = t.TableName, ["tagTablePath"] = t.TablePath,
                ["memberName"] = t.Item.MemberName, ["attributes"] = PrepareAttributes(t.Tag, t.Item.Attributes, TagBatchAllow) }));
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = "tag_set", ["itemCount"] = targets.Count, ["renames"] = targets.Count(t => t.Item.NewName != null),
                ["tagTables"] = new JArray(change.ObjectNames), ["items"] = "preview.items lists every edit." });
            ((JArray)preview["warnings"]).Add("All " + targets.Count + " edits share one checkpoint archive, one compile and one save. A refusal on any item refuses the whole change before a write; " +
                "a native failure after the first write leaves the whole change recovery_required.");
            change.TagBatchTargets = targets;
        }

        private void ApplyTagBatch(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var targets = ResolveTagBatch(change, plc, false);
            // Every item's attributes are preflighted (declared writable, convertible) before the first one is written.
            foreach (var target in targets) PrepareAttributes(target.Tag, target.Item.Attributes, TagBatchAllow);
            foreach (var target in targets) ApplyAttributes(target.Tag, target.Item.Attributes, TagBatchAllow);
            VerifyTagBatch(change, "after-mutation");
            change.Stages.Add("tag_batch_readback_verified");
        }

        // Reads every item back by its final name from a fresh table lookup: the tag exists under that name, a renamed tag's
        // old name is gone, it is no safety tag and each requested attribute holds the requested value.
        private void VerifyTagBatch(EngineeringChange change, string stage)
        {
            var plc = SelectPlc(change.Selector);
            var budget = new ScanBudget(50000);
            var tables = TagTables(ResolveSoftwareScope(plc, change.ScopePath).Tags, change.ScopePath + "/tags", budget).ToDictionary(t => t.Path, t => t.Item, StringComparer.Ordinal);
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Tag-table readback is incomplete.");
            var rows = new JArray();
            foreach (var item in TagBatchRules.Items(change.Payload))
            {
                string tablePath = change.TagBatchTablePaths[item.Index];
                var details = new JObject { ["index"] = item.Index, ["tagTablePath"] = tablePath, ["memberName"] = item.MemberName, ["name"] = item.FinalName, ["stage"] = stage };
                if (!tables.TryGetValue(tablePath, out var table)) throw new BridgeException("MutationVerificationFailed", "items[" + item.Index + "]: the tag table is missing at readback.", details);
                var tag = table.Tags.Find(item.FinalName);
                if (tag == null || tag.Name != item.FinalName)
                    throw new BridgeException("MutationVerificationFailed", "items[" + item.Index + "]: no tag named " + item.FinalName + " exists at readback.", details);
                if (!string.Equals(item.FinalName, item.MemberName, StringComparison.OrdinalIgnoreCase) && table.Tags.Find(item.MemberName) != null)
                    throw new BridgeException("MutationVerificationFailed", "items[" + item.Index + "]: the renamed tag's old name " + item.MemberName + " still exists at readback.", details);
                if (tag.IsSafety) throw new BridgeException("SafetyBoundary", "items[" + item.Index + "]: " + item.FinalName + " is a safety tag after the edit; generic tag edits exclude safety tags. Recover from the checkpoint.", details);
                var actual = new JObject();
                foreach (var property in item.Attributes.Properties())
                {
                    object value = ReadAttributeValue(tag, property.Name, TagBatchAllow, false, out var type);
                    actual[property.Name] = ChangeScalar(value);
                    if (!JToken.DeepEquals(ChangeScalar(ConvertChangeValue(property.Value, type)), actual[property.Name]))
                        throw new BridgeException("MutationVerificationFailed", "items[" + item.Index + "]: attribute " + property.Name + " of " + item.FinalName + " differs from the request at readback.", details);
                }
                rows.Add(new JObject { ["index"] = item.Index, ["tagTablePath"] = tablePath, ["memberName"] = item.MemberName, ["name"] = tag.Name, ["attributes"] = actual });
            }
            change.Readbacks.Add(new JObject { ["role"] = "tag-batch-readback", ["stage"] = stage, ["verifiedItems"] = rows.Count, ["items"] = rows });
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
            if (!change.PreflightOnly)
            {
                string path = Path.Combine(change.Directory, "before-tags.xml");
                table.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                change.Backups.Add(new JObject { ["tableName"] = table.Name, ["artifact"] = ChangeArtifact(path) });
            }
            string detail = change.Kind == "constant_create" ? "value" : "logicalAddress";
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = change.Kind, ["tableName"] = table.Name, ["name"] = change.Payload["name"], ["dataTypeName"] = change.Payload["dataTypeName"], [detail] = change.Payload[detail] });
        }

        // Prepare and apply share this so apply repeats the scope-wide collision check immediately before creation.
        private void TagCreate(EngineeringChange change, PlcSelection plc, PlcTagTable table, bool apply)
        {
            bool constant = change.Kind == "constant_create";
            string name = ChangeText(change.Payload, "name", 128), type = ChangeText(change.Payload, "dataTypeName", 256), detail = ChangeText(change.Payload, constant ? "value" : "logicalAddress", 1024);
            RejectTagNameCollision(change, plc, name, null);
            // Before creating anything (native run sl3 created the tag, then refused it as a safety tag).
            if (!constant) RejectFIoAddresses(plc, new[] { detail });
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
                // TIA may return the absolute address with a leading % and normalized case.
                if (created.DataTypeName != type || !string.Equals(created.LogicalAddress?.TrimStart('%'), detail.TrimStart('%'), StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("MutationVerificationFailed", "Created tag read-back differs from the request.");
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
            change.BlockNumberKey = "blockNumber\n" + kind + "\n" + number;
            allow = BlockNumberAttributes;
            // Ordered: the explicit Number applies only once auto-numbering is off.
            return new JObject { ["AutoNumber"] = false, ["Number"] = number };
        }

        // IEC edge FBs that TIA copies know-how protected into System blocks/Program resources on first use (wave 10 native slot).
        // Timers and counters stay out: their single-instance DBs are typed by the data types IEC_TIMER/IEC_COUNTER, not by an FB.
        // TIA deletes an unreferenced R_TRIG/F_TRIG instance DB at compile (native 28 October, created after an R_TRIG use), so
        // system FBs are refused: their global storage has to be created together with its call.
        private static readonly string[] SystemInstanceFbs = { "R_TRIG", "F_TRIG" };

        private void PrepareInstanceDbCreate(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            string name = ChangeText(change.Payload, "name", 128), requested = ChangeText(change.Payload, "instanceOfName", 128);
            int number = InstanceDbNumber(change.Payload);
            var budget = new ScanBudget(50000);
            var blocks = AllBlocks(plc, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Instance DB collision check is incomplete.");
            if (blocks.Any(block => string.Equals(block.Item.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("ObjectExists", "A block of this PLC already has the requested name.");
            if (number != 0 && blocks.Any(block => block.Item.Number == number && BlockNumberKind(block.Item) == "DB")) throw new BridgeException("BlockNumberCollision", "Another DB already uses number " + number + ".");
            change.BlockNames = new[] { name };
            PrepareMutationScope(change, plc, "block", change.BlockNames);
            var scope = ResolveSoftwareScope(plc, change.ScopePath);
            if (scope.Unit != null) throw new BridgeException("UnsupportedScope", "CreateInstanceDB takes no namespace; instance DBs are created only in the standard PLC program scope.");
            var fbs = ScopeBlocks(scope, budget).Where(block => block.Item is FB && string.Equals(block.Item.Name, requested, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "instanceOfName lookup is incomplete.");
            if (fbs.Length > 1) throw new BridgeException("AmbiguousTarget", "instanceOfName matches more than one FB.");
            var fb = fbs.FirstOrDefault();
            if (SystemInstanceFbs.Any(item => string.Equals(item, requested, StringComparison.OrdinalIgnoreCase)))
                throw new BridgeException("UnsupportedCapability", "TIA deletes an unreferenced " + requested.ToUpperInvariant() + " instance DB when it compiles; create such storage together with its call.");
            if (fb == null) throw new BridgeException("BlockNotFound", "instanceOfName must name one FB of the selected scope.");
            if (fb.NativeSystemGroup) throw new BridgeException("GeneratedObject", "FBs in generated system groups cannot be instantiated here.");
            EnsureEditableBlock(fb.Item, false);
            change.Payload["instanceOfName"] = fb.Item.Name;
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = "create", ["blockType"] = "InstanceDB", ["blockName"] = name, ["groupPath"] = change.GroupPath,
                ["number"] = number == 0 ? (JToken)"auto" : number, ["instanceOfName"] = change.Payload["instanceOfName"], ["instanceOfPath"] = fb.Path });
        }

        private void ApplyInstanceDbCreate(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            int number = InstanceDbNumber(change.Payload);
            // The number argument is ignored when auto-numbered; 1 is only a valid placeholder.
            MutationBlockGroup(change, plc).Blocks.CreateInstanceDB(change.BlockNames[0], number == 0, number == 0 ? 1 : number, change.Payload.Value<string>("instanceOfName"));
            var db = VerifyInstanceDb(change, plc);
            change.Readbacks.Add(new JObject { ["role"] = "instance-db", ["name"] = db.Name, ["number"] = db.Number, ["instanceOfName"] = db.InstanceOfName });
        }

        private InstanceDB VerifyInstanceDb(EngineeringChange change, PlcSelection plc)
        {
            var db = FindBlockByName(plc, change.BlockNames[0], true, change).Item as InstanceDB;
            int number = InstanceDbNumber(change.Payload);
            if (db == null || db.InstanceOfName != change.Payload.Value<string>("instanceOfName") || (number != 0 && db.Number != number))
                throw new BridgeException("MutationVerificationFailed", "Created instance DB read-back differs from the request.");
            return db;
        }

        private static int InstanceDbNumber(JObject payload)
        {
            if (payload["number"] == null) return 0;
            long number = payload["number"].Type == JTokenType.Integer ? payload.Value<long>("number") : 0;
            if (number < 1 || number > 65535) throw new BridgeException("InvalidRequest", "number must be an integer from 1 to 65535.");
            return (int)number;
        }

        private JObject CheckDeleteDependencies(EngineeringChange change, PlcSelection plc, IEngineeringObject target, string name)
        {
            try { DependencyRules.RequirePolicy(change.Payload.Value<string>("dependencyPolicy")); }
            catch (ArgumentException ex) { throw new BridgeException("DependencyPolicyRequired", ex.Message); }
            var budget = new ScanBudget(50000);
            var references = new JArray();
            var crossReferenced = new JArray();
            var generated = GeneratedSafetyBlockNames(plc);
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
                    if (item is PlcBlock block)
                    {
                        try { block.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None); }
                        catch (Exception refused) when (!(refused is BridgeException) && (ObjectIdentity.IsGeneratedPath(identity) || generated.Contains(block.Name)))
                        {
                            // TIA refuses to export the blocks it generates for a safety program: the F-OB of each runtime group, which it reports
                            // as a know-how protected SCL OB, and the blocks in its system block groups. Such a block is checked through its own
                            // native cross-references instead; without them the scan stays incomplete. Any other block whose export is refused
                            // (a user's protected or inconsistent block, an I/O failure) keeps the refusal below.
                            var checkedBlock = BlockCrossReferences(block, target, name, identity, refused);
                            crossReferenced.Add(checkedBlock);
                            if (checkedBlock.Value<bool>("referencesSymbol")) references.Add(new JObject { ["objectPath"] = identity, ["evidence"] = "TIA refuses to export this block; its native cross-references list the symbol." });
                            return;
                        }
                        catch (Exception refused) when (!(refused is BridgeException))
                        {
                            throw new BridgeException("DependencyCoverageIncomplete", "Cannot inspect " + identity + ": " + refused.Message + " " + BlockTraits(block));
                        }
                    }
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
            var result = new JObject { ["policy"] = "rejectReferenced", ["exportedObjects"] = exported, ["references"] = references, ["crossReferencedBlocks"] = crossReferenced,
                ["scope"] = "Exposed offline PLC scopes, blocks, types and tag tables. Conservative token matching; runtime pointer aliases and external consumers cannot be proven absent.", ["cascade"] = false };
            if (references.Count != 0) throw new BridgeException("DependencyExists", "Remove explicit dependencies before deleting this object; no cascade is performed.", result);
            return result;
        }

        // The F-OB of every safety runtime group, as the safety administration names it; empty without one.
        private HashSet<string> GeneratedSafetyBlockNames(PlcSelection plc)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var safety = SafetyAdministrationFor(plc.Software);
                if (safety != null) foreach (var group in safety.RuntimeGroups) if (!string.IsNullOrEmpty(group.FOBName)) names.Add(group.FOBName);
            }
            catch (Exception) { names.Clear(); }
            return names;
        }

        // What TIA reports for a block whose export is refused, so a refusal names why the cross-reference fallback did not apply.
        private static string BlockTraits(PlcBlock block)
        {
            string Read(Func<object> read) { try { return Convert.ToString(read()); } catch (Exception ex) { return "unreadable (" + ex.GetType().Name + ")"; } }
            return "(programming language " + Read(() => block.ProgrammingLanguage) + ", know-how protected " + Read(() => block.IsKnowHowProtected) +
                ", consistent " + Read(() => block.IsConsistent) + "; only blocks TIA generates for a safety program are checked through their cross-references.)";
        }

        private static string RefusalText(Exception refusal) =>
            string.Join(" ", (refusal?.Message ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));

        // A block TIA will not export: its CrossReferenceService lists the objects it uses. A reference to the target object, or to an
        // object or location with the target's name, counts as a dependency (conservative). No service, a failure, no source at all or
        // an over-deep reference tree leaves the scan incomplete. The entry records what the service returned.
        private static JObject BlockCrossReferences(PlcBlock block, IEngineeringObject target, string name, string identity, Exception exportRefusal)
        {
            string refused = RefusalText(exportRefusal);
            CrossReferenceService service;
            try { service = block.GetService<CrossReferenceService>(); }
            catch (Exception ex) { throw new BridgeException("DependencyCoverageIncomplete", "Cannot inspect " + identity + ": its export is refused (" + refused + ") and its cross-references are unavailable: " + ex.Message); }
            if (service == null)
                throw new BridgeException("DependencyCoverageIncomplete", "Cannot inspect " + identity + ": its export is refused (" + refused + ") and it has no native cross-reference service.");
            CrossReferenceResult native;
            try { native = service.GetCrossReferences(CrossReferenceFilter.AllObjects); }
            catch (Exception ex) { throw new BridgeException("DependencyCoverageIncomplete", "Cannot inspect " + identity + ": its export is refused (" + refused + ") and its cross-references failed: " + ex.Message); }
            bool Same(string text) => string.Equals(text, name, StringComparison.OrdinalIgnoreCase) || string.Equals((text ?? "").Trim('"'), name, StringComparison.OrdinalIgnoreCase);
            int sources = 0, referenceCount = 0;
            bool used = false;
            var names = new SortedSet<string>(StringComparer.Ordinal);
            void Walk(IEnumerable<SourceObject> level, int depth)
            {
                if (depth > 64) throw new BridgeException("DependencyCoverageIncomplete", "Cannot inspect " + identity + ": its cross-reference tree is deeper than 64 levels.");
                foreach (var source in level)
                {
                    sources++;
                    foreach (var reference in source.References)
                    {
                        referenceCount++;
                        if (names.Count < 50 && !string.IsNullOrEmpty(reference.Name)) names.Add(reference.Name);
                        if (Equals(reference.UnderlyingObject, target) || Same(reference.Name) || reference.Locations.Any(location => Same(location.ReferencedAsName))) used = true;
                    }
                    Walk(source.Children, depth + 1);
                }
            }
            Walk(native.Sources, 0);
            if (sources == 0)
                throw new BridgeException("DependencyCoverageIncomplete", "Cannot inspect " + identity + ": its export is refused (" + refused + ") and its cross-references name no source.");
            JToken Read(Func<object> read) { try { return JToken.FromObject(read()); } catch (Exception) { return null; } }
            return new JObject { ["objectPath"] = identity, ["exportRefused"] = refused, ["programmingLanguage"] = Read(() => block.ProgrammingLanguage.ToString()),
                ["isKnowHowProtected"] = Read(() => block.IsKnowHowProtected), ["isConsistent"] = Read(() => block.IsConsistent),
                ["sources"] = sources, ["references"] = referenceCount,
                ["referencedNames"] = new JArray(names), ["referencesSymbol"] = used };
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
