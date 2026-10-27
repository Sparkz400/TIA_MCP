using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject TargetCapabilities(JObject args)
        {
            if ((args.Value<string>("kind") ?? "").StartsWith("hmi", StringComparison.Ordinal) || args["hmiPath"] != null)
                return HmiCapabilities(args);
            var selected = ResolveObjectTarget(CapabilitySelectors(args));
            var project = SelectProject(args);
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var metadata = CapabilityMetadata(selected.Target, budget, warnings);
            var probes = new JArray();
            var features = new JArray();
            var protection = new JObject();
            string kind = selected.Identity.Value<string>("kind");
            bool safetyScope = selected.Scope?.Kind == "safetyUnit";
            bool generated = ObjectIdentity.IsGeneratedPath(selected.Identity.Value<string>("objectPath"));
            var directApi = ApiObservation.Available("The selected object exposes the native typed engineering API; operation preconditions were not exercised.");
            AddTargetFeature(features, "target_capabilities", ApiObservation.Available("Target identity and read-only capability metadata were observed in this call."), "implemented",
                "This is a point-in-time observation, not a transactional project snapshot.");
            AddTargetFeature(features, "object_resolve", directApi, "implemented", "Canonical paths identify the current location; rename or move changes the path.");

            var compile = ProbeCapabilityService<ICompilable>(selected.Target, "target", probes);
            metadata["compileService"] = compile.ToJson();
            if (kind == "plc") AddTargetFeature(features, "change.plc_compile", compile, "partial", "Explicit PLC software compilation through prepare/apply with a checkpoint. Generated state may change; SDK/native baseline and recovery qualification remain open.");
            if (kind == "project")
            {
                AddTargetFeature(features, "project_inventory", directApi, "partial", "Bounded configured hardware and exposed software scopes; library internals and runtime values are excluded.");
                AddTargetFeature(features, "project_index", directApi, "implemented", "Bounded metadata snapshots and stable pages; external edits require explicit verification and no whole-project or logic freshness is certified.");
                AddTargetFeature(features, "hardware_inspect", directApi, "partial", "Module-specific attributes/channels are limited to the installed API's exposed metadata.");
                AddTargetFeature(features, "hardware_catalog_search", directApi, "implemented", "Installed catalog content and optional products determine catalog results.");
                AddTargetFeature(features, "change.hardware_add", directApi, "partial", "An exact installed catalog identifier and valid device configuration are required.");
                var simulation = ProbeCapabilityService<PlcSimulationSettingsProvider>(project, "project", probes);
                AddTargetFeature(features, "change.project_simulation_enable", simulation, "implemented", "Prepared project checkpoint and explicit apply are required; no simulator is started.");
            }

            if (selected.Plc != null)
            {
                var plc = selected.Plc;
                bool blockTarget = selected.Target is PlcBlock;
                object xrefTarget = blockTarget ? (object)selected.Target : plc.Software;
                var xref = ProbeCapabilityService<CrossReferenceService>(xrefTarget, blockTarget ? "block" : "plc", probes);
                var analysisApi = xref;
                if (!blockTarget && xref.Status == "unavailable")
                    analysisApi = ApiObservation.Unknown("No PLC-level CrossReferenceService. Analysis can fall back to individual block services, which were not recursively probed by this bounded target inspection.");
                AddTargetFeature(features, "xref_query", xref, "implemented", "Native cross-references are queried only by a separate operation; current consistency and result completeness are not tested here.");
                foreach (string feature in new[] { "call_graph", "symbol_trace", "change_impact" })
                    AddTargetFeature(features, feature, analysisApi, "partial", "Static native reference analysis; indirect/runtime aliases and unresolved propagation remain explicit gaps.");
                var unit = ProbeCapabilityService<PlcUnitProvider>(plc.Software, "plc", probes);
                metadata["softwareUnitService"] = unit.ToJson();
                if (kind == "plc" || kind == "softwareUnit" || kind == "safetyUnit")
                {
                    AddTargetFeature(features, "software_inspect", directApi, "partial", "Bounded exposed software scopes, tags and data types; native protected content remains inaccessible.");
                    AddTargetFeature(features, "technology_read", directApi, "partial", "Enumerates PLC technology groups and the OutputCams/MeasuringInputs child compositions; it does not infer unit-local TO support or other service coverage.");
                    foreach (string feature in new[] { "source_upsert", "type_source_upsert", "block_import_xml", "plc_type_import_xml", "tag_table_import_xml" })
                        AddTargetFeature(features, "change." + feature, directApi, "partial", "Standard and software-unit scopes use explicit native group routing; payload language/type, collision, protection and compile checks occur during prepare/apply. Unit routes require separate qualification.",
                            blocked: safetyScope, blocker: "Safety-unit mutations require the dedicated safety import workflow.");
                    AddTargetFeature(features, "change.graphical_typed_edit", directApi, "partial", "Versioned separate LAD/FBD models, explicit declaration/storage effects and immutable native artifacts use the existing checkpoint workflow. Exact CPU, instruction, data type, language and route profiles are required. Native-format evidence and runtime qualification are separate; inspect the shipped typed profile catalog.",
                        blocked: safetyScope, blocker: "Typed graphical changes exclude safety scopes.");
                    foreach (string feature in new[] { "group_create", "group_rename", "group_delete" })
                        AddTargetFeature(features, "change." + feature, directApi, "partial", "Exact user groups in blocks/types/tags; generated/root groups are excluded, deletion requires an empty group, and moves are not implemented. Mandatory compilation and recovery checkpoints apply; recorded fixture evidence does not qualify the current target.",
                            blocked: safetyScope, blocker: "Safety-unit group mutations are excluded from this workflow.");
                    AddTargetFeature(features, "change.to_create", directApi, "partial", "The installed API does not expose a complete CPU-specific TO type/version catalog; Siemens validates the requested pair during creation. Optional initial scalar parameters are resolved, type/write-access checked and read back after creation within the same checkpoint before compile. Invalid initial configuration requires recovery; only exact recorded CPU/type/version fixture profiles are qualified.",
                        blocked: kind != "plc", blocker: "Technology creation currently targets the standard PLC technology-object group.");
                    var download = ProbeOwningHardwareService<DownloadProvider>(plc.Software, probes);
                    AddTargetFeature(features, "simulation_download", download, "partial", "Requires a compatible program/CPU, simulation compilation setting and an independently verified owned OFF PLCSIM Advanced instance with a fresh ticket. Runtime licensing is not probed here.");
                }
                bool safetyImportTarget = kind == "plc" || kind == "safetyUnit" || kind == "block" && ((PlcBlock)selected.Target).ProgrammingLanguage.ToString().StartsWith("F_", StringComparison.Ordinal);
                var selectedBlock = kind == "block" ? (PlcBlock)selected.Target : null;
                string safetyBlocker = selectedBlock == null ? null : CapabilityRules.SafetyImportBlocker(
                    selectedBlock.ProgrammingLanguage.ToString(), selectedBlock.IsKnowHowProtected, selectedBlock.IsConsistent);
                ObserveSafety(plc.Software, features, probes, protection, safetyImportTarget, generated, safetyBlocker);
            }

            if (kind == "block" && selected.Target is PlcBlock block)
            {
                bool protectedBlock = block.IsKnowHowProtected;
                bool consistent = block.IsConsistent;
                string language = block.ProgrammingLanguage.ToString();
                bool safetyBlock = language.StartsWith("F_", StringComparison.Ordinal);
                protection["knowHowProtected"] = protectedBlock;
                protection["isConsistent"] = consistent;
                protection["programmingLanguage"] = language;
                protection["generatedSystemGroup"] = generated;
                foreach (string feature in new[] { "block_read", "block_interface" })
                    AddTargetFeature(features, feature, directApi, feature == "block_interface" ? "partial" : "implemented",
                        feature == "block_interface" ? "Native XML is retained with normalized explicit array-element starts and nested declarations. Implicit array values are not expanded; unknown native elements remain explicit. Generated system blocks can reject export."
                            : "Native export eligibility is determined by Siemens; generated system blocks can reject export.",
                        blocked: protectedBlock || !consistent, blocker: protectedBlock ? "The selected block is know-how protected." : "The selected block is inconsistent; native export eligibility is not established.");
                foreach (string feature in new[] { "block_rename", "block_delete", "block_import_xml", "block_number_set", "block_attributes_set" })
                    AddTargetFeature(features, "change." + feature, directApi, "partial", "Prepare/apply enforce explicit scope, protection and recovery checkpoints. Generated/system objects may be rejected by the native API."
                        + (feature == "block_delete" ? " Deletion defaults to rejectReferenced and repeats the conservative exported-XML dependency scan at prepare/apply; exact own declaration is excluded, references and incomplete evidence are rejected." : ""),
                        blocked: protectedBlock || safetyBlock || safetyScope || generated,
                        blocker: protectedBlock ? "The selected block is know-how protected." : generated ? "Generated system-group objects are excluded from direct mutations." : "F-blocks and safety-unit objects require the dedicated safety workflow.");
                AddTargetFeature(features, "change.source_upsert", directApi, "partial", "SCL and STL/AWL source writes preserve the existing language; no implicit language conversion is supported.",
                    applicable: language == "SCL" || language == "STL" || block is DataBlock,
                    blocked: protectedBlock || safetyBlock || safetyScope || generated,
                    blocker: protectedBlock ? "The selected block is know-how protected." : generated ? "Generated system-group objects are excluded from direct mutations." : "Standard source changes cannot write safety blocks or safety-unit objects.");
                AddTargetFeature(features, "change.graphical_operand_set", directApi, "partial", "Only an existing local/global symbolic Access node with the same symbol-component count can be changed; new networks/instructions are not synthesized.",
                    applicable: language == "LAD" || language == "FBD", blocked: protectedBlock || !consistent || safetyScope || generated,
                    blocker: protectedBlock ? "The selected block is know-how protected." : !consistent ? "The selected block is inconsistent." : generated ? "Generated system-group objects are excluded from direct mutations." : "Safety-unit objects require the dedicated safety workflow.");
                AddTargetFeature(features, "change.graphical_typed_edit", directApi, "partial", "Typed Boolean, comparison, call, timer and counter networks require an exact native profile, resolved declarations and explicit instance storage. Complete native readback is mandatory. Supported edits, insertion and creation have separate qualification cells; source/profile availability does not certify this target.",
                    applicable: language == "LAD" || language == "FBD", blocked: protectedBlock || !consistent || safetyScope || generated,
                    blocker: protectedBlock ? "The selected block is know-how protected." : !consistent ? "The selected block is inconsistent." : generated ? "Generated system-group objects are excluded from direct mutations." : "Safety-unit objects require the dedicated safety workflow.");
                foreach (string feature in new[] { "graphical_network_patch", "graphical_network_insert", "call_connection_set" })
                    AddTargetFeature(features, "change." + feature, directApi, "partial", "Native FlgNet/v5 fragments or isolated existing call wires only; untouched interfaces, comments and unknown XML are preserved. Explicit UId/object ID and reference checks precede mandatory native compilation. Broad instruction creation and software-unit network insertion still require separate native qualification.",
                        applicable: language == "LAD" || language == "FBD", blocked: protectedBlock || !consistent || safetyScope || generated,
                        blocker: protectedBlock ? "The selected block is know-how protected." : !consistent ? "The selected block is inconsistent." : generated ? "Generated system-group objects are excluded from direct mutations." : "Safety-unit objects require the dedicated safety workflow.");
                AddTargetFeature(features, "change.call_parameter_set", directApi, "partial", "Edits one existing simple symbolic call argument in SCL/v4 or FlgNet/v5. Shared graphical Access nodes, expressions and indirect operands are rejected; instance relationships and all other native XML are preserved. Requires compilation; consult feature_catalog for the exact recorded native fixture profiles.",
                    applicable: language == "SCL" || language == "LAD" || language == "FBD", blocked: protectedBlock || !consistent || safetyScope || generated,
                    blocker: protectedBlock ? "The selected block is know-how protected." : !consistent ? "The selected block is inconsistent." : generated ? "Generated system-group objects are excluded from direct mutations." : "Safety-unit objects require the dedicated safety workflow.");
                AddTargetFeature(features, "engineering_read", directApi, "partial", "scope=calls reads FC/FB connections; scope=graphical returns a versioned read-only LAD/FBD graph with native IDs, hashes, opaque nodes and unresolved coverage. Native XML is retained as an artifact and can be explicitly inlined. Syntax coverage is separate from type resolution and runtime qualification.",
                    blocked: protectedBlock || !consistent, blocker: protectedBlock ? "The selected block is know-how protected." : "The selected block is inconsistent.");
            }
            else if (selected.Target is PlcType type)
            {
                protection["knowHowProtected"] = type.IsKnowHowProtected;
                protection["isConsistent"] = type.IsConsistent;
                AddTargetFeature(features, "type_read", directApi, "implemented", "Native type XML export; actual export is not performed by this discovery call.",
                    blocked: type.IsKnowHowProtected || !type.IsConsistent, blocker: type.IsKnowHowProtected ? "The selected type is know-how protected." : "The selected type is inconsistent.");
                AddTargetFeature(features, "change.plc_type_import_xml", directApi, "partial", "Explicit same-name type replacement in standard/software-unit scopes; imported dependencies require native validation.",
                    blocked: type.IsKnowHowProtected || safetyScope || generated,
                    blocker: type.IsKnowHowProtected ? "The selected type is know-how protected." : "Safety-unit/generated types are excluded from this mutation workflow.");
                foreach (string feature in new[] { "type_rename", "type_delete" })
                    AddTargetFeature(features, "change." + feature, directApi, "partial", "Exact UDT target with collision checks, dependency policy, native compile and recovery checkpoint. Conservative dependency scans reject unresolved/incomplete evidence. Recorded fixture evidence does not qualify the current target.",
                        blocked: type.IsKnowHowProtected || safetyScope || generated,
                        blocker: type.IsKnowHowProtected ? "The selected type is know-how protected." : "Safety-unit/generated types are excluded from this mutation workflow.");
            }
            else if (selected.Target is PlcTagTable)
            {
                AddTargetFeature(features, "tag_table_read", directApi, "implemented", "Native tag-table XML export; actual export is not performed by this discovery call.");
                AddTargetFeature(features, "change.tag_table_import_xml", directApi, "partial", "Explicit table import in standard/software-unit scopes; symbol/address and dependency checks occur during native import/compile.",
                    blocked: safetyScope || generated, blocker: "Safety-unit/generated tag tables are excluded from this mutation workflow.");
                foreach (string feature in new[] { "tag_set", "constant_set", "tag_delete", "constant_delete" })
                    AddTargetFeature(features, "change." + feature, directApi, "partial", "Exact existing table member with an explicit attribute allowlist. Deletion requires rejectReferenced dependency policy; incomplete dependency evidence is rejected. Native compilation and recovery checkpoints are required; recorded fixture evidence does not qualify the current target.",
                        blocked: safetyScope || generated, blocker: "Safety-unit/generated tag tables are excluded from this mutation workflow.");
                foreach (string feature in new[] { "tag_create", "constant_create" })
                    AddTargetFeature(features, "change." + feature, directApi, "partial", "Creates one member in this table after a scope-wide tag/constant name collision check; created safety tags are rejected. Native compilation and recovery checkpoints are required.",
                        blocked: safetyScope || generated, blocker: "Safety-unit/generated tag tables are excluded from this mutation workflow.");
            }
            else if (selected.Target is DeviceItem hardware)
            {
                AddTargetFeature(features, "hardware_inspect", directApi, "partial", "Only native offline configured address/channel data and exposed module attributes are available.");
                AddTargetFeature(features, "engineering_read", directApi, "partial", "Native attribute access metadata and the bridge's explicit scalar write allowlist are separate restrictions.");
                AddTargetFeature(features, "change.hardware_attributes", directApi, "partial", "Both native write access and HardwareWriteAttributes allowlist must permit each requested field.");
                AddTargetFeature(features, "change.hardware_address_set", directApi, "partial", "Edits one exact exposed native I/O address with ioType and oldStartAddress conflict checks. Native controller ownership and overlap must be known; uncertain ownership is rejected. Compilation and a recovery checkpoint are mandatory. Recorded native DI/DO/AI/AO profiles do not qualify arbitrary targets.");
                AddTargetFeature(features, "change.hardware_plug", directApi, "partial", "Actual CanPlugNew/type/position validation occurs during prepare; child-slot compatibility is not inferred from metadata.");
                AddTargetFeature(features, "change.hardware_move", directApi, "partial", "Same-rack pluggable-module slot moves require native CanPlugMove, compiled readback and configured hardware preservation.");
                AddTargetFeature(features, "change.hardware_channel_attributes", directApi, "partial", "Exact native channel type/I-O-type/number plus reviewed scalar allowlist and native read/write metadata; compiled preservation and checkpoint required.");
                AddTargetFeature(features, "change.hardware_cpu_memory_bytes", directApi, "partial", "CPU device items declaring the native system and clock memory attributes only; boolean enable flags, byte numbers 0-65535, distinct enabled bytes, compiled preservation and checkpoint required.");
                AddTargetFeature(features, "change.hardware_delete", directApi, "partial", "The mutation workflow also rejects software-bearing descendants; that subtree is not traversed here.",
                    blocked: hardware.IsBuiltIn, blocker: "Built-in hardware items cannot be deleted by this workflow.");
                var network = ProbeCapabilityService<NetworkInterface>(hardware, "hardware", probes);
                AddTargetFeature(features, "change.network_configure", network, "partial", "Offline node attributes and subnet links only; each field is checked against native access and the bridge allowlist.");
                AddTargetFeature(features, "change.network_io_assign", network, "partial", "Exact native connector/controller indexes, selected-PLC ownership and common-subnet prerequisite; shared-device/multi-controller assignments are rejected.");
                metadata["channelWriteAllowlist"] = new JArray(ChannelWriteAttributes.OrderBy(value => value, StringComparer.Ordinal));
                metadata["hardwareWriteAllowlist"] = new JArray(HardwareWriteAttributes.OrderBy(value => value, StringComparer.Ordinal));
                metadata["networkWriteAllowlist"] = new JArray(NetworkWriteAttributes.OrderBy(value => value, StringComparer.Ordinal));
                metadata["cpuMemoryByteAllowlist"] = new JArray(CpuMemoryByteAttributes.OrderBy(value => value, StringComparer.Ordinal));
            }

            // TechnologicalInstanceDB is also a PLC block. Its dedicated operations are
            // reported in addition to, never inferred from, the block-export interface.
            if (selected.Target is TechnologicalInstanceDB technology)
            {
                AddTargetFeature(features, "technology_read", directApi, "implemented", "Offline configuration parameters only; no runtime axis state or motion commands.");
                AddTargetFeature(features, "change.to_parameters", directApi, "partial", "Each selected parameter requires native write access and a supported scalar value. Range/unit constraints not exposed in metadata remain unknown.");
                AddTargetFeature(features, "change.to_delete", directApi, "partial", "Deletion requires a recoverable checkpoint and native dependency/compile checks.");
                metadata["technologyParameters"] = CapabilityTechnologyParameters(technology, budget, warnings);
                metadata["technologyType"] = technology.OfSystemLibElement;
                metadata["technologyVersion"] = technology.OfSystemLibVersion.ToString();
            }

            var result = Envelope();
            result["schemaVersion"] = 1;
            result["project"] = ProjectInfo(project);
            result["identity"] = selected.Identity.DeepClone();
            result["features"] = features;
            result["metadata"] = metadata;
            result["serviceProbes"] = probes;
            result["protection"] = protection;
            result["warnings"] = warnings;
            result["completeness"] = new JObject
            {
                ["complete"] = false, ["scopeComplete"] = !budget.Truncated && warnings.Count == 0 && !probes.Any(probe => probe.Value<string>("status") == "unknown"), ["truncated"] = budget.Truncated,
                ["maxItems"] = Limit(args, 500), ["visitedMetadataItems"] = budget.Used,
                ["scope"] = "Selected target self-description and a fixed set of typed read-only service probes; no operation execution or recursive capability scan.",
                ["gaps"] = new JArray("No payload-specific import, compilation or runtime qualification", "No feature qualification is inherited from another CPU/project/fixture",
                    "Native metadata does not guarantee operation success or protection authorization", "Range/unit metadata unavailable unless explicitly exposed",
                    "No transactional snapshot; repeat discovery after a project edit, reopen or target change", "Unlisted features are not evaluated, not implicitly unsupported")
            };
            return result;
        }

        private static JObject CapabilitySelectors(JObject args)
        {
            var result = (JObject)args.DeepClone();
            var aliases = new[] { new[] { "block", "blockPath", "blockName" }, new[] { "type", "typePath", "typeName" },
                new[] { "tagTable", "tagTablePath", "tagTableName" }, new[] { "hardware", "hardwarePath", "hardwareName" },
                new[] { "technology", "technologyPath", "technologyName" } };
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(args.Value<string>(alias[1])) && string.IsNullOrWhiteSpace(args.Value<string>(alias[2]))) continue;
                if (!string.IsNullOrWhiteSpace(result.Value<string>("kind")) && result.Value<string>("kind") != alias[0]) throw new BridgeException("InvalidRequest", "Capability target selectors conflict.");
                result["kind"] = alias[0];
                foreach (var mapping in new[] { new[] { alias[1], "objectPath" }, new[] { alias[2], "name" } })
                {
                    if (string.IsNullOrWhiteSpace(args.Value<string>(mapping[0]))) continue;
                    if (!string.IsNullOrWhiteSpace(result.Value<string>(mapping[1])) && result.Value<string>(mapping[1]) != args.Value<string>(mapping[0])) throw new BridgeException("InvalidRequest", "Capability target selectors conflict.");
                    result[mapping[1]] = args[mapping[0]].DeepClone();
                }
            }
            if (string.IsNullOrWhiteSpace(result.Value<string>("kind")) && string.IsNullOrWhiteSpace(result.Value<string>("objectPath")) && string.IsNullOrWhiteSpace(result.Value<string>("name")))
                result["kind"] = string.IsNullOrWhiteSpace(args.Value<string>("plcPath")) ? "project" : "plc";
            return result;
        }

        private static JObject CapabilityMetadata(IEngineeringObject target, ScanBudget budget, JArray warnings)
        {
            var result = new JObject { ["nativeType"] = target.GetType().FullName, ["attributeValuesRead"] = false };
            result["attributes"] = CapabilityAttributes(target, budget, warnings);
            var services = new JArray();
            result["services"] = services;
            try
            {
                var provider = target as IEngineeringServiceProvider;
                if (provider == null)
                {
                    result["serviceMetadata"] = ApiObservation.Unavailable("The selected native object does not implement IEngineeringServiceProvider.").ToJson();
                }
                else
                {
                    foreach (var service in provider.GetServiceInfos())
                    {
                        if (!budget.Take()) break;
                        services.Add(new JObject { ["type"] = service.Type.FullName, ["source"] = "GetServiceInfos" });
                    }
                    result["serviceMetadata"] = ApiObservation.Available("Native GetServiceInfos returned service descriptors.").ToJson();
                }
            }
            catch (Exception error)
            {
                result["serviceMetadata"] = ApiObservation.Unknown("Native service descriptor lookup failed: " + CapabilityError(error)).ToJson();
                warnings.Add(CapabilityWarning("GetServiceInfos", error));
            }
            var actions = new JArray();
            result["invocations"] = actions;
            try
            {
                foreach (var action in target.GetInvocationInfos())
                {
                    if (!budget.Take()) break;
                    actions.Add(new JObject { ["name"] = action.Name, ["returnType"] = action.Type?.FullName, ["executed"] = false });
                }
            }
            catch (Exception error) { warnings.Add(CapabilityWarning("GetInvocationInfos", error)); }
            var compositions = new JArray();
            result["compositions"] = compositions;
            try
            {
                foreach (var composition in target.GetCompositionInfos())
                {
                    if (!budget.Take()) break;
                    compositions.Add(new JObject { ["name"] = composition.Name, ["type"] = composition.Type?.FullName, ["expanded"] = false });
                }
            }
            catch (Exception error) { warnings.Add(CapabilityWarning("GetCompositionInfos", error)); }
            return result;
        }

        private static JArray CapabilityAttributes(IEngineeringObject target, ScanBudget budget, JArray warnings)
        {
            var rows = new JArray();
            try
            {
                foreach (var info in target.GetAttributeInfos())
                {
                    if (!budget.Take()) break;
                    if (IsSensitiveAttribute(info.Name)) { rows.Add(new JObject { ["name"] = info.Name, ["excluded"] = true }); continue; }
                    var types = info.SupportedTypes.Take(33).ToArray();
                    bool typeTruncated = types.Length > 32;
                    if (typeTruncated) budget.Truncated = true;
                    var enums = new JArray();
                    foreach (var type in types.Take(32).Where(type => type.IsEnum))
                    {
                        var names = Enum.GetNames(type);
                        var included = new JArray();
                        foreach (string name in names.Take(128)) { if (!budget.Take()) break; included.Add(name); }
                        bool truncated = included.Count < names.Length;
                        enums.Add(new JObject { ["type"] = type.FullName, ["names"] = included, ["truncated"] = truncated });
                        if (truncated) budget.Truncated = true;
                    }
                    rows.Add(new JObject { ["name"] = info.Name, ["access"] = info.AccessMode.ToString(),
                        ["readable"] = (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0,
                        ["writable"] = (info.AccessMode & EngineeringAttributeAccessMode.Write) != 0,
                        ["supportedTypes"] = new JArray(types.Take(32).Select(type => type.FullName)), ["typesTruncated"] = typeTruncated,
                        ["enumValues"] = enums, ["range"] = new JObject { ["status"] = "not_exposed" }, ["unit"] = new JObject { ["status"] = "not_exposed" } });
                }
            }
            catch (Exception error) { warnings.Add(CapabilityWarning("GetAttributeInfos", error)); }
            return rows;
        }

        private static JArray CapabilityTechnologyParameters(TechnologicalInstanceDB technology, ScanBudget budget, JArray warnings)
        {
            var rows = new JArray();
            try
            {
                foreach (var parameter in technology.Parameters)
                {
                    if (!budget.Take()) break;
                    rows.Add(new JObject { ["name"] = parameter.Name, ["attributes"] = CapabilityAttributes(parameter, budget, warnings) });
                }
            }
            catch (Exception error) { warnings.Add(CapabilityWarning("Technology.Parameters", error)); }
            return rows;
        }

        private static ApiObservation ProbeCapabilityService<T>(object target, string owner, JArray probes)
            where T : class, IEngineeringService
        {
            ApiObservation result;
            try
            {
                var provider = target as IEngineeringServiceProvider;
                result = provider == null ? ApiObservation.Unavailable("The selected native object does not implement IEngineeringServiceProvider.")
                    : provider.GetService<T>() != null ? ApiObservation.Available("Typed GetService<" + typeof(T).Name + "> returned a service.")
                    : ApiObservation.Unavailable("Typed GetService<" + typeof(T).Name + "> returned null on this target.");
            }
            catch (Exception error) { result = ApiObservation.Unknown("Typed service probe failed: " + error.GetType().Name + ": " + CapabilityError(error)); }
            var probe = result.ToJson();
            probe["owner"] = owner;
            probe["serviceType"] = typeof(T).FullName;
            probes.Add(probe);
            return result;
        }

        private static ApiObservation ProbeOwningHardwareService<T>(PlcSoftware software, JArray probes)
            where T : class, IEngineeringService
        {
            ApiObservation result = null;
            try
            {
                IEngineeringInstance owner = software;
                int depth = 0;
                for (; owner != null && depth < 64; owner = owner.Parent, depth++)
                    if (owner is DeviceItem device && device.GetService<T>() != null)
                    { result = ApiObservation.Available("Typed " + typeof(T).Name + " service is exposed on an owning DeviceItem."); break; }
                if (result == null) result = depth >= 64 ? ApiObservation.Unknown("Native ownership depth limit prevented a complete service lookup.")
                    : ApiObservation.Unavailable("No typed " + typeof(T).Name + " service was exposed on the owning DeviceItem chain.");
            }
            catch (Exception error) { result = ApiObservation.Unknown("Owning hardware service lookup failed: " + CapabilityError(error)); }
            var probe = result.ToJson(); probe["owner"] = "owningCpu"; probe["serviceType"] = typeof(T).FullName; probes.Add(probe);
            return result;
        }

        private static void ObserveSafety(PlcSoftware software, JArray features, JArray probes, JObject protection, bool importApplicable, bool generated, string targetBlocker)
        {
            ApiObservation api;
            bool blocked = false;
            try
            {
                var safety = SafetyAdministrationFor(software);
                if (safety == null) api = ApiObservation.Unavailable("No SafetyAdministration service was exposed on the owning PLC DeviceItem chain.");
                else
                {
                    api = ApiObservation.Available("Native SafetyAdministration is exposed on the owning PLC DeviceItem.");
                    protection["safetyPasswordSet"] = safety.IsSafetyOfflineProgramPasswordSet;
                    protection["safetyLoggedOn"] = safety.IsLoggedOnToSafetyOfflineProgram;
                    blocked = safety.IsSafetyOfflineProgramPasswordSet && !safety.IsLoggedOnToSafetyOfflineProgram;
                }
            }
            catch (Exception error) { api = ApiObservation.Unknown("Safety service/state probe failed: " + CapabilityError(error)); }
            var probe = api.ToJson();
            probe["owner"] = "owningCpu"; probe["serviceType"] = typeof(SafetyAdministration).FullName; probes.Add(probe);
            AddTargetFeature(features, "safety_inspect", api, "partial", "Reads exposed offline configuration/signatures only; it does not validate or accept the safety program.");
            AddTargetFeature(features, "change.safety_import_xml", api, "partial", "Dedicated acknowledged F-LAD/F-FBD/F-DB import only; explicit group/scope, protection, native consistency and signature checks apply.",
                applicable: importApplicable, blocked: blocked || generated || targetBlocker != null,
                blocker: generated ? "Generated system-group objects are excluded from direct mutations." : targetBlocker ?? "The safety program requires login through the authorized TIA workflow; discovery does not log in or collect credentials.");
        }

        private static void AddTargetFeature(JArray rows, string id, ApiObservation api, string implementation, string limitation,
            bool applicable = true, bool blocked = false, string blocker = null)
        {
            string availability = CapabilityRules.Availability(api.Status, implementation, "unverified", applicable, blocked, true);
            var reasons = new JArray();
            if (!applicable) reasons.Add("The selected target kind or programming language is outside this operation's scope.");
            if (blocked && blocker != null) reasons.Add(blocker);
            foreach (string reason in api.Reasons) reasons.Add(reason);
            reasons.Add("No operation qualification has been executed for this exact target in this read-only observation.");
            rows.Add(new JObject { ["featureId"] = id, ["api"] = api.ToJson(),
                ["implementation"] = new JObject { ["status"] = implementation, ["limitations"] = new JArray(limitation) },
                ["qualification"] = new JObject { ["status"] = "unverified", ["evidenceIds"] = new JArray() },
                ["availability"] = new JObject { ["status"] = availability, ["reasons"] = reasons } });
        }

        private static JObject CapabilityWarning(string operation, Exception error) => new JObject { ["operation"] = operation, ["errorType"] = error.GetType().Name, ["message"] = CapabilityError(error) };
        private static string CapabilityError(Exception error) => error.Message.Length <= 2048 ? error.Message : error.Message.Substring(0, 2048);

        private sealed class ApiObservation
        {
            internal string Status;
            internal string[] Reasons;
            internal static ApiObservation Available(string reason) => new ApiObservation { Status = "available", Reasons = new[] { reason } };
            internal static ApiObservation Unavailable(string reason) => new ApiObservation { Status = "unavailable", Reasons = new[] { reason } };
            internal static ApiObservation Unknown(string reason) => new ApiObservation { Status = "unknown", Reasons = new[] { reason } };
            internal JObject ToJson() => new JObject { ["status"] = Status, ["reasons"] = new JArray(Reasons) };
        }
    }
}
