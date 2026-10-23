#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;

namespace TiaMcp.Openness.V21
{
    internal static class DeviceCompilation
    {
        internal sealed class Entry { internal Device Item; internal string Path; }

        internal static List<Entry> Entries(Project project)
        {
            var entries = new List<Entry>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var devices = new HashSet<Device>();
            int visited = 0;
            bool Take() => visited++ < 50000;
            Action<IEnumerable<Device>, string> collect = (items, parent) => {
                foreach (var device in items)
                {
                    if (string.IsNullOrWhiteSpace(device.Name)) throw new BridgeException("TargetIdentityUnavailable", "Device compiler name is missing.");
                    if (!Take()) throw new BridgeException("TraversalLimit", "Device compiler inventory exceeds its bound.");
                    string path = parent + "/" + Uri.EscapeDataString(device.Name);
                    if (!paths.Add(path) || !devices.Add(device)) throw new BridgeException("AmbiguousTarget", "Device compiler identity is repeated: " + path);
                    entries.Add(new Entry { Item = device, Path = path });
                }
            };
            Action<IEnumerable<DeviceUserGroup>, string, int> groups = null;
            groups = (items, parent, depth) => {
                foreach (var group in items)
                {
                    if (string.IsNullOrWhiteSpace(group.Name)) throw new BridgeException("TargetIdentityUnavailable", "Device compiler group name is missing.");
                    if (depth > 64) throw new BridgeException("TraversalLimit", "Device compiler groups exceed nesting limit.");
                    if (!Take()) throw new BridgeException("TraversalLimit", "Device compiler inventory exceeds its bound.");
                    string path = parent + "/" + Uri.EscapeDataString(group.Name);
                    if (!paths.Add(path)) throw new BridgeException("AmbiguousTarget", "Device compiler group identity is repeated: " + path);
                    collect(group.Devices, path + "/devices");
                    groups(group.Groups, path + "/groups", depth + 1);
                }
            };
            if (string.IsNullOrWhiteSpace(project.Name)) throw new BridgeException("TargetIdentityUnavailable", "Compiler project name is missing.");
            string root = "/projects/" + Uri.EscapeDataString(project.Name);
            collect(project.Devices, root + "/devices");
            collect(project.UngroupedDevicesGroup.Devices, root + "/ungrouped");
            groups(project.DeviceGroups, root + "/groups", 0);
            return entries;
        }

        internal sealed class ItemEntry { internal DeviceItem Item; internal string Path; internal string DevicePath; }

        internal static List<ItemEntry> Items(Project project)
        {
            var result = new List<ItemEntry>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var objects = new HashSet<DeviceItem>();
            Action<IEnumerable<DeviceItem>, string, string, int> collect = null;
            collect = (items, parent, device, depth) => {
                foreach (var item in items)
                {
                    if (depth > 64 || result.Count >= 50000) throw new BridgeException("TraversalLimit", "Compiler hardware selection exceeds its bound.");
                    if (string.IsNullOrWhiteSpace(item.Name)) throw new BridgeException("TargetIdentityUnavailable", "Compiler hardware name is missing.");
                    string path = parent + "/items/" + Uri.EscapeDataString(item.Name);
                    if (!paths.Add(path) || !objects.Add(item)) throw new BridgeException("AmbiguousTarget", "Compiler hardware identity is repeated: " + path);
                    result.Add(new ItemEntry { Item = item, Path = path, DevicePath = device });
                    collect(item.DeviceItems, path, device, depth + 1);
                }
            };
            foreach (var entry in Entries(project)) collect(entry.Item.DeviceItems, entry.Path, entry.Path, 0);
            return result;
        }

        internal static JObject Describe(CompilerResult result, string target)
        {
            var messages = new JArray(); bool truncated = false; int visited = 0;
            Action<IEnumerable<CompilerResultMessage>, int> collect = null;
            collect = (items, depth) => {
                if (depth > 64) { truncated = true; return; }
                foreach (var item in items)
                {
                    if (visited++ >= 500) { truncated = true; return; }
                    messages.Add(new JObject { ["description"] = item.Description, ["path"] = item.Path, ["state"] = item.State.ToString(), ["depth"] = depth });
                    collect(item.Messages, depth + 1);
                    if (truncated) return;
                }
            };
            collect(result.Messages, 0);
            return new JObject { ["target"] = target, ["state"] = result.State.ToString(), ["errorCount"] = result.ErrorCount,
                ["warningCount"] = result.WarningCount, ["messages"] = messages, ["messagesTruncated"] = truncated };
        }

        internal static void RequireTargets(Project project, IEnumerable<string> expected) => RequireTargets(Entries(project), expected);

        private static void RequireTargets(List<Entry> entries, IEnumerable<string> expected)
        {
            var paths = expected?.Take(50001).ToArray();
            if (paths == null || paths.Length == 0 || paths.Length > 50000 || paths.Any(string.IsNullOrWhiteSpace) ||
                paths.Distinct(StringComparer.Ordinal).Count() != paths.Length ||
                !new HashSet<string>(paths, StringComparer.Ordinal).SetEquals(entries.Select(entry => entry.Path)))
                throw new BridgeException("CompileScopeChanged", "Device compiler targets differ from the independently authorized inventory.");
        }

        internal static JObject Compile(Project project, Action<string, string> journal = null, IEnumerable<string> expectedTargets = null)
        {
            var entries = Entries(project);
            if (expectedTargets != null) RequireTargets(entries, expectedTargets);
            if (entries.Count == 0) throw new BridgeException("UnsupportedCapability", "No project device compiler target exists.");
            // Resolve every service before the first compiler can change project state.
            var selected = entries.Select(entry => new { Entry = entry, Compiler = entry.Item.GetService<ICompilable>()
                ?? throw new BridgeException("UnsupportedCapability", "Device compiler unavailable: " + entry.Path) }).ToList();
            var targets = new JArray(); int errors = 0, warnings = 0;
            foreach (var target in selected)
            {
                journal?.Invoke(target.Entry.Path, "dispatching");
                var compiled = target.Compiler.Compile();
                errors = checked(errors + compiled.ErrorCount); warnings = checked(warnings + compiled.WarningCount);
                var receipt = Describe(compiled, target.Entry.Item.Name);
                receipt["targetPath"] = target.Entry.Path;
                targets.Add(receipt);
                journal?.Invoke(target.Entry.Path, "completed");
            }
            var after = Entries(project).ToDictionary(entry => entry.Path, entry => entry.Item, StringComparer.Ordinal);
            if (after.Count != entries.Count || entries.Any(entry => !after.TryGetValue(entry.Path, out var item) || !Equals(item, entry.Item)))
                throw new BridgeException("CompileScopeChanged", "Device identities changed during compilation; coverage is unqualified.");
            return new JObject { ["errorCount"] = errors, ["warningCount"] = warnings, ["targets"] = targets,
                ["requiredTargets"] = new JArray(entries.Select(entry => entry.Path)), ["targetIdentityPolicy"] = "device-path-v1",
                ["scope"] = "All devices of the explicitly selected affected project.", ["scopeComplete"] = true };
        }
    }
}
