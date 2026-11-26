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
    // Targets of a change: compile scope, blocks, types, tag tables, hardware, technology objects and safety state.
    internal sealed partial class OpennessSession
    {
        // Compile-only kinds report diagnostics as their result; HMI kinds keep their own baseline rules.
        private static bool UsesCompileBaseline(string kind) =>
            !IsCompileOnlyKind(kind) && kind != "hmi_classic_screen_edit" && !IsHmiChangeKind(kind);
        // Compiling an IO device before its first assignment generates the server module that assignment declares
        // (expectedServerModule), so that change keeps the clean-compile rule instead of a baseline compile that would mutate.
        private static bool UsesCompileBaseline(EngineeringChange change) =>
            UsesCompileBaseline(change.Kind) && !(change.Kind == "network_io_assign" && RemoteIoCompanionContract.ExpectedRows(change.Payload).Count != 0);

        private JObject CompileEngineeringChange(EngineeringChange change, Project project)
        {
            if (change.Kind == "hmi_engineering" || HmiEngineeringRules.IsAdmittedKind(change.Kind)) return CompileHmiEngineeringChange(change, project);
            if (change.Kind == "hmi_text_list_import") return CompileHmiTextListTarget(project, (JObject)change.Payload["target"]);
            if (IsCompileOnlyKind(change.Kind) && change.Kind != "plc_compile") return CompileOnly(change, project);
            if (CompileOnlyRules.CompilesEveryDevice(change.Kind) || PlcAlarmProjectKinds.Contains(change.Kind))
                return CompileDeviceProject(project);
            var plc = SelectPlc(change.Selector);
            return PlcCompilation.Compile(plc.Software.GetService<ICompilable>(), plc.Path, stage => {
                change.Stages.Add(stage);
                WriteChangeJournal(change);
            });
        }

        private static JObject ChangeCompilation(CompilerResult result, string target, int budget = DeviceCompilation.DefaultMessageBudget) => DeviceCompilation.Describe(result, target, budget);

        private void PrepareBlockTargets(EngineeringChange change, PlcSelection plc, bool safety, bool mustExist, JArray effects)
        {
            var find = CreateBlockFinder(plc, change);
            foreach (var name in change.BlockNames)
            {
                var block = find(name, mustExist);
                if (block != null)
                {
                    EnsureEditableBlock(block.Item, safety);
                    // TIA cannot export an inconsistent block, and a target needs its native backup and full fingerprint.
                    if (!block.Item.IsConsistent)
                        throw new BridgeException("FingerprintUnavailable", "The target block " + name + " is inconsistent; its native backup and fingerprint cannot be established. Compile it first (plc_compile) or read its generated source. Nothing was changed.",
                            new JObject { ["blockPath"] = block.Path, ["fallback"] = new JObject { ["tool"] = "block_read", ["format"] = "generatedSource" } });
                    if (!change.PreflightOnly || ChangeBatchRules.IsStructured(change.Kind))
                    {
                        string backup = Path.Combine(change.Directory, "before-" + Guid.NewGuid().ToString("N") + ".xml");
                        block.Item.Export(new FileInfo(backup), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                        change.Backups.Add(new JObject { ["blockName"] = name, ["blockPath"] = block.Path, ["artifact"] = ChangeArtifact(backup) });
                    }
                    effects.Add(new JObject { ["action"] = change.Kind, ["existingBlock"] = BlockInfo(block, plc.Path) });
                }
                else effects.Add(new JObject { ["action"] = "create", ["blockName"] = name });
            }
        }

        private void VerifyBlockTargets(EngineeringChange change, PlcSelection plc, bool safety)
        {
            var find = CreateBlockFinder(plc, change);
            foreach (var name in change.BlockNames)
            {
                var block = find(name, false);
                if (block != null) EnsureEditableBlock(block.Item, safety);
            }
        }

        private void VerifyExpectedBlocksExist(EngineeringChange change, PlcSelection plc)
        {
            var find = CreateBlockFinder(plc, change);
            foreach (var name in change.BlockNames) find(name, true);
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

        private BlockEntry FindBlockByName(PlcSelection plc, string name, bool required, EngineeringChange change, bool exactGroup = true) =>
            CreateBlockFinder(plc, change, exactGroup)(name, required);

        // One walk of the change's software scope; each lookup then refuses an ambiguous or (if required) missing name as a single walk would.
        private Func<string, bool, BlockEntry> CreateBlockFinder(PlcSelection plc, EngineeringChange change, bool exactGroup = true)
        {
            var budget = new ScanBudget(50000);
            var byName = ScopeBlocks(ResolveSoftwareScope(plc, change.ScopePath), budget)
                .Where(b => (b.Item.Namespace ?? "") == change.Namespace && (!exactGroup || ObjectIdentity.ParentPath(b.Path) == change.GroupPath))
                .ToLookup(b => b.Item.Name, StringComparer.OrdinalIgnoreCase);
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Block lookup was incomplete.");
            return (name, required) =>
            {
                var blocks = byName[name].ToArray();
                if (blocks.Length > 1) throw new BridgeException("AmbiguousTarget", "Multiple blocks have name: " + name);
                if (blocks.Length == 0 && required) throw new BridgeException("BlockNotFound", "Block not found: " + name);
                return blocks.FirstOrDefault();
            };
        }

        // Every engineering mutation route calls this; read-only callers pass write: false.
        private void EnsureProjectOffline(Project project, bool write = true)
        {
            if (write && !project.IsPrimary)
                throw new BridgeException("ReadOnlyProject", "The project is open read-only (secondary); mutations are refused. Use project_copy and open the copy for edits.");
            var budget = new ScanBudget(50000);
            foreach (var entry in DeviceItems(project, budget))
            {
                var provider = entry.Item.GetService<OnlineProvider>();
                if (provider != null) ChangeMilestoneRules.RequireOffline(entry.Path, provider.State == OnlineState.Offline);
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish complete offline state.");
        }

        private HardwareEntry ChangeHardware(Project project, JObject payload, string pathKey = "hardwarePath")
        {
            string path = ChangeText(payload, pathKey, 4096);
            var budget = new ScanBudget(50000);
            var entry = DeviceItems(project, budget).FirstOrDefault(item => item.Path == path);
            if (entry == null) throw new BridgeException("HardwareNotFound", "No hardware item matches " + pathKey + ".");
            return entry;
        }

        private Node ChangeNode(Project project, JObject payload, string pathKey = "hardwarePath", string nameKey = "nodeName")
        {
            var item = ChangeHardware(project, payload, pathKey).Item;
            var network = item.GetService<NetworkInterface>() ?? throw new BridgeException("UnsupportedCapability", "Selected hardware item has no network interface service.");
            string name = payload.Value<string>(nameKey);
            var nodes = network.Nodes.Where(node => name == null || node.Name == name).Take(2).ToArray();
            if (nodes.Length != 1) throw new BridgeException(nodes.Length == 0 ? "NodeNotFound" : "AmbiguousTarget", "Specify an exact " + nameKey + " from engineering_read.");
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
            // hardware_delete, hardware_move and hardware_replace all pass here, at prepare and again at apply.
            EnsureNoSafetyHardware(item);
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
                    if (!change.PreflightOnly)
                    {
                        string path = Path.Combine(change.Directory, "before-type-" + Guid.NewGuid().ToString("N") + ".xml");
                        type.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                        change.Backups.Add(new JObject { ["typeName"] = name, ["artifact"] = ChangeArtifact(path) });
                    }
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
        // Every tag, user and system constant name of a software scope's tag tables: the names TIA keeps unique per scope.
        private static string[] ScopeSymbolNames(PlcTagTableGroup group) => group == null ? Array.Empty<string>() : AllTagTables(group)
            .SelectMany(table => table.Tags.Select(tag => tag.Name).Concat(table.UserConstants.Select(constant => constant.Name)).Concat(table.SystemConstants.Select(constant => constant.Name))).ToArray();

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
    }
}
