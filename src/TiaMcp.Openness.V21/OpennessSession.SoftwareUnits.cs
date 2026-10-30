using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Native evidence (library lane C, 27 October 2026): unit blocks/types carry UnitAccessType in the attribute "Access".
        private static readonly HashSet<string> UnitAccessAttributes = new HashSet<string>(new[] { "Access" }, StringComparer.Ordinal);

        // preview == null applies after repeating every prepare-time check against the current project.
        private void SoftwareUnitEdit(EngineeringChange change, JObject preview)
        {
            var plc = SelectPlc(change.Selector);
            var payload = change.Payload;
            var provider = plc.Software.GetService<PlcUnitProvider>() ?? throw new BridgeException("UnsupportedCapability", "The selected PLC exposes no PlcUnitProvider.");
            var budget = new ScanBudget(50000);
            var scopes = SoftwareScopes(plc, budget);
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Software-unit lookup is incomplete.");
            var effects = preview == null ? new JArray() : (JArray)preview["effects"];
            Func<string, SoftwareScope> unit = key =>
            {
                string path = ChangeText(payload, key, 4096);
                var scope = scopes.SingleOrDefault(item => item.Path == path) ?? throw new BridgeException("ScopeNotFound", key + " matches no exposed unit of the selected PLC.");
                if (scope.Kind == "safetyUnit") throw new BridgeException("SafetyBoundary", "Software-unit lifecycle changes never change safety units.");
                if (scope.Kind != "softwareUnit") throw new BridgeException("InvalidRequest", key + " must be an exact software-unit path.");
                return scope;
            };
            Func<SoftwareScope, string, PlcUnitRelation[]> relationsTo = (owner, name) => owner.Unit.Relations
                .Where(relation => relation.RelationType == UnitRelationType.SoftwareUnit && string.Equals(Convert.ToString(relation.RelatedObject), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            Func<SoftwareScope, string> newName = self =>
            {
                string name = ChangeText(payload, "name", 128);
                if (scopes.Any(scope => scope.Unit != null && scope != self && string.Equals(scope.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new BridgeException("ObjectExists", "A software or safety unit already uses this name.");
                return name;
            };
            // V21 relations name units by text, so every unit relation naming the target is a reference to it.
            Func<SoftwareScope, JObject> references = target => new JObject { ["policy"] = "rejectReferenced", ["cascade"] = false,
                ["references"] = new JArray(scopes.Where(scope => scope.Unit != null && scope.Path != target.Path)
                    .SelectMany(scope => scope.Unit.Relations.Where(relation => string.Equals(Convert.ToString(relation.RelatedObject), target.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(relation => new JObject { ["unitPath"] = scope.Path, ["relationType"] = relation.RelationType.ToString(), ["relatedObject"] = Convert.ToString(relation.RelatedObject) }))),
                ["scope"] = "Native relations of every exposed software and safety unit of the selected PLC." };
            switch (change.Kind)
            {
                case "software_unit_create":
                {
                    string name = newName(null);
                    string preset = payload["namespacePreset"] == null ? null : ChangeText(payload, "namespacePreset", 128);
                    if (preview != null) { effects.Add(new JObject { ["createSoftwareUnit"] = name, ["unitPath"] = plc.Path + "/units/" + Segment(name), ["namespacePreset"] = preset }); break; }
                    var created = provider.UnitGroup.Units.Create(name);
                    if (preset != null) created.NamespacePreset = preset;
                    var found = provider.UnitGroup.Units.Find(name);
                    if (found == null || found.Name != name || preset != null && found.NamespacePreset != preset)
                        throw new BridgeException("MutationVerificationFailed", "The created software unit did not read back with the requested name and namespace preset.");
                    break;
                }
                case "software_unit_relation_set":
                {
                    var owner = unit("unitPath");
                    var related = unit("relatedUnitPath");
                    if (ChangeText(payload, "relationType", 32) != "SoftwareUnit")
                        throw new BridgeException("UnsupportedRelationType", "Only SoftwareUnit relations between two exact software units are supported; NonUnitDB and TODB relations are not.");
                    string action = ChangeText(payload, "action", 16);
                    if (action != "add" && action != "remove") throw new BridgeException("InvalidRequest", "action must be add or remove.");
                    var existing = relationsTo(owner, related.Name);
                    if (action == "remove" && existing.Length != 1) throw new BridgeException("ObjectNotFound", "Exactly one SoftwareUnit relation to relatedUnitPath must exist.");
                    if (action == "add" && existing.Length != 0) throw new BridgeException("ObjectExists", "The SoftwareUnit relation already exists.");
                    if (action == "add" && UnitReaches(scopes, related, owner.Name))
                        throw new BridgeException("RelationCycle", "The relation would close a SoftwareUnit relation cycle (self-relations included); cycles are refused.");
                    change.ScopePath = owner.Path;
                    if (preview != null) { effects.Add(new JObject { ["unitPath"] = owner.Path, ["relatedUnitPath"] = related.Path, ["relatedObject"] = related.Name, ["relationType"] = "SoftwareUnit", ["action"] = action }); break; }
                    if (action == "add") owner.Unit.Relations.Create(related.Name, UnitRelationType.SoftwareUnit); else existing[0].Delete();
                    if (relationsTo(owner, related.Name).Length != (action == "add" ? 1 : 0))
                        throw new BridgeException("MutationVerificationFailed", "The SoftwareUnit relation did not read back as requested.");
                    break;
                }
                case "software_unit_publication_set":
                {
                    var selector = (JObject)change.Selector.DeepClone();
                    selector["plcPath"] = plc.Path; selector["objectPath"] = ChangeText(payload, "objectPath", 4096);
                    var target = ResolveObjectTarget(selector);
                    string kind = target.Identity.Value<string>("kind");
                    if (target.Scope?.Kind == "safetyUnit") throw new BridgeException("SafetyBoundary", "Objects in safety units are excluded.");
                    if (target.Scope?.Kind != "softwareUnit" || (kind != "block" && kind != "type"))
                        throw new BridgeException("InvalidRequest", "objectPath must name one exact block or PLC data type inside a software unit.");
                    if (ObjectIdentity.IsGeneratedPath(target.Identity.Value<string>("objectPath"))) throw new BridgeException("GeneratedObject", "Generated system-group objects are excluded.");
                    if (target.Target is OB) throw new BridgeException("UnsupportedTarget", "Organization blocks cannot be published.");
                    if (target.Target is PlcBlock block) EnsureEditableBlock(block, false);
                    if (target.Target is PlcType type && type.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "Protected data types cannot be changed.");
                    var attributes = ChangeObject(payload, "attributes");
                    var attributeChanges = PrepareAttributes(target.Target, attributes, UnitAccessAttributes);
                    if (attributeChanges.Count != 1 || attributeChanges[0].Value<string>("type") != typeof(UnitAccessType).FullName)
                        throw new BridgeException("UnsupportedAttribute", "attributes must contain exactly Access with the native UnitAccessType value Published or Unpublished.");
                    if (preview == null) { ApplyAttributes(target.Target, attributes, UnitAccessAttributes); break; }
                    change.AttributeTarget = target.Target;
                    change.ScopePath = target.ScopePath;
                    preview["attributeChanges"] = attributeChanges;
                    effects.Add(new JObject { ["objectPath"] = target.Identity["objectPath"], ["kind"] = kind });
                    break;
                }
                case "software_unit_delete":
                {
                    var target = unit("unitPath");
                    if (payload["dependencyPolicy"] == null) payload["dependencyPolicy"] = "rejectReferenced";
                    try { DependencyRules.RequirePolicy(payload.Value<string>("dependencyPolicy")); }
                    catch (ArgumentException ex) { throw new BridgeException("DependencyPolicyRequired", ex.Message); }
                    var tables = TagTables(target.Tags, target.Path + "/tags", budget).ToArray();
                    int members = ScopeBlocks(target, budget).Count() + ScopeTypes(target, budget).Count()
                        + tables.Sum(table => table.Item.Tags.Count + table.Item.UserConstants.Count + table.Item.SystemConstants.Count)
                        + target.Unit.ExternalSourceGroup.ExternalSources.Count + target.Unit.ExternalSourceGroup.Groups.Count
                        + (target.Unit.PlcAlarmTextlistGroup?.PlcAlarmUserTextlists.Count ?? 0)
                        + EngineeringGroupPaths(target.Blocks, "", 0).Concat(EngineeringGroupPaths(target.Types, "", 0)).Concat(EngineeringGroupPaths(target.Tags, "", 0)).Count() - 3;
                    if (budget.Truncated) throw new BridgeException("TraversalLimit", "Unit content check is incomplete.");
                    if (members != 0) throw new BridgeException("UnitNotEmpty", "Only an empty software unit can be deleted; delete or move its blocks, types, tags, user groups, external sources and user text lists explicitly first.");
                    var dependencies = references(target);
                    dependencies["scope"] = dependencies.Value<string>("scope") + " The unit is empty, so none of its members can be referenced.";
                    if (dependencies["references"].HasValues) throw new BridgeException("DependencyExists", "Remove the relations that point to this unit before deleting it; no cascade is performed.", dependencies);
                    change.ScopePath = target.Path;
                    if (preview != null)
                    {
                        preview["dependencies"] = dependencies;
                        effects.Add(new JObject { ["deleteSoftwareUnit"] = target.Path, ["namespacePreset"] = target.Unit.NamespacePreset,
                            ["deletedEmptyTagTables"] = new JArray(tables.Select(table => table.Path)),
                            ["deletedOutgoingRelations"] = new JArray(target.Unit.Relations.Select(relation => new JObject { ["relationType"] = relation.RelationType.ToString(), ["relatedObject"] = Convert.ToString(relation.RelatedObject) })) });
                        break;
                    }
                    ((PlcUnit)target.Unit).Delete();
                    if (provider.UnitGroup.Units.Find(target.Name) != null) throw new BridgeException("MutationVerificationFailed", "The deleted software unit still exists.");
                    break;
                }
                case "software_unit_rename":
                {
                    var target = unit("unitPath");
                    string name = newName(target);
                    if (name == target.Name) throw new BridgeException("InvalidRequest", "name equals the unit's current name.");
                    var dependencies = references(target);
                    // Whether TIA rewrites the name in other units' relations is not established, so a referenced unit is refused.
                    if (dependencies["references"].HasValues) throw new BridgeException("DependencyExists", "Remove the relations that name this unit before renaming it.", dependencies);
                    change.ScopePath = target.Path;
                    if (preview != null) { preview["dependencies"] = dependencies; effects.Add(new JObject { ["renameSoftwareUnit"] = target.Path, ["name"] = name, ["unitPath"] = plc.Path + "/units/" + Segment(name) }); break; }
                    target.Unit.Name = name;
                    if (target.Unit.Name != name || !Equals(provider.UnitGroup.Units.Find(name), target.Unit))
                        throw new BridgeException("MutationVerificationFailed", "The renamed software unit did not read back with the requested name.");
                    break;
                }
                case "software_unit_create_from":
                {
                    string name = newName(null);
                    string conflict = payload["onNameConflict"] == null ? "refuse" : ChangeText(payload, "onNameConflict", 16);
                    if (conflict != "refuse" && conflict != "rename") throw new BridgeException("InvalidRequest", "onNameConflict must be refuse or rename.");
                    var master = ResolveLibraryMaster(ResolveLibrary(ChangeText(payload, "libraryId", 64)), ChangeText(payload, "masterCopyPath", 4096));
                    var contents = master.ContentDescriptions.ToArray();
                    var templates = contents.Where(content => content.ContentType?.FullName == typeof(PlcUnit).FullName).ToArray();
                    if (templates.Length != 1) throw new BridgeException("TargetIncompatible", "masterCopyPath must name a master copy holding exactly one software unit.");
                    string template = templates[0].ContentName;
                    bool renamed = scopes.Any(scope => scope.Unit != null && string.Equals(scope.Name, template, StringComparison.OrdinalIgnoreCase));
                    if (renamed && conflict == "refuse")
                        throw new BridgeException("ObjectExists", "The master copy's unit " + template + " already exists in the PLC; onNameConflict=rename inserts it with MasterCopyMode.Rename and then renames it to name.");
                    var source = new JObject { ["masterCopyPath"] = payload["masterCopyPath"], ["masterCopy"] = master.Name, ["creationDate"] = master.CreationDate.ToString("O"),
                        ["contents"] = new JArray(contents.Select(content => new JObject { ["name"] = content.ContentName, ["type"] = content.ContentType?.FullName })) };
                    var mode = renamed ? MasterCopyMode.Rename : MasterCopyMode.ThrowIfExists;
                    if (preview != null)
                    {
                        effects.Add(new JObject { ["createSoftwareUnitFrom"] = source, ["templateUnit"] = template, ["masterCopyMode"] = mode.ToString(), ["name"] = name, ["unitPath"] = plc.Path + "/units/" + Segment(name) });
                        break;
                    }
                    if (!JToken.DeepEquals(source, change.Preview["effects"][0]["createSoftwareUnitFrom"])) throw new BridgeException("StaleRevision", "The master copy changed since preparation; no write was applied.");
                    Func<SoftwareScope, ScanBudget, IEnumerable<Tuple<string, string>>> memberNames = (scope, scan) => ScopeBlocks(scope, scan).Select(block => Tuple.Create("block", block.Item.Name))
                        .Concat(ScopeTypes(scope, scan).Select(type => Tuple.Create("type", type.Item.Name))).Concat(TagTables(scope.Tags, scope.Path + "/tags", scan).Select(table => Tuple.Create("tagTable", table.Item.Name)));
                    var existing = new HashSet<string>(scopes.SelectMany(scope => memberNames(scope, budget)).Select(member => member.Item2), StringComparer.Ordinal);
                    if (budget.Truncated) throw new BridgeException("TraversalLimit", "Member lookup before CreateFrom is incomplete.");
                    var before = provider.UnitGroup.Units.ToList();
                    var created = provider.UnitGroup.Units.CreateFrom(master, mode);
                    if (created == null || before.Contains(created)) throw new BridgeException("MutationVerificationFailed", "CreateFrom did not return a new software unit.");
                    if (created.Name != name) created.Name = name;
                    var readback = new ScanBudget(50000);
                    var after = SoftwareScopes(plc, readback).SingleOrDefault(scope => Equals(scope.Unit, created));
                    var members = after == null ? new List<Tuple<string, string>>() : memberNames(after, readback).ToList();
                    if (readback.Truncated) throw new BridgeException("TraversalLimit", "The created unit's readback is incomplete; checkpoint recovery is required.");
                    if (after == null || created.Name != name || !Equals(provider.UnitGroup.Units.Find(name), created))
                        throw new BridgeException("MutationVerificationFailed", "The created software unit did not read back with the requested name.");
                    // The master copy describes only the unit, and MasterCopyMode.Rename also renames conflicting members (native, 30 October 2026:
                    // block UnitFb -> UnitFb_1, Default tag table_3 -> _4), so TIA's "<existing>_<n>" names are reported; renamed blocks or types refuse.
                    var renamedMembers = mode == MasterCopyMode.Rename ? members.Where(member => existing.Contains(Regex.Replace(member.Item2, "_[0-9]+$", ""))
                        && !existing.Contains(member.Item2)).ToList() : new List<Tuple<string, string>>();
                    change.Readbacks.Add(new JObject { ["role"] = "createdUnitMembers", ["unitPath"] = after.Path,
                        ["members"] = new JArray(members.Select(member => new JObject { ["kind"] = member.Item1, ["name"] = member.Item2 })),
                        ["renamedByMasterCopyMode"] = new JArray(renamedMembers.Select(member => new JObject { ["kind"] = member.Item1, ["name"] = member.Item2 })) });
                    break;
                }
            }
            if (preview != null) ((JArray)preview["warnings"]).Add("Unit membership, relations and publication change cross-unit visibility. compile=true reports affected consumers; recovery uses the whole-project checkpoint.");
        }

        // Checked after the compile: a refused copy's uncompiled blocks would leave no change fingerprint for change_restore
        // (native, 30 October 2026: FingerprintUnavailable on UnitFb_1).
        private static void RequireUnrenamedUnitMembers(EngineeringChange change)
        {
            var renamed = change.Readbacks.Where(row => row.Value<string>("role") == "createdUnitMembers").SelectMany(row => row["renamedByMasterCopyMode"])
                .Where(member => member.Value<string>("kind") != "tagTable").Select(member => member.Value<string>("name")).ToArray();
            if (renamed.Length != 0)
                throw new BridgeException("MutationVerificationFailed", "MasterCopyMode.Rename renamed blocks or PLC data types of the copy (" + string.Join(", ", renamed)
                    + "); callers would not reach them under their template names. Checkpoint recovery is required.");
        }

        // Conservative: follows SoftwareUnit relations by unit name; reaching target means the new relation closes a cycle.
        private static bool UnitReaches(List<SoftwareScope> scopes, SoftwareScope start, string target)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<SoftwareScope>(new[] { start });
            while (pending.Count != 0)
            {
                var current = pending.Pop();
                if (string.Equals(current.Name, target, StringComparison.OrdinalIgnoreCase)) return true;
                if (!visited.Add(current.Name)) continue;
                foreach (var relation in current.Unit.Relations.Where(relation => relation.RelationType == UnitRelationType.SoftwareUnit))
                    foreach (var next in scopes.Where(scope => scope.Unit != null && string.Equals(scope.Name, Convert.ToString(relation.RelatedObject), StringComparison.OrdinalIgnoreCase)))
                        pending.Push(next);
            }
            return false;
        }
    }
}
