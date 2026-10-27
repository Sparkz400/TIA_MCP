using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Compare;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.HW.Features;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly Dictionary<string, ManagedLibrary> _libraries = new Dictionary<string, ManagedLibrary>(StringComparer.Ordinal);
        private string _libraryGeneration = Guid.NewGuid().ToString("N");

        private sealed class ManagedLibrary
        {
            internal string Id, SourcePath, SourceHash, OriginalSourcePath, Directory, PackageId, PackageRetentionReference;
            internal ILibrary Native;
            internal Project Project;
            internal GlobalLibrary Global;
            internal bool Owned, Writable;
        }

        private JObject LibraryOpen(JObject args)
        {
            EnsureAttached();
            if (_libraries.Count >= 32) throw new BridgeException("LibraryLimitExceeded", "Close a library before opening more than 32 libraries in one session.");
            if (args.Value<string>("kind") == "project")
            {
                var project = SelectProject(args);
                return LibraryView(RegisterLibrary(project.ProjectLibrary, project, null, false, true));
            }
            string path = ChangeText(args, "path", 4096);
            if (!ProjectPathIsAbsolute(path) || !File.Exists(path)) throw new BridgeException("PackageInvalid", "An existing absolute native library/archive path on the TIA host is required.");
            path = Path.GetFullPath(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("PackageInvalid", "A linked native library input is not accepted.");
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".al21" && extension != ".zal21")
                throw new BridgeException("LibraryUpgradeRequired", "Read-only opening accepts V21 .al21/.zal21. Use a prepared library_upgrade operation for an older native library; newer versions cannot be down-converted.");
            string expected = ChangeText(args, "expectedSha256", 64);
            if (!string.Equals(LibrarySourceHash(path), expected, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("PackageChanged", "Library input hash differs from the supplied SHA-256.");
            string root = SharedMutationPlannedDestination("libraryOpen", () => Path.Combine(ArtifactDirectory(args), "l_" + Guid.NewGuid().ToString("N").Substring(0, 12)));
            string directory = Path.Combine(root, "work"), snapshot = Path.Combine(root, "source");
            Directory.CreateDirectory(directory); Directory.CreateDirectory(snapshot);
            string receiptPath = Path.Combine(root, "open-receipt.json");
            var receipt = new JObject { ["schemaVersion"] = 1, ["path"] = path, ["expectedSha256"] = expected, ["managedDirectory"] = directory, ["state"] = "opening", ["sourceReadOnly"] = true, ["packageId"] = args["packageId"], ["packageRetentionReference"] = args["packageRetentionReference"] };
            WriteLibraryRecord(receiptPath, receipt);
            bool writable = args.Value<bool?>("writableWorkingCopy") == true;
            GlobalLibrary library;
            string sourceHash;
            if (extension == ".zal21")
            {
                sourceHash = expected.ToLowerInvariant();
                if (new FileInfo(path).Length > 1024L * 1024 * 1024) throw new BridgeException("PackageLimitExceeded", "A native library archive exceeds the 1 GiB snapshot limit.");
                LibraryCopyArchiveSnapshot(path, Path.Combine(snapshot, Path.GetFileName(path)));
                if (ChangeFileHash(Path.Combine(snapshot, Path.GetFileName(path))) != sourceHash) throw new BridgeException("PackageChanged", "Archive changed while a managed snapshot was being created.");
                library = _portal.GlobalLibraries.Retrieve(new FileInfo(Path.Combine(snapshot, Path.GetFileName(path))), new DirectoryInfo(directory), writable ? OpenMode.ReadWrite : OpenMode.ReadOnly);
            }
            else
            {
                string sourceDirectory = Path.GetDirectoryName(path);
                sourceHash = LibraryDirectoryHash(sourceDirectory);
                LibraryCopyDirectory(sourceDirectory, snapshot);
                if (sourceHash != LibraryDirectoryHash(sourceDirectory) || sourceHash != LibraryDirectoryHash(snapshot))
                    throw new BridgeException("PackageChanged", "Library directory changed while a managed copy was being created.");
                if (!string.Equals(ChangeFileHash(Path.Combine(snapshot, Path.GetFileName(path))), expected, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("PackageChanged", "The managed native library marker no longer matches the approved SHA-256.");
                var expectedFiles = args["expectedFiles"] as JArray;
                if (args["packageId"] != null && expectedFiles == null) throw new BridgeException("PackageInvalid", "A staged native directory requires its original companion-file manifest.");
                if (expectedFiles != null) VerifyLibraryExpectedFiles(snapshot, expectedFiles);
                if (System.IO.Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories).Length < 2) throw new BridgeException("PackageInvalid", "A lone .al21 marker is not a complete native library directory. Supply the companion files or native archive.");
                LibraryCopyWorkingDirectory(snapshot, directory);
                library = _portal.GlobalLibraries.Open(new FileInfo(Path.Combine(directory, Path.GetFileName(path))), writable ? OpenMode.ReadWrite : OpenMode.ReadOnly);
            }
            var entry = RegisterLibrary(library, null, library, true, writable);
            entry.SourcePath = Path.Combine(snapshot, Path.GetFileName(path)); entry.SourceHash = sourceHash; entry.OriginalSourcePath = path; entry.Directory = directory;
            entry.PackageId = args.Value<string>("packageId"); entry.PackageRetentionReference = args.Value<string>("packageRetentionReference");
            var result = LibraryView(entry);
            result["originalSourceModified"] = false;
            result["sourceSha256"] = sourceHash;
            result["sourceSnapshotOwned"] = true;
            receipt["state"] = "opened"; receipt["library"] = result.DeepClone(); WriteLibraryRecord(receiptPath, receipt);
            return result;
        }

        private ManagedLibrary RegisterLibrary(ILibrary native, Project project, GlobalLibrary global, bool owned, bool writable)
        {
            var existing = _libraries.Values.FirstOrDefault(x => x.Native.Equals(native));
            if (existing != null) return existing;
            if (_libraries.Count >= 32) throw new BridgeException("LibraryLimitExceeded", "Close a library before opening more than 32 libraries in one session.");
            var result = new ManagedLibrary { Id = "library-" + Guid.NewGuid().ToString("N"), Native = native, Project = project, Global = global, Owned = owned, Writable = writable };
            _libraries.Add(result.Id, result);
            return result;
        }

        private void RequireLibraryHandleCapacity(int additional)
        {
            if (additional < 0 || additional > 32 - _libraries.Count)
                throw new BridgeException("LibraryLimitExceeded", "The operation would exceed 32 registered libraries. Close existing library handles before preparing or applying it.");
        }

        private ManagedLibrary ResolveLibrary(string id)
        {
            EnsureAttached();
            if (id == null || !_libraries.TryGetValue(id, out var result)) throw new BridgeException("LibraryIdentityMismatch", "Library ID is not registered in this session. Open/list it again after reconnecting.");
            if (result.Project != null && !_portal.Projects.Contains(result.Project)) throw new BridgeException("LibraryIdentityMismatch", "The project associated with this library is no longer open.");
            return result;
        }

        private JObject LibraryList(JObject args)
        {
            EnsureAttached();
            foreach (var project in _portal.Projects) RegisterLibrary(project.ProjectLibrary, project, null, false, true);
            foreach (var library in _portal.GlobalLibraries) RegisterLibrary(library, null, library, false, false);
            var result = Envelope();
            result["libraries"] = new JArray(_libraries.Values.Select(LibraryView));
            var available = _portal.GlobalLibraries.GetGlobalLibraryInfos().Take(201).ToList();
            result["availableLibraries"] = new JArray(available.Take(200).Select(x => new JObject { ["name"] = x.Name, ["path"] = x.Path?.FullName, ["family"] = x.LibraryType.ToString(), ["isOpen"] = x.IsOpen }));
            result["availableLibrariesTruncated"] = available.Count > 200;
            result["sessionGeneration"] = _libraryGeneration;
            result["availableCatalogBound"] = 200;
            return result;
        }

        private JObject LibraryView(ManagedLibrary entry)
        {
            var result = Envelope();
            result["libraryId"] = entry.Id; result["sessionGeneration"] = _libraryGeneration;
            result["kind"] = entry.Project != null ? "project" : entry.Global.GetType().Name;
            result["name"] = entry.Project?.Name ?? entry.Global.Name;
            result["path"] = entry.Project?.Path?.FullName ?? entry.Global.Path?.FullName;
            result["ownedWorkingCopy"] = entry.Owned; result["writable"] = entry.Writable && (entry.Global == null || !entry.Global.IsWriteProtected);
            result["sourcePath"] = entry.SourcePath; result["workingDirectory"] = entry.Directory;
            result["sourceSha256"] = entry.SourceHash;
            result["originalSourcePath"] = entry.OriginalSourcePath;
            result["packageId"] = entry.PackageId; result["packageRetentionReference"] = entry.PackageRetentionReference;
            result["revision"] = LibraryRevision(entry);
            result["revisionScope"] = "Library type/version/folder/master-copy metadata and native version GUIDs. Does not prove unmodified object document content.";
            return result;
        }

        private JObject LibraryClose(JObject args)
        {
            var entry = ResolveLibrary(ChangeText(args, "libraryId", 64));
            if (entry.Owned && entry.Global is UserGlobalLibrary user)
            {
                if (user.IsModified) throw new BridgeException("LibraryUnsaved", "Save the managed library through library_save before closing it.");
                user.Close();
            }
            _libraries.Remove(entry.Id);
            return new JObject { ["schemaVersion"] = 1, ["libraryId"] = entry.Id, ["released"] = true, ["nativeClosed"] = entry.Owned };
        }

        private void CloseManagedLibraries()
        {
            foreach (var entry in _libraries.Values)
                if (entry.Owned && entry.Global is UserGlobalLibrary user)
                    try { user.Close(); } catch { /* Connection disposal still runs. Journals retain checkpoints. */ }
            _libraries.Clear();
            _libraryChanges.Clear();
            _libraryGeneration = Guid.NewGuid().ToString("N");
        }

        private static LibraryTypeVersion ResolveLibraryVersion(ManagedLibrary library, JObject selector)
        {
            if (!Guid.TryParse(selector.Value<string>("typeGuid"), out var typeGuid) || !Guid.TryParse(selector.Value<string>("versionGuid"), out var versionGuid))
                throw new BridgeException("InvalidRequest", "Both exact typeGuid and versionGuid are required.");
            var version = library.Native.FindVersion(versionGuid);
            if (version == null || version.TypeObject.Guid != typeGuid) throw new BridgeException("LibraryIdentityMismatch", "The selected version GUID does not belong to this library/type.");
            return version;
        }

        private MasterCopy ResolveLibraryMaster(ManagedLibrary library, string path)
        {
            var budget = new ScanBudget(10000);
            var result = WalkLibraryMasters(library.Native.MasterCopyFolder, "/masterCopies", budget, 0).Where(x => x.Key == path).ToList();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Master-copy lookup exceeded 10000 entries.");
            if (result.Count != 1) throw new BridgeException("LibraryIdentityMismatch", "Use one exact masterCopyPath returned by library_query.");
            return result[0].Value;
        }

        private JObject LibraryQuery(JObject args)
        {
            if ((args.Value<string>("action") ?? "").StartsWith("hmi_", StringComparison.Ordinal)) return LibraryHmiQuery(args);
            var library = ResolveLibrary(ChangeText(args, "libraryId", 64));
            string query = args.Value<string>("query") ?? "elements";
            int walked = 0; JArray snapshot = null;
            var revision = query == "elements" ? LibraryRevision(library, out snapshot, out walked) : LibraryRevision(library);
            if (args["revision"] != null && args.Value<string>("revision") != revision) throw new BridgeException("PreviewStale", "Library metadata changed. Restart paging with the new revision.");
            int max = args.Value<int?>("maxItems") ?? 100, offset = args.Value<int?>("offset") ?? 0;
            if (max < 1 || max > 200 || offset < 0 || offset > 10000) throw new BridgeException("InvalidRequest", "maxItems must be 1..200 and offset 0..10000.");
            if (offset > 0 && args["revision"] == null) throw new BridgeException("InvalidRequest", "Paging after the first page requires its revision token.");
            JArray rows; bool complete = true;
            if (query == "elements" && walked <= 10000) rows = snapshot; // the revision walk is the item walk when it fits the item budget
            else if (query == "elements")
            {
                var budget = new ScanBudget(10000);
                rows = LibraryMetadata(library, budget);
                complete = !budget.Truncated;
                snapshot = null;
            }
            else if (query == "versions")
            {
                if (!Guid.TryParse(args.Value<string>("typeGuid"), out var id)) throw new BridgeException("InvalidRequest", "typeGuid is required.");
                var type = library.Native.FindType(id) ?? throw new BridgeException("LibraryIdentityMismatch", "Type GUID not found in this library.");
                rows = new JArray(type.Versions.Take(10001).Select(LibraryVersionInfo)); complete = rows.Count <= 10000;
            }
            else if (query == "dependencies")
            {
                var selected = ResolveLibraryVersion(library, args);
                rows = LibraryDependencyClosure(selected, 1000, out complete);
            }
            else if (query == "uses")
            {
                var selected = ResolveLibraryVersion(library, args);
                var project = SelectProject(args);
                rows = LibraryProjectUses(project, selected, out complete);
            }
            else if (query == "export")
            {
                if (library.Project == null) throw new BridgeException("TargetIncompatible", "Native document export is exposed for project-library versions. Copy the selected type into the project library first.");
                var selected = ResolveLibraryVersion(library, args);
                string directory = Path.Combine(ArtifactDirectory(args), "export-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string format = ChangeText(args, "format", 128);
                if (!selected.TypeObject.GetSupportedExportFormats().Contains(format)) throw new BridgeException("TargetIncompatible", "Requested format is not one of this native type's supported document export formats.");
                var transfer = selected.ExportAsDocuments(new DirectoryInfo(directory), "component", format, LibraryExportOptions.WithLibraryVersionInfoFile);
                string documentHash = LibraryDirectoryHash(directory);
                var documents = new JArray(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).Select(x =>
                {
                    var item = FileArtifact(new FileInfo(x)); item["relativePath"] = x.Substring(directory.Length + 1).Replace('\\', '/');
                    item["mediaType"] = Path.GetExtension(x).Equals(".xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" : "application/octet-stream"; return item;
                }));
                string manifestPath = Path.Combine(directory, "export-manifest.v1.json");
                WriteLibraryRecord(manifestPath, new JObject { ["schemaVersion"] = 1, ["version"] = LibraryVersionInfo(selected), ["format"] = format, ["documentsSha256"] = documentHash, ["files"] = documents });
                var manifest = FileArtifact(new FileInfo(manifestPath)); manifest["mediaType"] = "application/json";
                return new JObject { ["schemaVersion"] = 1, ["libraryId"] = library.Id, ["revision"] = revision, ["documentDirectory"] = directory, ["directory"] = directory, ["documentName"] = "component", ["sha256"] = LibraryDirectoryHash(directory), ["manifest"] = manifest,
                    ["transferState"] = transfer.TransferResultState.ToString(), ["files"] = new JArray(documents.Take(200)), ["filesTruncated"] = documents.Count > 200 };
            }
            else throw new BridgeException("InvalidRequest", "query must be elements, versions, dependencies, uses or export.");
            var result = Envelope();
            result["libraryId"] = library.Id; result["revision"] = revision; result["query"] = query;
            result["items"] = new JArray(rows.Skip(offset).Take(max)); result["totalCaptured"] = rows.Count;
            result["nextOffset"] = offset + max < rows.Count ? (JToken)(offset + max) : JValue.CreateNull();
            result["completeness"] = new JObject { ["scopeComplete"] = complete, ["truncated"] = !complete, ["scope"] = query == "uses" ? "Native library links in the selected project; other projects and textual references excluded." : "Native library metadata; protected or indirect runtime dependencies are not inferred." };
            // Rows taken from the revision snapshot are consistent with it by construction; a separate walk needs the recheck.
            if (snapshot == null && LibraryRevision(library) != revision) throw new BridgeException("PreviewStale", "Library changed during query. Retry inspection.");
            return result;
        }

        private JObject LibraryExport(JObject args)
        { var request = (JObject)args.DeepClone(); request["query"] = "export"; return LibraryQuery(request); }

        private static JArray LibraryProjectUses(Project project, LibraryTypeVersion version, out bool complete)
        {
            var rows = new JArray(); var budget = new ScanBudget(10000); complete = true;
            foreach (var entry in DeviceItems(project, budget))
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software;
                if (software == null) continue;
                var scope = software as IInstanceSearchScope;
                if (scope == null) { complete = false; continue; }
                foreach (var use in version.FindInstances(scope))
                {
                    if (!budget.Take()) break;
                    rows.Add(new JObject { ["version"] = LibraryVersionInfo(use.LibraryTypeVersion), ["nativeInstance"] = LibraryObjectDescription(use.LibraryTypeInstance), ["softwarePath"] = entry.Path, ["projectPath"] = project.Path?.FullName });
                }
            }
            complete &= !budget.Truncated; return rows;
        }

        private static JObject LibraryObjectDescription(object value)
        {
            var result = new JObject { ["engineeringType"] = value?.GetType().FullName };
            if (value is IEngineeringObject item)
            {
                foreach (string name in new[] { "Name", "Number", "Namespace" })
                    try { result[name] = JToken.FromObject(item.GetAttribute(name) ?? ""); } catch { }
            }
            return result;
        }

        private static JObject LibraryVersionInfo(LibraryTypeVersion version) => new JObject
        {
            ["kind"] = "version", ["typeGuid"] = version.TypeObject.Guid.ToString("D"), ["versionGuid"] = version.Guid.ToString("D"),
            ["typeName"] = version.TypeObject.Name, ["version"] = version.VersionNumber.ToString(), ["state"] = version.State.ToString(),
            ["isDefault"] = version.IsDefault, ["engineeringType"] = version.GetType().FullName, ["modifiedDate"] = version.ModifiedDate,
            ["author"] = version.Author, ["doNotUse"] = version.TypeObject.DoNotUse, ["setForUpdate"] = version.TypeObject.SetForUpdate
        };

        private static JArray LibraryDependencyClosure(LibraryTypeVersion root, int max, out bool complete)
        {
            var rows = new JArray(); var queue = new Queue<LibraryTypeVersion>(); var seen = new HashSet<Guid> { root.Guid }; queue.Enqueue(root);
            complete = true;
            while (queue.Count > 0)
            {
                var version = queue.Dequeue();
                if (rows.Count >= max) { complete = false; break; }
                var info = LibraryVersionInfo(version);
                var dependencies = version.Dependencies.Take(max + 1).ToList();
                if (dependencies.Count > max) { complete = false; break; }
                info["requiredVersionGuids"] = new JArray(dependencies.Select(x => x.Guid.ToString("D")));
                rows.Add(info);
                foreach (var dependency in dependencies)
                    if (seen.Add(dependency.Guid))
                    {
                        if (seen.Count > max) { complete = false; return rows; }
                        queue.Enqueue(dependency);
                    }
            }
            return rows;
        }

        private static IEnumerable<KeyValuePair<string, LibraryType>> WalkLibraryTypes(LibraryTypeFolder folder, string path, ScanBudget budget, int depth)
        {
            if (depth > 64 || !budget.Take()) { budget.Truncated = true; yield break; }
            foreach (var type in folder.Types) { if (!budget.Take()) yield break; yield return new KeyValuePair<string, LibraryType>(path + "/" + Segment(type.Name), type); }
            foreach (var child in folder.Folders) foreach (var item in WalkLibraryTypes(child, path + "/" + Segment(child.Name), budget, depth + 1)) yield return item;
        }

        private static IEnumerable<KeyValuePair<string, MasterCopy>> WalkLibraryMasters(MasterCopyFolder folder, string path, ScanBudget budget, int depth)
        {
            if (depth > 64 || !budget.Take()) { budget.Truncated = true; yield break; }
            foreach (var copy in folder.MasterCopies) { if (!budget.Take()) yield break; yield return new KeyValuePair<string, MasterCopy>(path + "/" + Segment(copy.Name), copy); }
            foreach (var child in folder.Folders) foreach (var item in WalkLibraryMasters(child, path + "/" + Segment(child.Name), budget, depth + 1)) yield return item;
        }

        private static JArray LibraryMetadata(ManagedLibrary library, ScanBudget budget)
        {
            var rows = new JArray();
            CaptureLibraryFolders(library.Native.TypeFolder, "/types", rows, budget, 0);
            CaptureLibraryFolders(library.Native.MasterCopyFolder, "/masterCopies", rows, budget, 0);
            foreach (var item in WalkLibraryTypes(library.Native.TypeFolder, "/types", budget, 0))
            {
                var type = item.Value;
                if (!CaptureAvailable(rows, "type", "path", item.Key, () => type.Name, () => {
                rows.Add(new JObject { ["kind"] = "type", ["path"] = item.Key, ["name"] = type.Name, ["typeGuid"] = type.Guid.ToString("D"), ["engineeringType"] = type.GetType().FullName, ["doNotUse"] = type.DoNotUse, ["setForUpdate"] = type.SetForUpdate, ["status"] = type.Status.ToString(),
                    ["documentExportAvailable"] = library.Project != null,
                    ["supportedExportFormats"] = library.Project != null ? new JArray(type.GetSupportedExportFormats()) : null,
                    ["documentExportLimitation"] = library.Project == null ? "The native document-export API is available only for project-library types. Copy this type into a project library before exporting documents." : null });
                foreach (var version in type.Versions) { if (!budget.Take()) break; var info = LibraryVersionInfo(version); info["path"] = item.Key + "/versions/" + version.Guid.ToString("D"); rows.Add(info); }
                })) continue;
            }
            foreach (var item in WalkLibraryMasters(library.Native.MasterCopyFolder, "/masterCopies", budget, 0))
                CaptureAvailable(rows, "masterCopy", "masterCopyPath", item.Key, () => item.Value.Name, () =>
                rows.Add(new JObject { ["kind"] = "masterCopy", ["masterCopyPath"] = item.Key, ["name"] = item.Value.Name, ["author"] = item.Value.Author, ["creationDate"] = item.Value.CreationDate,
                    ["contents"] = new JArray(item.Value.ContentDescriptions.Take(201).Select(x => new JObject { ["name"] = x.ContentName, ["type"] = x.ContentType.ToString() })) }));
            return new JArray(rows.OrderBy(x => x.Value<string>("path") ?? x.Value<string>("masterCopyPath"), StringComparer.Ordinal));
        }

        /// <summary>
        /// Upgraded vendor libraries can hold objects of products absent from this installation; TIA then fails to load
        /// their type ("Could not load type 'Dummy'"). Such an element stays in the metadata as an explicit unavailable row.
        /// </summary>
        private static bool CaptureAvailable(JArray rows, string kind, string pathKey, string path, Func<string> name, Action capture)
        {
            int before = rows.Count;
            try { capture(); return true; }
            catch (Exception error) when (!(error is BridgeException))
            {
                while (rows.Count > before) rows.RemoveAt(rows.Count - 1);
                string readable; try { readable = name(); } catch (Exception) { readable = null; }
                rows.Add(new JObject { ["kind"] = kind, [pathKey] = path, ["name"] = readable, ["unavailable"] = true, ["reason"] = error.Message });
                return false;
            }
        }

        private static void CaptureLibraryFolders(LibraryTypeFolder folder, string path, JArray rows, ScanBudget budget, int depth)
        {
            if (depth > 64 || !budget.Take()) { budget.Truncated = true; return; }
            rows.Add(new JObject { ["kind"] = "typeFolder", ["path"] = path, ["name"] = folder.Name });
            foreach (var child in folder.Folders) { CaptureLibraryFolders(child, path + "/" + Segment(child.Name), rows, budget, depth + 1); if (budget.Truncated) break; }
        }
        private static void CaptureLibraryFolders(MasterCopyFolder folder, string path, JArray rows, ScanBudget budget, int depth)
        {
            if (depth > 64 || !budget.Take()) { budget.Truncated = true; return; }
            rows.Add(new JObject { ["kind"] = "masterCopyFolder", ["path"] = path, ["name"] = folder.Name });
            foreach (var child in folder.Folders) { CaptureLibraryFolders(child, path + "/" + Segment(child.Name), rows, budget, depth + 1); if (budget.Truncated) break; }
        }

        private static string LibraryRevision(ManagedLibrary library) => LibraryRevision(library, out _, out _);

        private static string LibraryRevision(ManagedLibrary library, out JArray metadata, out int walked)
        {
            // Folders, types and versions each take entries, so a 10,000-type library needs more than 10,000 (scale plan).
            var budget = new ScanBudget(50000); metadata = LibraryMetadata(library, budget); walked = budget.Used;
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Library exceeds the 50000-entry revision budget. Split or filter the source library before preparing a mutation.");
            return ChangeHash(metadata.ToString(Formatting.None));
        }

        private JObject LibraryCompare(JObject args)
        {
            var left = ResolveLibrary(ChangeText(args, "leftLibraryId", 64)); var right = ResolveLibrary(ChangeText(args, "rightLibraryId", 64));
            var l = args["left"] as JObject; var r = args["right"] as JObject;
            var output = new JArray(); var budget = new ScanBudget(200);
            if (l != null && r != null)
            {
                var comparison = ResolveLibraryVersion(left, l).CompareTo(ResolveLibraryVersion(right, r));
                foreach (var property in comparison.Properties)
                { if (!budget.Take()) break; output.Add(new JObject { ["description"] = property.Description, ["status"] = property.DetailCompareStatus.ToString(), ["left"] = Convert.ToString(property.LeftValue), ["right"] = Convert.ToString(property.RightValue) }); }
            }
            else
            {
                var target = right.Native as Siemens.Engineering.Compare.ISoftwareCompareTarget ?? throw new BridgeException("TargetIncompatible", "Native library is not a compare target.");
                var comparison = left.Project != null ? left.Project.ProjectLibrary.CompareToLibrary(target) : left.Global.CompareToLibrary(target);
                FlattenLibraryComparison(comparison.RootElement, output, budget, 0);
            }
            return new JObject { ["schemaVersion"] = 1, ["items"] = output, ["truncated"] = budget.Truncated, ["scope"] = "Native comparison; compatibility and runtime behavior are not inferred." };
        }

        private static void FlattenLibraryComparison(LibraryCompareResultElement value, JArray output, ScanBudget budget, int depth)
        {
            if (depth > 64 || !budget.Take()) { budget.Truncated = true; return; }
            output.Add(new JObject { ["left"] = value.LeftName, ["right"] = value.RightName, ["status"] = value.ComparisonResult.ToString(), ["depth"] = depth });
            foreach (var child in value.Elements) { FlattenLibraryComparison(child, output, budget, depth + 1); if (budget.Truncated) break; }
        }

        private static string LibraryDirectoryHash(string directory)
        {
            var manifest = new JArray(); long bytes = 0; var stack = new Stack<string>(); stack.Push(Path.GetFullPath(directory));
            while (stack.Count != 0)
            {
                string current = stack.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("PackageInvalid", "Library directory contains a reparse point.");
                foreach (string child in System.IO.Directory.EnumerateDirectories(current)) stack.Push(child);
                foreach (string path in System.IO.Directory.EnumerateFiles(current))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("PackageInvalid", "Library directory contains a linked file.");
                    bytes += new FileInfo(path).Length;
                    if (manifest.Count >= 20000 || bytes > 4L * 1024 * 1024 * 1024) throw new BridgeException("PackageLimitExceeded", "Native library directory exceeds 20000 files or 4 GiB.");
                    manifest.Add(new JObject { ["path"] = path.Substring(directory.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'), ["sha256"] = ChangeFileHash(path) });
                }
            }
            return ChangeHash(new JArray(manifest.OrderBy(x => x.Value<string>("path"), StringComparer.Ordinal)).ToString(Formatting.None));
        }

        // A read-only source must still yield a writable owned working copy; the snapshot keeps its attributes.
        private static void LibraryCopyWorkingDirectory(string snapshot, string directory)
        {
            LibraryCopyDirectory(snapshot, directory);
            foreach (string file in System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        private static void LibraryCopyDirectory(string source, string destination)
        {
            source = Path.GetFullPath(source).TrimEnd('\\', '/'); destination = Path.GetFullPath(destination).TrimEnd('\\', '/');
            if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || source == destination)
                throw new BridgeException("PackageInvalid", "Managed destination must not be inside the source directory.");
            LibraryDirectoryHash(source); // Validate links and bounded size before copying.
            foreach (string file in System.IO.Directory.GetFiles(source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("PackageChanged", "A source file became a reparse point during copying.");
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            }
            foreach (string folder in System.IO.Directory.GetDirectories(source))
            { string target = Path.Combine(destination, Path.GetFileName(folder)); System.IO.Directory.CreateDirectory(target); LibraryCopyDirectory(folder, target); }
        }

        private static void LibraryCopyArchiveSnapshot(string source, string destination)
        {
            const long maximumBytes = 1024L * 1024 * 1024;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("PackageInvalid", "A linked native archive input is not accepted.");
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length > maximumBytes) throw new BridgeException("PackageLimitExceeded", "A native archive exceeds the 1 GiB snapshot limit.");
                byte[] buffer = new byte[81920]; long copied = 0; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    copied += count;
                    if (copied > maximumBytes) throw new BridgeException("PackageLimitExceeded", "A native archive exceeds the bounded snapshot limit.");
                    output.Write(buffer, 0, count);
                }
                output.Flush(true);
            }
        }

        private static void VerifyLibraryExpectedFiles(string directory, JArray expected)
        {
            if (expected.Count == 0 || expected.Count > 20000) throw new BridgeException("PackageInvalid", "Invalid expected library file manifest.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in expected)
            {
                string relative = file.Value<string>("relativePath");
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":") || relative.Split('/', '\\').Any(x => x == "..") || !paths.Add(relative.Replace('\\', '/')))
                    throw new BridgeException("PackageInvalid", "Invalid or duplicate companion path in expected manifest.");
                string path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length != file.Value<long>("bytes") || ChangeFileHash(path) != file.Value<string>("sha256"))
                    throw new BridgeException("PackageChanged", "Managed source snapshot differs from the staged companion manifest: " + relative);
            }
            if (Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length != expected.Count) throw new BridgeException("PackageChanged", "Managed source snapshot has an unmanifested or missing companion file.");
        }
    }
}
