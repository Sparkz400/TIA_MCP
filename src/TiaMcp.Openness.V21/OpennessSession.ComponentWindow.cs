using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject _componentWindowManifest;
        private string _componentWindowManifestHash;
        private bool _componentWindowInstallAllowed;
        private string _componentWindowDispatchStep;

        // Private worker protocol. A manifest describes paths only; no export, directory creation or project write occurs here.
        private JObject ComponentWindowPlan(JObject args)
        {
            PlcComponentQualificationGate.RequireEnabled(_componentQualificationEnabled); RequireComponentScratch(_scratchProject);
            if (_componentWindowManifest != null) throw new BridgeException("ResourceBusy", "This worker already has a bound component window.");
            string id = ChangeText(args, "id", 32); if (!Guid.TryParseExact(id, "N", out _)) throw new BridgeException("InvalidRequest", "Exact component window identity required.");
            string root = SharedMutationArtifactDirectory(args), planId = ChangeText(args, "planId", 64), preview = ChangeText(args, "planPreviewHash", 64);
            if (!_libraryChanges.TryGetValue(planId, out var plan) || plan.Kind != "library_integrate" || plan.PreviewHash != preview || !plan.Project.Equals(_scratchProject) || IsHmiLibraryTarget(plan.Payload))
                throw new BridgeException("InvalidComponentContract", "An exact current-session PLC integration plan is required.");
            if (plan.Source == null || plan.Source.Project != null || string.IsNullOrEmpty(plan.Source.SourcePath) || plan.Source.Writable)
                throw new BridgeException("MutationScopeIncomplete", "This qualification requires the exact read-only working global source and retained original snapshot.");
            string workspace = Path.Combine(root, "q", "c" + id.Substring(0, 12));
            if (Directory.Exists(workspace) || File.Exists(workspace)) throw new BridgeException("ComponentWorkspaceExists", "Planned descendant workspace already exists.");
            string source = Path.GetExtension(plan.Source.SourcePath).StartsWith(".zal", StringComparison.OrdinalIgnoreCase) ? plan.Source.SourcePath : Path.GetDirectoryName(plan.Source.SourcePath);
            string working = plan.Source.Directory ?? Path.GetDirectoryName(plan.Source.Global.Path.FullName);
            string original = Path.GetExtension(plan.Source.OriginalSourcePath).StartsWith(".zal", StringComparison.OrdinalIgnoreCase) ? plan.Source.OriginalSourcePath : Path.GetDirectoryName(plan.Source.OriginalSourcePath);
            var manifest = new JObject { ["schemaVersion"] = 1, ["id"] = id, ["componentId"] = "component-" + id,
                ["workspace"] = workspace, ["projectPath"] = plan.Project.Path.FullName, ["plcPath"] = plan.Payload["target"]["plcPath"].DeepClone(),
                ["planId"] = plan.Id, ["planPreviewHash"] = plan.PreviewHash, ["planDirectory"] = plan.Directory,
                ["sourceDirectory"] = working, ["sourceSnapshotPath"] = source, ["sourceSnapshotSha256"] = plan.Source.SourceHash,
                ["sourceSnapshotIsDirectory"] = Directory.Exists(source), ["sourceLibraryId"] = plan.Source.Id,
                ["originalSourcePath"] = original, ["originalSourceIsDirectory"] = Directory.Exists(original),
                ["adapterSha256"] = ChangeFileHash(Assembly.GetExecutingAssembly().Location), ["initialNativeProcessId"] = _processId,
                ["initialNativeSessionEpoch"] = _libraryGeneration,
                ["resources"] = new JArray(ComponentResource("project", Path.GetDirectoryName(plan.Project.Path.FullName), "targetProjectAndCompanions"),
                    ComponentResource("workspace", plan.Directory, "reviewedPlanAndSourceChecks"), ComponentResource("library", working, "pinnedWorkingSource"),
                    ComponentResource("library", source, "immutableSourceSnapshot"), ComponentResource("library", original, "immutableOriginalSource"), ComponentResource("workspace", workspace, "allPreparationJournalsClaimsCheckpointsReadbackAndRecovery")) };
            return new JObject { ["manifest"] = manifest, ["manifestSha256"] = SharedMutationHash(manifest), ["nativeDispatched"] = false };
        }

        private JObject ComponentWindowBind(JObject args)
        {
            PlcComponentQualificationGate.RequireEnabled(_componentQualificationEnabled); EnsureAttached();
            var manifest = args["manifest"] as JObject ?? throw new BridgeException("MutationScopeIncomplete", "Exact complete manifest required.");
            string hash = ChangeText(args, "manifestSha256", 64);
            if (SharedMutationHash(manifest) != hash || manifest.Value<string>("adapterSha256") != ChangeFileHash(Assembly.GetExecutingAssembly().Location))
                throw new BridgeException("MutationScopeIncomplete", "Manifest/adapter identity differs.");
            var reservation = ComponentWindowReadReservation(args, manifest, hash);
            if (_componentWindowManifest != null && _componentWindowManifestHash != hash) throw new BridgeException("ResourceBusy", "Worker already belongs to another component window.");
            _componentWindowManifest = (JObject)manifest.DeepClone(); _componentWindowManifestHash = hash;
            _componentWindowInstallAllowed = reservation.Value<bool>("installationAllowed");
            if (_componentWindowInstallAllowed)
            {
                RequireComponentScratch(_scratchProject);
                if (manifest.Value<int>("initialNativeProcessId") != _processId || manifest.Value<string>("initialNativeSessionEpoch") != _libraryGeneration ||
                    _scratchProject.Path.FullName != manifest.Value<string>("projectPath")) throw new BridgeException("ComponentFreshSessionRequired", "Installation binding does not match the exact original native session.");
            }
            return new JObject { ["bound"] = true, ["manifestSha256"] = hash, ["installationAllowed"] = _componentWindowInstallAllowed, ["processId"] = _processId, ["sessionEpoch"] = _libraryGeneration };
        }

        private static JObject ComponentWindowReadReservation(JObject args, JObject manifest, string hash)
        {
            string path = ChangeText(args, "reservationPath", 8192), expected = ChangeText(args, "reservationSha256", 64);
            if (!Path.IsPathRooted(path) || !File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024 || ChangeFileHash(path) != expected)
                throw new BridgeException("ComponentReservationRequired", "Exact durable parent revision is missing or changed.");
            var record = JObject.Parse(File.ReadAllText(path));
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)));
            string expectedWorkspace = Path.Combine(root, "q", "c" + manifest.Value<string>("id").Substring(0, 12));
            if (Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) != "engineering-component-windows" ||
                Path.GetFileName(Path.GetDirectoryName(path)) != manifest.Value<string>("id") || manifest.Value<string>("workspace") != expectedWorkspace ||
                record.Value<string>("id") != manifest.Value<string>("id") || record.Value<string>("manifestSha256") != hash ||
                !JToken.DeepEquals(record["manifest"], manifest) || new[] { "released", "recoveryReconciled" }.Contains(record.Value<string>("state")))
                throw new BridgeException("ComponentReservationRequired", "Exact canonical parent reservation/manifest is required.");
            using (var host = Process.GetProcessById(record.Value<int>("ownerHostProcessId")))
                if (host.HasExited || host.StartTime.ToUniversalTime() != record.Value<DateTime>("ownerHostBirthUtc").ToUniversalTime())
                    throw new BridgeException("ComponentOwnerMismatch", "The durable exact Host owner is not alive.");
            return record;
        }

        private JObject ComponentWindowScope(JObject args)
        {
            PlcComponentQualificationGate.RequireEnabled(_componentQualificationEnabled);
            if (_componentWindowManifest == null || args.Value<string>("manifestSha256") != _componentWindowManifestHash)
                throw new BridgeException("MutationScopeIncomplete", "A complete durable component manifest must be bound in Host and worker.");
            string step = ChangeText(args, "step", 32); bool install = new[] { "start", "applyLibrary", "verifyImport", "prepareGraphical", "applyGraphical", "verifyInstallation" }.Contains(step);
            if (install && !_componentWindowInstallAllowed) throw new BridgeException("ComponentFreshSessionRequired", "Owner resume never resumes installation.");
            ComponentWindowVerifySource();
            if (install)
            {
                RequireComponentScratch(_scratchProject);
                if (_scratchProject.Path.FullName != _componentWindowManifest.Value<string>("projectPath")) throw new BridgeException("ComponentResourceEscape", "Component target changed.");
            }
            var scope = new JObject { ["operation"] = "component_window_step", ["requestSha256"] = SharedMutationHash(args), ["sessionEpoch"] = _watchEpoch,
                ["adapterSha256"] = ChangeFileHash(Assembly.GetExecutingAssembly().Location), ["processId"] = _processId,
                ["resources"] = _componentWindowManifest["resources"].DeepClone(),
                ["observationSha256"] = SharedMutationHash(new JObject { ["manifestSha256"] = _componentWindowManifestHash, ["step"] = step, ["request"] = args["request"]?.DeepClone(), ["sourceSha256"] = _componentWindowManifest["sourceSnapshotSha256"].DeepClone() }) };
            scope["scopeSha256"] = SharedMutationHash(scope); return scope;
        }

        private void ComponentWindowAuthorizeDispatch(JObject args)
        {
            var record = ComponentWindowReadReservation(args, _componentWindowManifest, _componentWindowManifestHash);
            var child = (record["children"] as JArray)?.Last as JObject;
            bool recoveryOnly = child != null && new[] { "recover", "observeRestored", "observeRecoveryOriginal" }.Contains(child.Value<string>("step")) && !record.Value<bool>("installationAllowed");
            if ((record.Value<string>("state") != "active" && !(record.Value<string>("state") == "outcomeUnknown" && recoveryOnly)) || child == null || child.Value<string>("state") != "dispatchIntent" ||
                child.Value<string>("step") != _sharedMutationArguments.Value<string>("step") || child.Value<string>("requestSha256") != SharedMutationHash(_sharedMutationArguments) ||
                record.Value<bool>("installationAllowed") != _componentWindowInstallAllowed)
                throw new BridgeException("ComponentReservationRequired", "Dispatch does not bind the current durable exact child intent.");
            _componentWindowDispatchStep = child.Value<string>("step");
        }

        private JObject ComponentWindowExecute(JObject args)
        {
            string step = ChangeText(args, "step", 32);
            if (_componentWindowDispatchStep != step || !_sharedMutationDispatched) throw new BridgeException("ComponentReservationRequired", "Only the durably admitted native dispatch may enter a component step.");
            var request = args["request"] as JObject ?? new JObject(); JObject result, observation = null;
            string workspace = _componentWindowManifest.Value<string>("workspace");
            Directory.CreateDirectory(workspace);
            if (step == "start")
            {
                observation = ComponentWindowCapture("before", _componentWindowManifest.Value<string>("projectPath"));
                var payload = new JObject { ["schemaVersion"] = 1, ["action"] = "start", ["planChangeId"] = _componentWindowManifest["planId"].DeepClone(), ["planPreviewHash"] = _componentWindowManifest["planPreviewHash"].DeepClone() };
                result = ComponentQualificationPrepare(new JObject { ["kind"] = PlcComponentQualificationGate.Kind, ["payload"] = payload, ["artifactDirectory"] = workspace });
            }
            else if (step == "applyLibrary" || step == "applyGraphical") result = ComponentQualificationApply(request);
            else if (step == "verifyImport" || step == "prepareGraphical" || step == "verifyInstallation")
            {
                result = ComponentQualificationPrepare(new JObject { ["kind"] = PlcComponentQualificationGate.Kind, ["payload"] = new JObject { ["schemaVersion"] = 1, ["action"] = "advance", ["changeId"] = "lib-" + _componentWindowManifest.Value<string>("componentId") } });
                if (step == "verifyInstallation") observation = ComponentWindowCapture("after", _componentWindowManifest.Value<string>("projectPath"));
            }
            else if (step == "observeOriginal")
            { result = ComponentWindowDiskStatus(); observation = ComponentWindowCapture("original-fresh", _componentWindowManifest.Value<string>("projectPath")); }
            else if (step == "observeRecoveryOriginal")
            { result = ComponentWindowDiskStatus(); observation = ComponentWindowCapture("recovery-original-fresh", _componentWindowManifest.Value<string>("projectPath")); }
            else if (step == "recover")
            {
                if (!_ownsPortal || _portal.Projects.Count != 0) throw new BridgeException("ProjectIsolationRequired", "First-checkpoint recovery requires a fresh empty owned Portal.");
                var record = ComponentWindowRecoveryStatus(); var checkpoint = record["initialCheckpoint"] as JObject;
                if (checkpoint == null || checkpoint.Value<string>("changeId") != record["library"]["prepared"].Value<string>("changeId") || checkpoint.Value<string>("previewHash") != record["library"]["prepared"].Value<string>("previewHash"))
                    throw new BridgeException("ComponentCheckpointMissing", "Only the exact first inner library checkpoint may be recovered.");
                string destination = Path.Combine(workspace, "recovery");
                if (Directory.Exists(destination) || File.Exists(destination)) throw new BridgeException("ComponentReplayRefused", "The one admitted recovery destination already exists.");
                _sharedMutationDestinations["libraryRecovery:" + checkpoint.Value<string>("originalPath")] = destination;
                result = LibraryChangeRestore(new JObject { ["changeId"] = checkpoint["changeId"].DeepClone(), ["previewHash"] = checkpoint["previewHash"].DeepClone(), ["artifactDirectory"] = Path.Combine(workspace, "changes") });
                result["firstCheckpointEvidence"] = checkpoint.DeepClone(); result["coordinatorBeforeRecovery"] = record.DeepClone();
                string recovered = result["resources"][0]["project"].Value<string>("path"); observation = ComponentWindowCapture("restored", recovered);
            }
            else if (step == "observeRestored")
            {
                var recovery = JObject.Parse(File.ReadAllText(Path.Combine(workspace, "responses", "recover.json")));
                string recovered = recovery["resources"][0]["project"].Value<string>("path"); result = ComponentWindowDiskStatus(); observation = ComponentWindowCapture("restored-fresh", recovered);
            }
            else throw new BridgeException("InvalidRequest", "Unknown component lifetime child.");
            result["componentWindow"] = new JObject { ["manifestSha256"] = _componentWindowManifestHash, ["step"] = step,
                ["processId"] = _processId, ["sessionEpoch"] = _libraryGeneration, ["observation"] = observation };
            string outputs = Path.Combine(workspace, "responses"); Directory.CreateDirectory(outputs);
            string output = Path.Combine(outputs, step + ".json"); if (File.Exists(output)) throw new BridgeException("ComponentReplayRefused", "An exact child response is already retained.");
            LibraryJournalStore.Write(output, result); _componentWindowDispatchStep = null; return result;
        }

        private JObject ComponentWindowDiskStatus() => ComponentQualificationView(PlcComponentCoordinator.Open(_componentWindowManifest.Value<string>("workspace"), null).Status());
        private JObject ComponentWindowRecoveryStatus()
        {
            string workspace = _componentWindowManifest.Value<string>("workspace");
            var coordinator = PlcComponentCoordinator.Open(workspace, null); var record = coordinator.Status();
            string id = record["library"]?["prepared"]?.Value<string>("changeId");
            if (string.IsNullOrWhiteSpace(id)) throw new BridgeException("ComponentCheckpointMissing", "No first inner import was prepared.");
            var observed = LibraryChangeStatus(new JObject { ["changeId"] = id, ["artifactDirectory"] = Path.Combine(workspace, "changes") });
            var anchor = coordinator.RecoveryAnchor(observed); var statuses = new JArray(observed.DeepClone());
            string typed = record["graphical"]?["prepared"]?.Value<string>("changeId");
            if (!string.IsNullOrWhiteSpace(typed)) statuses.Add(ChangeStatus(new JObject { ["changeId"] = typed, ["artifactDirectory"] = Path.Combine(workspace, "changes") }));
            anchor["innerRecoveryStatuses"] = statuses; return anchor;
        }
        private JObject ComponentWindowRead(JObject args)
        {
            if (_componentWindowManifest == null) throw new BridgeException("ComponentReservationRequired", "A bound component parent is required.");
            ComponentWindowReadReservation(args, _componentWindowManifest, _componentWindowManifestHash);
            string operation = ChangeText(args, "operation", 32);
            if (!new[] { "block_read", "type_read", "tag_table_read", "software_inspect" }.Contains(operation)) throw new BridgeException("InvalidRequest", "Only bounded readback operations inherit this reservation.");
            if (!_ownsPortal || _portal.Projects.Count != 1) throw new BridgeException("OwnedScratchRequired", "One exact owned qualification project is required.");
            var project = _portal.Projects.First(); EnsureProjectOffline(project);
            if (project.Path.FullName != _componentWindowManifest.Value<string>("projectPath") &&
                !project.Path.FullName.StartsWith(Path.Combine(_componentWindowManifest.Value<string>("workspace"), "recovery") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ComponentResourceEscape", "Readback project is outside the admitted original/recovery resources.");
            var request = (JObject)(args["arguments"] as JObject ?? new JObject()).DeepClone();
            if (request.Value<string>("format") != null) throw new BridgeException("InvalidRequest", "Component readback uses the default native XML export only.");
            if (request.Value<string>("projectName") != null && request.Value<string>("projectName") != project.Name ||
                request.Value<string>("plcPath") != null && request.Value<string>("plcPath") != _componentWindowManifest.Value<string>("plcPath"))
                throw new BridgeException("ComponentResourceEscape", "Readback selector escaped the admitted component project/PLC.");
            request["projectName"] = project.Name; request["plcPath"] = _componentWindowManifest["plcPath"].DeepClone();
            request["artifactDirectory"] = Path.Combine(_componentWindowManifest.Value<string>("workspace"), "captures", "external-" + Guid.NewGuid().ToString("N"));
            using (_portal.ExclusiveAccess("TIA MCP admitted component peer readback"))
                return operation == "block_read" ? BlockRead(request) : operation == "type_read" ? TypeRead(request) : operation == "tag_table_read" ? TagTableRead(request) : SoftwareInspect(request);
        }
        private JObject ComponentWindowSourceObservation(JObject args)
        {
            if (_componentWindowManifest == null || args.Value<string>("manifestSha256") != _componentWindowManifestHash) throw new BridgeException("ComponentReservationRequired", "Exact bound component window required.");
            return new JObject { ["manifestSha256"] = _componentWindowManifestHash, ["sourceSnapshotSha256"] = ComponentWindowVerifySource(), ["processId"] = _processId, ["sessionEpoch"] = _libraryGeneration };
        }
        private string ComponentWindowVerifySource()
        {
            string path = _componentWindowManifest.Value<string>("sourceSnapshotPath");
            string hash = _componentWindowManifest.Value<bool>("sourceSnapshotIsDirectory") ? LibraryDirectoryHash(path) : ChangeFileHash(path);
            string original = _componentWindowManifest.Value<string>("originalSourcePath");
            string originalHash = _componentWindowManifest.Value<bool>("originalSourceIsDirectory") ? LibraryDirectoryHash(original) : ChangeFileHash(original);
            if (hash != _componentWindowManifest.Value<string>("sourceSnapshotSha256") || originalHash != hash) throw new BridgeException("PackageChanged", "The original pinned component source or retained snapshot changed."); return hash;
        }
        private JObject ComponentWindowCapture(string phase, string expectedPath)
        {
            if (!_ownsPortal || _portal.Projects.Count != 1) throw new BridgeException("OwnedScratchRequired", "Native observation requires one exact owned project.");
            var project = _portal.Projects.First(); EnsureProjectOffline(project);
            if (project.Path.FullName != expectedPath || project.IsModified) throw new BridgeException("ComponentReadbackMismatch", "Native observation requires the exact saved original/recovered project.");
            string path = Path.Combine(_componentWindowManifest.Value<string>("workspace"), "captures", phase);
            if (Directory.Exists(path)) throw new BridgeException("ComponentReplayRefused", "Native observation phase has already been attempted."); Directory.CreateDirectory(path);
            var selector = new JObject { ["projectName"] = project.Name, ["plcPath"] = _componentWindowManifest["plcPath"].DeepClone(), ["maxItems"] = 2000, ["artifactDirectory"] = path };
            var inventory = SoftwareInspect(selector);
            if (inventory["completeness"].Value<bool?>("scopeComplete") != true || inventory["completeness"].Value<bool?>("truncated") != false ||
                ((JArray)inventory["softwareScopes"]).Count != 1 || ((JArray)inventory["technologyObjects"]).Count != 0)
                throw new BridgeException("ComponentScopeIncomplete", "This bounded profile requires a complete single standard PLC scope without technology objects.");
            var objects = new JObject();
            foreach (var kind in new[] { "block", "type", "tagTable" })
            {
                string collection = kind == "block" ? "blocks" : kind == "type" ? "types" : "tagTables", field = kind + "Path";
                foreach (JObject item in (JArray)inventory[collection])
                {
                    string identity = item.Value<string>("path"), key = kind + ":" + identity;
                    if (objects[key] != null) throw new BridgeException("ComponentScopeIncomplete", "Native peer identity is ambiguous.");
                    var request = (JObject)selector.DeepClone(); request[field] = identity;
                    var exported = kind == "block" ? BlockRead(request) : kind == "type" ? TypeRead(request) : TagTableRead(request);
                    objects[key] = new JObject { ["kind"] = kind, ["path"] = identity, ["artifact"] = exported["artifact"].DeepClone() };
                }
            }
            if (objects.Count == 0 || project.IsModified) throw new BridgeException("ComponentScopeIncomplete", "Empty or modified native observation cannot qualify.");
            var versions = new JArray();
            JArray pins = phase == "before" ? (JArray)_libraryChanges[_componentWindowManifest.Value<string>("planId")].Preview["integrationPlan"]["versions"]
                : (JArray)ComponentWindowDiskStatus()["record"]["contract"]["plan"]["versions"];
            var plc = SelectPlc(selector); var scan = new ScanBudget(50000); var paths = new Dictionary<IEngineeringObject, string>();
            foreach (var scope in SoftwareScopes(plc, scan))
            {
                foreach (var block in ScopeBlocks(scope, scan)) paths[block.Item] = block.Path;
                foreach (var type in ScopeTypes(scope, scan)) paths[type.Item] = type.Path;
            }
            if (scan.Truncated) throw new BridgeException("ComponentScopeIncomplete", "Exact native library-use inventory is incomplete.");
            foreach (JObject pin in pins)
            {
                var version = project.ProjectLibrary.FindVersion(Guid.Parse(pin.Value<string>("versionGuid")));
                var row = new JObject { ["typeGuid"] = pin["typeGuid"].DeepClone(), ["versionGuid"] = pin["versionGuid"].DeepClone(), ["exists"] = version != null }; versions.Add(row);
                if (version == null) continue;
                if (version.TypeObject.Guid != Guid.Parse(pin.Value<string>("typeGuid"))) throw new BridgeException("ComponentReadbackMismatch", "Reopened native dependency type/version differs.");
                var uses = version.FindInstances(plc.Software).Take(1001).ToArray();
                if (uses.Length == 0 || uses.Length > 1000 || uses.Any(u => u.LibraryTypeVersion.Guid != version.Guid || !(u.LibraryTypeInstance is IEngineeringObject item) || !paths.ContainsKey(item)))
                    throw new BridgeException("ComponentReadbackMismatch", "Exact native dependency-use identities are incomplete.");
                row["objectPaths"] = new JArray(uses.Select(u => paths[(IEngineeringObject)u.LibraryTypeInstance]).OrderBy(p => p, StringComparer.Ordinal));
                var info = LibraryVersionInfo(version); row["state"] = info["state"].DeepClone(); row["doNotUse"] = info["doNotUse"].DeepClone();
                string export = Path.Combine(path, version.Guid.ToString("N") + "-library.xml");
                version.Export(new FileInfo(export), ExportOptions.WithDefaults, DocumentInfoOptions.None); row["artifact"] = ChangeArtifact(export);
            }
            var libraryBudget = new ScanBudget(2000);
            var libraryMetadata = LibraryMetadata(new ManagedLibrary { Native = project.ProjectLibrary, Project = project }, libraryBudget);
            var presentVersions = new HashSet<string>(versions.Where(v => v.Value<bool>("exists")).Select(v => v.Value<string>("versionGuid")), StringComparer.OrdinalIgnoreCase);
            if (libraryBudget.Truncated || libraryMetadata.Any(r => r.Value<string>("kind") == "masterCopy") ||
                libraryMetadata.Where(r => r.Value<string>("kind") == "version").Any(v => !presentVersions.Contains(v.Value<string>("versionGuid"))) ||
                libraryMetadata.Where(r => r.Value<string>("kind") == "type").Any(t => !libraryMetadata.Any(v => v.Value<string>("kind") == "version" && v.Value<string>("typeGuid") == t.Value<string>("typeGuid"))))
                throw new BridgeException("ComponentScopeIncomplete", "This bounded profile refuses unrelated project-library versions, empty types or master copies.");
            var peerMetadata = ComponentWindowPeerMetadata(project, plc, path, out var watchForceTables);
            if (project.IsModified) throw new BridgeException("ComponentReadbackMismatch", "Peer capture changed the saved project.");
            using (var process = Process.GetProcessById(_processId.Value))
                return new JObject { ["phase"] = phase, ["projectPath"] = project.Path.FullName, ["plcPath"] = _componentWindowManifest["plcPath"].DeepClone(),
                    ["processId"] = _processId, ["nativeBirthUtc"] = process.StartTime.ToUniversalTime().ToString("O"), ["sessionEpoch"] = _libraryGeneration,
                    ["projectFileSha256"] = OpenProjectFileHash(project.Path.FullName), ["scopeComplete"] = true, ["isModified"] = false, ["inventory"] = inventory, ["objects"] = objects, ["componentVersions"] = versions,
                    ["peerMetadata"] = peerMetadata, ["watchForceTables"] = watchForceTables, ["projectLibrary"] = new JObject { ["scopeComplete"] = true, ["metadata"] = libraryMetadata } };
        }
        private JObject ComponentWindowPeerMetadata(Project project, PlcSelection plc, string directory, out JObject tables)
        {
            tables = new JObject(); var groups = new JObject();
            foreach (var group in WatchGroups(plc.Software.WatchAndForceTableGroup, "root", 0))
            {
                if (groups.Count >= 1000 || tables.Count + group.Item1.WatchTables.Count + group.Item1.ForceTables.Count > 1000 || groups[group.Item2] != null)
                    throw new BridgeException("ComponentScopeIncomplete", "Watch/force peer identity or count exceeds the complete bounded profile.");
                groups[group.Item2] = group.Item1.Name;
                foreach (var watch in group.Item1.WatchTables)
                {
                    string identity = "watch:" + group.Item2 + "/" + Segment(watch.Name), export = Path.Combine(directory, "watch-" + tables.Count + ".xml");
                    if (tables[identity] != null) throw new BridgeException("ComponentScopeIncomplete", "Duplicate watch peer identity.");
                    watch.Export(new FileInfo(export), ExportOptions.WithDefaults);
                    tables[identity] = new JObject { ["kind"] = "watch", ["path"] = group.Item2 + "/" + Segment(watch.Name), ["group"] = group.Item2, ["name"] = watch.Name, ["artifact"] = ChangeArtifact(export) };
                }
                foreach (var force in group.Item1.ForceTables)
                {
                    string identity = "force:" + group.Item2 + "/" + Segment(force.Name), export = Path.Combine(directory, "force-" + tables.Count + ".xml");
                    if (tables[identity] != null) throw new BridgeException("ComponentScopeIncomplete", "Duplicate force peer identity.");
                    force.Export(new FileInfo(export), ExportOptions.WithDefaults);
                    tables[identity] = new JObject { ["kind"] = "force", ["path"] = group.Item2 + "/" + Segment(force.Name), ["group"] = group.Item2, ["name"] = force.Name, ["artifact"] = ChangeArtifact(export) };
                }
            }
            var ownerBudget = new ScanBudget(10000); var owners = DeviceItems(project, ownerBudget).ToArray();
            if (ownerBudget.Truncated) throw new BridgeException("ComponentScopeIncomplete", "Hardware/access owner inventory is incomplete.");
            var softwareOwners = new JArray(); var access = new JArray();
            foreach (var owner in owners.OrderBy(o => o.Path, StringComparer.Ordinal))
            {
                var software = owner.Item.GetService<SoftwareContainer>()?.Software;
                if (software != null)
                {
                    if (!Equals(software, plc.Software)) throw new BridgeException("ComponentScopeIncomplete", "This bounded profile cannot omit another PLC/HMI software owner.");
                    softwareOwners.Add(new JObject { ["owner"] = owner.Path, ["type"] = software.GetType().FullName });
                }
                var manager = owner.Item.GetService<WatchAndForceTableAccessManager>();
                access.Add(new JObject { ["owner"] = owner.Path, ["available"] = manager != null,
                    ["watch"] = manager == null ? null : new JArray(manager.WatchtableAccessRules.Select(r => new JObject { ["table"] = r.WatchTable.Name, ["access"] = r.Access.ToString() })),
                    ["force"] = manager == null ? null : new JArray(manager.ForcetableAccessRules.Select(r => new JObject { ["table"] = r.ForceTable.Name, ["access"] = r.Access.ToString() })) });
            }
            if (softwareOwners.Count != 1) throw new BridgeException("ComponentScopeIncomplete", "Exactly one native PLC software owner must be captured.");
            return new JObject { ["hardware"] = CaptureHardwareEngineering(project), ["softwareOwners"] = softwareOwners, ["watchGroups"] = groups, ["vci"] = QualificationVciCapture(project),
                ["watchTableInventory"] = new JArray(tables.Properties().Select(p => p.Name).OrderBy(p => p, StringComparer.Ordinal)), ["access"] = access,
                ["languages"] = new JObject { ["active"] = new JArray(project.LanguageSettings.ActiveLanguages.Select(l => l.Culture.Name)),
                    ["reference"] = project.LanguageSettings.ReferenceLanguage.Culture.Name, ["editing"] = project.LanguageSettings.EditingLanguage.Culture.Name } };
        }
        private static JObject ComponentResource(string kind, string path, string effect) => new JObject { ["kind"] = kind, ["path"] = Path.GetFullPath(path), ["effect"] = effect };
    }
}
