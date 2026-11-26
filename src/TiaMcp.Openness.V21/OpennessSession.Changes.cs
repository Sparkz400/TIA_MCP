using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Model;
using static TiaMcp.Openness.V21.ChangeArguments;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly Dictionary<string, EngineeringChange> _changes = new Dictionary<string, EngineeringChange>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _changeKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> HardwareWriteAttributes = new HashSet<string>(new[]
        {
            "Name", "Author", "Comment", "InformationText", "PlantDesignation", "LocationIdentifier", "AdditionalInformation",
            "ChannelActivation", "MeasuringType", "MeasuringRange", "InputDelay", "SubstituteValue", "DigitalInputDelay",
            // A PROFINET interface's InterfaceOperatingModes flags: "IoController, IoDevice" makes an S7-1500 CPU an I-device.
            "InterfaceOperatingMode"
        }, StringComparer.Ordinal);
        private static readonly HashSet<string> NetworkWriteAttributes = new HashSet<string>(new[]
        {
            "Address", "SubnetMask", "RouterAddress", "UseRouter", "UseIpProtocol", "IpProtocolSelection",
            "PnDeviceName", "PnDeviceNameAutoGeneration", "PnDeviceNameSetDirectly", "DhcpClientId"
        }, StringComparer.Ordinal);

        private JObject ChangePrepare(JObject args, CapturedStorageQualification capturedStorage = null)
        {
            var project = SelectProject(args);
            EnsureProjectOffline(project);
            RequireNoUnresolvedChange(project, null);
            EvictStalePreparedChanges();
            var kind = ChangeText(args, "kind", 64);
            var payload = args["payload"] as JObject ?? throw new BridgeException("InvalidRequest", "payload must be an object.");
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(ArtifactDirectory(args), "change-" + id);
            // Old preparations no worker holds can never be applied; remove them (see StaleChangeDirectories for what is kept).
            // Only the Host's own changes directory asks for it; coordinator workspaces read their prepared inner journals later.
            if (args.Value<bool?>("pruneStalePreparations") == true)
                StaleChangeDirectories.Sweep(ArtifactDirectory(args), new HashSet<string>(_changes.Keys, StringComparer.Ordinal), DateTime.UtcNow);
            Directory.CreateDirectory(directory);
            var change = new EngineeringChange
            {
                Id = id, Kind = kind, Payload = (JObject)payload.DeepClone(), Directory = directory,
                ProjectPath = project.Path.FullName, ProjectName = project.Name,
                Selector = new JObject { ["projectName"] = project.Name, ["plcPath"] = args["plcPath"]?.DeepClone(), ["scopePath"] = args["scopePath"]?.DeepClone() },
                State = "preparing", Journal = Path.Combine(directory, "journal.json"),
                PreparedProcessId = _processId, CapturedStorageQualification = capturedStorage, ReuseProjectContent = FingerprintReuse
            };
#if SAFETY_FPARAMETER_DISCOVERY
            if (kind == "safety_fparameter_set" && args.Value<bool?>("fParameterDiscovery") == true) change.Selector["fParameterDiscovery"] = true;
#endif
            if (string.IsNullOrWhiteSpace(change.Selector.Value<string>("plcPath")))
                change.Selector["plcPath"] = ObjectIdentity.PlcPath(payload.Value<string>("scopePath") ?? args.Value<string>("scopePath"))
                    ?? ObjectIdentity.PlcPath(payload.Value<string>("blockPath") ?? payload.Value<string>("typePath") ?? payload.Value<string>("tagTablePath") ?? payload.Value<string>("technologyPath")
                        ?? payload.Value<string>("unitPath") ?? payload.Value<string>("objectPath")
                        ?? (payload["items"] as JArray)?.OfType<JObject>().SelectMany(item => new[] { item["scopePath"], item["tagTablePath"], item["blockPath"], item["typePath"] })
                            .FirstOrDefault(path => path?.Type == JTokenType.String)?.Value<string>());
            try
            {
                using (SharedMutationExclusive("TIA MCP: prepare offline change preview"))
                {
                    change.Preview = PrepareEngineeringMutation(change, project);
                    if (change.Kind == TagTableImportRules.Kind) PrepareScopedTagImport(change);
                    change.BaseFingerprint = MeasureChange(change, "prepare-fingerprint", () => EngineeringFingerprint(change, project));
                }
                change.Preview["changeId"] = id;
                change.Preview["kind"] = kind;
                change.Preview["project"] = ProjectInfo(project);
                change.Preview["isModified"] = project.IsModified;
                change.Preview["payload"] = PreviewPayload(change);
                change.Preview["baseFingerprint"] = change.BaseFingerprint;
                change.Preview["fingerprintScope"] = "Project save state; hardware identities and network configuration; exposed software scopes, user-block identities/native fingerprints, type/tag-table metadata and selected mutation attributes. Protected blocks reached through native generated system groups use identity/modified/protection/consistency metadata instead of generated payload checksums; owning PLC safety signatures and runtime-group metadata are included. Unexposed vendor data and generated payload bytes are not covered.";
                if (change.Kind.StartsWith("opcua_", StringComparison.Ordinal)) change.Preview["fingerprintScope"] += " OPC UA kinds additionally cover each PLC's server interface group: user and SIMATIC interface names, enabled state and native modification time, reference namespaces, and role mappings with namespace permissions. Interface and namespace content is covered only through native modification times.";
                if (change.Kind == "hmi_classic_screen_edit") change.Preview["fingerprintScope"] = "Project path, last save time and unsaved-state flag, plus the declared native screen scope. PLC and hardware content is not fingerprinted. " + HmiPreservationRules.ClassicScreenExportNote;
                else if (IsCompileOnlyKind(change.Kind)) change.Preview["fingerprintScope"] = "Exact compiler target identity and project save state. Compiles current source without exporting inconsistent blocks; source content preservation is not claimed.";
                else if (change.Kind == TagTableImportRules.Kind) change.Preview["fingerprintScope"] = TagTableImportRules.Coverage;
                else if (IsHmiChangeKind(change.Kind)) change.Preview["fingerprintScope"] += " HMI engineering additionally requires a complete family-specific native content manifest across discovered project HMI targets; partial HMI export or explorer metadata hashes cannot authorize apply or recovery.";
                if (change.Kind.StartsWith("vci_", StringComparison.Ordinal)) change.Preview["fingerprintScope"] += " VCI kinds additionally cover every workspace group path, each workspace's root, language and library policy, and every mapping's native identity, directory, file name and format. Files below the workspace root are pinned separately by preview.vci.filesSha256 and rechecked before the checkpoint.";
                if (change.Kind.StartsWith("certificate_", StringComparison.Ordinal)) change.Preview["fingerprintScope"] += " Certificate kinds additionally cover the selected local store (certificate Id, subject common name, validity end, private-key presence flag) and every certificate reference read on its device. Certificate bytes and key material are not fingerprinted.";
                if (IsPlcAlarmKind(change.Kind)) change.Preview["fingerprintScope"] += " PLC alarm imports additionally hash a fresh native export of their artifact type for every PLC/software-unit owner, or the project; XLSX document properties are excluded and a nondeterministic export fails closed as StaleRevision.";
                if (change.Kind == "opcua_server_settings_set") change.Preview["fingerprintScope"] += " OPC UA server settings additionally cover every device item's pinned OPC UA server attribute values (server, port, automatic certificate acceptance, purchased license, alarms and conditions).";
                if (change.Kind == "webserver_page_set") change.Preview["fingerprintScope"] += " Web page settings additionally cover every web-capable CPU's WebserverActivate value, system web page flag and web application names, types and default flags.";
                if (change.Kind == "drive_parameter_set" || change.Kind == "drive_telegram_set" || change.Kind == "drive_motor_configure") change.Preview["fingerprintScope"] += " Drive kinds additionally cover the selected drive object's identity (hardware path, driveId, drive object count of its container, order number, firmware and parameter count) and the listed parameter values, the drive object's telegram rows or the data set's motor configuration entries.";
                if (change.Kind == "umac_role_set") change.Preview["fingerprintScope"] += " UMAC role changes additionally cover all custom and system roles with engineering and device function rights, and the role assignments and activation state of project, anonymous and UMC principals.";
                change.Preview["fingerprintCoverage"] = change.FingerprintCoverage?.DeepClone();
                change.Preview["targetScopePath"] = change.ScopePath;
                change.Preview["targetGroupPath"] = change.GroupPath;
                change.Preview["targetNamespace"] = change.Namespace;
                change.Preview["checkpoint"] = "Applying requires checkpointSave=true: current in-memory project edits are saved before a compressed recovery archive is created. No mutation proceeds without that archive.";
                change.Preview["compileAndSave"] = "Apply explicitly chooses compile and save. Save-after-change requires a successful compile. Compile does not validate machine behavior.";
                if (UsesCompileBaseline(change))
                    change.Preview["compileAndSave"] = "Apply explicitly chooses compile and save. With compile=true the same scope compiles before and after the mutation; the change fails on an introduced or worsened error, a changed compile scope or incomplete diagnostics (tag-table imports also fail on new or changed warnings of the imported tables). Unchanged findings may remain and are listed in compile.baselineComparison, as are warnings of other objects that the compile merely reports again (revealed); inspect compilerSucceeded. Compile does not validate machine behavior.";
                change.Preview["backups"] = change.Backups;
                if (args["intent"] is JObject intent)
                {
                    // #220: recorded and hash-bound with the preview; compared, never evaluated.
                    var recorded = IntentRules.Normalize(intent);
                    IntentRules.Compare(recorded, EarlierDecisions(args, change.ProjectPath));
                    if (recorded["conflicts"].HasValues)
                        ((JArray)change.Preview["warnings"]).Add("Confirmed intent conflicts with a decision of an earlier applied change in this project; review intent.conflicts before applying.");
                    change.Preview["intent"] = recorded;
                }
                else if (args["intent"] != null && args["intent"].Type != JTokenType.Null) throw new BridgeException("InvalidRequest", "intent must be an object.");
                change.PreviewHash = ChangeHash(change.Preview.ToString(Formatting.None));
                change.State = "prepared";
                WriteChangeJournal(change);
                _changes.Add(id, change);
                return ChangeView(change);
            }
            catch (Exception ex)
            {
                change.State = "prepare_failed";
                change.Failure = ex.Message;
                change.PrimaryFailure = ex.Message;
                WriteChangeJournal(change);
                // Nothing reads a failed preparation's inputs or backups; only its journal is kept.
                foreach (var pattern in new[] { "before-*.xml", "requested*" })
                    foreach (var file in Directory.EnumerateFiles(directory, pattern))
                        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        private JObject ChangeApply(JObject args)
        {
            var change = GetEngineeringChange(args);
            var key = ChangeText(args, "idempotencyKey", 128);
            if (!string.Equals(ChangeText(args, "previewHash", 64), change.PreviewHash, StringComparison.Ordinal))
                throw new BridgeException("PreviewMismatch", "previewHash does not match the prepared content.");
            string existing;
            if (_changeKeys.TryGetValue(key, out existing) && existing != change.Id)
                throw new BridgeException("IdempotencyConflict", "This idempotency key belongs to another change.");
            if (change.IdempotencyKey != null)
            {
                if (change.IdempotencyKey != key) throw new BridgeException("IdempotencyConflict", "The change already has a different idempotency key.");
                if (ChangeMilestoneRules.IsResolvedOutcome(change.State)) return ChangeView(change);
                throw new BridgeException("RecoveryRequired", "This change was already attempted and cannot be retried. Inspect its journal and recover explicitly.", ChangeView(change));
            }
            if (change.State != "prepared") throw new BridgeException("InvalidChangeState", "Only a prepared change can be applied.");
            RequireChangeBool(args, "checkpointSave");
            bool compile = ExplicitChangeBool(args, "compile");
            bool save = ExplicitChangeBool(args, "save");
            if (!compile && IsCompileOnlyKind(change.Kind)) throw new BridgeException("InvalidRequest", "Compilation changes require compile=true.");
            if (!compile && change.Kind == "graphical_typed_edit") throw new BridgeException("InvalidRequest", "Typed graphical edits always require native compilation.");
            if (!compile && change.Kind == "source_upsert" && change.Payload.Value<string>("language")?.ToUpperInvariant() == "SIMATIC_SD") throw new BridgeException("InvalidRequest", "Native graphical document imports require compilation.");
            if (save && !compile) throw new BridgeException("InvalidRequest", "Saving applied changes requires compile=true.");
            if (IsHmiChangeKind(change.Kind) && (!compile || !save))
                throw new BridgeException("InvalidRequest", "HMI engineering requires compile=true and save=true; native readback and preservation remain mandatory.");
            if (change.Kind == "db_snapshot_load_actual" && (compile || save))
                throw new BridgeException("InvalidRequest", "Actual-value loads change no project content; use compile=false and save=false so the applied PLCSIM route is never saved.");
            if (change.Kind == "simulation_download_online" && (compile || save))
                throw new BridgeException("InvalidRequest", "The online download compiles the PLC itself before downloading; use compile=false and save=false so the applied PLCSIM route is never saved.");
            if (!compile && change.Kind == "to_create" && change.Payload["parameters"] != null)
                throw new BridgeException("InvalidRequest", "Technology creation with initial parameters requires compile=true.");
            if (!compile && change.Kind == "to_create" && change.Preview.Value<bool?>("firstMotionObject") == true)
                throw new BridgeException("CompileRequired", "The PLC's first motion technology object adds the know-how protected MC_Servo and MC_Interpolator OBs, which stay inconsistent without a compile, " +
                    "so the change could be neither fingerprinted nor restored in place. Apply it with compile=true; an unconnected real axis or encoder does not compile, so create a compilable one first " +
                    "(on an S7-1500, a TO_PositioningAxis with parameters {\"VirtualAxis.Mode\": 1}). Nothing was written.", new JObject { ["typeIdentifier"] = change.Payload["typeIdentifier"].DeepClone() });
            if (!compile && (change.Kind == "vci_synchronize" || change.Kind == "vci_conflict_resolve") && change.Payload.Value<string>("direction") == "WorkspaceToProject")
                throw new BridgeException("InvalidRequest", "Project-ward VCI synchronization changes PLC software and requires compile=true.");
            // network_configure kept its optional compile when it joined the hardware rules; remote-I/O fixtures defer it.
            if (!compile && ((HardwareEngineeringRules.Applies(change.Kind) && change.Kind != "network_configure") || IsPlcAlarmKind(change.Kind) || new[] { "block_rename", "block_delete", "to_delete", "hardware_delete", "type_rename", "type_delete", "tag_set", "tag_delete", "constant_set", "constant_delete", "tag_create", "constant_create", "block_number_set", "block_attributes_set", "instance_db_create", "group_create", "group_rename", "group_delete", "tag_table_create", "tag_table_rename", "tag_table_delete", "graphical_network_patch", "graphical_network_insert", "call_parameter_set", "call_connection_set", "hardware_address_set", "drive_telegram_set", "connection_create", "connection_delete", "connection_set", "graph_import", "to_connect", "to_disconnect",
                "software_unit_create", "software_unit_relation_set", "software_unit_publication_set", "software_unit_delete", "software_unit_rename", "software_unit_create_from", "webserver_page_set", "webserver_user_pages_generate", "umac_role_set" }.Concat(BlockProtectionKinds).Contains(change.Kind) || change.Kind.StartsWith("opcua_", StringComparison.Ordinal)))
                throw new BridgeException("InvalidRequest", "This engineering operation requires compile=true.");
            if (change.Kind == "cax_import") RequireCaxImportRoute();
            var project = ProjectForChange(change);
            EnsureProjectOffline(project);
            using (SharedMutationExclusive("TIA MCP: checkpoint and apply " + change.Kind))
            {
                string currentFingerprint = MeasureChange(change, "stale-revision-fingerprint", () => EngineeringFingerprint(change, project));
                if (currentFingerprint != change.BaseFingerprint)
                    throw new BridgeException("StaleRevision", "Project or affected objects changed since preparation. Prepare a new preview; no write was applied.",
                        new JObject { ["changeId"] = change.Id, ["preparedFingerprint"] = change.BaseFingerprint, ["currentFingerprint"] = currentFingerprint,
                            ["remediation"] = "Call change_prepare again with the same payload; the new preview is bound to the current state." });
                var compileOnlyPlc = change.Kind == "plc_compile" ? SelectPlc(change.Selector) : null;
                if (compileOnlyPlc != null) PlcCompilation.Prepare(change.Payload, change.Selector.Value<string>("plcPath"), compileOnlyPlc.Path, compileOnlyPlc.Software.GetService<ICompilable>());
                else if (IsCompileOnlyKind(change.Kind)) CompileOnlyScope(change, project);
                ValidatePreparedFiles(change);
                ValidateTypedDependencies(change);
                if (change.Kind.StartsWith("vci_", StringComparison.Ordinal)) VciRequireUnchangedFiles(change, project);
                if (change.Kind == "webserver_user_pages_generate") WebSourceUnchanged(change);
                // An unopenable credential must refuse before the checkpoint save, not after it.
                if (change.Preview["credentials"] != null) RequireRoutedCredentials(change);
                EngineeringPaths.ValidateProjectArchivePath(change.Directory, "before.zap21");
                // Another change may have failed after this one was prepared.
                RequireNoUnresolvedChange(project, change.Id);
                change.IdempotencyKey = key;
                _changeKeys[key] = change.Id;
                change.State = "checkpointing";
                // From here the worker saves and changes the project itself; the kept project content is dropped first.
                _projectContent.Clear();
                try
                {
                    // Inside the try: a failed journal write must still end in a resolvable state.
                    WriteChangeJournal(change);
                    InvalidateHmiObservations();
                    MeasureChange(change, "checkpoint-save", () => { project.Save(); return true; });
                    change.Stages.Add("prechange_project_saved");
                    change.ArchivePath = Path.Combine(change.Directory, "before.zap21");
                    MeasureChange(change, "checkpoint-archive", () => { project.Archive(new DirectoryInfo(change.Directory), "before.zap21", ProjectArchivationMode.Compressed); return true; });
                    if (!File.Exists(change.ArchivePath)) throw new BridgeException("CheckpointFailed", "TIA did not produce the required recovery archive.");
                    change.ArchiveHash = ChangeFileHash(change.ArchivePath);
                    change.Stages.Add("recovery_archive_created");
                    change.State = "applying";
                    WriteChangeJournal(change);
                    if (compile && UsesCompileBaseline(change))
                    {
                        // The same scope compiles before and after, so unchanged unrelated findings do not fail the change (#221).
                        change.CompileBaseline = CompileEngineeringChange(change, project);
                        if (change.Kind == TagTableImportRules.Kind) change.TagTableEvidence["baselineCompile"] = change.CompileBaseline;
                        change.Stages.Add(change.Kind == TagTableImportRules.Kind ? "tag_table_baseline_compile_finished" : "baseline_compile_finished");
                        WriteChangeJournal(change);
                        // Without complete baseline diagnostics only a clean compile can pass; the tag import refuses outright.
                        if (change.Kind == TagTableImportRules.Kind && !CompilationEvidenceRules.IsCompleted(change.CompileBaseline))
                            throw new BridgeException("CompileEvidenceIncomplete", "The baseline compiler did not return complete diagnostics. No tag import was dispatched.");
                    }
                    if (!IsCompileOnlyKind(change.Kind))
                    {
                        long attributeReadbacks = System.Threading.Interlocked.Read(ref _attributeReadbacks);
                        ApplyEngineeringMutation(change, project);
                        change.Stages.Add("mutation_applied");
                        if (System.Threading.Interlocked.Read(ref _attributeReadbacks) != attributeReadbacks) change.Stages.Add("attribute_readback_verified");
                        // Durable stage boundaries tell reconciliation how far an interrupted apply got.
                        WriteChangeJournal(change);
                    }
                    else change.Stages.Add("compile_only_requested");
                    CaptureTechnologyEvidence(change);
                    if (compile)
                    {
                        change.Compilation = CompileEngineeringChange(change, project);
                        change.Stages.Add("compile_finished");
                        if (compileOnlyPlc != null)
                        {
                            var after = SelectPlc(change.Selector);
                            if (after.Path != compileOnlyPlc.Path || !Equals(after.Software, compileOnlyPlc.Software))
                                throw new BridgeException("CompileScopeChanged", "The selected PLC changed during compilation.");
                        }
                        // Set before the verdict so a failed outcome still reports the compiler result apart from the change state.
                        if (change.Compilation != null)
                        {
                            change.Compilation["compilerSucceeded"] = CompilationEvidenceRules.IsSuccessful(change.Compilation);
                            change.Compilation["unresolvedSymbols"] = UnresolvedSymbolRules.From(change.Compilation);
                            if (change.CompileBaseline != null) change.Compilation["baselineComparison"] = CompileBaselineRules.Compare(change.CompileBaseline, change.Compilation, change.BlockNames.Concat(change.ObjectNames));
                        }
                        // A text-list import or an admitted HMI kind must leave the compiler problems as the compile before the write found them;
                        // a Classic screen edit or tag-table import may also resolve some (HmiEngineeringRules.AllowsResolvedCompileProblems),
                        // and a screen edit that removes the last error may reveal warnings outside its screens (ClassicScreenCompileRules.NoNewProblems).
                        bool resolvedAllowed = HmiEngineeringRules.AllowsResolvedCompileProblems(change.Kind, change.HmiBeforeManifest);
                        JArray unmaskedWarnings = null;
                        bool noNewHmiProblems = resolvedAllowed && ClassicScreenCompileRules.NoNewProblems(change.HmiEvidence["baselineCompile"] as JObject, change.Compilation,
                            change.Kind == "hmi_classic_screen_edit" ? ClassicScreenCompileRules.EditedScreenNames(change.Payload) : null, out unmaskedWarnings);
                        if (noNewHmiProblems && unmaskedWarnings.Count > 0) change.Compilation["unmaskedWarnings"] = unmaskedWarnings;
                        if (resolvedAllowed ? !noNewHmiProblems
                            : change.Kind == "hmi_text_list_import" || HmiEngineeringRules.IsAdmittedKind(change.Kind) ? !HmiTextListImportRules.CompileProblemsUnchanged(change.HmiEvidence["baselineCompile"] as JObject, change.Compilation)
                            : change.Kind == TagTableImportRules.Kind ? change.Compilation?["baselineComparison"]?.Value<bool>("verified") != true
                            : UsesCompileBaseline(change) ? change.Compilation == null || !CompileBaselineRules.Accepts(change.CompileBaseline, change.Compilation, (JObject)change.Compilation["baselineComparison"])
                            : !CompilationEvidenceRules.IsCompleted(change.Compilation))
                        {
                            string unavailable = change.Kind == "hmi_classic_screen_edit" ? ClassicScreenCompileRules.FinalCompileUnavailable(change.Compilation) : null;
                            if (unavailable != null) change.Compilation["compilerUnavailable"] = true;
                            throw new BridgeException(change.Compilation?.Value<int?>("errorCount") == 0 ? "CompileDiagnosticRegression" : "CompileFailed",
                                unavailable ?? "The change failed compiler verification: errors, new/changed warnings, or incomplete diagnostics. "
                                + (change.Kind == "hmi_classic_screen_edit" ? "Verified screen rollback is attempted; inspect the retained outcome." : "Inspect the retained outcome."));
                        }
                    }
                    if (IsHmiChangeKind(change.Kind)) VerifyHmiEngineeringPreservation(change, project);
                    if (change.Kind == "software_unit_create_from") RequireUnrenamedUnitMembers(change);
                    VerifyHardwarePreservation(change, project);
                    if (change.Kind == "source_upsert" && compile) CompareSourceRefresh(change);
                    if (change.Kind == TagTableImportRules.Kind) VerifyScopedTagImport(change);
                    if (new[] { "graphical_typed_edit", "graphical_operand_set", "graphical_network_patch", "graphical_network_insert", "call_parameter_set", "call_connection_set", "graph_import" }.Contains(change.Kind))
                    {
                        // A batch reads back every item's block against that item's own requested XML.
                        var targets = change.BatchMembers ?? new List<EngineeringChange> { change };
                        for (int index = 0; index < targets.Count; index++)
                        {
                            var target = targets[index];
                            var plc = SelectPlc(target.Selector);
                            string readback = Path.Combine(target.Directory, "after-structured-edit.xml");
                            FindBlockByName(plc, target.BlockNames[0], true, target).Item.Export(new FileInfo(readback), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                            var row = new JObject { ["role"] = "structured-edit-readback", ["artifact"] = ChangeArtifact(readback) };
                            if (change.BatchMembers != null) row["index"] = index;
                            change.Readbacks.Add(row);
                            try { EngineeringSources.RequireNativePreservation(EngineeringSources.ReadXml(File.ReadAllText(target.InputPath)), EngineeringSources.ReadXml(File.ReadAllText(readback))); }
                            catch (BridgeException error) when (change.BatchMembers != null) { throw new BridgeException(error.Code, "items[" + index + "]: " + error.Message, error.Details); }
                        }
                        change.Stages.Add("native_preservation_verified");
                    }
                    if (change.Kind == "graphical_typed_edit") VerifyTypedInstanceReadbacks(change);
                    if (change.Kind == "block_import_xml" && compile) ReadBackImportedBlocks(change);
                    if (save) { InvalidateHmiObservations(); MeasureChange(change, "changed-project-save", () => { project.Save(); return true; }); change.Stages.Add("changed_project_saved"); WriteChangeJournal(change); }
                    if (change.Kind == "hmi_classic_screen_edit") VerifyHmiEngineeringPreservation(change, project, "after-save");
                    if (change.Kind == TagTableImportRules.Kind && save) VerifyScopedTagImport(change);
                    // A generated block can be present right after generation and gone after compile/save (R_TRIG/F_TRIG instance DBs, native probe w9p).
                    if (change.Kind == "source_upsert") { VerifyExpectedBlocksExist(change, SelectPlc(change.Selector)); change.Stages.Add("source_blocks_verified_after_save"); }
                    if (change.Kind == "certificate_create") { VerifyCreatedCertificateAfterSave(change, project); change.Stages.Add("certificate_verified_after_save"); }
                    if (change.Kind == "instance_db_create") { VerifyInstanceDb(change, SelectPlc(change.Selector)); change.Stages.Add("instance_db_verified_after_save"); }
                    // A batch is read back item by item again after compile and save.
                    if (change.TagBatchTablePaths != null) { VerifyTagBatch(change, save ? "after-save" : "after-compile"); change.Stages.Add("tag_batch_verified_after_" + (save ? "save" : "compile")); }
                    if (change.Kind == "safety_import_xml" || change.Kind == "plc_compile" || change.Kind == "safety_fparameter_set" || change.Kind == "safety_drive_tags" || change.Preview?["safetyTagRename"] != null) change.SafetyAfter = ReadSafety(SelectPlc(change.Selector).Software);
                    change.AfterFingerprint = change.Kind == "hmi_classic_screen_edit"
                        ? ClassicScreenCapturedFingerprint(change, project, change.HmiEvidence["after-save"] as JObject)
                        : MeasureChange(change, "after-fingerprint", () => EngineeringFingerprint(change, project));
                    change.State = "applied";
                    WriteChangeJournal(change);
                }
                catch (Exception ex)
                {
                    // Until the state leaves checkpointing only the project save and the archive ran; nothing was mutated.
                    bool mutationReached = change.State != "checkpointing";
                    change.State = mutationReached ? "recovery_required" : "not_applied";
                    change.Failure = ex.Message;
                    change.PrimaryFailure = ex.Message;
                    // The failure's own evidence (for example the rows a preservation check found changed) is journaled with the change.
                    if ((ex as BridgeException)?.Details is JObject details) change.Readbacks.Add(new JObject { ["role"] = "failure-details", ["details"] = details.DeepClone() });
                    else change.Readbacks.Add(new JObject { ["role"] = "native-exception", ["chain"] = NativeFailureText.Chain(ex) });
                    CaptureTechnologyEvidence(change);
                    if (mutationReached) RecoverFailedClassicScreen(change, project);
                    if (change.State != "rolled_back" && change.State != "not_applied")
                        try { change.AfterFingerprint = EngineeringFingerprint(change, project); }
                        // The primary failure is reported either way; the journal names why no after-fingerprint was recorded.
                        catch (Exception fingerprint) { change.Readbacks.Add(new JObject { ["role"] = "after-fingerprint-failure", ["chain"] = NativeFailureText.Chain(fingerprint) }); }
                    else { change.AttributeTarget = null; change.TagBatchTargets = null; change.BatchMembers = null; }
                    change.Stages.Add("change_finished_with_failure");
                    WriteChangeJournal(change);
                    throw new BridgeException((ex as BridgeException)?.Code ?? "EngineeringError", ex.Message, ChangeView(change));
                }
            }
            // The decision log records the applied change but is not part of it: a failed append must not undo "applied".
            // A read-only log, or a directory in its place, raises UnauthorizedAccessException rather than IOException.
            try { RecordAppliedDecisions(change); }
            catch (Exception ex) when (IntentRules.IsLogWriteFailure(ex))
            {
                change.Readbacks.Add(new JObject { ["role"] = "intent-log-write-failure", ["message"] = ex.Message });
                try { WriteChangeJournal(change); } catch (Exception journal) when (IntentRules.IsLogWriteFailure(journal)) { /* The applied journal is already durable. */ }
            }
            return ChangeView(change);
        }

        private JObject ChangeStatus(JObject args)
        {
            string id = ChangeText(args, "changeId", 64);
            if (_changes.TryGetValue(id, out var change)) return ChangeView(change, args.Value<bool?>("includeDetails") == true);
            if (!Guid.TryParseExact(id, "N", out _)) throw new BridgeException("InvalidRequest", "Persisted changeId must be a 32-character GUID.");
            string directory = ChangeText(args, "artifactDirectory", 4096);
            if (!Path.IsPathRooted(directory)) throw new BridgeException("InvalidRequest", "artifactDirectory must be absolute.");
            string journal = Path.Combine(Path.GetFullPath(directory), "change-" + id, "journal.json");
            if (!File.Exists(journal)) throw new BridgeException("ChangeNotFound", "No saved journal exists for this changeId.");
            if (new FileInfo(journal).Length > 64 * 1024 * 1024) throw new BridgeException("ResponseTooLarge", "Journal exceeds 64 MiB.");
            var data = JObject.Parse(File.ReadAllText(journal));
            if (data.Value<string>("kind") != "hmi_classic_screen_edit" && new FileInfo(journal).Length > 8 * 1024 * 1024)
                throw new BridgeException("ResponseTooLarge", "Non-screen journal exceeds 8 MiB.");
            var result = Envelope();
            result["changeId"] = id; result["journal"] = data; result["journalPath"] = journal;
            result["recoveredFromDisk"] = true; result["automaticRetryAllowed"] = false;
            result["note"] = "Journal records the last durable observation. A crashed applying/checkpointing state has an uncertain outcome and must not be replayed.";
            return data.Value<string>("kind") == "hmi_classic_screen_edit" && args.Value<bool?>("includeDetails") != true ? ClassicScreenReceipts.Compact(result) : result;
        }

        private JObject ChangeRestore(JObject args)
        {
            var change = GetEngineeringChange(args);
            if (ChangeMilestoneRules.RestoreAlreadyDone(change.State, ChangeText(args, "previewHash", 64), change.PreviewHash)) return ChangeView(change);
            // A scoped fingerprint never authorizes replacing the whole project; only the imported tables are restored.
            if (change.Kind == TagTableImportRules.Kind) return RestoreScopedTagImport(change, args);
            if (change.FingerprintCoverage?["inconsistentBlocksMetadataOnly"] is JArray bystanders && bystanders.Count > 0)
                throw new BridgeException("ScopedRecoveryRequired", "Unrelated inconsistent blocks were covered by metadata only, so this change cannot authorize replacing the whole project. Use the retained archive for explicit manual recovery; nothing was saved or closed.",
                    new JObject { ["inconsistentBlocksMetadataOnly"] = bystanders.DeepClone(), ["recoveryArchive"] = change.ArchivePath });
            ChangeMilestoneRules.RequireRestorable(change.State, _ownsPortal,
                () => !string.IsNullOrEmpty(change.ArchivePath) && File.Exists(change.ArchivePath) && ChangeFileHash(change.ArchivePath) == change.ArchiveHash, () => ChangeView(change));
            var project = ProjectForChange(change);
            EnsureProjectOffline(project);
            if (change.AfterFingerprint == null || EngineeringFingerprint(change, project) != change.AfterFingerprint)
                throw new BridgeException("StaleRevision", "Current state differs from the recorded outcome; automatic recovery would discard unreviewed edits.");
            // TIA limits the retrieved project directory to 143 characters. Check the
            // complete projected path before saving or closing the current project.
            string afterName = EngineeringPaths.NewRecoveryArchiveName(change.Directory);
            var recoveryDirectory = SharedMutationPlannedDestination("projectRecovery", () => EngineeringPaths.NewRecoveryDirectory(ArtifactDirectory(args), project.Name));
            Directory.CreateDirectory(recoveryDirectory);
            bool wasScratchProject = _scratchProject != null && project.Equals(_scratchProject);
            change.RecoveryDirectory = recoveryDirectory;
            _projectContent.Clear();
            change.State = "restoring";
            WriteChangeJournal(change);
            try
            {
                // Preserve the failed/applied state before closing only our owned project.
                using (SharedMutationExclusive("TIA MCP: preserve current state for recovery"))
                {
                    if (EngineeringFingerprint(change, project) != change.AfterFingerprint)
                        throw new BridgeException("StaleRevision", "Project changed while recovery was acquiring exclusive access; no project was closed.");
                    project.Save();
                    change.Stages.Add("pre_recovery_project_saved");
                    project.Archive(new DirectoryInfo(change.Directory), afterName, ProjectArchivationMode.Compressed);
                    change.RecoveryPreviousArchive = Path.Combine(change.Directory, afterName);
                    change.Stages.Add("pre_recovery_archive_created");
                    WriteChangeJournal(change);
                }
                project.Close();
                _scratchProject = null;
                change.Stages.Add("previous_project_closed");
                WriteChangeJournal(change);
                var restored = _portal.Projects.Retrieve(new FileInfo(change.ArchivePath), new DirectoryInfo(recoveryDirectory));
                if (wasScratchProject) _scratchProject = restored;
                change.RecoveredProject = ProjectInfo(restored);
                if (OpcUaPayloadFields.ContainsKey(change.Kind)) _opcUaRoleCacheProcess = _processId;
                if (IsHmiChangeKind(change.Kind)) VerifyHmiEngineeringRecovery(change, restored);
                VerifyHardwareRecovery(change, restored);
                change.State = "restored";
                change.AttributeTarget = null;
                change.TagBatchTargets = null;
                change.BatchMembers = null;
                change.Stages.Add("checkpoint_retrieved_to_new_directory");
                WriteChangeJournal(change);
                return ChangeView(change);
            }
            catch (Exception ex)
            {
                change.State = "recovery_required";
                change.RecoveryFailure = "Archive recovery did not complete: " + ex.Message;
                WriteChangeJournal(change);
                throw new BridgeException("RecoveryRequired", change.RecoveryFailure, ChangeView(change));
            }
        }

        private static readonly string[] PersistedTextFields = { "xml", "source" };
        // Source/XML persisted as the prepared input file appears in the preview by length and hash only; the hash binds the preview to the content.
        private static JObject PreviewPayload(EngineeringChange change)
        {
            var payload = (JObject)change.Payload.DeepClone();
            // A Classic screen reconciliation re-reads its exact request from the journal preview.
            if (change.InputPath == null || change.Kind == "hmi_classic_screen_edit") return payload;
            foreach (var name in PersistedTextFields)
                if (payload[name]?.Type == JTokenType.String)
                {
                    var bytes = Encoding.UTF8.GetBytes(payload.Value<string>(name));
                    using (var sha = SHA256.Create())
                        payload[name] = new JObject { ["bytes"] = bytes.Length, ["sha256"] = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
                }
            return payload;
        }

        // A prepared change nobody applied would otherwise live until detach. Past the TTL or the count the worker drops it; its journal stays
        // on disk, and applying it then answers ChangeNotFound, as after a detach.
        private void EvictStalePreparedChanges()
        {
            foreach (var id in PreparedChangeEviction.Select(_changes.Values.Select(change => (change.Id, change.State, change.PreparedUtc)), DateTime.UtcNow, PreparedChangeEviction.Ttl, PreparedChangeEviction.MaxPrepared))
                _changes.Remove(id);
            foreach (var key in _changeKeys.Where(p => !_changes.ContainsKey(p.Value)).Select(p => p.Key).ToList()) _changeKeys.Remove(key);
        }

        // Called on detach. Unresolved changes stay: if the same TIA process is attached again they still block a checkpoint save.
        // The rest stay readable from their journals through change_status.
        private void ReleaseResolvedChanges()
        {
            foreach (var id in _changes.Where(p => !ChangeMilestoneRules.IsUnresolved(p.Value.State)).Select(p => p.Key).ToList()) _changes.Remove(id);
            foreach (var key in _changeKeys.Where(p => !_changes.ContainsKey(p.Value)).Select(p => p.Key).ToList()) _changeKeys.Remove(key);
        }

        private EngineeringChange GetEngineeringChange(JObject args)
        {
            string id = ChangeText(args, "changeId", 64);
            if (!_changes.TryGetValue(id, out var change)) throw new BridgeException("ChangeNotFound", "Unknown changeId in this worker session. Saved journal files remain available after disconnect.");
            return change;
        }

        // A checkpoint save would persist an unresolved change's unsaved edits and make it unrestorable. Only the TIA process
        // that prepared it can still hold them; a new process starts from the saved files.
        private void RequireNoUnresolvedChange(Project project, string exceptId)
        {
            var unresolved = _changes.Values.FirstOrDefault(x => x.Id != exceptId && x.PreparedProcessId == _processId &&
                string.Equals(x.ProjectPath, project.Path.FullName, StringComparison.OrdinalIgnoreCase) &&
                ChangeMilestoneRules.IsUnresolved(x.State));
            if (unresolved != null)
                throw new BridgeException("RecoveryRequired", "Change " + unresolved.Id + " on this project is " + unresolved.State +
                    ". Inspect change_status and the retained recovery evidence before another edit. For recovery_required, change_restore can recover the archive in a worker-owned instance. Do not restart merely to clear an unverified change.", ChangeView(unresolved));
        }

        private Project ProjectForChange(EngineeringChange change)
        {
            ChangeMilestoneRules.RequireSameAttachment(change.PreparedProcessId, _processId);
            var project = SelectProject(change.Selector);
            ChangeMilestoneRules.RequireSameProject(project.Path.FullName, change.ProjectPath);
            return project;
        }

        private static void ValidatePreparedFiles(EngineeringChange change)
        {
            foreach (var member in change.HmiScreenMembers) ValidatePreparedFiles(member);
            // Batch items keep their own requested files (structured edits); each must be unchanged since its preview.
            if (change.BatchMembers != null) foreach (var member in change.BatchMembers) ValidatePreparedFiles(member);
            if (change.InputPath != null && (!File.Exists(change.InputPath) || ChangeFileHash(change.InputPath) != change.InputHash))
                throw new BridgeException("ArtifactChanged", "Prepared import/source artifact changed after preview.");
            if (change.TagTableEvidence?["before"] is JArray tables)
                foreach (var table in tables)
                {
                    var artifact = table["artifact"];
                    string file = artifact.Value<string>("path");
                    if (!File.Exists(file) || ChangeFileHash(file) != artifact.Value<string>("sha256"))
                        throw new BridgeException("ArtifactChanged", "Captured tag-table preservation evidence changed after preview.");
                }
            // A text-list import reads its TextLibrary file next to the configuration file.
            foreach (var file in change.Backups.OfType<JObject>().Where(b => b.Value<string>("role") == "hmi-text-list-candidate-library").Select(b => (JObject)b["artifact"]))
                if (!File.Exists(file.Value<string>("path")) || ChangeFileHash(file.Value<string>("path")) != file.Value<string>("sha256"))
                    throw new BridgeException("ArtifactChanged", "Prepared TextLibrary candidate changed after preview.");
        }

        private JObject ChangeView(EngineeringChange change, bool includeDetails = false)
        {
            var view = Envelope();
            view["changeId"] = change.Id; view["state"] = change.State; view["previewHash"] = change.PreviewHash;
            view["preview"] = change.Preview?.DeepClone(); view["journalPath"] = change.Journal;
            view["stages"] = change.Stages.DeepClone(); view["compile"] = change.Compilation?.DeepClone();
            view["failure"] = change.Failure; view["afterFingerprint"] = change.AfterFingerprint;
            view["fingerprintCoverage"] = change.FingerprintCoverage?.DeepClone();
            view["primaryFailure"] = change.PrimaryFailure; view["recoveryFailure"] = change.RecoveryFailure;
            view["recoveryDirectory"] = change.RecoveryDirectory;
            view["recoveryArchive"] = change.ArchivePath;
            view["recoveryArchiveSha256"] = change.ArchiveHash;
            view["previousStateArchive"] = change.RecoveryPreviousArchive;
            view["recoveredProject"] = change.RecoveredProject?.DeepClone();
            view["safetyAfter"] = change.SafetyAfter?.DeepClone();
            view["technologyAfter"] = change.TechnologyAfter?.DeepClone();
            view["readbacks"] = change.Readbacks.DeepClone();
            view["hmiEvidence"] = change.HmiEvidence?.DeepClone();
            if (change.TagTableEvidence != null) view["tagTableEvidence"] = change.TagTableEvidence.DeepClone();
            if (change.CompileBaseline != null) view["compileBaseline"] = change.CompileBaseline.DeepClone();
            if (change.Kind == "graphical_typed_edit") view["typedQualification"] = TypedChangeQualification(change);
            view["scratchSecurity"] = change.ScratchSecurity?.DeepClone();
            view["automaticRetryAllowed"] = false;
            view["phaseTimings"] = change.PhaseTimings.DeepClone();
            view["fingerprintContent"] = FingerprintContentView(change);
            view["progressPath"] = Path.Combine(change.Directory, "progress.json");
            view["progress"] = ChangeMilestoneRules.From(change.State, change.Stages, change.Compilation);
            view["projectSave"] = ProjectSaveView(change, (JObject)view["progress"]);
            return change.Kind == "hmi_classic_screen_edit" && !includeDetails ? ClassicScreenReceipts.Compact(view) : view;
        }

        // preview.isModified is fixed at change_prepare (the preview is hash-bound); this says what apply saved since.
        // Read from the progress milestones (checkpointSaved, saved), so every kind's save stages count the same way there and here.
        private static JObject ProjectSaveView(EngineeringChange change, JObject progress)
        {
            string Milestone(string name) => (progress?["milestones"] as JArray)?.OfType<JObject>().FirstOrDefault(m => m.Value<string>("milestone") == name)?.Value<string>("status");
            return new JObject {
                ["isModifiedAtPrepare"] = change.Preview?["isModified"]?.DeepClone(),
                ["savedBeforeApply"] = Milestone("checkpointSaved") == "done",
                ["savedAfterApply"] = Milestone("saved") == "done",
                ["note"] = "preview.isModified is the state at change_prepare. savedBeforeApply is the checkpointSave of change_apply, which writes the in-memory edits before its recovery archive; savedAfterApply is its final save." };
        }

        // How often this change's fingerprints read the whole project and how often they reused the last read of the same saved state.
        private static JObject FingerprintContentView(EngineeringChange change) => new JObject { ["fullReads"] = change.ProjectContentReads, ["reusedForSavedState"] = change.ProjectContentReuses };

        private static string DecisionLog(string changesDirectory) => Path.Combine(changesDirectory, "intent-decisions.jsonl");

        private IEnumerable<JObject> EarlierDecisions(JObject args, string projectPath)
        {
            string log = DecisionLog(ArtifactDirectory(args));
            if (!File.Exists(log)) return Enumerable.Empty<JObject>();
            return IntentRules.DecisionsOf(File.ReadAllLines(log), projectPath);
        }

        // Only decisions of applied changes become the project's confirmed record.
        private static void RecordAppliedDecisions(EngineeringChange change)
        {
            if (!(change.Preview?["intent"]?["decisions"] is JArray decisions) || decisions.Count == 0) return;
            File.AppendAllLines(DecisionLog(Path.GetDirectoryName(change.Directory)), decisions.OfType<JObject>().Select(d => IntentRules.Line(change.ProjectPath, change.Id, d)));
        }

        private static void WriteChangeJournal(EngineeringChange change)
        {
            var data = new JObject { ["changeId"] = change.Id, ["kind"] = change.Kind, ["state"] = change.State,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("O"), ["projectPath"] = change.ProjectPath,
                ["previewHash"] = change.PreviewHash, ["baseFingerprint"] = change.BaseFingerprint, ["afterFingerprint"] = change.AfterFingerprint,
                ["fingerprintCoverage"] = change.FingerprintCoverage?.DeepClone(),
                ["preview"] = change.Preview?.DeepClone(), ["idempotencyKey"] = change.IdempotencyKey,
                ["recoveryArchive"] = change.ArchivePath, ["recoveryArchiveSha256"] = change.ArchiveHash,
                ["previousStateArchive"] = change.RecoveryPreviousArchive, ["stages"] = change.Stages.DeepClone(),
                ["readbacks"] = change.Readbacks.DeepClone(),
                ["hmiEvidence"] = change.HmiEvidence?.DeepClone(),
                ["tagTableEvidence"] = change.TagTableEvidence?.DeepClone(),
                ["compileBaseline"] = change.CompileBaseline?.DeepClone(),
                ["typedQualification"] = change.Kind == "graphical_typed_edit" ? TypedChangeQualification(change) : null,
                ["compile"] = change.Compilation?.DeepClone(), ["failure"] = change.Failure,
                ["primaryFailure"] = change.PrimaryFailure, ["recoveryFailure"] = change.RecoveryFailure,
                ["recoveryDirectory"] = change.RecoveryDirectory, ["recoveredProject"] = change.RecoveredProject?.DeepClone(),
                ["scratchSecurity"] = change.ScratchSecurity?.DeepClone(), ["safetyAfter"] = change.SafetyAfter?.DeepClone(), ["technologyAfter"] = change.TechnologyAfter?.DeepClone() };
            var temporary = change.Journal + ".new";
            data["phaseTimings"] = change.PhaseTimings.DeepClone();
            data["fingerprintContent"] = FingerprintContentView(change);
            File.WriteAllText(temporary, data.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(change.Journal)) TiaMcp.Core.JournalFileReplacement.Replace(temporary, change.Journal); else File.Move(temporary, change.Journal);
            // A completed phase can precede the terminal state transition. Refresh
            // diagnostic progress after every journal commit, so progress.json carries the current state, terminal states included.
            try { SaveChangeProgress(change); } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { /* The durable journal remains authoritative. */ }
        }

        private sealed class EngineeringChange
        {
            internal readonly JArray PhaseTimings = new JArray();
            internal readonly List<EngineeringChange> HmiScreenMembers = new List<EngineeringChange>();
            internal bool HmiScreenTouched;
            internal readonly JArray TypedDependencies = new JArray();
            internal readonly JArray TypedInstanceCreations = new JArray();
            internal CapturedStorageQualification CapturedStorageQualification;
            internal JArray Readbacks = new JArray();
            internal string Id, Kind, Directory, Journal, State, ProjectPath, ProjectName, PreviewHash, BaseFingerprint, AfterFingerprint;
            internal string InputPath, InputHash, ArchivePath, ArchiveHash, IdempotencyKey, Failure, RecoveryPreviousArchive;
            internal string PrimaryFailure, RecoveryFailure, RecoveryDirectory, ScopePath, GroupPath, Namespace;
            internal int? PreparedProcessId;
            internal DateTime PreparedUtc = DateTime.UtcNow;
            internal JObject Selector, Payload, Preview, Compilation, RecoveredProject, SafetyAfter, ScratchSecurity, TechnologyAfter, FingerprintCoverage;
            internal JObject HmiEvidence, HmiBeforeManifest, TagTableEvidence, CompileBaseline;
            internal JArray HardwareBeforeManifest;
            internal string[] BlockNames = Array.Empty<string>();
            internal string[] ObjectNames = Array.Empty<string>();
            internal readonly JArray Backups = new JArray();
            internal readonly JArray Stages = new JArray();
            internal IEngineeringObject AttributeTarget;
            // tag_set with payload.items: the resolved table path per item, and the tags whose requested attributes the fingerprint covers.
            // Set for changes prepared in this session; other fingerprint callers (library changes, reconciliation replays) always read in full.
            internal bool ReuseProjectContent;
            internal int ProjectContentReads, ProjectContentReuses;
            internal string[] TagBatchTablePaths;
            internal List<TagBatchTarget> TagBatchTargets;
            // payload.items on the kinds of ChangeBatchRules: one prepared member change per item.
            internal List<EngineeringChange> BatchMembers;
            // A batch item prepared again at apply, only for its checks: it writes no backup export.
            internal bool PreflightOnly;
            // block_number_set: the block kind and number the change takes, which no other item of a batch may take.
            internal string BlockNumberKey;
        }
    }
}
