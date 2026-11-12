using System;
using System.Collections.Generic;
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

namespace TiaMcp.Openness.V21
{
    /// <summary>
    /// Project selection, bounded traversal and argument helpers shared by the V21 and V19 sessions. Compiled into each worker
    /// against its own Siemens.Engineering API, so both build the same canonical object paths.
    /// </summary>
    internal abstract class OpennessTraversal
    {
        internal sealed class HardwareEntry { internal DeviceItem Item; internal string Path; }
        internal sealed class BlockEntry { internal PlcBlock Item; internal string Path; internal bool NativeSystemGroup; }
        internal sealed class TypeEntry { internal PlcType Item; internal string Path; }
        internal sealed class TagTableEntry { internal PlcTagTable Item; internal string Path; }
        internal sealed class PlcSelection { internal Project Project; internal PlcSoftware Software; internal string Path; }
        internal sealed class SoftwareScope { internal string Path, Name, Kind; internal PlcUnitBase Unit; internal PlcBlockGroup Blocks; internal PlcTagTableGroup Tags; internal PlcTypeGroup Types; }

        internal static Project SelectProject(TiaPortal portal, JObject args)
        {
            string name = args.Value<string>("projectName");
            var matches = portal.Projects.Where(p => string.IsNullOrWhiteSpace(name) || string.Equals(p.Name, name, StringComparison.Ordinal)).Take(2).ToList();
            if (matches.Count == 0) throw new BridgeException("ProjectNotFound", "No matching open project was found.");
            if (matches.Count != 1) throw new BridgeException("AmbiguousTarget", "More than one open project matches; specify projectName.");
            return matches[0];
        }

        internal static PlcSelection SelectPlc(Project project, JObject args)
        {
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

        internal static IEnumerable<HardwareEntry> DeviceItems(Project project, ScanBudget budget)
        {
            string root = "/projects/" + Segment(project.Name);
            foreach (var entry in Devices(project.Devices, root + "/devices", budget)) yield return entry;
            foreach (var entry in Devices(project.UngroupedDevicesGroup.Devices, root + "/ungrouped", budget)) yield return entry;
            foreach (var entry in DeviceGroups(project.DeviceGroups, root + "/groups", budget, 0)) yield return entry;
        }

        internal static IEnumerable<HardwareEntry> DeviceGroups(IEnumerable<DeviceUserGroup> groups, string root, ScanBudget budget, int depth)
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

        internal static IEnumerable<HardwareEntry> Devices(IEnumerable<Device> devices, string root, ScanBudget budget)
        {
            foreach (var device in devices)
            {
                if (!budget.Take()) yield break;
                foreach (var entry in Items(device.DeviceItems, root + "/" + Segment(device.Name), budget, 0)) yield return entry;
            }
        }

        internal static IEnumerable<HardwareEntry> Items(IEnumerable<DeviceItem> items, string root, ScanBudget budget, int depth)
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

        internal static IEnumerable<BlockEntry> ScopeBlocks(SoftwareScope scope, ScanBudget budget)
        {
            foreach (var block in Blocks(scope.Blocks, scope.Path + "/blocks", budget)) yield return block;
            if (scope.Blocks is PlcBlockSystemGroup system)
                foreach (var group in system.SystemBlockGroups)
                    foreach (var block in SystemBlocks(group, scope.Path + "/blocks/systemGroups/" + Segment(group.Name), budget, 0)) yield return block;
        }

        internal static IEnumerable<BlockEntry> Blocks(PlcBlockGroup group, string root, ScanBudget budget, int depth = 0)
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

        internal static IEnumerable<BlockEntry> SystemBlocks(PlcSystemBlockGroup group, string path, ScanBudget budget, int depth)
        {
            if (depth > 64 || !budget.Take()) { budget.Truncated = true; yield break; }
            foreach (var block in group.Blocks)
            {
                if (!budget.Take()) yield break;
                yield return new BlockEntry { Item = block, Path = ObjectIdentity.ObjectPath(path, block.Name, block.Namespace), NativeSystemGroup = true };
            }
            foreach (var child in group.Groups)
                foreach (var block in SystemBlocks(child, path + "/groups/" + Segment(child.Name), budget, depth + 1)) yield return block;
        }

        internal static IEnumerable<TypeEntry> ScopeTypes(SoftwareScope scope, ScanBudget budget)
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

        internal static IEnumerable<TypeEntry> Types(PlcTypeGroup group, string path, ScanBudget budget, int depth)
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

        internal static IEnumerable<TagTableEntry> TagTables(PlcTagTableGroup group, string path, ScanBudget budget, int depth = 0)
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

        internal static JObject BlockInfo(BlockEntry block, string plcPath) => new JObject { ["path"] = block.Path, ["plcPath"] = plcPath,
            ["name"] = block.Item.Name, ["number"] = block.Item.Number, ["kind"] = block.Item.GetType().Name,
            ["programmingLanguage"] = block.Item.ProgrammingLanguage.ToString(), ["isConsistent"] = block.Item.IsConsistent,
            ["isKnowHowProtected"] = block.Item.IsKnowHowProtected };

        // Same fields and order as BlockInfo, from one batched read.
        internal static JObject BlockInfo(BlockEntry block, string plcPath, BatchedAttributeReads.BlockAttributes values) => new JObject { ["path"] = block.Path, ["plcPath"] = plcPath,
            ["name"] = values.Name, ["number"] = values.Number, ["kind"] = block.Item.GetType().Name,
            ["programmingLanguage"] = values.ProgrammingLanguage, ["isConsistent"] = values.IsConsistent,
            ["isKnowHowProtected"] = values.IsKnowHowProtected };

        internal static string Segment(string value) => Uri.EscapeDataString(value ?? "");

        internal static int RequiredInteger(JObject args, string name)
        {
            if (args[name]?.Type != JTokenType.Integer || args.Value<long>(name) < 1 || args.Value<long>(name) > int.MaxValue)
                throw new BridgeException("InvalidRequest", name + " must be a positive integer.");
            return args.Value<int>(name);
        }

        internal static int Limit(JObject args, int defaultValue)
        {
            if (args["maxItems"] == null) return defaultValue;
            int value = RequiredInteger(args, "maxItems");
            if (value > 2000) throw new BridgeException("InvalidRequest", "maxItems must be between 1 and 2000.");
            return value;
        }

        internal static JObject FileArtifact(FileInfo file, string mediaType = "application/xml")
        {
            string hash;
            using (var stream = file.OpenRead())
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return new JObject { ["path"] = file.FullName, ["bytes"] = file.Length, ["sha256"] = hash, ["mediaType"] = mediaType };
        }

        internal static bool ProjectPathIsAbsolute(string path) => !string.IsNullOrWhiteSpace(path) &&
            (Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]") ||
             path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
             path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length >= 3);
    }
}
