#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Security;
using Siemens.Engineering.Umac;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private const string SecretPolicy = "Never reads or returns passwords, password hashes, secrets, tokens or private keys. Attributes whose names match password|credential|privatekey|secret|token are excluded by name; certificates are returned only as their re-encoded public DER.";
        private static readonly (string Id, string Scope)[] NoRights = new (string, string)[0];

        private JObject AccessInspect(JObject args)
        {
            var project = SelectProject(args);
            var lookup = new ScanBudget(50000); var budget = new ScanBudget(Limit(args, 500)); var warnings = new JArray();
            var selected = SecurityHardware(project, args, lookup, warnings, out var all);
            string root = "/projects/" + Segment(project.Name);
            // UMAC device function rights belong to the Device that owns each top-level item.
            var umacDevices = new List<KeyValuePair<string, UmacDevice>>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in all)
            {
                if (!(entry.Item.Parent is Device device)) continue;
                string devicePath = entry.Path.Substring(0, entry.Path.LastIndexOf("/items/", StringComparison.Ordinal));
                if (!seen.Add(devicePath)) continue;
                try { var service = device.GetService<UmacDevice>(); if (service != null) umacDevices.Add(new KeyValuePair<string, UmacDevice>(devicePath, service)); }
                catch (Exception error) { warnings.Add(SecurityWarning(devicePath, error)); }
            }
            JObject projectUmac = null;
            var umac = project.GetService<UmacConfigurator>();
            var snapshot = new AccessSnapshot(budget, warnings, root); var roleNames = new JObject();
            if (umac != null) try
            {
                IEnumerable<string> Roles(RoleAssociation roles) => roles.Select(role => "role:" + role.Identifier);
                (string, string)[] RoleRights(IEnumerable<EngineeringFunctionRight> engineering, Func<UmacDevice, IEnumerable<DeviceFunctionRight>> device)
                    => engineering.Select(right => (right.Identifier, "project")).Concat(umacDevices.SelectMany(d => device(d.Value).Select(right => (right.Identifier, d.Key)))).ToArray();
                bool AddUser(string prefix, User user, Func<bool> active, bool assignmentsComplete)
                    => snapshot.Add(prefix + user.Name, id => AccessEntity(id, "user", active(), NoRights, true, Roles(user.Roles), assignmentsComplete));
                foreach (ProjectUser user in umac.ProjectUsers) if (!AddUser("user:", user, () => user.IsActive, true)) break;
                var anonymous = umac.AnonymousUser;
                if (anonymous != null && !umac.ProjectUsers.Contains(anonymous)) AddUser("user:", anonymous, () => anonymous.IsActive, true);
                // UMC group membership is resolved by the UMC server and is not exposed offline.
                foreach (UmcUser user in umac.UmcUsers) if (!AddUser("umcUser:", user, () => user.IsActive, false)) break;
                foreach (UmcUserGroup group in umac.UmcUserGroups)
                    if (!snapshot.Add("umcGroup:" + group.Name, id => AccessEntity(id, "group", group.IsActive, NoRights, true, Roles(group.Roles), true))) break;
                // Roles expose no activation state, so enabled stays unknown (null).
                foreach (CustomRole role in umac.CustomRoles)
                {
                    roleNames["role:" + role.Identifier] = role.Name;
                    if (!snapshot.Add("role:" + role.Identifier, id => AccessEntity(id, "role", null, RoleRights(role.AssignedEngineeringRights, role.GetAssignedDeviceFunctionRights), !lookup.Truncated, new string[0], true))) break;
                }
                foreach (SystemRole role in umac.SystemRoles)
                {
                    roleNames["role:" + role.Identifier] = role.Name;
                    if (!snapshot.Add("role:" + role.Identifier, id => AccessEntity(id, "role", null, RoleRights(role.AssignedEngineeringRights, role.GetAssignedSystemDeviceFunctionRights), !lookup.Truncated, new string[0], true))) break;
                }
                projectUmac = new JObject { ["anonymousUserActive"] = anonymous == null ? null : SecurityValue(() => anonymous.IsActive) };
            }
            catch (Exception error) { snapshot.Incomplete(error); projectUmac = new JObject(); }
            if (projectUmac != null)
            {
                // Keep what was captured before a native failure; the snapshot is then marked incomplete.
                projectUmac.Merge(snapshot.Observation("project", AccessIdentity("projectUmac", root, "V21 Openness exposes no UMAC configuration version or security mode.")));
                projectUmac["roleNames"] = roleNames;
                projectUmac["deviceRightScopes"] = new JArray(umacDevices.Select(d => d.Key));
            }
            var devices = new JArray();
            foreach (var entry in selected)
            {
                try
                {
                    var item = entry.Item;
                    var level = item.GetService<PlcAccessLevelProvider>(); var control = item.GetService<PlcAccessControlConfigurationProvider>();
                    var secret = item.GetService<PlcMasterSecretConfigurator>(); var full = item.GetService<WebserverUserManagement>(); var simple = item.GetService<SimpleWebserverUserManagement>();
                    if (level == null && control == null && secret == null && full == null && simple == null) continue;
                    if (!budget.Take()) break;
                    var row = new JObject { ["objectPath"] = entry.Path, ["name"] = item.Name,
                        ["accessLevel"] = level == null ? null : SecurityValue(() => level.PlcProtectionAccessLevel),
                        ["accessControl"] = control == null ? null : new JObject { ["configuration"] = SecurityValue(() => control.PlcAccessControlConfiguration),
                            ["umcConnectionState"] = SecurityValue(() => control.UmcConnectionState), ["umcServerAddress"] = SecurityValue(() => control.UmcServerAddress),
                            ["umcServerId"] = SecurityValue(() => control.UmcServerId), ["umcTrustedCertificateIds"] = SecurityValue(() => control.UmcTrustedCertificateIds) },
                        ["confidentialConfiguration"] = secret == null ? null : SecurityValue(() => secret.MasterSecretConfiguration), ["legacyWeb"] = null };
                    devices.Add(row);
                    if (full == null && simple == null) continue;
                    var web = new AccessSnapshot(budget, warnings, entry.Path);
                    if (full != null)
                        foreach (WebserverUser user in full.WebserverUsers)
                            if (!web.Add("webUser:" + user.UserName, id => AccessEntity(id, "user", null, WebRights(user.Permissions, entry.Path), KnownWebRights(user.Permissions), new string[0], true))) break;
                    if (simple != null)
                        foreach (SimpleWebserverUser user in simple.WebserverUsers)
                            if (!web.Add("webUser:" + user.UserName, id => AccessEntity(id, "user", user.Active,
                                user.Permissions == SimpleWebserverUserPermissions.None ? NoRights : new[] { (user.Permissions.ToString(), entry.Path) }, true, new string[0], true))) break;
                    row["legacyWeb"] = web.Observation("hardware", AccessIdentity("legacyWeb", entry.Path, "V21 Openness exposes no legacy web user-management version or security mode."));
                }
                catch (Exception error) { warnings.Add(SecurityWarning(entry.Path, error)); }
            }
            var result = SecurityEnvelope(project);
            result["projectUmac"] = projectUmac;
            result["devices"] = devices;
            result["warnings"] = warnings;
            result["reviewUse"] = "access_review: copy an observation's identity to request.identity and its snapshot to before or after; resolve owner.objectPath to its nodeId in the saved review.";
            result["completeness"] = InspectionCoverage(budget, warnings, "Project UMAC users, UMC users/groups, custom/system roles with assigned engineering and device function rights, CPU access level, access-control/UMC settings, confidential-configuration state and legacy web users.",
                "Roles expose no activation state (enabled=null)", "UMC group membership is resolved by the UMC server and not exposed (UMC users report assignmentsComplete=false)",
                "CPU runtime rights appear as device-scoped projectUmac rights; no separate cpuRuntime observation", "Password policy, effective rights, UMC synchronization and online state are not read");
            return result;
        }

        private JObject CertificateInspect(JObject args)
        {
            var project = SelectProject(args);
            var lookup = new ScanBudget(50000); var budget = new ScanBudget(Limit(args, 500)); var warnings = new JArray();
            var selected = SecurityHardware(project, args, lookup, warnings, out var all);
            string exports = Path.Combine(ArtifactDirectory(args), "certificate-inspect-" + Guid.NewGuid().ToString("N"));
            var devices = new JArray();
            try
            {
                foreach (var entry in selected)
                {
                    try
                    {
                        var manager = entry.Item.GetService<LocalCertificateManager>(); var management = entry.Item.GetService<CertificateManagementConfiguration>();
                        if (manager == null && management == null) continue;
                        if (!budget.Take()) break;
                        var certificates = new JArray(); var services = new JArray();
                        var row = new JObject { ["objectPath"] = entry.Path, ["name"] = entry.Item.Name,
                            ["globalCertificateStoreEnabled"] = manager == null ? null : SecurityValue(() => manager.EnableGlobalCertificatesStore), ["certificates"] = certificates,
                            ["certificateManagement"] = management == null ? null : new JObject { ["usage"] = SecurityValue(() => management.Usage),
                                ["expirationEventActivated"] = SecurityValue(() => management.CertificateExpirationEventActivated),
                                ["remainingCertificateLifetime"] = SecurityValue(() => management.RemainingCertificateLifetime), ["supportedServices"] = services } };
                        devices.Add(row);
                        if (manager != null)
                            foreach (Certificate certificate in manager.LocalCertificateStore.Certificates)
                            { if (!budget.Take()) break; certificates.Add(PublicCertificate(certificate, entry.Path, exports)); }
                        if (management != null)
                            foreach (CertificateSupportedService service in management.CertificateSupportedServices)
                            {
                                if (!budget.Take()) break;
                                services.Add(new JObject { ["id"] = service.Id, ["guid"] = service.Guid.ToString(), ["serviceType"] = service.ServiceType.ToString(),
                                    ["serviceGroupName"] = service.ServiceGroupName, ["applicationUri"] = service.ApplicationUri });
                            }
                        row["attributes"] = InspectionAttributes(entry.Item, budget, warnings, entry.Path, include: name => Regex.IsMatch(name, "certificate|syslog", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                    }
                    catch (Exception error) { warnings.Add(SecurityWarning(entry.Path, error)); }
                }
            }
            finally { try { if (Directory.Exists(exports)) Directory.Delete(exports, true); } catch (Exception) { } }
            var syslog = project.GetService<SyslogServerProvider>(); var servers = new JArray();
            if (syslog != null)
                foreach (SyslogServer server in syslog.Servers)
                {
                    if (!budget.Take()) break;
                    try
                    {
                        servers.Add(new JObject { ["name"] = server.Name, ["address"] = server.Address, ["port"] = server.Port, ["tls"] = server.Tls, ["comment"] = server.Comment,
                            ["assignedModules"] = new JArray(server.AssignedModules.TakeWhile(_ => budget.Take()).Select(module => all.FirstOrDefault(h => h.Item.Equals(module))?.Path ?? module.Name)) });
                    }
                    catch (Exception error) { warnings.Add(SecurityWarning("/projects/" + Segment(project.Name) + "/syslog", error)); }
                }
            var result = SecurityEnvelope(project);
            result["devices"] = devices;
            result["syslogServers"] = syslog == null ? null : servers;
            result["warnings"] = warnings;
            result["reviewUse"] = "public_certificate_review: use a certificate's observation as before or after (association.usage is unspecified because V21 exposes no per-certificate usage); resolve objectPath to its nodeId in the saved review and supply evaluatedAtUtc.";
            result["completeness"] = InspectionCoverage(budget, warnings, "CPU local certificate stores (public DER, subject, issuer, validity, fingerprints), dynamic certificate-management services, certificate/syslog-named CPU attributes and project syslog servers.",
                "No per-certificate usage or web-server/OPC UA assignment API; assignment evidence is limited to certificate-named attributes and supported services",
                "Global (project) certificate manager is not exposed; only its enable flag", "Private keys are never exported; HasPrivateKey is reported only as a flag", "No chain, revocation or trust evaluation");
            return result;
        }

        private JObject WebInspect(JObject args)
        {
            var project = SelectProject(args);
            var lookup = new ScanBudget(50000); var budget = new ScanBudget(Limit(args, 500)); var warnings = new JArray();
            var devices = new JArray();
            foreach (var entry in SecurityHardware(project, args, lookup, warnings, out _))
            {
                try
                {
                    var system = entry.Item.GetService<SystemWebPagesFeature>(); var pages = entry.Item.GetService<DefaultWebPagesFeature>(); var userPages = entry.Item.GetService<WebserverUserDefinedPages>();
                    if (system == null && pages == null && userPages == null) continue;
                    if (!budget.Take()) break;
                    var attributes = InspectionAttributes(entry.Item, budget, warnings, entry.Path, include: name => name.IndexOf("web", StringComparison.OrdinalIgnoreCase) >= 0);
                    var applications = new JArray(); bool inventoryComplete = pages != null; var ids = new HashSet<string>(StringComparer.Ordinal);
                    if (pages != null)
                        foreach (WebApplicationConfiguration application in pages.WebApplicationConfigurations)
                        {
                            // web_application_review accepts at most 512 applications per phase.
                            if (applications.Count == 512 || !budget.Take()) { inventoryComplete = false; break; }
                            string name = application.Name;
                            if (string.IsNullOrWhiteSpace(name) || !ids.Add(name)) { inventoryComplete = false; continue; }
                            applications.Add(new JObject { ["applicationId"] = name, ["name"] = name, ["applicationType"] = application.ApplicationType.ToString(),
                                ["isDefault"] = application.IsDefault, ["properties"] = new JArray() });
                        }
                    // Only an exactly named, completely captured Boolean is used; otherwise the review keeps web serving unknown.
                    var activate = attributes.OfType<JObject>().FirstOrDefault(a => (string)a["name"] == "WebserverActivate" && a.Value<bool?>("valueComplete") == true && a["value"]?.Type == JTokenType.Boolean);
                    var generation = attributes.OfType<JObject>().Where(a => a != activate && a.Value<bool?>("valueComplete") == true && a["value"] is JValue value &&
                        !(value.Type == JTokenType.String && ((string)value).Any(char.IsControl))).Select(a => new JObject { ["name"] = a["name"], ["value"] = a["value"] }).Take(129).ToArray();
                    var configuration = new JObject { ["webEnabled"] = activate?["value"]?.DeepClone(), ["systemPagesEnabled"] = system == null ? null : SecurityFlag(() => system.EnableSystemWebPages, entry.Path, warnings),
                        ["applicationInventoryComplete"] = inventoryComplete, ["applications"] = applications, ["generationProperties"] = new JArray(generation.Take(128)) };
                    var archive = new JObject { ["uploadRef"] = null, ["sha256"] = null, ["rootRelative"] = null, ["defaultPage"] = null, ["purpose"] = "userDefinedPageSource",
                        ["unavailableReason"] = "web_inspect does not read page sources or application archives; upload the original ZIP and fill this selector to compare files." };
                    devices.Add(new JObject { ["objectPath"] = entry.Path, ["name"] = entry.Item.Name, ["owner"] = new JObject { ["objectPath"] = entry.Path, ["kind"] = "hardware" },
                        ["observation"] = new JObject { ["configuration"] = configuration, ["archive"] = archive }, ["webEnabledAttribute"] = activate == null ? null : "WebserverActivate",
                        ["generationPropertiesTruncated"] = generation.Length > 128, ["userDefinedPagesServiceExposed"] = userPages != null, ["attributes"] = attributes });
                }
                catch (Exception error) { warnings.Add(SecurityWarning(entry.Path, error)); }
            }
            var result = SecurityEnvelope(project);
            result["devices"] = devices;
            result["warnings"] = warnings;
            result["reviewUse"] = "web_application_review: use a device's observation as before or after, pick selectedApplicationId from its applications and resolve owner.objectPath to its nodeId in the saved review. generationProperties carry every completely captured scalar web-named CPU attribute.";
            result["completeness"] = InspectionCoverage(budget, warnings, "CPU system web pages flag, default/user web application configurations and web-named CPU attributes.",
                "webEnabled is read only from an exposed Boolean WebserverActivate attribute; otherwise null", "WebserverUserDefinedPages exposes only block generation, which is never called",
                "No page sources, archives, generated blocks or deployed content", "Legacy web users are reported by access_inspect");
            return result;
        }

        private HardwareEntry[] SecurityHardware(Project project, JObject args, ScanBudget lookup, JArray warnings, out HardwareEntry[] all)
        {
            string wanted = args.Value<string>("hardwarePath");
            all = DeviceItems(project, lookup).ToArray();
            if (lookup.Truncated) warnings.Add("Hardware lookup reached its 50000-item traversal bound.");
            var selected = all.Where(entry => string.IsNullOrWhiteSpace(wanted) || string.Equals(entry.Path, wanted, StringComparison.Ordinal)).ToArray();
            if (selected.Length == 0 && !string.IsNullOrWhiteSpace(wanted) && !lookup.Truncated) throw new BridgeException("HardwareNotFound", "No hardware item matches hardwarePath.");
            return selected;
        }

        private JObject SecurityEnvelope(Project project)
        {
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["secretPolicy"] = SecretPolicy;
            result["sensitiveAttributesExcluded"] = true;
            return result;
        }

        private static JObject PublicCertificate(Certificate certificate, string storePath, string directory)
        {
            string id = certificate.Id.ToString(CultureInfo.InvariantCulture);
            var observation = new JObject { ["association"] = new JObject { ["storeId"] = storePath + "/certificates", ["certificateId"] = id, ["usage"] = "unspecified" },
                ["format"] = null, ["contentBase64"] = null, ["expectedSha256"] = null, ["unavailableReason"] = null };
            var row = new JObject { ["certificateId"] = id, ["subjectCommonName"] = SecurityValue(() => certificate.SubjectCommonName),
                ["validUntil"] = SecurityValue(() => certificate.ValidUntil), ["hasPrivateKey"] = SecurityValue(() => certificate.HasPrivateKey), ["privateKeyReturned"] = false };
            try
            {
                Directory.CreateDirectory(directory);
                var file = new FileInfo(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cer"));
                try
                {
                    certificate.Export(file);
                    file.Refresh();
                    // public_certificate_review accepts at most 64 KiB DER; anything but one public certificate is discarded unread.
                    if (!file.Exists || file.Length > 64 * 1024) throw new InvalidDataException();
                    byte[] bytes = File.ReadAllBytes(file.FullName);
                    if (X509Certificate2.GetCertContentType(bytes) != X509ContentType.Cert) throw new InvalidDataException();
                    using (var parsed = new X509Certificate2(bytes))
                    using (var sha = SHA256.Create())
                    {
                        if (parsed.HasPrivateKey) throw new InvalidDataException();
                        byte[] der = parsed.RawData;
                        row["subject"] = parsed.Subject; row["issuer"] = parsed.Issuer; row["serialNumber"] = parsed.SerialNumber; row["thumbprintSha1"] = parsed.Thumbprint;
                        row["sha256"] = BitConverter.ToString(sha.ComputeHash(der)).Replace("-", "").ToLowerInvariant();
                        row["notBeforeUtc"] = parsed.NotBefore.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                        row["notAfterUtc"] = parsed.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                        observation["format"] = "der"; observation["contentBase64"] = Convert.ToBase64String(der);
                    }
                }
                finally { file.Refresh(); if (file.Exists) file.Delete(); }
            }
            catch (Exception) { observation["unavailableReason"] = "Native export was unavailable or did not yield exactly one public certificate."; }
            row["observation"] = observation;
            return row;
        }

        private static JObject AccessEntity(string id, string kind, bool? enabled, IEnumerable<(string Id, string Scope)> rights, bool rightsComplete, IEnumerable<string> inherits, bool assignmentsComplete)
        {
            var observed = rights.Distinct().ToArray();
            var granted = observed.Where(r => !string.IsNullOrWhiteSpace(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Scope, StringComparer.Ordinal).ToArray();
            var links = inherits.Distinct(StringComparer.Ordinal).Take(65).ToArray();
            // access_review accepts at most 64 rights and 64 inheritance links per entity.
            return new JObject { ["id"] = id, ["kind"] = kind, ["enabled"] = enabled,
                ["rightsComplete"] = rightsComplete && granted.Length == observed.Length && granted.Length <= 64, ["assignmentsComplete"] = assignmentsComplete && links.Length <= 64,
                ["rights"] = new JArray(granted.Take(64).Select(r => new JObject { ["id"] = r.Id, ["scope"] = r.Scope })), ["inherits"] = new JArray(links.Take(64)) };
        }

        private static JObject AccessIdentity(string domain, string source, string reason)
            => new JObject { ["domain"] = domain, ["sourceId"] = source, ["version"] = null, ["securityMode"] = null, ["unknownReason"] = reason };

        private static IEnumerable<(string, string)> WebRights(WebserverUserPermissions permissions, string scope)
            => Enum.GetValues(typeof(WebserverUserPermissions)).Cast<WebserverUserPermissions>()
                .Where(flag => flag != WebserverUserPermissions.None && (permissions & flag) == flag).Select(flag => (flag.ToString(), scope)).ToArray();

        // Unknown future permission bits make the decoded rights incomplete.
        private static bool KnownWebRights(WebserverUserPermissions permissions)
            => (Convert.ToInt64(permissions) & ~Enum.GetValues(typeof(WebserverUserPermissions)).Cast<object>().Aggregate(0L, (known, flag) => known | Convert.ToInt64(flag))) == 0;

        private static JToken SecurityValue(Func<object> read)
        {
            // Native exception text can contain values; report only that the read failed.
            try { return InspectionValue(read()); } catch (Exception) { return new JObject { ["valueStatus"] = "readError" }; }
        }

        private static bool? SecurityFlag(Func<bool> read, string path, JArray warnings)
        {
            try { return read(); }
            catch (Exception error) { warnings.Add(SecurityWarning(path, error)); return null; }
        }

        private static JObject SecurityWarning(string path, Exception error)
            => new JObject { ["path"] = path, ["exceptionType"] = error.GetType().Name, ["message"] = "Native security metadata could not be captured." };

        private sealed class AccessSnapshot
        {
            private readonly JArray _entities = new JArray(); private readonly HashSet<string> _ids = new HashSet<string>(StringComparer.Ordinal);
            private readonly ScanBudget _budget; private readonly JArray _warnings; private readonly string _path; private bool _complete = true;
            internal AccessSnapshot(ScanBudget budget, JArray warnings, string path) { _budget = budget; _warnings = warnings; _path = path; }

            // Returns false once a bound is reached; access_review accepts at most 512 entities per phase.
            internal bool Add(string id, Func<string, JObject> build)
            {
                if (_entities.Count == 512 || !_budget.Take()) { _complete = false; return false; }
                if (!_ids.Add(id)) { _complete = false; return true; }
                try { _entities.Add(build(id)); }
                catch (Exception error) { Incomplete(error); }
                return true;
            }

            internal void Incomplete(Exception error) { _complete = false; _warnings.Add(SecurityWarning(_path, error)); }

            internal JObject Observation(string ownerKind, JObject identity) => new JObject { ["owner"] = new JObject { ["objectPath"] = _path, ["kind"] = ownerKind },
                ["identity"] = identity, ["snapshot"] = new JObject { ["identity"] = identity.DeepClone(), ["entitiesComplete"] = _complete, ["entities"] = _entities } };
        }
    }
}
