using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.SW.OpcUa.AccessControl;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly string[] OpcUaPermissionNames = { "Browse", "Read", "Write", "Call", "ReceiveEvents", "ReadRolePermissions" };
        // Exact payload fields per prepared OPC UA kind; anything else (passwords, certificates, users) is refused.
        private static readonly Dictionary<string, string[]> OpcUaPayloadFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["opcua_server_interface_import"] = new[] { "interfaceName", "mode", "path", "expectedSha256" },
            ["opcua_server_interface_delete"] = new[] { "interfaceName" },
            ["opcua_server_interface_enable"] = new[] { "interfaceName", "enabled" },
            ["opcua_reference_namespace_import"] = new[] { "namespaceName", "mode", "path", "expectedSha256" },
            ["opcua_reference_namespace_delete"] = new[] { "namespaceName" },
            ["opcua_role_mapping_set"] = new[] { "action", "roleName", "definedInNamespace", "projectRole", "namespacePermissions" }
        };

        // The V21 XML does not name the service owner: try the PLC software, then its owning device items.
        private static OpcUaProvider OpcUaProviderFor(PlcSoftware software)
        {
            var provider = software.GetService<OpcUaProvider>();
            for (IEngineeringInstance owner = software.Parent; provider == null && owner != null && !(owner is Device); owner = owner.Parent)
                provider = (owner as DeviceItem)?.GetService<OpcUaProvider>();
            return provider;
        }

        private static ServerInterfaceGroup OpcUaGroup(PlcSelection plc) => OpcUaProviderFor(plc.Software)?.CommunicationGroup?.ServerInterfaceGroup
            ?? throw new BridgeException("UnsupportedCapability", "The selected PLC exposes no OPC UA server interface group; an S7-1500 CPU with OPC UA is required.");

        private static JObject OpcUaSnapshot(ServerInterfaceGroup group, ScanBudget budget) => group == null ? null : new JObject
        {
            ["serverInterfaces"] = new JArray(group.ServerInterfaces.TakeWhile(_ => budget.Take()).Select(item => new JObject { ["name"] = item.Name, ["enabled"] = item.Enabled, ["lastModified"] = item.LastModified.ToString("O") })),
            ["simaticInterfaces"] = new JArray(group.SimaticInterfaces.TakeWhile(_ => budget.Take()).Select(item => new JObject { ["name"] = item.Name, ["enabled"] = item.Enabled, ["useStringNodeIds"] = item.UseStringNodeIds, ["lastModified"] = item.LastModified.ToString("O") })),
            ["referenceNamespaces"] = new JArray(group.ReferenceNamespaces.TakeWhile(_ => budget.Take()).Select(OpcUaNamespaceInfo)),
            ["roleMappings"] = group.AccessControl == null ? null : new JArray(group.AccessControl.RoleMappings.TakeWhile(_ => budget.Take()).Select(OpcUaRoleInfo))
        };

        private static JObject OpcUaNamespaceInfo(ReferenceNamespace item) => new JObject { ["name"] = item.Name, ["enabled"] = item.Enabled, ["generateNodes"] = item.GenerateNodes,
            ["generatedInterfaceName"] = item.GeneratedInterfaceName, ["useStringNodeIds"] = item.UseStringNodeIds, ["lastModified"] = item.LastModified.ToString("O") };

        private static JObject OpcUaRoleInfo(RoleMapping role) => new JObject { ["name"] = role.Name, ["roleId"] = role.RoleId, ["definedInNamespace"] = role.DefinedInNamespace,
            ["projectRole"] = role.ProjectRole, ["namespacePermissions"] = new JArray(role.NamespacePermissions.Select(item => new JObject { ["namespaceUri"] = item.NamespaceUri,
                ["Browse"] = item.Browse, ["Read"] = item.Read, ["Write"] = item.Write, ["Call"] = item.Call, ["ReceiveEvents"] = item.ReceiveEvents, ["ReadRolePermissions"] = item.ReadRolePermissions })) };

        private static ServerInterface OpcUaUserInterface(ServerInterfaceGroup group, string name)
        {
            Func<string, bool> same = value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
            if (group.SimaticInterfaces.Any(item => same(item.Name))) throw new BridgeException("ProtectedObject", "SIMATIC server interfaces are excluded from OPC UA interface changes.");
            if (group.ReferenceNamespaces.Any(item => same(item.GeneratedInterfaceName))) throw new BridgeException("GeneratedObject", "Interfaces generated from a reference (companion) namespace are excluded; change the reference namespace instead.");
            return group.ServerInterfaces.FirstOrDefault(item => same(item.Name));
        }

        private static ReferenceNamespace OpcUaNamespace(ServerInterfaceGroup group, string name) => group.ReferenceNamespaces.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

        private static RoleMappingComposition OpcUaRoles(ServerInterfaceGroup group) => group.AccessControl?.RoleMappings ?? throw new BridgeException("UnsupportedCapability", "The selected PLC exposes no OPC UA access control.");

        private static RoleMapping OpcUaRole(ServerInterfaceGroup group, string name) => OpcUaRoles(group).FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase));

        private void PrepareOpcUaChange(EngineeringChange change, JObject preview)
        {
            var payload = change.Payload;
            var unsupported = payload.Properties().Select(p => p.Name).Except(OpcUaPayloadFields[change.Kind], StringComparer.Ordinal).ToArray();
            if (unsupported.Length != 0) throw new BridgeException("InvalidRequest", "Unsupported OPC UA payload fields: " + string.Join(", ", unsupported) + ". Passwords, certificates and user credentials are never accepted.");
            if (change.Selector["scopePath"]?.Type == JTokenType.String) throw new BridgeException("InvalidRequest", "OPC UA changes target one PLC; scopePath is not supported.");
            var group = OpcUaGroup(SelectPlc(change.Selector));
            var budget = new ScanBudget(5000);
            preview["opcUaBefore"] = OpcUaSnapshot(group, budget);
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "The OPC UA server interface group exceeds 5000 entries.");
            var effects = (JArray)preview["effects"];
            ((JArray)preview["warnings"]).Add("OPC UA clients are not discovered: removed or changed nodes, namespaces or role permissions can break them. Mapping validity is established only by native import and PLC compile diagnostics.");
            if (change.Kind == "opcua_role_mapping_set") { PrepareOpcUaRole(change, group, effects); return; }
            bool reference = change.Kind.StartsWith("opcua_reference_namespace_", StringComparison.Ordinal), import = change.Kind.EndsWith("_import", StringComparison.Ordinal);
            string name = ChangeText(payload, reference ? "namespaceName" : "interfaceName", 128);
            change.ObjectNames = new[] { name };
            var target = reference ? (object)OpcUaNamespace(group, name) : OpcUaUserInterface(group, name);
            string mode = import ? ChangeText(payload, "mode", 16) : "existing";
            if (import && mode != "create" && mode != "replace") throw new BridgeException("InvalidRequest", "mode must be create or replace.");
            if (mode == "create" && target != null) throw new BridgeException("ObjectExists", "An OPC UA object with this name already exists; use mode=replace.");
            if (mode != "create" && target == null) throw new BridgeException("ObjectNotFound", "No user-defined OPC UA " + (reference ? "reference namespace" : "server interface") + " has this name.");
            if (target != null)
            {
                string before = Path.Combine(change.Directory, "before-opcua.xml");
                if (reference) ((ReferenceNamespace)target).Export(new FileInfo(before)); else ((ServerInterface)target).Export(new FileInfo(before));
                change.Backups.Add(new JObject { ["name"] = name, ["artifact"] = ChangeArtifact(before) });
            }
            if (import)
            {
                string path = ChangeText(payload, "path", 4096), hash = ChangeText(payload, "expectedSha256", 64).ToLowerInvariant();
                if (!ProjectPathIsAbsolute(path) || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("InvalidRequest", "path must name an existing absolute, non-linked file.");
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new BridgeException("InputLimitExceeded", "OPC UA import files are limited to 16 MiB.");
                change.InputPath = Path.Combine(change.Directory, "requested-opcua.xml");
                File.Copy(path, change.InputPath);
                change.InputHash = ChangeFileHash(change.InputPath);
                if (change.InputHash != hash) throw new BridgeException("ArtifactChanged", "The import file does not match expectedSha256.");
            }
            if (change.Kind == "opcua_server_interface_enable") ExplicitChangeBool(payload, "enabled");
            effects.Add(new JObject { ["action"] = change.Kind, ["name"] = name, ["mode"] = mode, ["sourceSha256"] = change.InputHash, ["enabled"] = payload["enabled"]?.DeepClone(),
                ["generatedInterfaceName"] = (target as ReferenceNamespace)?.GeneratedInterfaceName });
        }

        private static void PrepareOpcUaRole(EngineeringChange change, ServerInterfaceGroup group, JArray effects)
        {
            var payload = change.Payload;
            string action = ChangeText(payload, "action", 16), name = ChangeText(payload, "roleName", 128);
            change.ObjectNames = new[] { name };
            bool exists = OpcUaRole(group, name) != null;
            if (action == "remove")
            {
                if (payload.Count != 2) throw new BridgeException("InvalidRequest", "action=remove takes only roleName.");
                if (!exists) throw new BridgeException("ObjectNotFound", "No OPC UA role mapping has this name.");
            }
            else if (action == "create")
            {
                if (exists) throw new BridgeException("ObjectExists", "An OPC UA role mapping with this name already exists; remove it first.");
                ChangeText(payload, "definedInNamespace", 1024);
                if (payload["projectRole"] != null) ChangeText(payload, "projectRole", 128);
                OpcUaRequestedPermissions(payload);
            }
            else throw new BridgeException("InvalidRequest", "action must be create or remove.");
            effects.Add(new JObject { ["action"] = action, ["roleName"] = name, ["definedInNamespace"] = payload["definedInNamespace"]?.DeepClone(),
                ["projectRole"] = payload["projectRole"]?.DeepClone(), ["namespacePermissions"] = payload["namespacePermissions"]?.DeepClone() });
        }

        private static JObject[] OpcUaRequestedPermissions(JObject payload)
        {
            var entries = payload["namespacePermissions"] as JArray;
            if (entries == null || entries.Count < 1 || entries.Count > 64) throw new BridgeException("InvalidRequest", "namespacePermissions must list 1-64 namespaces.");
            var fields = new[] { "namespaceUri" }.Concat(OpcUaPermissionNames).ToArray();
            foreach (var entry in entries)
            {
                var row = entry as JObject;
                if (row == null || row.Count != fields.Length || row.Properties().Any(p => !fields.Contains(p.Name)) || OpcUaPermissionNames.Any(n => row[n].Type != JTokenType.Boolean))
                    throw new BridgeException("InvalidRequest", "Each namespacePermissions entry needs namespaceUri and explicit booleans for " + string.Join(", ", OpcUaPermissionNames) + ".");
                ChangeText(row, "namespaceUri", 1024);
            }
            if (entries.Select(e => e.Value<string>("namespaceUri")).Distinct(StringComparer.Ordinal).Count() != entries.Count) throw new BridgeException("InvalidRequest", "namespacePermissions namespaces must be distinct.");
            return entries.Cast<JObject>().ToArray();
        }

        private void ApplyOpcUaChange(EngineeringChange change)
        {
            var payload = change.Payload;
            var group = OpcUaGroup(SelectPlc(change.Selector));
            string name = change.ObjectNames[0];
            var missing = new BridgeException("ObjectNotFound", "The prepared OPC UA object no longer exists.");
            JObject readback;
            if (change.Kind == "opcua_role_mapping_set")
            {
                if (payload.Value<string>("action") == "remove")
                {
                    (OpcUaRole(group, name) ?? throw missing).Delete();
                    if (OpcUaRole(group, name) != null) throw new BridgeException("MutationVerificationFailed", "Removed OPC UA role mapping still exists.");
                    readback = new JObject { ["name"] = name, ["exists"] = false };
                }
                else
                {
                    var role = OpcUaRoles(group).Create(name, payload.Value<string>("definedInNamespace"));
                    if (payload["projectRole"] != null) role.SetProjectRole(payload.Value<string>("projectRole"));
                    var wanted = OpcUaRequestedPermissions(payload);
                    foreach (var row in wanted)
                    {
                        string uri = row.Value<string>("namespaceUri");
                        var permission = role.NamespacePermissions.FirstOrDefault(item => item.NamespaceUri == uri) ?? throw new BridgeException("MutationVerificationFailed", "The new role exposes no permissions for namespace " + uri + ".");
                        foreach (string permissionName in OpcUaPermissionNames) permission.SetPermission(permissionName, row.Value<bool>(permissionName));
                    }
                    readback = OpcUaRoleInfo(OpcUaRole(group, name) ?? throw new BridgeException("MutationVerificationFailed", "Created OPC UA role mapping was not found."));
                    var actual = (JArray)readback["namespacePermissions"];
                    if (payload["projectRole"] != null && readback.Value<string>("projectRole") != payload.Value<string>("projectRole") || wanted.Any(row => !actual.Any(item => JToken.DeepEquals(item, row))))
                        throw new BridgeException("MutationVerificationFailed", "OPC UA role mapping read-back differs from the requested project role or namespace permissions.");
                }
            }
            else if (change.Kind.StartsWith("opcua_server_interface_", StringComparison.Ordinal))
            {
                var target = OpcUaUserInterface(group, name);
                if (change.Kind == "opcua_server_interface_delete")
                {
                    (target ?? throw missing).Delete();
                    if (OpcUaUserInterface(group, name) != null) throw new BridgeException("MutationVerificationFailed", "Deleted OPC UA server interface still exists.");
                    readback = new JObject { ["name"] = name, ["exists"] = false };
                }
                else
                {
                    if (change.Kind == "opcua_server_interface_enable") (target ?? throw missing).Enabled = payload.Value<bool>("enabled");
                    else (target ?? group.ServerInterfaces.Create(name)).Import(new FileInfo(change.InputPath));
                    target = OpcUaUserInterface(group, name) ?? throw new BridgeException("MutationVerificationFailed", "OPC UA server interface was not found after the change.");
                    if (change.Kind == "opcua_server_interface_enable" && target.Enabled != payload.Value<bool>("enabled")) throw new BridgeException("MutationVerificationFailed", "OPC UA server interface enabled state did not match the request.");
                    readback = new JObject { ["name"] = target.Name, ["enabled"] = target.Enabled, ["lastModified"] = target.LastModified.ToString("O") };
                    if (change.InputPath != null) OpcUaExportReadback(change, readback, target.Export);
                }
            }
            else
            {
                var target = OpcUaNamespace(group, name);
                if (change.Kind == "opcua_reference_namespace_delete")
                {
                    (target ?? throw missing).Delete();
                    if (OpcUaNamespace(group, name) != null) throw new BridgeException("MutationVerificationFailed", "Deleted OPC UA reference namespace still exists.");
                    readback = new JObject { ["name"] = name, ["exists"] = false };
                }
                else
                {
                    if (target == null) group.ReferenceNamespaces.Create(name, new FileInfo(change.InputPath)); else target.Import(new FileInfo(change.InputPath));
                    readback = OpcUaNamespaceInfo(target = OpcUaNamespace(group, name) ?? throw new BridgeException("MutationVerificationFailed", "OPC UA reference namespace was not found after import."));
                    OpcUaExportReadback(change, readback, target.Export);
                }
            }
            readback["role"] = "opcua-readback";
            change.Readbacks.Add(readback);
        }

        // Native export is the only public content readback; equality with the input is evidence, not a requirement.
        private static void OpcUaExportReadback(EngineeringChange change, JObject readback, Action<FileInfo> export)
        {
            string after = Path.Combine(change.Directory, "after-opcua.xml");
            export(new FileInfo(after));
            readback["artifact"] = ChangeArtifact(after);
            readback["exportMatchesInput"] = readback["artifact"].Value<string>("sha256") == change.InputHash;
            readback["mappingStatus"] = "No public mapping-status property exists; see the PLC compile diagnostics of this change.";
        }
    }
}
