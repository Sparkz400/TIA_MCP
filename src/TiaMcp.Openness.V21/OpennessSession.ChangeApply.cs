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
    // change_apply per kind: ApplyEngineeringMutation, the kind-specific writes and their readbacks.
    internal sealed partial class OpennessSession
    {
        private void ApplyEngineeringMutation(EngineeringChange change, Project project)
        {
            if (change.Kind == "hmi_engineering") { ApplyHmiEngineeringMutation(change, project); return; }
            if (change.Kind == "hmi_classic_import") { ApplyHmiEngineeringMutation(change, project); return; }
            if (change.Kind == "hmi_classic_screen_edit") { ApplyHmiEngineeringMutation(change, project); return; }
            if (change.Kind == "hmi_external_tag_create") { ApplyHmiEngineeringMutation(change, project); return; }
            if (change.Kind == "hmi_screen_create") { ApplyHmiEngineeringMutation(change, project); return; }
            if (change.Kind == "hmi_dynamization_create") { ApplyHmiEngineeringMutation(change, project); return; }
            if (change.Kind == "hmi_event_handler_create") { ApplyHmiEngineeringMutation(change, project); return; }
            if (change.Kind == "hmi_text_list_import") { ApplyHmiTextListImportMutation(change, project); return; }
            if (change.BatchMembers != null) { ApplyChangeBatch(change, project); return; }
            var payload = change.Payload;
            switch (change.Kind)
            {
                case "graphical_typed_edit":
                    ApplyTypedGraphical(change);
                    break;
                case "testsuite_application_import":
                    ApplyTestSuiteApplicationImport(change, project);
                    break;
                case "testsuite_system_import":
                    ApplyTestSuiteSystemImport(change, project);
                    break;
                case "project_simulation_enable":
                {
                    var settings = project.GetService<PlcSimulationSettingsProvider>() ?? throw new BridgeException("UnsupportedCapability", "Project has no PlcSimulationSettingsProvider service.");
                    settings.IsSimulationDuringBlockCompilationEnabled = payload.Value<bool>("enabled");
                    if (settings.IsSimulationDuringBlockCompilationEnabled != payload.Value<bool>("enabled")) throw new BridgeException("MutationVerificationFailed", "Project simulation setting did not match the requested value.");
                    break;
                }
                case "project_attributes_set":
                    ApplyAttributes(project, ChangeObject(payload, "attributes"), ProjectWriteAttributes);
                    change.Readbacks.Add(new JObject { ["role"] = "project-attributes", ["after"] = new JObject(ChangeObject(payload, "attributes").Properties().Select(p => new JProperty(p.Name, ChangeScalar(project.GetAttribute(p.Name))))) });
                    break;
                case "project_languages_set":
                    ApplyProjectLanguages(change, project);
                    break;
                case "source_upsert":
                    ApplySourceUpsert(change, payload);
                    break;
                case "block_import_xml":
                case "safety_import_xml":
                case "graph_import":
                case "graphical_operand_set":
                case "graphical_network_patch":
                case "graphical_network_insert":
                case "call_parameter_set":
                case "call_connection_set":
                    ApplyBlockXmlImport(change);
                    break;
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
                case "tag_create":
                case "constant_create":
                {
                    var plc = SelectPlc(change.Selector);
                    TagCreate(change, plc, MutationTable(change, plc), true);
                    break;
                }
                case "safety_drive_tags":
                    Startdrive(() => { ApplySafetyDriveTags(change, project); return true; });
                    break;
                case "block_number_set":
                case "block_attributes_set":
                    ApplyBlockEdit(change);
                    break;
                case "instance_db_create":
                    ApplyInstanceDbCreate(change);
                    break;
                case "group_create":
                case "group_rename":
                case "group_delete":
                    GroupEdit(change, SelectPlc(change.Selector), true);
                    break;
                case "tag_table_create":
                case "tag_table_rename":
                case "tag_table_delete":
                    ApplyTagTableLifecycle(change);
                    break;
                case "software_unit_create":
                case "software_unit_relation_set":
                case "software_unit_publication_set":
                case "software_unit_delete":
                case "software_unit_rename":
                case "software_unit_create_from":
                    SoftwareUnitEdit(change, null);
                    break;
                case "plc_alarm_text_list_import":
                case "plc_alarm_instance_text_import":
                case "plc_alarm_class_import":
                case "plc_supervision_settings_import":
                case "plc_supervision_import":
                    ApplyPlcAlarmImport(change, project);
                    break;
                case "opcua_server_interface_import":
                case "opcua_server_interface_delete":
                case "opcua_server_interface_enable":
                case "opcua_reference_namespace_import":
                case "opcua_reference_namespace_delete":
                case "opcua_role_mapping_set":
                    ApplyOpcUaChange(change);
                    break;
                case "opcua_server_settings_set":
                    ApplyOpcUaServerSettings(change);
                    break;
                case "webserver_page_set":
                    ApplyWebServerPageSet(change);
                    break;
                case "webserver_user_pages_generate":
                    ApplyWebServerUserPages(change);
                    break;
                case "umac_role_set":
                    ApplyUmacRoleSet(change, project);
                    break;
                case "type_source_upsert":
                    ApplyTypeSourceUpsert(change);
                    break;
                case "plc_type_import_xml":
                    MutationTypeGroup(change, SelectPlc(change.Selector)).Types.Import(new FileInfo(change.InputPath), ImportOptions.Override);
                    VerifyExpectedTypesExist(change, SelectPlc(change.Selector));
                    break;
                case "tag_table_import_xml":
                    RejectFIoAddresses(SelectPlc(change.Selector), TagAddressRules.TagTableAddresses(EngineeringSources.ReadXml(File.ReadAllText(change.InputPath))));
                    MutationTagGroup(change, SelectPlc(change.Selector)).TagTables.Import(new FileInfo(change.InputPath), ImportOptions.Override);
                    if (change.ObjectNames.Any(name => !MutationTagGroup(change, SelectPlc(change.Selector)).TagTables.Any(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))))
                        throw new BridgeException("ImportVerificationFailed", "Not all expected tag tables exist in the selected destination after import.");
                    break;
                case "block_delete":
                    ApplyBlockDelete(change);
                    break;
                case "block_rename":
                    ApplyBlockRename(change, payload);
                    break;
                case "block_protect":
                case "block_unprotect":
                case "block_write_protection_set":
                    ApplyBlockProtection(change);
                    break;
                case "hardware_add":
                {
                    var device = project.Devices.CreateWithItem(payload.Value<string>("typeIdentifier"), payload.Value<string>("itemName"), payload.Value<string>("deviceName"));
                    if (payload.Value<bool?>("configureScratchSecurity") == true)
                        change.ScratchSecurity = ConfigureNewScratchCpu(project, device, change.Directory, payload.Value<bool?>("failsafeRuntimeAccess") == true);
                    break;
                }
                case "hardware_plug":
                    ApplyHardwarePlug(project, payload);
                    break;
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
                    ApplyHardwareChannel(change, project);
                    break;
                case "hardware_cpu_memory_bytes":
                    ApplyCpuMemoryBytes(change, project);
                    break;
                case "hardware_cpu_parameters":
                    ApplyCpuParameters(change, project);
                    break;
                case "safety_fparameter_set":
                    change.Readbacks.Add(new JObject { ["role"] = "f-parameter-readback", ["parameters"] = FParameterSet(change, project, true) });
                    break;
                case "network_io_assign":
                    ApplyIoAssignment(change, project);
                    break;
                case "connection_create":
                case "connection_delete":
                case "connection_set":
                    ConnectionEdit(change, project, null);
                    break;
                case "hardware_port_connect":
                case "hardware_port_disconnect":
                    ApplyPortLink(change, project);
                    break;
                case "hardware_replace":
                    ApplyHardwareReplace(change, project);
                    break;
                case "hardware_transfer_area_create":
                case "hardware_transfer_area_delete":
                    ApplyTransferArea(change, project);
                    break;
                case "network_mrp_set":
                    ApplyMrp(change, project);
                    break;
                case "hardware_shared_device_access":
                    ApplySharedDeviceAccess(change, project);
                    break;
                case "cax_import":
                    ApplyCaxImport(change, project);
                    break;
                case "hardware_delete":
                {
                    var item = ChangeHardware(project, payload).Item;
                    EnsureHardwareDeleteEligible(item);
                    item.Delete();
                    break;
                }
                case "network_configure":
                    ApplyNetworkConfigure(project, payload);
                    break;
                case "to_create":
                    ApplyTechnologyCreate(change, payload);
                    break;
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
                case "to_connect":
                case "to_disconnect":
                    ApplyTechnologyConnection(change, project);
                    break;
                case "to_child_create":
                    ApplyTechnologyChildCreate(change);
                    break;
                case "drive_parameter_set":
                    change.Readbacks.Add(new JObject { ["role"] = "drive-parameter-readback", ["parameters"] = Startdrive(() => DriveParameterSet(change, project, true)) });
                    break;
                case "drive_telegram_set":
                    Startdrive(() => DriveTelegramSet(change, project, true));
                    break;
                case "drive_motor_configure":
                    change.Readbacks.Add(new JObject { ["role"] = "drive-motor-readback", ["motor"] = Startdrive(() => DriveMotorConfigure(change, project, true)) });
                    break;
                case "db_snapshot_create":
                case "db_snapshot_load_actual":
                    ApplyDbSnapshotChange(change);
                    break;
                case "simulation_download_online":
                    ApplySimulationDownloadOnline(change);
                    break;
                case "vci_workspace_create":
                case "vci_workspace_delete":
                case "vci_mapping_add":
                case "vci_mapping_remove":
                case "vci_synchronize":
                case "vci_conflict_resolve":
                    ApplyVciChange(change, project);
                    break;
                case "certificate_create":
                case "certificate_import":
                case "certificate_assign":
                case "certificate_delete":
                    ApplyCertificateChange(change, project);
                    break;
            }
        }

        private void ApplySourceUpsert(EngineeringChange change, JObject payload)
        {
            var plc = SelectPlc(change.Selector);
            VerifyBlockTargets(change, plc, false);
            RejectFIoBlockContent(plc, File.ReadAllText(change.InputPath), false);
            if (payload.Value<string>("language")?.ToUpperInvariant() == "SIMATIC_SD")
            {
                MutationBlockGroup(change, plc).Blocks.ImportFromDocuments(new DirectoryInfo(change.Directory), Path.GetFileNameWithoutExtension(change.InputPath), ImportDocumentOptions.None);
                change.Stages.Add("simatic_sd_imported");
                VerifyExpectedBlocksExist(change, plc);
                return;
            }
            var source = MutationSources(change, plc).ExternalSources.CreateFromFile("TiaMcp_" + change.Id, change.InputPath);
            change.Stages.Add("external_source_imported");
            var group = MutationBlockGroup(change, plc);
            if (group is PlcBlockUserGroup userGroup) source.GenerateBlocksFromSource(userGroup, GenerateBlockOption.None);
            else source.GenerateBlocksFromSource();
            VerifyExpectedBlocksExist(change, plc);
        }

        private void ApplyBlockXmlImport(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            bool safety = change.Kind == "safety_import_xml";
            VerifyBlockTargets(change, plc, safety);
            if (safety) EnsureSafetyWritable(plc.Software);
            else RejectFIoBlockContent(plc, File.ReadAllText(change.InputPath), true);
            MutationBlockGroup(change, plc).Blocks.Import(new FileInfo(change.InputPath), ImportOptions.Override);
            VerifyExpectedBlocksExist(change, plc);
            if (safety) change.SafetyAfter = ReadSafety(plc.Software);
        }

        // Content readback of block_import_xml after compile (an imported block is exported reliably only once it is consistent):
        // every supplied block is exported again without DocumentInfo and compared by EngineeringSources.ImportedBlockDifference.
        // Equal blocks record block_import_content_verified (readbackVerified done). A difference, or an export TIA refuses, is
        // recorded in the readback row and leaves the milestone notRecorded; it never fails a change that TIA imported and compiled.
        private void ReadBackImportedBlocks(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var supplied = EngineeringSources.ReadXml(File.ReadAllText(change.InputPath)).Root.Elements()
                .Where(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)).ToList();
            var rows = new JArray();
            bool equal = supplied.Count > 0;
            for (int index = 0; index < supplied.Count; index++)
            {
                string name = supplied[index].Element("AttributeList")?.Element("Name")?.Value;
                var row = new JObject { ["block"] = name };
                try
                {
                    string readback = Path.Combine(change.Directory, "after-import-" + index + ".xml");
                    FindBlockByName(plc, name, true, change).Item.Export(new FileInfo(readback), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    row["artifact"] = ChangeArtifact(readback);
                    var exported = EngineeringSources.ReadXml(File.ReadAllText(readback)).Root.Elements()
                        .FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
                    string difference = exported == null ? "The export holds no block." : EngineeringSources.ImportedBlockDifference(supplied[index], exported);
                    row["equal"] = difference == null;
                    if (difference != null) row["firstDifference"] = difference;
                }
                catch (Exception error) when (error is EngineeringException || error is IOException || error is System.Xml.XmlException || error is BridgeException)
                {
                    row["equal"] = false;
                    row["exportFailure"] = error.Message;
                }
                equal &= row.Value<bool>("equal");
                rows.Add(row);
            }
            change.Readbacks.Add(new JObject { ["role"] = "block-import-content", ["blocks"] = rows, ["equal"] = equal,
                ["rule"] = "Export provenance (DocumentInfo), empty multilingual texts, formatting, attribute order and ID numbering are ignored; everything else must be equal." });
            if (equal) change.Stages.Add("block_import_content_verified");
            WriteChangeJournal(change);
        }

        private void ApplyTypeSourceUpsert(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var source = MutationSources(change, plc).ExternalSources.CreateFromFile("TiaMcp_" + change.Id, change.InputPath);
            var group = MutationTypeGroup(change, plc);
            if (group is PlcTypeUserGroup userGroup) source.GenerateBlocksFromSource(userGroup, GenerateBlockOption.None);
            else source.GenerateBlocksFromSource();
            VerifyExpectedTypesExist(change, plc);
        }

        private void ApplyBlockDelete(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            VerifyBlockTargets(change, plc, false);
            var target = FindBlockByName(plc, change.BlockNames[0], true, change);
            CheckDeleteDependencies(change, plc, target.Item, target.Item.Name);
            target.Item.Delete();
            if (FindBlockByName(plc, change.BlockNames[0], false, change) != null) throw new BridgeException("MutationVerificationFailed", "Deleted block still exists.");
        }

        private void ApplyBlockRename(EngineeringChange change, JObject payload)
        {
            var plc = SelectPlc(change.Selector);
            VerifyBlockTargets(change, plc, false);
            FindBlockByName(plc, change.BlockNames[0], true, change).Item.Name = payload.Value<string>("newName");
            if (FindBlockByName(plc, change.BlockNames[0], false, change) != null || FindBlockByName(plc, payload.Value<string>("newName"), false, change) == null)
                throw new BridgeException("MutationVerificationFailed", "Block rename did not produce the expected names.");
        }

        private void ApplyHardwarePlug(Project project, JObject payload)
        {
            var item = ChangeHardware(project, payload).Item;
            string identifier = payload.Value<string>("typeIdentifier");
            string name = payload.Value<string>("name");
            int position = payload.Value<int>("positionNumber");
            if (!item.CanPlugNew(identifier, name, position)) throw new BridgeException("IncompatibleHardware", "Requested plug position is no longer available.");
            item.PlugNew(identifier, name, position);
        }

        private void ApplyNetworkConfigure(Project project, JObject payload)
        {
            var node = ChangeNode(project, payload);
            PrepareAttributes(node, payload["attributes"] as JObject ?? new JObject(), NetworkWriteAttributes);
            if (payload.Value<bool?>("disconnect") == true) node.DisconnectFromSubnet();
            string subnetName = payload.Value<string>("subnetName");
            if (subnetName != null)
            {
                var subnet = project.Subnets.FirstOrDefault(s => string.Equals(s.Name, subnetName, StringComparison.OrdinalIgnoreCase));
                if (subnet == null) node.CreateAndConnectToSubnet(subnetName);
                // TIA refuses to reconnect a node to the subnet it is already on; the preview reports no subnet change.
                else if (!Equals(node.ConnectedSubnet, subnet)) node.ConnectToSubnet(subnet);
            }
            ApplyAttributes(node, payload["attributes"] as JObject ?? new JObject(), NetworkWriteAttributes);
        }

        private void ApplyTechnologyCreate(EngineeringChange change, JObject payload)
        {
            var plc = SelectPlc(change.Selector);
            if (FindTechnologyEntry(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", new JObject { ["name"] = payload["name"].DeepClone() }, false) != null) throw new BridgeException("ObjectExists", "Technology object already exists.");
            AdmitTechnologyVersion(plc, payload);
            var created = plc.Software.TechnologicalObjectGroup.TechnologicalObjects.Create(payload.Value<string>("name"), payload.Value<string>("typeIdentifier"), new Version(payload.Value<string>("version")));
            if (payload["parameters"] != null) ApplyTechnologyParameters(created, ChangeObject(payload, "parameters"));
        }

        private void CaptureTechnologyEvidence(EngineeringChange change)
        {
            if (!new[] { "to_create", "to_parameters", "to_connect", "to_disconnect", "to_child_create" }.Contains(change.Kind)) return;
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
    }
}
