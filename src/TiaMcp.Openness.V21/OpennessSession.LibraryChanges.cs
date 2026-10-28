using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Analysis;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly Dictionary<string, LibraryChange> _libraryChanges = new Dictionary<string, LibraryChange>(StringComparer.Ordinal);
        private static readonly HashSet<string> LibraryPolicyAttributes = new HashSet<string>(new[] { "DoNotUse", "SetForUpdate" }, StringComparer.Ordinal);
        private static readonly string[] LibraryChangeKinds = { "library_create", "library_upgrade", "library_copy_elements", "library_instantiate", "library_update_project", "library_integrate", "library_create_type", "library_create_master_copy", "library_edit_version", "library_release_version", "library_discard_version", "library_set_default", "library_type_policy", "library_organize", "library_delete", "library_publish", "library_save", "library_archive", "library_cleanup", "library_harmonize" };

        private sealed class LibraryChange
        {
            internal string Id, Kind, Directory, State, PreviewHash, IdempotencyKey, DocumentDirectory;
            internal JObject Payload, Selector, Preview, Journal;
            internal JArray ComponentContentPins;
            internal Project Project;
            internal ManagedLibrary Source, Destination;
            internal List<LibraryTypeVersion> Versions = new List<LibraryTypeVersion>();
            internal List<LibraryType> CopyTypes = new List<LibraryType>();
            internal List<MasterCopy> Masters = new List<MasterCopy>();
        }

        private JObject LibraryCapabilities(JObject args)
        {
            var result = Envelope();
            result["apiAssemblyVersion"] = typeof(ILibrary).Assembly.GetName().Version.ToString();
            result["changeKinds"] = new JArray(LibraryChangeKinds);
            result["supportedPlcVersionFamilies"] = new JArray(typeof(CodeBlockLibraryTypeVersion).FullName, typeof(PlcTypeLibraryTypeVersion).FullName);
            result["sourcePolicy"] = "Global-library file/archive inputs are copied/retrieved into owned working directories. Original sources and externally opened global libraries cannot be changed.";
            result["updatePolicy"] = "Selected type must be the highest committed source default. Native update scope is the complete explicitly selected PLC. Native cleanup cannot be disabled: every existing linked PLC object requires a proven native reference path from an OB, excluding outgoing references of updated objects. Unproven reachability refuses the update before mutation. Force updates, downgrades and automatic deletion of old library versions are disabled.";
            result["qualification"] = "implementation_present_native_qualification_required";
            result["hmi"] = LibraryHmiCapabilities();
            result["limits"] = new JObject { ["libraries"] = 32, ["metadataEntries"] = 10000, ["pageSize"] = 200, ["dependencyNodes"] = 1000, ["selectedComponents"] = 100 };
            result["plcIntegration"] = new JObject { ["action"] = "plc_component_plan", ["planOnly"] = true, ["applySupported"] = false, ["applyError"] = PlcLibraryIntegrationPlanner.ApplyError, ["schema"] = "contracts/plc-library-integration-recipe.v1.schema.json", ["scope"] = "One exact FB with pinned PLC dependencies in the PLC or a software unit; a new or exactly reused global instance DB, or a new multi-instance in an existing FB caller; an existing LAD/FBD FC, FB or OB caller or a new FC; scalar tags, UDT-tag members or DB members. Exact installed versions are reused with native proofs. Returns concrete separate preparation steps, planOnly reasons for steps the component route cannot apply, and a recovery/qualification packet." };
            if (_libraryIntegrateApplyEnabled)
            {
                result["plcIntegration"]["applySupported"] = true;
                result["plcIntegration"]["combinedApply"] = "Enabled by --enable-library-integrate-apply for a fresh FB/dependency import with a new global instance DB and a new FBD FC caller or an existing one after its last network in the standard PLC scope; preview.combinedApply states eligibility. OB insertion, LAD/FB callers, reuse, multi-instances, software units, HMI targets and PLC/HMI recipes stay plan-only. Not natively qualified as a production route.";
            }
            result["limitations"] = new JArray("Type authoring uses documented CreateFromDocuments; direct block-to-type creation is not exposed by the installed public API.", "PLC library_integrate prepares an explicit recipe. Combined apply runs only on a Host started with --enable-library-integrate-apply and only for the verified shapes in plcIntegration; otherwise the proposed steps require separate fresh preparation. Steps are never atomic.", "Library copy/publication can initialize a default or promote a newer source default, with exact effects pinned in preview. Arbitrary default changes and project updates require separate prepared actions.", "Full document/preservation/runtime qualification must be retained for each target profile.");
            return result;
        }

        private JObject LibraryChangePrepare(JObject args) => LibraryChangePrepare(args, null);

        // Optional internal argument is supplied only by the owned component
        // coordinator, never deserialized from a public request or payload.
        private JObject LibraryChangePrepare(JObject args, JArray componentContentPins)
        {
            if (args.Value<string>("kind") == PlcComponentQualificationGate.Kind) return ComponentQualificationPrepare(args);
            EnsureAttached();
            string kind = ChangeText(args, "kind", 64);
            if (!LibraryChangeKinds.Contains(kind)) throw new BridgeException("UnsupportedChangeKind", "Unknown library change kind: " + kind);
            var payload = args["payload"] as JObject ?? throw new BridgeException("InvalidRequest", "payload must be an object.");
            if (payload.Value<int?>("schemaVersion") != 1) throw new BridgeException("InvalidRequest", "Library payload schemaVersion=1 is required.");
            if (kind == "library_integrate" && PlcHmiIntegrationPlanner.IsRecipe(payload)) ValidatePlcHmiRecipe(payload);
            var change = new LibraryChange { Id = "lib-" + Guid.NewGuid().ToString("N"), Kind = kind, Payload = (JObject)payload.DeepClone(), Selector = (JObject)args.DeepClone(), State = "preparing" };
            if (componentContentPins != null)
            {
                if (kind != "library_instantiate") throw new BridgeException("InvalidComponentContract", "Internal component source pins apply only to the exact import step.");
                change.ComponentContentPins = (JArray)componentContentPins.DeepClone();
            }
            change.Directory = Path.Combine(ArtifactDirectory(args), "change-" + change.Id);
            Directory.CreateDirectory(change.Directory);
            var target = payload["target"] as JObject;
            if (target != null) foreach (var property in target.Properties()) change.Selector[property.Name] = property.Value.DeepClone();
            var source = payload["source"] as JObject;
            if (source != null)
            {
                change.Source = ResolveLibrary(ChangeText(source, "libraryId", 64));
                if (ChangeText(source, "revision", 64) != LibraryRevision(change.Source)) throw new BridgeException("PreviewStale", "Source library metadata differs from the requested revision.");
            }
            if (payload["destinationLibraryId"] != null) change.Destination = ResolveLibrary(ChangeText(payload, "destinationLibraryId", 64));
            if (kind == "library_instantiate" || kind == "library_update_project" || kind == "library_integrate" || kind == "library_create_master_copy" || kind == "library_harmonize") change.Project = SelectProject(change.Selector);
            if (change.Destination?.Project != null)
            {
                if (change.Project != null && !change.Project.Equals(change.Destination.Project)) throw new BridgeException("TargetIncompatible", "Cross-project authoring requires an explicit separate publication/import workflow.");
                change.Project = change.Destination.Project;
            }
            if (change.Project == null && change.Source?.Project != null && IsLibraryMaintenance(kind)) change.Project = change.Source.Project;
            if (change.Project != null) EnsureProjectOffline(change.Project);
            if (kind != "library_create" && kind != "library_upgrade" && change.Source == null && kind != "library_integrate" && kind != "library_create_type" && kind != "library_create_master_copy")
                throw new BridgeException("InvalidRequest", "This kind requires source.libraryId and source.revision.");
            var selection = payload["selection"] as JArray ?? new JArray();
            if (selection.Count > 100) throw new BridgeException("PackageLimitExceeded", "Select at most 100 components in one change.");
            foreach (var token in selection)
            {
                var item = token as JObject ?? throw new BridgeException("InvalidRequest", "Each selection must be an object.");
                if (change.Source == null) throw new BridgeException("InvalidRequest", "A source library is required for selected components.");
                if (item["masterCopyPath"] != null) change.Masters.Add(ResolveLibraryMaster(change.Source, ChangeText(item, "masterCopyPath", 4096)));
                else change.Versions.Add(ResolveLibraryVersion(change.Source, item));
            }
            if (change.Versions.Select(x => x.Guid).Distinct().Count() != change.Versions.Count || change.Masters.Distinct().Count() != change.Masters.Count)
                throw new BridgeException("InvalidRequest", "Duplicate selections are not allowed.");
            if (change.Versions.Select(x => x.TypeObject.Guid).Distinct().Count() != change.Versions.Count)
                throw new BridgeException("DependencyConflict", "Select only one version per type in an operation.");
            var preview = new JObject { ["schemaVersion"] = 1, ["kind"] = kind, ["sessionGeneration"] = _libraryGeneration, ["resources"] = new JArray(), ["selection"] = selection.DeepClone(), ["dependencies"] = new JArray(), ["effects"] = new JArray(), ["warnings"] = new JArray(), ["payload"] = payload.DeepClone() };
            change.Preview = preview;
            if (change.ComponentContentPins != null) preview["componentContentPins"] = change.ComponentContentPins.DeepClone();
            foreach (var library in new[] { change.Source, change.Destination }.Where(x => x != null).Distinct())
                ((JArray)preview["resources"]).Add(new JObject { ["libraryId"] = library.Id, ["revision"] = LibraryRevision(library), ["path"] = library.Project?.Path?.FullName ?? library.Global.Path?.FullName, ["sourcePath"] = library.SourcePath, ["sourceHash"] = library.SourceHash });
            if (change.Project != null)
            {
                preview["projectPath"] = change.Project.Path?.FullName;
                // checkpointSave persists in-memory edits before archiving, so they join the recovery baseline and the result.
                preview["unsavedTargetEdits"] = new JObject { ["present"] = change.Project.IsModified,
                    ["disposition"] = change.Project.IsModified ? "savedIntoCheckpointBaseline" : "none" };
            }
            var dependencyRoots = change.Versions.ToList();
            if (kind == "library_copy_elements" || kind == "library_publish")
            {
                // Synchronization can copy all releases of dependent types, not merely the
                // selected default's dependency tree. Preview the complete version union.
                var types = new Queue<LibraryType>(change.Versions.Select(x => x.TypeObject)); var seen = new HashSet<Guid>();
                dependencyRoots.Clear();
                while (types.Count != 0)
                {
                    var type = types.Dequeue(); if (!seen.Add(type.Guid)) continue;
                    if (seen.Count > 1000) throw new BridgeException("DependencyUnresolved", "All-version type synchronization exceeds the 1000-type closure budget.");
                    change.CopyTypes.Add(type);
                    foreach (var release in type.Versions)
                    {
                        if (release.State != LibraryTypeVersionState.Committed || type.DoNotUse) throw new BridgeException("DependencyConflict", "Every transitively synchronized release must be committed and usable.");
                        dependencyRoots.Add(release);
                        if (dependencyRoots.Count > 1000) throw new BridgeException("DependencyUnresolved", "All-version synchronization exceeds 1000 versions.");
                        foreach (var dependency in LibraryDependencyCapture.Read(release).Versions) types.Enqueue(dependency.TypeObject);
                    }
                }
            }
            foreach (var version in dependencyRoots)
            {
                if (version.State == LibraryTypeVersionState.InWork && IsLibraryMaintenance(kind))
                {
                    ((JArray)preview["dependencies"]).Add(new JObject { ["rootVersionGuid"] = version.Guid.ToString("D"), ["scopeComplete"] = false, ["reason"] = "Native in-work dependency access is not available. Release uses DoNotAutomaticallyCreateOrReleaseDependencies and cannot implicitly release dependency versions." });
                    continue;
                }
                var closure = LibraryDependencyClosure(version, 1000, out var complete);
                if (!complete) throw new BridgeException("DependencyUnresolved", "Native dependency closure exceeds 1000 nodes.");
                ((JArray)preview["dependencies"]).Add(new JObject { ["rootVersionGuid"] = version.Guid.ToString("D"), ["versions"] = closure });
                if (new[] { "library_instantiate", "library_update_project", "library_copy_elements", "library_publish" }.Contains(kind) && closure.Any(x => x.Value<string>("state") != "Committed" || x.Value<bool>("doNotUse")))
                    throw new BridgeException("DependencyConflict", "Consumption requires committed, usable versions throughout the exact dependency closure.");
            }
            if (change.Masters.Count != 0)
            {
                var budget = new ScanBudget(10000);
                foreach (var type in WalkLibraryTypes(change.Source.Native.TypeFolder, "/types", budget, 0))
                    foreach (var version in type.Value.Versions)
                    {
                        if (!budget.Take()) break;
                        foreach (var master in change.Masters.Where(x => version.MasterCopiesContainingInstances.Contains(x)))
                        {
                            var closure = LibraryDependencyClosure(version, 1000, out var complete);
                            if (!complete || closure.Any(x => x.Value<string>("state") != "Committed" || x.Value<bool>("doNotUse"))) throw new BridgeException("DependencyUnresolved", "Master-copy native type dependencies are incomplete, not committed or marked DoNotUse.");
                            ((JArray)preview["dependencies"]).Add(new JObject { ["masterCopy"] = master.Name, ["rootVersionGuid"] = version.Guid.ToString("D"), ["versions"] = closure });
                        }
                    }
                if (budget.Truncated) throw new BridgeException("DependencyUnresolved", "Master-copy embedded type dependency lookup exceeded its budget.");
            }
            PrepareLibraryAction(change);
            if (change.Project != null && !IsHmiLibraryTarget(payload))
            {
                var compileTargets = EngineeringDeviceEntries(change.Project);
                preview["compileScope"] = new JArray(compileTargets.Select(x => x.Item.Name));
                preview["compileTargetPaths"] = new JArray(compileTargets.Select(x => x.Path));
            }
            preview["fingerprint"] = LibraryChangeFingerprint(change);
            change.PreviewHash = ChangeHash(preview.ToString(Formatting.None));
            change.State = "prepared";
            change.Journal = new JObject { ["schemaVersion"] = 1, ["changeId"] = change.Id, ["kind"] = kind, ["sessionGeneration"] = _libraryGeneration, ["previewHash"] = change.PreviewHash, ["preview"] = preview.DeepClone(), ["checkpoints"] = new JArray(), ["outputs"] = new JArray(), ["events"] = new JArray(), ["automaticRetryAllowed"] = false };
            LibraryJournal(change, "prepared", "completed");
            _libraryChanges.Add(change.Id, change);
            return LibraryChangeView(change);
        }

        private static bool IsLibraryMaintenance(string kind) => new[] { "library_edit_version", "library_release_version", "library_discard_version", "library_set_default", "library_type_policy", "library_organize", "library_delete", "library_save", "library_archive", "library_cleanup" }.Contains(kind);

        private void PrepareLibraryAction(LibraryChange change)
        {
            var payload = change.Payload; var effects = (JArray)change.Preview["effects"];
            if (IsLibraryMaintenance(change.Kind)) RequireWritableLibrary(change.Source);
            if (change.Destination != null) RequireWritableLibrary(change.Destination);
            switch (change.Kind)
            {
                case "library_create":
                    RequireLibraryHandleCapacity(1);
                    ValidateLibraryName(ChangeText(payload, "name", 128));
                    change.Preview["outputAbsent"] = Path.Combine(change.Directory, "created");
                    break;
                case "library_upgrade":
                {
                    RequireLibraryHandleCapacity(1);
                    string path = ChangeText(payload, "path", 4096), hash = ChangeText(payload, "expectedSha256", 64).ToLowerInvariant();
                    if (!ProjectPathIsAbsolute(path) || !File.Exists(path) || LibrarySourceHash(path) != hash) throw new BridgeException("PackageChanged", "Upgrade input is absent or does not match expectedSha256.");
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("PackageInvalid", "A linked native upgrade input is not accepted.");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(path), "^\\.(z?al)(1[4-9]|20)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        throw new BridgeException("LibraryVersionUnsupported", "Upgrade requires a native V14..V20 library/archive. Vendor compatibility is checked on a separate working copy.");
                    bool archive = Path.GetExtension(path).StartsWith(".zal", StringComparison.OrdinalIgnoreCase);
                    if (archive && new FileInfo(path).Length > 1024L * 1024 * 1024) throw new BridgeException("PackageLimitExceeded", "A native library archive exceeds the 1 GiB snapshot limit.");
                    if (!archive && payload["expectedFiles"] is JArray expectedFiles) VerifyLibraryExpectedFiles(Path.GetDirectoryName(path), expectedFiles);
                    change.Preview["upgradeSourceHash"] = archive ? hash.ToLowerInvariant() : LibraryDirectoryHash(Path.GetDirectoryName(path));
                    change.Preview["upgradeSourceHashScope"] = archive ? "Complete original archive bytes" : "Sorted SHA-256 manifest of every relative companion file; bound to the approved preview hash";
                    change.Preview["outputAbsent"] = Path.Combine(change.Directory, "upgraded");
                    break;
                }
                case "library_copy_elements": case "library_publish":
                    if (change.Destination == null || change.Source == change.Destination || change.Versions.Count + change.Masters.Count == 0) throw new BridgeException("InvalidRequest", "Select elements and a distinct writable destinationLibraryId.");
                    foreach (var version in change.Versions)
                    {
                        if (!version.IsDefault) throw new BridgeException("VersionSelectionUnsupported", "Native type synchronization copies the selected type and all versions, not one arbitrary version. Select its default and review copiedVersions.");
                        if (version.TypeObject.DoNotUse || version.TypeObject.Versions.Any(x => x.State != LibraryTypeVersionState.Committed)) throw new BridgeException("DependencyConflict", "Every copied version must be committed and the source type must be usable.");
                        effects.Add(new JObject { ["action"] = "copyTypeAllVersions", ["versions"] = new JArray(version.TypeObject.Versions.Select(LibraryVersionInfo)), ["defaultPolicy"] = "Promote source default only when newer; exact per-type effects are in typeSynchronization." });
                    }
                    CheckLibraryNameConflicts(change);
                    change.Preview["typeSynchronization"] = PrepareLibraryTypeSynchronization(change);
                    break;
                case "library_instantiate": case "library_integrate":
                    if (IsHmiLibraryTarget(payload))
                    {
                        if (change.Kind == "library_integrate" &&
                            ((payload["target"] as JObject)?.Value<string>("family") != "unified" || HmiAction(payload) != "faceplate_bind"))
                            throw new BridgeException("IntegrationMappingRequired", "HMI library_integrate requires the supported Unified faceplate_bind action with explicit native-interface mappings. Classic HMI binding integration is not implemented. Use library_instantiate for supported copying, placement or tag creation; those actions do not establish integration.");
                        if (change.Versions.Count + change.Masters.Count > 1) throw new BridgeException("InvalidRequest", "One HMI object per prepared change is required.");
                        change.Preview["targetPreview"] = PrepareLibraryHmiAction(change.Project, payload, change.Versions.FirstOrDefault(), change.Masters.FirstOrDefault());
                        break;
                    }
                    if (change.Kind == "library_integrate") { if (PlcHmiIntegrationPlanner.IsRecipe(payload)) PreparePlcHmiIntegrationPlan(change); else { PreparePlcLibraryIntegrationPlan(change); PreparePlcCombinedApply(change); } break; }
                    PreparePlcLibraryInstantiation(change);
                    break;
                case "library_update_project":
                {
                    if (change.Versions.Count == 0 || change.Masters.Count != 0) throw new BridgeException("InvalidRequest", "Update requires selected type versions.");
                    var plc = SelectPlc(change.Selector);
                    if (payload.Value<string>("updateScope") != "entireSelectedPlc") throw new BridgeException("NativeScopeTooBroad", "Native update applies to all uses in the selected PLC. Explicit updateScope=entireSelectedPlc is required.");
                    foreach (var version in change.Versions) if (!version.IsDefault) throw new BridgeException("VersionSelectionUnsupported", "Native project update is enabled only for the selected source default; arbitrary version assignment is not inferred.");
                    foreach (var version in change.Versions)
                    {
                        if (version.TypeObject.Versions.Any(x => x.State == LibraryTypeVersionState.Committed && x.VersionNumber > version.VersionNumber)) throw new BridgeException("VersionSelectionUnsupported", "Native UpdateProject uses latest type content. The selected default must also be the highest committed source release.");
                        var installed = change.Project.ProjectLibrary.FindType(version.TypeObject.Guid);
                        if (installed != null && installed.Versions.Any(x => x.IsDefault && x.VersionNumber > version.VersionNumber)) throw new BridgeException("VersionSelectionUnsupported", "A newer project default already exists; this adapter never silently downgrades or substitutes versions.");
                    }
                    change.Preview["updateImpact"] = LibraryProjectUpdateImpact(change, plc);
                    change.Preview["updateBaseline"] = CaptureLibraryUpdateState(change, plc);
                    change.Preview["updateCleanupGuard"] = PrepareLibraryUpdateCleanupGuard(change, plc);
                    effects.Add(new JObject { ["action"] = "updateAllSelectedTypeUses", ["plcPath"] = plc.Path, ["forceDefault"] = false, ["deleteUnusedVersions"] = false });
                    break;
                }
                case "library_edit_version": case "library_release_version": case "library_discard_version": case "library_set_default": case "library_type_policy":
                    if (change.Kind != "library_release_version" && (change.Versions.Count != 1 || change.Masters.Count != 0)) throw new BridgeException("InvalidRequest", "Exactly one version selection is required.");
                    if (change.Kind == "library_release_version")
                    {
                        // Up to 100 releases per change, in selection order; one release per change cost about 150 s at 1,000 types (scale run).
                        if (change.Versions.Count < 1 || change.Masters.Count != 0) throw new BridgeException("InvalidRequest", "Select 1..100 InWork versions in release order.");
                        if (!Version.TryParse(ChangeText(payload, "versionNumber", 32), out _)) throw new BridgeException("InvalidRequest", "versionNumber must be a native numeric version.");
                        ChangeText(payload, "author", 128); ChangeText(payload, "comment", 4096);
                        if (change.Versions.Any(x => x.State != LibraryTypeVersionState.InWork)) throw new BridgeException("InvalidChangeState", "Release requires InWork versions.");
                    }
                    if (new[] { "library_edit_version", "library_release_version", "library_discard_version" }.Contains(change.Kind) && change.Source.Project == null) throw new BridgeException("TargetIncompatible", "Native version authoring is performed in a project library; publish released results to a global working library.");
                    if (change.Kind == "library_edit_version" && (change.Versions[0] is CodeBlockLibraryTypeVersion || change.Versions[0] is PlcTypeLibraryTypeVersion) && payload["documentDirectory"] != null)
                        PrepareLibraryVersionDocuments(change);
                    else if (change.Kind == "library_edit_version" && (change.Versions[0] is CodeBlockLibraryTypeVersion || change.Versions[0] is PlcTypeLibraryTypeVersion))
                    {
                        var plc = SelectPlc(change.Selector);
                        var uses = change.Versions[0].FindInstances(plc.Software).ToList();
                        string instanceName = ChangeText(payload, "testInstanceName", 128);
                        if (uses.Count(x => LibraryObjectDescription(x.LibraryTypeInstance).Value<string>("Name") == instanceName) != 1) throw new BridgeException("AmbiguousTarget", "PLC version Edit requires exactly one native test instance matching testInstanceName in the selected PLC.");
                    }
                    if (change.Kind == "library_discard_version" && change.Versions[0].State != LibraryTypeVersionState.InWork) throw new BridgeException("InvalidChangeState", "Discard only accepts an InWork version.");
                    if (change.Kind == "library_type_policy" &&
                        (payload["doNotUse"] == null && payload["setForUpdate"] == null ||
                         payload["doNotUse"] != null && payload["doNotUse"].Type != JTokenType.Boolean ||
                         payload["setForUpdate"] != null && payload["setForUpdate"].Type != JTokenType.Boolean))
                        throw new BridgeException("InvalidRequest", "Specify at least one policy flag; every supplied doNotUse/setForUpdate value must be a boolean.");
                    if (change.Kind == "library_type_policy")
                        foreach (var flag in new[] { "DoNotUse", "SetForUpdate" }.Where(x => payload[char.ToLowerInvariant(x[0]) + x.Substring(1)] != null))
                            ReadAttributeValue(change.Versions[0].TypeObject, flag, LibraryPolicyAttributes, true, out _); // Native types can expose a flag read-only.
                    break;
                case "library_create_type":
                    if (change.Destination == null) throw new BridgeException("InvalidRequest", "destinationLibraryId is required.");
                    if (change.Destination.Project == null) throw new BridgeException("TargetIncompatible", "CreateFromDocuments type authoring requires a project library. Publish its released versions separately.");
                    if (payload.Value<string>("targetEnvironment") == "selectedPlc")
                    {
                        LibraryPlcScope(change, out _);
                        if (payload.Value<string>("destinationComposition") != "blocks" && payload.Value<string>("destinationComposition") != "types") throw new BridgeException("InvalidRequest", "PLC document authoring requires destinationComposition=blocks or types and an exact selected PLC/software-unit scope.");
                    }
                    PrepareLibraryDocumentSnapshot(change);
                    break;
                case "library_create_master_copy":
                    if (change.Destination == null) throw new BridgeException("InvalidRequest", "destinationLibraryId is required.");
                    var native = LibraryMasterCopySource(change);
                    if (!(native.Target is IMasterCopySource)) throw new BridgeException("TargetIncompatible", "Selected engineering object is not a native master-copy source.");
                    change.Preview["sourceObject"] = native.Identity.DeepClone();
                    if (change.Destination.Native.MasterCopyFolder.MasterCopies.Any(x => x.Name == native.Identity.Value<string>("name"))) throw new BridgeException("LibraryConflict", "Destination already contains a master copy with this name.");
                    break;
                case "library_organize":
                    PrepareLibraryFolderOperation(change);
                    break;
                case "library_cleanup": case "library_harmonize":
                    PrepareLibraryMaintenance(change);
                    break;
                case "library_delete":
                    if (change.Versions.Count + change.Masters.Count != 1) throw new BridgeException("InvalidRequest", "Select exactly one version or master copy for deletion.");
                    if (change.Versions.Count != 0)
                    {
                        var version = change.Versions[0];
                        if (version.IsDefault || version.Dependents.Any() || version.MasterCopiesContainingInstances.Any()) throw new BridgeException("DependencyConflict", "Default, depended-on or embedded versions cannot be deleted.");
                        if (change.Source.Project == null) throw new BridgeException("DependencyUnresolved", "Global-library external consumers cannot be enumerated; version deletion is restricted to project libraries with verified project uses.");
                        var uses = LibraryProjectUses(change.Source.Project, version, out var complete);
                        if (!complete) throw new BridgeException("DependencyUnresolved", "Some software does not expose complete native library-use search.");
                        if (uses.Count != 0) throw new BridgeException("DependencyConflict", "Version has live uses in this project.");
                    }
                    break;
                case "library_save": case "library_archive":
                    if (!(change.Source.Global is UserGlobalLibrary)) throw new BridgeException("TargetIncompatible", "This operation requires an owned user global library; project libraries are persisted with their project.");
                    if (change.Kind == "library_save" && change.Source.Global.IsModified)
                    {
                        string recoveryId = ChangeText(payload, "recoveryChangeId", 64);
                        if (!_ownsPortal || !_libraryChanges.TryGetValue(recoveryId, out var prior) || prior.State != "recovery_required" || !LibraryWrittenResources(prior).Contains(change.Source)) throw new BridgeException("RecoveryRequired", "Saving dirty state requires its failed same-session library operation's recoveryChangeId in a worker-owned instance.");
                        var checkpoint = ((JArray)prior.Journal["checkpoints"]).FirstOrDefault(x => x.Value<string>("kind") == "library" && string.Equals(x.Value<string>("originalPath"), change.Source.Global.Path.FullName, StringComparison.OrdinalIgnoreCase));
                        if (checkpoint == null || !File.Exists(checkpoint.Value<string>("archivePath")) || ChangeFileHash(checkpoint.Value<string>("archivePath")) != checkpoint.Value<string>("sha256")) throw new BridgeException("CheckpointInvalid", "The failed operation's intact library checkpoint is required before persisting its partial state.");
                        change.Preview["existingRecoveryCheckpoint"] = checkpoint.DeepClone();
                        effects.Add(new JObject { ["action"] = "persistCurrentPartialNativeState", ["recoveryChangeId"] = recoveryId, ["scope"] = "All current unsaved content in the owned working copy. The prior failed operation's checkpoint remains the recovery baseline." });
                    }
                    if (change.Kind == "library_archive") change.Preview["outputAbsent"] = Path.Combine(change.Directory, "published.zal21");
                    break;
            }
            effects.Add(new JObject { ["action"] = change.Kind, ["targetLibraryId"] = (change.Destination ?? change.Source)?.Id, ["projectPath"] = change.Project?.Path?.FullName });
        }

        private static bool IsHmiLibraryTarget(JObject payload) => new[] { "classic", "unified", "hmi" }.Contains((payload["target"] as JObject)?.Value<string>("family"));
        private static void ValidateLibraryName(string name) { if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..") throw new BridgeException("InvalidRequest", "A plain valid library/folder name is required."); }
        private static void RequireWritableLibrary(ManagedLibrary library)
        {
            if (library == null || !library.Writable || library.Global != null && (!library.Owned || !(library.Global is UserGlobalLibrary) || library.Global.IsWriteProtected))
                throw new BridgeException("LibraryReadOnly", "Writes require a project library or an explicitly opened writable owned library copy.");
        }

        private void CheckLibraryNameConflicts(LibraryChange change)
        {
            var budget = new ScanBudget(10000); var targetTypes = WalkLibraryTypes(change.Destination.Native.TypeFolder, "/types", budget, 0).Select(x => x.Value).ToList();
            if (budget.Truncated) throw new BridgeException("DependencyUnresolved", "Destination lookup exceeded its completeness budget.");
            foreach (var node in ((JArray)change.Preview["dependencies"]).SelectMany(x => (x["versions"] as JArray ?? new JArray())))
                foreach (var type in targetTypes.Where(x => string.Equals(x.Name, node.Value<string>("typeName"), StringComparison.OrdinalIgnoreCase)))
                    if (type.Guid.ToString("D") != node.Value<string>("typeGuid")) throw new BridgeException("LibraryConflict", "Different type GUID with a selected/transitive type name exists in the destination.");
            foreach (var master in change.Masters) if (change.Destination.Native.MasterCopyFolder.MasterCopies.Any(x => x.Name == master.Name)) throw new BridgeException("LibraryConflict", "A same-name master copy already exists in the destination root.");
        }

        private static JArray PrepareLibraryTypeSynchronization(LibraryChange change)
        {
            var plan = new JArray(); int capturedVersions = 0;
            foreach (var type in change.CopyTypes.OrderBy(x => x.Guid))
            {
                var sourceVersions = type.Versions.Take(1001).ToList();
                var installed = change.Destination.Native.FindType(type.Guid);
                var existing = installed == null ? new List<LibraryTypeVersion>() : installed.Versions.Take(1001).ToList();
                capturedVersions += sourceVersions.Count + existing.Count;
                if (capturedVersions > 2000) throw new BridgeException("DependencyUnresolved", "Complete library synchronization readback exceeds 2000 source/destination versions.");
                if (sourceVersions.Any(x => x.State != LibraryTypeVersionState.Committed) || existing.Any(x => x.State != LibraryTypeVersionState.Committed))
                    throw new BridgeException("DependencyConflict", "Library synchronization requires committed source and destination versions throughout the copied type closure.");
                var sourceDefaults = sourceVersions.Where(x => x.IsDefault).ToList();
                var existingDefaults = existing.Where(x => x.IsDefault).ToList();
                if (sourceDefaults.Count != 1 || existingDefaults.Count > 1 || existing.Count != 0 && existingDefaults.Count != 1)
                    throw new BridgeException("DependencyUnresolved", "Every populated copied type must have exactly one native default for an exact synchronization preview.");
                foreach (var sourceVersion in sourceVersions)
                    if (existing.Any(x => x.VersionNumber == sourceVersion.VersionNumber && x.Guid != sourceVersion.Guid))
                        throw new BridgeException("LibraryConflict", "The destination contains a different version GUID at the same source type version number.");
                var sourceDefault = sourceDefaults[0]; var beforeDefault = existingDefaults.FirstOrDefault();
                if (sourceVersions.Any(x => x.VersionNumber > sourceDefault.VersionNumber))
                    throw new BridgeException("VersionSelectionUnsupported", "Synchronization requires every copied source type's default to be its highest committed version; lower-default publication needs a separately qualified native policy.");
                var expectedDefault = beforeDefault == null || sourceDefault.VersionNumber > beforeDefault.VersionNumber ? sourceDefault : beforeDefault;
                var expectedVersions = sourceVersions.Concat(existing).GroupBy(x => x.Guid).Select(x => x.First()).OrderBy(x => x.VersionNumber).ThenBy(x => x.Guid);
                plan.Add(new JObject { ["typeGuid"] = type.Guid.ToString("D"), ["typeName"] = type.Name,
                    ["nativeForceUpdateMode"] = ForceUpdateMode.SetOnlyHigherUpdatedVersionAsDefault.ToString(), ["forceUpdate"] = false,
                    ["beforeDefault"] = beforeDefault == null ? null : LibraryVersionInfo(beforeDefault), ["sourceDefault"] = LibraryVersionInfo(sourceDefault),
                    ["expectedDefault"] = LibraryVersionInfo(expectedDefault), ["defaultChange"] = beforeDefault?.Guid != expectedDefault.Guid,
                    ["defaultEffect"] = beforeDefault == null ? "initializeDefault" : beforeDefault.Guid == expectedDefault.Guid ? "preserveDefault" : "promoteNewerSourceDefault",
                    ["expectedVersions"] = new JArray(expectedVersions.Select(x => new JObject { ["versionGuid"] = x.Guid.ToString("D"), ["versionNumber"] = x.VersionNumber.ToString() })), ["deleteVersions"] = false });
            }
            return plan;
        }

        private static JArray VerifyLibraryTypeSynchronization(LibraryChange change)
        {
            var result = new JArray();
            foreach (var expected in (JArray)change.Preview["typeSynchronization"])
            {
                var type = change.Destination.Native.FindType(Guid.Parse(expected.Value<string>("typeGuid")))
                    ?? throw new BridgeException("PreservationMismatch", "A copied native type is absent after library synchronization.");
                var versions = type.Versions.Take(2001).ToList();
                var pins = (JArray)expected["expectedVersions"];
                if (versions.Count != pins.Count || pins.Any(pin => !versions.Any(x => x.Guid.ToString("D") == pin.Value<string>("versionGuid") && x.VersionNumber.ToString() == pin.Value<string>("versionNumber") && x.State == LibraryTypeVersionState.Committed)))
                    throw new BridgeException("PreservationMismatch", "Native synchronization did not preserve the approved exact source/destination version union for " + type.Name + ".");
                var defaults = versions.Where(x => x.IsDefault).ToList();
                if (defaults.Count != 1 || defaults[0].Guid.ToString("D") != expected["expectedDefault"].Value<string>("versionGuid"))
                    throw new BridgeException("PreservationMismatch", "Native synchronization produced a default different from the approved non-forcing update effect for " + type.Name + ".");
                result.Add(new JObject { ["typeGuid"] = type.Guid.ToString("D"), ["defaultVersion"] = LibraryVersionInfo(defaults[0]), ["versions"] = new JArray(versions.Select(LibraryVersionInfo)), ["exactVersionsAndDefaultVerified"] = true });
            }
            return result;
        }

        private SoftwareScope LibraryPlcScope(LibraryChange change, out PlcSelection plc)
        {
            plc = SelectPlc(change.Selector); var budget = new ScanBudget(50000);
            string wanted = change.Selector.Value<string>("scopePath") ?? plc.Path;
            var matches = SoftwareScopes(plc, budget).Where(x => x.Path == wanted).ToList();
            if (budget.Truncated || matches.Count != 1) throw new BridgeException("TargetIncompatible", "Exact PLC/software-unit scope could not be resolved completely.");
            if (matches[0].Kind == "safetyUnit") throw new BridgeException("TargetIncompatible", "Safety-unit library mutations require a separately qualified safety adapter.");
            return matches[0];
        }

        private void PreparePlcLibraryInstantiation(LibraryChange change)
        {
            if (change.Versions.Count + change.Masters.Count == 0) throw new BridgeException("InvalidRequest", "Select at least one exact type version or master copy.");
            var scope = LibraryPlcScope(change, out var plc); var budget = new ScanBudget(50000);
            var names = ScopeBlocks(scope, budget).Select(x => x.Item.Name).Concat(ScopeTypes(scope, budget).Select(x => x.Item.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (budget.Truncated) throw new BridgeException("DependencyUnresolved", "Target inventory exceeded its completeness budget.");
            var selectedNames = change.Versions.Select(x => x.TypeObject.Name).Concat(change.Masters.SelectMany(x => x.ContentDescriptions.Select(y => y.ContentName))).ToList();
            if (selectedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selectedNames.Count) throw new BridgeException("LibraryConflict", "Selected roots have conflicting names in the target composition.");
            var closure = (JArray)change.Preview["dependencies"];
            var reused = new JArray(); var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in change.Versions)
            {
                if (!(version is CodeBlockLibraryTypeVersion) && !(version is PlcTypeLibraryTypeVersion)) throw new BridgeException("TargetIncompatible", "Only eligible PLC code-block and PLC-data-type versions are accepted by this destination.");
                foreach (var node in ((JArray)closure.First(x => x.Value<string>("rootVersionGuid") == version.Guid.ToString("D"))["versions"]))
                {
                    string name = node.Value<string>("typeName");
                    bool root = node.Value<string>("versionGuid") == version.Guid.ToString("D");
                    RequireNoInstanceInOtherScope(change, plc, scope, node);
                    if (!names.Contains(name)) { created.Add(name); continue; }
                    if (root) throw new BridgeException("LibraryConflict", "An existing PLC object shares a selected name: " + name + ". Use explicit library_update_project for existing uses.");
                    if (reused.Any(x => x.Value<string>("versionGuid") == node.Value<string>("versionGuid"))) continue;
                    var dependency = change.Source.Native.FindVersion(Guid.Parse(node.Value<string>("versionGuid"))) ?? throw new BridgeException("DependencyUnresolved", "A previewed dependency version is absent from the source library: " + name);
                    reused.Add(ProveInstalledLibraryDependency(change, plc, node, dependency));
                }
            }
            change.Preview["reusedDependencies"] = reused;
            change.Preview["createdNames"] = new JArray(created.OrderBy(x => x, StringComparer.Ordinal));
            change.Preview["targetNamesBefore"] = new JArray(names.OrderBy(x => x, StringComparer.Ordinal));
            foreach (var master in change.Masters)
            {
                var descriptions = master.ContentDescriptions.ToList();
                if (descriptions.Count != 1) throw new BridgeException("TargetIncompatible", "This PLC adapter requires one master-copy root. Composite/group roots need a qualified dedicated destination adapter.");
                string composition = change.Payload.Value<string>("destinationComposition");
                if (composition != "blocks" && composition != "types" && composition != "tagTables") throw new BridgeException("InvalidRequest", "Master copy destinationComposition must explicitly be blocks, types or tagTables.");
                if (composition == "tagTables")
                {
                    if (scope.Tags == null) throw new BridgeException("TargetIncompatible", "The selected scope has no tag-table group.");
                    var tableBudget = new ScanBudget(50000);
                    var tables = TagTables(scope.Tags, scope.Path, tableBudget).Select(x => x.Item.Name).ToList();
                    if (tableBudget.Truncated) throw new BridgeException("DependencyUnresolved", "Target tag-table inventory exceeded its completeness budget.");
                    if (tables.Contains(descriptions[0].ContentName, StringComparer.OrdinalIgnoreCase)) throw new BridgeException("LibraryConflict", "A tag table with the master-copy name already exists.");
                }
                else if (names.Contains(descriptions[0].ContentName)) throw new BridgeException("LibraryConflict", "Master-copy destination name already exists.");
            }
            change.Preview["targetScope"] = scope.Path;
            change.Preview["compileTarget"] = plc.Path;
        }

        private void PrepareLibraryFolderOperation(LibraryChange change)
        {
            string action = ChangeText(change.Payload, "action", 32), folderKind = ChangeText(change.Payload, "folderKind", 32);
            if (folderKind != "types" && folderKind != "masterCopies") throw new BridgeException("InvalidRequest", "folderKind must be types or masterCopies.");
            string path = change.Payload.Value<string>("folderPath") ?? "/" + folderKind;
            if (action != "create" && action != "rename" && action != "delete") throw new BridgeException("UnsupportedCapability", "Native folder create/rename/delete are available; moves need a separate preservation-qualified transfer strategy.");
            if (action != "delete") ValidateLibraryName(ChangeText(change.Payload, "name", 128));
            if (folderKind == "types")
            {
                var folder = ResolveLibraryTypeFolder(change.Source, path);
                if (action != "create" && !(folder is LibraryTypeUserFolder)) throw new BridgeException("TargetIncompatible", "The root folder cannot be renamed/deleted.");
                if (action == "delete" && (folder.Types.Any() || folder.Folders.Any())) throw new BridgeException("DependencyConflict", "Only an empty folder can be deleted.");
                if (action == "create" && folder.Folders.Find(change.Payload.Value<string>("name")) != null) throw new BridgeException("LibraryConflict", "Folder name already exists.");
            }
            else
            {
                var folder = ResolveLibraryMasterFolder(change.Source, path);
                if (action != "create" && !(folder is MasterCopyUserFolder)) throw new BridgeException("TargetIncompatible", "The root folder cannot be renamed/deleted.");
                if (action == "delete" && (folder.MasterCopies.Any() || folder.Folders.Any())) throw new BridgeException("DependencyConflict", "Only an empty folder can be deleted.");
                if (action == "create" && folder.Folders.Find(change.Payload.Value<string>("name")) != null) throw new BridgeException("LibraryConflict", "Folder name already exists.");
            }
        }

        private JObject LibraryProjectUpdateImpact(LibraryChange change, PlcSelection plc)
        {
            var paths = new Dictionary<IEngineeringObject, string>(); var budget = new ScanBudget(50000);
            foreach (var scope in SoftwareScopes(plc, budget))
            {
                foreach (var block in ScopeBlocks(scope, budget)) paths[block.Item] = block.Path;
                foreach (var type in ScopeTypes(scope, budget)) paths[type.Item] = type.Path;
            }
            if (budget.Truncated) throw new BridgeException("DependencyUnresolved", "Current update-use paths exceeded the complete inventory budget.");
            var requested = new Dictionary<Guid, LibraryTypeVersion>();
            var queue = new Queue<LibraryTypeVersion>(change.Versions);
            while (queue.Count != 0)
            {
                var version = queue.Dequeue();
                if (requested.TryGetValue(version.TypeObject.Guid, out var already))
                {
                    if (already.Guid != version.Guid) throw new BridgeException("DependencyConflict", "The selected update requires two versions of the same native dependency type.");
                    continue;
                }
                if (requested.Count >= 1000) throw new BridgeException("DependencyUnresolved", "Update impact exceeds 1000 native dependency types.");
                if (!version.IsDefault || version.TypeObject.Versions.Any(x => x.State == LibraryTypeVersionState.Committed && x.VersionNumber > version.VersionNumber))
                    throw new BridgeException("VersionSelectionUnsupported", "Every updated dependency must be its highest committed source default; native exact-version substitution is forbidden.");
                requested.Add(version.TypeObject.Guid, version);
                foreach (var dependency in LibraryDependencyCapture.Read(version).Versions) queue.Enqueue(dependency);
            }
            var uses = new JArray(); var types = new JArray(); bool completeComparisons = true;
            foreach (var selected in requested.Values)
            {
                var installed = change.Project.ProjectLibrary.FindType(selected.TypeObject.Guid);
                if (installed != null && installed.Versions.Any(x => x.IsDefault && x.VersionNumber > selected.VersionNumber)) throw new BridgeException("VersionSelectionUnsupported", "An affected dependency already has a newer project default; native update would not satisfy the exact selection.");
                var comparisons = new JArray(); int localUses = 0;
                if (installed != null)
                    foreach (var current in installed.Versions)
                    {
                        var nativeUses = current.FindInstances(plc.Software);
                        if (nativeUses.Count + uses.Count > 1000) throw new BridgeException("DependencyUnresolved", "More than 1000 affected native uses requires a smaller explicit update scope.");
                        foreach (var use in nativeUses)
                        {
                            var engineering = use.LibraryTypeInstance as IEngineeringObject;
                            if (engineering == null || !paths.TryGetValue(engineering, out var path)) throw new BridgeException("DependencyUnresolved", "A native type use is outside the complete exposed PLC block/UDT scope.");
                            uses.Add(new JObject { ["objectPath"] = path, ["object"] = LibraryObjectDescription(engineering), ["typeGuid"] = selected.TypeObject.Guid.ToString("D"), ["beforeVersionGuid"] = current.Guid.ToString("D"), ["beforeVersion"] = current.VersionNumber.ToString(), ["requestedVersionGuid"] = selected.Guid.ToString("D"), ["requestedVersion"] = selected.VersionNumber.ToString(), ["action"] = current.Guid == selected.Guid ? "keepExactVersion" : "update" });
                            localUses++;
                        }
                        if (nativeUses.Count == 0 || current.Guid == selected.Guid) continue;
                        try
                        {
                            var properties = current.CompareTo(selected).Properties.Take(201).ToList();
                            if (properties.Count > 200) throw new BridgeException("DependencyUnresolved", "Native version comparison exceeded 200 properties; impact is incomplete.");
                            comparisons.Add(new JObject { ["beforeVersionGuid"] = current.Guid.ToString("D"), ["requestedVersionGuid"] = selected.Guid.ToString("D"), ["properties"] = new JArray(properties.Select(x => new JObject { ["description"] = x.Description, ["status"] = x.DetailCompareStatus.ToString(), ["before"] = Convert.ToString(x.LeftValue), ["after"] = Convert.ToString(x.RightValue) })) });
                        }
                        catch (BridgeException) { throw; }
                        catch (Exception error) { completeComparisons = false; comparisons.Add(new JObject { ["beforeVersionGuid"] = current.Guid.ToString("D"), ["comparisonAvailable"] = false, ["reason"] = error.Message }); }
                    }
                if (localUses == 0 && change.Versions.Any(x => x.TypeObject.Guid == selected.TypeObject.Guid)) throw new BridgeException("NoMatchingUses", "A selected root type has no native uses in this PLC. Use library_copy_elements to synchronize only the library, or library_instantiate to create a new use.");
                types.Add(new JObject { ["requested"] = LibraryVersionInfo(selected), ["affectedUseCount"] = localUses, ["scope"] = localUses == 0 ? "librarySynchronizationOnly_NoNewEngineeringInstance" : "allNativeUsesInSelectedPlc", ["nativeComparisons"] = comparisons, ["libraryVersionsEligibleForCopy"] = new JArray(selected.TypeObject.Versions.Select(LibraryVersionInfo)) });
            }
            return new JObject { ["plcPath"] = plc.Path, ["affectedUses"] = uses, ["usesScopeComplete"] = true, ["types"] = types, ["noOp"] = uses.Count != 0 && uses.All(x => x.Value<string>("action") == "keepExactVersion"), ["nativeComparisonsComplete"] = completeComparisons, ["libraryScope"] = "Selected types and required native dependency types; native synchronization can copy their other committed library releases. It does not create new engineering instances when no uses exist.", ["runtimeCompatibilityVerified"] = false };
        }

        private static LibraryTypeFolder ResolveLibraryTypeFolder(ManagedLibrary library, string path)
        {
            if (path == null || path != "/types" && !path.StartsWith("/types/", StringComparison.Ordinal)) throw new BridgeException("LibraryIdentityMismatch", "Invalid type folder path.");
            LibraryTypeFolder folder = library.Native.TypeFolder;
            foreach (string segment in path.Substring(6).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) folder = folder.Folders.Find(Uri.UnescapeDataString(segment)) ?? throw new BridgeException("LibraryIdentityMismatch", "Type folder not found.");
            return folder;
        }
        private static MasterCopyFolder ResolveLibraryMasterFolder(ManagedLibrary library, string path)
        {
            if (path == null || path != "/masterCopies" && !path.StartsWith("/masterCopies/", StringComparison.Ordinal)) throw new BridgeException("LibraryIdentityMismatch", "Invalid master-copy folder path.");
            MasterCopyFolder folder = library.Native.MasterCopyFolder;
            foreach (string segment in path.Substring(13).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) folder = folder.Folders.Find(Uri.UnescapeDataString(segment)) ?? throw new BridgeException("LibraryIdentityMismatch", "Master-copy folder not found.");
            return folder;
        }

        private string LibraryChangeFingerprint(LibraryChange change)
        {
            var state = new JObject();
            foreach (var library in new[] { change.Source, change.Destination }.Where(x => x != null).Distinct())
            {
                state[library.Id] = LibraryRevision(library);
                if (library.Global != null)
                {
                    if (library.Global.IsModified && change.Kind != "library_save") throw new BridgeException("PreviewStale", "A global library has unsaved native edits. Recover it or explicitly persist its failed-operation state through library_save with recoveryChangeId.");
                    // Active native library databases are exclusively locked by TIA. The
                    // native save revision covers persisted changes; exports below cover
                    // selected version content, and immutable original snapshots are hashed.
                    state[library.Id + "NativeSaveRevision"] = new JObject { ["lastModified"] = library.Global.LastModified, ["lastModifiedBy"] = library.Global.LastModifiedBy, ["size"] = library.Global.Size, ["isModified"] = library.Global.IsModified };
                }
                if (library.Project != null)
                {
                    state[library.Id + "ProjectModified"] = library.Project.IsModified;
                    state[library.Id + "ProjectLastModified"] = library.Project.LastModified;
                    if (change.Masters.Count != 0 && library == change.Source && library.Project.IsModified) throw new BridgeException("PreviewStale", "Project master-copy reads require a saved baseline because native masters expose no content export.");
                }
            }
            // Native export fingerprints cover implementation/interface changes that preserve a version GUID.
            var documents = new JArray();
            foreach (var version in change.Versions)
            {
                if (version.State == LibraryTypeVersionState.InWork)
                {
                    documents.Add(LibraryInWorkVersionFingerprint(change, version));
                    continue;
                }
                string path = Path.Combine(change.Directory, "version-check-" + Guid.NewGuid().ToString("N") + ".xml");
                version.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                documents.Add(new JObject { ["versionGuid"] = version.Guid.ToString("D"), ["sha256"] = ChangeFileHash(path) });
            }
            state["versionDocuments"] = documents;
            if (change.ComponentContentPins != null)
            {
                var observations = new JArray();
                foreach (JObject pin in change.ComponentContentPins)
                {
                    var version = ResolveLibraryVersion(change.Source, pin);
                    string basis = Path.Combine(change.Directory, "component-source-check-" + Guid.NewGuid().ToString("N") + ".xml");
                    version.Export(new FileInfo(basis), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    string baseHash = ChangeFileHash(basis), oracleHash = null;
                    if (pin["contentPolicy"] != null)
                    {
                        string oracle = Path.Combine(change.Directory, "component-oracle-check-" + Guid.NewGuid().ToString("N") + ".xml");
                        version.Export(new FileInfo(oracle), ExportOptions.WithDefaults | ExportOptions.WithReadOnly, DocumentInfoOptions.None);
                        oracleHash = ChangeFileHash(oracle);
                    }
                    try { PlcComponentContentContract.ValidateSourceObservation(pin, baseHash, oracleHash, change.Source.Global?.Name); }
                    catch (PlcComponentException error) { throw new BridgeException(error.Code, error.Message); }
                    observations.Add(new JObject { ["typeGuid"] = pin["typeGuid"].DeepClone(), ["versionGuid"] = pin["versionGuid"].DeepClone(),
                        ["sourceBaseSha256"] = baseHash, ["sourceOracleSha256"] = oracleHash });
                }
                // Included before previewHash is computed; the same fingerprint
                // runs inside existing apply ExclusiveAccess before any claim,
                // checkpoint or native mutation.
                state["componentSourceObservations"] = observations;
            }
            if (change.Project != null)
            {
                state["projectPath"] = change.Project.Path?.FullName;
                state["projectContent"] = EngineeringFingerprint(new EngineeringChange { Kind = "library", Selector = change.Selector, Payload = change.Payload }, change.Project);
                state["projectModified"] = change.Project.IsModified;
                if (change.Masters.Count != 0 && change.Source?.Project != null && change.Project.IsModified)
                    throw new BridgeException("PreviewStale", "Project master-copy consumption requires a saved project baseline because master copies expose no standalone content export.");
                if (IsHmiLibraryTarget(change.Payload)) state["hmi"] = PrepareLibraryHmiAction(change.Project, change.Payload, change.Versions.FirstOrDefault(), change.Masters.FirstOrDefault());
                if (change.Kind == "library_update_project")
                {
                    var plc = SelectPlc(change.Selector);
                    state["updateState"] = CaptureLibraryUpdateState(change, plc);
                    state["updateCleanupGuard"] = PrepareLibraryUpdateCleanupGuard(change, plc);
                }
            }
            if (change.Kind == "library_create_type") state["documents"] = VerifyLibraryDocumentSnapshot(change);
            if (change.Kind == "library_cleanup" || change.Kind == "library_harmonize") state["maintenancePlan"] = LibraryMaintenancePlan(change);
            if (change.Kind == "library_upgrade")
            {
                string path = change.Payload.Value<string>("path");
                state["source"] = Path.GetExtension(path).StartsWith(".zal", StringComparison.OrdinalIgnoreCase) ? ChangeFileHash(path) : LibraryDirectoryHash(Path.GetDirectoryName(path));
            }
            return ChangeHash(state.ToString(Formatting.None));
        }

        private static JObject CaptureLibraryUpdateState(LibraryChange change, PlcSelection plc)
        {
            var budget = new ScanBudget(50000); var paths = new Dictionary<IEngineeringObject, string>();
            foreach (var scope in SoftwareScopes(plc, budget))
            {
                foreach (var block in ScopeBlocks(scope, budget)) paths[block.Item] = block.Path;
                foreach (var type in ScopeTypes(scope, budget)) paths[type.Item] = type.Path;
            }
            if (budget.Truncated) throw new BridgeException("PreservationCoverageIncomplete", "Complete PLC block/type inventory exceeded the update-preservation budget.");
            var versions = WalkLibraryTypes(change.Project.ProjectLibrary.TypeFolder, "/types", budget, 0).SelectMany(x => x.Value.Versions)
                .Where(x => x is CodeBlockLibraryTypeVersion || x is PlcTypeLibraryTypeVersion).Take(1001).ToList();
            if (budget.Truncated || versions.Count > 1000) throw new BridgeException("PreservationCoverageIncomplete", "Complete PLC library-use inventory exceeded 1000 native versions.");
            var uses = new List<JObject>();
            foreach (var version in versions)
                foreach (var use in version.FindInstances(plc.Software))
                {
                    if (uses.Count >= 1000) throw new BridgeException("PreservationCoverageIncomplete", "Complete PLC library-use inventory exceeded 1000 native uses.");
                    var native = use.LibraryTypeInstance as IEngineeringObject;
                    if (native == null || !paths.TryGetValue(native, out var path) || use.LibraryTypeVersion.Guid != version.Guid)
                        throw new BridgeException("PreservationCoverageIncomplete", "A native library use cannot be resolved to its exact PLC object path and version link.");
                    uses.Add(new JObject { ["objectPath"] = path, ["typeGuid"] = version.TypeObject.Guid.ToString("D"), ["versionGuid"] = version.Guid.ToString("D") });
                }
            if (uses.Select(x => x.Value<string>("objectPath")).Distinct(StringComparer.Ordinal).Count() != uses.Count)
                throw new BridgeException("PreservationCoverageIncomplete", "The native library-use inventory contains ambiguous object links.");
            return new JObject { ["plcPath"] = plc.Path, ["objectPaths"] = new JArray(paths.Values.OrderBy(x => x, StringComparer.Ordinal)), ["uses"] = new JArray(uses.OrderBy(x => x.Value<string>("objectPath"), StringComparer.Ordinal)), ["scopeComplete"] = true };
        }

        private static JObject PrepareLibraryUpdateCleanupGuard(LibraryChange change, PlcSelection plc)
        {
            // Native UpdateProject removes unreferenced engineering objects, including
            // unrelated library instances. DoNotDelete governs library versions only.
            // Prove each existing library use has a stable OB-rooted native reference.
            var budget = new ScanBudget(50000); var paths = new Dictionary<IEngineeringObject, string>();
            var roots = new List<string>();
            foreach (var scope in SoftwareScopes(plc, budget))
            {
                foreach (var block in ScopeBlocks(scope, budget))
                {
                    if (!block.Item.IsConsistent) throw new BridgeException("LibraryUpdateCleanupRisk", "Native cleanup reachability requires consistent compiled PLC objects: " + block.Path);
                    paths[block.Item] = block.Path;
                    if (block.Item is OB) roots.Add(block.Path);
                }
                foreach (var type in ScopeTypes(scope, budget))
                {
                    if (!type.Item.IsConsistent) throw new BridgeException("LibraryUpdateCleanupRisk", "Native cleanup reachability requires consistent compiled PLC types: " + type.Path);
                    paths[type.Item] = type.Path;
                }
            }
            var edges = new List<LibraryReferenceEdge>(); var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
            var changedCallers = new HashSet<string>(((JArray)change.Preview["updateImpact"]["affectedUses"]).Where(x => x.Value<string>("action") == "update").Select(x => x.Value<string>("objectPath")), StringComparer.Ordinal);
            var unavailable = new JArray();
            foreach (var item in paths.OrderBy(x => x.Value, StringComparer.Ordinal))
            {
                if (!budget.Take()) break;
                try
                {
                    var service = (item.Key as IEngineeringServiceProvider)?.GetService<CrossReferenceService>();
                    if (service == null) { unavailable.Add(item.Value); continue; }
                    AddLibraryUpdateReferenceEdges(service.GetCrossReferences(CrossReferenceFilter.AllObjects).Sources, paths, changedCallers, edges, edgeKeys, budget, 0);
                }
                catch (BridgeException) { throw; }
                catch (Exception error) { throw new BridgeException("LibraryUpdateCleanupRisk", "Native cross-reference evidence is unavailable for " + item.Value + ": " + error.Message); }
                if (budget.Truncated) break;
            }
            var required = ((JArray)change.Preview["updateBaseline"]["uses"]).Select(x => x.Value<string>("objectPath")).ToArray();
            string[] reachable;
            try { reachable = LibraryReferenceReachability.RequireReachable(paths.Values, roots, edges, required, !budget.Truncated); }
            catch (ArgumentException error)
            {
                throw new BridgeException("LibraryUpdateCleanupRisk", "Native UpdateProject may delete unreferenced library objects anywhere in this PLC. Stable OB-rooted references could not be proved for every existing library instance; no update was dispatched. " + error.Message,
                    new JObject { ["requiredLibraryUses"] = new JArray(required), ["obRoots"] = new JArray(roots), ["objectsWithoutCrossReferenceService"] = unavailable, ["excludedMutableCallers"] = new JArray(changedCallers.OrderBy(x => x, StringComparer.Ordinal)) });
            }
            return new JObject { ["policy"] = "Every baseline library use must have an exact native reference path from an OB, excluding outgoing references of updated objects.", ["requiredLibraryUses"] = new JArray(required), ["obRoots"] = new JArray(roots.OrderBy(x => x, StringComparer.Ordinal)),
                ["reachablePaths"] = new JArray(reachable), ["proofEdges"] = new JArray(edges.OrderBy(x => x.CallerPath, StringComparer.Ordinal).ThenBy(x => x.CalleePath, StringComparer.Ordinal).Select(x => new JObject { ["callerPath"] = x.CallerPath, ["calleePath"] = x.CalleePath })),
                ["objectsWithoutCrossReferenceService"] = unavailable, ["proofScope"] = "All required baseline library uses have positive native OB-rooted reference paths; unavailable or unresolved graph edges provide no evidence.",
                ["allRequiredUsesProven"] = true, ["completeReferenceGraphClaimed"] = false, ["nativeCleanupDisabled"] = false, ["runtimeReachabilityClaimed"] = false };
        }

        private static void AddLibraryUpdateReferenceEdges(IEnumerable<SourceObject> sources, Dictionary<IEngineeringObject, string> paths, HashSet<string> changedCallers, List<LibraryReferenceEdge> edges, HashSet<string> edgeKeys, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; return; }
            foreach (var source in sources)
            {
                if (!budget.Take()) return;
                string from = LibraryReferenceOwnerPath(source.UnderlyingObject, paths);
                foreach (var reference in source.References)
                {
                    if (!budget.Take()) return;
                    string to = LibraryReferenceOwnerPath(reference.UnderlyingObject, paths);
                    foreach (var location in reference.Locations)
                    {
                        if (!budget.Take()) return;
                        int direction = OfflineEvidence.DependencyDirection(location.ReferenceType.ToString());
                        if (direction == 0 || from == null || to == null) continue;
                        string caller = direction > 0 ? from : to, callee = direction > 0 ? to : from;
                        if (caller == callee || changedCallers.Contains(caller)) continue;
                        if (edgeKeys.Add(caller + "\n" + callee)) edges.Add(new LibraryReferenceEdge(caller, callee));
                    }
                }
                AddLibraryUpdateReferenceEdges(source.Children, paths, changedCallers, edges, edgeKeys, budget, depth + 1);
                if (budget.Truncated) return;
            }
        }

        private static string LibraryReferenceOwnerPath(IEngineeringObject item, Dictionary<IEngineeringObject, string> paths)
        {
            IEngineeringInstance current = item;
            for (int depth = 0; current != null && depth < 64; depth++)
            {
                if (current is IEngineeringObject engineering && paths.TryGetValue(engineering, out var path)) return path;
                current = current.Parent;
            }
            return null;
        }

        private void VerifyLibraryUpdateReadback(LibraryChange change, string phase, bool updated)
        {
            if (change.Kind != "library_update_project") return;
            var actual = CaptureLibraryUpdateState(change, SelectPlc(change.Selector));
            var baseline = (JObject)change.Preview["updateBaseline"];
            var record = new JObject { ["phase"] = phase, ["observedUtc"] = DateTime.UtcNow, ["state"] = actual, ["verified"] = false, ["verificationScope"] = "All baseline PLC object paths remain present, with exact requested affected and unchanged unrelated native library type/version links. Arbitrary engineering content preservation is not asserted." };
            if (!(change.Journal["updateReadbacks"] is JArray)) change.Journal["updateReadbacks"] = new JArray();
            ((JArray)change.Journal["updateReadbacks"]).Add(record);
            LibraryJournal(change, "verify_update_" + phase, "dispatching");
            var actualPaths = new HashSet<string>(((JArray)actual["objectPaths"]).Values<string>(), StringComparer.Ordinal);
            var missing = ((JArray)baseline["objectPaths"]).Values<string>().Where(x => !actualPaths.Contains(x)).ToArray();
            if (missing.Length != 0)
                throw new BridgeException("PreservationMismatch", "PLC objects disappeared during library update phase " + phase + ".", new JObject { ["phase"] = phase, ["missingObjectPaths"] = new JArray(missing) });
            var observedUses = ((JArray)actual["uses"]).Cast<JObject>().ToDictionary(x => x.Value<string>("objectPath"), StringComparer.Ordinal);
            var expectedUses = (JArray)baseline["uses"];
            var affected = ((JArray)change.Preview["updateImpact"]["affectedUses"]).Cast<JObject>().ToDictionary(x => x.Value<string>("objectPath"), StringComparer.Ordinal);
            foreach (var use in expectedUses)
            {
                string path = use.Value<string>("objectPath");
                string expectedVersion = updated && affected.TryGetValue(path, out var effect) ? effect.Value<string>("requestedVersionGuid") : use.Value<string>("versionGuid");
                if (!observedUses.TryGetValue(path, out var observed) || observed.Value<string>("typeGuid") != use.Value<string>("typeGuid") || observed.Value<string>("versionGuid") != expectedVersion)
                    throw new BridgeException("PreservationMismatch", "An affected or unrelated native PLC library use differs from its approved path/type/version after phase " + phase + ".", new JObject { ["phase"] = phase, ["objectPath"] = path, ["expectedVersionGuid"] = expectedVersion, ["actual"] = observed });
            }
            if (observedUses.Count != expectedUses.Count) throw new BridgeException("PreservationMismatch", "Native update created unpreviewed PLC library uses during phase " + phase + ".");
            record["verified"] = true;
            LibraryJournal(change, "verify_update_" + phase, "completed");
        }

        private static string VerifyLibraryDocumentSnapshot(LibraryChange change)
        {
            if (change.DocumentDirectory == null) throw new BridgeException("LibraryContentFingerprintUnavailable", "Prepared native authoring document snapshot is unavailable.");
            string hash = LibraryDirectoryHash(change.DocumentDirectory);
            if (hash != change.Preview.Value<string>("documentsHash")) throw new BridgeException("PackageChanged", "The owned native authoring document snapshot differs from the approved complete content hash.");
            return hash;
        }

        private JObject LibraryInWorkVersionFingerprint(LibraryChange change, LibraryTypeVersion version)
        {
            // TIA forbids LibraryTypeVersion.Export while InWork. Its native test
            // instances are the editable payload: cover their exact version links
            // and full native documents, alongside the owning project fingerprint.
            var project = change.Source?.Project;
            if (project == null || change.Project != project || !(version is CodeBlockLibraryTypeVersion || version is PlcTypeLibraryTypeVersion))
                throw new BridgeException("LibraryContentFingerprintUnavailable", "This in-work version requires complete native test-instance export coverage in its owning project. Only PLC block and PLC data-type test instances are currently supported.");
            var budget = new ScanBudget(50000);
            var instances = new List<JObject>();
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in DeviceItems(project, budget))
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (software == null) continue;
                var plc = new PlcSelection { Project = project, Software = software, Path = entry.Path + "/plc/" + Segment(software.Name) };
                var paths = new Dictionary<IEngineeringObject, string>();
                foreach (var scope in SoftwareScopes(plc, budget))
                {
                    foreach (var block in ScopeBlocks(scope, budget)) paths[block.Item] = block.Path;
                    foreach (var type in ScopeTypes(scope, budget)) paths[type.Item] = type.Path;
                }
                if (budget.Truncated) throw new BridgeException("LibraryContentFingerprintUnavailable", "Cannot establish complete in-work test-instance paths within the project inventory limit.");
                foreach (var use in version.FindInstances(software))
                {
                    if (instances.Count >= 1000) throw new BridgeException("LibraryContentFingerprintUnavailable", "In-work version has more than 1000 test instances; content coverage is incomplete.");
                    var item = use.LibraryTypeInstance as IEngineeringObject;
                    if (item == null || use.LibraryTypeVersion?.Guid != version.Guid || !paths.TryGetValue(item, out var instancePath) || !seenPaths.Add(instancePath))
                        throw new BridgeException("LibraryContentFingerprintUnavailable", "An in-work test instance cannot be resolved to a unique exact native version and project object path.");
                    string path = Path.Combine(change.Directory, "inwork-check-" + Guid.NewGuid().ToString("N") + ".xml");
                    EngineeringFingerprintRules.Content<string> content;
                    if (item is PlcBlock block && version is CodeBlockLibraryTypeVersion)
                        content = EngineeringFingerprintRules.ReadInWorkBlockContent(block.IsKnowHowProtected,
                            () => { block.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None); return ChangeFileHash(path); },
                            () => { var provider = block.GetService<FingerprintProvider>(); return provider == null ? null : ChangeHash(new JArray(provider.GetFingerprints().OrderBy(f => f.Id).Select(f => new JObject { ["id"] = f.Id.ToString(), ["value"] = ChangeScalar(f.Value) })).ToString(Formatting.None)); });
                    else if (item is PlcType type && version is PlcTypeLibraryTypeVersion && !type.IsKnowHowProtected)
                    {
                        type.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                        content = new EngineeringFingerprintRules.Content<string> { Mode = "nativeExport", Payload = ChangeFileHash(path) };
                    }
                    else throw new BridgeException("LibraryContentFingerprintUnavailable", "An in-work test instance is a protected PLC data type or has an unsupported native family; full content export is required.");
                    instances.Add(new JObject { ["path"] = instancePath, ["plcPath"] = plc.Path, ["versionGuid"] = use.LibraryTypeVersion.Guid.ToString("D"), ["engineeringType"] = item.GetType().FullName, ["contentMode"] = content.Mode, ["sha256"] = content.Payload });
                }
            }
            if (budget.Truncated || instances.Count == 0)
                throw new BridgeException("LibraryContentFingerprintUnavailable", "No complete native test-instance payload could be captured for this in-work version. Select or create its test instance in the owning project before preparing this operation.");
            return new JObject { ["mode"] = "nativeInWorkTestInstanceExports", ["version"] = LibraryVersionInfo(version), ["projectPath"] = project.Path.FullName, ["scopeComplete"] = true, ["instances"] = new JArray(instances.OrderBy(x => x.Value<string>("path"), StringComparer.Ordinal)) };
        }

        private JObject LibraryChangeApply(JObject args)
        {
            if (PlcComponentQualificationGate.IsId(args.Value<string>("changeId"))) return ComponentQualificationApply(args);
            string id = ChangeText(args, "changeId", 64);
            if (!_libraryChanges.TryGetValue(id, out var change)) throw new BridgeException("OutcomeUnknown", "Prepared native handles do not survive restart. Read durable change_status; never replay a dispatched operation.");
            bool component = change.Kind == "library_integrate" && !IsHmiLibraryTarget(change.Payload);
            if (component && !_libraryIntegrateApplyEnabled)
                throw new BridgeException(PlcLibraryIntegrationPlanner.ApplyError, "This PLC integration is a plan only. No idempotency claim, checkpoint, save, compile or native mutation was dispatched. Review the explicit separate steps and native qualification gates.", LibraryChangeView(change));
            if (component && PlcCombinedApplyRefusal(change) is string refusal)
                throw new BridgeException(PlcLibraryIntegrationPlanner.ApplyError, "Combined apply refused: " + refusal + " No idempotency claim, checkpoint or native mutation was dispatched.", LibraryChangeView(change));
            string key = ChangeText(args, "idempotencyKey", 128);
            if (ChangeText(args, "previewHash", 64) != change.PreviewHash) throw new BridgeException("PreviewMismatch", "previewHash does not match.");
            if (change.IdempotencyKey != null)
            {
                if (change.IdempotencyKey != key) throw new BridgeException("IdempotencyConflict", "This operation is already claimed with a different key.");
                if (change.State == "applied") return LibraryChangeView(change);
                throw new BridgeException("OutcomeUnknown", "This operation was already dispatched and cannot be replayed.", LibraryChangeView(change));
            }
            RequireChangeBool(args, "checkpointSave");
            bool compile = ExplicitChangeBool(args, "compile"), save = ExplicitChangeBool(args, "save");
            if (change.Project != null && !compile) throw new BridgeException("InvalidRequest", "Project library operations require compile=true.");
            if (!save) throw new BridgeException("InvalidRequest", "Library transactions require save=true so their durable outcome can be verified after reopen.");
            if (change.State != "prepared") throw new BridgeException("InvalidChangeState", "Only a prepared library change may be applied.");
            using (SharedMutationExclusive("TIA MCP library transaction " + change.Kind))
            {
                if (change.Kind == "library_create" || change.Kind == "library_upgrade") RequireLibraryHandleCapacity(1);
                if (LibraryChangeFingerprint(change) != change.Preview.Value<string>("fingerprint")) throw new BridgeException("PreviewStale", "An affected library or target changed after preview.");
                VerifyLibrarySources(change);
                var coordinator = component ? PlcComponentApplyCoordinator(change) : null;
                // A file claim survives worker termination. CreateNew is atomic across processes.
                try { LibraryJournalStore.Claim(Path.GetDirectoryName(change.Directory), key, change.Id); }
                catch (IOException) { throw new BridgeException("IdempotencyConflict", "This durable idempotency key was already claimed. Inspect its previous operation; no native call was dispatched."); }
                change.IdempotencyKey = key; change.Journal["idempotencyKeyHash"] = ChangeHash(key);
                if (coordinator != null) return ApplyPlcComponentIntegration(change, coordinator);
                try
                {
                    change.State = "checkpointing"; LibraryJournal(change, "claim", "completed");
                    CheckpointLibraryResources(change);
                    VerifyLibraryUpdateReadback(change, "checkpoint", false);
                    change.State = "applying"; LibraryJournal(change, "native_mutation", "dispatching");
                    change.Journal["result"] = ApplyLibraryAction(change);
                    LibraryJournal(change, "native_mutation", "completed");
                    VerifyLibraryUpdateReadback(change, "native_action", true);
                    if (change.Project != null)
                    {
                        LibraryJournal(change, "compile", "dispatching");
                        var result = IsHmiLibraryTarget(change.Payload) ? CompileLibraryHmiTarget(change.Project, (JObject)change.Payload["target"]) : CompileLibraryProject(change);
                        change.Journal["compile"] = result; LibraryJournal(change, "compile", "completed");
                        if (!CompilationEvidenceRules.IsSuccessful(result)) throw new BridgeException("CompileFailed", "Applied library change has compiler errors or incomplete diagnostic evidence. Checkpoint resources remain available.");
                        VerifyLibraryUpdateReadback(change, "compile", true);
                    }
                    foreach (var library in LibraryWrittenResources(change))
                    {
                        LibraryJournal(change, "save_" + library.Id, "dispatching");
                        if (library.Project != null) library.Project.Save(); else ((UserGlobalLibrary)library.Global).Save();
                        LibraryJournal(change, "save_" + library.Id, "completed");
                    }
                    if (change.Project != null && !LibraryWrittenResources(change).Any(x => x.Project == change.Project))
                    { LibraryJournal(change, "save_project", "dispatching"); change.Project.Save(); LibraryJournal(change, "save_project", "completed"); }
                    VerifyLibraryUpdateReadback(change, "save", true);
                    VerifyLibrarySources(change);
                    var manifest = new JObject { ["schemaVersion"] = 1, ["manifestKind"] = "tia-library-lock", ["changeId"] = change.Id, ["previewHash"] = change.PreviewHash, ["createdUtc"] = DateTime.UtcNow,
                        ["sources"] = change.Preview["resources"].DeepClone(), ["selectedVersionsAndMasters"] = change.Preview["selection"].DeepClone(), ["dependencies"] = change.Preview["dependencies"].DeepClone(),
                        ["target"] = change.Payload["target"]?.DeepClone(), ["projectPath"] = change.Project?.Path?.FullName, ["actualResult"] = change.Journal["result"]?.DeepClone(), ["compilation"] = change.Journal["compile"]?.DeepClone(),
                        ["savedLibraryIdentities"] = new JArray(LibraryWrittenResources(change).Select(LibraryView)), ["nativeSaveCompleted"] = true, ["freshReopenVerified"] = false, ["runtimeVerified"] = false,
                        ["journalPath"] = Path.Combine(change.Directory, "journal.json"), ["pinScope"] = "Exact observations for this bridge operation; does not prevent later native edits or enforce Git history." };
                    string lockPath = Path.Combine(change.Directory, "library-lock.v1.json"); WriteLibraryRecord(lockPath, manifest);
                    var lockArtifact = FileArtifact(new FileInfo(lockPath)); lockArtifact["mediaType"] = "application/json"; change.Journal["lockManifest"] = lockArtifact;
                    change.State = "applied"; change.Journal["engineeringVerified"] = change.Project != null;
                    change.Journal["persistenceVerified"] = false; change.Journal["runtimeVerified"] = false;
                    LibraryJournal(change, "transaction", "completed");
                    return LibraryChangeView(change);
                }
                catch (Exception exception)
                {
                    change.State = "recovery_required"; change.Journal["failure"] = new JObject { ["code"] = (exception as BridgeException)?.Code ?? "NativeLibraryError", ["message"] = exception.Message, ["details"] = (exception as BridgeException)?.Details };
                    LibraryJournal(change, "transaction", "failed");
                    throw new BridgeException((exception as BridgeException)?.Code ?? "NativeLibraryError", exception.Message, LibraryChangeView(change));
                }
            }
        }

        private IEnumerable<ManagedLibrary> LibraryWrittenResources(LibraryChange change)
        {
            if (change.Destination != null) yield return change.Destination;
            if (IsLibraryMaintenance(change.Kind) && change.Source != null && change.Source != change.Destination) yield return change.Source;
        }

        private JObject CompileLibraryProject(LibraryChange change)
        {
            return CompileDeviceProject(change.Project, (path, state) => LibraryJournal(change, "compile_device_" + path, state));
        }

        private void VerifyLibrarySources(LibraryChange change)
        {
            foreach (var library in new[] { change.Source, change.Destination }.Where(x => x?.SourcePath != null).Distinct())
            {
                string actual = Path.GetExtension(library.SourcePath).StartsWith(".zal", StringComparison.OrdinalIgnoreCase) ? ChangeFileHash(library.SourcePath) : LibraryDirectoryHash(Path.GetDirectoryName(library.SourcePath));
                if (actual != library.SourceHash) throw new BridgeException("PackageChanged", "Original source library content changed: " + library.SourcePath);
            }
        }

        private void CheckpointLibraryResources(LibraryChange change)
        {
            if (change.Project != null)
            {
                EnsureProjectOffline(change.Project); LibraryJournal(change, "checkpoint_project_save", "dispatching"); change.Project.Save(); LibraryJournal(change, "checkpoint_project_save", "completed");
                LibraryJournal(change, "checkpoint_project_archive", "dispatching"); change.Project.Archive(new DirectoryInfo(change.Directory), "before.zap21", ProjectArchivationMode.Compressed);
                AddLibraryCheckpoint(change, "project", change.Project.Path?.FullName, Path.Combine(change.Directory, "before.zap21")); LibraryJournal(change, "checkpoint_project_archive", "completed");
            }
            int index = 0;
            foreach (var library in LibraryWrittenResources(change).Where(x => x.Global != null))
            {
                var native = (UserGlobalLibrary)library.Global; string name = "before-" + index++ + ".zal21";
                if (change.Kind == "library_save" && change.Preview["existingRecoveryCheckpoint"] is JObject prior)
                {
                    string source = prior.Value<string>("archivePath"), target = Path.Combine(change.Directory, name);
                    if (ChangeFileHash(source) != prior.Value<string>("sha256")) throw new BridgeException("CheckpointInvalid", "The previous recovery archive changed.");
                    File.Copy(source, target, false); AddLibraryCheckpoint(change, "library", native.Path.FullName, target);
                    LibraryJournal(change, "retain_failed_operation_checkpoint", "completed"); continue;
                }
                LibraryJournal(change, "checkpoint_save_" + library.Id, "dispatching"); native.Save(); LibraryJournal(change, "checkpoint_save_" + library.Id, "completed");
                LibraryJournal(change, "checkpoint_archive_" + library.Id, "dispatching"); native.Archive(new DirectoryInfo(change.Directory), name, LibraryArchivationMode.Compressed);
                AddLibraryCheckpoint(change, "library", native.Path?.FullName, Path.Combine(change.Directory, name)); LibraryJournal(change, "checkpoint_archive_" + library.Id, "completed");
            }
            if (change.Preview["outputAbsent"] != null)
            {
                string path = change.Preview.Value<string>("outputAbsent");
                if (Directory.Exists(path) || File.Exists(path)) throw new BridgeException("PreviewStale", "A planned new output already exists.");
                ((JArray)change.Journal["outputs"]).Add(new JObject { ["path"] = path, ["existedBefore"] = false, ["recoveryPolicy"] = "Retained for inspection. Never deleted automatically." });
                LibraryJournal(change, "record_absent_output", "completed");
            }
        }
        private static void AddLibraryCheckpoint(LibraryChange change, string kind, string original, string path)
        {
            if (!File.Exists(path)) throw new BridgeException("CheckpointFailed", "Native archive was not created.");
            ((JArray)change.Journal["checkpoints"]).Add(new JObject { ["kind"] = kind, ["originalPath"] = original, ["archivePath"] = path, ["sha256"] = ChangeFileHash(path) });
        }

        private JObject ApplyLibraryAction(LibraryChange change)
        {
            if (change.Kind == "library_integrate" && !IsHmiLibraryTarget(change.Payload))
                throw new BridgeException(PlcLibraryIntegrationPlanner.ApplyError, "PLC component recipes have no native combined executor.");
            var payload = change.Payload; var result = new JObject { ["objects"] = new JArray() }; var rows = (JArray)result["objects"];
            switch (change.Kind)
            {
                case "library_create":
                {
                    string directory = change.Preview.Value<string>("outputAbsent"); Directory.CreateDirectory(directory);
                    var library = _portal.GlobalLibraries.Create<UserGlobalLibrary>(new DirectoryInfo(directory), payload.Value<string>("name"));
                    var entry = RegisterLibrary(library, null, library, true, true); entry.Directory = directory;
                    change.Destination = entry; result["library"] = LibraryView(entry); break;
                }
                case "library_upgrade":
                {
                    string path = Path.GetFullPath(payload.Value<string>("path")), root = change.Preview.Value<string>("outputAbsent");
                    string snapshot = Path.Combine(root, "source"), directory = Path.Combine(root, "work"), expected = change.Preview.Value<string>("upgradeSourceHash");
                    string snapshotPath = Path.Combine(snapshot, Path.GetFileName(path));
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("PackageChanged", "Upgrade input became a reparse point after preparation.");
                    Directory.CreateDirectory(snapshot); Directory.CreateDirectory(directory);
                    bool archive = Path.GetExtension(path).StartsWith(".zal", StringComparison.OrdinalIgnoreCase);
                    LibraryJournal(change, "upgrade_source_snapshot", "dispatching");
                    if (archive)
                    {
                        if (new FileInfo(path).Length > 1024L * 1024 * 1024) throw new BridgeException("PackageLimitExceeded", "A native library archive exceeds the 1 GiB snapshot limit.");
                        LibraryCopyArchiveSnapshot(path, snapshotPath);
                        if (ChangeFileHash(snapshotPath) != expected) throw new BridgeException("PackageChanged", "The immutable upgrade archive snapshot differs from the approved archive bytes.");
                    }
                    else
                    {
                        LibraryCopyDirectory(Path.GetDirectoryName(path), snapshot);
                        if (LibraryDirectoryHash(snapshot) != expected || !string.Equals(ChangeFileHash(snapshotPath), payload.Value<string>("expectedSha256"), StringComparison.OrdinalIgnoreCase)) throw new BridgeException("PackageChanged", "The immutable upgrade directory snapshot differs from the approved native marker or companion-file manifest.");
                        if (payload["expectedFiles"] is JArray expectedFiles) VerifyLibraryExpectedFiles(snapshot, expectedFiles);
                        if (System.IO.Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories).Length < 2) throw new BridgeException("PackageInvalid", "A lone native library marker is incomplete. Supply its companion files or a native archive.");
                        LibraryCopyWorkingDirectory(snapshot, directory);
                        if (LibraryDirectoryHash(directory) != expected) throw new BridgeException("PackageChanged", "The mutable upgrade working copy differs from its approved immutable snapshot.");
                    }
                    change.Journal["upgradeSourceSnapshot"] = new JObject { ["path"] = snapshotPath, ["sha256"] = expected, ["hashScope"] = change.Preview["upgradeSourceHashScope"], ["originalSourcePath"] = path, ["owned"] = true, ["workingDirectory"] = directory };
                    LibraryJournal(change, "upgrade_source_snapshot", "completed");
                    LibraryJournal(change, "native_upgrade", "dispatching");
                    GlobalLibrary library = UpgradeLibrary(archive, snapshotPath, directory, Path.GetFileName(path), result);
                    var entry = RegisterLibrary(library, null, library, true, true); entry.Directory = directory; entry.SourcePath = snapshotPath; entry.SourceHash = expected; entry.OriginalSourcePath = path;
                    change.Destination = entry; result["library"] = LibraryView(entry); result["sourceSnapshotOwned"] = true;
                    LibraryJournal(change, "native_upgrade", "completed"); break;
                }
                case "library_copy_elements": case "library_publish":
                    foreach (var version in change.Versions)
                    {
                        LibraryJournal(change, "copy_type_" + version.TypeObject.Guid, "dispatching");
                        version.TypeObject.UpdateLibrary(change.Destination.Native, DeleteUnusedVersionsMode.DoNotDelete, StructureConflictResolutionMode.CancelIfStructureConflicts, ForceUpdateMode.SetOnlyHigherUpdatedVersionAsDefault);
                        if (change.Destination.Native.FindVersion(version.Guid) == null) throw new BridgeException("PreservationMismatch", "Selected version is absent after native type synchronization.");
                        rows.Add(LibraryVersionInfo(change.Destination.Native.FindVersion(version.Guid))); LibraryJournal(change, "copy_type_" + version.TypeObject.Guid, "completed");
                    }
                    foreach (var master in change.Masters)
                    { LibraryJournal(change, "copy_master_" + master.Name, "dispatching"); var copy = change.Destination.Native.MasterCopyFolder.MasterCopies.CreateFrom(master); rows.Add(new JObject { ["masterCopy"] = copy.Name }); LibraryJournal(change, "copy_master_" + master.Name, "completed"); }
                    result["typeSynchronization"] = VerifyLibraryTypeSynchronization(change);
                    break;
                case "library_instantiate": case "library_integrate":
                    if (IsHmiLibraryTarget(payload)) return ApplyLibraryHmiAction(change.Project, payload, change.Versions.FirstOrDefault(), change.Masters.FirstOrDefault());
                    var scope = LibraryPlcScope(change, out var plc);
                    foreach (var version in change.Versions)
                    {
                        LibraryJournal(change, "instantiate_" + version.Guid, "dispatching"); object created;
                        if (version is CodeBlockLibraryTypeVersion block) created = scope.Blocks.Blocks.CreateFrom(block, UpdatePathsMode.ThrowIfPathsConflict);
                        else created = scope.Types.Types.CreateFrom((PlcTypeLibraryTypeVersion)version, UpdatePathsMode.ThrowIfPathsConflict);
                        var installed = change.Project.ProjectLibrary.FindVersion(version.Guid) ?? throw new BridgeException("VersionSelectionUnsupported", "Selected version GUID was not copied into the project library.");
                        var uses = installed.FindInstances(plc.Software).ToList();
                        if (!uses.Any(x => x.LibraryTypeInstance.Equals(created))) throw new BridgeException("VersionSelectionUnsupported", "Native result does not carry the exact requested version link.");
                        rows.Add(new JObject { ["object"] = LibraryObjectDescription(created), ["version"] = LibraryVersionInfo(installed), ["linkVerified"] = true });
                        LibraryJournal(change, "instantiate_" + version.Guid, "completed");
                    }
                    if (change.Masters.Count == 0)
                    {
                        VerifyReusedLibraryDependencies(change, plc); var after = new ScanBudget(50000);
                        VerifyInstantiatedNames(change, ScopeBlocks(scope, after).Select(x => x.Item.Name).Concat(ScopeTypes(scope, after).Select(x => x.Item.Name)).ToList());
                        if (after.Truncated) throw new BridgeException("DependencyUnresolved", "Post-instantiation inventory exceeded its completeness budget.");
                    }
                    foreach (var master in change.Masters)
                    {
                        LibraryJournal(change, "instantiate_master_" + master.Name, "dispatching");
                        string composition = payload.Value<string>("destinationComposition");
                        object created = composition == "blocks" ? (object)scope.Blocks.Blocks.CreateFrom(master, MasterCopyMode.ThrowIfExists)
                            : composition == "tagTables" ? (object)scope.Tags.TagTables.CreateFrom(master, MasterCopyMode.ThrowIfExists)
                            : scope.Types.Types.CreateFrom(master, MasterCopyMode.ThrowIfExists);
                        rows.Add(LibraryObjectDescription(created)); LibraryJournal(change, "instantiate_master_" + master.Name, "completed");
                    }
                    break;
                case "library_update_project":
                    var selectedPlc = SelectPlc(change.Selector);
                    if (change.Preview["updateImpact"].Value<bool>("noOp")) { result["noOp"] = true; result["reason"] = "All selected native uses already have the exact requested version GUIDs."; break; }
                    foreach (var version in change.Versions)
                    {
                        LibraryJournal(change, "update_type_" + version.TypeObject.Guid, "dispatching");
                        version.TypeObject.UpdateProject(selectedPlc.Software, DeleteUnusedVersionsMode.DoNotDelete, StructureConflictResolutionMode.CancelIfStructureConflicts, ForceUpdateMode.SetOnlyHigherUpdatedVersionAsDefault);
                        var installed = change.Project.ProjectLibrary.FindVersion(version.Guid) ?? throw new BridgeException("VersionSelectionUnsupported", "Selected version was not copied by native update.");
                        foreach (var other in installed.TypeObject.Versions) if (other.Guid != version.Guid && other.FindInstances(selectedPlc.Software).Any()) throw new BridgeException("VersionSelectionUnsupported", "Native update retained uses at another version. Restore or inspect this outcome; no selected-version success is claimed.");
                        rows.Add(LibraryVersionInfo(installed)); LibraryJournal(change, "update_type_" + version.TypeObject.Guid, "completed");
                    }
                    foreach (var impact in (JArray)change.Preview["updateImpact"]["types"])
                    {
                        var selected = (JObject)impact["requested"];
                        var installed = change.Project.ProjectLibrary.FindVersion(Guid.Parse(selected.Value<string>("versionGuid")));
                        if (installed == null) throw new BridgeException("VersionSelectionUnsupported", "An explicitly previewed dependency version is absent after native update.");
                        foreach (var other in installed.TypeObject.Versions)
                            if (other.Guid != installed.Guid && other.FindInstances(selectedPlc.Software).Any()) throw new BridgeException("VersionSelectionUnsupported", "A dependent type retained native uses at an unrequested version after update.");
                    }
                    break;
                case "library_create_type":
                    VerifyLibraryDocumentSnapshot(change);
                    foreach (string documentName in LibraryDocumentNames.Read(payload))
                    {
                        var createdType = payload.Value<string>("targetEnvironment") == "selectedPlc"
                            ? change.Destination.Native.TypeFolder.Types.CreateFromDocuments(new DirectoryInfo(change.DocumentDirectory), documentName, payload.Value<string>("destinationComposition") == "blocks" ? (IEngineeringObject)LibraryPlcScope(change, out _).Blocks : LibraryPlcScope(change, out _).Types, LibraryImportOptions.None)
                            : change.Destination.Native.TypeFolder.Types.CreateFromDocuments(new DirectoryInfo(change.DocumentDirectory), documentName, LibraryImportOptions.None);
                        rows.Add(new JObject { ["typeGuid"] = createdType.CreatedType.Guid.ToString("D"), ["name"] = createdType.CreatedType.Name, ["transferState"] = createdType.TransferResultState.ToString() });
                    }
                    break;
                case "library_create_master_copy":
                    var copyResult = change.Destination.Native.MasterCopyFolder.MasterCopies.Create((IMasterCopySource)LibraryMasterCopySource(change).Target); rows.Add(new JObject { ["masterCopy"] = copyResult.Name }); break;
                case "library_edit_version":
                    LibraryTypeVersion edited;
                    if (change.DocumentDirectory != null) edited = ApplyLibraryVersionDocuments(change, result);
                    else if (change.Versions[0] is CodeBlockLibraryTypeVersion || change.Versions[0] is PlcTypeLibraryTypeVersion)
                    {
                        var use = change.Versions[0].FindInstances(SelectPlc(change.Selector).Software).Single(x => LibraryObjectDescription(x.LibraryTypeInstance).Value<string>("Name") == payload.Value<string>("testInstanceName"));
                        edited = change.Versions[0].Edit(use);
                    }
                    else edited = change.Versions[0].Edit();
                    rows.Add(LibraryVersionInfo(edited)); break;
                case "library_release_version":
                    foreach (var released in change.Versions)
                    {
                        released.Release(CreateOrReleaseDependenciesMode.DoNotAutomaticallyCreateOrReleaseDependencies, Version.Parse(payload.Value<string>("versionNumber")), payload.Value<string>("author"), payload.Value<string>("comment"));
                        if (released.State != LibraryTypeVersionState.Committed || released.VersionNumber != Version.Parse(payload.Value<string>("versionNumber"))) throw new BridgeException("PreservationMismatch", "Native released state/version differs from the requested release.");
                        rows.Add(LibraryVersionInfo(released));
                    }
                    break;
                case "library_discard_version":
                    var discardedGuid = change.Versions[0].Guid; change.Versions[0].Discard();
                    result["discardedVersionGuid"] = discardedGuid.ToString("D"); result["remainingType"] = change.Source.Native.FindVersion(discardedGuid) == null ? JValue.CreateNull() : LibraryVersionInfo(change.Source.Native.FindVersion(discardedGuid)); break;
                case "library_set_default":
                    change.Versions[0].SetAsDefault();
                    if (!change.Versions[0].IsDefault) throw new BridgeException("PreservationMismatch", "Selected version is not the native default after SetAsDefault.");
                    rows.Add(LibraryVersionInfo(change.Versions[0])); break;
                case "library_type_policy":
                    if (payload["doNotUse"] != null) change.Versions[0].TypeObject.DoNotUse = payload.Value<bool>("doNotUse");
                    if (payload["setForUpdate"] != null) change.Versions[0].TypeObject.SetForUpdate = payload.Value<bool>("setForUpdate");
                    rows.Add(LibraryVersionInfo(change.Versions[0]));
                    break;
                case "library_organize": ApplyLibraryFolderOperation(change); break;
                case "library_cleanup": case "library_harmonize": return ApplyLibraryMaintenance(change);
                case "library_delete": if (change.Versions.Count == 1) change.Versions[0].Delete(); else change.Masters[0].Delete(); break;
                case "library_save": break; // Save is the durable transaction's separately journaled final stage.
                case "library_archive":
                    ((UserGlobalLibrary)change.Source.Global).Archive(new DirectoryInfo(change.Directory), "published.zal21", LibraryArchivationMode.Compressed);
                    result["archive"] = FileArtifact(new FileInfo(Path.Combine(change.Directory, "published.zal21"))); result["archive"]["mediaType"] = "application/octet-stream"; break;
            }
            return result;
        }

        private void ApplyLibraryFolderOperation(LibraryChange change)
        {
            string kind = change.Payload.Value<string>("folderKind"), action = change.Payload.Value<string>("action"), path = change.Payload.Value<string>("folderPath") ?? "/" + kind, name = change.Payload.Value<string>("name");
            if (kind == "types") { var folder = ResolveLibraryTypeFolder(change.Source, path); if (action == "create") folder.Folders.Create(name); else if (action == "rename") ((LibraryTypeUserFolder)folder).Name = name; else ((LibraryTypeUserFolder)folder).Delete(); }
            else { var folder = ResolveLibraryMasterFolder(change.Source, path); if (action == "create") folder.Folders.Create(name); else if (action == "rename") ((MasterCopyUserFolder)folder).Name = name; else ((MasterCopyUserFolder)folder).Delete(); }
        }

        private static void LibraryJournal(LibraryChange change, string stage, string state)
        {
            change.Journal["state"] = change.State; change.Journal["updatedUtc"] = DateTime.UtcNow;
            ((JArray)change.Journal["events"]).Add(new JObject { ["stage"] = stage, ["state"] = state, ["utc"] = DateTime.UtcNow });
            WriteLibraryRecord(Path.Combine(change.Directory, "journal.json"), change.Journal);
        }

        private static void WriteLibraryRecord(string path, JObject record)
        {
            LibraryJournalStore.Write(path, record);
        }
        private static JObject LibraryChangeView(LibraryChange change) => new JObject { ["schemaVersion"] = 1, ["changeId"] = change.Id, ["previewHash"] = change.PreviewHash, ["state"] = change.State, ["preview"] = change.Preview.DeepClone(), ["journal"] = change.Journal.DeepClone(), ["journalPath"] = Path.Combine(change.Directory, "journal.json"), ["automaticRetryAllowed"] = false };

        private JObject ReadLibraryJournal(JObject args)
        {
            string id = ChangeText(args, "changeId", 64);
            if (!id.StartsWith("lib-", StringComparison.Ordinal) || !Guid.TryParseExact(id.Substring(4), "N", out _)) throw new BridgeException("InvalidRequest", "Malformed library changeId.");
            string path = Path.Combine(ArtifactDirectory(args), "change-" + id, "journal.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) throw new BridgeException("ChangeNotFound", "Durable library journal absent or oversized.");
            try { return LibraryJournalStore.Read(path, id); }
            catch (InvalidDataException error) { throw new BridgeException("PreviewMismatch", error.Message); }
        }

        private JObject LibraryChangeStatus(JObject args)
        {
            if (PlcComponentQualificationGate.IsId(args.Value<string>("changeId"))) return ComponentQualificationStatus(args);
            string id = ChangeText(args, "changeId", 64);
            if (_libraryChanges.TryGetValue(id, out var change)) return LibraryChangeView(change);
            var journal = ReadLibraryJournal(args); string state = journal.Value<string>("state");
            return new JObject { ["schemaVersion"] = 1, ["changeId"] = id, ["state"] = LibraryJournalStore.ReconciledState(journal), ["journal"] = journal,
                ["recoveredFromDisk"] = true, ["automaticRetryAllowed"] = false, ["note"] = "The last dispatch may have completed before a worker failure. Recovery retrieves checkpoints into new locations and never replays a mutation." };
        }

        private JObject LibraryChangeRestore(JObject args)
        {
            EnsureAttached();
            var journal = ReadLibraryJournal(args);
            string originalJournalPath = Path.Combine(ArtifactDirectory(args), "change-" + journal.Value<string>("changeId"), "journal.json");
            if (journal.Value<string>("previewHash") != ChangeText(args, "previewHash", 64)) throw new BridgeException("PreviewMismatch", "Recovery previewHash mismatch.");
            var attempts = journal["recoveryAttempts"] as JArray;
            if (attempts != null && attempts.Count != 0)
            {
                var previous = attempts[attempts.Count - 1];
                if (previous.Value<string>("state") == "completed") return new JObject { ["schemaVersion"] = 1, ["changeId"] = journal["changeId"], ["state"] = "restored_to_new_locations", ["resources"] = previous["resources"].DeepClone(), ["idempotentReadback"] = true, ["automaticRetryAllowed"] = false };
                throw new BridgeException("OutcomeUnknown", "An earlier recovery attempt may have retrieved some resources. Inspect its recorded destinations; this endpoint never replays an uncertain recovery.", journal);
            }
            if (!_ownsPortal) throw new BridgeException("OwnedSessionRequired", "Recovery retrieves into an explicitly started owned TIA instance; user projects are never closed by this operation.");
            var checkpoints = journal["checkpoints"] as JArray ?? new JArray();
            if (checkpoints.Count == 0) throw new BridgeException("CheckpointInvalid", "No completed archive checkpoint exists. New outputs are retained in the journal for inspection.", journal);
            RequireLibraryHandleCapacity(checkpoints.Count(x => x.Value<string>("kind") == "library"));
            if (checkpoints.Any(x => x.Value<string>("kind") == "project") && _portal.Projects.Count != 0) throw new BridgeException("ProjectIsolationRequired", "Project recovery requires an empty worker-owned instance. Detach and start a new instance; the current project is preserved.");
            foreach (var resource in checkpoints)
                if (!File.Exists(resource.Value<string>("archivePath")) || ChangeFileHash(resource.Value<string>("archivePath")) != resource.Value<string>("sha256")) throw new BridgeException("CheckpointInvalid", "A recovery archive is missing or changed; no retrieval was started.");
            var recovered = new JArray();
            var attempt = new JObject { ["attemptId"] = Guid.NewGuid().ToString("N"), ["startedUtc"] = DateTime.UtcNow, ["state"] = "dispatching", ["resources"] = recovered };
            attempts = new JArray(attempt); journal["recoveryAttempts"] = attempts;
            _libraryChanges.Remove(journal.Value<string>("changeId")); // Subsequent status observes the durable recovery record.
            WriteLibraryRecord(originalJournalPath, journal);
            foreach (var resource in checkpoints)
            {
                string directory = SharedMutationLibraryRecoveryDirectory(resource, ArtifactDirectory(args));
                var row = new JObject { ["kind"] = resource["kind"], ["destination"] = directory, ["archivePath"] = resource["archivePath"], ["archiveSha256"] = resource["sha256"], ["state"] = "dispatching", ["existedBefore"] = false }; recovered.Add(row);
                WriteLibraryRecord(originalJournalPath, journal);
                Directory.CreateDirectory(directory);
                // Persist intent separately before every retrieval; a failed recovery never overwrites originals.
                string recoveryJournal = Path.Combine(directory, "recovery-intent.json"); WriteLibraryRecord(recoveryJournal, row);
                try
                {
                    if (resource.Value<string>("kind") == "project") row["project"] = ProjectInfo(_portal.Projects.Retrieve(new FileInfo(resource.Value<string>("archivePath")), new DirectoryInfo(directory)));
                    else
                    {
                        var native = _portal.GlobalLibraries.Retrieve(new FileInfo(resource.Value<string>("archivePath")), new DirectoryInfo(directory), OpenMode.ReadWrite);
                        var entry = RegisterLibrary(native, null, native, true, true); entry.Directory = directory; row["library"] = LibraryView(entry);
                    }
                    row["retrieved"] = true; row["state"] = "completed"; row["completedUtc"] = DateTime.UtcNow; WriteLibraryRecord(recoveryJournal, row); WriteLibraryRecord(originalJournalPath, journal);
                }
                catch (Exception exception) { row["failure"] = exception.Message; row["state"] = "failed"; attempt["state"] = "failed"; WriteLibraryRecord(recoveryJournal, row); WriteLibraryRecord(originalJournalPath, journal); throw new BridgeException("RecoveryRequired", "Recovery retained partial results and original resources.", new JObject { ["resources"] = recovered }); }
            }
            attempt["state"] = "completed"; attempt["completedUtc"] = DateTime.UtcNow; journal["state"] = "restored_to_new_locations"; WriteLibraryRecord(originalJournalPath, journal);
            return new JObject { ["schemaVersion"] = 1, ["changeId"] = journal["changeId"], ["state"] = "restored_to_new_locations", ["resources"] = recovered, ["originalsPreserved"] = true, ["newOutputsRetained"] = journal["outputs"], ["automaticRetryAllowed"] = false };
        }
    }
}
