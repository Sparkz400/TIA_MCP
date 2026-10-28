using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness.V21
{
    /// <summary>All methods execute serially on the worker's STA thread. Online credential provisioning is restricted to verified owned local PLCSIM targets.</summary>
    internal sealed partial class OpennessSession : IDisposable
    {
        private readonly string _apiDirectory;
        private TiaPortal _portal;
        private OwnedQualificationPortal _ownedQualificationPortal;
        private bool _ownsPortal;
        private int? _processId;
        private Project _scratchProject;
        private readonly NativeRuntimeLifetime _simulationRuntimeLifetime = new NativeRuntimeLifetime();

        internal OpennessSession(string apiDirectory, bool componentQualification = false) { _apiDirectory = apiDirectory; _componentQualificationEnabled = componentQualification; }
        internal OpennessSession(string apiDirectory, bool componentQualification, bool testSuiteScopeProbe) : this(apiDirectory, componentQualification)
        { _testSuiteScopeProbe = testSuiteScopeProbe; }
        internal OpennessSession(string apiDirectory, bool componentQualification, bool testSuiteScopeProbe, bool testSuiteQualification) : this(apiDirectory, componentQualification, testSuiteScopeProbe)
        {
#if TESTSUITE_QUALIFICATION
            _testSuiteQualification = testSuiteQualification;
#else
            if (testSuiteQualification) throw new BridgeException("TestSuiteProfileUnqualified", "This build contains no native qualification entry point.");
#endif
        }

        internal OpennessSession(string apiDirectory, bool componentQualification, bool testSuiteScopeProbe, bool testSuiteQualification, bool translationQualification, bool libraryIntegrateApply = false) : this(apiDirectory, componentQualification, testSuiteScopeProbe, testSuiteQualification)
        { _translationQualification = translationQualification; _libraryIntegrateApplyEnabled = libraryIntegrateApply; }

        internal JObject Execute(string operation, JObject arguments)
        {
            if (_translationLease != null && operation != "translation_stage" && operation != "translation_end")
                throw new BridgeException("ResourceBusy", "The translation session owns native exclusive access.");
            if (_testSuiteSession != null && operation != "testsuite_style_end" && operation != "testsuite_style_observe" &&
                operation != "testsuite_style_checkpoint" && operation != "testsuite_style_run" && operation != "testsuite_style_verify" &&
                operation != "testsuite_style_restore" && operation != "testsuite_style_reconcile")
                throw new BridgeException("ResourceBusy", "The owning-worker Test Suite session holds exclusive access.");
            if (IsSharedMutation(operation))
                throw new BridgeException("MutationAdmissionRequired", "This mutation requires shared resource admission through the current Host dispatcher.");
            if (_sharedMutationLease != null && operation != "mutation_dispatch" && operation != "mutation_end")
                throw new BridgeException("ResourceBusy", "The native shared mutation dispatch owns exclusive access.");
            if (_watchLease != null && operation != "watch_checkpoint" && operation != "watch_apply_native" && operation != "watch_end")
                throw new BridgeException("ResourceBusy", "The native watch dispatch owns exclusive access.");
            ObserveIndexOperation(operation);
            try { return Dispatch(operation, arguments); }
            // TIA refuses exports and other writes in a secondary (read-only) project; report that as the typed read-only refusal.
            catch (EngineeringTargetInvocationException ex) when (ex.Message.IndexOf("read-only context", StringComparison.OrdinalIgnoreCase) >= 0
                && _portal != null && _portal.Projects.Any(project => !project.IsPrimary))
            {
                throw new BridgeException("ReadOnlyProject", "TIA refuses this operation in a read-only (secondary) project: " + ex.Message);
            }
        }

        private JObject Dispatch(string operation, JObject arguments)
        {
            switch (operation)
            {
                case "addin_owner_context": return AddInOwnerContext(arguments);
                case "translation_fixture": return TranslationFixture(arguments);
                case "translation_capture": return TranslationCapture(arguments);
                case "translation_categories": return TranslationCategories(arguments);
                case "translation_begin": return TranslationBegin(arguments);
                case "translation_stage": return TranslationStage(arguments);
                case "translation_end": return TranslationEnd(arguments);
                case "project_text_export": return ProjectTextExport(arguments);
                case "testsuite_style_begin": return TestSuiteBegin(arguments);
                case "testsuite_scope_probe": return TestSuiteScopeProbe(arguments);
#if TESTSUITE_QUALIFICATION
                case "testsuite_qualification_prepare": return TestSuiteQualificationPrepare(arguments);
                case "testsuite_qualification_fault": return TestSuiteQualificationFault(arguments);
#endif
                case "testsuite_style_prerequisites": return TestSuitePrerequisites(arguments);
                case "testsuite_application_inventory": return TestSuiteApplicationInventory(arguments);
                case "testsuite_system_inventory": return TestSuiteSystemInventory(arguments);
                case "testsuite_style_observe": return TestSuiteStage("observe", arguments);
                case "testsuite_style_checkpoint": return TestSuiteStage("checkpoint", arguments);
                case "testsuite_style_run": return TestSuiteStage("run", arguments);
                case "testsuite_style_verify": return TestSuiteStage("verify", arguments);
                case "testsuite_style_restore": return TestSuiteStage("restore", arguments);
                case "testsuite_style_reconcile": return TestSuiteStage("reconcile", arguments);
                case "testsuite_style_end": return TestSuiteEnd(arguments);
                case "mutation_begin": return SharedMutationBegin(arguments);
                case "mutation_dispatch": return SharedMutationDispatch(arguments);
                case "mutation_end": return SharedMutationEnd(arguments);
                case "simulation_provision_reconcile_verify": return SimulationProvisionReconcileVerify(arguments);
                case "engineering_admission_reconcile_verify": return AdmissionReconcileVerify(arguments);
                case "component_window_plan": return ComponentWindowPlan(arguments);
                case "component_window_bind": return ComponentWindowBind(arguments);
                case "component_window_source": return ComponentWindowSourceObservation(arguments);
                case "component_window_read": return ComponentWindowRead(arguments);
                case "watch_read_native": return WatchRead(arguments);
                case "watch_capture_instance_native": return WatchCaptureInstance(arguments);
                case "watch_begin": return WatchBegin(arguments);
                case "watch_checkpoint": return WatchCheckpoint(arguments);
                case "watch_apply_native": return WatchApply(arguments);
                case "watch_end": return WatchEnd(arguments);
                case "watch_restore_native": return WatchRestore(arguments);
                case "watch_fixture_seed": return WatchFixtureSeed(arguments);
                case "watch_authoring_source_negative": return WatchAuthoringSourceNegative(arguments);
                case "bridge_info": return BridgeInfo();
                case "discover": return Discover();
                case "attach": return Attach(arguments);
                case "start": return Start();
                case "session_status": return Status();
                case "detach": return Detach();
                case "project_inventory": return Inventory(arguments);
                case "project_properties": return ProjectProperties(arguments);
                case "device_inventory_capture": return DeviceInventoryCapture(arguments);
                case "hmi_inspect": return HmiInspect(arguments);
                case "hmi_references": return HmiReferences(arguments);
                case "hmi_capabilities": return HmiCapabilities(arguments);
                case "project_index": return ProjectIndex(arguments);
                case "project_model": return ProjectModel(arguments);
                case "block_read": return BlockRead(arguments);
                case "xref_query": return XrefQuery(arguments);
                case "catalog_query": return CatalogQuery(arguments);
                case "scratch_create": return ScratchCreate(arguments);
                case "project_open": return ProjectOpen(arguments);
                case "project_retrieve": return ProjectRetrieve(arguments);
                case "plc_compare": return PlcCompare(arguments);
                case "software_inspect": return SoftwareInspect(arguments);
                case "type_read": return TypeRead(arguments);
                case "tag_table_read": return TagTableRead(arguments);
                case "block_interface": return BlockInterface(arguments);
                case "hardware_inspect": return HardwareInspect(arguments);
                case "connection_inspect": return ConnectionInspect(arguments);
                case "hardware_gsd_export": return HardwareGsdExport(arguments);
                case "cax_export": return CaxExport(arguments);
                case "technology_read": return TechnologyRead(arguments);
                case "plc_alarm_inspect": return PlcAlarmInspect(arguments);
                case "opcua_engineering_inspect": return OpcUaEngineeringInspect(arguments);
                case "opcua_engineering_export": return OpcUaEngineeringExport(arguments);
                case "drive_observe": return Startdrive(() => DriveObserve(arguments));
                case "safety_inspect": return SafetyInspect(arguments);
                case "access_inspect": return AccessInspect(arguments);
                case "certificate_inspect": return CertificateInspect(arguments);
                case "web_inspect": return WebInspect(arguments);
                case "vci_workspace_inspect": return VciWorkspaceInspect(arguments);
                case "safety_validation_inspect": return SafetyInspect(arguments, validation: true);
                case "safety_printout": return SafetyPrintoutCapture(arguments);
                case "call_graph": return CallGraph(arguments);
                case "instance_lookup": return InstanceLookup(arguments);
                case "symbol_trace": return SymbolTrace(arguments);
                case "change_impact": return ChangeImpact(arguments);
                case "engineering_read": return EngineeringRead(arguments);
                case "fixture_sources": return FixtureSources(arguments);
                case "plc_document_inventory": return PlcDocumentInventory(arguments);
                case "plc_loadable_generate": return PlcLoadableGenerate(arguments);
                case "change_prepare": return (arguments.Value<string>("kind") ?? "").StartsWith("library_", StringComparison.Ordinal) ? LibraryChangePrepare(arguments) : ChangePrepare(arguments);
                case "change_apply": return (arguments.Value<string>("changeId") ?? "").StartsWith("lib-", StringComparison.Ordinal) ? LibraryChangeApply(arguments) : ChangeApply(arguments);
                case "change_status": return (arguments.Value<string>("changeId") ?? "").StartsWith("lib-", StringComparison.Ordinal) ? LibraryChangeStatus(arguments) : ChangeStatus(arguments);
                case "change_restore": return (arguments.Value<string>("changeId") ?? "").StartsWith("lib-", StringComparison.Ordinal) ? LibraryChangeRestore(arguments) : ChangeRestore(arguments);
                case "library_open": return LibraryOpen(arguments);
                case "library_list": return LibraryList(arguments);
                case "library_query": return LibraryQuery(arguments);
                case "library_compare": return LibraryCompare(arguments);
                case "library_update_check": return LibraryUpdateCheck(arguments);
                case "library_export": return LibraryExport(arguments);
                case "library_close": return LibraryClose(arguments);
                case "library_capabilities": return LibraryCapabilities(arguments);
                case "simulation_download": return SimulationDownload(arguments);
                case "simulation_provision_scratch_credential": return SimulationProvision(arguments);
                case "simulation_discovery_baseline": return SimulationDiscoveryBaselineOperation(arguments);
                case "plc_online_compare": return PlcOnlineCompare(arguments);
                case "db_snapshot_export": return DbSnapshotExport(arguments);
                case "plc_station_upload": return PlcStationUpload(arguments);
                case "object_resolve": return ObjectResolve(arguments);
                case "target_capabilities": return TargetCapabilities(arguments);
                default: throw new BridgeException("UnknownOperation", "Unknown worker operation: " + operation);
            }
        }

        private JObject BridgeInfo()
        {
            var result = Envelope();
            result["workerVersion"] = "0.3.0";
            result["tiaVersion"] = 21;
            result["apiDirectory"] = _apiDirectory;
            result["apiAssemblyVersion"] = typeof(TiaPortal).Assembly.GetName().Version.ToString();
            result["framework"] = ".NET Framework 4.8";
            result["architecture"] = Environment.Is64BitProcess ? "x64" : "x86";
            result["capabilities"] = Capabilities();
            result["capabilityScope"] = "Static bridge summary only. Use target_capabilities for the selected native object and feature_catalog for versioned implementation/evidence contracts.";
            return result;
        }

        private static JArray Capabilities()
        {
            return new JArray(
                Capability("discover", "conditional", "Installed V21 Openness; discovery depends on current Windows session and Openness access."),
                Capability("attach", "conditional", "Requires an accessible V21 process and Openness authorization; attaches without opening or closing its projects."),
                Capability("project_inventory", "conditional", "Bounded offline device/address and block inventory including exposed software/safety units and generated system groups. Detailed inspection is provided by separate tools."),
                Capability("project_index", "conditional", "Bounded immutable metadata snapshots with authenticated pagination; external edits require explicit metadata verification, not a whole-project or logic revision."),
                Capability("project_model", "conditional", "Read-only versioned native hierarchy, bounded cached child/search queries, exact snapshot references, separately observed inspection/relationship evidence and portable JSON exports. Metadata does not certify content; hardware composition does not prove physical topology."),
                Capability("block_read", "conditional", "Native SimaticML XML export for eligible blocks; protected, inconsistent and F-system blocks can reject export."),
                Capability("xref_query", "conditional", "Native CrossReferenceService on the selected block or PLC; bounded result flattening with native locations."),
                Capability("start", "conditional", "Explicitly starts a worker-owned TIA instance without UI; disposal requests connection release. Siemens closes the instance when no other Openness client remains."),
                Capability("catalog_query", "conditional", "Queries the installed hardware catalog of the attached V21 instance; an open project is required."),
                Capability("scratch_create", "conditional", "Only a worker-owned empty TIA instance can create a uniquely named scratch project, one catalog-verified CPU and fixed SCL fixtures."),
                Capability("project_open", "conditional", "Existing .ap21 files can be opened in an empty worker-owned instance; no automatic upgrade. readOnly opens a secondary project on which every mutation is refused."),
                Capability("project_copy", "conditional", "Archives the saved open project and retrieves it into a new owned directory as a closed copy; the source is never saved or changed."),
                Capability("plc_compare", "conditional", "Bounded offline PlcSoftware.CompareTo tree against another PLC in the project or an open library; no online compare."),
                Capability("software_inspect", "conditional", "Tags/constants, types, software/safety units, block/DB interfaces and TO parameters via exposed services and native XML."),
                Capability("hardware_inspect", "conditional", "Offline hardware attributes, configured address ranges, network and exposed channel metadata; coverage varies by module."),
                Capability("drive_observe", "conditional", "Offline SINAMICS drive objects, parameters, BICO references and telegrams through the optional Startdrive Openness assembly; absent Startdrive reports UnsupportedCapability."),
                Capability("static_analysis", "conditional", "Native cross-reference call graphs, readers/writers, I/O associations and change impact. Indirect access and unresolved instance propagation remain explicit gaps."),
                Capability("recoverable_changes", "conditional", "Prepare/apply with preview hashes, conflict detection, recovery archive, persistent journal and compile results. Supported source/XML and typed engineering subsets only."),
                Capability("library_management", "conditional", "Managed global/project libraries, exact native type/version inspection, native copies, constrained authoring/update operations and durable resource journals. HMI products and native target qualification remain separate prerequisites."),
                Capability("safety_engineering", "conditional", "Exposed safety metadata/signatures and dedicated eligible F-block XML import. Protection is respected; safety acceptance/commissioning is outside this workflow."),
                Capability("simulation_download", "conditional", "Offline memory-card generation for an independently verified owned PLCSIM Advanced V8 instance; no physical PLC download route."),
                Capability("simulation_provision_scratch_credential", "conditional", "Existing generated scratch credential provisioning only for a ticket-bound owned local PLCSIM instance over isolated Softbus; runtime qualification is separate."),
                Capability("simulation_discovery_baseline", "conditional", "PLCSIM discovery before any instance registration; may start the Runtime Manager. Lets the next provisioning attempt in the same session, project and PLC, within five minutes, exclude only the single exact all-zero Runtime Manager entry from the same process."),
                Capability("runtime_values", "conditional", "Separate PLCSIM Advanced V8 adapter provides typed values and bounded test scenarios; installed runtime license and compatible CPU/program are required."),
                Capability("physical_plc", "unsupported", "No physical PLC runtime writes, forces, downloads or CPU state changes are exposed."));
        }

        private static JObject Capability(string name, string status, string reason) => new JObject { ["name"] = name, ["status"] = status, ["reason"] = reason };

        private JObject Discover()
        {
            var processes = new JArray();
            foreach (var process in PortalProcesses.Discover())
            {
                processes.Add(new JObject { ["processId"] = process.ProcessId, ["mode"] = process.Mode,
                    ["executablePath"] = process.ExecutablePath, ["projectPath"] = process.ProjectPath });
            }
            var result = Envelope();
            result["processes"] = processes;
            return result;
        }

        private JObject Attach(JObject args)
        {
            EnsureDetached();
            int id = RequiredInteger(args, "processId");
            var identity = CapturedProcessIdentity.Capture(id);
            try { _portal = PortalProcesses.Attach(id); }
            catch { identity.Dispose(); throw; }
            _portalProcessIdentity = identity;
            _processId = id;
            _ownsPortal = false;
            return Status();
        }

        private JObject Start()
        {
            EnsureDetached();
            if (_testSuiteScopeProbe || _testSuiteQualification)
            {
                _ownedQualificationPortal = new OwnedQualificationPortal();
                _portal = _ownedQualificationPortal.Connection;
            }
            else _portal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
            _ownsPortal = true;
            var processObservedAtUtc = DateTime.UtcNow;
            _processId = PortalProcesses.CurrentId(_portal);
            _portalProcessIdentity = CapturedProcessIdentity.Capture(_processId.Value, processObservedAtUtc);
            return Status();
        }

        private JObject Status()
        {
            var result = Envelope();
            result["attached"] = _portal != null;
            result["processId"] = _processId;
            result["ownsPortalProcess"] = _ownsPortal;
            result["disconnectBehavior"] = _ownsPortal ? "Dispose this connection. Siemens closes the headless instance when this is its last attached Openness client; process exit is not verified. Only explicitly saved scratch data is retained." : "Dispose only this Openness attachment; the user-owned TIA instance remains open.";
            if ((_testSuiteScopeProbe || _testSuiteQualification) && _ownsPortal)
                result["disconnectBehavior"] = "Dispose this connection and publicly close its exact worker-created scope-probe UI process. The qualification controller must independently verify its exit through the original retained handle.";
            result["projects"] = _portal == null ? new JArray() : new JArray(_portal.Projects.Select(p => { var info = ProjectInfo(p); info["readOnly"] = !p.IsPrimary; return info; }));
            result["capabilities"] = Capabilities();
            return result;
        }

        private JObject Detach()
        {
            bool disposedOwnedConnection = _ownsPortal && _portal != null;
            DetachPortalConnection();
            var result = Status();
            result["disposedOwnedPortalConnection"] = disposedOwnedConnection;
            result["processExitVerified"] = false;
            return result;
        }

        public void Dispose()
        {
            try { DetachPortalConnection(); }
            finally { _simulationRuntimeLifetime.Dispose(); }
        }

        private void DetachPortalConnection()
        {
            TranslationRelease();
            TestSuiteRelease();
            SharedMutationRelease();
            WatchRelease();
            _watchEpoch = Guid.NewGuid().ToString("N");
            CloseManagedLibraries();
            _projectIndexes.Invalidate("dispose");
            _indexRequests.Clear();
            var portal = _portal;
            var ownedQualificationPortal = _ownedQualificationPortal;
            var processIdentity = _portalProcessIdentity;
            _portalProcessIdentity = null;
            _ownedQualificationPortal = null;
            _portal = null;
            _ownsPortal = false;
            _processId = null;
            _scratchProject = null;
            // Dispose detaches from externally owned instances. Do not call Project.Close or Process.Kill.
            try
            {
                if (ownedQualificationPortal != null) ownedQualificationPortal.Dispose();
                else portal?.Dispose();
            }
            finally { processIdentity?.Dispose(); }
        }

        private JObject Inventory(JObject args)
        {
            var project = SelectProject(args);
            int max = Limit(args, 500);
            var budget = new ScanBudget(max);
            var hardware = new JArray();
            var plcs = new JArray();
            var blocks = new JArray();
            var warnings = new JArray();
            foreach (var entry in DeviceItems(project, budget))
            {
                var item = entry.Item;
                var info = new JObject { ["path"] = entry.Path, ["name"] = item.Name,
                    ["positionNumber"] = item.PositionNumber, ["classification"] = item.Classification.ToString(),
                    ["typeIdentifier"] = item.TypeIdentifier };
                try
                {
                    info["addresses"] = new JArray(item.Addresses.Take(100).Select(a => new JObject { ["ioType"] = a.IoType.ToString(), ["startAddress"] = a.StartAddress,
                        ["length"] = a.Length, ["lengthUnit"] = "bits", ["byteSpan"] = TiaMcp.Analysis.OfflineEvidence.IoBitLengthToByteLength(a.Length) }));
                    if (item.Addresses.Count > 100) { budget.Truncated = true; warnings.Add("Address list bounded to 100 entries for " + entry.Path); }
                }
                catch (Exception ex) { warnings.Add("Addresses unavailable at " + entry.Path + ": " + ex.Message); }
                hardware.Add(info);
                PlcSoftware software;
                try { software = item.GetService<SoftwareContainer>()?.Software as PlcSoftware; }
                catch (Exception ex) { warnings.Add("Software unavailable at " + entry.Path + ": " + ex.Message); continue; }
                if (software == null) continue;
                string plcPath = entry.Path + "/plc/" + Segment(software.Name);
                plcs.Add(new JObject { ["path"] = plcPath, ["name"] = software.Name, ["hardwarePath"] = entry.Path });
                foreach (var block in AllBlocks(new PlcSelection { Project = project, Software = software, Path = plcPath }, budget)) blocks.Add(BlockInfo(block, plcPath));
            }
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["hardware"] = hardware;
            result["plcs"] = plcs;
            result["blocks"] = blocks;
            result["warnings"] = warnings;
            result["completeness"] = new JObject { ["complete"] = false, ["scopeComplete"] = !budget.Truncated && warnings.Count == 0,
                ["truncated"] = budget.Truncated, ["maxItems"] = max, ["visitedItems"] = budget.Used,
                ["scope"] = "Device items, configured address ranges and exposed program blocks including units/system groups; no online data.",
                ["gaps"] = new JArray("Use software_inspect for tags and PLC data types", "Use hardware_inspect for exposed channel details", "Use technology_read for TO parameters", "Use safety_inspect for safety administration", "Library internals", "No transactional snapshot or project revision token") };
            return result;
        }

        private JObject BlockRead(JObject args)
        {
            if (args.Value<string>("format") != null) return FormatRead(args, "block", "blockPath", "blockName");
            var selected = SelectBlock(args);
            if (selected.Block.Item.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "The selected block is know-how protected.");
            string directory = ArtifactDirectory(args);
            string destination = Path.Combine(directory, "block-" + Guid.NewGuid().ToString("N") + ".xml");
            if (File.Exists(destination)) throw new BridgeException("ArtifactExists", "Refusing to overwrite an existing export.");
            selected.Block.Item.Export(new FileInfo(destination), ExportOptions.WithDefaults);
            var file = new FileInfo(destination);
            var result = Envelope();
            result["project"] = ProjectInfo(selected.Project);
            result["block"] = BlockInfo(selected.Block, selected.PlcPath);
            result["artifact"] = FileArtifact(file);
            // Large XML stays on disk, and can be read independently of MCP response limits.
            if (file.Length <= 512 * 1024) result["xml"] = File.ReadAllText(destination);
            result["completeness"] = new JObject { ["complete"] = true, ["scope"] = "Native block XML export", ["xmlIncluded"] = file.Length <= 512 * 1024,
                ["note"] = "Native XML is retained unchanged. No normalized semantic model or revision guarantee is implied." };
            return result;
        }

        private JObject XrefQuery(JObject args)
        {
            CrossReferenceService service;
            string target;
            Project project;
            if (!string.IsNullOrWhiteSpace(args.Value<string>("blockPath")) || !string.IsNullOrWhiteSpace(args.Value<string>("blockName")))
            {
                var block = SelectBlock(args);
                project = block.Project;
                target = block.Block.Path;
                service = block.Block.Item.GetService<CrossReferenceService>();
            }
            else
            {
                var plc = SelectPlc(args);
                project = plc.Project;
                target = plc.Path;
                service = plc.Software.GetService<CrossReferenceService>();
            }
            if (service == null) throw new BridgeException("UnsupportedCapability", "CrossReferenceService is unavailable for this target.");
            int max = Limit(args, 500);
            var budget = new ScanBudget(max);
            var rows = new JArray();
            var native = service.GetCrossReferences(CrossReferenceFilter.AllObjects);
            AddSources(native.Sources, rows, budget, 0);
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["targetPath"] = target;
            result["references"] = rows;
            result["completeness"] = new JObject { ["complete"] = !budget.Truncated, ["truncated"] = budget.Truncated, ["maxItems"] = max,
                ["scope"] = "Native offline cross-reference results for the target, including child sources.",
                ["note"] = "Rows without a native engineering object preserve the native textual identity. Indirect runtime accesses and call-instance propagation are not resolved." };
            return result;
        }

        private static void AddSources(IEnumerable<SourceObject> sources, JArray rows, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; return; }
            foreach (var source in sources)
            {
                if (!budget.Take()) return;
                var sourceInfo = new JObject { ["name"] = source.Name, ["path"] = source.Path, ["device"] = source.Device,
                    ["address"] = source.Address, ["typeName"] = source.TypeName, ["hasEngineeringObject"] = source.UnderlyingObject != null };
                var references = new JArray();
                rows.Add(new JObject { ["source"] = sourceInfo, ["references"] = references });
                foreach (var reference in source.References)
                {
                    if (!budget.Take()) return;
                    var locations = new JArray();
                    references.Add(new JObject { ["name"] = reference.Name, ["path"] = reference.Path, ["device"] = reference.Device,
                        ["address"] = reference.Address, ["typeName"] = reference.TypeName, ["hasEngineeringObject"] = reference.UnderlyingObject != null, ["locations"] = locations });
                    foreach (var location in reference.Locations)
                    {
                        if (!budget.Take()) return;
                        locations.Add(new JObject { ["access"] = location.Access.ToString(), ["address"] = location.Address,
                            ["name"] = location.Name, ["referenceLocation"] = location.ReferenceLocation, ["referenceType"] = location.ReferenceType.ToString(),
                            ["referencedAsName"] = location.ReferencedAsName, ["typeName"] = location.TypeName,
                            ["hasReferencedAsObject"] = location.ReferencedAs != null });
                    }
                }
                AddSources(source.Children, rows, budget, depth + 1);
                if (budget.Truncated) return;
            }
        }

        private JObject CatalogQuery(JObject args)
        {
            EnsureAttached();
            if (_portal.Projects.Count == 0) throw new BridgeException("ProjectRequired", "TIA hardware catalog queries require an open project. scratch_create can create an isolated project and select a CPU from its catalog.");
            string filter = args.Value<string>("filter");
            if (string.IsNullOrWhiteSpace(filter) || filter.Length > 256) throw new BridgeException("InvalidRequest", "A catalog filter of 1–256 characters is required.");
            var entries = _portal.HardwareCatalog.Find(filter);
            int max = Limit(args, 100);
            var result = Envelope();
            result["entries"] = new JArray(entries.Take(max).Select(entry => new JObject { ["typeIdentifier"] = entry.TypeIdentifier,
                ["articleNumber"] = entry.ArticleNumber, ["typeName"] = entry.TypeName, ["description"] = entry.Description,
                ["version"] = entry.Version?.ToString(), ["catalogPath"] = entry.CatalogPath }));
            result["completeness"] = new JObject { ["complete"] = entries.Count <= max, ["truncated"] = entries.Count > max, ["totalMatches"] = entries.Count };
            return result;
        }

        private JObject ScratchCreate(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0 || _scratchProject != null)
                throw new BridgeException("ScratchIsolationRequired", "scratch_create requires an empty TIA instance explicitly started by this worker. Existing projects cannot be changed.");
            string cpuType = args.Value<string>("cpuTypeIdentifier");
            if (cpuType != null && cpuType.Length > 256) throw new BridgeException("InvalidRequest", "cpuTypeIdentifier is limited to 256 characters.");
            string artifacts = ArtifactDirectory(args);
            string name = SharedMutationScratchName();
            string runDirectory = Path.Combine(artifacts, name);
            if (Path.Combine(runDirectory, name).Length > 143)
                throw new BridgeException("ProjectPathTooLong", "TIA project directories are limited to 143 characters. Configure a shorter host --artifacts path before creating a scratch project; no project was created.");
            if (Directory.Exists(runDirectory)) throw new BridgeException("ArtifactExists", "Scratch output directory already exists.");
            Directory.CreateDirectory(runDirectory);
            var stages = new JArray();
            var result = Envelope();
            result["artifactDirectory"] = runDirectory;
            result["stages"] = stages;
            try
            {
                _scratchProject = _portal.Projects.Create(new DirectoryInfo(runDirectory), name);
                stages.Add("project_created");
                ComponentScratchLanguage(_scratchProject, result);
                // Siemens requires an open project for catalog access. Never invent an order number.
                string catalogFilter = "1511-1 PN";
                if (!string.IsNullOrWhiteSpace(cpuType))
                {
                    catalogFilter = cpuType;
                    int prefix = catalogFilter.IndexOf(':');
                    if (prefix >= 0) catalogFilter = catalogFilter.Substring(prefix + 1);
                    int version = catalogFilter.IndexOf('/');
                    if (version >= 0) catalogFilter = catalogFilter.Substring(0, version);
                }
                var candidates = _portal.HardwareCatalog.Find(catalogFilter);
                var catalogEntry = string.IsNullOrWhiteSpace(cpuType)
                    ? candidates.Where(entry => string.Equals(entry.TypeName, "CPU 1511-1 PN", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(entry => ParseVersion(entry.Version))
                        .ThenBy(entry => entry.ArticleNumber, StringComparer.Ordinal).FirstOrDefault()
                    : candidates.FirstOrDefault(entry => string.Equals(entry.TypeIdentifier, cpuType, StringComparison.Ordinal) ||
                        string.Equals(entry.TypeIdentifierNormalized, cpuType, StringComparison.Ordinal));
                if (catalogEntry == null) throw new BridgeException("CatalogItemNotFound", "No matching CPU was found in the installed catalog. Supply an exact catalog CPU type identifier on the next isolated run.");
                cpuType = catalogEntry.TypeIdentifier;
                result["selectedCpu"] = new JObject { ["typeIdentifier"] = cpuType, ["typeName"] = catalogEntry.TypeName,
                    ["articleNumber"] = catalogEntry.ArticleNumber, ["version"] = catalogEntry.Version,
                    ["selection"] = string.IsNullOrWhiteSpace(args.Value<string>("cpuTypeIdentifier")) ? "Installed standard CPU 1511-1 PN; newest available firmware across matching articles, with article number as deterministic tie-break." : "Explicit catalog identifier." };
                stages.Add("cpu_catalog_verified");
                var scratchDevice = _scratchProject.Devices.CreateWithItem(cpuType, "ScratchPLC", "ScratchStation");
                stages.Add("device_created");
                result["scratchSecurity"] = ConfigureNewScratchCpu(_scratchProject, scratchDevice, runDirectory);
                stages.Add("new_scratch_cpu_security_configured");
                var plc = SelectPlc(new JObject { ["projectName"] = _scratchProject.Name });
                string sourcePath = Path.Combine(runDirectory, "TiaMcpFixture.scl");
                File.WriteAllText(sourcePath, ScratchSource, new UTF8Encoding(true));
                var source = plc.Software.ExternalSourceGroup.ExternalSources.CreateFromFile("TiaMcpFixture", sourcePath);
                source.GenerateBlocksFromSource();
                stages.Add("scl_blocks_generated");
                var compiler = plc.Software.GetService<ICompilable>();
                if (compiler != null)
                {
                    var compilation = compiler.Compile();
                    var compileBudget = new ScanBudget(100);
                    var messages = new JArray();
                    AddCompileMessages(compilation.Messages, messages, compileBudget, 0);
                    result["compile"] = CompilationEvidenceRules.SingleTarget(new JObject { ["state"] = compilation.State.ToString(), ["errorCount"] = compilation.ErrorCount, ["warningCount"] = compilation.WarningCount,
                        ["messages"] = messages, ["messagesTruncated"] = compileBudget.Truncated }, plc.Path);
                    stages.Add("software_compiled");
                    if (!CompilationEvidenceRules.IsSuccessfulTarget((JObject)result["compile"])) throw new BridgeException("CompileFailed", "Scratch source generated, but the PLC software compile reported errors or incomplete diagnostic evidence.");
                }
                else throw new BridgeException("UnsupportedCapability", "The scratch PLC has no supported compiler service.");
                _scratchProject.Save();
                stages.Add("project_saved");
                var exports = new JArray();
                result["exports"] = exports;
                var fixtureNames = new HashSet<string>(new[] { "Main", "TiaMcp_IsEnabled", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB" }, StringComparer.Ordinal);
                result["exportScope"] = "Only the four fixed standard source fixtures; generated safety/system blocks are not exported.";
                foreach (var block in Blocks(plc.Software.BlockGroup, plc.Path + "/blocks", new ScanBudget(1000)).Where(block => fixtureNames.Contains(block.Item.Name)))
                {
                    string path = Path.Combine(runDirectory, "block-" + Guid.NewGuid().ToString("N") + ".xml");
                    block.Item.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    exports.Add(new JObject { ["block"] = BlockInfo(block, plc.Path), ["artifact"] = FileArtifact(new FileInfo(path)) });
                }
                stages.Add("blocks_exported");
                result["completed"] = true;
            }
            catch (Exception ex)
            {
                // Keep partial scratch artifacts and report exactly what completed; never retry a write here.
                result["completed"] = false;
                result["failure"] = new JObject { ["code"] = (ex as BridgeException)?.Code ?? "EngineeringError", ["exceptionType"] = ex.GetType().FullName, ["message"] = ex.Message };
                result["recovery"] = "Partial scratch state remains while this worker-owned session is connected. Saved artifacts were retained; unsaved project edits can be lost on disconnect. This operation is not automatically retried.";
                if (_scratchProject != null) result["project"] = ProjectInfo(_scratchProject);
                throw new BridgeException((ex as BridgeException)?.Code ?? "EngineeringError", "Scratch feasibility did not complete: " + ex.Message, result);
            }
            if (_scratchProject != null) result["project"] = ProjectInfo(_scratchProject);
            result["attemptedCoverage"] = new JArray("One installed CPU", "Fixed standard SCL FC, FB, instance DB and cyclic Main OB call", "Software compile", "Native block XML exports");
            result["unverified"] = new JArray("KOP/LAD", "FUP/FBD", "AWL/STL", "Safety", "PLCSIM download and execution");
            return result;
        }

        // An OB with an empty TEMP interface must use optimized access. A standard OB requires
        // its legacy start-information interface (20 bytes) even when our code does not use it.
        private const string ScratchSource = "FUNCTION \"TiaMcp_IsEnabled\" : Bool\r\nVERSION : 0.1\r\nVAR_INPUT\r\n    Enable : Bool;\r\nEND_VAR\r\nBEGIN\r\n    #TiaMcp_IsEnabled := #Enable;\r\nEND_FUNCTION\r\n\r\nFUNCTION_BLOCK \"TiaMcp_EnableGate\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nVAR_INPUT\r\n    Enable : Bool;\r\nEND_VAR\r\nVAR_OUTPUT\r\n    Active : Bool;\r\nEND_VAR\r\nBEGIN\r\n    #Active := \"TiaMcp_IsEnabled\"(Enable := #Enable);\r\nEND_FUNCTION_BLOCK\r\n\r\nDATA_BLOCK \"TiaMcp_EnableGate_DB\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\n\"TiaMcp_EnableGate\"\r\nBEGIN\r\nEND_DATA_BLOCK\r\n\r\nORGANIZATION_BLOCK \"Main\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nBEGIN\r\n    \"TiaMcp_EnableGate_DB\"(Enable := TRUE);\r\nEND_ORGANIZATION_BLOCK\r\n";

        private static void AddCompileMessages(IEnumerable<CompilerResultMessage> input, JArray output, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; return; }
            foreach (var message in input)
            {
                if (!budget.Take()) return;
                output.Add(new JObject { ["description"] = message.Description, ["path"] = message.Path, ["state"] = message.State.ToString(), ["depth"] = depth });
                AddCompileMessages(message.Messages, output, budget, depth + 1);
                if (budget.Truncated) return;
            }
        }

        private Project SelectProject(JObject args)
        {
            EnsureAttached();
            string name = args.Value<string>("projectName");
            var matches = _portal.Projects.Where(p => string.IsNullOrWhiteSpace(name) || string.Equals(p.Name, name, StringComparison.Ordinal)).Take(2).ToList();
            if (matches.Count == 0) throw new BridgeException("ProjectNotFound", "No matching open project was found.");
            if (matches.Count != 1) throw new BridgeException("AmbiguousTarget", "More than one open project matches; specify projectName.");
            return matches[0];
        }

        private PlcSelection SelectPlc(JObject args)
        {
            var project = SelectProject(args);
            string wanted = args.Value<string>("plcPath");
            var budget = new ScanBudget(50000);
            var matches = new List<PlcSelection>();
            foreach (var entry in DeviceItems(project, budget))
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (software == null) continue;
                string path = entry.Path + "/plc/" + Segment(software.Name);
                if (string.IsNullOrWhiteSpace(wanted) || string.Equals(path, wanted, StringComparison.Ordinal))
                    matches.Add(new PlcSelection { Project = project, Software = software, Path = path });
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "PLC lookup reached its traversal limit; completeness cannot be established.");
            if (matches.Count == 0) throw new BridgeException("PlcNotFound", "No matching PLC software was found.");
            if (matches.Count != 1) throw new BridgeException("AmbiguousTarget", "More than one PLC matches; specify an exact plcPath from project_inventory.");
            return matches[0];
        }

        private BlockSelection SelectBlock(JObject args)
        {
            return CanonicalSelectBlock(args);
        }

        private static IEnumerable<HardwareEntry> DeviceItems(Project project, ScanBudget budget)
        {
            string root = "/projects/" + Segment(project.Name);
            foreach (var entry in Devices(project.Devices, root + "/devices", budget)) yield return entry;
            foreach (var entry in Devices(project.UngroupedDevicesGroup.Devices, root + "/ungrouped", budget)) yield return entry;
            foreach (var entry in DeviceGroups(project.DeviceGroups, root + "/groups", budget, 0)) yield return entry;
        }

        private static IEnumerable<HardwareEntry> DeviceGroups(IEnumerable<DeviceUserGroup> groups, string root, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var group in groups)
            {
                if (!budget.Take()) yield break;
                string path = root + "/" + Segment(group.Name);
                foreach (var entry in Devices(group.Devices, path + "/devices", budget)) yield return entry;
                foreach (var entry in DeviceGroups(group.Groups, path + "/groups", budget, depth + 1)) yield return entry;
            }
        }

        private static IEnumerable<HardwareEntry> Devices(IEnumerable<Device> devices, string root, ScanBudget budget)
        {
            foreach (var device in devices)
            {
                if (!budget.Take()) yield break;
                foreach (var entry in Items(device.DeviceItems, root + "/" + Segment(device.Name), budget, 0)) yield return entry;
            }
        }

        private static IEnumerable<HardwareEntry> Items(IEnumerable<DeviceItem> items, string root, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var item in items)
            {
                if (!budget.Take()) yield break;
                string path = root + "/items/" + Segment(item.Name);
                yield return new HardwareEntry { Item = item, Path = path };
                foreach (var entry in Items(item.DeviceItems, path, budget, depth + 1)) yield return entry;
            }
        }

        private static IEnumerable<BlockEntry> Blocks(PlcBlockGroup group, string root, ScanBudget budget, int depth = 0)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var block in group.Blocks)
            {
                if (!budget.Take()) yield break;
                yield return new BlockEntry { Item = block, Path = BlockObjectPath(root, block) };
            }
            foreach (var child in group.Groups)
            {
                if (!budget.Take()) yield break;
                foreach (var block in Blocks(child, root + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return block;
            }
        }

        private static JObject BlockInfo(BlockEntry block, string plcPath) => new JObject { ["path"] = block.Path, ["plcPath"] = plcPath,
            ["name"] = block.Item.Name, ["number"] = block.Item.Number, ["kind"] = block.Item.GetType().Name,
            ["programmingLanguage"] = block.Item.ProgrammingLanguage.ToString(), ["isConsistent"] = block.Item.IsConsistent,
            ["isKnowHowProtected"] = block.Item.IsKnowHowProtected };

        private static JObject ProjectInfo(Project project) => new JObject { ["name"] = project.Name, ["path"] = project.Path?.FullName };
        private JObject Envelope() => new JObject { ["origin"] = "TIA Portal V21 Openness/offline", ["observedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["processId"] = _processId };
        private static string Segment(string value) => Uri.EscapeDataString(value ?? "");
        private static Version ParseVersion(string version)
        {
            Version parsed;
            return Version.TryParse((version ?? "").TrimStart('V', 'v'), out parsed) ? parsed : new Version(0, 0);
        }
        private void EnsureAttached() { if (_portal == null) throw new BridgeException("NotAttached", "Attach to or explicitly start a TIA V21 instance first."); }
        private void EnsureDetached() { if (_portal != null) throw new BridgeException("AlreadyAttached", "Detach the current session before attaching to or starting another instance."); }

        private static int RequiredInteger(JObject args, string name)
        {
            if (args[name]?.Type != JTokenType.Integer || args.Value<long>(name) < 1 || args.Value<long>(name) > int.MaxValue)
                throw new BridgeException("InvalidRequest", name + " must be a positive integer.");
            return args.Value<int>(name);
        }

        private static int Limit(JObject args, int defaultValue)
        {
            if (args["maxItems"] == null) return defaultValue;
            int value = RequiredInteger(args, "maxItems");
            if (value > 2000) throw new BridgeException("InvalidRequest", "maxItems must be between 1 and 2000.");
            return value;
        }

        private static string ArtifactDirectory(JObject args)
        {
            string path = args.Value<string>("artifactDirectory");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new BridgeException("InvalidRequest", "An absolute artifactDirectory on the TIA host is required.");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(path);
            return path;
        }

        private static JObject FileArtifact(FileInfo file, string mediaType = "application/xml")
        {
            string hash;
            using (var stream = file.OpenRead())
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return new JObject { ["path"] = file.FullName, ["bytes"] = file.Length, ["sha256"] = hash, ["mediaType"] = mediaType };
        }

        private sealed class HardwareEntry { internal DeviceItem Item; internal string Path; }
        private sealed class BlockEntry { internal PlcBlock Item; internal string Path; internal bool NativeSystemGroup; }
        private sealed class PlcSelection { internal Project Project; internal PlcSoftware Software; internal string Path; }
        private sealed class BlockSelection { internal Project Project; internal string PlcPath; internal BlockEntry Block; }
    }
}
