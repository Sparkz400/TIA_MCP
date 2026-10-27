using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Simulation;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private IDisposable _sharedMutationLease;
        private string _sharedMutationToken, _sharedMutationOperation;
        private JObject _sharedMutationArguments, _sharedMutationScope;
        private bool _sharedMutationDispatched;
        private readonly Dictionary<string, string> _sharedMutationDestinations = new Dictionary<string, string>(StringComparer.Ordinal);

        private static bool IsSharedMutation(string operation) => operation == "change_apply" || operation == "change_restore" ||
            operation == "simulation_download" || operation == "simulation_provision_scratch_credential" || operation == "library_open" ||
            operation == "scratch_create" || operation == "project_retrieve" || operation == "project_copy" || operation == "watch_fixture_seed" || operation == "watch_authoring_source_negative" || operation == "component_window_step" || operation == "plc_station_upload";

        private JObject SharedMutationBegin(JObject args)
        {
            EnsureAttached();
            if (_sharedMutationLease != null || _watchLease != null) throw new BridgeException("ResourceBusy", "A native mutation already owns exclusive access.");
            string operation = ChangeText(args, "operation", 64);
            if (!IsSharedMutation(operation)) throw new BridgeException("InvalidRequest", "This operation has no shared mutation scope adapter.");
            var arguments = args["arguments"] as JObject ?? throw new BridgeException("InvalidRequest", "Exact mutation arguments are required.");
            _sharedMutationOperation = operation; _sharedMutationArguments = (JObject)arguments.DeepClone();
            _sharedMutationToken = Guid.NewGuid().ToString("N"); _sharedMutationDispatched = false; _sharedMutationDestinations.Clear();
            try
            {
                _sharedMutationLease = _portal.ExclusiveAccess("TIA MCP: shared resource admission " + operation);
                _sharedMutationScope = SharedMutationResolve();
                return new JObject { ["leaseToken"] = _sharedMutationToken, ["scope"] = _sharedMutationScope.DeepClone(),
                    ["nativeDispatched"] = false, ["nativeQualified"] = false };
            }
            catch { SharedMutationRelease(); throw; }
        }

        private JObject SharedMutationDispatch(JObject args)
        {
            RequireSharedMutationLease(args);
            if (_sharedMutationDispatched) throw new BridgeException("OutcomeUnknown", "This lease has already attempted dispatch; it cannot be replayed.");
            if (args.Value<string>("scopeSha256") != _sharedMutationScope.Value<string>("scopeSha256"))
                throw new BridgeException("StaleMutationScope", "Dispatch does not bind the admitted native scope.");
            var fresh = SharedMutationResolve();
            if (!JToken.DeepEquals(fresh, _sharedMutationScope)) throw new BridgeException("StaleMutationScope", "Native resources or prepared content changed before dispatch.");
            if (_sharedMutationOperation == "component_window_step") ComponentWindowAuthorizeDispatch(args);
            _sharedMutationDispatched = true;
            ObserveIndexOperation(_sharedMutationOperation);
            var arguments = (JObject)_sharedMutationArguments.DeepClone();
            switch (_sharedMutationOperation)
            {
                case "change_apply": return (arguments.Value<string>("changeId") ?? "").StartsWith("lib-", StringComparison.Ordinal) ? LibraryChangeApply(arguments) : ChangeApply(arguments);
                case "change_restore": return (arguments.Value<string>("changeId") ?? "").StartsWith("lib-", StringComparison.Ordinal) ? LibraryChangeRestore(arguments) : ChangeRestore(arguments);
                case "simulation_download": return SimulationDownload(arguments);
                case "simulation_provision_scratch_credential": return SimulationProvision(arguments);
                case "library_open": return LibraryOpen(arguments);
                case "scratch_create": return ScratchCreate(arguments);
                case "project_retrieve": return ProjectRetrieve(arguments);
                case "project_copy": return ProjectCopy(arguments);
                case "plc_station_upload": return PlcStationUpload(arguments);
                case "watch_fixture_seed": return WatchFixtureSeed(arguments);
                case "watch_authoring_source_negative": return WatchAuthoringSourceNegative(arguments);
                case "component_window_step": return ComponentWindowExecute(arguments);
                default: throw new BridgeException("InvalidRequest", "No admitted mutation route.");
            }
        }

        private JObject SharedMutationEnd(JObject args)
        {
            RequireSharedMutationLease(args); bool dispatched = _sharedMutationDispatched; SharedMutationRelease();
            return new JObject { ["released"] = true, ["dispatchAttempted"] = dispatched, ["outcomeQualified"] = false };
        }
        private void RequireSharedMutationLease(JObject args)
        {
            if (_sharedMutationLease == null || args.Value<string>("leaseToken") != _sharedMutationToken)
                throw new BridgeException("MutationLeaseRequired", "The exact current native mutation lease is required.");
        }
        private void SharedMutationRelease()
        {
            var lease = _sharedMutationLease; _sharedMutationLease = null; _sharedMutationToken = null;
            _sharedMutationArguments = null; _sharedMutationScope = null; _sharedMutationOperation = null;
            _sharedMutationDispatched = false; _sharedMutationDestinations.Clear(); lease?.Dispose();
        }
        private sealed class SharedMutationNestedScope : IDisposable { public void Dispose() { } }
        // Existing mutation methods use this instead of attempting a second native ExclusiveAccess lease.
        private IDisposable SharedMutationExclusive(string description) => _sharedMutationLease != null
            ? (IDisposable)new SharedMutationNestedScope() : _portal.ExclusiveAccess(description);

        private string SharedMutationPlannedDestination(string role, Func<string> legacyFactory)
        {
            if (_sharedMutationLease == null) return legacyFactory();
            if (!_sharedMutationDestinations.TryGetValue(role, out var path))
            {
                if (_sharedMutationDispatched) throw new BridgeException("MutationScopeIncomplete", "A destination was not planned before dispatch.");
                path = Path.GetFullPath(legacyFactory()); _sharedMutationDestinations.Add(role, path);
            }
            return path;
        }
        private string SharedMutationScratchName()
        {
            if (_sharedMutationLease != null && _sharedMutationDestinations.TryGetValue("scratchName", out var retained)) return retained;
            string name = "TiaMcpScratch_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (_sharedMutationLease != null) _sharedMutationDestinations.Add("scratchName", name);
            return name;
        }
        private string SharedMutationLibraryRecoveryDirectory(JToken resource, string artifactDirectory) =>
            SharedMutationPlannedDestination("libraryRecovery:" + resource.Value<string>("originalPath"),
                () => Path.Combine(artifactDirectory, "lr_" + Guid.NewGuid().ToString("N").Substring(0, 10)));

        private string SimulationDiscoveryObserverDirectory(JObject args) => SharedMutationPlannedDestination("discoveryObserver",
            () => Path.Combine(SharedMutationArtifactDirectory(args), "do_" + Guid.NewGuid().ToString("N").Substring(0, 8)));

        private static string SharedMutationArtifactDirectory(JObject args)
        {
            string path = args.Value<string>("artifactDirectory");
            if (!ProjectPathIsAbsolute(path)) throw new BridgeException("InvalidRequest", "An absolute artifact directory is required for native scope resolution.");
            // Resolving a new destination must not create even its artifact parent before durable admission.
            return Path.GetFullPath(path);
        }

        private JObject SharedMutationResolve()
        {
            var args = _sharedMutationArguments; string operation = _sharedMutationOperation;
            if (operation == "component_window_step") return ComponentWindowScope(args);
            var resources = new JArray(); var observation = new JObject();
            Action<string, string, string> add = (kind, path, effect) => {
                if (!ProjectPathIsAbsolute(path)) throw new BridgeException("MutationScopeIncomplete", "Every native resource needs an exact absolute path.");
                path = Path.GetFullPath(path);
                if (!resources.Any(r => r.Value<string>("kind") == kind && string.Equals(r.Value<string>("path"), path, StringComparison.OrdinalIgnoreCase)))
                    resources.Add(new JObject { ["kind"] = kind, ["path"] = path, ["effect"] = effect });
            };
            Action<string, string, string> absent = (kind, role, path) => {
                if (File.Exists(path) || Directory.Exists(path)) throw new BridgeException("MutationScopeStale", "The admitted new destination already exists.");
                add(kind, path, "createNewDestination"); observation[role] = path;
            };
            if (operation == "change_apply" || operation == "change_restore")
            {
                string id = ChangeText(args, "changeId", 64);
                if (PlcComponentQualificationGate.IsId(id))
                    throw new BridgeException("MutationScopeIncomplete", "Component qualification needs its complete multi-step resource manifest before shared dispatch.");
                if (id.StartsWith("lib-", StringComparison.Ordinal))
                {
                    if (operation == "change_restore")
                    {
                        var journal = ReadLibraryJournal(args);
                        add("workspace", Path.Combine(SharedMutationArtifactDirectory(args), "change-" + id), "legacyRecoveryJournal");
                        if (journal.Value<string>("previewHash") != ChangeText(args, "previewHash", 64)) throw new BridgeException("PreviewMismatch", "Recovery preview hash differs.");
                        var attempts = journal["recoveryAttempts"] as JArray;
                        bool returnedRecovery = attempts != null && attempts.Count > 0 && attempts.Last.Value<string>("state") == "completed";
                        if (attempts != null && attempts.Count > 0 && !returnedRecovery) throw new BridgeException("OutcomeUnknown", "An earlier recovery must be reconciled without replay.");
                        var checkpoints = journal["checkpoints"] as JArray;
                        if (checkpoints == null || checkpoints.Count == 0 || checkpoints.Count > 64) throw new BridgeException("CheckpointInvalid", "Bounded completed checkpoints are required.");
                        observation["journalSha256"] = SharedMutationHash(journal);
                        foreach (var checkpoint in checkpoints)
                        {
                            string kind = checkpoint.Value<string>("kind"), original = checkpoint.Value<string>("originalPath");
                            if (kind != "project" && kind != "library") throw new BridgeException("MutationScopeIncomplete", "Unknown checkpoint resource kind.");
                            add(kind, original, "preserveOriginalDuringRecovery");
                            if (returnedRecovery) continue; // Existing route returns its completed receipt before any retrieval.
                            string archive = checkpoint.Value<string>("archivePath");
                            if (ChangeFileHash(archive) != checkpoint.Value<string>("sha256")) throw new BridgeException("CheckpointInvalid", "Recovery archive changed.");
                            add("workspace", archive, "readCheckpoint");
                            string destination = SharedMutationLibraryRecoveryDirectory(checkpoint, SharedMutationArtifactDirectory(args));
                            absent(kind, "recovery:" + original, destination);
                        }
                        if (returnedRecovery)
                        {
                            foreach (var recovered in attempts.Last["resources"] as JArray ?? new JArray())
                                add(recovered.Value<string>("kind"), recovered.Value<string>("destination"), "completedRecoveryReceiptReadback");
                            observation["idempotentReadback"] = true;
                        }
                    }
                    else
                    {
                        if (!_libraryChanges.TryGetValue(id, out var change)) throw new BridgeException("OutcomeUnknown", "Prepared native library handles do not survive restart; inspect the legacy journal.");
                        add("workspace", change.Directory, "legacyJournalCheckpointOutput");
                        if (ChangeText(args, "previewHash", 64) != change.PreviewHash) throw new BridgeException("PreviewMismatch", "Preview hash differs.");
                        string key = ChangeText(args, "idempotencyKey", 128);
                        bool returnedApply = change.IdempotencyKey == key && change.State == "applied";
                        if (change.IdempotencyKey != null && !returnedApply) throw new BridgeException("OutcomeUnknown", "The earlier library attempt cannot be replayed with this key/state.");
                        if (change.Project != null) { EnsureProjectOffline(change.Project); add("project", change.Project.Path.FullName, "checkpointCompileSave"); }
                        foreach (var library in new[] { change.Source, change.Destination }.Where(l => l != null).Distinct())
                        {
                            if (library.Project != null) add("project", library.Project.Path.FullName, "libraryResource");
                            else add("library", library.Directory ?? Path.GetDirectoryName(library.Global.Path.FullName), "libraryResourceAndCompanions");
                            if (!string.IsNullOrEmpty(library.SourcePath)) add("library", Path.GetExtension(library.SourcePath).StartsWith(".zal", StringComparison.OrdinalIgnoreCase)
                                ? library.SourcePath : Path.GetDirectoryName(library.SourcePath), "retainedSourceSnapshot");
                        }
                        if (change.Preview["outputAbsent"] != null)
                        {
                            if (returnedApply) add("library", change.Preview.Value<string>("outputAbsent"), "completedOutputReceiptReadback");
                            else absent("library", "outputAbsent", change.Preview.Value<string>("outputAbsent"));
                        }
                        observation["previewHash"] = change.PreviewHash; observation["fingerprint"] = LibraryChangeFingerprint(change);
                        observation["idempotentReadback"] = returnedApply;
                        if (!returnedApply && observation.Value<string>("fingerprint") != change.Preview.Value<string>("fingerprint")) throw new BridgeException("PreviewStale", "Library resources changed since preview.");
                        VerifyLibrarySources(change);
                    }
                }
                else
                {
                    var change = GetEngineeringChange(args);
                    add("workspace", change.Directory, "legacyJournalCheckpointOutput");
                    if (!string.IsNullOrEmpty(change.InputPath)) add("workspace", change.InputPath, "reviewedMutationInput");
                    if (ChangeText(args, "previewHash", 64) != change.PreviewHash) throw new BridgeException("PreviewMismatch", "Preview hash differs.");
                    if (operation == "change_restore" && change.State == "restored")
                    {
                        add("project", change.ProjectPath, "completedRecoveryReceiptReadback");
                        add("project", change.RecoveredProject?.Value<string>("path"), "completedRecoveredProjectReceiptReadback");
                        observation["receiptSha256"] = SharedMutationHash(ChangeView(change)); observation["idempotentReadback"] = true;
                    }
                    else
                    {
                        bool returnedApply = operation == "change_apply" && change.IdempotencyKey == ChangeText(args, "idempotencyKey", 128) && change.State == "applied";
                        if (operation == "change_apply" && change.IdempotencyKey != null && !returnedApply)
                            throw new BridgeException("RecoveryRequired", "The earlier engineering attempt cannot be replayed with this key/state.");
                        var project = ProjectForChange(change); EnsureProjectOffline(project);
                        add("project", project.Path.FullName, operation == "change_apply" ? "checkpointMutationCompileSave" : "preserveAndRecover");
                        observation["fingerprint"] = EngineeringFingerprint(change, project); observation["previewHash"] = change.PreviewHash; observation["idempotentReadback"] = returnedApply;
                        string expected = operation == "change_apply" ? change.BaseFingerprint : change.AfterFingerprint;
                        if (!returnedApply && observation.Value<string>("fingerprint") != expected) throw new BridgeException("StaleRevision", "Project changed since its retained preparation or outcome.");
                        if (operation == "change_apply" && !returnedApply && change.Kind.StartsWith("db_snapshot_", StringComparison.Ordinal))
                        {
                            // Online DB snapshot kinds also reserve the owned simulator and plan the observer project before dispatch.
                            var marker = OwnedSimulationMarker(change.Payload);
                            add("workspace", marker.Value<string>("storagePath"), "ownedSimulatorOnlineValues");
                            observation["markerSha256"] = SharedMutationHash(marker);
                            string observer = SimulationDiscoveryObserverDirectory(args);
                            SimulationDiscoveryObserver.VerifyDestination(observer);
                            absent("project", "discoveryObserverProject", observer);
                        }
                        if (operation == "change_restore")
                        {
                            if (ChangeFileHash(change.ArchivePath) != change.ArchiveHash) throw new BridgeException("CheckpointInvalid", "Recovery archive changed.");
                            add("workspace", change.ArchivePath, "readCheckpoint");
                            string destination = SharedMutationPlannedDestination("projectRecovery", () => EngineeringPaths.NewRecoveryDirectory(SharedMutationArtifactDirectory(args), project.Name));
                            absent("project", "recoveryDestination", destination);
                        }
                    }
                }
            }
            else if (operation == "scratch_create")
            {
                if (!_ownsPortal || _portal.Projects.Count != 0 || _scratchProject != null) throw new BridgeException("ScratchIsolationRequired", "Scratch creation requires this worker's empty Portal.");
                string name = SharedMutationScratchName(); string destination = Path.Combine(SharedMutationArtifactDirectory(args), name);
                if (Path.Combine(destination, name).Length > 143) throw new BridgeException("ProjectPathTooLong", "Planned scratch destination exceeds the native path limit.");
                absent("project", "scratchDestination", destination);
            }
            else if (operation == "project_retrieve")
            {
                if (!_ownsPortal || _portal.Projects.Count != 0) throw new BridgeException("ProjectIsolationRequired", "Retrieval requires this worker's empty Portal.");
                string archive = ChangeText(args, "archivePath", 4096);
                if (!string.Equals(ChangeFileHash(archive), ChangeText(args, "expectedSha256", 64), StringComparison.OrdinalIgnoreCase)) throw new BridgeException("CheckpointInvalid", "Archive hash differs.");
                add("workspace", archive, "readCheckpoint"); observation["archiveSha256"] = ChangeFileHash(archive);
                string destination = SharedMutationPlannedDestination("projectRetrieve", () => Path.Combine(SharedMutationArtifactDirectory(args), "r_" + Guid.NewGuid().ToString("N").Substring(0, 12)));
                if (destination.Length > 80) throw new BridgeException("RecoveryPathTooLong", "Planned retrieval destination exceeds the native path limit.");
                absent("project", "retrievalDestination", destination);
            }
            else if (operation == "project_copy")
            {
                if (!_ownsPortal) throw new BridgeException("OwnedSessionRequired", "Project copy requires this worker's owned Portal.");
                var source = SelectProject(args); string root = SharedMutationArtifactDirectory(args);
                if (source.IsModified) throw new BridgeException("UnsavedProject", "project_copy never saves its source; save or discard its edits first.");
                add("project", source.Path.FullName, "archiveSavedSourceUnchanged"); observation["source"] = ProjectInfo(source);
                absent("workspace", "copyArchive", SharedMutationPlannedDestination("projectCopyArchive", () => Path.Combine(root, EngineeringPaths.NewRecoveryArchiveName(root))));
                absent("project", "copyDestination", SharedMutationPlannedDestination("projectCopy", () => EngineeringPaths.NewRecoveryDirectory(root, source.Name)));
            }
            else if (operation == "plc_station_upload")
            {
                if (!_ownsPortal || _portal.Projects.Count != 0) throw new BridgeException("ProjectIsolationRequired", "Station upload requires this worker's empty Portal.");
                var marker = OwnedSimulationMarker(args);
                add("workspace", marker.Value<string>("storagePath"), "ownedSimulatorUploadSource");
                observation["markerSha256"] = SharedMutationHash(marker);
                absent("project", "uploadDestination", StationUploadDestination(args));
                string observer = SimulationDiscoveryObserverDirectory(args);
                SimulationDiscoveryObserver.VerifyDestination(observer);
                absent("project", "discoveryObserverProject", observer);
            }
            else if (operation == "library_open")
            {
                if (args.Value<string>("kind") == "project")
                {
                    var project = SelectProject(args); add("project", project.Path.FullName, "registerProjectLibraryHandle");
                    observation["project"] = ProjectInfo(project);
                }
                else
                {
                    string path = ChangeText(args, "path", 4096), expected = ChangeText(args, "expectedSha256", 64);
                    if (!string.Equals(ChangeFileHash(path), expected, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("PackageChanged", "Library source marker changed.");
                    bool archive = string.Equals(Path.GetExtension(path), ".zal21", StringComparison.OrdinalIgnoreCase);
                    add("library", archive ? path : Path.GetDirectoryName(Path.GetFullPath(path)), "readSourceAndCompanions");
                    observation["sourceSha256"] = archive ? ChangeFileHash(path) : LibraryDirectoryHash(Path.GetDirectoryName(Path.GetFullPath(path)));
                    string destination = SharedMutationPlannedDestination("libraryOpen", () => Path.Combine(SharedMutationArtifactDirectory(args), "l_" + Guid.NewGuid().ToString("N").Substring(0, 12)));
                    absent("library", "workingCopyDestination", destination);
                }
            }
            else if (operation == "simulation_download" || operation == "simulation_provision_scratch_credential")
            {
                JObject marker;
                try { marker = SimulationTicket.Decode(args.Value<string>("downloadTicket")); }
                catch { throw new BridgeException("InvalidSimulationTicket", "A current ticket-bound owned simulator is required for scope resolution."); }
                var plc = SelectPlc(args); EnsureProjectOffline(plc.Project);
                add("project", plc.Project.Path.FullName, "simulationCompilationOrCredentialPreparation");
                add("workspace", marker.Value<string>("storagePath"), "ownedSimulatorStorageAndCard");
                observation["markerSha256"] = SharedMutationHash(marker); observation["plcPath"] = plc.Path;
                observation["project"] = ProjectInfo(plc.Project);
                if (operation == "simulation_provision_scratch_credential")
                {
                    observation["projectCheckpointPath"] = SimulationProvisionCheckpointPath(marker.Value<string>("storagePath"), plc.Project.IsModified);
                    string observer = SimulationDiscoveryObserverDirectory(args);
                    SimulationDiscoveryObserver.VerifyDestination(observer);
                    absent("project", "discoveryObserverProject", observer);
                }
            }
            else if (operation == "watch_fixture_seed")
            {
                var plc = SelectPlc(args); EnsureProjectOffline(plc.Project); add("project", plc.Project.Path.FullName, "ownedWatchFixtureSeed");
                observation["project"] = ProjectInfo(plc.Project); observation["plcPath"] = plc.Path;
            }
            else if (operation == "watch_authoring_source_negative")
            {
                var binding = args["sourceBinding"] as JObject ?? throw new BridgeException("InvalidRequest", "Exact source binding required.");
                var plc = SelectPlc(binding); EnsureProjectOffline(plc.Project);
                add("project", plc.Project.Path.FullName, "ownedWatchSourceNegative");
                observation["project"] = ProjectInfo(plc.Project); observation["plcPath"] = plc.Path;
                observation["sourceBinding"] = binding.DeepClone();
            }
            else throw new BridgeException("MutationScopeIncomplete", "No exact shared resource scope is implemented for this operation.");
            if (resources.Count == 0 || resources.Count > 64) throw new BridgeException("MutationScopeIncomplete", "Exact bounded resources are required before native dispatch.");
            var scope = new JObject { ["operation"] = operation, ["requestSha256"] = SharedMutationHash(args), ["sessionEpoch"] = _watchEpoch,
                ["adapterSha256"] = ChangeFileHash(Assembly.GetExecutingAssembly().Location), ["processId"] = _processId,
                ["resources"] = new JArray(resources.OrderBy(r => r.Value<string>("kind") + ":" + r.Value<string>("path"), StringComparer.Ordinal)),
                ["observationSha256"] = SharedMutationHash(observation) };
            scope["scopeSha256"] = SharedMutationHash(scope); return scope;
        }
        private static string SharedMutationHash(JToken token)
        {
            JToken Sort(JToken value)
            {
                if (value is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Sort(p.Value))));
                if (value is JArray array) return new JArray(array.Select(Sort));
                return value.DeepClone();
            }
            return ChangeHash(Sort(token).ToString(Formatting.None));
        }
    }
}
