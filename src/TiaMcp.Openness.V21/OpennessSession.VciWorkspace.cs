using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Read side only: Workspace.ExportObject creates a mapping and Synchronize writes the user's
        // workspace, so neither belongs in a read-only tool (see docs/reference/vci-package-workflow.md).
        private JObject VciWorkspaceInspect(JObject args)
        {
            var project = SelectProject(args);
            var service = project.GetService<VersionControlInterface>() ?? throw new BridgeException("UnsupportedCapability", "The VCI service is not available for this project.");
            var budget = new ScanBudget(Limit(args, 500)); var warnings = new JArray(); var groups = new JArray(); var workspaces = new JArray();
            void Visit(WorkspaceGroup group, string parentPath, int depth)
            {
                if (depth > 32) { budget.Truncated = true; return; }
                if (!budget.Take()) return;
                string name = (group as WorkspaceSystemGroup)?.Name ?? (group as WorkspaceUserGroup)?.Name;
                string path = parentPath + "/" + Segment(name);
                groups.Add(new JObject { ["path"] = path, ["name"] = name, ["kind"] = group is WorkspaceSystemGroup ? "system" : "user" });
                foreach (var workspace in group.Workspaces.OrderBy(w => w.Name, StringComparer.Ordinal))
                {
                    if (!budget.Take()) return;
                    string workspacePath = path + "/" + Segment(workspace.Name);
                    var mappings = new JArray();
                    var row = new JObject { ["path"] = workspacePath, ["groupPath"] = path, ["name"] = workspace.Name, ["mappedObjects"] = mappings };
                    workspaces.Add(row);
                    try
                    {
                        row["rootPath"] = workspace.RootPath?.FullName; row["language"] = workspace.WorkspaceLanguage?.Name;
                        row["libraryPolicy"] = new JObject { ["globalLibraryPath"] = workspace.GlobalLibraryPath?.FullName, ["deleteUnusedTypeVersionFromLibrary"] = workspace.DeleteUnusedTypeVersionFromLibrary };
                    }
                    catch (Exception exception) { warnings.Add(InspectionWarning(workspacePath, exception)); }
                    foreach (var mapping in workspace.MappedObjects)
                    {
                        if (!budget.Take()) return;
                        var mapped = new JObject();
                        mappings.Add(mapped);
                        try
                        {
                            mapped["object"] = QualificationNativeIdentity(mapping.EngineeringObject);
                            mapped["directory"] = mapping.DirectoryPath?.FullName; mapped["fileName"] = mapping.FileNameWithoutExtension; mapped["format"] = mapping.FileFormat;
                            var status = mapping.GetStatus();
                            mapped["compareState"] = status.CompareState.ToString(); mapped["compareDetails"] = status.IndividualObjectCompareDetails.ToString();
                            mapped["childMappingState"] = mapping.GetChildStatus().MappingState.ToString();
                            var formats = new JArray(); mapped["supportedFormats"] = formats;
                            foreach (string format in workspace.GetSupportedFileFormats(mapping.EngineeringObject)) { if (!budget.Take()) return; formats.Add(format); }
                        }
                        catch (Exception exception) { warnings.Add(InspectionWarning(workspacePath + "/mapped/" + mappings.Count, exception)); }
                    }
                }
                foreach (var child in group.Groups.OrderBy(g => g.Name, StringComparer.Ordinal)) Visit(child, path, depth + 1);
            }
            Visit(service.WorkspaceGroup, "vci", 0);
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["groups"] = groups;
            result["workspaces"] = workspaces;
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "VCI workspace groups, workspaces (root, language, library policy) and mapped objects with native compare/child mapping state and supported file formats.",
                "No workspace creation, mapping edit, export or synchronization", "Mapped-object identity is the native ancestor Name/Number chain, not a stable GUID",
                "Compare state is a native observation, not preservation or import qualification");
            return result;
        }

        // Prepared VCI lifecycle kinds. Workspace and mapping state live in the project and are fingerprinted (VciFingerprint);
        // files below the workspace root are not, so each kind pins a bounded root inventory at prepare, rechecks it before the
        // checkpoint and verifies after the native call that only the previewed packages and .vci configuration changed.
        private static readonly string[] VciChangeKinds = { "vci_workspace_create", "vci_workspace_delete", "vci_mapping_add", "vci_mapping_remove", "vci_synchronize" };
        private const int VciMaxEntries = 20000, VciMaxSyncMappings = 200;
        private const long VciMaxBytes = 1L << 30;

        private void PrepareVciChange(EngineeringChange change, Project project, JObject preview)
        {
            var payload = change.Payload; var service = VciService(project);
            var effects = (JArray)preview["effects"]; var warnings = (JArray)preview["warnings"];
            var packages = new JArray(); bool reportOnly = false;
            string root; JArray files; JObject effect;
            if (change.Kind == "vci_workspace_create")
            {
                string groupPath = ChangeText(payload, "groupPath", 4096), name = ChangeText(payload, "name", 128), language = ChangeText(payload, "language", 32);
                var groups = VciGroups(service).Where(g => g.Key == groupPath).ToList();
                if (groups.Count != 1) throw new BridgeException("VciGroupNotFound", "groupPath must name exactly one workspace group from vci_workspace_inspect.");
                if (groups[0].Value.Workspaces.Find(name) != null) throw new BridgeException("VciWorkspaceExists", "The group already contains a workspace named " + name + ".");
                if (!project.LanguageSettings.ActiveLanguages.Any(l => l.Culture.Name == language))
                    throw new BridgeException("InvalidRequest", "language must be the exact name of an active project language, such as de-DE.");
                root = VciRoot(ChangeText(payload, "rootPath", 240), project, change, true);
                if (VciWorkspaces(service).Any(w => w.Value.RootPath != null && VciOverlaps(w.Value.RootPath.FullName, root)))
                    throw new BridgeException("VciRootInUse", "The root overlaps the root of another workspace.");
                files = VciInventory(root); reportOnly = true;
                effect = new JObject { ["action"] = "createWorkspace", ["groupPath"] = groupPath, ["name"] = name, ["language"] = language, ["rootExists"] = Directory.Exists(root) };
                warnings.Add("Creates the workspace in the project; the root directory is created if absent. The workspace language cannot be changed later (no public setter).");
            }
            else
            {
                string workspacePath = ChangeText(payload, "workspacePath", 4096);
                var workspace = VciWorkspace(service, workspacePath);
                root = VciRoot(workspace.RootPath?.FullName ?? throw new BridgeException("VciWorkspaceUnconfigured", "The workspace has no root path."), project, change, false);
                files = VciInventory(root);
                effect = new JObject { ["workspacePath"] = workspacePath, ["language"] = workspace.WorkspaceLanguage?.Name };
                if (change.Kind == "vci_workspace_delete")
                {
                    var rows = new JArray(VciMappings(workspace).Select(m => VciMappingRow(m, root)));
                    effect["action"] = "deleteWorkspace"; effect["mappingsRemoved"] = rows; reportOnly = true;
                    warnings.Add("Deletes the workspace and its " + rows.Count + " mapping(s) from the project; the mapped engineering objects stay. Whether TIA also removes files below the root is not established, so every root file is backed up first. The project checkpoint does not restore files.");
                }
                else if (change.Kind == "vci_mapping_add")
                {
                    string mode = ChangeText(payload, "mode", 16), directory = VciPayloadDirectory(payload), fileName = ChangeText(payload, "fileName", 128), format = ChangeText(payload, "format", 64);
                    if (mode != "export" && mode != "connect") throw new BridgeException("InvalidRequest", "mode must be export (ExportObject writes the package) or connect (ConnectObject maps existing files).");
                    if (!VciSafeName(fileName)) throw new BridgeException("InvalidRequest", "fileName must be one plain file name without extension.");
                    var target = VciTarget(change);
                    var formats = workspace.GetSupportedFileFormats(target.Target).ToList();
                    if (!formats.Contains(format, StringComparer.Ordinal)) throw new BridgeException("VciFormatUnsupported", "format must be a native supported format for this object: " + string.Join(", ", formats));
                    foreach (var other in VciWorkspaces(service))
                        if (other.Value.MappedObjects.Find(target.Target) != null) throw new BridgeException("VciMappingExists", "The object is already mapped in workspace " + other.Key + ".");
                    if (VciMappings(workspace).Any(m => string.Equals(VciRelativeDirectory(root, m.DirectoryPath), directory, StringComparison.OrdinalIgnoreCase) && string.Equals(m.FileNameWithoutExtension, fileName, StringComparison.OrdinalIgnoreCase)))
                        throw new BridgeException("VciMappingCollision", "Another mapping already owns this package.");
                    var existing = new JArray(files.Select(f => f.Value<string>("path")).Where(p => VciInPackage(p, directory, fileName)));
                    if (mode == "export" && existing.Count != 0) throw new BridgeException("VciPackageCollision", "Files of this package already exist; export never overwrites them.");
                    if (mode == "connect" && existing.Count == 0) throw new BridgeException("VciPackageMissing", "connect requires the existing package files below the workspace root.");
                    if (mode == "export") packages.Add(new JObject { ["directory"] = directory, ["fileName"] = fileName });
                    effect["action"] = mode == "export" ? "exportObject" : "connectObject"; effect["object"] = target.Identity.DeepClone();
                    effect["directory"] = directory; effect["fileName"] = fileName; effect["format"] = format; effect["existingPackageFiles"] = existing;
                    warnings.Add(mode == "export" ? "Exports the object into new package files and maps it; the mapping must read back Equal." : "Maps the object to the existing package files without writing them; the native status after mapping is reported, not required.");
                }
                else if (change.Kind == "vci_mapping_remove")
                {
                    var target = VciTarget(change);
                    var row = VciMappingRow(workspace.MappedObjects.Find(target.Target) ?? throw new BridgeException("VciMappingNotFound", "The object is not mapped in this workspace."), root, true);
                    packages.Add(new JObject { ["directory"] = row["directory"], ["fileName"] = row["fileName"] });
                    effect["action"] = "removeMapping"; effect["object"] = target.Identity.DeepClone(); effect["mapping"] = row;
                    warnings.Add("Removes only the mapping; the engineering object stays. Package files are backed up first and any native file effect is reported.");
                }
                else
                {
                    string direction = ChangeText(payload, "direction", 32);
                    if (direction != "ProjectToWorkspace" && direction != "WorkspaceToProject") throw new BridgeException("InvalidRequest", "direction must be ProjectToWorkspace or WorkspaceToProject.");
                    bool toProject = direction == "WorkspaceToProject", all = payload["allMappedObjects"] != null && ExplicitChangeBool(payload, "allMappedObjects");
                    if (all == (payload["objectPath"] != null)) throw new BridgeException("InvalidRequest", "Select exactly one scope: objectPath or allMappedObjects=true.");
                    if (all && toProject) throw new BridgeException("VciScopeTooBroad", "Project-ward synchronization is limited to one exact objectPath per change.");
                    List<MappedObject> mappings;
                    if (all)
                    {
                        mappings = VciMappings(workspace);
                        if (mappings.Count == 0 || mappings.Count > VciMaxSyncMappings) throw new BridgeException("VciScopeTooBroad", "Whole-workspace synchronization needs 1.." + VciMaxSyncMappings + " mappings.");
                    }
                    else
                    {
                        var target = VciTarget(change);
                        if (toProject) VciRequireWritableTarget(target);
                        mappings = new List<MappedObject> { workspace.MappedObjects.Find(target.Target) ?? throw new BridgeException("VciMappingNotFound", "The object is not mapped in this workspace.") };
                        effect["object"] = target.Identity.DeepClone();
                    }
                    var rows = new JArray(); int overwritten = 0;
                    foreach (var mapping in mappings)
                    {
                        var status = mapping.GetStatus(); var details = status.IndividualObjectCompareDetails;
                        var row = VciMappingRow(mapping, root, true, status); rows.Add(row);
                        if (details.HasFlag(IndividualObjectCompareDetails.ProjectObjectChanged) && details.HasFlag(IndividualObjectCompareDetails.WorkspaceFileChanged))
                            throw new BridgeException("VciConflict", "Both the project object and its workspace files changed; synchronizing either way discards one side. Resolve the conflict first.", new JObject { ["mapping"] = row });
                        if (toProject && status.CompareState == CompareState.WorkspaceFileMissing) throw new BridgeException("VciPackageMissing", "The workspace files of this mapping are missing.");
                        if (details.HasFlag(toProject ? IndividualObjectCompareDetails.ProjectObjectChanged : IndividualObjectCompareDetails.WorkspaceFileChanged)) overwritten++;
                        if (!toProject) packages.Add(new JObject { ["directory"] = row["directory"], ["fileName"] = row["fileName"] });
                    }
                    effect["action"] = "synchronize"; effect["direction"] = direction; effect["scope"] = all ? "allMappedObjects" : "objectPath"; effect["mappings"] = rows;
                    warnings.Add(toProject ? "Imports the workspace files into the project object; requires compile=true. The project checkpoint is the way back."
                        : "Overwrites the package files of " + rows.Count + " mapping(s) from the project; they are backed up first. Mappings are synchronized one by one, not as a transaction.");
                    if (overwritten != 0) warnings.Add(overwritten + " mapping(s) have changes on the " + (toProject ? "project" : "workspace") + " side that this direction overwrites.");
                }
            }
            effect["rootPath"] = root;
            effects.Add(effect);
            string inventory = Path.Combine(change.Directory, "vci-files-before.json");
            File.WriteAllText(inventory, files.ToString(Formatting.None));
            var vci = new JObject { ["rootPath"] = root, ["filesBefore"] = ChangeArtifact(inventory), ["fileCount"] = files.Count, ["filesSha256"] = ChangeHash(files.ToString(Formatting.None)),
                ["writablePackages"] = packages, ["reportOnly"] = reportOnly };
            preview["vci"] = vci;
            VciBackup(change, root, files, VciMayChange(vci));
        }

        private void ApplyVciChange(EngineeringChange change, Project project)
        {
            var payload = change.Payload; var service = VciService(project); var vci = (JObject)change.Preview["vci"];
            string root = vci.Value<string>("rootPath");
            var before = JArray.Parse(File.ReadAllText(vci["filesBefore"].Value<string>("path")));
            if (ChangeHash(before.ToString(Formatting.None)) != vci.Value<string>("filesSha256")) throw new BridgeException("ArtifactChanged", "The prepared workspace file inventory changed.");
            var readback = new JObject { ["role"] = "vci-readback" };
            change.Readbacks.Add(readback);
            if (change.Kind == "vci_workspace_create")
            {
                string name = payload.Value<string>("name"), language = payload.Value<string>("language");
                var group = VciGroups(service).Single(g => g.Key == payload.Value<string>("groupPath")).Value;
                Directory.CreateDirectory(root);
                group.Workspaces.Create(name, new DirectoryInfo(root), CultureInfo.GetCultureInfo(language));
                var created = group.Workspaces.Find(name);
                if (created == null || created.Name != name || !VciSamePath(created.RootPath?.FullName, root) || created.WorkspaceLanguage?.Name != language)
                    throw new BridgeException("MutationVerificationFailed", "The created workspace did not read back with the requested name, root and language.");
                readback["workspace"] = new JObject { ["name"] = created.Name, ["rootPath"] = created.RootPath.FullName, ["language"] = created.WorkspaceLanguage.Name, ["mappedObjects"] = created.MappedObjects.Count };
            }
            else
            {
                string workspacePath = payload.Value<string>("workspacePath");
                var workspace = VciWorkspace(service, workspacePath);
                if (change.Kind == "vci_workspace_delete")
                {
                    workspace.Delete();
                    if (VciWorkspaces(service).Any(w => w.Key == workspacePath)) throw new BridgeException("MutationVerificationFailed", "The workspace still exists after Delete.");
                    readback["workspaceDeleted"] = workspacePath; readback["rootExists"] = Directory.Exists(root);
                }
                else if (change.Kind == "vci_mapping_add")
                {
                    var target = VciTarget(change);
                    string directory = VciPayloadDirectory(payload), fileName = payload.Value<string>("fileName"), format = payload.Value<string>("format");
                    // V21 refused DirectoryInfo(".") as a relative path and accepted the absolute directory below the root (vci-native-profiles-20261015.md).
                    var location = new DirectoryInfo(directory.Length == 0 ? root : Path.Combine(root, directory.Replace('/', '\\')));
                    if (payload.Value<string>("mode") == "export") { Directory.CreateDirectory(location.FullName); workspace.ExportObject(target.Target, location, fileName, format); }
                    else workspace.ConnectObject(target.Target, location, fileName, format);
                    var mapping = workspace.MappedObjects.Find(target.Target) ?? throw new BridgeException("MutationVerificationFailed", "No mapping exists for the object after the native call.");
                    var row = VciMappingRow(mapping, root);
                    readback["mappingsAfter"] = new JArray(row);
                    if (!string.Equals(row.Value<string>("directory"), directory, StringComparison.OrdinalIgnoreCase) || mapping.FileNameWithoutExtension != fileName || mapping.FileFormat != format)
                        throw new BridgeException("MutationVerificationFailed", "The mapping did not read back with the requested directory, file name and format.");
                    if (payload.Value<string>("mode") == "export" && row.Value<string>("compareState") != "Equal")
                        throw new BridgeException("MutationVerificationFailed", "The exported mapping did not read back Equal.");
                }
                else if (change.Kind == "vci_mapping_remove")
                {
                    var target = VciTarget(change);
                    (workspace.MappedObjects.Find(target.Target) ?? throw new BridgeException("VciMappingNotFound", "The object is no longer mapped in this workspace.")).Delete();
                    if (workspace.MappedObjects.Find(target.Target) != null) throw new BridgeException("MutationVerificationFailed", "The mapping still exists after Delete.");
                    readback["mappingRemoved"] = true;
                }
                else
                {
                    var mode = payload.Value<string>("direction") == "WorkspaceToProject" ? SynchronizationMode.WorkspaceToProject : SynchronizationMode.ProjectToWorkspace;
                    var mappings = payload["objectPath"] == null ? VciMappings(workspace)
                        : new List<MappedObject> { workspace.MappedObjects.Find(VciTarget(change).Target) ?? throw new BridgeException("VciMappingNotFound", "The object is no longer mapped in this workspace.") };
                    var rows = new JArray(); readback["mappingsAfter"] = rows;
                    foreach (var mapping in mappings)
                    {
                        mapping.Synchronize(mode);
                        var row = VciMappingRow(mapping, root); rows.Add(row);
                        WriteChangeJournal(change); // A multi-mapping sync is a journaled sequence, not a transaction.
                        if (row.Value<string>("compareState") != "Equal") throw new BridgeException("MutationVerificationFailed", "Mapping " + rows.Count + " did not read back Equal after synchronization.");
                    }
                }
            }
            var diff = VciFileDiff(before, VciInventory(root), VciMayChange(vci));
            readback["files"] = diff;
            if (((JArray)diff["outsideScope"]).Count != 0) throw new BridgeException("MutationVerificationFailed", "Workspace files outside the previewed packages changed: " + string.Join(", ", diff["outsideScope"].Values<string>().Take(20)));
        }

        // Called before the checkpoint so a changed workspace refuses without any project write.
        private void VciRequireUnchangedFiles(EngineeringChange change, Project project)
        {
            var vci = (JObject)change.Preview["vci"];
            string root = VciRoot(vci.Value<string>("rootPath"), project, change, change.Kind == "vci_workspace_create");
            if (ChangeHash(VciInventory(root).ToString(Formatting.None)) != vci.Value<string>("filesSha256"))
                throw new BridgeException("StaleRevision", "Workspace files changed since preparation. Prepare a new preview; no write was applied.");
        }

        private static JArray VciFingerprint(Project project)
        {
            var service = VciService(project); int mappings = 0;
            var rows = new JArray(VciGroups(service).Select(g => g.Key));
            foreach (var entry in VciWorkspaces(service).OrderBy(w => w.Key, StringComparer.Ordinal))
            {
                var workspace = entry.Value;
                rows.Add(new JObject { ["path"] = entry.Key, ["rootPath"] = workspace.RootPath?.FullName, ["language"] = workspace.WorkspaceLanguage?.Name,
                    ["globalLibraryPath"] = workspace.GlobalLibraryPath?.FullName, ["deleteUnusedTypeVersionFromLibrary"] = workspace.DeleteUnusedTypeVersionFromLibrary,
                    ["mappings"] = new JArray(VciMappings(workspace).Select(m =>
                    {
                        if (++mappings > VciMaxEntries) throw new BridgeException("TraversalLimit", "Cannot fingerprint more than 20000 VCI mappings.");
                        return new JObject { ["object"] = QualificationNativeIdentity(m.EngineeringObject), ["directory"] = m.DirectoryPath?.ToString(), ["fileName"] = m.FileNameWithoutExtension, ["format"] = m.FileFormat };
                    })) });
            }
            return rows;
        }

        private static VersionControlInterface VciService(Project project) =>
            project.GetService<VersionControlInterface>() ?? throw new BridgeException("UnsupportedCapability", "The VCI service is not available for this project.");

        // Same path scheme as vci_workspace_inspect: "vci/<root group>/<user group>..." with escaped names.
        private static List<KeyValuePair<string, WorkspaceGroup>> VciGroups(VersionControlInterface service)
        {
            var groups = new List<KeyValuePair<string, WorkspaceGroup>>();
            void Visit(WorkspaceGroup group, string parentPath, int depth)
            {
                if (depth > 32 || groups.Count >= 2000) throw new BridgeException("TraversalLimit", "VCI workspace groups exceed 32 levels or 2000 groups.");
                string path = parentPath + "/" + Segment((group as WorkspaceSystemGroup)?.Name ?? (group as WorkspaceUserGroup)?.Name);
                groups.Add(new KeyValuePair<string, WorkspaceGroup>(path, group));
                foreach (var child in group.Groups) Visit(child, path, depth + 1);
            }
            Visit(service.WorkspaceGroup, "vci", 0);
            return groups;
        }

        private static List<KeyValuePair<string, Workspace>> VciWorkspaces(VersionControlInterface service)
        {
            var result = VciGroups(service).SelectMany(g => g.Value.Workspaces.Select(w => new KeyValuePair<string, Workspace>(g.Key + "/" + Segment(w.Name), w))).Take(2001).ToList();
            if (result.Count > 2000) throw new BridgeException("TraversalLimit", "The project has more than 2000 VCI workspaces.");
            return result;
        }

        private static Workspace VciWorkspace(VersionControlInterface service, string path)
        {
            var matches = VciWorkspaces(service).Where(w => w.Key == path).ToList();
            if (matches.Count != 1) throw new BridgeException("VciWorkspaceNotFound", "workspacePath must name exactly one workspace from vci_workspace_inspect.");
            return matches[0].Value;
        }

        private static List<MappedObject> VciMappings(Workspace workspace)
        {
            if (workspace.MappedObjects.Count > VciMaxEntries) throw new BridgeException("TraversalLimit", "The workspace has more than 20000 mappings.");
            return workspace.MappedObjects.ToList();
        }

        private NativeObjectTarget VciTarget(EngineeringChange change)
        {
            var target = ResolveObjectTarget(new JObject { ["projectName"] = change.Selector["projectName"], ["plcPath"] = change.Selector["plcPath"], ["objectPath"] = ChangeText(change.Payload, "objectPath", 4096) });
            if (!new[] { "block", "type", "tagTable", "technology" }.Contains(target.Identity.Value<string>("kind"), StringComparer.Ordinal))
                throw new BridgeException("UnsupportedObjectKind", "VCI changes select one exact block, PLC data type, tag table or technology object by objectPath.");
            if (ObjectIdentity.IsGeneratedPath(target.Identity.Value<string>("objectPath"))) throw new BridgeException("GeneratedObject", "Generated system-group objects are excluded.");
            return target;
        }

        // Project-ward sync rewrites the object, so it keeps the guards of the direct mutation kinds.
        private static void VciRequireWritableTarget(NativeObjectTarget target)
        {
            if (target.Scope?.Kind == "safetyUnit") throw new BridgeException("SafetyBoundary", "Safety-unit objects require the dedicated safety workflow.");
            if (target.Target is PlcBlock block) EnsureEditableBlock(block, false);
            if ((target.Target as PlcType)?.IsKnowHowProtected == true) throw new BridgeException("ProtectedObject", "Know-how protected type cannot be changed.");
        }

        private static JObject VciMappingRow(MappedObject mapping, string root, bool requireInsideRoot = false, IndividualObjectCompareResult status = null)
        {
            status = status ?? mapping.GetStatus();
            string directory = VciRelativeDirectory(root, mapping.DirectoryPath);
            if (requireInsideRoot && directory == null) throw new BridgeException("VciUnsafeRoot", "The mapped package lies outside the workspace root; its files cannot be backed up or verified.");
            return new JObject { ["object"] = QualificationNativeIdentity(mapping.EngineeringObject), ["directory"] = directory, ["nativeDirectory"] = mapping.DirectoryPath?.ToString(),
                ["fileName"] = mapping.FileNameWithoutExtension, ["format"] = mapping.FileFormat, ["compareState"] = status.CompareState.ToString(),
                ["compareDetails"] = status.IndividualObjectCompareDetails.ToString(), ["childMappingState"] = mapping.GetChildStatus().MappingState.ToString() };
        }

        // Relative to the root with '/' separators ("" is the root), or null outside it. A relative native value is read
        // against the root; DirectoryInfo.ToString keeps the original spelling, FullName would resolve it in this process.
        private static string VciRelativeDirectory(string root, DirectoryInfo directory)
        {
            if (directory == null) return null;
            string full = Path.GetFullPath(Path.Combine(root, directory.ToString())).TrimEnd('\\', '/');
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return "";
            return full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length + 1).Replace('\\', '/') : null;
        }

        private static string VciRoot(string root, Project project, EngineeringChange change, bool create)
        {
            if (!ProjectPathIsAbsolute(root)) throw new BridgeException("InvalidRequest", "The workspace root must be an absolute path.");
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (Path.GetPathRoot(root).TrimEnd('\\', '/') == root) throw new BridgeException("VciUnsafeRoot", "A drive or share root cannot be a workspace root.");
            // change.Directory is <artifacts>/changes/change-<id>; the whole artifact store is excluded.
            foreach (string owned in new[] { project.Path.DirectoryName, Path.GetDirectoryName(Path.GetDirectoryName(change.Directory)) })
                if (VciOverlaps(root, owned)) throw new BridgeException("VciUnsafeRoot", "The workspace root must not overlap the project directory or the artifact store: " + owned);
            for (var current = new DirectoryInfo(root); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new BridgeException("VciUnsafeRoot", "The workspace root or one of its parents is a symbolic link or junction.");
            if (File.Exists(root)) throw new BridgeException("VciUnsafeRoot", "The workspace root is a file.");
            if (create && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new BridgeException("VciUnsafeRoot", "A new workspace root must be absent or an empty directory.");
            return root;
        }

        private static bool VciOverlaps(string first, string second)
        {
            first = Path.GetFullPath(first).TrimEnd('\\', '/') + "\\"; second = Path.GetFullPath(second).TrimEnd('\\', '/') + "\\";
            return first.StartsWith(second, StringComparison.OrdinalIgnoreCase) || second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
        }

        private static bool VciSamePath(string path, string root) => path != null && string.Equals(Path.GetFullPath(path).TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase);

        private static string VciPayloadDirectory(JObject payload)
        {
            if (payload["directory"]?.Type != JTokenType.String) throw new BridgeException("InvalidRequest", "directory must be a string relative to the workspace root (empty for the root).");
            string value = payload.Value<string>("directory");
            var parts = value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (value.Length > 240 || !parts.All(VciSafeName)) throw new BridgeException("InvalidRequest", "directory must be relative to the workspace root without traversal, drive, stream or device names.");
            return string.Join("/", parts);
        }

        private static bool VciSafeName(string name) => name.Length <= 128 && name.Trim(' ', '.') == name && name.Length != 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !Regex.IsMatch(name.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool VciInPackage(string path, string directory, string fileName)
        {
            int slash = path.LastIndexOf('/');
            return string.Equals(slash < 0 ? "" : path.Substring(0, slash), directory, StringComparison.OrdinalIgnoreCase)
                && path.Substring(slash + 1).StartsWith(fileName + ".", StringComparison.OrdinalIgnoreCase);
        }

        // Create and delete only report file effects; other kinds may change their previewed packages and .vci configuration.
        private static Func<string, bool> VciMayChange(JObject vci) => path => vci.Value<bool>("reportOnly") || ("/" + path).Contains("/.vci/")
            || ((JArray)vci["writablePackages"]).Any(p => VciInPackage(path, p.Value<string>("directory"), p.Value<string>("fileName")));

        // Bounded SHA-256 inventory below the root; links are refused, never followed.
        private static JArray VciInventory(string root)
        {
            var files = new List<JObject>(); long bytes = 0; int entries = 0;
            if (!Directory.Exists(root)) return new JArray();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count != 0)
                foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new BridgeException("VciUnsafeRoot", "The workspace contains a symbolic link or junction: " + entry);
                    if (++entries > VciMaxEntries) throw new BridgeException("VciLimitExceeded", "The workspace root exceeds 20000 entries.");
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                    long length = new FileInfo(entry).Length;
                    if ((bytes += length) > VciMaxBytes) throw new BridgeException("VciLimitExceeded", "The workspace root exceeds 1 GiB.");
                    files.Add(new JObject { ["path"] = entry.Substring(root.Length + 1).Replace('\\', '/'), ["bytes"] = length, ["sha256"] = ChangeFileHash(entry) });
                }
            return new JArray(files.OrderBy(f => f.Value<string>("path"), StringComparer.Ordinal));
        }

        private static void VciBackup(EngineeringChange change, string root, JArray files, Func<string, bool> include)
        {
            string directory = Path.Combine(change.Directory, "vci-backup"); int count = 0;
            foreach (var file in files.Where(f => include(f.Value<string>("path"))))
            {
                string relative = file.Value<string>("path").Replace('/', '\\'), target = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(Path.Combine(root, relative), target);
                if (ChangeFileHash(target) != file.Value<string>("sha256")) throw new BridgeException("StaleRevision", "A workspace file changed while it was backed up.");
                count++;
            }
            if (count != 0) change.Backups.Add(new JObject { ["role"] = "vci-workspace-files", ["directory"] = directory, ["files"] = count,
                ["note"] = "Byte copies of every root file this change may overwrite or remove; hashes match the preview's filesBefore inventory. The project checkpoint does not restore workspace files." });
        }

        private static JObject VciFileDiff(JArray before, JArray after, Func<string, bool> mayChange)
        {
            var old = before.ToDictionary(f => f.Value<string>("path"), f => f.Value<string>("sha256"), StringComparer.Ordinal);
            var now = after.ToDictionary(f => f.Value<string>("path"), f => f.Value<string>("sha256"), StringComparer.Ordinal);
            var added = now.Keys.Where(p => !old.ContainsKey(p)).ToList();
            var removed = old.Keys.Where(p => !now.ContainsKey(p)).ToList();
            var changed = old.Keys.Where(p => now.ContainsKey(p) && now[p] != old[p]).ToList();
            var outside = added.Concat(removed).Concat(changed).Where(p => !mayChange(p)).ToList();
            return new JObject { ["added"] = new JArray(added.Take(500)), ["changed"] = new JArray(changed.Take(500)), ["removed"] = new JArray(removed.Take(500)),
                ["counts"] = new JObject { ["added"] = added.Count, ["changed"] = changed.Count, ["removed"] = removed.Count }, ["truncated"] = added.Count > 500 || changed.Count > 500 || removed.Count > 500,
                ["outsideScope"] = new JArray(outside.Take(500)), ["afterFiles"] = after.Count, ["afterSha256"] = ChangeHash(after.ToString(Formatting.None)) };
        }
    }
}
