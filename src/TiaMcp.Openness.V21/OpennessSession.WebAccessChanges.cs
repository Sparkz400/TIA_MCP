using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.Umac;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Exact payload fields per kind; passwords, aliases, activation and authentication settings are never accepted.
        private static readonly Dictionary<string, string[]> WebAccessPayloadFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["webserver_page_set"] = new[] { "webServerActive", "systemWebPages", "defaultApplication" },
            ["webserver_user_pages_generate"] = new[] { "htmlDirectory", "defaultPage", "applicationName", "override", "overrideBlocks" },
            ["umac_role_set"] = new[] { "action", "roleName", "userName", "engineeringRights", "deviceRights" }
        };

        private static void RequireWebAccessFields(EngineeringChange change)
        {
            var unsupported = change.Payload.Properties().Select(p => p.Name).Except(WebAccessPayloadFields[change.Kind], StringComparer.Ordinal).ToArray();
            if (unsupported.Length != 0) throw new BridgeException("InvalidRequest", "Unsupported payload fields: " + string.Join(", ", unsupported) + ". Passwords, aliases, activation and authentication settings are never accepted.");
            if (change.Selector["scopePath"]?.Type == JTokenType.String) throw new BridgeException("InvalidRequest", change.Kind + " does not support scopePath.");
        }

        // Web features live on the CPU DeviceItem that owns the PLC software, as OpcUaProvider does.
        private static DeviceItem WebServerCpu(PlcSoftware software)
        {
            for (IEngineeringInstance owner = software.Parent; owner != null && !(owner is Device); owner = owner.Parent)
                if (owner is DeviceItem item && (item.GetService<DefaultWebPagesFeature>() != null || item.GetService<SystemWebPagesFeature>() != null || item.GetService<WebserverUserDefinedPages>() != null)) return item;
            throw new BridgeException("UnsupportedCapability", "The selected PLC's CPU exposes no web server page service.");
        }

        private static bool? WebServerActive(DeviceItem cpu)
        {
            try { return cpu.GetAttribute("WebserverActivate") is bool on ? on : (bool?)null; } catch (Exception) { return null; }
        }

        private static void RequireWebServerActive(DeviceItem cpu)
        {
            if (WebServerActive(cpu) != true) throw new BridgeException("WebServerNotActivated", "Web serving is not activated on this CPU (Boolean WebserverActivate attribute); activate it with webServerActive in the same or an earlier change.");
        }

        private static JObject WebServerPages(DeviceItem cpu)
        {
            var system = cpu.GetService<SystemWebPagesFeature>(); var pages = cpu.GetService<DefaultWebPagesFeature>();
            var applications = pages?.WebApplicationConfigurations.Take(513).ToArray();
            if (applications?.Length > 512) throw new BridgeException("TraversalLimit", "The CPU has more than 512 web applications.");
            return new JObject { ["systemWebPagesEnabled"] = system?.EnableSystemWebPages, ["applications"] = applications == null ? null
                : new JArray(applications.Select(a => new JObject { ["name"] = a.Name, ["applicationType"] = a.ApplicationType.ToString(), ["isDefault"] = a.IsDefault })) };
        }

        private static JArray WebServerFingerprint(Project project)
        {
            var budget = new ScanBudget(50000); var result = new JArray();
            foreach (var entry in DeviceItems(project, budget))
                if (entry.Item.GetService<SystemWebPagesFeature>() != null || entry.Item.GetService<DefaultWebPagesFeature>() != null)
                {
                    object active = null;
                    try { active = entry.Item.GetAttribute("WebserverActivate"); } catch (Exception) { }
                    result.Add(new JObject { ["path"] = entry.Path, ["webserverActivate"] = active is bool on ? (JToken)on : null, ["pages"] = WebServerPages(entry.Item) });
                }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Web server fingerprint is incomplete.");
            return result;
        }

        private static string WebDefaultApplication(JObject pages)
        {
            var defaults = (pages["applications"] as JArray ?? throw new BridgeException("UnsupportedCapability", "The CPU exposes no DefaultWebPagesFeature."))
                .Where(a => a.Value<bool>("isDefault")).Select(a => a.Value<string>("name")).ToArray();
            if (defaults.Length > 1) throw new BridgeException("AmbiguousTarget", "More than one web application is marked default.");
            return defaults.FirstOrDefault();
        }

        private static JObject WebSetting(JObject payload, string name)
        {
            var setting = payload[name] as JObject;
            if (setting == null || setting.Count != 2 || setting["expected"] == null || setting["value"] == null)
                throw new BridgeException("InvalidRequest", name + " must be { expected, value }: the current value and the requested value.");
            return setting;
        }

        private static string WebApplicationName(JObject setting, string name) => setting[name].Type == JTokenType.Null ? null : ChangeText(setting, name, 256);

        private void PrepareWebServerPageSet(EngineeringChange change, JObject preview)
        {
            RequireWebAccessFields(change);
            var payload = change.Payload;
            if (payload.Count == 0) throw new BridgeException("InvalidRequest", "Name webServerActive, systemWebPages and/or defaultApplication.");
            var plc = SelectPlc(change.Selector); var cpu = WebServerCpu(plc.Software);
            var before = WebServerPages(cpu); before["webserverActivate"] = WebServerActive(cpu); var after = (JObject)before.DeepClone();
            var effects = (JArray)preview["effects"];
            if (payload["webServerActive"] != null)
            {
                var setting = WebSetting(payload, "webServerActive");
                bool expected = ExplicitChangeBool(setting, "expected"), value = ExplicitChangeBool(setting, "value");
                if (before["webserverActivate"].Type != JTokenType.Boolean || !cpu.GetAttributeInfos().Any(i => i.Name == "WebserverActivate" && (i.AccessMode & EngineeringAttributeAccessMode.Write) != 0))
                    throw new BridgeException("UnsupportedCapability", "The CPU exposes no writable Boolean WebserverActivate attribute.");
                if (before.Value<bool>("webserverActivate") != expected) throw new BridgeException("StaleRevision", "webServerActive.expected differs from the current WebserverActivate value.");
                after["webserverActivate"] = value;
                // Natively, switching web serving off also clears EnableSystemWebPages (probe wp_c236a724).
                if (!value && after["systemWebPagesEnabled"]?.Type == JTokenType.Boolean) after["systemWebPagesEnabled"] = false;
                effects.Add(new JObject { ["plcPath"] = plc.Path, ["setting"] = "WebserverActivate", ["before"] = expected, ["after"] = value });
                ((JArray)preview["warnings"]).Add("WebserverActivate changes whether the CPU serves web pages once this configuration is downloaded. Natively, switching it on also makes TIA create a web server certificate in the CPU's local store and assign it to WebserverCertificate (probe wp_c236a724); web users and rights are not changed.");
            }
            if ((payload["systemWebPages"] != null || payload["defaultApplication"] != null) && after.Value<bool?>("webserverActivate") != true)
                throw new BridgeException("WebServerNotActivated", "Page settings require web serving to be active on this CPU, now or through webServerActive in this change.");
            if (payload["systemWebPages"] != null)
            {
                var setting = WebSetting(payload, "systemWebPages");
                bool expected = ExplicitChangeBool(setting, "expected"), value = ExplicitChangeBool(setting, "value");
                if (before["systemWebPagesEnabled"].Type != JTokenType.Boolean) throw new BridgeException("UnsupportedCapability", "The CPU exposes no SystemWebPagesFeature.");
                if (before.Value<bool>("systemWebPagesEnabled") != expected) throw new BridgeException("StaleRevision", "systemWebPages.expected differs from the current EnableSystemWebPages value.");
                after["systemWebPagesEnabled"] = value;
                effects.Add(new JObject { ["plcPath"] = plc.Path, ["setting"] = "EnableSystemWebPages", ["before"] = expected, ["after"] = value });
            }
            if (payload["defaultApplication"] != null)
            {
                var setting = WebSetting(payload, "defaultApplication");
                string expected = WebApplicationName(setting, "expected"), value = WebApplicationName(setting, "value");
                if (WebDefaultApplication(before) != expected) throw new BridgeException("StaleRevision", "defaultApplication.expected differs from the current default web application.");
                if (value != null && !after["applications"].Any(a => a.Value<string>("name") == value)) throw new BridgeException("ObjectNotFound", "No web application has exactly the name defaultApplication.value.");
                foreach (var application in after["applications"]) application["isDefault"] = application.Value<string>("name") == value;
                effects.Add(new JObject { ["plcPath"] = plc.Path, ["setting"] = "defaultApplication", ["before"] = expected, ["after"] = value });
            }
            preview["webServerBefore"] = before;
            preview["webServerAfter"] = after;
            ((JArray)preview["warnings"]).Add("Offline project settings only: nothing is downloaded or deployed, and browser reachability, web user rights and certificates are not checked. Application names and types never change; apply reads back webServerAfter exactly.");
        }

        private void ApplyWebServerPageSet(EngineeringChange change)
        {
            var payload = change.Payload; var cpu = WebServerCpu(SelectPlc(change.Selector).Software);
            if (payload["webServerActive"] is JObject activation) cpu.SetAttribute("WebserverActivate", activation.Value<bool>("value"));
            if (payload["systemWebPages"] != null || payload["defaultApplication"] != null) RequireWebServerActive(cpu);
            if (payload["systemWebPages"] is JObject pages)
                (cpu.GetService<SystemWebPagesFeature>() ?? throw new BridgeException("UnsupportedCapability", "SystemWebPagesFeature is no longer exposed.")).EnableSystemWebPages = pages.Value<bool>("value");
            if (payload["defaultApplication"] is JObject selection)
            {
                string value = selection.Value<string>("value");
                var applications = (cpu.GetService<DefaultWebPagesFeature>() ?? throw new BridgeException("UnsupportedCapability", "DefaultWebPagesFeature is no longer exposed.")).WebApplicationConfigurations.ToArray();
                if (value != null) (applications.FirstOrDefault(a => a.Name == value) ?? throw new BridgeException("ObjectNotFound", "The requested default web application no longer exists.")).IsDefault = true;
                // Clear any other default the native setter left in place, so exactly the requested selection remains.
                foreach (var application in applications.Where(a => a.Name != value && a.IsDefault)) application.IsDefault = false;
            }
            var after = WebServerPages(cpu); after["webserverActivate"] = WebServerActive(cpu);
            change.Readbacks.Add(new JObject { ["role"] = "webserver-pages-readback", ["after"] = after });
            if (!JToken.DeepEquals(after, change.Preview["webServerAfter"])) throw new BridgeException("MutationVerificationFailed", "Web page read-back differs from the previewed webServerAfter.");
        }

        private static string WebSourceFile(string snapshot, string page)
        {
            string root = Path.GetFullPath(snapshot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, file = Path.GetFullPath(Path.Combine(snapshot, page));
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) throw new BridgeException("InvalidRequest", "defaultPage must name a file by its path relative to htmlDirectory.");
            return file;
        }

        private static string WebSourceSnapshot(EngineeringChange change) => Path.Combine(change.Directory, "web-source");

        // Checked before the checkpoint: apply generates from the prepared copy, never from the caller's directory.
        private static void WebSourceUnchanged(EngineeringChange change)
        {
            if (LibraryDirectoryHash(WebSourceSnapshot(change)) != change.Preview["webSource"].Value<string>("sha256"))
                throw new BridgeException("ArtifactChanged", "The prepared web page source copy changed after preview.");
        }

        private static HashSet<string> WebBlockNames(PlcSelection plc)
        {
            var budget = new ScanBudget(50000);
            var names = new HashSet<string>(AllBlocks(plc, budget).Select(b => b.Item.Name), StringComparer.OrdinalIgnoreCase);
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Block inventory is incomplete.");
            return names;
        }

        private void PrepareWebServerUserPages(EngineeringChange change, JObject preview)
        {
            RequireWebAccessFields(change);
            var payload = change.Payload; var plc = SelectPlc(change.Selector); var cpu = WebServerCpu(plc.Software);
            RequireWebServerActive(cpu);
            if (cpu.GetService<WebserverUserDefinedPages>() == null) throw new BridgeException("UnsupportedCapability", "The CPU exposes no WebserverUserDefinedPages service.");
            string source = ChangeText(payload, "htmlDirectory", 4096), page = ChangeText(payload, "defaultPage", 1024), application = ChangeText(payload, "applicationName", 128);
            if (!ProjectPathIsAbsolute(source) || !Directory.Exists(source)) throw new BridgeException("InvalidRequest", "htmlDirectory must name an existing absolute directory.");
            bool overwrite = ExplicitChangeBool(payload, "override");
            var named = overwrite ? ChangeNames(payload, "overrideBlocks") : payload["overrideBlocks"] == null ? new string[0] : throw new BridgeException("InvalidRequest", "overrideBlocks requires override=true.");
            string snapshot = WebSourceSnapshot(change);
            Directory.CreateDirectory(snapshot);
            LibraryCopyDirectory(source, snapshot);
            WebSourceFile(snapshot, page);
            var budget = new ScanBudget(50000); var blocks = AllBlocks(plc, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Block inventory is incomplete.");
            foreach (string name in named)
            {
                var matches = blocks.Where(b => string.Equals(b.Item.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches.Length != 1 || !(matches[0].Item is DataBlock)) throw new BridgeException("ObjectNotFound", "Each overrideBlocks entry must name exactly one existing data block: " + name);
                string backup = Path.Combine(change.Directory, "before-" + Guid.NewGuid().ToString("N") + ".xml");
                matches[0].Item.Export(new FileInfo(backup), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                change.Backups.Add(new JObject { ["blockName"] = matches[0].Item.Name, ["blockPath"] = matches[0].Path, ["artifact"] = ChangeArtifact(backup) });
            }
            change.ObjectNames = named;
            preview["webSource"] = new JObject { ["sha256"] = LibraryDirectoryHash(snapshot), ["defaultPage"] = page, ["applicationName"] = application };
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = "generateUserPageBlocks", ["plcPath"] = plc.Path, ["option"] = overwrite ? "Override" : "None", ["overrideBlocks"] = new JArray(named),
                ["affectedSet"] = "The block identities GenerateBlocks returns; apply lists them in its readback." });
            ((JArray)preview["warnings"]).Add("Generated DB names and numbers follow the CPU's web settings and are known only after generation. With override=false native generation refuses an existing target DB; with override=true the overwritten pre-existing blocks must equal overrideBlocks exactly, otherwise apply fails after the checkpoint and recovery is required. Generated block internals are never edited, no delete API exists and nothing is downloaded.");
        }

        private void ApplyWebServerUserPages(EngineeringChange change)
        {
            var payload = change.Payload; var plc = SelectPlc(change.Selector); var cpu = WebServerCpu(plc.Software);
            RequireWebServerActive(cpu);
            var service = cpu.GetService<WebserverUserDefinedPages>() ?? throw new BridgeException("UnsupportedCapability", "WebserverUserDefinedPages is no longer exposed.");
            string snapshot = WebSourceSnapshot(change);
            var before = WebBlockNames(plc);
            var generated = (service.GenerateBlocks(new DirectoryInfo(snapshot), new FileInfo(WebSourceFile(snapshot, payload.Value<string>("defaultPage"))), payload.Value<string>("applicationName"),
                payload.Value<bool>("override") ? WebDBGenerateOptions.Override : WebDBGenerateOptions.None) ?? new List<PlcBlock>()).ToArray();
            change.Stages.Add("webserver_blocks_generated");
            var overwritten = generated.Select(b => b.Name).Where(before.Contains).ToArray();
            var after = WebBlockNames(plc);
            change.Readbacks.Add(new JObject { ["role"] = "webserver-generated-blocks", ["overwritten"] = new JArray(overwritten),
                ["blocks"] = new JArray(generated.Select(b => new JObject { ["name"] = b.Name, ["number"] = b.Number, ["type"] = b.GetType().Name })) });
            if (generated.Length == 0) throw new BridgeException("MutationVerificationFailed", "GenerateBlocks returned no blocks.");
            if (!new HashSet<string>(overwritten, StringComparer.OrdinalIgnoreCase).SetEquals(change.ObjectNames)) throw new BridgeException("MutationVerificationFailed", "The overwritten pre-existing blocks differ from overrideBlocks.");
            if (!before.IsSubsetOf(after) || generated.Any(b => !after.Contains(b.Name))) throw new BridgeException("MutationVerificationFailed", "A pre-existing or generated block is missing after generation.");
        }

        private static List<KeyValuePair<string, UmacDevice>> UmacDevices(Project project)
        {
            // UMAC device function rights belong to the Device that owns each top-level item, as access_inspect scopes them.
            var budget = new ScanBudget(50000); var devices = new List<KeyValuePair<string, UmacDevice>>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in DeviceItems(project, budget))
            {
                if (!(entry.Item.Parent is Device device)) continue;
                string path = entry.Path.Substring(0, entry.Path.LastIndexOf("/items/", StringComparison.Ordinal));
                var service = seen.Add(path) ? device.GetService<UmacDevice>() : null;
                if (service != null) devices.Add(new KeyValuePair<string, UmacDevice>(path, service));
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "UMAC device lookup is incomplete.");
            return devices;
        }

        private static JObject UmacState(Project project, UmacConfigurator umac)
        {
            var budget = new ScanBudget(50000); var devices = UmacDevices(project);
            JArray Rights(IEnumerable<EngineeringFunctionRight> rights) => new JArray(rights.TakeWhile(_ => budget.Take()).Select(r => r.Identifier));
            JArray DeviceRights(Func<UmacDevice, IEnumerable<DeviceFunctionRight>> read) => new JArray(devices.SelectMany(d => read(d.Value).TakeWhile(_ => budget.Take())
                .Select(r => new JObject { ["device"] = d.Key, ["identifier"] = r.Identifier })));
            JObject Principal(string kind, string name, bool active, RoleAssociation roles) => new JObject { ["kind"] = kind, ["name"] = name, ["active"] = active,
                ["roles"] = new JArray(roles.TakeWhile(_ => budget.Take()).Select(r => r.Identifier)) };
            var roleRows = new JArray(); var principals = new JArray();
            foreach (CustomRole role in umac.CustomRoles.TakeWhile(_ => budget.Take()))
                roleRows.Add(new JObject { ["identifier"] = role.Identifier, ["name"] = role.Name, ["system"] = false, ["engineeringRights"] = Rights(role.AssignedEngineeringRights), ["deviceRights"] = DeviceRights(role.GetAssignedDeviceFunctionRights) });
            foreach (SystemRole role in umac.SystemRoles.TakeWhile(_ => budget.Take()))
                roleRows.Add(new JObject { ["identifier"] = role.Identifier, ["name"] = role.Name, ["system"] = true, ["engineeringRights"] = Rights(role.AssignedEngineeringRights), ["deviceRights"] = DeviceRights(role.GetAssignedSystemDeviceFunctionRights) });
            var anonymous = umac.AnonymousUser;
            foreach (ProjectUser user in umac.ProjectUsers.TakeWhile(_ => budget.Take())) principals.Add(Principal(user.Equals(anonymous) ? "anonymous" : "projectUser", user.Name, user.IsActive, user.Roles));
            if (anonymous != null && !umac.ProjectUsers.Contains(anonymous)) principals.Add(Principal("anonymous", anonymous.Name, anonymous.IsActive, anonymous.Roles));
            foreach (UmcUser user in umac.UmcUsers.TakeWhile(_ => budget.Take())) principals.Add(Principal("umcUser", user.Name, user.IsActive, user.Roles));
            foreach (UmcUserGroup group in umac.UmcUserGroups.TakeWhile(_ => budget.Take())) principals.Add(Principal("umcGroup", group.Name, group.IsActive, group.Roles));
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "UMAC role state exceeds 50000 entries.");
            return UmacRoleRules.Normalize(new JObject { ["roles"] = roleRows, ["principals"] = principals });
        }

        private static JObject UmacFingerprint(Project project)
        {
            var umac = project.GetService<UmacConfigurator>();
            return umac == null ? null : UmacState(project, umac);
        }

        private static JObject[] UmacRequestedDeviceRights(JObject payload)
        {
            if (payload["deviceRights"] == null) return new JObject[0];
            var rows = payload["deviceRights"] as JArray;
            if (rows == null || rows.Count > 64 || rows.Any(r => !(r is JObject row) || row.Count != 2 || row["device"]?.Type != JTokenType.String || row["identifier"]?.Type != JTokenType.String))
                throw new BridgeException("InvalidRequest", "deviceRights must list at most 64 { device, identifier } entries; device is a deviceRightScopes path from access_inspect.");
            return rows.Cast<JObject>().Select(r => new JObject { ["device"] = ChangeText(r, "device", 4096), ["identifier"] = ChangeText(r, "identifier", 256) }).ToArray();
        }

        // Grants resolve against the available rights, revokes against the role's assigned rights.
        private static void UmacResolveRights(Project project, UmacConfigurator umac, CustomRole role, string[] engineering, JObject[] device, bool grant,
            out EngineeringFunctionRight[] engineeringRights, out KeyValuePair<UmacDevice, DeviceFunctionRight>[] deviceRights)
        {
            var source = grant ? umac.EngineeringFunctionRights.ToArray() : role.AssignedEngineeringRights.ToArray();
            engineeringRights = engineering.Select(id => source.FirstOrDefault(r => r.Identifier == id) ?? throw new BridgeException("ObjectNotFound",
                "Engineering function right " + id + (grant ? " is not available." : " is not assigned to this role."))).ToArray();
            var devices = UmacDevices(project);
            deviceRights = device.Select(row =>
            {
                string path = row.Value<string>("device"), id = row.Value<string>("identifier");
                var umacDevice = devices.FirstOrDefault(d => d.Key == path).Value ?? throw new BridgeException("ObjectNotFound", "No UMAC device has the path " + path + ".");
                var right = (grant ? umacDevice.AvailableDeviceFunctionRights.ToArray() : role.GetAssignedDeviceFunctionRights(umacDevice).ToArray()).FirstOrDefault(r => r.Identifier == id)
                    ?? throw new BridgeException("ObjectNotFound", "Device function right " + id + (grant ? " is not available on " : " is not assigned to this role on ") + path + ".");
                return new KeyValuePair<UmacDevice, DeviceFunctionRight>(umacDevice, right);
            }).ToArray();
        }

        private void PrepareUmacRoleSet(EngineeringChange change, Project project, JObject preview)
        {
            RequireWebAccessFields(change);
            var payload = change.Payload;
            string action = ChangeText(payload, "action", 16), roleName = ChangeText(payload, "roleName", 128);
            var fields = action == "create" ? new[] { "action", "roleName" } : action == "assign" || action == "unassign" ? new[] { "action", "roleName", "userName" }
                : action == "grant" || action == "revoke" ? new[] { "action", "roleName", "engineeringRights", "deviceRights" }
                : throw new BridgeException("InvalidRequest", "action must be create, grant, revoke, assign or unassign.");
            if (payload.Properties().Any(p => !fields.Contains(p.Name))) throw new BridgeException("InvalidRequest", "action=" + action + " takes only " + string.Join(", ", fields) + ".");
            var umac = project.GetService<UmacConfigurator>() ?? throw new BridgeException("UnsupportedCapability", "The project exposes no UmacConfigurator.");
            if (umac.SystemRoles.Any(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("ProtectedObject", "System roles are never changed, assigned or unassigned.");
            string userName = action == "assign" || action == "unassign" ? ChangeText(payload, "userName", 128) : null;
            var engineering = payload["engineeringRights"] == null ? new string[0] : ChangeStringArray(payload, "engineeringRights", 64, 256);
            var device = UmacRequestedDeviceRights(payload);
            if ((action == "grant" || action == "revoke") && engineering.Length + device.Length == 0) throw new BridgeException("InvalidRequest", "Name at least one engineeringRights or deviceRights entry.");
            var before = UmacState(project, umac);
            var after = UmacRoleRules.Plan(before, action, roleName, userName, engineering, device);
            if (action == "grant" || action == "revoke")
                UmacResolveRights(project, umac, umac.CustomRoles.First(r => r.Name == roleName), engineering, device, action == "grant", out _, out _);
            UmacRoleRules.RequireAdministratorPreserved(before, after);
            change.ObjectNames = new[] { roleName };
            preview["umacBefore"] = before;
            preview["umacAfter"] = after;
            ((JArray)preview["effects"]).Add(new JObject { ["action"] = action, ["roleName"] = roleName, ["userName"] = userName, ["engineeringRights"] = new JArray(engineering), ["deviceRights"] = new JArray(device) });
            ((JArray)preview["warnings"]).Add("Project UMAC engineering only: no password, alias, activation, authentication, anonymous-user or UMC setting is read or changed. Every engineering function right an active project user holds stays held by one. Effective runtime rights, UMC group membership and CPU download are not evaluated.");
        }

        private void ApplyUmacRoleSet(EngineeringChange change, Project project)
        {
            var payload = change.Payload;
            string action = payload.Value<string>("action"), roleName = payload.Value<string>("roleName"), userName = payload.Value<string>("userName");
            var umac = project.GetService<UmacConfigurator>() ?? throw new BridgeException("UnsupportedCapability", "The project no longer exposes UmacConfigurator.");
            if (action == "create") umac.CustomRoles.Create(roleName, "");
            else
            {
                var role = umac.CustomRoles.FirstOrDefault(r => r.Name == roleName) ?? throw new BridgeException("ObjectNotFound", "The prepared custom role no longer exists.");
                if (action == "assign" || action == "unassign")
                {
                    var user = umac.ProjectUsers.FirstOrDefault(u => u.Name == userName && !u.Equals(umac.AnonymousUser)) ?? throw new BridgeException("ObjectNotFound", "The prepared project user no longer exists.");
                    if (action == "assign") user.Roles.Add(role); else user.Roles.Remove(role);
                }
                else
                {
                    bool grant = action == "grant";
                    UmacResolveRights(project, umac, role, ChangeStringArray(payload, "engineeringRights", 64, 256, true), UmacRequestedDeviceRights(payload), grant, out var engineering, out var device);
                    foreach (var right in engineering) if (grant) role.AssignedEngineeringRights.Add(right); else role.AssignedEngineeringRights.Remove(right);
                    foreach (var right in device) if (grant) role.AssignDeviceFunctionRight(right.Key, right.Value); else role.UnAssignDeviceFunctionRight(right.Key, right.Value);
                }
            }
            var actual = UmacState(project, umac); var expected = (JObject)change.Preview["umacAfter"].DeepClone();
            // A created role's native identifier exists only after creation.
            if (action == "create") expected["roles"].First(r => !r.Value<bool>("system") && (string)r["name"] == roleName)["identifier"] =
                actual["roles"].FirstOrDefault(r => !r.Value<bool>("system") && (string)r["name"] == roleName)?["identifier"]?.DeepClone();
            change.Readbacks.Add(new JObject { ["role"] = "umac-readback", ["state"] = actual });
            if (!JToken.DeepEquals(actual, expected)) throw new BridgeException("MutationVerificationFailed", "UMAC read-back differs from the previewed umacAfter roles, rights and assignments.");
            UmacRoleRules.RequireAdministratorPreserved((JObject)change.Preview["umacBefore"], actual);
        }
    }
}
