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
    // change_prepare per kind: PrepareEngineeringMutation and the kind-specific checks and previews it calls.
    internal sealed partial class OpennessSession
    {
        private JObject PrepareEngineeringMutation(EngineeringChange change, Project project)
        {
            if (change.Kind == "hmi_engineering") return PrepareHmiEngineeringMutation(change, project);
            if (change.Kind == "hmi_classic_import") return PrepareHmiEngineeringMutation(change, project);
            if (change.Kind == "hmi_classic_screen_edit") return PrepareHmiEngineeringMutation(change, project);
            if (change.Kind == "hmi_external_tag_create") return PrepareHmiEngineeringMutation(change, project);
            if (change.Kind == "hmi_screen_create") return PrepareHmiEngineeringMutation(change, project);
            if (change.Kind == "hmi_dynamization_create") return PrepareHmiEngineeringMutation(change, project);
            if (change.Kind == "hmi_event_handler_create") return PrepareHmiEngineeringMutation(change, project);
            if (change.Kind == "hmi_text_list_import") return PrepareHmiTextListImportMutation(change, project);
            if (ChangeBatchRules.IsBatch(change.Kind, change.Payload)) return PrepareChangeBatch(change, project);
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
                case "device_compile":
                case "project_compile":
                case "hmi_compile":
                    PrepareCompileOnly(change, project, preview);
                    break;
                case "graphical_typed_edit":
                    PrepareTypedGraphical(change, preview);
                    break;
                case "testsuite_application_import":
                    PrepareTestSuiteApplicationImport(change, project, preview);
                    break;
                case "testsuite_system_import":
                    PrepareTestSuiteSystemImport(change, project, preview);
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
                case "project_attributes_set":
                    change.AttributeTarget = project;
                    preview["attributeChanges"] = PrepareAttributes(project, ChangeObject(payload, "attributes"), ProjectWriteAttributes);
                    effects.Add(new JObject { ["projectAttributes"] = new JArray(ChangeObject(payload, "attributes").Properties().Select(p => p.Name)) });
                    break;
                case "project_languages_set":
                    effects.Add(new JObject { ["languageSettings"] = new JObject { ["before"] = ProjectLanguageView(project.LanguageSettings), ["after"] = PlanProjectLanguages(project, payload, out _, out _, out _, out _) } });
                    ((JArray)preview["warnings"]).Add("Removing an active language can discard project texts in that language; the recovery archive is the only way back.");
                    break;
                case "source_upsert":
                    PrepareSourceUpsert(change, payload, preview, effects);
                    break;
                case "block_import_xml":
                case "safety_import_xml":
                case "graph_import":
                    PrepareBlockXmlImport(change, payload, preview, effects);
                    break;
                case "graphical_operand_set":
                case "graphical_network_patch":
                case "graphical_network_insert":
                case "call_parameter_set":
                case "call_connection_set":
                    PrepareGraphicalXmlEdit(change, payload, preview, effects);
                    break;
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
                case "tag_create":
                case "constant_create":
                    PrepareTagCreate(change, preview);
                    break;
                case "block_number_set":
                case "block_attributes_set":
                    PrepareBlockEdit(change, preview);
                    break;
                case "instance_db_create":
                    PrepareInstanceDbCreate(change, preview);
                    break;
                case "group_create":
                case "group_rename":
                case "group_delete":
                    PrepareGroupEdit(change, preview);
                    break;
                case "tag_table_create":
                case "tag_table_rename":
                case "tag_table_delete":
                    PrepareTagTableLifecycle(change, preview);
                    break;
                case "software_unit_create":
                case "software_unit_relation_set":
                case "software_unit_publication_set":
                case "software_unit_delete":
                case "software_unit_rename":
                case "software_unit_create_from":
                    SoftwareUnitEdit(change, preview);
                    break;
                case "plc_alarm_text_list_import":
                case "plc_alarm_instance_text_import":
                case "plc_alarm_class_import":
                case "plc_supervision_settings_import":
                case "plc_supervision_import":
                    PreparePlcAlarmImport(change, project, preview);
                    break;
                case "opcua_server_interface_import":
                case "opcua_server_interface_delete":
                case "opcua_server_interface_enable":
                case "opcua_reference_namespace_import":
                case "opcua_reference_namespace_delete":
                case "opcua_role_mapping_set":
                    PrepareOpcUaChange(change, preview);
                    break;
                case "opcua_server_settings_set":
                    PrepareOpcUaServerSettings(change, preview);
                    break;
                case "webserver_page_set":
                    PrepareWebServerPageSet(change, preview);
                    break;
                case "webserver_user_pages_generate":
                    PrepareWebServerUserPages(change, preview);
                    break;
                case "umac_role_set":
                    PrepareUmacRoleSet(change, project, preview);
                    break;
                case "type_source_upsert":
                    PrepareTypeSourceUpsert(change, payload, effects);
                    break;
                case "plc_type_import_xml":
                case "tag_table_import_xml":
                    PrepareObjectXmlImport(change, project, payload, preview, effects);
                    break;
                case "block_delete":
                case "block_rename":
                    PrepareBlockDeleteOrRename(change, payload, preview, effects);
                    break;
                case "block_protect":
                case "block_unprotect":
                case "block_write_protection_set":
                    PrepareBlockProtection(change, preview);
                    break;
                case "hardware_add":
                    PrepareHardwareAdd(project, payload, effects);
                    break;
                case "hardware_address_set":
                    PrepareHardwareAddress(change, project, preview);
                    break;
                case "hardware_move":
                    PrepareHardwareMove(change, project, preview);
                    break;
                case "hardware_channel_attributes":
                    PrepareHardwareChannel(change, project, preview, out _);
                    break;
                case "hardware_cpu_memory_bytes":
                    PrepareCpuMemoryBytes(change, project, preview);
                    break;
                case "hardware_cpu_parameters":
                    PrepareCpuParameters(change, project, preview);
                    break;
                case "safety_drive_tags":
                {
                    var plc = SelectPlc(change.Selector);
                    preview["safetyDriveTags"] = Startdrive(() => SafetyDriveTagPlan(change, project));
                    preview["safetyBefore"] = ReadSafety(plc.Software);
                    effects.Add(new JObject { ["plcPath"] = plc.Path, ["action"] = change.Kind });
                    ((JArray)preview["warnings"]).Add("Creates PROFIsafe safety tags, which changes the safety program signatures after the F-compile. Nothing here validates, accepts or approves the safety program or the drive's Safety Integrated configuration.");
                    break;
                }
                case "safety_fparameter_set":
                    PrepareSafetyFParameterSet(change, project, preview, effects);
                    break;
                case "network_io_assign":
                    PrepareIoAssignment(change, project, preview, out _, out _);
                    break;
                case "connection_create":
                case "connection_delete":
                case "connection_set":
                    ConnectionEdit(change, project, preview);
                    break;
                case "hardware_port_connect":
                case "hardware_port_disconnect":
                    PreparePortLink(change, project, preview);
                    break;
                case "hardware_replace":
                    PrepareHardwareReplace(change, project, preview);
                    break;
                case "hardware_transfer_area_create":
                case "hardware_transfer_area_delete":
                    PrepareTransferArea(change, project, preview);
                    break;
                case "network_mrp_set":
                    PrepareMrp(change, project, preview, out _);
                    break;
                case "hardware_shared_device_access":
                    PrepareSharedDeviceAccess(change, project, preview, out _, out _);
                    break;
                case "cax_import":
                    PrepareCaxImport(change, project, preview);
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
                    HardwareEngineeringRules.RequireProfileWritable(ReadItemProfile(item.Item, out string profilePath), profilePath, null, ChangeObject(payload, "attributes").Properties().Select(property => property.Name));
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
                    PrepareNetworkConfigure(project, payload, preview, effects);
                    break;
                case "to_create":
                    PrepareTechnologyCreate(change, payload, preview, effects);
                    break;
                case "to_child_create":
                    PrepareTechnologyChildCreate(change, effects);
                    break;
                case "to_parameters":
                case "to_delete":
                case "to_connect":
                case "to_disconnect":
                    PrepareTechnologyEdit(change, project, payload, preview, effects);
                    break;
                case "db_snapshot_create":
                case "db_snapshot_load_actual":
                    PrepareDbSnapshotChange(change, preview);
                    break;
                case "simulation_download_online":
                    PrepareSimulationDownloadOnline(change, preview);
                    break;
                case "vci_workspace_create":
                case "vci_workspace_delete":
                case "vci_mapping_add":
                case "vci_mapping_remove":
                case "vci_synchronize":
                case "vci_conflict_resolve":
                    PrepareVciChange(change, project, preview);
                    break;
                case "drive_parameter_set":
                    preview["parameterChanges"] = Startdrive(() => DriveParameterSet(change, project, false));
                    effects.Add(new JObject { ["hardwarePath"] = payload["hardwarePath"].DeepClone(), [DriveObjectRules.Handle(payload)] = payload[DriveObjectRules.Handle(payload)].DeepClone(), ["action"] = change.Kind });
                    ((JArray)preview["warnings"]).Add("Startdrive may recalculate dependent parameters. Apply reads the listed parameters back and records every other parameter whose value changed (readbacks role drive-parameter-side-effects). A value outside the parameter's declared limits refuses before anything is written. Offline project only: no online write, download or RAM-to-ROM.");
                    break;
                case "drive_motor_configure":
                    preview["motorChange"] = Startdrive(() => DriveMotorConfigure(change, project, false));
                    effects.Add(new JObject { ["hardwarePath"] = payload["hardwarePath"].DeepClone(), [DriveObjectRules.Handle(payload)] = payload[DriveObjectRules.Handle(payload)].DeepClone(),
                        ["driveDataSet"] = payload["driveDataSet"].DeepClone(), ["action"] = change.Kind });
                    ((JArray)preview["warnings"]).Add("Projects a motor without a Siemens code number into one drive data set through Startdrive's HardwareProjection: an optional motor type, then the listed nameplate entries, then ProjectMotorConfiguration. Startdrive recalculates dependent parameters; apply reads the listed entries back and records every other parameter whose value changed (readbacks role drive-motor-side-effects). Offline project only: no motor identification, online write, download or RAM-to-ROM.");
                    break;
                case "drive_telegram_set":
                    preview["telegramChange"] = Startdrive(() => DriveTelegramSet(change, project, false));
                    effects.Add(new JObject { ["hardwarePath"] = payload["hardwarePath"].DeepClone(), [DriveObjectRules.Handle(payload)] = payload[DriveObjectRules.Handle(payload)].DeepClone(),
                        ["operation"] = payload["operation"].DeepClone(), ["telegramType"] = payload["telegramType"].DeepClone(), ["action"] = change.Kind });
                    ((JArray)preview["warnings"]).Add("The telegram change moves PLC-side I/O addresses. Apply reads back every telegram row of the drive object and reports the address shift; a collateral telegram change or an address overlap fails after the write and requires checkpoint recovery. Technology-object connections, PLC tags and program references to the telegram are not rebound; the mandatory device compile validates the configuration. Offline project only: no download.");
                    break;
                case "certificate_create":
                case "certificate_import":
                case "certificate_assign":
                case "certificate_delete":
                    PrepareCertificateChange(change, project, preview);
                    break;
                default: throw new BridgeException("UnsupportedChange", "Unknown or unsupported change kind: " + change.Kind);
            }
            PrepareHardwarePreservation(change, project, preview);
            return preview;
        }

        private void PrepareSourceUpsert(EngineeringChange change, JObject payload, JObject preview, JArray effects)
        {
            var plc = SelectPlc(change.Selector);
            string language = ChangeText(payload, "language", 16).ToUpperInvariant();
            if (language != "SCL" && language != "STL" && language != "AWL" && language != "SIMATIC_SD") throw new BridgeException("UnsupportedLanguage", "Source writes accept SCL, STL/AWL or explicit new-block SIMATIC_SD documents.");
            string source = ChangeText(payload, "source", 2 * 1024 * 1024);
            RejectFIoBlockContent(plc, source, false);
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
            var findOld = CreateBlockFinder(plc, change);
            foreach (var name in expected)
            {
                var old = findOld(name, false);
                if (language == "SIMATIC_SD" && old != null)
                    throw new BridgeException("TargetAlreadyExists", "SIMATIC_SD source import is limited to explicitly new blocks; use typed edits for existing graphical blocks.");
                if (old != null && !(old.Item is DataBlock) && old.Item.ProgrammingLanguage.ToString() != nativeLanguage)
                    throw new BridgeException("LanguageConversionNotSupported", "Source update must preserve the existing language of " + name + ". Use a dedicated conversion workflow if conversion is intended.");
            }
            change.InputPath = Path.Combine(change.Directory, language == "SIMATIC_SD" ? "requested.s7dcl" : language == "SCL" ? "requested.scl" : "requested.awl");
            File.WriteAllText(change.InputPath, source, new UTF8Encoding(true));
            change.InputHash = ChangeFileHash(change.InputPath);
            preview["language"] = language;
        }

        private void PrepareBlockXmlImport(EngineeringChange change, JObject payload, JObject preview, JArray effects)
        {
            var plc = SelectPlc(change.Selector);
            string xml = ChangeText(payload, "xml", 2 * 1024 * 1024);
            var document = EngineeringSources.ReadXml(xml);
            var targets = EngineeringSources.BlockDeclarations(document);
            var expected = ChangeNames(payload, "expectedBlockNames");
            if (!new HashSet<string>(targets.Select(t => t.Name), StringComparer.Ordinal).SetEquals(expected)) throw new BridgeException("XmlScopeMismatch", "Top-level XML blocks must exactly match expectedBlockNames.");
            if (change.Kind == "graph_import" && (targets.Length != 1 || targets[0].Language != "GRAPH")) throw new BridgeException("UnsupportedLanguage", "graph_import accepts exactly one GRAPH block per document.");
            bool safety = change.Kind == "safety_import_xml";
            if (targets.Any(target => target.IsSafety != safety)) throw new BridgeException("SafetyBoundary", "Standard and safety imports use separate workflows, and cannot be mixed or converted.");
            if (!safety) RejectFIoBlockContent(plc, xml, true);
            if (safety && targets.Any(target => target.Language != "F_LAD" && target.Language != "F_FBD" && target.Language != "F_DB"))
                throw new BridgeException("UnsupportedSafetyBlock", "Only user F-LAD, F-FBD and F-DB XML is accepted; F-system and generated call languages are excluded.");
            if (safety) { RequireChangeBool(payload, "safetyAcknowledged"); EnsureSafetyWritable(plc.Software); preview["safetyBefore"] = ReadSafety(plc.Software); }
            change.BlockNames = expected;
            change.Namespace = ImportNamespace(document);
            PrepareMutationScope(change, plc, "block", change.BlockNames);
            PrepareBlockTargets(change, plc, safety, false, effects);
            var findOld = CreateBlockFinder(plc, change);
            foreach (var target in targets)
            {
                var old = findOld(target.Name, false);
                if (old != null && old.Item.ProgrammingLanguage.ToString() != target.Language)
                    throw new BridgeException("LanguageConversionNotSupported", "XML update must retain the existing block language: " + target.Name);
            }
            ValidateXmlBlockNumbers(document, plc, change);
            change.InputPath = Path.Combine(change.Directory, "requested.xml");
            File.WriteAllText(change.InputPath, xml, new UTF8Encoding(true));
            change.InputHash = ChangeFileHash(change.InputPath);
        }

        private void PrepareGraphicalXmlEdit(EngineeringChange change, JObject payload, JObject preview, JArray effects)
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
                    {
                        var originalUids = new HashSet<string>(document.Descendants("SW.Blocks.CompileUnit").Where(e => (string)e.Attribute("ID") == networkId)
                            .Descendants().Where(e => e.Name.LocalName != "IdentCon" && e.Name.LocalName != "NameCon").Attributes("UId").Select(a => a.Value), StringComparer.Ordinal);
                        EngineeringSources.PatchGraphicalNetwork(document, networkId, ChangeStringArray(payload, "upsertXml", 500, 1024 * 1024, true), ChangeStringArray(payload, "removeUids", 500, 32, true));
                        // Opt-in: predict V21's renumbering so added/removed nodes can read back exactly; the readback check is unchanged.
                        if (payload.Value<bool?>("renumberUids") == true)
                        {
                            preview["uidRenumbering"] = GraphicalUidRenumbering.Predict(document, networkId, originalUids);
                            ((JArray)preview["warnings"]).Add("renumberUids applied V21's predicted UId renumbering (uidRenumbering). It is not natively qualified for patches: a wrong prediction fails closed with NativePreservationMismatch after apply and needs checkpoint recovery. Try it on a project_copy first.");
                        }
                    }
                    else if (change.Kind == "call_parameter_set")
                        EngineeringSources.SetCallParameterOperand(document, networkId, ChangeText(payload, "callUid", 32), ChangeText(payload, "parameterName", 128), ChangeStringArray(payload, "components", 128, 128));
                    else EngineeringSources.SetGraphicalCallConnection(document, networkId, ChangeText(payload, "callUid", 32), ChangeText(payload, "parameterName", 128), ChangeText(payload, "targetAccessUid", 32));
                }
            }
            catch (ArgumentException ex) { throw new BridgeException("InvalidGraphicalEdit", ex.Message); }
            catch (InvalidOperationException ex) { throw new BridgeException("UnsupportedGraphicalEdit", ex.Message); }
            preview["edit"] = payload.DeepClone();
            EngineeringSources.BlockDeclarations(document);
            RejectFIoBlockContent(plc, document.ToString(SaveOptions.DisableFormatting), true);
            change.InputPath = Path.Combine(change.Directory, "requested.xml");
            File.WriteAllText(change.InputPath, document.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(true));
            change.InputHash = ChangeFileHash(change.InputPath);
            ((JArray)preview["warnings"]).Add("Only explicitly selected XML nodes change in the prepared artifact. Untouched interfaces, comments, networks and unknown elements are preserved structurally. Native import/compilation remains authoritative for instruction and pin compatibility.");
        }

        private void PrepareTypeSourceUpsert(EngineeringChange change, JObject payload, JArray effects)
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
        }

        private void PrepareObjectXmlImport(EngineeringChange change, Project project, JObject payload, JObject preview, JArray effects)
        {
            var plc = SelectPlc(change.Selector);
            bool types = change.Kind == "plc_type_import_xml";
            // Row form of a tag-table import: {tagTableName, tags:[...]} instead of {xml, expectedTableNames}.
            bool rows = !types && payload["tags"] != null;
            if (rows && (payload["xml"] != null || payload["expectedTableNames"] != null))
                throw new BridgeException("InvalidRequest", "Give either xml with expectedTableNames, or tagTableName with tags.");
            string xml = rows ? null : ChangeText(payload, "xml", 2 * 1024 * 1024);
            change.ObjectNames = rows ? new[] { ChangeText(payload, "tagTableName", 128) } : ChangeNames(payload, types ? "expectedTypeNames" : "expectedTableNames");
            if (rows)
            {
                change.Namespace = "";
                PrepareMutationScope(change, plc, "tagTable", change.ObjectNames);
                xml = TagRowsXml(change, plc, project, payload, effects);
            }
            var document = EngineeringSources.ReadXml(xml);
            string elementName = types ? "SW.Types.PlcStruct" : "SW.Tags.PlcTagTable";
            var declarations = EngineeringSources.NamedXmlObjects(document, elementName);
            change.Namespace = types ? ImportNamespace(document) : "";
            if (!new HashSet<string>(declarations, StringComparer.Ordinal).SetEquals(change.ObjectNames)) throw new BridgeException("XmlScopeMismatch", "Imported object names must exactly match the expected set.");
            if (!rows) PrepareMutationScope(change, plc, types ? "type" : "tagTable", change.ObjectNames);
            if (types) PrepareTypeTargets(change, plc, effects);
            else
            {
                TagTableImportRules.Validate(document);
                RejectFIoAddresses(plc, TagAddressRules.TagTableAddresses(document));
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
                    if (table.Tags.Any(tag => tag.IsSafety))
                        throw new BridgeException("SafetyBoundary", "Generic tag-table import cannot overwrite a table containing native safety tags: " + table.Name);
                    string path = Path.Combine(change.Directory, "before-tags-" + Guid.NewGuid().ToString("N") + ".xml");
                    table.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    TagTableImportRules.RequireExistingMembers(TagTableImportRules.Tables(EngineeringSources.ReadXml(File.ReadAllText(path))).Single(),
                        TagTableImportRules.Tables(document).Single(t => string.Equals((string)t.Element("AttributeList").Element("Name"), table.Name, StringComparison.OrdinalIgnoreCase)));
                    change.Backups.Add(new JObject { ["tableName"] = table.Name, ["artifact"] = ChangeArtifact(path) });
                }
                effects.Add(new JObject { ["importTagTables"] = new JArray(change.ObjectNames) });
                // Advisory only: findings or an unreadable hardware configuration never refuse the import.
                var crossCheck = TagTableRowRules.CrossCheck(document, () => IoAddressRanges(plc));
                preview["hardwareCrossCheck"] = crossCheck;
                if (TagTableRowRules.Warning(crossCheck) is string crossCheckWarning) ((JArray)preview["warnings"]).Add(crossCheckWarning);
            }
            change.InputPath = Path.Combine(change.Directory, "requested.xml");
            File.WriteAllText(change.InputPath, xml, new UTF8Encoding(true));
            change.InputHash = ChangeFileHash(change.InputPath);
        }

        private void PrepareBlockDeleteOrRename(EngineeringChange change, JObject payload, JObject preview, JArray effects)
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
        }

        private void PrepareHardwareAdd(Project project, JObject payload, JArray effects)
        {
            string identifier = ChangeText(payload, "typeIdentifier", 256);
            ValidateCatalogIdentifier(identifier);
            string name = ChangeText(payload, "deviceName", 128);
            if (EngineeringDevices(project).Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("ObjectExists", "Device name already exists.");
            ChangeText(payload, "itemName", 128);
            effects.Add(new JObject { ["createDevice"] = name, ["typeIdentifier"] = identifier });
            bool failsafe = payload.Value<bool?>("failsafeRuntimeAccess") == true;
            if (failsafe && payload.Value<bool?>("configureScratchSecurity") != true)
                throw new BridgeException("InvalidRequest", "failsafeRuntimeAccess applies only together with configureScratchSecurity.");
            if (payload.Value<bool?>("configureScratchSecurity") == true)
            {
                EnsureCurrentScratchProject(project);
                effects.Add(new JObject { ["configureNewCpuSecurity"] = "Protect the new scratch CPU configuration and provision a new password-authenticated runtime user/role with standard full access scoped to that new device. Generated credentials are stored under Windows DPAPI CurrentUser. No existing protection or anonymous access is changed." });
                if (failsafe)
                    effects.Add(new JObject { ["failsafeRuntimeAccess"] = "The generated role receives the F-CPU runtime right Full access including fail-safe instead of Full access, for this new scratch device only." });
            }
        }

        private void PrepareSafetyFParameterSet(EngineeringChange change, Project project, JObject preview, JArray effects)
        {
            var plc = SelectPlc(change.Selector);
            preview["parameterChanges"] = FParameterSet(change, project, false);
            if (((JArray)preview["parameterChanges"]).OfType<JObject>().Any(row => row["fDestinationUsedOnOtherFSource"] != null))
                ((JArray)preview["warnings"]).Add("An F-destination address is also used by an F-I/O on another F-source (see fDestinationUsedOnOtherFSource). PROFIsafe addresses should be unique across the project; the F-compile reports a real duplicate.");
#if SAFETY_FPARAMETER_DISCOVERY
            if (change.Selector.Value<bool?>("fParameterDiscovery") == true)
            {
                preview["fParameterDiscovery"] = true;
                ((JArray)preview["warnings"]).Add("F-parameter discovery Host: declared-writable F-parameters outside the qualified allowlist are admitted for qualification runs only.");
            }
#endif
            preview["safetyBefore"] = ReadSafety(plc.Software);
            effects.Add(new JObject { ["plcPath"] = plc.Path, ["action"] = change.Kind });
            ((JArray)preview["warnings"]).Add("Changing F-parameters changes the F-parameter signature of each F-I/O and the safety program signatures. Nothing here validates, accepts or approves the safety program, assigns a PROFIsafe address to the physical F-I/O, or downloads anything; that stays with the authorized safety workflow.");
        }

        private void PrepareNetworkConfigure(Project project, JObject payload, JObject preview, JArray effects)
        {
            var node = ChangeNode(project, payload);
            // No AttributeTarget: the fingerprint already holds every node, so a fresh session can recompute the outcome.
            var attributes = payload["attributes"] as JObject ?? new JObject();
            foreach (var name in new[] { "Address", "SubnetMask", "RouterAddress" })
                if (attributes[name] != null && (!IPAddress.TryParse(attributes.Value<string>(name), out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
                    throw new BridgeException("InvalidRequest", name + " must be an IPv4 address.");
            preview["attributeChanges"] = PrepareAttributes(node, attributes, NetworkWriteAttributes);
            if (payload["subnetName"] != null) ChangeText(payload, "subnetName", 128);
            if (payload.Value<bool?>("disconnect") == true && payload["subnetName"] != null) throw new BridgeException("InvalidRequest", "Choose subnetName or disconnect, not both.");
            effects.Add(new JObject { ["nodeName"] = node.Name, ["connectedSubnetBefore"] = node.ConnectedSubnet?.Name,
                ["connectedSubnetAfter"] = payload["subnetName"]?.DeepClone(), ["disconnect"] = payload.Value<bool?>("disconnect") ?? false });
        }

        private void PrepareTechnologyCreate(EngineeringChange change, JObject payload, JObject preview, JArray effects)
        {
            var plc = SelectPlc(change.Selector);
            string name = ChangeText(payload, "name", 128);
            if (payload["technologyPath"] != null) throw new BridgeException("InvalidRequest", "to_create creates in the PLC root technology group; technologyPath is only supported for existing objects.");
            if (FindTechnologyEntry(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", new JObject { ["name"] = name }, false) != null) throw new BridgeException("ObjectExists", "Technology object already exists.");
            ChangeText(payload, "typeIdentifier", 128);
            if (!Version.TryParse(ChangeText(payload, "version", 32), out _)) throw new BridgeException("InvalidRequest", "version must be a dotted numeric version supported by this CPU.");
            preview["technologyProfile"] = AdmitTechnologyVersion(plc, payload);
            if (preview["technologyProfile"].Value<string>("outcome") == "unprofiled")
                ((JArray)preview["warnings"]).Add("No recorded technology version profile covers this CPU/type/version; unprofiledAcknowledged lets Siemens validate it at creation, and a native refusal requires checkpoint recovery.");
            // FindTechnologyEntry above traversed the same tree completely, so the type list is complete.
            if (TechnologyVersionProfileRules.FirstMotionObject(payload.Value<string>("typeIdentifier"),
                    TechnologyObjects(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", new ScanBudget(50000)).Select(entry => entry.Item.OfSystemLibElement).ToArray()))
            {
                preview["firstMotionObject"] = true;
                ((JArray)preview["warnings"]).Add("This is the PLC's first motion technology object; it adds the know-how protected MC_Servo and MC_Interpolator OBs, so change_apply requires compile=true (CompileRequired otherwise).");
            }
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
        }

        private void PrepareTechnologyEdit(EngineeringChange change, Project project, JObject payload, JObject preview, JArray effects)
        {
            var plc = SelectPlc(change.Selector);
            var entry = BindTechnologySelection(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", payload);
            var technology = entry.Item;
            string beforePath = Path.Combine(change.Directory, "before-to.xml");
            // V21 exports no inconsistent object (native, 30 October 2026), and a real axis or encoder stays inconsistent until it is connected.
            if (technology.IsConsistent)
            {
                technology.Export(new FileInfo(beforePath), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                change.Backups.Add(ChangeArtifact(beforePath));
            }
            else ((JArray)preview["warnings"]).Add("The technology object is inconsistent (not compiled) and V21 cannot export it, so no XML backup precedes this write; the project checkpoint archive is the recovery.");
            if (change.Kind == "to_parameters")
            {
                preview["parameterChanges"] = PrepareTechnologyParameters(technology, ChangeObject(payload, "parameters"), out _);
            }
            else if (change.Kind != "to_delete") PrepareTechnologyConnection(change, project, plc, entry, preview);
            effects.Add(new JObject { ["technologyObject"] = technology.Name, ["technologyPath"] = entry.Path, ["action"] = change.Kind });
        }
    }
}
