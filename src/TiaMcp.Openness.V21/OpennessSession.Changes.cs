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

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly Dictionary<string, EngineeringChange> _changes = new Dictionary<string, EngineeringChange>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _changeKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> HardwareWriteAttributes = new HashSet<string>(new[]
        {
            "Name", "Author", "Comment", "InformationText", "PlantDesignation", "LocationIdentifier", "AdditionalInformation",
            "ChannelActivation", "MeasuringType", "MeasuringRange", "InputDelay", "SubstituteValue", "DigitalInputDelay"
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
            var kind = ChangeText(args, "kind", 64);
            var payload = args["payload"] as JObject ?? throw new BridgeException("InvalidRequest", "payload must be an object.");
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(ArtifactDirectory(args), "change-" + id);
            Directory.CreateDirectory(directory);
            var change = new EngineeringChange
            {
                Id = id, Kind = kind, Payload = (JObject)payload.DeepClone(), Directory = directory,
                ProjectPath = project.Path.FullName, ProjectName = project.Name,
                Selector = new JObject { ["projectName"] = project.Name, ["plcPath"] = args["plcPath"]?.DeepClone(), ["scopePath"] = args["scopePath"]?.DeepClone() },
                State = "preparing", Journal = Path.Combine(directory, "journal.json"),
                PreparedProcessId = _processId, CapturedStorageQualification = capturedStorage
            };
            if (string.IsNullOrWhiteSpace(change.Selector.Value<string>("plcPath")))
                change.Selector["plcPath"] = ObjectIdentity.PlcPath(payload.Value<string>("scopePath") ?? args.Value<string>("scopePath"))
                    ?? ObjectIdentity.PlcPath(payload.Value<string>("blockPath") ?? payload.Value<string>("typePath") ?? payload.Value<string>("tagTablePath") ?? payload.Value<string>("technologyPath"));
            try
            {
                using (SharedMutationExclusive("TIA MCP: prepare offline change preview"))
                {
                    change.Preview = PrepareEngineeringMutation(change, project);
                    change.BaseFingerprint = EngineeringFingerprint(change, project);
                }
                change.Preview["changeId"] = id;
                change.Preview["kind"] = kind;
                change.Preview["project"] = ProjectInfo(project);
                change.Preview["payload"] = change.Payload.DeepClone();
                change.Preview["baseFingerprint"] = change.BaseFingerprint;
                change.Preview["fingerprintScope"] = "Project save state; hardware identities and network configuration; exposed software scopes, user-block identities/native fingerprints, type/tag-table metadata and selected mutation attributes. Protected blocks reached through native generated system groups use identity/modified/protection/consistency metadata instead of generated payload checksums; owning PLC safety signatures and runtime-group metadata are included. Unexposed vendor data and generated payload bytes are not covered.";
                if (change.Kind == "hmi_engineering") change.Preview["fingerprintScope"] += " HMI engineering additionally requires a complete family-specific native content manifest across discovered project HMI targets; partial HMI export or explorer metadata hashes cannot authorize apply or recovery.";
                change.Preview["fingerprintCoverage"] = change.FingerprintCoverage?.DeepClone();
                change.Preview["targetScopePath"] = change.ScopePath;
                change.Preview["targetGroupPath"] = change.GroupPath;
                change.Preview["targetNamespace"] = change.Namespace;
                change.Preview["checkpoint"] = "Applying requires checkpointSave=true: current in-memory project edits are saved before a compressed recovery archive is created. No mutation proceeds without that archive.";
                change.Preview["compileAndSave"] = "Apply explicitly chooses compile and save. Save-after-change requires a successful compile. Compile does not validate machine behavior.";
                change.Preview["backups"] = change.Backups;
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
                if (change.State == "applied") return ChangeView(change);
                throw new BridgeException("RecoveryRequired", "This change was already attempted and cannot be retried. Inspect its journal and recover explicitly.", ChangeView(change));
            }
            if (change.State != "prepared") throw new BridgeException("InvalidChangeState", "Only a prepared change can be applied.");
            RequireChangeBool(args, "checkpointSave");
            bool compile = ExplicitChangeBool(args, "compile");
            bool save = ExplicitChangeBool(args, "save");
            if (!compile && change.Kind == "plc_compile") throw new BridgeException("InvalidRequest", "PLC compilation requires compile=true.");
            if (!compile && change.Kind == "graphical_typed_edit") throw new BridgeException("InvalidRequest", "Typed graphical edits always require native compilation.");
            if (!compile && change.Kind == "source_upsert" && change.Payload.Value<string>("language")?.ToUpperInvariant() == "SIMATIC_SD") throw new BridgeException("InvalidRequest", "Native graphical document imports require compilation.");
            if (save && !compile) throw new BridgeException("InvalidRequest", "Saving applied changes requires compile=true.");
            if (change.Kind == "hmi_engineering" && (!compile || !save))
                throw new BridgeException("InvalidRequest", "HMI engineering requires compile=true and save=true; native readback and preservation remain mandatory.");
            if (!compile && change.Kind == "to_create" && change.Payload["parameters"] != null)
                throw new BridgeException("InvalidRequest", "Technology creation with initial parameters requires compile=true.");
            if (!compile && (HardwareEngineeringRules.Applies(change.Kind) || new[] { "type_rename", "type_delete", "tag_set", "tag_delete", "constant_set", "constant_delete", "group_create", "group_rename", "group_delete", "graphical_network_patch", "graphical_network_insert", "call_parameter_set", "call_connection_set", "hardware_address_set" }.Contains(change.Kind)))
                throw new BridgeException("InvalidRequest", "This engineering operation requires compile=true.");
            var project = ProjectForChange(change);
            EnsureProjectOffline(project);
            using (SharedMutationExclusive("TIA MCP: checkpoint and apply " + change.Kind))
            {
                if (EngineeringFingerprint(change, project) != change.BaseFingerprint)
                    throw new BridgeException("StaleRevision", "Project or affected objects changed since preparation. Prepare a new preview; no write was applied.");
                var compileOnlyPlc = change.Kind == "plc_compile" ? SelectPlc(change.Selector) : null;
                if (compileOnlyPlc != null) PlcCompilation.Prepare(change.Payload, change.Selector.Value<string>("plcPath"), compileOnlyPlc.Path, compileOnlyPlc.Software.GetService<ICompilable>());
                ValidatePreparedFiles(change);
                ValidateTypedDependencies(change);
                EngineeringPaths.ValidateProjectArchivePath(change.Directory, "before.zap21");
                change.IdempotencyKey = key;
                _changeKeys[key] = change.Id;
                change.State = "checkpointing";
                WriteChangeJournal(change);
                try
                {
                    project.Save();
                    change.Stages.Add("prechange_project_saved");
                    change.ArchivePath = Path.Combine(change.Directory, "before.zap21");
                    project.Archive(new DirectoryInfo(change.Directory), "before.zap21", ProjectArchivationMode.Compressed);
                    if (!File.Exists(change.ArchivePath)) throw new BridgeException("CheckpointFailed", "TIA did not produce the required recovery archive.");
                    change.ArchiveHash = ChangeFileHash(change.ArchivePath);
                    change.Stages.Add("recovery_archive_created");
                    change.State = "applying";
                    WriteChangeJournal(change);
                    if (compileOnlyPlc == null)
                    {
                        ApplyEngineeringMutation(change, project);
                        change.Stages.Add("mutation_applied");
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
                        if (!CompilationEvidenceRules.IsSuccessful(change.Compilation))
                            throw new BridgeException("CompileFailed", "The change was applied but compilation failed or its diagnostic evidence is incomplete. Recovery archive and changed project state are retained.");
                    }
                    if (change.Kind == "hmi_engineering") VerifyHmiEngineeringPreservation(change, project);
                    VerifyHardwarePreservation(change, project);
                    if (new[] { "graphical_typed_edit", "graphical_operand_set", "graphical_network_patch", "graphical_network_insert", "call_parameter_set", "call_connection_set" }.Contains(change.Kind))
                    {
                        var plc = SelectPlc(change.Selector);
                        string readback = Path.Combine(change.Directory, "after-structured-edit.xml");
                        FindBlockByName(plc, change.BlockNames[0], true, change).Item.Export(new FileInfo(readback), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                        change.Readbacks.Add(new JObject { ["role"] = "structured-edit-readback", ["artifact"] = ChangeArtifact(readback) });
                        EngineeringSources.RequireNativePreservation(EngineeringSources.ReadXml(File.ReadAllText(change.InputPath)), EngineeringSources.ReadXml(File.ReadAllText(readback)));
                        change.Stages.Add("native_preservation_verified");
                    }
                    if (change.Kind == "graphical_typed_edit") VerifyTypedInstanceReadbacks(change);
                    if (save) { project.Save(); change.Stages.Add("changed_project_saved"); }
                    if (change.Kind == "safety_import_xml" || change.Kind == "plc_compile") change.SafetyAfter = ReadSafety(SelectPlc(change.Selector).Software);
                    change.AfterFingerprint = EngineeringFingerprint(change, project);
                    change.State = "applied";
                    WriteChangeJournal(change);
                    return ChangeView(change);
                }
                catch (Exception ex)
                {
                    change.State = "recovery_required";
                    change.Failure = ex.Message;
                    change.PrimaryFailure = ex.Message;
                    CaptureTechnologyEvidence(change);
                    try { change.AfterFingerprint = EngineeringFingerprint(change, project); } catch { }
                    WriteChangeJournal(change);
                    throw new BridgeException((ex as BridgeException)?.Code ?? "EngineeringError", ex.Message, ChangeView(change));
                }
            }
        }

        private JObject ChangeStatus(JObject args)
        {
            string id = ChangeText(args, "changeId", 64);
            if (_changes.TryGetValue(id, out var change)) return ChangeView(change);
            if (!Guid.TryParseExact(id, "N", out _)) throw new BridgeException("InvalidRequest", "Persisted changeId must be a 32-character GUID.");
            string directory = ChangeText(args, "artifactDirectory", 4096);
            if (!Path.IsPathRooted(directory)) throw new BridgeException("InvalidRequest", "artifactDirectory must be absolute.");
            string journal = Path.Combine(Path.GetFullPath(directory), "change-" + id, "journal.json");
            if (!File.Exists(journal)) throw new BridgeException("ChangeNotFound", "No saved journal exists for this changeId.");
            if (new FileInfo(journal).Length > 8 * 1024 * 1024) throw new BridgeException("ResponseTooLarge", "Journal exceeds 8 MiB.");
            var data = JObject.Parse(File.ReadAllText(journal));
            var result = Envelope();
            result["changeId"] = id; result["journal"] = data; result["journalPath"] = journal;
            result["recoveredFromDisk"] = true; result["automaticRetryAllowed"] = false;
            result["note"] = "Journal records the last durable observation. A crashed applying/checkpointing state has an uncertain outcome and must not be replayed.";
            return result;
        }

        private JObject ChangeRestore(JObject args)
        {
            var change = GetEngineeringChange(args);
            if (change.State == "restored") return ChangeView(change);
            if (!_ownsPortal) throw new BridgeException("OwnedSessionRequired", "Automatic archive retrieval is limited to worker-owned TIA instances. The checkpoint archive is available for manual recovery in a user-owned instance.", ChangeView(change));
            if (change.State != "applied" && change.State != "recovery_required") throw new BridgeException("InvalidChangeState", "This change has no applied state to recover.");
            if (string.IsNullOrEmpty(change.ArchivePath) || !File.Exists(change.ArchivePath) || ChangeFileHash(change.ArchivePath) != change.ArchiveHash)
                throw new BridgeException("CheckpointInvalid", "The recorded recovery archive is missing or its hash has changed.");
            if (ChangeText(args, "previewHash", 64) != change.PreviewHash) throw new BridgeException("PreviewMismatch", "Wrong previewHash.");
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
                if (change.Kind == "hmi_engineering") VerifyHmiEngineeringRecovery(change, restored);
                VerifyHardwareRecovery(change, restored);
                change.State = "restored";
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

        private JObject PrepareEngineeringMutation(EngineeringChange change, Project project)
        {
            if (change.Kind == "hmi_engineering") return PrepareHmiEngineeringMutation(change, project);
            var payload = change.Payload;
            var preview = new JObject { ["effects"] = new JArray(), ["warnings"] = new JArray() };
            var effects = (JArray)preview["effects"];
            switch (change.Kind)
            {
                case "plc_compile":
                {
                    var plc = SelectPlc(change.Selector);
                    if (change.Selector["scopePath"] != null && change.Selector["scopePath"].Type != JTokenType.Null)
                        throw new BridgeException("InvalidRequest", "PLC compilation covers software of one PLC; scopePath is not supported.");
                    effects.Add(PlcCompilation.Prepare(payload, change.Selector.Value<string>("plcPath"), plc.Path, plc.Software.GetService<ICompilable>()));
                    ((JArray)preview["warnings"]).Add("Compilation may change generated state. Apply saves the current project and creates a recovery archive before dispatch.");
                    break;
                }
                case "graphical_typed_edit":
                    PrepareTypedGraphical(change, preview);
                    break;
                case "project_simulation_enable":
                {
                    bool enabled = ExplicitChangeBool(payload, "enabled");
                    var settings = project.GetService<PlcSimulationSettingsProvider>() ?? throw new BridgeException("UnsupportedCapability", "Project has no PlcSimulationSettingsProvider service.");
                    effects.Add(new JObject { ["projectSetting"] = "IsSimulationDuringBlockCompilationEnabled",
                        ["before"] = settings.IsSimulationDuringBlockCompilationEnabled, ["after"] = enabled });
                    ((JArray)preview["warnings"]).Add("This setting affects subsequent project block compilation. It does not start a simulator or download a program.");
                    break;
                }
                case "source_upsert":
                {
                    var plc = SelectPlc(change.Selector);
                    string language = ChangeText(payload, "language", 16).ToUpperInvariant();
                    if (language != "SCL" && language != "STL" && language != "AWL" && language != "SIMATIC_SD") throw new BridgeException("UnsupportedLanguage", "Source writes accept SCL, STL/AWL or explicit new-block SIMATIC_SD documents.");
                    string source = ChangeText(payload, "source", 2 * 1024 * 1024);
                    string declarationSource = language == "SIMATIC_SD" ? EngineeringSources.SimaticSdDeclarationEnvelope(source) : source;
                    var declared = EngineeringSources.DeclaredBlocks(declarationSource);
                    if (language == "SIMATIC_SD" && declared.Length != 1)
                        throw new BridgeException("UnsupportedSourceDocument", "The native SIMATIC SD route accepts exactly one explicit new block per document; multi-block documents can import partially.");
                    var expected = ChangeNames(payload, "expectedBlockNames");
                    if (!new HashSet<string>(declared, StringComparer.Ordinal).SetEquals(expected))
                        throw new BridgeException("SourceScopeMismatch", "Source block declarations must exactly match expectedBlockNames.", new JObject { ["declared"] = new JArray(declared), ["expected"] = new JArray(expected) });
                    change.BlockNames = expected;
                    change.Namespace = EngineeringSources.DeclaredNamespace(declarationSource, false);
                    PrepareMutationScope(change, plc, "block", change.BlockNames);
                    PrepareBlockTargets(change, plc, false, false, effects);
                    string nativeLanguage = language == "AWL" ? "STL" : language;
                    foreach (var name in expected)
                    {
                        var old = FindBlockByName(plc, name, false, change);
                        if (language == "SIMATIC_SD" && old != null)
                            throw new BridgeException("TargetAlreadyExists", "SIMATIC_SD source import is limited to explicitly new blocks; use typed edits for existing graphical blocks.");
                        if (old != null && !(old.Item is DataBlock) && old.Item.ProgrammingLanguage.ToString() != nativeLanguage)
                            throw new BridgeException("LanguageConversionNotSupported", "Source update must preserve the existing language of " + name + ". Use a dedicated conversion workflow if conversion is intended.");
                    }
                    change.InputPath = Path.Combine(change.Directory, language == "SIMATIC_SD" ? "requested.s7dcl" : language == "SCL" ? "requested.scl" : "requested.awl");
                    File.WriteAllText(change.InputPath, source, new UTF8Encoding(true));
                    change.InputHash = ChangeFileHash(change.InputPath);
                    preview["language"] = language;
                    break;
                }
                case "block_import_xml":
                case "safety_import_xml":
                {
                    var plc = SelectPlc(change.Selector);
                    string xml = ChangeText(payload, "xml", 2 * 1024 * 1024);
                    var document = EngineeringSources.ReadXml(xml);
                    var targets = EngineeringSources.BlockDeclarations(document);
                    var expected = ChangeNames(payload, "expectedBlockNames");
                    if (!new HashSet<string>(targets.Select(t => t.Name), StringComparer.Ordinal).SetEquals(expected)) throw new BridgeException("XmlScopeMismatch", "Top-level XML blocks must exactly match expectedBlockNames.");
                    bool safety = change.Kind == "safety_import_xml";
                    if (targets.Any(target => target.IsSafety != safety)) throw new BridgeException("SafetyBoundary", "Standard and safety imports use separate workflows, and cannot be mixed or converted.");
                    if (safety && targets.Any(target => target.Language != "F_LAD" && target.Language != "F_FBD" && target.Language != "F_DB"))
                        throw new BridgeException("UnsupportedSafetyBlock", "Only user F-LAD, F-FBD and F-DB XML is accepted; F-system and generated call languages are excluded.");
                    if (safety) { RequireChangeBool(payload, "safetyAcknowledged"); EnsureSafetyWritable(plc.Software); preview["safetyBefore"] = ReadSafety(plc.Software); }
                    change.BlockNames = expected;
                    change.Namespace = ImportNamespace(document);
                    PrepareMutationScope(change, plc, "block", change.BlockNames);
                    PrepareBlockTargets(change, plc, safety, false, effects);
                    foreach (var target in targets)
                    {
                        var old = FindBlockByName(plc, target.Name, false, change);
                        if (old != null && old.Item.ProgrammingLanguage.ToString() != target.Language)
                            throw new BridgeException("LanguageConversionNotSupported", "XML update must retain the existing block language: " + target.Name);
                    }
                    ValidateXmlBlockNumbers(document, plc, change);
                    change.InputPath = Path.Combine(change.Directory, "requested.xml");
                    File.WriteAllText(change.InputPath, xml, new UTF8Encoding(true));
                    change.InputHash = ChangeFileHash(change.InputPath);
                    break;
                }
                case "graphical_operand_set":
                case "graphical_network_patch":
                case "graphical_network_insert":
                case "call_parameter_set":
                case "call_connection_set":
                {
                    var plc = SelectPlc(change.Selector);
                    string name = MutationBlockName(payload, plc);
                    change.BlockNames = new[] { name };
                    PrepareMutationScope(change, plc, "block", change.BlockNames);
                    var block = FindBlockByName(plc, name, true, change);
                    string language = block.Item.ProgrammingLanguage.ToString();
                    if (language != "LAD" && language != "FBD" && !(change.Kind == "call_parameter_set" && language == "SCL")) throw new BridgeException("UnsupportedLanguage", "Graphical editing requires standard LAD/FBD; call_parameter_set also supports exported SCL calls.");
                    change.BlockNames = new[] { name };
                    PrepareBlockTargets(change, plc, false, true, effects);
                    string backup = change.Backups[0]["artifact"].Value<string>("path");
                    var document = EngineeringSources.ReadXml(File.ReadAllText(backup));
                    try
                    {
                        if (change.Kind == "graphical_network_insert")
                            preview["insertedNetworkId"] = EngineeringSources.InsertGraphicalNetwork(document, payload.Value<string>("afterNetworkId"), ChangeText(payload, "compileUnitXml", 1024 * 1024));
                        else
                        {
                            string networkId = ChangeText(payload, "networkId", 32);
                            if (change.Kind == "graphical_operand_set")
                                EngineeringSources.SetGraphicalOperand(document, networkId, ChangeText(payload, "accessUid", 32), ChangeStringArray(payload, "components", 128, 128));
                            else if (change.Kind == "graphical_network_patch")
                                EngineeringSources.PatchGraphicalNetwork(document, networkId, ChangeStringArray(payload, "upsertXml", 500, 1024 * 1024, true), ChangeStringArray(payload, "removeUids", 500, 32, true));
                            else if (change.Kind == "call_parameter_set")
                                EngineeringSources.SetCallParameterOperand(document, networkId, ChangeText(payload, "callUid", 32), ChangeText(payload, "parameterName", 128), ChangeStringArray(payload, "components", 128, 128));
                            else EngineeringSources.SetGraphicalCallConnection(document, networkId, ChangeText(payload, "callUid", 32), ChangeText(payload, "parameterName", 128), ChangeText(payload, "targetAccessUid", 32));
                        }
                    }
                    catch (ArgumentException ex) { throw new BridgeException("InvalidGraphicalEdit", ex.Message); }
                    catch (InvalidOperationException ex) { throw new BridgeException("UnsupportedGraphicalEdit", ex.Message); }
                    preview["edit"] = payload.DeepClone();
                    EngineeringSources.BlockDeclarations(document);
                    change.InputPath = Path.Combine(change.Directory, "requested.xml");
                    File.WriteAllText(change.InputPath, document.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(true));
                    change.InputHash = ChangeFileHash(change.InputPath);
                    ((JArray)preview["warnings"]).Add("Only explicitly selected XML nodes change in the prepared artifact. Untouched interfaces, comments, networks and unknown elements are preserved structurally. Native import/compilation remains authoritative for instruction and pin compatibility.");
                    break;
                }
                case "type_rename":
                case "type_delete":
                    PrepareTypeEdit(change, preview);
                    break;
                case "tag_set":
                case "tag_delete":
                case "constant_set":
                case "constant_delete":
                    PrepareTagEdit(change, preview);
                    break;
                case "group_create":
                case "group_rename":
                case "group_delete":
                    PrepareGroupEdit(change, preview);
                    break;
                case "type_source_upsert":
                {
                    var plc = SelectPlc(change.Selector);
                    string source = ChangeText(payload, "source", 2 * 1024 * 1024);
                    change.ObjectNames = ChangeNames(payload, "expectedTypeNames");
                    change.Namespace = EngineeringSources.DeclaredNamespace(source, true);
                    if (!new HashSet<string>(EngineeringSources.DeclaredTypes(source), StringComparer.Ordinal).SetEquals(change.ObjectNames))
                        throw new BridgeException("SourceScopeMismatch", "TYPE declarations must exactly match expectedTypeNames.");
                    PrepareMutationScope(change, plc, "type", change.ObjectNames);
                    PrepareTypeTargets(change, plc, effects);
                    change.InputPath = Path.Combine(change.Directory, "requested.scl");
                    File.WriteAllText(change.InputPath, source, new UTF8Encoding(true));
                    change.InputHash = ChangeFileHash(change.InputPath);
                    break;
                }
                case "plc_type_import_xml":
                case "tag_table_import_xml":
                {
                    var plc = SelectPlc(change.Selector);
                    string xml = ChangeText(payload, "xml", 2 * 1024 * 1024);
                    bool types = change.Kind == "plc_type_import_xml";
                    change.ObjectNames = ChangeNames(payload, types ? "expectedTypeNames" : "expectedTableNames");
                    var document = EngineeringSources.ReadXml(xml);
                    string elementName = types ? "SW.Types.PlcStruct" : "SW.Tags.PlcTagTable";
                    var declarations = EngineeringSources.NamedXmlObjects(document, elementName);
                    change.Namespace = types ? ImportNamespace(document) : "";
                    if (!new HashSet<string>(declarations, StringComparer.Ordinal).SetEquals(change.ObjectNames)) throw new BridgeException("XmlScopeMismatch", "Imported object names must exactly match the expected set.");
                    PrepareMutationScope(change, plc, types ? "type" : "tagTable", change.ObjectNames);
                    if (types) PrepareTypeTargets(change, plc, effects);
                    else
                    {
                        var allTables = AllTagTables(ResolveSoftwareScope(plc, change.ScopePath).Tags).ToArray();
                        var importedSymbols = document.Descendants().Where(element => element.Name == "SW.Tags.PlcTag" || element.Name == "SW.Tags.PlcUserConstant")
                            .Select(element => element.Element("AttributeList")?.Element("Name")?.Value).ToArray();
                        if (importedSymbols.Any(string.IsNullOrWhiteSpace) || importedSymbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != importedSymbols.Length)
                            throw new BridgeException("TagScopeMismatch", "Every imported tag or user constant must have a unique explicit symbolic name.");
                        var incomingTags = new HashSet<string>(importedSymbols, StringComparer.OrdinalIgnoreCase);
                        foreach (var table in allTables)
                        {
                            if (!change.ObjectNames.Contains(table.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                if (table.Tags.Any(tag => incomingTags.Contains(tag.Name)) || table.UserConstants.Any(constant => incomingTags.Contains(constant.Name)) || table.SystemConstants.Any(constant => incomingTags.Contains(constant.Name)))
                                    throw new BridgeException("TagScopeMismatch", "Imported tag/constant symbols collide with symbols in an unpreviewed table: " + table.Name);
                                continue;
                            }
                            string path = Path.Combine(change.Directory, "before-tags-" + Guid.NewGuid().ToString("N") + ".xml");
                            table.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                            change.Backups.Add(new JObject { ["tableName"] = table.Name, ["artifact"] = ChangeArtifact(path) });
                        }
                        effects.Add(new JObject { ["importTagTables"] = new JArray(change.ObjectNames) });
                    }
                    change.InputPath = Path.Combine(change.Directory, "requested.xml");
                    File.WriteAllText(change.InputPath, xml, new UTF8Encoding(true));
                    change.InputHash = ChangeFileHash(change.InputPath);
                    break;
                }
                case "block_delete":
                case "block_rename":
                {
                    var plc = SelectPlc(change.Selector);
                    change.BlockNames = new[] { MutationBlockName(payload, plc) };
                    PrepareMutationScope(change, plc, "block", change.BlockNames);
                    PrepareBlockTargets(change, plc, false, true, effects);
                    if (change.Kind == "block_rename")
                    {
                        string name = ChangeText(payload, "newName", 128);
                        if (FindBlockByName(plc, name, false, change, false) != null) throw new BridgeException("ObjectExists", "A block in this software scope already has the requested newName.");
                        ValidateNamespaceOwnership(change, plc, "block", new[] { name });
                        effects.Add(new JObject { ["renameTo"] = name });
                    }
                    else
                    {
                        if (payload["dependencyPolicy"] == null) payload["dependencyPolicy"] = "rejectReferenced";
                        var target = FindBlockByName(plc, change.BlockNames[0], true, change);
                        preview["dependencies"] = CheckDeleteDependencies(change, plc, target.Item, target.Item.Name);
                    }
                    ((JArray)preview["warnings"]).Add("Callers, instance references and interfaces may be affected. Compile the complete PLC after applying.");
                    break;
                }
                case "hardware_add":
                {
                    string identifier = ChangeText(payload, "typeIdentifier", 256);
                    ValidateCatalogIdentifier(identifier);
                    string name = ChangeText(payload, "deviceName", 128);
                    if (EngineeringDevices(project).Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("ObjectExists", "Device name already exists.");
                    ChangeText(payload, "itemName", 128);
                    effects.Add(new JObject { ["createDevice"] = name, ["typeIdentifier"] = identifier });
                    if (payload.Value<bool?>("configureScratchSecurity") == true)
                    {
                        EnsureCurrentScratchProject(project);
                        effects.Add(new JObject { ["configureNewCpuSecurity"] = "Protect the new scratch CPU configuration and provision a new password-authenticated runtime user/role with standard full access scoped to that new device. Generated credentials are stored under Windows DPAPI CurrentUser. No existing protection or anonymous access is changed." });
                    }
                    break;
                }
                case "hardware_address_set":
                    PrepareHardwareAddress(change, project, preview);
                    break;
                case "hardware_move":
                    PrepareHardwareMove(change, project, preview);
                    break;
                case "hardware_channel_attributes":
                    PrepareHardwareChannel(change, project, preview);
                    break;
                case "hardware_cpu_memory_bytes":
                    PrepareCpuMemoryBytes(change, project, preview);
                    break;
                case "network_io_assign":
                    PrepareIoAssignment(change, project, preview, out _);
                    break;
                case "hardware_plug":
                {
                    var item = ChangeHardware(project, payload);
                    string identifier = ChangeText(payload, "typeIdentifier", 256);
                    ValidateCatalogIdentifier(identifier);
                    string name = ChangeText(payload, "name", 128);
                    int slot = payload.Value<int?>("positionNumber") ?? throw new BridgeException("InvalidRequest", "positionNumber is required.");
                    if (!item.Item.CanPlugNew(identifier, name, slot)) throw new BridgeException("IncompatibleHardware", "The catalog item cannot be plugged into the requested position.");
                    effects.Add(new JObject { ["parentPath"] = item.Path, ["createItem"] = name, ["positionNumber"] = slot, ["typeIdentifier"] = identifier });
                    break;
                }
                case "hardware_attributes":
                {
                    var item = ChangeHardware(project, payload);
                    change.AttributeTarget = item.Item;
                    preview["attributeChanges"] = PrepareAttributes(item.Item, ChangeObject(payload, "attributes"), HardwareWriteAttributes);
                    effects.Add(new JObject { ["hardwarePath"] = item.Path });
                    break;
                }
                case "hardware_delete":
                {
                    var item = ChangeHardware(project, payload);
                    EnsureHardwareDeleteEligible(item.Item);
                    effects.Add(new JObject { ["deleteHardwarePath"] = item.Path, ["typeIdentifier"] = item.Item.TypeIdentifier, ["positionNumber"] = item.Item.PositionNumber });
                    ((JArray)preview["warnings"]).Add("The selected module and its subitems are deleted. References, address maps and software dependencies require recompilation. Recovery uses the whole-project archive.");
                    break;
                }
                case "network_configure":
                {
                    var node = ChangeNode(project, payload);
                    change.AttributeTarget = node;
                    var attributes = payload["attributes"] as JObject ?? new JObject();
                    foreach (var name in new[] { "Address", "SubnetMask", "RouterAddress" })
                        if (attributes[name] != null && (!IPAddress.TryParse(attributes.Value<string>(name), out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
                            throw new BridgeException("InvalidRequest", name + " must be an IPv4 address.");
                    preview["attributeChanges"] = PrepareAttributes(node, attributes, NetworkWriteAttributes);
                    if (payload["subnetName"] != null) ChangeText(payload, "subnetName", 128);
                    if (payload.Value<bool?>("disconnect") == true && payload["subnetName"] != null) throw new BridgeException("InvalidRequest", "Choose subnetName or disconnect, not both.");
                    effects.Add(new JObject { ["nodeName"] = node.Name, ["connectedSubnetBefore"] = node.ConnectedSubnet?.Name,
                        ["connectedSubnetAfter"] = payload["subnetName"]?.DeepClone(), ["disconnect"] = payload.Value<bool?>("disconnect") ?? false });
                    break;
                }
                case "to_create":
                {
                    var plc = SelectPlc(change.Selector);
                    string name = ChangeText(payload, "name", 128);
                    if (payload["technologyPath"] != null) throw new BridgeException("InvalidRequest", "to_create creates in the PLC root technology group; technologyPath is only supported for existing objects.");
                    if (FindTechnologyEntry(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", new JObject { ["name"] = name }, false) != null) throw new BridgeException("ObjectExists", "Technology object already exists.");
                    ChangeText(payload, "typeIdentifier", 128);
                    if (!Version.TryParse(ChangeText(payload, "version", 32), out _)) throw new BridgeException("InvalidRequest", "version must be a dotted numeric version supported by this CPU.");
                    if (payload["parameters"] != null)
                    {
                        var initial = ChangeObject(payload, "parameters");
                        if (initial.Count > 500) throw new BridgeException("InvalidRequest", "Initial TO parameters are limited to 500 scalar values.");
                        foreach (var property in initial.Properties())
                        {
                            RejectSensitiveName(property.Name);
                            if (!(property.Value is JValue) || property.Value.Type == JTokenType.Null) throw new BridgeException("InvalidRequest", "Initial TO parameters must be non-null scalars.");
                        }
                        preview["initialParameters"] = initial.DeepClone();
                        ((JArray)preview["warnings"]).Add("Initial parameter names, native types, write access and dependencies can only be checked after native creation. They are applied within the same checkpoint before compilation; failure requires recovery.");
                    }
                    effects.Add(new JObject { ["createTechnologyObject"] = name, ["typeIdentifier"] = payload["typeIdentifier"], ["version"] = payload["version"] });
                    ((JArray)preview["warnings"]).Add("Type/version availability is validated by Siemens during creation; a successful create can still require parameter configuration before compile succeeds.");
                    break;
                }
                case "to_parameters":
                case "to_delete":
                {
                    var plc = SelectPlc(change.Selector);
                    var entry = BindTechnologySelection(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", payload);
                    var technology = entry.Item;
                    string beforePath = Path.Combine(change.Directory, "before-to.xml");
                    technology.Export(new FileInfo(beforePath), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    change.Backups.Add(ChangeArtifact(beforePath));
                    if (change.Kind == "to_parameters")
                    {
                        preview["parameterChanges"] = PrepareTechnologyParameters(technology, ChangeObject(payload, "parameters"), out _);
                    }
                    effects.Add(new JObject { ["technologyObject"] = technology.Name, ["technologyPath"] = entry.Path, ["action"] = change.Kind });
                    break;
                }
                case "drive_parameter_set":
                    preview["parameterChanges"] = Startdrive(() => DriveParameterSet(change, project, false));
                    effects.Add(new JObject { ["hardwarePath"] = payload["hardwarePath"].DeepClone(), ["driveObjectNumber"] = payload["driveObjectNumber"].DeepClone(), ["action"] = change.Kind });
                    ((JArray)preview["warnings"]).Add("Startdrive may recalculate dependent parameters; readback covers only the listed parameters. Compare drive_observe snapshots with drive_profile_review. Offline project only: no online write, download or RAM-to-ROM.");
                    break;
                default: throw new BridgeException("UnsupportedChange", "Unknown or unsupported change kind: " + change.Kind);
            }
            PrepareHardwarePreservation(change, project, preview);
            return preview;
        }

        private void ApplyEngineeringMutation(EngineeringChange change, Project project)
        {
            if (change.Kind == "hmi_engineering") { ApplyHmiEngineeringMutation(change, project); return; }
            var payload = change.Payload;
            switch (change.Kind)
            {
                case "graphical_typed_edit":
                    ApplyTypedGraphical(change);
                    break;
                case "project_simulation_enable":
                {
                    var settings = project.GetService<PlcSimulationSettingsProvider>() ?? throw new BridgeException("UnsupportedCapability", "Project has no PlcSimulationSettingsProvider service.");
                    settings.IsSimulationDuringBlockCompilationEnabled = payload.Value<bool>("enabled");
                    if (settings.IsSimulationDuringBlockCompilationEnabled != payload.Value<bool>("enabled")) throw new BridgeException("MutationVerificationFailed", "Project simulation setting did not match the requested value.");
                    break;
                }
                case "source_upsert":
                {
                    var plc = SelectPlc(change.Selector);
                    VerifyBlockTargets(change, plc, false);
                    if (payload.Value<string>("language")?.ToUpperInvariant() == "SIMATIC_SD")
                    {
                        MutationBlockGroup(change, plc).Blocks.ImportFromDocuments(new DirectoryInfo(change.Directory), Path.GetFileNameWithoutExtension(change.InputPath), ImportDocumentOptions.None);
                        change.Stages.Add("simatic_sd_imported");
                        VerifyExpectedBlocksExist(change, plc);
                        break;
                    }
                    var source = MutationSources(change, plc).ExternalSources.CreateFromFile("TiaMcp_" + change.Id, change.InputPath);
                    change.Stages.Add("external_source_imported");
                    var group = MutationBlockGroup(change, plc);
                    if (group is PlcBlockUserGroup userGroup) source.GenerateBlocksFromSource(userGroup, GenerateBlockOption.None);
                    else source.GenerateBlocksFromSource();
                    VerifyExpectedBlocksExist(change, plc);
                    break;
                }
                case "block_import_xml":
                case "safety_import_xml":
                case "graphical_operand_set":
                case "graphical_network_patch":
                case "graphical_network_insert":
                case "call_parameter_set":
                case "call_connection_set":
                {
                    var plc = SelectPlc(change.Selector);
                    bool safety = change.Kind == "safety_import_xml";
                    VerifyBlockTargets(change, plc, safety);
                    if (safety) EnsureSafetyWritable(plc.Software);
                    MutationBlockGroup(change, plc).Blocks.Import(new FileInfo(change.InputPath), ImportOptions.Override);
                    VerifyExpectedBlocksExist(change, plc);
                    if (safety) change.SafetyAfter = ReadSafety(plc.Software);
                    break;
                }
                case "type_rename":
                case "type_delete":
                    ApplyTypeEdit(change);
                    break;
                case "tag_set":
                case "tag_delete":
                case "constant_set":
                case "constant_delete":
                    ApplyTagEdit(change);
                    break;
                case "group_create":
                case "group_rename":
                case "group_delete":
                    GroupEdit(change, SelectPlc(change.Selector), true);
                    break;
                case "type_source_upsert":
                {
                    var plc = SelectPlc(change.Selector);
                    var source = MutationSources(change, plc).ExternalSources.CreateFromFile("TiaMcp_" + change.Id, change.InputPath);
                    var group = MutationTypeGroup(change, plc);
                    if (group is PlcTypeUserGroup userGroup) source.GenerateBlocksFromSource(userGroup, GenerateBlockOption.None);
                    else source.GenerateBlocksFromSource();
                    VerifyExpectedTypesExist(change, plc);
                    break;
                }
                case "plc_type_import_xml":
                    MutationTypeGroup(change, SelectPlc(change.Selector)).Types.Import(new FileInfo(change.InputPath), ImportOptions.Override);
                    VerifyExpectedTypesExist(change, SelectPlc(change.Selector));
                    break;
                case "tag_table_import_xml":
                    MutationTagGroup(change, SelectPlc(change.Selector)).TagTables.Import(new FileInfo(change.InputPath), ImportOptions.Override);
                    if (change.ObjectNames.Any(name => !MutationTagGroup(change, SelectPlc(change.Selector)).TagTables.Any(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))))
                        throw new BridgeException("ImportVerificationFailed", "Not all expected tag tables exist in the selected destination after import.");
                    break;
                case "block_delete":
                {
                    var plc = SelectPlc(change.Selector);
                    VerifyBlockTargets(change, plc, false);
                    var target = FindBlockByName(plc, change.BlockNames[0], true, change);
                    CheckDeleteDependencies(change, plc, target.Item, target.Item.Name);
                    target.Item.Delete();
                    if (FindBlockByName(plc, change.BlockNames[0], false, change) != null) throw new BridgeException("MutationVerificationFailed", "Deleted block still exists.");
                    break;
                }
                case "block_rename":
                {
                    var plc = SelectPlc(change.Selector);
                    VerifyBlockTargets(change, plc, false);
                    FindBlockByName(plc, change.BlockNames[0], true, change).Item.Name = payload.Value<string>("newName");
                    if (FindBlockByName(plc, change.BlockNames[0], false, change) != null || FindBlockByName(plc, payload.Value<string>("newName"), false, change) == null)
                        throw new BridgeException("MutationVerificationFailed", "Block rename did not produce the expected names.");
                    break;
                }
                case "hardware_add":
                {
                    var device = project.Devices.CreateWithItem(payload.Value<string>("typeIdentifier"), payload.Value<string>("itemName"), payload.Value<string>("deviceName"));
                    if (payload.Value<bool?>("configureScratchSecurity") == true)
                        change.ScratchSecurity = ConfigureNewScratchCpu(project, device, change.Directory);
                    break;
                }
                case "hardware_plug":
                {
                    var item = ChangeHardware(project, payload).Item;
                    string identifier = payload.Value<string>("typeIdentifier");
                    string name = payload.Value<string>("name");
                    int position = payload.Value<int>("positionNumber");
                    if (!item.CanPlugNew(identifier, name, position)) throw new BridgeException("IncompatibleHardware", "Requested plug position is no longer available.");
                    item.PlugNew(identifier, name, position);
                    break;
                }
                case "hardware_attributes":
                    ApplyAttributes(change.AttributeTarget, ChangeObject(payload, "attributes"), HardwareWriteAttributes);
                    break;
                case "hardware_address_set":
                {
                    ApplyHardwareAddress(change, project);
                    break;
                }
                case "hardware_move":
                {
                    var item = PrepareHardwareMove(change, project, null);
                    ((DeviceItem)item.Container).PlugMove(item, HardwareIndex(payload, "positionNumber"));
                    break;
                }
                case "hardware_channel_attributes":
                    ApplyAttributes(PrepareHardwareChannel(change, project, null), ChangeObject(payload, "attributes"), ChannelWriteAttributes);
                    break;
                case "hardware_cpu_memory_bytes":
                    ApplyCpuMemoryBytes(change, project);
                    break;
                case "network_io_assign":
                    ApplyIoAssignment(change, project);
                    break;
                case "hardware_delete":
                {
                    var item = ChangeHardware(project, payload).Item;
                    EnsureHardwareDeleteEligible(item);
                    item.Delete();
                    break;
                }
                case "network_configure":
                {
                    var node = ChangeNode(project, payload);
                    PrepareAttributes(node, payload["attributes"] as JObject ?? new JObject(), NetworkWriteAttributes);
                    if (payload.Value<bool?>("disconnect") == true) node.DisconnectFromSubnet();
                    string subnetName = payload.Value<string>("subnetName");
                    if (subnetName != null)
                    {
                        var subnet = project.Subnets.FirstOrDefault(s => string.Equals(s.Name, subnetName, StringComparison.OrdinalIgnoreCase));
                        if (subnet == null) node.CreateAndConnectToSubnet(subnetName);
                        else node.ConnectToSubnet(subnet);
                    }
                    ApplyAttributes(node, payload["attributes"] as JObject ?? new JObject(), NetworkWriteAttributes);
                    break;
                }
                case "to_create":
                {
                    var plc = SelectPlc(change.Selector);
                    if (FindTechnologyEntry(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", new JObject { ["name"] = payload["name"].DeepClone() }, false) != null) throw new BridgeException("ObjectExists", "Technology object already exists.");
                    var created = plc.Software.TechnologicalObjectGroup.TechnologicalObjects.Create(payload.Value<string>("name"), payload.Value<string>("typeIdentifier"), new Version(payload.Value<string>("version")));
                    if (payload["parameters"] != null) ApplyTechnologyParameters(created, ChangeObject(payload, "parameters"));
                    break;
                }
                case "to_delete":
                {
                    var plc = SelectPlc(change.Selector);
                    DeleteTechnologySelection(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", payload);
                    break;
                }
                case "to_parameters":
                {
                    var technology = RequiredTechnologyObject(SelectPlc(change.Selector), payload);
                    ApplyTechnologyParameters(technology, ChangeObject(payload, "parameters"));
                    break;
                }
                case "drive_parameter_set":
                    change.Readbacks.Add(new JObject { ["role"] = "drive-parameter-readback", ["parameters"] = Startdrive(() => DriveParameterSet(change, project, true)) });
                    break;
            }
        }

        private void CaptureTechnologyEvidence(EngineeringChange change)
        {
            if (change.Kind != "to_create" && change.Kind != "to_parameters") return;
            try
            {
                var plc = SelectPlc(change.Selector);
                var entry = RequiredTechnologyEntry(plc, change.Payload);
                var target = entry.Item;
                var budget = new ScanBudget(500);
                var warnings = new JArray();
                var parameters = new JArray((change.Payload["parameters"] as JObject ?? new JObject()).Properties().Select(property =>
                {
                    var parameter = target.Parameters.Find(property.Name);
                    return parameter == null || !string.Equals(parameter.Name, property.Name, StringComparison.Ordinal)
                        ? new JObject { ["name"] = property.Name, ["unresolved"] = true } : EngineeringParameterInfo(parameter);
                }));
                foreach (var warning in TechnologyParameterWarnings(parameters)) warnings.Add(warning.DeepClone());
                var connections = TechnologyConnections(target, budget, warnings, entry.Path, out var connectionCoverage, new TechnologyReferenceReader(plc.Project, plc.Path));
                change.TechnologyAfter = new JObject { ["name"] = target.Name, ["path"] = entry.Path,
                    ["ownerPath"] = entry.OwnerPath, ["composition"] = entry.Composition,
                    ["typeIdentifier"] = target.OfSystemLibElement, ["version"] = target.OfSystemLibVersion.ToString(),
                    ["requestedParameters"] = parameters, ["parametersComplete"] = TechnologyParametersComplete(parameters),
                    ["hardwareConnections"] = connections, ["hardwareConnectionCoverage"] = connectionCoverage, ["warnings"] = warnings, ["truncated"] = budget.Truncated };
            }
            catch (Exception error) { change.TechnologyAfter = new JObject { ["unresolved"] = true, ["reason"] = error.Message }; }
        }

        private JObject CompileEngineeringChange(EngineeringChange change, Project project)
        {
            if (change.Kind == "hmi_engineering") return CompileHmiEngineeringChange(change, project);
            if (change.Kind.StartsWith("hardware_", StringComparison.Ordinal) || change.Kind == "network_configure" || change.Kind == "network_io_assign" || change.Kind == "drive_parameter_set")
                return CompileDeviceProject(project);
            var plc = SelectPlc(change.Selector);
            return PlcCompilation.Compile(plc.Software.GetService<ICompilable>(), plc.Path, stage => {
                change.Stages.Add(stage);
                WriteChangeJournal(change);
            });
        }

        private static JObject ChangeCompilation(CompilerResult result, string target) => DeviceCompilation.Describe(result, target);

        private void PrepareBlockTargets(EngineeringChange change, PlcSelection plc, bool safety, bool mustExist, JArray effects)
        {
            foreach (var name in change.BlockNames)
            {
                var block = FindBlockByName(plc, name, mustExist, change);
                if (block != null)
                {
                    EnsureEditableBlock(block.Item, safety);
                    string backup = Path.Combine(change.Directory, "before-" + Guid.NewGuid().ToString("N") + ".xml");
                    block.Item.Export(new FileInfo(backup), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    change.Backups.Add(new JObject { ["blockName"] = name, ["blockPath"] = block.Path, ["artifact"] = ChangeArtifact(backup) });
                    effects.Add(new JObject { ["action"] = change.Kind, ["existingBlock"] = BlockInfo(block, plc.Path) });
                }
                else effects.Add(new JObject { ["action"] = "create", ["blockName"] = name });
            }
        }

        private void VerifyBlockTargets(EngineeringChange change, PlcSelection plc, bool safety)
        {
            foreach (var name in change.BlockNames)
            {
                var block = FindBlockByName(plc, name, false, change);
                if (block != null) EnsureEditableBlock(block.Item, safety);
            }
        }

        private void VerifyExpectedBlocksExist(EngineeringChange change, PlcSelection plc)
        {
            foreach (var name in change.BlockNames) FindBlockByName(plc, name, true, change);
        }

        private void ValidateXmlBlockNumbers(XDocument document, PlcSelection plc, EngineeringChange change)
        {
            var budget = new ScanBudget(50000);
            var existing = AllBlocks(plc, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Block-number collision check is incomplete.");
            foreach (var element in document.Root.Elements().Where(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)))
            {
                var attributes = element.Element("AttributeList");
                string name = attributes?.Element("Name")?.Value;
                if (!int.TryParse(attributes?.Element("Number")?.Value, out var number) || number < 1) continue;
                string type = element.Name.LocalName.Substring("SW.Blocks.".Length);
                string kind = type == "GlobalDB" || type == "InstanceDB" ? "DB" : type;
                string targetPath = ObjectIdentity.ObjectPath(change.GroupPath, name, change.Namespace);
                if (existing.Any(block => block.Item.Number == number && block.Path != targetPath && BlockNumberKind(block.Item) == kind))
                    throw new BridgeException("BlockNumberCollision", "XML block " + name + " names a number already used by an unpreviewed block. Remove the explicit Number and use auto-numbering for creation.");
            }
        }

        private static string BlockNumberKind(PlcBlock block)
        {
            if (block is FB) return "FB";
            if (block is FC) return "FC";
            if (block is OB) return "OB";
            return "DB";
        }

        private static void EnsureEditableBlock(PlcBlock block, bool safety)
        {
            if (block.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "Know-how protected block cannot be changed: " + block.Name);
            bool isSafety = block.ProgrammingLanguage.ToString().StartsWith("F_", StringComparison.Ordinal);
            if (isSafety != safety) throw new BridgeException("SafetyBoundary", "Standard and safety block operations cannot cross the safety-language boundary: " + block.Name);
            if (isSafety && (!block.IsConsistent || block.ProgrammingLanguage.ToString() == "F_CALL"))
                throw new BridgeException("UnsupportedSafetyBlock", "Only consistent exportable user safety blocks are eligible; generated F-system blocks are excluded.");
        }

        private BlockEntry FindBlockByName(PlcSelection plc, string name, bool required, EngineeringChange change, bool exactGroup = true)
        {
            var budget = new ScanBudget(50000);
            var blocks = ScopeBlocks(ResolveSoftwareScope(plc, change.ScopePath), budget)
                .Where(b => string.Equals(b.Item.Name, name, StringComparison.OrdinalIgnoreCase) && (b.Item.Namespace ?? "") == change.Namespace &&
                    (!exactGroup || ObjectIdentity.ParentPath(b.Path) == change.GroupPath)).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Block lookup was incomplete.");
            if (blocks.Length > 1) throw new BridgeException("AmbiguousTarget", "Multiple blocks have name: " + name);
            if (blocks.Length == 0 && required) throw new BridgeException("BlockNotFound", "Block not found: " + name);
            return blocks.FirstOrDefault();
        }

        // locationIndependent omits only the project location and save state, to compare a retrieved archive copy with its original.
        private string EngineeringFingerprint(EngineeringChange change, Project project, bool locationIndependent = false)
        {
            change.FingerprintCoverage = null;
            var snapshot = new JObject { ["fingerprintPolicyVersion"] = 2, ["projectPath"] = project.Path.FullName, ["lastModified"] = project.LastModified.ToString("O"), ["isModified"] = project.IsModified,
                ["simulationDuringBlockCompilationEnabled"] = project.GetService<PlcSimulationSettingsProvider>()?.IsSimulationDuringBlockCompilationEnabled };
            if (locationIndependent) { snapshot.Remove("projectPath"); snapshot.Remove("lastModified"); snapshot.Remove("isModified"); }
            var hardware = new JArray();
            var software = new JArray();
            var safetyStates = new JArray();
            var generatedExclusions = new JArray();
            var missingProviders = new JArray();
            int nativeFingerprintBlocks = 0;
            var budget = new ScanBudget(50000);
            foreach (var entry in DeviceItems(project, budget))
            {
                var item = entry.Item;
                var hw = new JObject { ["path"] = entry.Path, ["typeIdentifier"] = item.TypeIdentifier, ["position"] = item.PositionNumber };
                var network = item.GetService<NetworkInterface>();
                if (network != null) hw["nodes"] = new JArray(network.Nodes.Select(NodeSnapshot));
                hw["addresses"] = new JArray(item.Addresses.Select(address => new JObject { ["type"] = address.IoType.ToString(), ["start"] = address.StartAddress, ["length"] = address.Length }));
                hardware.Add(hw);
                var plc = item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (plc == null) continue;
                var selection = new PlcSelection { Project = project, Software = plc, Path = entry.Path + "/plc/" + Segment(plc.Name) };
                var safetyState = ReadSafety(plc);
                var safetySignatures = safetyState["signatures"] as JArray;
                bool hasSafetySignatureCoverage = safetyState.Value<bool>("available") && safetySignatures != null && safetySignatures.Count > 0
                    && safetySignatures.All(signature => !string.IsNullOrWhiteSpace(signature.Value<string>("type"))
                        && signature["value"] != null && signature["value"].Type != JTokenType.Null);
                safetyStates.Add(new JObject { ["plcPath"] = selection.Path, ["safety"] = safetyState });
                var scopes = SoftwareScopes(selection, budget);
                foreach (var scope in scopes)
                {
                    var blockItems = new JArray();
                    foreach (var block in ScopeBlocks(scope, budget))
                    {
                        bool protectedBlock = block.Item.IsKnowHowProtected;
                        bool consistentBlock = block.Item.IsConsistent;
                        var info = new JObject { ["name"] = block.Item.Name, ["path"] = block.Path, ["number"] = block.Item.Number,
                            ["language"] = block.Item.ProgrammingLanguage.ToString(), ["modified"] = block.Item.ModifiedDate.ToString("O"),
                            ["protected"] = protectedBlock, ["consistent"] = consistentBlock, ["nativeSystemGroup"] = block.NativeSystemGroup };
                        var content = EngineeringFingerprintRules.ReadBlockContent(block.NativeSystemGroup, protectedBlock, consistentBlock, hasSafetySignatureCoverage, block.Path, () =>
                        {
                            var provider = block.Item.GetService<FingerprintProvider>();
                            return provider == null ? null : new JArray(provider.GetFingerprints().OrderBy(f => f.Id).Select(f => new JObject { ["id"] = f.Id.ToString(), ["value"] = ChangeScalar(f.Value) }));
                        });
                        info["fingerprintMode"] = content.Mode;
                        if (content.Payload != null) { info["fingerprints"] = content.Payload; nativeFingerprintBlocks++; }
                        else if (content.Mode == "protectedGeneratedMetadata")
                            generatedExclusions.Add(new JObject { ["path"] = block.Path, ["name"] = block.Item.Name,
                                ["mode"] = content.Mode, ["consistent"] = consistentBlock,
                                ["reason"] = "Protected native system-group derived payload: checksum API can reject generated safety blocks after compilation; identity/modified/protection/consistency metadata remains covered." });
                        else missingProviders.Add(block.Path);
                        blockItems.Add(info);
                    }
                    software.Add(new JObject { ["name"] = scope.Name, ["path"] = scope.Path, ["kind"] = scope.Kind, ["blocks"] = blockItems,
                        ["userGroups"] = new JArray(EngineeringGroupPaths(scope.Blocks, scope.Path + "/blocks", 0)
                            .Concat(EngineeringGroupPaths(scope.Types, scope.Path + "/types", 0)).Concat(EngineeringGroupPaths(scope.Tags, scope.Path + "/tags", 0))),
                        ["namespacePreset"] = scope.Unit?.NamespacePreset,
                        ["relations"] = scope.Unit == null ? null : new JArray(scope.Unit.Relations.Select(relation => new JObject { ["relationType"] = relation.RelationType.ToString(), ["relatedObject"] = InspectionValue(relation.RelatedObject) })),
                        ["types"] = new JArray(ScopeTypes(scope, budget).Select(type => new JObject { ["name"] = type.Item.Name, ["path"] = type.Path, ["modified"] = type.Item.ModifiedDate.ToString("O"), ["protected"] = type.Item.IsKnowHowProtected })),
                        ["tagTables"] = new JArray(TagTables(scope.Tags, scope.Path + "/tags", budget).Select(table => new JObject { ["name"] = table.Item.Name, ["path"] = table.Path, ["modified"] = table.Item.ModifiedTimeStamp.ToString("O") })) });
                }
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish a complete change fingerprint for this project size.");
            snapshot["hardware"] = hardware;
            snapshot["software"] = software;
            snapshot["safety"] = safetyStates;
            if (change.Kind == "hmi_engineering") snapshot["hmiContent"] = HmiEngineeringFingerprint(change, project);
            if (HardwareEngineeringRules.Applies(change.Kind)) snapshot["configuredHardware"] = CaptureHardwareEngineering(project, change.Kind == "hardware_cpu_memory_bytes");
            if (change.AttributeTarget != null && change.Kind != "tag_delete" && change.Kind != "constant_delete")
            {
                var attrs = change.Payload["attributes"] as JObject ?? new JObject();
                snapshot["targetAttributes"] = new JObject(attrs.Properties().Select(p => new JProperty(p.Name, ChangeScalar(change.AttributeTarget.GetAttribute(p.Name)))));
            }
            if (change.Kind == "to_parameters")
            {
                var target = RequiredTechnologyObject(SelectPlc(change.Selector), change.Payload);
                var values = new JObject();
                foreach (var p in (change.Payload["parameters"] as JObject ?? new JObject()).Properties())
                {
                    object value = ReadTechnologyParameterValue(target, p.Name, false, out _, out var type);
                    values[p.Name] = new JObject { ["type"] = type.FullName, ["value"] = ChangeScalar(value) };
                }
                snapshot["technologyParameters"] = values;
            }
            if (change.Kind == "drive_parameter_set") snapshot["driveParameters"] = Startdrive(() => DriveParameterFingerprint(change, project));
            if (change.Kind.StartsWith("to_", StringComparison.Ordinal))
            {
                var plc = SelectPlc(change.Selector);
                snapshot["technologyObjects"] = TechnologyIdentityFingerprint(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology");
            }
            change.FingerprintCoverage = new JObject { ["policyVersion"] = 2, ["complete"] = false, ["nativeFingerprintBlocks"] = nativeFingerprintBlocks,
                ["protectedGeneratedPayloadExclusions"] = generatedExclusions, ["blocksWithoutNativeProvider"] = missingProviders,
                ["safetyCoverage"] = new JArray(safetyStates.Select(state => new JObject { ["plcPath"] = state["plcPath"].DeepClone(),
                    ["available"] = state["safety"]["available"].DeepClone(), ["signatureCount"] = (state["safety"]["signatures"] as JArray)?.Count ?? 0 })),
                ["limitations"] = new JArray("Generated payload bytes and unexposed vendor data are not fingerprinted; explicit covered native metadata and available safety signatures are compared.",
                    "Native fingerprint failures for user/editable blocks propagate; exclusions require protected native system-group provenance and available owning-PLC safety signatures.") };
            return ChangeHash(snapshot.ToString(Formatting.None));
        }

        private static IEnumerable<string> EngineeringGroupPaths(IEngineeringObject group, string path, int depth)
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "Group fingerprint nesting exceeds 64 levels.");
            yield return path;
            if (group is PlcBlockGroup blocks)
                foreach (var child in blocks.Groups) foreach (string value in EngineeringGroupPaths(child, path + "/groups/" + Segment(child.Name), depth + 1)) yield return value;
            else if (group is PlcTypeGroup types)
                foreach (var child in types.Groups) foreach (string value in EngineeringGroupPaths(child, path + "/groups/" + Segment(child.Name), depth + 1)) yield return value;
            else if (group is PlcTagTableGroup tags)
                foreach (var child in tags.Groups) foreach (string value in EngineeringGroupPaths(child, path + "/groups/" + Segment(child.Name), depth + 1)) yield return value;
            else if (group is TechnologicalInstanceDBGroup technology)
                foreach (var child in technology.Groups) foreach (string value in EngineeringGroupPaths(child, path + "/groups/" + Segment(child.Name), depth + 1)) yield return value;
        }

        private static JObject NodeSnapshot(Node node)
        {
            var attributes = new JObject();
            var infos = node.GetAttributeInfos();
            foreach (var name in NetworkWriteAttributes.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (!infos.Any(info => info.Name == name && (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0)) continue;
                attributes[name] = ChangeScalar(node.GetAttribute(name));
            }
            return new JObject { ["name"] = node.Name, ["type"] = node.NodeType.ToString(), ["subnet"] = node.ConnectedSubnet?.Name, ["attributes"] = attributes };
        }

        private void EnsureProjectOffline(Project project)
        {
            var budget = new ScanBudget(50000);
            foreach (var entry in DeviceItems(project, budget))
            {
                var provider = entry.Item.GetService<OnlineProvider>();
                if (provider != null && provider.State != OnlineState.Offline)
                    throw new BridgeException("OfflineRequired", "Offline engineering changes require the target to be offline. No disconnect or CPU action was performed: " + entry.Path);
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish complete offline state.");
        }

        private HardwareEntry ChangeHardware(Project project, JObject payload)
        {
            string path = ChangeText(payload, "hardwarePath", 4096);
            var budget = new ScanBudget(50000);
            var entry = DeviceItems(project, budget).FirstOrDefault(item => item.Path == path);
            if (entry == null) throw new BridgeException("HardwareNotFound", "No hardware item matches hardwarePath.");
            return entry;
        }

        private Node ChangeNode(Project project, JObject payload)
        {
            var item = ChangeHardware(project, payload).Item;
            var network = item.GetService<NetworkInterface>() ?? throw new BridgeException("UnsupportedCapability", "Selected hardware item has no network interface service.");
            string name = payload.Value<string>("nodeName");
            var nodes = network.Nodes.Where(node => name == null || node.Name == name).Take(2).ToArray();
            if (nodes.Length != 1) throw new BridgeException(nodes.Length == 0 ? "NodeNotFound" : "AmbiguousTarget", "Specify an exact nodeName from engineering_read.");
            return nodes[0];
        }

        private void ValidateCatalogIdentifier(string identifier)
        {
            string filter = identifier;
            int colon = filter.IndexOf(':'); if (colon >= 0) filter = filter.Substring(colon + 1);
            int slash = filter.IndexOf('/'); if (slash >= 0) filter = filter.Substring(0, slash);
            if (!_portal.HardwareCatalog.Find(filter).Any(e => e.TypeIdentifier == identifier || e.TypeIdentifierNormalized == identifier))
                throw new BridgeException("CatalogItemNotFound", "Hardware type identifier is not present in the installed catalog.");
        }

        private static TechnologyEntry RequiredTechnologyEntry(PlcSelection plc, JObject payload)
            => FindTechnologyEntry(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", payload, true);

        private static TechnologicalInstanceDB RequiredTechnologyObject(PlcSelection plc, JObject payload)
            => RequiredTechnologyEntry(plc, payload).Item;

        private static void EnsureHardwareDeleteEligible(DeviceItem item)
        {
            if (item.IsBuiltIn) throw new BridgeException("BuiltInHardware", "Built-in hardware items cannot be deleted through this operation.");
            if (HardwareSubtreeHasSoftware(item, 0)) throw new BridgeException("SoftwareBearingHardware", "Deletion of CPUs/HMIs or containers containing software requires a separate device-removal workflow; this operation covers pluggable modules.");
        }

        private static bool HardwareSubtreeHasSoftware(DeviceItem item, int depth)
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "Cannot inspect complete hardware subtree for deletion.");
            if (item.GetService<SoftwareContainer>()?.Software != null) return true;
            foreach (var child in item.DeviceItems) if (HardwareSubtreeHasSoftware(child, depth + 1)) return true;
            return false;
        }

        private void PrepareTypeTargets(EngineeringChange change, PlcSelection plc, JArray effects)
        {
            foreach (var name in change.ObjectNames)
            {
                var matches = MutationTypeGroup(change, plc).Types.Where(type => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) && (type.Namespace ?? "") == change.Namespace).ToArray();
                if (matches.Length > 1) throw new BridgeException("AmbiguousTarget", "Data type name is ambiguous: " + name);
                if (matches.Length == 1)
                {
                    var type = matches[0];
                    if (type.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "Protected data type cannot be changed: " + name);
                    string path = Path.Combine(change.Directory, "before-type-" + Guid.NewGuid().ToString("N") + ".xml");
                    type.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    change.Backups.Add(new JObject { ["typeName"] = name, ["artifact"] = ChangeArtifact(path) });
                }
                effects.Add(new JObject { ["dataType"] = name, ["action"] = matches.Length == 0 ? "create" : "update" });
            }
        }

        private static System.Collections.Generic.IEnumerable<PlcType> AllPlcTypes(PlcTypeGroup group, int depth = 0)
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "PLC type groups exceed nesting limit.");
            foreach (var type in group.Types) yield return type;
            foreach (var child in group.Groups) foreach (var type in AllPlcTypes(child, depth + 1)) yield return type;
        }
        private static System.Collections.Generic.IEnumerable<PlcTagTable> AllTagTables(PlcTagTableGroup group, int depth = 0)
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "Tag table groups exceed nesting limit.");
            foreach (var table in group.TagTables) yield return table;
            foreach (var child in group.Groups) foreach (var table in AllTagTables(child, depth + 1)) yield return table;
        }

        private static void EnsureSafetyWritable(PlcSoftware software)
        {
            var safety = SafetyAdministrationFor(software) ?? throw new BridgeException("UnsupportedCapability", "No SafetyAdministration service is available on the selected PLC CPU.");
            if (safety.IsSafetyOfflineProgramPasswordSet && !safety.IsLoggedOnToSafetyOfflineProgram)
                throw new BridgeException("SafetyLoginRequired", "Log into the safety offline program through the authorized TIA workflow first. This tool does not collect or bypass passwords.");
        }

        private static JObject ReadSafety(PlcSoftware software)
        {
            var safety = SafetyAdministrationFor(software);
            if (safety == null) return new JObject { ["available"] = false, ["reason"] = "SafetyAdministration service unavailable for this PLC." };
            return new JObject { ["available"] = true, ["isPasswordSet"] = safety.IsSafetyOfflineProgramPasswordSet,
                ["isLoggedOn"] = safety.IsLoggedOnToSafetyOfflineProgram,
                ["signatures"] = new JArray(safety.ProgramSignatures.Signatures.Select(s => new JObject { ["type"] = s.Type.ToString(), ["value"] = ChangeScalar(s.Value) })),
                ["runtimeGroups"] = new JArray(safety.RuntimeGroups.Select(g => new JObject { ["name"] = g.Name, ["fOb"] = g.FOBName,
                    ["mainSafetyBlock"] = g.MainSafetyBlockName, ["instanceDb"] = g.MainSafetyBlockIDbName,
                    ["maximumCycleTime"] = ChangeScalar(g.MaximumCycleTime), ["warnCycleTime"] = ChangeScalar(g.WarnCycleTime) })) };
        }

        private EngineeringChange GetEngineeringChange(JObject args)
        {
            string id = ChangeText(args, "changeId", 64);
            if (!_changes.TryGetValue(id, out var change)) throw new BridgeException("ChangeNotFound", "Unknown changeId in this worker session. Saved journal files remain available after disconnect.");
            return change;
        }

        private Project ProjectForChange(EngineeringChange change)
        {
            if (change.PreparedProcessId != _processId) throw new BridgeException("SessionChanged", "Change belongs to a different TIA attachment.");
            var project = SelectProject(change.Selector);
            if (!string.Equals(project.Path.FullName, change.ProjectPath, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("ProjectChanged", "Change belongs to a different project file.");
            return project;
        }

        private static void ValidatePreparedFiles(EngineeringChange change)
        {
            if (change.InputPath != null && (!File.Exists(change.InputPath) || ChangeFileHash(change.InputPath) != change.InputHash))
                throw new BridgeException("ArtifactChanged", "Prepared import/source artifact changed after preview.");
        }

        private JObject ChangeView(EngineeringChange change)
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
            if (change.Kind == "graphical_typed_edit") view["typedQualification"] = TypedChangeQualification(change);
            view["scratchSecurity"] = change.ScratchSecurity?.DeepClone();
            view["automaticRetryAllowed"] = false;
            return view;
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
                ["typedQualification"] = change.Kind == "graphical_typed_edit" ? TypedChangeQualification(change) : null,
                ["compile"] = change.Compilation?.DeepClone(), ["failure"] = change.Failure,
                ["primaryFailure"] = change.PrimaryFailure, ["recoveryFailure"] = change.RecoveryFailure,
                ["recoveryDirectory"] = change.RecoveryDirectory, ["recoveredProject"] = change.RecoveredProject?.DeepClone(),
                ["scratchSecurity"] = change.ScratchSecurity?.DeepClone(), ["safetyAfter"] = change.SafetyAfter?.DeepClone(), ["technologyAfter"] = change.TechnologyAfter?.DeepClone() };
            var temporary = change.Journal + ".new";
            File.WriteAllText(temporary, data.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(change.Journal)) TiaMcp.Core.JournalFileReplacement.Replace(temporary, change.Journal); else File.Move(temporary, change.Journal);
        }

        private static JObject ChangeObject(JObject args, string name) => args[name] as JObject ?? throw new BridgeException("InvalidRequest", name + " must be an object.");
        private static string[] ChangeStringArray(JObject args, string name, int count, int length, bool optional = false)
        {
            if (args[name] == null && optional) return new string[0];
            var array = args[name] as JArray;
            if (array == null || array.Count > count || (!optional && array.Count == 0) || array.Any(item => item.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)item) || ((string)item).Length > length))
                throw new BridgeException("InvalidRequest", name + " must be a bounded array of nonempty strings.");
            return array.Values<string>().ToArray();
        }
        private static string[] ChangeNames(JObject args, string name)
        {
            var array = args[name] as JArray ?? throw new BridgeException("InvalidRequest", name + " must be an array.");
            if (array.Count < 1 || array.Count > 100 || array.Any(v => v.Type != JTokenType.String || string.IsNullOrWhiteSpace(v.Value<string>())))
                throw new BridgeException("InvalidRequest", name + " must contain 1–100 block names.");
            var names = array.Values<string>().ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new BridgeException("InvalidRequest", "Duplicate expected block names.");
            return names;
        }
        private static bool ExplicitChangeBool(JObject args, string name)
        {
            if (args[name]?.Type != JTokenType.Boolean) throw new BridgeException("InvalidRequest", name + " must be explicitly true or false.");
            return args.Value<bool>(name);
        }
        private static void RequireChangeBool(JObject args, string name) { if (!ExplicitChangeBool(args, name)) throw new BridgeException("InvalidRequest", name + " must be true for this operation."); }
        private static string ChangeHash(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static string ChangeFileHash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static JObject ChangeArtifact(string path) => new JObject { ["path"] = path, ["bytes"] = new FileInfo(path).Length, ["sha256"] = ChangeFileHash(path) };
        private sealed class EngineeringChange
        {
            internal readonly JArray TypedDependencies = new JArray();
            internal readonly JArray TypedInstanceCreations = new JArray();
            internal CapturedStorageQualification CapturedStorageQualification;
            internal JArray Readbacks = new JArray();
            internal string Id, Kind, Directory, Journal, State, ProjectPath, ProjectName, PreviewHash, BaseFingerprint, AfterFingerprint;
            internal string InputPath, InputHash, ArchivePath, ArchiveHash, IdempotencyKey, Failure, RecoveryPreviousArchive;
            internal string PrimaryFailure, RecoveryFailure, RecoveryDirectory, ScopePath, GroupPath, Namespace;
            internal int? PreparedProcessId;
            internal JObject Selector, Payload, Preview, Compilation, RecoveredProject, SafetyAfter, ScratchSecurity, TechnologyAfter, FingerprintCoverage;
            internal JObject HmiEvidence, HmiBeforeManifest;
            internal JArray HardwareBeforeManifest;
            internal string[] BlockNames = Array.Empty<string>();
            internal string[] ObjectNames = Array.Empty<string>();
            internal readonly JArray Backups = new JArray();
            internal readonly JArray Stages = new JArray();
            internal IEngineeringObject AttributeTarget;
        }
    }
}
