using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Spool only inventory fields, rather than putting all channel/software metadata in MCP.
        private JObject DeviceInventoryCapture(JObject args)
        {
            var project = SelectProject(args);
            string wanted = args.Value<string>("devicePath");
            bool modifiedBefore = project.IsModified;
            int maximum = IndexInteger(args, "maxVisited", 50000, 100, 50000);
            var budget = new ScanBudget(maximum); var warnings = new JArray();
            var file = new FileInfo(Path.Combine(ArtifactDirectory(args), "device-inventory-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            int count = 0; long bytes = 0; var started = DateTime.UtcNow;
            string projectPath = project.Path == null ? null : project.Path.FullName;
            if (projectPath == null) throw new BridgeException("ProjectIdentityUnavailable", "A project file path is required for inventory provenance.");
            string root = "/projects/" + Segment(project.Name);
            using (var stream = new FileStream(file.FullName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                Action<JObject> emit = row =>
                {
                    if (budget.Truncated) return;
                    string line = row.ToString(Formatting.None);
                    int size = Encoding.UTF8.GetByteCount(line) + 1;
                    if (bytes + size > 48L * 1024 * 1024 || size > 96 * 1024)
                    { budget.Truncated = true; warnings.Add("Inventory reached its 48 MiB spool / 96 KiB row bound."); return; }
                    writer.WriteLine(line); bytes += size; count++;
                };
                InventoryDevices(project.Devices, root + "/devices", projectPath, budget, emit, warnings, wanted);
                InventoryDevices(project.UngroupedDevicesGroup.Devices, root + "/ungrouped", projectPath, budget, emit, warnings, wanted);
                InventoryGroups(project.DeviceGroups, root + "/groups", projectPath, budget, emit, warnings, 0, wanted);
            }
            if (wanted != null && count == 0 && !budget.Truncated && warnings.Count == 0)
                throw new BridgeException("DeviceNotFound", "No device matches the exact devicePath.");
            var result = Envelope();
            result["project"] = ProjectInfo(project); result["capturedAtUtc"] = started.ToString("O", CultureInfo.InvariantCulture);
            var artifact = FileArtifact(file); artifact["mediaType"] = "application/x-ndjson";
            result["artifact"] = artifact; result["rowCount"] = count;
            result["coverage"] = new JObject { ["scopeComplete"] = !budget.Truncated && warnings.Count == 0,
                ["truncated"] = budget.Truncated, ["visitedItems"] = budget.Used, ["maxVisited"] = maximum, ["warnings"] = warnings,
                ["devicePath"] = wanted, ["projectModifiedBefore"] = modifiedBefore, ["projectModifiedAfter"] = project.IsModified,
                ["scope"] = "Configured devices and device items, including built-in submodules. Field availability is independent of traversal completeness. Capture is not transactional." };
            return result;
        }

        private static void InventoryGroups(IEnumerable<DeviceUserGroup> groups, string root, string projectPath,
            ScanBudget budget, Action<JObject> emit, JArray warnings, int depth, string wanted)
        {
            if (depth > 64) { budget.Truncated = true; return; }
            try
            {
                foreach (var group in groups)
                {
                    if (!budget.Take()) return;
                    string path = root + "/" + Segment(group.Name);
                    InventoryDevices(group.Devices, path + "/devices", projectPath, budget, emit, warnings, wanted);
                    InventoryGroups(group.Groups, path + "/groups", projectPath, budget, emit, warnings, depth + 1, wanted);
                }
            }
            catch (Exception error) { warnings.Add(root + ": " + error.GetType().Name); }
        }

        private static void InventoryDevices(IEnumerable<Device> devices, string root, string projectPath,
            ScanBudget budget, Action<JObject> emit, JArray warnings, string wanted)
        {
            try
            {
                foreach (var device in devices)
                {
                    if (!budget.Take()) return;
                    string path = root + "/" + Segment(device.Name);
                    if (wanted != null && !string.Equals(path, wanted, StringComparison.Ordinal)) continue;
                    emit(InventoryNativeRow(device, device.Name, device.Name, "device", path, null, projectPath));
                    InventoryItems(device.DeviceItems, path, projectPath, device.Name, budget, emit, warnings, 0);
                }
            }
            catch (Exception error) { warnings.Add(root + ": " + error.GetType().Name); }
        }

        private static void InventoryItems(IEnumerable<DeviceItem> items, string root, string projectPath, string deviceName,
            ScanBudget budget, Action<JObject> emit, JArray warnings, int depth)
        {
            if (depth > 64) { budget.Truncated = true; return; }
            try
            {
                foreach (var item in items)
                {
                    if (!budget.Take()) return;
                    string path = root + "/items/" + Segment(item.Name);
                    string kind = item.Classification.ToString() == "CPU" ? "cpu" : item.Classification.ToString() == "HM" ? "headModule" : item.IsBuiltIn ? "builtIn" : "module";
                    emit(InventoryNativeRow(item, item.Name, deviceName, kind, path, root, projectPath));
                    InventoryItems(item.DeviceItems, path, projectPath, deviceName, budget, emit, warnings, depth + 1);
                }
            }
            catch (Exception error) { warnings.Add(root + ": " + error.GetType().Name); }
        }

        private static JObject InventoryNativeRow(IEngineeringObject target, string name, string deviceName,
            string kind, string path, string parentPath, string projectPath)
        {
            var fields = new JObject(); HashSet<string> names = null;
            try { names = new HashSet<string>(target.GetAttributeInfos().Select(a => a.Name), StringComparer.Ordinal); }
            catch { /* Each field explicitly records unavailable attribute metadata below. */ }
            foreach (var pair in new[] { new[] { "plantDesignation", "PlantDesignation" }, new[] { "locationIdentifier", "LocationIdentifier" },
                new[] { "productDesignation", "ProductDesignation" }, new[] { "mlfb", "OrderNumber" }, new[] { "firmware", "FirmwareVersion" },
                new[] { "hardwareRevision", "HardwareRevision" }, new[] { "slot", "PositionNumber" }, new[] { "typeIdentifier", "TypeIdentifier" } })
            {
                string source = path + "@" + pair[1];
                if (names == null) { fields[pair[0]] = InventoryMissing("readError", source, "Attribute metadata could not be read."); continue; }
                if (!names.Contains(pair[1])) { fields[pair[0]] = InventoryMissing("notExposed", source, "This engineering object does not expose this attribute."); continue; }
                try { fields[pair[0]] = InventoryValue(target.GetAttribute(pair[1]), source); }
                catch (Exception error) { fields[pair[0]] = InventoryMissing("readError", source, error.GetType().Name); }
            }
            fields["ip"] = InventoryMissing("notExposed", path, "No IP address exposed on this object.");
            if (target is DeviceItem item)
            {
                try
                {
                    var network = item.GetService<NetworkInterface>();
                    if (network != null)
                    {
                        var values = new List<string>(); int visited = 0;
                        foreach (var node in network.Nodes)
                        {
                            if (++visited > 64) throw new InvalidOperationException("Network node bound exceeded.");
                            if (!node.GetAttributeInfos().Any(a => a.Name == "Address")) continue;
                            var address = Convert.ToString(node.GetAttribute("Address"), CultureInfo.InvariantCulture);
                            IPAddress parsed;
                            // Numeric PROFIBUS/MPI addresses must not be interpreted as IPv4 integers.
                            if ((address.Contains(".") || address.Contains(":")) && IPAddress.TryParse(address, out parsed) && address != "0.0.0.0") values.Add(address);
                        }
                        fields["ip"] = values.Count > 0 ? InventoryValue(string.Join("; ", values.Distinct()), path + "@NetworkInterface.Nodes.Address") :
                            InventoryMissing("notConfigured", path, "No configured IPv4/IPv6 address on this interface.");
                    }
                }
                catch (Exception error) { fields["ip"] = InventoryMissing("readError", path, error.GetType().Name); }
            }
            return new JObject { ["rowId"] = path, ["name"] = name, ["deviceName"] = deviceName, ["kind"] = kind,
                ["path"] = path, ["parentPath"] = parentPath, ["projectPath"] = projectPath, ["configured"] = fields };
        }

        private static JObject InventoryValue(object value, string source)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text)) return InventoryMissing("notConfigured", source, "The source returned an empty value.");
            if (text.Length > 4096) return InventoryMissing("readError", source, "Value exceeds the 4096 character field bound; it was not silently truncated.");
            return new JObject { ["value"] = text, ["state"] = "available", ["source"] = source, ["reason"] = null };
        }
        private static JObject InventoryMissing(string state, string source, string reason) =>
            new JObject { ["value"] = null, ["state"] = state, ["source"] = source, ["reason"] = reason };
    }
}
