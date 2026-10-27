using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Explorer network scan: network interfaces only, without channel/address
        // attributes. hardwarePath selects one exact item; null scans all hardware.
        private JObject InspectModelNetworks(Project project, int maximum, string hardwarePath)
        {
            var lookup = new ScanBudget(50000); var budget = new ScanBudget(maximum);
            var warnings = new JArray(); var items = new JArray();
            var hardware = DeviceItems(project, lookup).ToArray();
            if (lookup.Truncated) warnings.Add("Hardware lookup reached its 50000-item traversal bound.");
            foreach (var entry in hardware)
            {
                if (hardwarePath != null && !string.Equals(entry.Path, hardwarePath, StringComparison.Ordinal)) continue;
                if (!budget.Take()) break;
                try
                {
                    var network = entry.Item.GetService<NetworkInterface>();
                    if (network != null) items.Add(new JObject { ["path"] = entry.Path, ["network"] = InspectConfiguredNetwork(project, entry, network, hardware, budget, warnings) });
                }
                catch (Exception error) { warnings.Add(InspectionWarning(entry.Path, error)); }
            }
            return new JObject { ["items"] = items, ["warnings"] = warnings,
                ["completeness"] = InspectionCoverage(budget, warnings, "Configured network interfaces, subnet memberships, IO-system associations and port interconnections of bounded project hardware.",
                    "Configured port interconnections only; no cables or physical topology") };
        }

        private JObject InspectConfiguredNetwork(Project project, HardwareEntry owner, NetworkInterface network,
            HardwareEntry[] hardware, ScanBudget budget, JArray warnings)
        {
            var nodes = new JArray(); var controllers = new JArray(); var connectors = new JArray();
            var result = new JObject { ["interfaceType"] = network.InterfaceType.ToString(),
                ["operatingMode"] = network.InterfaceOperatingMode.ToString(), ["nodes"] = nodes,
                ["ioControllers"] = controllers, ["ioConnectors"] = connectors,
                ["origin"] = "nativeConfiguredOffline", ["physicalTopologyVerified"] = false };
            int failuresBefore = warnings.Count;
            try
            {
                foreach (var node in network.Nodes)
                {
                    if (!budget.Take()) break;
                    var row = new JObject(); nodes.Add(row);
                    try
                    {
                        row["name"] = node.Name; row["type"] = node.NodeType.ToString();
                        row["subnet"] = InspectSubnetReference(project, node.ConnectedSubnet);
                        row["attributes"] = InspectionAttributes(node, budget, warnings, owner.Path + "/network/node");
                    }
                    catch (Exception error) { row["unavailable"] = error.Message; warnings.Add(InspectionWarning(owner.Path, error)); }
                }
            }
            catch (Exception error) { warnings.Add(InspectionWarning(owner.Path + "/network/nodes", error)); }
            try
            {
                int controllerIndex = 0;
                foreach (var controller in network.IoControllers)
                {
                    if (!budget.Take()) break;
                    var row = new JObject { ["ownerHardwarePath"] = owner.Path, ["controllerIndex"] = controllerIndex++ }; controllers.Add(row);
                    try
                    {
                        row["ioSystem"] = InspectIoSystemReference(project, controller.IoSystem, hardware);
                        var assigned = new JArray(); row["assignedConnectors"] = assigned;
                        if (controller.IoSystem != null)
                            foreach (var connector in controller.IoSystem.ConnectedIoDevices)
                            {
                                if (!budget.Take()) break;
                                try { assigned.Add(NetworkOwnerReference(connector, hardware)); }
                                catch (Exception error) { assigned.Add(new JObject { ["resolution"] = "unavailable", ["reason"] = error.Message }); warnings.Add(InspectionWarning(owner.Path, error)); }
                            }
                        row["attributes"] = InspectionAttributes(controller, budget, warnings, owner.Path + "/network/controller");
                    }
                    catch (Exception error) { row["unavailable"] = error.Message; warnings.Add(InspectionWarning(owner.Path, error)); }
                }
            }
            catch (Exception error) { warnings.Add(InspectionWarning(owner.Path + "/network/controllers", error)); }
            try
            {
                int connectorIndex = 0;
                foreach (var connector in network.IoConnectors)
                {
                    if (!budget.Take()) break;
                    var row = new JObject { ["ownerHardwarePath"] = owner.Path, ["connectorIndex"] = connectorIndex++ }; connectors.Add(row);
                    try
                    {
                        row["connectedToIoSystem"] = InspectIoSystemReference(project, connector.ConnectedToIoSystem, hardware);
                        row["attributes"] = InspectionAttributes(connector, budget, warnings, owner.Path + "/network/connector");
                    }
                    catch (Exception error) { row["unavailable"] = error.Message; warnings.Add(InspectionWarning(owner.Path, error)); }
                }
            }
            catch (Exception error) { warnings.Add(InspectionWarning(owner.Path + "/network/connectors", error)); }
            var ports = new JArray(); result["ports"] = ports;
            try
            {
                int portIndex = 0;
                foreach (var port in network.Ports)
                {
                    if (!budget.Take()) break;
                    var row = new JObject { ["portIndex"] = portIndex++ }; ports.Add(row);
                    try
                    {
                        row["port"] = NetworkOwnerReference(port, hardware);
                        var partners = new JArray(); row["connectedPorts"] = partners;
                        foreach (var partner in port.ConnectedPorts)
                        {
                            if (!budget.Take()) break;
                            partners.Add(NetworkOwnerReference(partner, hardware));
                        }
                    }
                    catch (Exception error) { row["unavailable"] = error.Message; warnings.Add(InspectionWarning(owner.Path, error)); }
                }
            }
            catch (Exception error) { warnings.Add(InspectionWarning(owner.Path + "/network/ports", error)); }
            bool unresolved = result.Descendants().OfType<JProperty>().Any(property => property.Name == "resolution" && (string)property.Value != "resolved");
            result["scopeComplete"] = !budget.Truncated && warnings.Count == failuresBefore && !unresolved;
            result["associationBasis"] = "Native controller/connector associations and engineering-object equality; names and address overlap never establish ownership.";
            return result;
        }

        private static JObject InspectIoSystemReference(Project project, IoSystem system, HardwareEntry[] hardware)
        {
            if (system == null) return new JObject { ["state"] = "notAssigned" };
            var subnet = InspectSubnetReference(project, system.Subnet);
            // Explorer identity of a native IO system below its resolved global subnet.
            string path = subnet.Value<string>("path");
            return new JObject { ["state"] = "assigned", ["name"] = system.Name, ["number"] = system.Number,
                ["path"] = path == null ? null : path + "/ioSystems/" + Segment(system.Name),
                ["controllerOwner"] = NetworkOwnerReference(system, hardware), ["subnet"] = subnet };
        }

        private static JObject InspectSubnetReference(Project project, Subnet subnet)
        {
            if (subnet == null) return new JObject { ["state"] = "notConnected" };
            // Only equality with a project-owned global subnet establishes this canonical identity.
            bool global = project.Subnets.Take(5000).Any(candidate => Equals(candidate, subnet));
            return new JObject { ["state"] = "connected", ["name"] = subnet.Name,
                ["path"] = global ? "/projects/" + Segment(project.Name) + "/subnets/" + Segment(subnet.Name) : null,
                ["resolution"] = global ? "resolved" : "unresolved",
                ["reason"] = global ? null : "Native subnet is not in the first 5000 observed global project subnets; internal subnet identity is not inferred." };
        }

        private static JObject NetworkOwnerReference(IEngineeringObject item, HardwareEntry[] hardware)
        {
            for (int depth = 0; item != null && depth < 32; depth++)
            {
                if (item is DeviceItem device)
                {
                    var matches = hardware.Where(entry => Equals(entry.Item, device)).Take(2).ToArray();
                    if (matches.Length == 1) return new JObject { ["resolution"] = "resolved", ["ownerHardwarePath"] = matches[0].Path };
                    break;
                }
                if (item is IoSystem system) item = system.Parent;
                else if (item is IoController controller) item = controller.Parent;
                else if (item is IoConnector connector) item = connector.Parent;
                else if (item is NetworkInterface network) item = network.OwnedBy;
                else if (item is NetworkPort port) item = port.OwnedBy;
                else break;
            }
            return new JObject { ["resolution"] = "unresolved", ["ownerHardwarePath"] = null,
                ["reason"] = "Native association owner was not uniquely resolved within the bounded hardware capture." };
        }
    }
}
