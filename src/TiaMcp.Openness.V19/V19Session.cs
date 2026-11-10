using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcp.Model;
using TiaMcp.Openness.V21;

namespace TiaMcp.Openness.V19
{
    /// <summary>
    /// Read-only V19 adapter. Every operation runs serially on the worker's STA thread. Nothing here saves, archives,
    /// compiles, imports or deletes: the only native calls that touch disk are Projects.Open and object exports into the
    /// Host's artifact directory. Object paths use the V21 worker's canonical scheme, so a V19 path and the path of the
    /// same object after project_upgrade into V21 are equal strings.
    /// </summary>
    internal sealed class V19Session : IDisposable
    {
        internal const string Origin = "TIA Portal V19 Openness/offline";
        // Exactly the operations this adapter implements. bridge_info reports them; anything else is refused unexecuted.
        internal static readonly string[] Operations =
        {
            "bridge_info", "discover", "attach", "start", "session_status", "detach",
            "project_open", "project_inventory", "software_inspect", "block_read", "type_read", "tag_table_read"
        };

        private readonly string _apiDirectory;
        private TiaPortal _portal;
        private bool _ownsPortal;
        private int? _processId;

        internal V19Session(string apiDirectory) { _apiDirectory = apiDirectory; }

        internal JObject Execute(string operation, JObject args)
        {
            switch (operation)
            {
                case "bridge_info": return BridgeInfo();
                case "discover": return Discover();
                case "attach": return Attach(args);
                case "start": return Start();
                case "session_status": return Status();
                case "detach": return Detach();
                case "project_open": return ProjectOpen(args);
                case "project_inventory": return Inventory(args);
                case "software_inspect": return SoftwareInspect(args);
                case "block_read": return BlockRead(args);
                case "type_read": return TypeRead(args);
                case "tag_table_read": return TagTableRead(args);
                default:
                    throw new BridgeException("UnsupportedOnTiaVersion",
                        "The TIA V19 adapter is read-only and implements only: " + string.Join(", ", Operations) + ". Use the V21 Host for " + operation + ".",
                        new JObject { ["operation"] = operation, ["tiaVersion"] = Program.TiaMajor, ["supportedOperations"] = new JArray(Operations) });
            }
        }

        private JObject BridgeInfo()
        {
            var result = Envelope();
            result["workerVersion"] = "0.1.0";
            result["tiaVersion"] = Program.TiaMajor;
            result["apiDirectory"] = _apiDirectory;
            result["apiAssemblyVersion"] = typeof(TiaPortal).Assembly.GetName().Version.ToString();
            result["framework"] = ".NET Framework 4.8";
            result["architecture"] = Environment.Is64BitProcess ? "x64" : "x86";
            result["readOnly"] = true;
            result["operations"] = new JArray(Operations);
            result["capabilities"] = Capabilities();
            result["capabilityScope"] = "Static summary of the read-only V19 adapter. Changes, compiles, saves, archives, libraries, HMI, simulation and online operations are V21-only.";
            return result;
        }

        private static JArray Capabilities() => new JArray(
            Capability("discover", "conditional", "Installed V19 Openness; lists running V19 Portal processes only."),
            Capability("attach", "conditional", "Requires an accessible V19 process and Openness authorization; attaches without opening, saving or closing its projects."),
            Capability("start", "conditional", "Starts a worker-owned V19 instance without UI; disposal releases the connection and discards unsaved state."),
            Capability("project_open", "conditional", "Opens an existing .ap19 file in an empty worker-owned V19 instance; nothing is ever saved. readOnly opens a secondary project, on which TIA also refuses exports."),
            Capability("project_inventory", "conditional", "Bounded device/address and block inventory including software and safety units and generated system groups."),
            Capability("software_inspect", "conditional", "Software and safety unit scopes with their blocks, tag tables (tags and constants) and PLC data types, in the V21 shape without technology objects."),
            Capability("block_read", "conditional", "Native SimaticML XML export of a block; protected and some inconsistent blocks refuse export."),
            Capability("type_read", "conditional", "Native SimaticML XML export of a PLC data type; protected types refuse export."),
            Capability("tag_table_read", "conditional", "Native SimaticML XML export of a tag table."),
            Capability("changes", "unsupported", "The V19 adapter has no change, compile, save or archive route. Upgrade the project into V21 with project_upgrade on the V21 Host."));

        private static JObject Capability(string name, string status, string reason) => new JObject { ["name"] = name, ["status"] = status, ["reason"] = reason };

        private JObject Discover()
        {
            var processes = new JArray();
            var native = TiaPortal.GetProcesses();
            // Indexing, not foreach: TiaPortalProcess.Dispose terminates TIA, so no enumerator may dispose a process proxy.
            for (int i = 0; i < native.Count; i++)
            {
                var process = native[i];
                processes.Add(new JObject { ["processId"] = process.Id, ["mode"] = process.Mode.ToString(),
                    ["executablePath"] = process.Path?.FullName, ["projectPath"] = process.ProjectPath?.FullName });
            }
            var result = Envelope();
            result["processes"] = processes;
            return result;
        }

        private JObject Attach(JObject args)
        {
            EnsureDetached();
            int id = RequiredInteger(args, "processId");
            var native = TiaPortal.GetProcesses();
            for (int i = 0; i < native.Count; i++)
            {
                if (native[i].Id != id) continue;
                _portal = native[i].Attach();
                _processId = id;
                _ownsPortal = false;
                return Status();
            }
            throw new BridgeException("ProcessNotFound", "No attachable TIA V19 process has that processId.");
        }

        private JObject Start()
        {
            EnsureDetached();
            _portal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
            _ownsPortal = true;
            _processId = _portal.GetCurrentProcess().Id;
            return Status();
        }

        private JObject Status()
        {
            var result = Envelope();
            result["attached"] = _portal != null;
            result["processId"] = _processId;
            result["ownsPortalProcess"] = _ownsPortal;
            result["tiaVersion"] = Program.TiaMajor;
            result["disconnectBehavior"] = _ownsPortal
                ? "Dispose this connection. Siemens closes the headless instance when this is its last attached Openness client; process exit is not verified. Nothing was saved."
                : "Dispose only this Openness attachment; the user-owned TIA instance remains open.";
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

        public void Dispose() => DetachPortalConnection();

        private void DetachPortalConnection()
        {
            var portal = _portal;
            _portal = null;
            _ownsPortal = false;
            _processId = null;
            // Dispose detaches from externally owned instances. Never Project.Save, Project.Close or Process.Kill.
            portal?.Dispose();
        }

        private JObject ProjectOpen(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0)
                throw new BridgeException("ProjectIsolationRequired", "project_open requires an empty worker-owned instance started with tia_start.");
            string path = args.Value<string>("path");
            if (!ProjectPathIsAbsolute(path) || !string.Equals(Path.GetExtension(path), ".ap19", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ProjectVersionUnsupported", "The V19 adapter opens only an absolute existing .ap19 project path. It never upgrades: older projects need their own TIA version, and project_upgrade on the V21 Host converts V14-V20 projects into V21.");
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists) throw new BridgeException("ProjectNotFound", "The .ap19 project file does not exist on this host.");
            bool readOnly = args.Value<bool?>("readOnly") == true;
            var project = readOnly ? _portal.Projects.Open(file, null, ProjectOpenMode.Secondary) : _portal.Projects.Open(file);
            if (readOnly && project.IsPrimary)
            {
                project.Close();
                throw new BridgeException("ReadOnlyOpenUnverified", "TIA opened the project as primary; it was closed again without saving.");
            }
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["opened"] = true;
            result["readOnly"] = !project.IsPrimary;
            result["saved"] = false;
            result["note"] = "The adapter never saves. A primary project allows exports; detaching discards anything TIA changed in memory.";
            return result;
        }

        // Filters and text rendering exist on the V21 Host only; silently ignoring them would return unfiltered results.
        private static void RefuseV21Options(JObject args, params string[] names)
        {
            foreach (string name in names)
            {
                var value = args[name];
                if (value == null || value.Type == JTokenType.Null || (name == "includeHardware" && value.Type == JTokenType.Boolean && value.Value<bool>())) continue;
                throw new BridgeException("UnsupportedOnTiaVersion", "The V19 adapter does not support " + name + "; it is V21-only.");
            }
        }

        private JObject Inventory(JObject args)
        {
            RefuseV21Options(args, "groupPath", "namePrefix", "includeHardware", "includeBlocks", "compact");
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
                        ["length"] = a.Length, ["lengthUnit"] = "bits", ["byteSpan"] = a.Length <= 0 ? 0 : (int)(((long)a.Length + 7) / 8) }));
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
                var plc = new PlcSelection { Project = project, Software = software, Path = plcPath };
                foreach (var scope in SoftwareScopes(plc, budget, warnings))
                    foreach (var block in ScopeBlocks(scope, budget)) blocks.Add(BlockInfo(block, plcPath));
            }
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["hardware"] = hardware;
            result["plcs"] = plcs;
            result["blocks"] = blocks;
            result["warnings"] = warnings;
            result["completeness"] = new JObject { ["complete"] = false, ["scopeComplete"] = !budget.Truncated && warnings.Count == 0,
                ["truncated"] = budget.Truncated, ["maxItems"] = max, ["visitedItems"] = budget.Used,
                ["scope"] = "Device items, configured address ranges and exposed program blocks including software units and generated system groups; no online data.",
                ["gaps"] = new JArray("Use software_inspect for tag tables and PLC data types", "No hardware channel, technology, safety or HMI details in V19", "Library internals", "No transactional snapshot or project revision token") };
            return result;
        }

        // The V21 software_inspect shape without technology objects, group paths and unit relations.
        private JObject SoftwareInspect(JObject args)
        {
            RefuseV21Options(args, "groupPath", "namePrefix", "include", "compact");
            var plc = SelectInspectionPlc(args, "scopePath");
            string wantedScope = args.Value<string>("scopePath");
            int max = Limit(args, 500);
            var budget = new ScanBudget(max);
            var warnings = new JArray();
            var scopes = SoftwareScopes(plc, budget, warnings).Where(scope => string.IsNullOrWhiteSpace(wantedScope) || scope.Path == wantedScope).ToList();
            if (!string.IsNullOrWhiteSpace(wantedScope)) RequireOne(scopes.Count, budget, "PLC or software-unit scope");
            var units = new JArray();
            var blocks = new JArray();
            var tables = new JArray();
            var types = new JArray();
            foreach (var scope in scopes)
            {
                units.Add(new JObject { ["path"] = scope.Path, ["name"] = scope.Name, ["kind"] = scope.Kind, ["namespacePreset"] = scope.NamespacePreset,
                    ["identity"] = IdentityInfo(plc.Project, plc.Path, scope.Path, scope.Path, scope.Kind, scope.Name) });
                foreach (var block in ScopeBlocks(scope, budget))
                {
                    var row = BlockInfo(block, plc.Path);
                    row["scopePath"] = scope.Path;
                    row["identity"] = IdentityInfo(plc.Project, plc.Path, scope.Path, block.Path, "block", block.Item.Name);
                    blocks.Add(row);
                }
                foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget))
                {
                    var tags = new JArray();
                    var constants = new JArray();
                    tables.Add(new JObject { ["path"] = table.Path, ["name"] = table.Item.Name, ["scopePath"] = scope.Path, ["tags"] = tags, ["constants"] = constants });
                    foreach (var tag in table.Item.Tags)
                    {
                        if (!budget.Take()) break;
                        tags.Add(new JObject { ["path"] = table.Path + "/tags/" + Segment(tag.Name), ["name"] = tag.Name,
                            ["dataType"] = tag.DataTypeName, ["logicalAddress"] = tag.LogicalAddress, ["externalAccessible"] = tag.ExternalAccessible,
                            ["externalVisible"] = tag.ExternalVisible, ["externalWritable"] = tag.ExternalWritable });
                    }
                    foreach (var constant in table.Item.UserConstants)
                    {
                        if (!budget.Take()) break;
                        constants.Add(new JObject { ["path"] = table.Path + "/constants/" + Segment(constant.Name),
                            ["name"] = constant.Name, ["dataType"] = constant.DataTypeName, ["value"] = constant.Value, ["kind"] = "user" });
                    }
                    foreach (var constant in table.Item.SystemConstants)
                    {
                        if (!budget.Take()) break;
                        constants.Add(new JObject { ["path"] = table.Path + "/systemConstants/" + Segment(constant.Name),
                            ["name"] = constant.Name, ["dataType"] = constant.DataTypeName, ["value"] = constant.Value?.ToString(), ["kind"] = "system" });
                    }
                }
                foreach (var type in ScopeTypes(scope, budget))
                    types.Add(new JObject { ["path"] = type.Path, ["name"] = type.Item.Name, ["scopePath"] = scope.Path,
                        ["isConsistent"] = type.Item.IsConsistent, ["isKnowHowProtected"] = type.Item.IsKnowHowProtected });
            }
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["softwareScopes"] = units;
            result["blocks"] = blocks;
            result["tagTables"] = tables;
            result["types"] = types;
            result["warnings"] = warnings;
            result["completeness"] = new JObject { ["complete"] = false, ["scopeComplete"] = !budget.Truncated && warnings.Count == 0,
                ["truncated"] = budget.Truncated, ["visitedItems"] = budget.Used,
                ["scope"] = "Program and generated system blocks, software/safety-unit contents, tag tables, constants and PLC types (the V21 shape without technology objects, group paths and unit relations).",
                ["gaps"] = new JArray("Block bodies and type members require native XML reads", "No technology objects", "No group paths or unit relations", "No online values", "No transactional project snapshot") };
            return result;
        }

        private JObject BlockRead(JObject args)
        {
            if (args.Value<string>("format") != null) throw new BridgeException("UnsupportedOnTiaVersion", "The V19 adapter exports native XML only; simaticSd and generatedSource formats are V21-only.");
            RefuseV21Options(args, "render", "culture");
            var plc = SelectInspectionPlc(args, "blockPath");
            string path = args.Value<string>("blockPath");
            string name = args.Value<string>("blockName");
            RequireSelector(path, name, "blockPath", "blockName");
            var lookup = new ScanBudget(50000);
            var matches = SoftwareScopes(plc, lookup).SelectMany(scope => ScopeBlocks(scope, lookup))
                .Where(entry => ObjectIdentity.Matches(entry.Path, entry.Item.Name, ObjectIdentity.ScopePath(plc.Path, entry.Path), path, name, args.Value<string>("scopePath"))).ToList();
            RequireOne(matches.Count, lookup, "block");
            var selected = matches[0];
            if (selected.Item.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "The selected block is know-how protected.");
            var file = NewArtifact(args, "block");
            Export(() => selected.Item.Export(file, ExportOptions.WithDefaults), plc.Project);
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["block"] = BlockInfo(selected, plc.Path);
            result["identity"] = IdentityInfo(plc.Project, plc.Path, ObjectIdentity.ScopePath(plc.Path, selected.Path), selected.Path, "block", selected.Item.Name);
            return WithXml(result, file, "Native block XML export");
        }

        private JObject TypeRead(JObject args)
        {
            if (args.Value<string>("format") != null) throw new BridgeException("UnsupportedOnTiaVersion", "The V19 adapter exports native XML only; simaticSd and generatedSource formats are V21-only.");
            var plc = SelectInspectionPlc(args, "typePath");
            string path = args.Value<string>("typePath");
            string name = args.Value<string>("typeName");
            RequireSelector(path, name, "typePath", "typeName");
            var lookup = new ScanBudget(50000);
            var matches = SoftwareScopes(plc, lookup).SelectMany(scope => ScopeTypes(scope, lookup))
                .Where(entry => ObjectIdentity.Matches(entry.Path, entry.Item.Name, ObjectIdentity.ScopePath(plc.Path, entry.Path), path, name, args.Value<string>("scopePath"))).ToList();
            RequireOne(matches.Count, lookup, "PLC type");
            var selected = matches[0];
            if (selected.Item.IsKnowHowProtected) throw new BridgeException("ProtectedObject", "The selected PLC type is know-how protected.");
            var file = NewArtifact(args, "type");
            Export(() => selected.Item.Export(file, ExportOptions.WithDefaults), plc.Project);
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["type"] = new JObject { ["path"] = selected.Path, ["name"] = selected.Item.Name, ["isConsistent"] = selected.Item.IsConsistent };
            result["identity"] = IdentityInfo(plc.Project, plc.Path, ObjectIdentity.ScopePath(plc.Path, selected.Path), selected.Path, "type", selected.Item.Name);
            return WithXml(result, file, "Native PLC data type XML export");
        }

        private JObject TagTableRead(JObject args)
        {
            var plc = SelectInspectionPlc(args, "tagTablePath");
            string path = args.Value<string>("tagTablePath");
            string name = args.Value<string>("tagTableName");
            RequireSelector(path, name, "tagTablePath", "tagTableName");
            var budget = new ScanBudget(50000);
            var matches = SoftwareScopes(plc, budget).SelectMany(scope => TagTables(scope.Tags, scope.Path + "/tags", budget))
                .Where(entry => ObjectIdentity.Matches(entry.Path, entry.Item.Name, ObjectIdentity.ScopePath(plc.Path, entry.Path), path, name, args.Value<string>("scopePath"))).ToList();
            RequireOne(matches.Count, budget, "tag table");
            var selected = matches[0];
            var file = NewArtifact(args, "tag-table");
            Export(() => selected.Item.Export(file, ExportOptions.WithDefaults), plc.Project);
            var result = Envelope();
            result["project"] = ProjectInfo(plc.Project);
            result["plcPath"] = plc.Path;
            result["tagTable"] = new JObject { ["path"] = selected.Path, ["name"] = selected.Item.Name };
            result["identity"] = IdentityInfo(plc.Project, plc.Path, ObjectIdentity.ScopePath(plc.Path, selected.Path), selected.Path, "tagTable", selected.Item.Name);
            return WithXml(result, file, "Unchanged native tag-table XML export, including constants and language-specific comments.");
        }

        // TIA refuses exports from a secondary project; report that as the typed read-only refusal the V21 worker uses.
        private static void Export(Action export, Project project)
        {
            try { export(); }
            catch (EngineeringTargetInvocationException ex) when (!project.IsPrimary)
            { throw new BridgeException("ReadOnlyProject", "TIA refuses exports in a read-only (secondary) project: " + ex.Message); }
        }

        private JObject WithXml(JObject result, FileInfo file, string scope)
        {
            file.Refresh();
            result["artifact"] = FileArtifact(file);
            bool included = file.Length <= 512 * 1024;
            if (included) result["xml"] = File.ReadAllText(file.FullName);
            result["completeness"] = new JObject { ["complete"] = true, ["scope"] = scope, ["xmlIncluded"] = included, ["engineeringVersion"] = "V19",
                ["note"] = "Native V19 XML is retained unchanged; it is not interchangeable with V21 XML. No normalized semantic model or revision guarantee is implied." };
            return result;
        }

        // --- selection and traversal (the V21 worker's canonical paths) ---

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

        private PlcSelection SelectInspectionPlc(JObject args, string pathKey)
        {
            var effective = (JObject)args.DeepClone();
            string path = args.Value<string>(pathKey) ?? args.Value<string>("scopePath");
            if (string.IsNullOrWhiteSpace(args.Value<string>("plcPath")) && !string.IsNullOrWhiteSpace(path)) effective["plcPath"] = ObjectIdentity.PlcPath(path);
            return SelectPlc(effective);
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

        // Software and safety units, as in V21; a unit traversal failure is a warning on inventory, an error on lookup.
        private static List<SoftwareScope> SoftwareScopes(PlcSelection plc, ScanBudget budget, JArray warnings = null)
        {
            var scopes = new List<SoftwareScope> { new SoftwareScope { Name = plc.Software.Name, Kind = "plc", Path = plc.Path,
                Blocks = plc.Software.BlockGroup, Tags = plc.Software.TagTableGroup, Types = plc.Software.TypeGroup } };
            try
            {
                var provider = plc.Software.GetService<PlcUnitProvider>();
                if (provider == null) return scopes;
                foreach (var unit in provider.UnitGroup.Units)
                {
                    if (!budget.Take()) break;
                    scopes.Add(UnitScope(unit, plc.Path + "/units/" + Segment(unit.Name), "softwareUnit"));
                }
                foreach (var unit in provider.UnitGroup.SafetyUnits)
                {
                    if (!budget.Take()) break;
                    scopes.Add(UnitScope(unit, plc.Path + "/safetyUnits/" + Segment(unit.Name), "safetyUnit"));
                }
            }
            catch (Exception exception)
            {
                if (warnings == null) throw new BridgeException("TraversalIncomplete", "Software-unit traversal failed: " + exception.Message);
                warnings.Add("Software units unavailable at " + plc.Path + ": " + exception.Message);
            }
            return scopes;
        }

        private static SoftwareScope UnitScope(PlcUnitBase unit, string path, string kind) => new SoftwareScope
        { Name = unit.Name, Kind = kind, Path = path, NamespacePreset = unit.NamespacePreset, Blocks = unit.BlockGroup, Tags = unit.TagTableGroup, Types = unit.TypeGroup };

        private static IEnumerable<BlockEntry> ScopeBlocks(SoftwareScope scope, ScanBudget budget)
        {
            foreach (var block in Blocks(scope.Blocks, scope.Path + "/blocks", budget, 0)) yield return block;
            if (scope.Blocks is PlcBlockSystemGroup system)
                foreach (var group in system.SystemBlockGroups)
                    foreach (var block in SystemBlocks(group, scope.Path + "/blocks/systemGroups/" + Segment(group.Name), budget, 0)) yield return block;
        }

        private static IEnumerable<BlockEntry> Blocks(PlcBlockGroup group, string root, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var block in group.Blocks)
            {
                if (!budget.Take()) yield break;
                yield return new BlockEntry { Item = block, Path = ObjectIdentity.ObjectPath(root, block.Name, block.Namespace) };
            }
            foreach (var child in group.Groups)
            {
                if (!budget.Take()) yield break;
                foreach (var block in Blocks(child, root + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return block;
            }
        }

        private static IEnumerable<BlockEntry> SystemBlocks(PlcSystemBlockGroup group, string path, ScanBudget budget, int depth)
        {
            if (depth > 64 || !budget.Take()) { budget.Truncated = true; yield break; }
            foreach (var block in group.Blocks)
            {
                if (!budget.Take()) yield break;
                yield return new BlockEntry { Item = block, Path = ObjectIdentity.ObjectPath(path, block.Name, block.Namespace) };
            }
            foreach (var child in group.Groups)
                foreach (var block in SystemBlocks(child, path + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return block;
        }

        private static IEnumerable<TypeEntry> ScopeTypes(SoftwareScope scope, ScanBudget budget)
        {
            foreach (var entry in Types(scope.Types, scope.Path + "/types", budget, 0)) yield return entry;
            if (scope.Types is PlcTypeSystemGroup system)
                foreach (var group in system.SystemTypeGroups)
                {
                    if (!budget.Take()) yield break;
                    foreach (var type in group.Types)
                    {
                        if (!budget.Take()) yield break;
                        yield return new TypeEntry { Item = type, Path = ObjectIdentity.ObjectPath(scope.Path + "/types/systemGroups/" + Segment(group.Name), type.Name, type.Namespace) };
                    }
                }
        }

        private static IEnumerable<TypeEntry> Types(PlcTypeGroup group, string path, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var type in group.Types)
            {
                if (!budget.Take()) yield break;
                yield return new TypeEntry { Item = type, Path = ObjectIdentity.ObjectPath(path, type.Name, type.Namespace) };
            }
            foreach (var child in group.Groups)
            {
                if (!budget.Take()) yield break;
                foreach (var type in Types(child, path + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return type;
            }
        }

        private static IEnumerable<TagTableEntry> TagTables(PlcTagTableGroup group, string path, ScanBudget budget, int depth = 0)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var table in group.TagTables)
            {
                if (!budget.Take()) yield break;
                yield return new TagTableEntry { Item = table, Path = path + "/" + Segment(table.Name) };
            }
            foreach (var child in group.Groups)
            {
                if (!budget.Take()) yield break;
                foreach (var table in TagTables(child, path + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return table;
            }
        }

        // --- small helpers (same wording and limits as the V21 worker) ---

        private static JObject BlockInfo(BlockEntry block, string plcPath) => new JObject { ["path"] = block.Path, ["plcPath"] = plcPath,
            ["name"] = block.Item.Name, ["number"] = block.Item.Number, ["kind"] = block.Item.GetType().Name,
            ["programmingLanguage"] = block.Item.ProgrammingLanguage.ToString(), ["isConsistent"] = block.Item.IsConsistent,
            ["isKnowHowProtected"] = block.Item.IsKnowHowProtected };

        private static JObject IdentityInfo(Project project, string plcPath, string scopePath, string objectPath, string kind, string name) => new JObject
            { ["projectPath"] = project.Path?.FullName, ["plcPath"] = plcPath, ["scopePath"] = scopePath, ["objectPath"] = objectPath, ["kind"] = kind, ["name"] = name,
                ["namespace"] = ObjectIdentity.NamespaceFromPath(objectPath) };

        private static JObject ProjectInfo(Project project) => new JObject { ["name"] = project.Name, ["path"] = project.Path?.FullName, ["tiaVersion"] = Program.TiaMajor };
        private JObject Envelope() => new JObject { ["origin"] = Origin, ["observedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["processId"] = _processId };
        private static string Segment(string value) => Uri.EscapeDataString(value ?? "");
        private void EnsureAttached() { if (_portal == null) throw new BridgeException("NotAttached", "Attach to or explicitly start a TIA V19 instance first."); }
        private void EnsureDetached() { if (_portal != null) throw new BridgeException("AlreadyAttached", "Detach the current session before attaching to or starting another instance."); }

        private static void RequireSelector(string path, string name, string pathKey, string nameKey)
        { if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(name)) throw new BridgeException("InvalidRequest", "Specify " + pathKey + " or " + nameKey + "."); }

        private static void RequireOne(int count, ScanBudget budget, string kind)
        {
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Lookup reached its traversal bound; target uniqueness is not established.");
            if (count == 0) throw new BridgeException("ObjectNotFound", "No matching " + kind + " was found.");
            if (count != 1) throw new BridgeException("AmbiguousTarget", "More than one " + kind + " matches; specify its exact path.");
        }

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

        private static FileInfo NewArtifact(JObject args, string prefix)
        {
            string path = args.Value<string>("artifactDirectory");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new BridgeException("InvalidRequest", "An absolute artifactDirectory on the TIA host is required.");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(path);
            var file = new FileInfo(Path.Combine(path, prefix + "-" + Guid.NewGuid().ToString("N") + ".xml"));
            if (file.Exists) throw new BridgeException("ArtifactExists", "Refusing to overwrite an existing export.");
            return file;
        }

        private static JObject FileArtifact(FileInfo file)
        {
            string hash;
            using (var stream = file.OpenRead())
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return new JObject { ["path"] = file.FullName, ["bytes"] = file.Length, ["sha256"] = hash, ["mediaType"] = "application/xml" };
        }

        private static bool ProjectPathIsAbsolute(string path) => !string.IsNullOrWhiteSpace(path) &&
            (Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]") ||
             path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
             path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length >= 3);

        private sealed class ScanBudget
        {
            internal int Used;
            internal bool Truncated;
            private readonly int _maximum;
            internal ScanBudget(int maximum) { _maximum = maximum; }
            internal bool Take() { if (Used >= _maximum) { Truncated = true; return false; } Used++; return true; }
        }

        private sealed class HardwareEntry { internal DeviceItem Item; internal string Path; }
        private sealed class BlockEntry { internal PlcBlock Item; internal string Path; }
        private sealed class TypeEntry { internal PlcType Item; internal string Path; }
        private sealed class TagTableEntry { internal PlcTagTable Item; internal string Path; }
        private sealed class PlcSelection { internal Project Project; internal PlcSoftware Software; internal string Path; }
        private sealed class SoftwareScope { internal string Path, Name, Kind, NamespacePreset; internal PlcBlockGroup Blocks; internal PlcTagTableGroup Tags; internal PlcTypeGroup Types; }
    }
}
