using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.OpcUa;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private sealed class OpcUaCpu { internal HardwareEntry Cpu; internal PlcSelection Plc; }

        private OpcUaCpu SelectOpcUaCpu(JObject args)
        {
            var project = SelectProject(args);
            string hardwarePath = args.Value<string>("hardwarePath"), plcPath = args.Value<string>("plcPath");
            var budget = new ScanBudget(50000);
            var matches = new List<OpcUaCpu>();
            foreach (var entry in DeviceItems(project, budget))
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (software == null) continue;
                string path = entry.Path + "/plc/" + Segment(software.Name);
                if ((string.IsNullOrWhiteSpace(hardwarePath) || entry.Path == hardwarePath) && (string.IsNullOrWhiteSpace(plcPath) || path == plcPath))
                    matches.Add(new OpcUaCpu { Cpu = entry, Plc = new PlcSelection { Project = project, Software = software, Path = path } });
            }
            RequireOne(matches.Count, budget, "PLC CPU");
            return matches[0];
        }

        private static ServerInterfaceGroup OpcUaServerInterfaces(OpcUaCpu target) =>
            target.Plc.Software.GetService<OpcUaProvider>()?.CommunicationGroup?.ServerInterfaceGroup;

        private JObject OpcUaEngineeringInspect(JObject args)
        {
            var target = SelectOpcUaCpu(args);
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var result = Envelope();
            result["project"] = ProjectInfo(target.Plc.Project);
            result["hardwarePath"] = target.Cpu.Path;
            result["plcPath"] = target.Plc.Path;
            result["typeIdentifier"] = target.Cpu.Item.TypeIdentifier;
            // V21 documents no fixed OPC UA attribute list per CPU, so settings are discovered by native name.
            var settings = new JArray();
            foreach (var entry in new[] { target.Cpu }.Concat(Items(target.Cpu.Item.DeviceItems, target.Cpu.Path, budget, 0)))
            {
                var attributes = InspectionAttributes(entry.Item, budget, warnings, entry.Path, null, name => name.IndexOf("OpcUa", StringComparison.OrdinalIgnoreCase) >= 0);
                if (attributes.Count > 0) settings.Add(new JObject { ["hardwarePath"] = entry.Path, ["name"] = entry.Item.Name, ["attributes"] = attributes });
            }
            result["serverSettings"] = settings;
            var group = OpcUaServerInterfaces(target);
            result["serverInterfaceGroup"] = group == null ? "notExposed" : "observed";
            if (group != null)
            {
                string path = target.Plc.Path + "/opcua";
                result["serverInterfaces"] = OpcUaRows(() => group.ServerInterfaces, budget, warnings, path + "/serverInterfaces", i => new JObject {
                    ["name"] = i.Name, ["enabled"] = i.Enabled, ["author"] = i.Author, ["creationTime"] = i.CreationTime, ["lastModified"] = i.LastModified });
                result["simaticInterfaces"] = OpcUaRows(() => group.SimaticInterfaces, budget, warnings, path + "/simaticInterfaces", i => new JObject {
                    ["name"] = i.Name, ["enabled"] = i.Enabled, ["author"] = i.Author, ["useStringNodeIds"] = i.UseStringNodeIds,
                    ["creationTime"] = i.CreationTime, ["lastModified"] = i.LastModified });
                result["referenceNamespaces"] = OpcUaRows(() => group.ReferenceNamespaces, budget, warnings, path + "/referenceNamespaces", i => new JObject {
                    ["name"] = i.Name, ["enabled"] = i.Enabled, ["author"] = i.Author, ["generateNodes"] = i.GenerateNodes,
                    ["generatedInterfaceName"] = i.GeneratedInterfaceName, ["useStringNodeIds"] = i.UseStringNodeIds,
                    ["creationTime"] = i.CreationTime, ["lastModified"] = i.LastModified });
                result["namespaceAccessRestrictions"] = OpcUaRows(() => group.AccessControl.NamespaceAccessRestrictions, budget, warnings, path + "/accessControl/namespaceAccessRestrictions", r => new JObject {
                    ["namespaceUri"] = r.NamespaceUri, ["namespaceIndex"] = r.NamespaceIndex, ["applyRestrictionsToBrowse"] = r.ApplyRestrictionsToBrowse,
                    ["encryptionRequired"] = r.EncryptionRequired, ["sessionRequired"] = r.SessionRequired, ["signingRequired"] = r.SigningRequired });
                result["roleMappings"] = OpcUaRows(() => group.AccessControl.RoleMappings, budget, warnings, path + "/accessControl/roleMappings", r => new JObject {
                    ["name"] = r.Name, ["roleId"] = r.RoleId, ["definedInNamespace"] = r.DefinedInNamespace, ["projectRole"] = r.ProjectRole,
                    ["namespacePermissions"] = OpcUaRows(() => r.NamespacePermissions, budget, warnings, path + "/accessControl/roleMappings/" + Segment(r.Name), p => new JObject {
                        ["namespaceUri"] = p.NamespaceUri, ["namespaceIndex"] = p.NamespaceIndex, ["browse"] = p.Browse, ["read"] = p.Read, ["write"] = p.Write,
                        ["call"] = p.Call, ["receiveEvents"] = p.ReceiveEvents, ["readRolePermissions"] = p.ReadRolePermissions }) });
            }
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "Offline OPC UA server configuration of one PLC CPU exposed by V21 Openness: OpcUa-named CPU/sub-item attributes, server, SIMATIC and reference-namespace interfaces, namespace access restrictions and role mappings.",
                "Server settings are only CPU and sub-item attributes whose native names contain OpcUa; use hardware_inspect for all attributes",
                "Interface node content is read through opcua_engineering_export and opcua_inspect",
                "Multilingual comments, legacy OPC UA users, certificates and client interfaces are not read", "No online or runtime server state");
            return result;
        }

        private JObject OpcUaEngineeringExport(JObject args)
        {
            var target = SelectOpcUaCpu(args);
            string category = args.Value<string>("category"), name = args.Value<string>("name");
            int max = Limit(args, 32);
            if (max > 32) throw new BridgeException("InvalidRequest", "maxItems must be between 1 and 32.");
            if (!new[] { "server", "serverInterface", "simaticInterface", "referenceNamespace" }.Contains(category))
                throw new BridgeException("InvalidRequest", "category must be server, serverInterface, simaticInterface or referenceNamespace.");
            if (category == "server" && name != null) throw new BridgeException("InvalidRequest", "The whole-server export takes no name.");
            var budget = new ScanBudget(max);
            var files = new JArray();
            var warnings = new JArray();
            void Export(string itemName, Action<FileInfo> export)
            {
                if (name != null && itemName != name || !budget.Take()) return;
                var file = NewInspectionArtifact(args, "opcua-" + category);
                try { export(file); file.Refresh(); files.Add(new JObject { ["category"] = category, ["name"] = itemName, ["artifact"] = FileArtifact(file) }); }
                catch (Exception exception) { warnings.Add(InspectionWarning(target.Plc.Path + "/opcua/" + category + "/" + Segment(itemName), exception)); }
            }
            var group = category == "server" ? null : OpcUaServerInterfaces(target)
                ?? throw new BridgeException("UnsupportedCapability", "The selected PLC does not expose an OPC UA server interface group.");
            switch (category)
            {
                case "server":
                    // Siemens documents this hardware utility by identifier; it exports the CPU's whole OPC UA address space.
                    var provider = target.Plc.Project.HwUtilities.Find("OPCUAExportProvider") as OpcUaExportProvider
                        ?? throw new BridgeException("UnsupportedCapability", "The project does not expose the OPC UA export hardware utility.");
                    Export(target.Cpu.Item.Name, file => provider.Export(target.Cpu.Item, file));
                    break;
                case "serverInterface": foreach (var item in group.ServerInterfaces) Export(item.Name, item.Export); break;
                case "simaticInterface": foreach (var item in group.SimaticInterfaces) Export(item.Name, item.Export); break;
                default: foreach (var item in group.ReferenceNamespaces) Export(item.Name, item.Export); break;
            }
            if (name != null && files.Count == 0 && warnings.Count == 0) throw new BridgeException("ObjectNotFound", "No " + category + " named " + name + " was found.");
            var result = Envelope();
            result["project"] = ProjectInfo(target.Plc.Project);
            result["hardwarePath"] = target.Cpu.Path;
            result["plcPath"] = target.Plc.Path;
            result["category"] = category;
            result["files"] = files;
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "Unchanged native OPC UA XML exports of the selected category; export failures are listed as warnings.",
                "No import, generation or online export", "Export bytes are not schema-validated by the worker");
            return result;
        }

        private static JArray OpcUaRows<T>(Func<IEnumerable<T>> source, ScanBudget budget, JArray warnings, string path, Func<T, JObject> row)
        {
            var rows = new JArray();
            try { foreach (var item in source()) { if (!budget.Take()) break; rows.Add(row(item)); } }
            catch (Exception exception) { warnings.Add(InspectionWarning(path, exception)); }
            return rows;
        }
    }
}
