using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject ObjectResolve(JObject args)
        {
            if ((args.Value<string>("kind") ?? "").StartsWith("hmi", StringComparison.Ordinal) || args["hmiPath"] != null)
                return HmiObjectResolve(args);
            var target = ResolveObjectTarget(args);
            var result = Envelope();
            result["schemaVersion"] = 1;
            result["identity"] = target.Identity.DeepClone();
            result["objectId"] = target.Identity["objectPath"];
            result["engineeringType"] = target.Target.GetType().FullName;
            result["scope"] = target.Scope == null ? null : new JObject { ["path"] = target.Scope.Path, ["kind"] = target.Scope.Kind, ["name"] = target.Scope.Name };
            result["candidates"] = new JArray(target.Identity.DeepClone());
            result["completeness"] = new JObject { ["scopeComplete"] = true, ["truncated"] = false,
                ["scope"] = "Exact uniqueness within the selected project/PLC and requested supported object kind; lookup bounded to 50000 objects.",
                ["gaps"] = new JArray("Identity changes after rename, movement, archive retrieval or project path change", "Native group/folder objects, individual tags and TO parameter members are not independently resolved") };
            return result;
        }

        private NativeObjectTarget ResolveObjectTarget(JObject args)
        {
            string kind = args.Value<string>("kind"), path = args.Value<string>("objectPath"), name = args.Value<string>("name"), scopePath = args.Value<string>("scopePath"), nameSpace = args.Value<string>("namespace");
            if (string.IsNullOrWhiteSpace(kind)) kind = ObjectIdentity.KindFromPath(path);
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(name) && kind != "project" && kind != "plc")
                throw new BridgeException("InvalidRequest", "Specify objectPath or name; kind=project or kind=plc may use their normal project/PLC selectors.");
            var supported = new[] { "project", "plc", "block", "type", "tagTable", "hardware", "technology", "softwareUnit", "safetyUnit" };
            if (!string.IsNullOrWhiteSpace(kind) && !supported.Contains(kind, StringComparer.Ordinal))
                throw new BridgeException("UnsupportedObjectKind", "Supported object kinds: " + string.Join(", ", supported));
            var project = SelectProject(args);
            if (!string.IsNullOrWhiteSpace(args.Value<string>("projectPath")) &&
                !string.Equals(args.Value<string>("projectPath"), project.Path?.FullName, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ProjectMismatch", "The supplied projectPath does not identify the selected open project.");
            var budget = new ScanBudget(50000);
            var candidates = new List<NativeObjectTarget>();
            Action<NativeObjectTarget> add = target =>
            {
                if ((string.IsNullOrWhiteSpace(kind) || kind == target.Identity.Value<string>("kind")) &&
                    ObjectIdentity.Matches(target.Identity.Value<string>("objectPath"), target.Identity.Value<string>("name"), target.ScopePath, path, name, scopePath) &&
                    (nameSpace == null || target.Identity.Value<string>("namespace") == nameSpace))
                    candidates.Add(target);
            };
            string projectObjectPath = "/projects/" + Segment(project.Name);
            if (kind == "project" || string.IsNullOrWhiteSpace(kind)) add(ObjectTarget(project, null, null, project, projectObjectPath, "project", project.Name));
            if (kind == "hardware" || string.IsNullOrWhiteSpace(kind))
                foreach (var entry in DeviceItems(project, budget)) add(ObjectTarget(project, null, null, entry.Item, entry.Path, "hardware", entry.Item.Name));
            if (kind != "hardware" && kind != "project")
            {
                var effective = (JObject)args.DeepClone();
                if (string.IsNullOrWhiteSpace(effective.Value<string>("plcPath"))) effective["plcPath"] = ObjectIdentity.PlcPath(path) ?? ObjectIdentity.PlcPath(scopePath);
                var plc = SelectPlc(effective);
                if (kind == "plc" || string.IsNullOrWhiteSpace(kind)) add(ObjectTarget(project, plc, null, plc.Software, plc.Path, "plc", plc.Software.Name));
                foreach (var scope in SoftwareScopes(plc, budget))
                {
                    if (!string.IsNullOrWhiteSpace(scopePath) && scope.Path != scopePath) continue;
                    if (scope.Unit != null) add(ObjectTarget(project, plc, scope, scope.Unit, scope.Path, scope.Kind, scope.Name));
                    if (kind == "block" || string.IsNullOrWhiteSpace(kind))
                        foreach (var block in ScopeBlocks(scope, budget))
                        {
                            var target = ObjectTarget(project, plc, scope, block.Item, block.Path, "block", block.Item.Name);
                            target.Block = block; add(target);
                        }
                    if (kind == "type" || string.IsNullOrWhiteSpace(kind))
                        foreach (var type in ScopeTypes(scope, budget)) add(ObjectTarget(project, plc, scope, type.Item, type.Path, "type", type.Item.Name));
                    if (kind == "tagTable" || string.IsNullOrWhiteSpace(kind))
                        foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget)) add(ObjectTarget(project, plc, scope, table.Item, table.Path, "tagTable", table.Item.Name));
                }
                if (kind == "technology" || string.IsNullOrWhiteSpace(kind))
                    foreach (var item in TechnologyObjects(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", budget))
                        add(ObjectTarget(project, plc, null, item.Item, item.Path, "technology", item.Item.Name));
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Object lookup reached 50000 objects; uniqueness is not established.");
            if (candidates.Count == 0) throw new BridgeException("ObjectNotFound", "No object matches all supplied identity selectors.");
            if (candidates.Count != 1) throw new BridgeException("AmbiguousTarget", "Multiple objects match. Specify the exact objectPath, scopePath and kind.",
                new JObject { ["schemaVersion"] = 1, ["candidates"] = new JArray(candidates.Take(100).Select(candidate => candidate.Identity)), ["candidateCount"] = candidates.Count, ["candidatesTruncated"] = candidates.Count > 100 });
            return candidates[0];
        }

        private static NativeObjectTarget ObjectTarget(Project project, PlcSelection plc, SoftwareScope scope, IEngineeringObject target, string path, string kind, string name) => new NativeObjectTarget
        {
            Target = target, Plc = plc, Scope = scope, ScopePath = scope?.Path ?? plc?.Path,
            Identity = IdentityInfo(project, plc?.Path, scope?.Path ?? plc?.Path, path, kind, name)
        };

        private static JObject IdentityInfo(Project project, string plcPath, string scopePath, string objectPath, string kind, string name) => new JObject
            { ["projectPath"] = project.Path?.FullName, ["plcPath"] = plcPath, ["scopePath"] = scopePath, ["objectPath"] = objectPath, ["kind"] = kind, ["name"] = name,
                ["namespace"] = ObjectIdentity.NamespaceFromPath(objectPath) };

        private static string BlockObjectPath(string groupPath, PlcBlock block) => ObjectIdentity.ObjectPath(groupPath, block.Name, block.Namespace);

        private BlockSelection CanonicalSelectBlock(JObject args)
        {
            var effective = (JObject)args.DeepClone();
            effective["kind"] = "block";
            effective["objectPath"] = args["blockPath"];
            effective["name"] = args["blockName"];
            try
            {
                var target = ResolveObjectTarget(effective);
                return new BlockSelection { Project = target.Plc.Project, PlcPath = target.Plc.Path, Block = target.Block };
            }
            catch (BridgeException exception) when (exception.Code == "ObjectNotFound")
            { throw new BridgeException("BlockNotFound", "No matching block was found in the exposed PLC/software-unit/safety-unit groups."); }
        }

        private static SoftwareScope ResolveSoftwareScope(PlcSelection plc, string path)
        {
            var budget = new ScanBudget(50000);
            var scopes = SoftwareScopes(plc, budget);
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Software-scope resolution is incomplete.");
            return scopes.SingleOrDefault(scope => scope.Path == path) ?? throw new BridgeException("ScopeNotFound", "No exposed software scope matches scopePath.");
        }

        private void PrepareMutationScope(EngineeringChange change, PlcSelection plc, string objectKind, string[] names)
        {
            if (!string.IsNullOrWhiteSpace(change.Payload.Value<string>("scopePath")) && !string.IsNullOrWhiteSpace(change.Selector.Value<string>("scopePath")) &&
                change.Payload.Value<string>("scopePath") != change.Selector.Value<string>("scopePath"))
                throw new BridgeException("ScopeMismatch", "Top-level and payload scopePath selectors disagree.");
            string explicitScope = change.Payload.Value<string>("scopePath") ?? change.Selector.Value<string>("scopePath");
            string explicitNamespace = change.Payload.Value<string>("namespace");
            if (explicitNamespace != null && change.Namespace != null && explicitNamespace != change.Namespace)
                throw new BridgeException("NamespaceMismatch", "Payload namespace conflicts with the imported source/XML namespace.");
            if (change.Namespace == null) change.Namespace = explicitNamespace;
            string exactPath = change.Payload.Value<string>(objectKind == "block" ? "blockPath" : objectKind == "type" ? "typePath" : "tagTablePath");
            if (!string.IsNullOrWhiteSpace(exactPath) && change.Namespace != null && ObjectIdentity.NamespaceFromPath(exactPath) != change.Namespace)
                throw new BridgeException("NamespaceMismatch", "Imported source/XML namespace does not match the exact selected object; namespace relocation is not part of this change.");
            if (!string.IsNullOrWhiteSpace(exactPath) && names.Length != 1)
                throw new BridgeException("InvalidRequest", "An exact object path can only select a single-object change.");
            var budget = new ScanBudget(50000);
            var matches = new List<Tuple<string, string, string>>();
            foreach (var scope in SoftwareScopes(plc, budget))
            {
                if (!string.IsNullOrWhiteSpace(explicitScope) && scope.Path != explicitScope) continue;
                if (objectKind == "block")
                    foreach (var block in ScopeBlocks(scope, budget))
                        if (names.Contains(block.Item.Name, StringComparer.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(exactPath) || block.Path == exactPath) &&
                            (change.Namespace == null || (block.Item.Namespace ?? "") == change.Namespace))
                            matches.Add(Tuple.Create(scope.Path, block.Path, block.Item.Name));
                if (objectKind == "type")
                    foreach (var type in ScopeTypes(scope, budget))
                        if (names.Contains(type.Item.Name, StringComparer.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(exactPath) || type.Path == exactPath) &&
                            (change.Namespace == null || (type.Item.Namespace ?? "") == change.Namespace))
                            matches.Add(Tuple.Create(scope.Path, type.Path, type.Item.Name));
                if (objectKind == "tagTable")
                    foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget))
                        if (names.Contains(table.Item.Name, StringComparer.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(exactPath) || table.Path == exactPath))
                            matches.Add(Tuple.Create(scope.Path, table.Path, table.Item.Name));
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Mutation target lookup is incomplete; no preview can be prepared.");
            if (matches.GroupBy(match => match.Item3, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new BridgeException("AmbiguousTarget", "Mutation names match more than one object. Specify scopePath and, when needed, an exact object path.",
                    new JObject { ["candidates"] = new JArray(matches.Take(100).Select(match => new JObject { ["scopePath"] = match.Item1, ["objectPath"] = match.Item2, ["name"] = match.Item3 })),
                        ["candidateCount"] = matches.Count, ["candidatesTruncated"] = matches.Count > 100 });
            if (!string.IsNullOrWhiteSpace(exactPath) && matches.Count == 0)
                throw new BridgeException("ObjectNotFound", "The exact mutation object path does not match its expected name and scope.");
            try { change.ScopePath = ObjectIdentity.SingleScope(matches.Select(match => match.Item1), explicitScope, plc.Path); }
            catch (InvalidOperationException ex) { throw new BridgeException("ScopeMismatch", ex.Message); }
            var selected = ResolveSoftwareScope(plc, change.ScopePath);
            var namespaces = matches.Select(match => ObjectIdentity.NamespaceFromPath(match.Item2)).Distinct(StringComparer.Ordinal).ToArray();
            if (namespaces.Length > 1) throw new BridgeException("NamespaceMismatch", "Targets occupy different namespaces; split this change into separate namespace targets.");
            change.Namespace = change.Namespace ?? namespaces.FirstOrDefault() ?? "";
            if (objectKind == "tagTable" && change.Namespace.Length != 0)
                throw new BridgeException("UnsupportedNamespace", "Tag tables have no namespace-qualified identity in this API; namespace selectors apply only to blocks and types.");
            if (selected.Unit == null && change.Namespace.Length != 0)
                throw new BridgeException("UnsupportedNamespace", "Named block/type namespaces require an explicitly selected software-unit scope.");
            if (selected.Kind == "safetyUnit" && change.Kind != "safety_import_xml")
                throw new BridgeException("SafetyBoundary", "Safety-unit mutations require the explicit safety import workflow.");
            if (matches.Any(match => ObjectIdentity.IsGeneratedPath(match.Item2)))
                throw new BridgeException("GeneratedObject", "Generated system-group objects are not eligible for direct mutation.");
            var parentPaths = matches.Select(match => ObjectIdentity.ParentPath(match.Item2)).Distinct(StringComparer.Ordinal).ToArray();
            if (parentPaths.Length > 1) throw new BridgeException("GroupMismatch", "Targets occupy different native groups. Split this change so each import has one exact destination.");
            string requestedGroup = change.Payload.Value<string>("groupPath");
            change.GroupPath = requestedGroup ?? parentPaths.FirstOrDefault() ?? change.ScopePath + (objectKind == "block" ? "/blocks" : objectKind == "type" ? "/types" : "/tags");
            if (parentPaths.Length == 1 && parentPaths[0] != change.GroupPath)
                throw new BridgeException("GroupMismatch", "Updates must preserve the current native group; movement is not part of this mutation.");
            // Resolving here rejects invented or generated group paths before a checkpoint is saved.
            if (objectKind == "block") MutationBlockGroup(change, plc);
            else if (objectKind == "type") MutationTypeGroup(change, plc);
            else MutationTagGroup(change, plc);
            if (change.Kind == "source_upsert" || change.Kind == "type_source_upsert") MutationSources(change, plc);
            if (objectKind != "tagTable") ValidateNamespaceOwnership(change, plc, objectKind, names);
            change.Selector["scopePath"] = change.ScopePath;
        }

        private static void ValidateNamespaceOwnership(EngineeringChange change, PlcSelection plc, string kind, string[] names)
        {
            var budget = new ScanBudget(50000);
            foreach (var scope in SoftwareScopes(plc, budget))
            {
                var candidates = kind == "block"
                    ? ScopeBlocks(scope, budget).Where(block => names.Contains(block.Item.Name, StringComparer.OrdinalIgnoreCase)).Select(block => block.Item.Namespace ?? "")
                    : ScopeTypes(scope, budget).Where(type => names.Contains(type.Item.Name, StringComparer.OrdinalIgnoreCase)).Select(type => type.Item.Namespace ?? "");
                foreach (string candidate in candidates)
                {
                    if (ObjectIdentity.NamespaceCaseAlias(candidate, change.Namespace))
                        throw new BridgeException("NamespaceMismatch", "An existing target's namespace differs only by case. Use its exact namespace spelling; case-alias imports are not treated as new objects.");
                    if (scope.Path != change.ScopePath && candidate == change.Namespace)
                        throw new BridgeException("NamespaceCollision", "A target name and namespace already belong to a different software scope; cross-scope relocation is not an import operation.");
                }
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Namespace ownership lookup is incomplete.");
        }

        private static string ImportNamespace(System.Xml.Linq.XDocument document)
        {
            var values = document.Root.Elements().Where(element => element.Name != "Engineering" && element.Name != "DocumentInfo")
                .Select(element => element.Element("AttributeList")?.Element("Namespace")?.Value ?? "").Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length != 1) throw new BridgeException("NamespaceMismatch", "Each XML change must target one namespace; split mixed namespace objects into separate changes.");
            return values[0];
        }

        private string MutationBlockName(JObject payload, PlcSelection plc)
        {
            string name = payload.Value<string>("blockName");
            if (string.IsNullOrWhiteSpace(payload.Value<string>("blockPath"))) return ChangeText(payload, "blockName", 128);
            var target = ResolveObjectTarget(new JObject { ["projectName"] = plc.Project.Name, ["plcPath"] = plc.Path, ["kind"] = "block",
                ["objectPath"] = payload["blockPath"], ["name"] = name, ["scopePath"] = payload["scopePath"], ["namespace"] = payload["namespace"] });
            return target.Block.Item.Name;
        }

        private static void VerifyExpectedTypesExist(EngineeringChange change, PlcSelection plc)
        {
            var group = MutationTypeGroup(change, plc);
            if (change.ObjectNames.Any(name => !group.Types.Any(type => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) && (type.Namespace ?? "") == change.Namespace)))
                throw new BridgeException("ImportVerificationFailed", "Not all expected data types exist in the selected scope and group after generation/import.");
        }

        private static PlcExternalSourceGroup MutationSources(EngineeringChange change, PlcSelection plc)
        {
            var scope = ResolveSoftwareScope(plc, change.ScopePath);
            return (scope.Unit == null ? plc.Software.ExternalSourceGroup : scope.Unit.ExternalSourceGroup)
                ?? throw new BridgeException("UnsupportedCapability", "The selected scope exposes no native external-source group.");
        }

        private static PlcBlockGroup MutationBlockGroup(EngineeringChange change, PlcSelection plc)
        {
            var scope = ResolveSoftwareScope(plc, change.ScopePath);
            return FindBlockGroup(scope.Blocks, scope.Path + "/blocks", change.GroupPath, 0)
                ?? throw new BridgeException("GroupNotFound", "No user block group matches groupPath in the selected scope.");
        }

        private static PlcBlockGroup FindBlockGroup(PlcBlockGroup group, string path, string wanted, int depth)
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "Block group nesting exceeds 64 levels.");
            if (path == wanted) return group;
            foreach (var child in group.Groups)
            {
                var found = FindBlockGroup(child, path + "/groups/" + Segment(child.Name), wanted, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        private static PlcTypeGroup MutationTypeGroup(EngineeringChange change, PlcSelection plc)
        {
            var scope = ResolveSoftwareScope(plc, change.ScopePath);
            return FindTypeGroup(scope.Types, scope.Path + "/types", change.GroupPath, 0)
                ?? throw new BridgeException("GroupNotFound", "No user type group matches groupPath in the selected scope.");
        }

        private static PlcTypeGroup FindTypeGroup(PlcTypeGroup group, string path, string wanted, int depth)
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "Type group nesting exceeds 64 levels.");
            if (path == wanted) return group;
            foreach (var child in group.Groups)
            {
                var found = FindTypeGroup(child, path + "/groups/" + Segment(child.Name), wanted, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        private static PlcTagTableGroup MutationTagGroup(EngineeringChange change, PlcSelection plc)
        {
            var scope = ResolveSoftwareScope(plc, change.ScopePath);
            return FindTagGroup(scope.Tags, scope.Path + "/tags", change.GroupPath, 0)
                ?? throw new BridgeException("GroupNotFound", "No user tag-table group matches groupPath in the selected scope.");
        }

        private static PlcTagTableGroup FindTagGroup(PlcTagTableGroup group, string path, string wanted, int depth)
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "Tag-table group nesting exceeds 64 levels.");
            if (path == wanted) return group;
            foreach (var child in group.Groups)
            {
                var found = FindTagGroup(child, path + "/groups/" + Segment(child.Name), wanted, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        private sealed class NativeObjectTarget
        {
            internal IEngineeringObject Target;
            internal JObject Identity;
            internal string ScopePath;
            internal PlcSelection Plc;
            internal SoftwareScope Scope;
            internal BlockEntry Block;
        }
    }
}
