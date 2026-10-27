using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Deterministic, so LibraryChangeFingerprint recomputes and pins it again inside the apply lease.
        private JObject LibraryMaintenancePlan(LibraryChange change) => change.Kind == "library_cleanup" ? LibraryCleanupPlan(change) : LibraryHarmonizePlan(change);

        private void PrepareLibraryMaintenance(LibraryChange change) => change.Preview[change.Kind == "library_cleanup" ? "cleanup" : "harmonize"] = LibraryMaintenancePlan(change);

        private static List<LibraryType> LibraryMaintenanceSelection(LibraryChange change, out List<ILibraryTypeOrFolderSelection> native)
        {
            var guids = change.Payload["types"] as JArray ?? new JArray(); var folders = change.Payload["folderPaths"] as JArray ?? new JArray();
            if (guids.Count + folders.Count == 0 || guids.Count + folders.Count > 100) throw new BridgeException("InvalidRequest", "Select 1..100 exact types (types: [typeGuid]) and/or type folders (folderPaths).");
            native = new List<ILibraryTypeOrFolderSelection>(); var types = new Dictionary<Guid, LibraryType>(); var budget = new ScanBudget(10000);
            foreach (var token in guids)
            {
                if (token.Type != JTokenType.String || !Guid.TryParse((string)token, out var id)) throw new BridgeException("InvalidRequest", "types must contain exact type GUID strings.");
                var type = change.Source.Native.FindType(id) ?? throw new BridgeException("LibraryIdentityMismatch", "Type GUID not found in the source library: " + id);
                native.Add(type); types[type.Guid] = type;
            }
            foreach (var token in folders)
            {
                if (token.Type != JTokenType.String) throw new BridgeException("InvalidRequest", "folderPaths must contain exact type folder paths.");
                var folder = ResolveLibraryTypeFolder(change.Source, (string)token); native.Add(folder);
                foreach (var item in WalkLibraryTypes(folder, (string)token, budget, 0)) types[item.Value.Guid] = item.Value;
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Selected type folders exceed the 10000-entry walk budget.");
            if (native.Distinct().Count() != native.Count) throw new BridgeException("InvalidRequest", "Duplicate selections are not allowed.");
            return types.Values.OrderBy(x => x.Guid).ToList();
        }

        private JObject LibraryCleanupPlan(LibraryChange change)
        {
            var project = change.Source.Project ?? throw new BridgeException("TargetIncompatible", "Cleanup is limited to project libraries; consumers of a global library in other projects cannot be enumerated.");
            string mode = ChangeText(change.Payload, "cleanUpMode", 64);
            if (mode != nameof(CleanUpMode.PreserveDefaultVersionOfUnusedTypes) && mode != nameof(CleanUpMode.DeleteUnusedTypes)) throw new BridgeException("InvalidRequest", "cleanUpMode must be PreserveDefaultVersionOfUnusedTypes or DeleteUnusedTypes.");
            var types = LibraryMaintenanceSelection(change, out _);
            var versions = types.SelectMany(x => x.Versions).Take(1001).ToList();
            if (versions.Count > 1000) throw new BridgeException("PackageLimitExceeded", "Cleanup scope exceeds 1000 versions; select fewer types or folders.");
            if (versions.Any(x => x.State != LibraryTypeVersionState.Committed)) throw new BridgeException("InvalidChangeState", "Release or discard every in-work version in the cleanup scope first.");
            var reasons = new Dictionary<Guid, string>(); var scope = new HashSet<Guid>(versions.Select(x => x.Guid));
            Func<LibraryTypeVersion, bool> candidate = x => scope.Contains(x.Guid) && !reasons.ContainsKey(x.Guid);
            foreach (var version in versions)
            {
                if (version.MasterCopiesContainingInstances.Any()) { reasons[version.Guid] = "embeddedInMasterCopy"; continue; }
                var uses = LibraryProjectUses(project, version, out var complete);
                if (!complete) throw new BridgeException("DependencyUnresolved", "Some project software does not expose complete native library-use search; unused versions cannot be proven.");
                if (uses.Count != 0) reasons[version.Guid] = "usedInProject";
            }
            // A version stays while any dependent stays, including dependents outside the selection.
            Action keepDependencies = () =>
            {
                for (bool changed = true; changed;)
                {
                    changed = false;
                    foreach (var version in versions.Where(x => candidate(x) && x.Dependents.Any(d => !candidate(d))).ToList()) { reasons[version.Guid] = "dependencyOfRetainedVersion"; changed = true; }
                }
            };
            keepDependencies();
            if (mode == nameof(CleanUpMode.PreserveDefaultVersionOfUnusedTypes))
            {
                foreach (var type in types.Where(t => t.Versions.All(candidate)).ToList())
                    foreach (var kept in type.Versions.Where(v => v.IsDefault || v.VersionNumber == type.Versions.Max(x => x.VersionNumber)).ToList()) reasons[kept.Guid] = "preservedDefaultOrHighest";
                keepDependencies();
            }
            var deleted = versions.Where(candidate).OrderBy(x => x.Guid).ToList();
            if (deleted.Count == 0) throw new BridgeException("NoEffect", "No selected version is provably unused; nothing would be cleaned up.");
            var deletedGuids = new HashSet<string>(deleted.Select(x => x.Guid.ToString("D")), StringComparer.Ordinal);
            var retained = LibraryVersionGuids(change).Where(x => !deletedGuids.Contains(x)).ToList();
            return new JObject
            {
                ["cleanUpMode"] = mode, ["projectPath"] = project.Path?.FullName,
                ["deleteVersions"] = new JArray(deleted.Select(LibraryVersionInfo)),
                ["deleteTypes"] = new JArray(mode == nameof(CleanUpMode.DeleteUnusedTypes) ? types.Where(t => t.Versions.All(candidate)).Select(t => new JObject { ["typeGuid"] = t.Guid.ToString("D"), ["typeName"] = t.Name }) : Enumerable.Empty<JObject>()),
                ["retainedScopeVersions"] = new JArray(versions.Where(x => reasons.ContainsKey(x.Guid)).OrderBy(x => x.Guid).Select(x => { var row = LibraryVersionInfo(x); row["reason"] = reasons[x.Guid]; return row; })),
                ["retainedLibraryVersions"] = new JObject { ["count"] = retained.Count, ["sha256"] = ChangeHash(string.Join("\n", retained)) },
                ["policy"] = "Candidates have no native use in any project software, no master-copy embedding and no retained dependent. Apply fails unless every other library version remains; native may retain candidates."
            };
        }

        private static List<string> LibraryVersionGuids(LibraryChange change)
        {
            var budget = new ScanBudget(50000);
            var result = WalkLibraryTypes(change.Source.Native.TypeFolder, "/types", budget, 0).SelectMany(x => x.Value.Versions).Select(x => x.Guid.ToString("D")).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (budget.Truncated) throw new BridgeException("PreservationCoverageIncomplete", "Library version inventory exceeded its 50000-entry budget.");
            return result;
        }

        private JObject LibraryHarmonizePlan(LibraryChange change)
        {
            if (change.Source.Project != null && !change.Source.Project.Equals(change.Project)) throw new BridgeException("TargetIncompatible", "A project library harmonizes only its own project.");
            foreach (string flag in new[] { "harmonizeNames", "harmonizePaths" })
                if (change.Payload[flag] != null && change.Payload[flag].Type != JTokenType.Boolean) throw new BridgeException("InvalidRequest", flag + " must be a boolean.");
            bool names = change.Payload.Value<bool?>("harmonizeNames") == true, paths = change.Payload.Value<bool?>("harmonizePaths") == true;
            if (!names && !paths) throw new BridgeException("InvalidRequest", "Set harmonizeNames and/or harmonizePaths to true.");
            var plc = SelectPlc(change.Selector);
            var inventory = LibraryPlcInventory(plc);
            var namesByScope = inventory.Values.GroupBy(x => x[0]).ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(x => x[2]), StringComparer.OrdinalIgnoreCase));
            var instances = new List<JObject>(); var seen = new HashSet<IEngineeringObject>();
            foreach (var type in LibraryMaintenanceSelection(change, out _))
            {
                if (type.Versions.Any(x => !(x is CodeBlockLibraryTypeVersion) && !(x is PlcTypeLibraryTypeVersion))) throw new BridgeException("TargetIncompatible", "Harmonize is limited to PLC code-block and PLC data types: " + type.Name);
                string folder = LibraryGroupPath(type);
                foreach (var use in LibraryPlcInstances(change.Project, type.Guid, plc))
                {
                    if (use.Item2 == null || !inventory.TryGetValue(use.Item2, out var location) || !seen.Add(use.Item2)) throw new BridgeException("DependencyUnresolved", "A native type instance cannot be resolved to one exact PLC object path.");
                    if (instances.Count >= 1000) throw new BridgeException("PackageLimitExceeded", "More than 1000 affected instances; select fewer types.");
                    string group = LibraryGroupPath(use.Item2), expectedName = names ? type.Name : location[2], expectedGroup = paths ? folder : group;
                    if (!string.Equals(expectedName, location[2], StringComparison.OrdinalIgnoreCase) && namesByScope[location[0]].Contains(expectedName))
                        throw new BridgeException("LibraryConflict", "Another object in " + location[0] + " already uses the harmonized name " + expectedName + ".");
                    var changes = new JArray(); if (expectedName != location[2]) changes.Add("name"); if (expectedGroup != group) changes.Add("path");
                    instances.Add(new JObject { ["objectPath"] = location[1], ["scopePath"] = location[0], ["typeGuid"] = type.Guid.ToString("D"), ["versionGuid"] = use.Item1.Guid.ToString("D"),
                        ["name"] = location[2], ["expectedName"] = expectedName, ["groupPath"] = group, ["expectedGroupPath"] = expectedGroup, ["changes"] = changes });
                }
            }
            if (instances.GroupBy(x => x.Value<string>("scopePath") + "\n" + x.Value<string>("expectedName").ToUpperInvariant()).Any(g => g.Count() > 1))
                throw new BridgeException("LibraryConflict", "Several instances would share one harmonized name in the same software scope.");
            if (instances.All(x => !x["changes"].HasValues)) throw new BridgeException("NoEffect", "Every selected type instance in this PLC already has its library name and path; nothing to harmonize.");
            var others = inventory.Where(x => !seen.Contains(x.Key)).Select(x => x.Value[1]).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return new JObject
            {
                ["plcPath"] = plc.Path, ["harmonizeNames"] = names, ["harmonizePaths"] = paths,
                ["instances"] = new JArray(instances.OrderBy(x => x.Value<string>("objectPath"), StringComparer.Ordinal)),
                ["unaffectedObjects"] = new JObject { ["count"] = others.Count, ["sha256"] = ChangeHash(string.Join("\n", others)) },
                ["pathMapping"] = "expectedGroupPath mirrors the source type's library folder below /types as project user groups; inferred, not natively qualified."
            };
        }

        private JObject ApplyLibraryMaintenance(LibraryChange change)
        {
            LibraryMaintenanceSelection(change, out var selection);
            var result = new JObject { ["objects"] = new JArray() };
            if (change.Kind == "library_cleanup")
            {
                var plan = (JObject)change.Preview["cleanup"];
                change.Source.Project.ProjectLibrary.CleanUpLibrary(selection, (CleanUpMode)Enum.Parse(typeof(CleanUpMode), plan.Value<string>("cleanUpMode")));
                var candidates = ((JArray)plan["deleteVersions"]).Select(x => x.Value<string>("versionGuid")).ToList(); var candidateSet = new HashSet<string>(candidates, StringComparer.Ordinal);
                var after = new HashSet<string>(LibraryVersionGuids(change), StringComparer.Ordinal);
                var retained = after.Where(x => !candidateSet.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (retained.Count != plan["retainedLibraryVersions"].Value<int>("count") || ChangeHash(string.Join("\n", retained)) != plan["retainedLibraryVersions"].Value<string>("sha256"))
                    throw new BridgeException("PreservationMismatch", "Native cleanup removed or added a library version outside the previewed deletion set.");
                result["removedVersionGuids"] = new JArray(candidates.Where(x => !after.Contains(x)));
                result["candidatesRetainedByNative"] = new JArray(candidates.Where(after.Contains));
                result["removedTypeGuids"] = new JArray(((JArray)plan["deleteTypes"]).Select(x => x.Value<string>("typeGuid")).Where(x => change.Source.Native.FindType(Guid.Parse(x)) == null));
                return result;
            }
            var harmonize = (JObject)change.Preview["harmonize"]; var plc = SelectPlc(change.Selector);
            var options = (harmonize.Value<bool>("harmonizeNames") ? HarmonizeProjectOptions.HarmonizeNames : HarmonizeProjectOptions.None) | (harmonize.Value<bool>("harmonizePaths") ? HarmonizeProjectOptions.HarmonizePaths : HarmonizeProjectOptions.None);
            change.Source.Native.HarmonizeProject(selection, new IUpdateProjectScope[] { plc.Software }, options);
            var rows = (JArray)harmonize["instances"];
            var expected = rows.Select(x => string.Join("|", x.Value<string>("typeGuid"), x.Value<string>("versionGuid"), x.Value<string>("scopePath"), x.Value<string>("expectedName"), x.Value<string>("expectedGroupPath"))).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var inventory = LibraryPlcInventory(plc); var seen = new HashSet<IEngineeringObject>(); var actual = new List<string>();
            foreach (string typeGuid in rows.Select(x => x.Value<string>("typeGuid")).Distinct())
                foreach (var use in LibraryPlcInstances(change.Project, Guid.Parse(typeGuid), plc))
                {
                    if (use.Item2 == null || !inventory.TryGetValue(use.Item2, out var location) || !seen.Add(use.Item2)) throw new BridgeException("PreservationCoverageIncomplete", "A harmonized type instance cannot be resolved to one exact PLC object path.");
                    actual.Add(string.Join("|", typeGuid, use.Item1.Guid.ToString("D"), location[0], location[2], LibraryGroupPath(use.Item2)));
                    ((JArray)result["objects"]).Add(new JObject { ["objectPath"] = location[1], ["name"] = location[2], ["groupPath"] = LibraryGroupPath(use.Item2), ["versionGuid"] = use.Item1.Guid.ToString("D") });
                }
            actual.Sort(StringComparer.Ordinal);
            if (!expected.SequenceEqual(actual)) throw new BridgeException("PreservationMismatch", "Harmonized instance names, group paths or version links differ from the preview.", new JObject { ["expected"] = new JArray(expected), ["actual"] = new JArray(actual) });
            var others = inventory.Where(x => !seen.Contains(x.Key)).Select(x => x.Value[1]).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (others.Count != harmonize["unaffectedObjects"].Value<int>("count") || ChangeHash(string.Join("\n", others)) != harmonize["unaffectedObjects"].Value<string>("sha256"))
                throw new BridgeException("PreservationMismatch", "PLC objects outside the harmonized instances changed path, appeared or disappeared.");
            return result;
        }

        // Exact PLC block/type inventory: object -> { scopePath, objectPath, name }.
        private static Dictionary<IEngineeringObject, string[]> LibraryPlcInventory(PlcSelection plc)
        {
            var budget = new ScanBudget(50000); var result = new Dictionary<IEngineeringObject, string[]>();
            foreach (var scope in SoftwareScopes(plc, budget))
            {
                foreach (var block in ScopeBlocks(scope, budget)) result[block.Item] = new[] { scope.Path, block.Path, block.Item.Name };
                foreach (var type in ScopeTypes(scope, budget)) result[type.Item] = new[] { scope.Path, type.Path, type.Item.Name };
            }
            if (budget.Truncated) throw new BridgeException("PreservationCoverageIncomplete", "Complete PLC block/type inventory exceeded its 50000-entry budget.");
            return result;
        }

        private static IEnumerable<Tuple<LibraryTypeVersion, IEngineeringObject>> LibraryPlcInstances(Project project, Guid typeGuid, PlcSelection plc)
        {
            var installed = project.ProjectLibrary.FindType(typeGuid);
            if (installed == null) yield break;
            foreach (var version in installed.Versions)
                foreach (var use in version.FindInstances(plc.Software)) yield return Tuple.Create(version, use.LibraryTypeInstance as IEngineeringObject);
        }

        // User folder/group names between the object and its root, so library folders and project groups compare directly.
        private static string LibraryGroupPath(IEngineeringInstance item)
        {
            var names = new List<string>(); var current = item?.Parent;
            for (int depth = 0; depth < 64; depth++, current = current.Parent)
            {
                string name = current is LibraryTypeUserFolder folder ? folder.Name : current is PlcBlockUserGroup blocks ? blocks.Name : (current as PlcTypeUserGroup)?.Name;
                if (name == null) break;
                names.Insert(0, Segment(name));
            }
            return string.Join("/", names);
        }

        private JObject LibraryUpdateCheck(JObject args)
        {
            var library = ResolveLibrary(ChangeText(args, "libraryId", 64));
            var project = SelectProject(args);
            string mode = args.Value<string>("mode") ?? "outOfDateOnly";
            if (mode != "outOfDateOnly" && mode != "outOfDateAndUpToDate") throw new BridgeException("InvalidRequest", "mode must be outOfDateOnly or outOfDateAndUpToDate.");
            int max = args.Value<int?>("maxItems") ?? 200;
            if (max < 1 || max > 1000) throw new BridgeException("InvalidRequest", "maxItems must be 1..1000.");
            bool modified = project.IsModified;
            var check = library.Native.UpdateCheck(project, mode == "outOfDateOnly" ? UpdateCheckMode.ReportOutOfDateOnly : UpdateCheckMode.ReportOutOfDateAndUpToDate);
            var rows = new JArray(); var budget = new ScanBudget(max);
            LibraryUpdateCheckMessages(check.Messages, rows, budget, 0);
            var result = Envelope();
            result["libraryId"] = library.Id; result["projectPath"] = project.Path?.FullName; result["mode"] = mode; result["items"] = rows;
            result["completeness"] = new JObject { ["scopeComplete"] = !budget.Truncated, ["truncated"] = budget.Truncated, ["maxItems"] = max,
                ["scope"] = "Native UpdateCheck log messages, depth-first; messages beyond maxItems or depth 64 and message parts beyond 50 are omitted. No update is performed." };
            result["projectModifiedByCheck"] = !modified && project.IsModified;
            return result;
        }

        private static void LibraryUpdateCheckMessages(UpdateCheckResultMessageComposition messages, JArray rows, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; return; }
            foreach (var message in messages)
            {
                if (!budget.Take()) return;
                var parts = message.MessageParts ?? new Dictionary<string, string>();
                if (parts.Count > 50) budget.Truncated = true;
                rows.Add(new JObject { ["depth"] = depth, ["description"] = message.Description, ["parts"] = new JObject(parts.Take(50).Select(x => new JProperty(x.Key, x.Value))) });
                LibraryUpdateCheckMessages(message.Messages, rows, budget, depth + 1);
            }
        }

        // A native user group is itself an IMasterCopySource: one composite master copy with the group's content.
        private NativeObjectTarget LibraryMasterCopySource(LibraryChange change)
        {
            var selector = change.Payload["object"] as JObject ?? throw new BridgeException("InvalidRequest", "An exact object selector is required.");
            var args = (JObject)change.Selector.DeepClone(); foreach (var property in selector.Properties()) args[property.Name] = property.Value.DeepClone();
            if (selector["groupKind"] == null) return ResolveObjectTarget(args);
            var plc = SelectPlc(args);
            var group = new EngineeringChange { ScopePath = args.Value<string>("scopePath") ?? plc.Path, GroupPath = ChangeText(selector, "groupPath", 2048) };
            string kind = ChangeText(selector, "groupKind", 32);
            var native = kind == "blocks" ? (IEngineeringObject)(MutationBlockGroup(group, plc) as PlcBlockUserGroup)
                : kind == "types" ? (IEngineeringObject)(MutationTypeGroup(group, plc) as PlcTypeUserGroup)
                : kind == "tags" ? (IEngineeringObject)(MutationTagGroup(group, plc) as PlcTagTableUserGroup)
                : throw new BridgeException("InvalidRequest", "groupKind must be blocks, types or tags.");
            if (native == null) throw new BridgeException("TargetIncompatible", "Only a native user group is a composite master-copy source; root and system groups are not.");
            return new NativeObjectTarget { Target = native, Plc = plc, ScopePath = group.ScopePath, Identity = IdentityInfo(plc.Project, plc.Path, group.ScopePath, group.GroupPath, "group", (string)native.GetAttribute("Name")) };
        }
    }
}
