using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Formatting = Newtonsoft.Json.Formatting;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private string _hmiSessionGeneration = Guid.NewGuid().ToString("N");
        private void InvalidateHmiSession() { _hmiSessionGeneration = Guid.NewGuid().ToString("N"); }

        private sealed class HmiDiscoveredTarget
        {
            internal Software Software;
            internal DeviceItem Hardware;
            internal string Family, Path, Name, DevicePath, Profile, CatalogIdentifier, RuntimeVersion;
            internal JObject Diagnostic, CatalogEvidence;
        }
        private sealed class HmiNativeEntry
        {
            internal IEngineeringObject Native;
            internal JObject Identity, Row;
        }
        private sealed class HmiCapture
        {
            internal readonly List<HmiNativeEntry> Entries = new List<HmiNativeEntry>();
            internal readonly JArray Diagnostics = new JArray();
            internal readonly JArray Artifacts = new JArray();
            internal JArray ClassicScreenScopes;
            internal readonly Dictionary<string, JObject> CatalogDefinitions = new Dictionary<string, JObject>(StringComparer.Ordinal);
            internal string Status = "complete";
            // Scalar read failures are reported separately: they do not hide another declaration
            // when the owning candidate compositions themselves were completely enumerated.
            internal bool CandidateScopesComplete = true;
            internal readonly ScanBudget Budget;
            internal int RetainedBytes;
            internal int RelationBytes;
            internal HmiCapture(int maximum) { Budget = new ScanBudget(maximum); }
            internal HmiCapture(ScanBudget sharedBudget) { Budget = sharedBudget; }
        }

        private static int HmiBound(JObject args, string name, int fallback, int minimum, int maximum)
        {
            var value = args[name];
            if (value == null) return fallback;
            if (value.Type != JTokenType.Integer || value.Value<long>() < minimum || value.Value<long>() > maximum)
                throw new BridgeException("InvalidRequest", name + " must be an integer from " + minimum + " through " + maximum + ".");
            return value.Value<int>();
        }

        private JObject HmiInspect(JObject args)
        {
            if (args.Value<int?>("schemaVersion") is int version && version != 1) throw new BridgeException("UnsupportedSchemaVersion", "HMI inspection request schemaVersion must be 1.");
            args = (JObject)args.DeepClone();
            var requestedIdentity = args["target"] as JObject ?? args["identity"] as JObject;
            if (args["screenPath"] == null && requestedIdentity?["screenPath"] != null) args["screenPath"] = requestedIdentity["screenPath"].DeepClone();
            var project = SelectProject(args);
            string section = args.Value<string>("section") ?? "devices";
            if (!new[] { "devices", "connections", "tags", "screens", "items", "alarms", "scripts", "resources" }.Contains(section))
                throw new BridgeException("InvalidRequest", "Unknown HMI inspection section.");
            int limit = HmiBound(args, "limit", 100, 1, 200), offset = HmiBound(args, "offset", 0, 0, 50000);
            int maxBytes = HmiBound(args, "maxBytes", 262144, 4096, 1048576);
            var capture = new HmiCapture(HmiBound(args, "maxVisited", 5000, 1, 50000));
            HmiDiscoveredTarget target = null;
            HmiValidateIdentity(project, args["target"] as JObject ?? args["identity"] as JObject ?? args);
            if (section == "devices")
            {
                foreach (var device in HmiDiscover(project, capture))
                {
                    var row = HmiDeviceView(project, device);
                    capture.RetainedBytes += Encoding.UTF8.GetByteCount(row.ToString(Formatting.None));
                    if (capture.RetainedBytes > 8 * 1024 * 1024) { capture.Budget.Truncated = true; break; }
                    capture.Entries.Add(new HmiNativeEntry { Native = device.Software ?? (IEngineeringObject)device.Hardware, Identity = (JObject)row["identity"], Row = row });
                }
            }
            else
            {
                target = HmiResolveNativeTarget(project, args["target"] as JObject ?? args["identity"] as JObject ?? args, capture);
                try
                {
                    RequireHmiModule(target.Family);
                    if (target.Family == "classic") HmiCaptureClassic(project, target, section, args, capture, includeScreenOwner: args.Value<bool?>("includeScreenOwner") == true);
                    else HmiCaptureUnified(project, target, section, args, capture, includeScreenOwner: args.Value<bool?>("includeScreenOwner") == true);
                    if (args.Value<bool?>("includeSource") == true) HmiExportInspectionSource(target, section, args, capture);
                }
                catch (Exception error) { capture.Status = "failed"; capture.CandidateScopesComplete = false; capture.Diagnostics.Add(HmiDiagnostic(section, error)); }
            }
            if (capture.Budget.Truncated) capture.Status = "truncated";
            var ordered = capture.Entries.OrderBy(e => e.Identity.Value<string>("objectPath"), StringComparer.Ordinal).ToList();
            string exact = args.Value<string>("objectPath") ?? (args["target"] as JObject ?? args["identity"] as JObject)?.Value<string>("objectPath");
            if (target != null && exact == target.Path) exact = null;
            if (exact != null && section != "items" && section != "devices") ordered = ordered.Where(e => e.Identity.Value<string>("objectPath") == exact).ToList();
            string revision = ChangeHash(new JArray(ordered.Select(e => e.Row)).ToString(Formatting.None));
            if (args.Value<string>("expectedMetadataRevision") is string expected && revision != expected)
                throw new BridgeException("HmiMetadataChanged", "Observed metadata changed between pages; capture again from offset zero.");
            var rows = new JArray(); int bytes = 0;
            foreach (var entry in ordered.Skip(offset).Take(limit))
            {
                int count = Encoding.UTF8.GetByteCount(entry.Row.ToString(Formatting.None));
                if (bytes + count > maxBytes) break;
                rows.Add(entry.Row); bytes += count;
            }
            bool more = offset + rows.Count < ordered.Count;
            if (more && rows.Count == 0) { capture.Status = "truncated"; capture.Diagnostics.Add(new JObject { ["code"] = "HmiRowTooLarge", ["reason"] = "The next row exceeds maxBytes; request a larger byte limit." }); }
            var result = Envelope(); result["schemaVersion"] = 1; result["projectPath"] = project.Path?.FullName;
            result["projectCopyId"] = HmiProjectCopyId(project); result["sessionGeneration"] = _hmiSessionGeneration;
            result["section"] = section; result["target"] = target == null ? null : HmiDeviceView(project, target);
            result["items"] = rows; result["offset"] = offset; result["nextOffset"] = more && rows.Count > 0 ? (JToken)(offset + rows.Count) : null;
            result["metadataRevision"] = revision; result["revisionScope"] = "Only metadata observed in this bounded section; not a whole-project or HMI-content fingerprint.";
            result["observedAtUtc"] = DateTime.UtcNow.ToString("O"); result["payloadBytes"] = bytes;
            result["coverage"] = new JObject { ["status"] = capture.Status, ["complete"] = capture.Status == "complete", ["truncated"] = capture.Status == "truncated",
                ["pageComplete"] = !more, ["visited"] = capture.Budget.Used, ["reasons"] = capture.Diagnostics.DeepClone(),
                ["scope"] = "This section and its explicitly requested details only; native proxies are never cached." };
            if (capture.ClassicScreenScopes != null) result["coverage"]["screenScopes"] = capture.ClassicScreenScopes.DeepClone();
            if (section == "resources")
            {
                result["coverage"]["supportedConstructs"] = new JArray("Native text lists", "Native graphic lists");
                result["coverage"]["excludedConstructs"] = new JArray("Standalone images, fonts, global resource repositories and unknown resource types are not enumerated by this section.");
            }
            if (section == "screens" || section == "items") result["coverage"]["behaviorScope"] = target?.Family == "unified"
                ? "Exposed screen/item dynamizations, event handlers, property event handlers, script text/hash and trigger scalar properties. Script semantics and static navigation targets remain unresolved."
                : "Classic native scalar metadata and selected screen exports. Unknown event/script/navigation XML remains in the retained artifact without invented bindings.";
            result["diagnostics"] = capture.Diagnostics; result["artifacts"] = capture.Artifacts;
            result["scriptsExecuted"] = false; result["runtimeVerified"] = false;
            return result;
        }

        private string HmiProjectCopyId(Project project) => ChangeHash((project.Path?.FullName ?? "unsaved:" + project.Name).ToUpperInvariant());
        private void HmiValidateIdentity(Project project, JObject supplied)
        {
            if (supplied == null) return;
            if (supplied.Value<string>("projectPath") is string path && !string.Equals(path, project.Path?.FullName, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ProjectMismatch", "The HMI identity belongs to a different project copy.");
            if (supplied.Value<string>("projectCopyId") is string copy && copy != HmiProjectCopyId(project))
                throw new BridgeException("ProjectMismatch", "The HMI project copy identity does not match the selected open project.");
            if (supplied.Value<string>("sessionGeneration") is string session && session != _hmiSessionGeneration)
                throw new BridgeException("StaleHmiIdentity", "The HMI identity predates the current attachment/project generation. Inspect the target again.");
        }
        private JObject HmiIdentity(Project project, HmiDiscoveredTarget target, string kind, string path, string name)
            => new JObject { ["schemaVersion"] = 2, ["projectPath"] = project.Path?.FullName, ["projectCopyId"] = HmiProjectCopyId(project),
                ["sessionGeneration"] = _hmiSessionGeneration, ["family"] = target.Family, ["devicePath"] = target.DevicePath,
                ["hmiPath"] = target.Path, ["scopePath"] = target.Path, ["objectPath"] = path, ["kind"] = kind, ["name"] = name };

        private List<HmiDiscoveredTarget> HmiDiscover(Project project, HmiCapture capture)
        {
            var found = new List<HmiDiscoveredTarget>();
            foreach (var entry in DeviceItems(project, capture.Budget))
            {
                if (capture.Diagnostics.Count >= 100) { capture.Budget.Truncated = true; break; }
                Software software = null; Exception failure = null;
                try { software = entry.Item.GetService<SoftwareContainer>()?.Software; } catch (Exception error) { failure = error; }
                string type = software?.GetType().FullName;
                string family = type == "Siemens.Engineering.Hmi.HmiTarget" ? "classic" : type == "Siemens.Engineering.HmiUnified.HmiSoftware" ? "unified" : null;
                var ownerMetadata = HmiHardwareMetadata(entry.Item, entry.Path, family, capture);
                string identifier = ownerMetadata.Value<string>("catalogIdentifier"), classification = ownerMetadata.Value<string>("classification");
                bool possibleHmi = classification.IndexOf("hmi", StringComparison.OrdinalIgnoreCase) >= 0 || classification.IndexOf("wincc", StringComparison.OrdinalIgnoreCase) >= 0 || classification.IndexOf("comfort", StringComparison.OrdinalIgnoreCase) >= 0;
                if (family == null && !possibleHmi && failure == null) continue;
                if (software is PlcSoftware) continue;
                var target = new HmiDiscoveredTarget { Software = software, Hardware = entry.Item, Family = family ?? "unknown", DevicePath = entry.Path,
                    Name = software?.Name ?? entry.Item.Name, Path = software == null ? entry.Path : entry.Path + "/hmi/" + Segment(software.Name),
                    CatalogIdentifier = identifier, RuntimeVersion = ownerMetadata.Value<string>("runtimeVersion"), Profile = HmiInspectionRules.Profile(family, classification), CatalogEvidence = ownerMetadata["evidence"] as JObject };
                if (failure != null) target.Diagnostic = HmiDiagnostic("SoftwareContainer.Software", failure);
                else if (family == null) target.Diagnostic = new JObject { ["code"] = "HmiSoftwareUnavailable", ["reason"] = software == null ? "HMI-classified hardware has no accessible software. This may indicate missing engineering options, licensing, protection, or unsupported target exposure; absence is not established." : "This native software type has no Classic/Unified adapter.", ["nativeType"] = type };
                found.Add(target);
                if (target.Diagnostic != null) { capture.Diagnostics.Add(target.Diagnostic.DeepClone()); capture.Status = "unavailable"; }
            }
            return found.OrderBy(t => t.Path, StringComparer.Ordinal).ToList();
        }

        private HmiDiscoveredTarget HmiResolveNativeTarget(Project project, JObject args, HmiCapture capture = null)
        {
            HmiValidateIdentity(project, args);
            capture = capture ?? new HmiCapture(50000);
            string path = args?.Value<string>("hmiPath");
            if (string.IsNullOrEmpty(path)) throw new BridgeException("InvalidRequest", "Select an exact hmiPath from hmi_inspect section=devices.");
            var targets = HmiDiscover(project, capture);
            if (capture.Budget.Truncated) throw new BridgeException("TraversalLimit", "HMI lookup is incomplete; target uniqueness has not been established.");
            var target = targets.SingleOrDefault(t => t.Path == path) ?? throw new BridgeException("HmiTargetNotFound", "The exact HMI software path was not found in this project generation.");
            if (args.Value<string>("family") is string family && target.Family != family) throw new BridgeException("TargetIncompatible", "The exact target belongs to a different HMI family.");
            if (args.Value<string>("devicePath") is string device && target.DevicePath != device) throw new BridgeException("TargetIncompatible", "The supplied hardware owner does not match the exact HMI software path.");
            if (args.Value<string>("targetProfile") is string profile && target.Profile != profile) throw new BridgeException("TargetIncompatible", "The observed target profile does not match the selected profile.");
            if (target.Family == "unknown" || target.Software == null) throw new BridgeException("HmiTargetUnavailable", "The hardware exists but supported HMI software is not accessible.", target.Diagnostic);
            capture.Status = "complete";
            return target;
        }

        private JObject HmiDeviceView(Project project, HmiDiscoveredTarget target)
            => new JObject { ["identity"] = HmiIdentity(project, target, "hmiDevice", target.Path, target.Name), ["kind"] = "hmiDevice", ["name"] = target.Name,
                ["parentPath"] = target.DevicePath, ["family"] = target.Family, ["hmiPath"] = target.Path, ["devicePath"] = target.DevicePath,
                ["profile"] = target.Profile, ["catalogIdentifier"] = target.CatalogIdentifier, ["runtimeVersion"] = target.RuntimeVersion,
                ["catalogEvidence"] = target.CatalogEvidence,
                ["nativeType"] = target.Software?.GetType().FullName, ["availability"] = target.Diagnostic == null ? "available" : "unavailable",
                ["diagnostic"] = target.Diagnostic, ["source"] = new JObject { ["kind"] = "native", ["property"] = "SoftwareContainer.Software" },
                ["qualification"] = "unverified", ["runtimeVerified"] = false };

        private static string HmiSafeAttribute(IEngineeringObject item, string name)
        {
            try { return Convert.ToString(item.GetAttribute(name), System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }
        private JObject HmiCatalogCandidate(IEngineeringObject hardware, string path, bool lookupCatalog, HmiCapture capture)
        {
            string identifier = null; bool complete = true;
            try { identifier = (hardware as HardwareObject)?.TypeIdentifier; }
            catch (Exception error) { complete = false; capture.Diagnostics.Add(HmiDiagnostic("HardwareObject.TypeIdentifier", error)); }
            string typeName = HmiSafeAttribute(hardware, "TypeName");
            JObject definition = null;
            if (lookupCatalog && !string.IsNullOrEmpty(identifier) && identifier.StartsWith("OrderNumber:", StringComparison.Ordinal))
            {
                if (!capture.CatalogDefinitions.TryGetValue(identifier, out var cached))
                {
                    if (capture.CatalogDefinitions.Count >= 128) { complete = false; capture.Budget.Truncated = true; }
                    else
                    {
                        cached = new JObject { ["complete"] = false };
                        try
                        {
                            string filter = identifier.Substring("OrderNumber:".Length).Split('/')[0];
                            var entries = _portal.HardwareCatalog.Find(filter);
                            cached["complete"] = entries.Count <= 512;
                            cached["definition"] = HmiInspectionRules.ExactCatalogDefinition(identifier,
                                entries.Take(512).Select(entry => new JObject { ["typeIdentifier"] = entry.TypeIdentifier,
                                    ["typeIdentifierNormalized"] = entry.TypeIdentifierNormalized, ["typeName"] = entry.TypeName,
                                    ["catalogPath"] = entry.CatalogPath, ["version"] = entry.Version?.ToString() }), entries.Count <= 512);
                        }
                        catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic("Exact HMI hardware catalog lookup", error)); }
                        capture.CatalogDefinitions.Add(identifier, cached);
                    }
                }
                if (cached != null)
                {
                    complete &= cached.Value<bool>("complete");
                    definition = cached["definition"] as JObject;
                    if (definition != null) typeName = definition.Value<string>("typeName") + " " + definition.Value<string>("catalogPath");
                }
            }
            return new JObject { ["catalogIdentifier"] = identifier, ["classification"] = identifier + " " + typeName,
                ["runtimeVersion"] = HmiSafeAttribute(hardware, "Version"), ["sourcePath"] = path,
                ["nativeType"] = hardware.GetType().FullName, ["catalogReadComplete"] = complete,
                ["catalogDefinition"] = definition?.DeepClone(), ["identifierSource"] = "HardwareObject.TypeIdentifier" };
        }

        private JObject HmiHardwareMetadata(DeviceItem hardware, string hardwarePath, string family, HmiCapture capture)
        {
            string identifier = null, runtime = null; var classification = new List<string>();
            IEngineeringObject current = hardware; string currentPath = hardwarePath; Device owningDevice = null; string owningPath = null;
            var ancestors = new List<JObject>(); bool ancestorComplete = true;
            for (int depth = 0; current != null; depth++)
            {
                if (depth >= 32) { ancestorComplete = false; break; }
                var observation = HmiCatalogCandidate(current, currentPath, family != null, capture);
                ancestorComplete &= observation.Value<bool>("catalogReadComplete");
                string candidate = observation.Value<string>("catalogIdentifier"), typeName = observation.Value<string>("classification");
                if (!string.IsNullOrEmpty(candidate)) { classification.Add(candidate); if (string.IsNullOrEmpty(identifier) || candidate.StartsWith("OrderNumber:", StringComparison.Ordinal)) identifier = candidate; }
                if (!string.IsNullOrEmpty(typeName)) classification.Add(typeName);
                string version = observation.Value<string>("runtimeVersion"); if (!string.IsNullOrEmpty(version)) runtime = version;
                ancestors.Add(observation);
                if (current is DeviceItem item)
                {
                    current = item.Parent;
                    int boundary = currentPath.LastIndexOf("/items/", StringComparison.Ordinal);
                    if (boundary < 0) { ancestorComplete = false; break; }
                    currentPath = currentPath.Substring(0, boundary);
                }
                else { owningDevice = current as Device; owningPath = currentPath; break; }
            }
            if (family == null) return new JObject { ["catalogIdentifier"] = identifier, ["classification"] = string.Join(" ", classification), ["runtimeVersion"] = runtime };
            var direct = HmiInspectionRules.CatalogSelection(family, ancestors, ancestorComplete, 1, "nativeAncestor");
            if (direct.Value<string>("profile") != "unknown" || !ancestorComplete || owningDevice == null) return direct;
            // Some panels expose the runtime and the catalog-bearing panel as sibling DeviceItems.
            // Same-device association is usable only after complete catalog AND runtime enumeration.
            var candidates = new List<JObject>(); int count = 0, familyRuntimes = 0; bool complete = true;
            try
            {
                foreach (var entry in Items(owningDevice.DeviceItems, owningPath, capture.Budget, 0))
                {
                    if (++count > 128) { complete = false; break; }
                    var observation = HmiCatalogCandidate(entry.Item, entry.Path, true, capture);
                    complete &= observation.Value<bool>("catalogReadComplete");
                    candidates.Add(observation);
                    var software = entry.Item.GetService<SoftwareContainer>()?.Software;
                    string nativeType = software?.GetType().FullName;
                    if (nativeType == (family == "classic" ? "Siemens.Engineering.Hmi.HmiTarget" : "Siemens.Engineering.HmiUnified.HmiSoftware")) familyRuntimes++;
                }
            }
            catch (Exception error) { complete = false; if (capture.Diagnostics.Count < 100) capture.Diagnostics.Add(HmiDiagnostic("HMI owning-device catalog", error)); else capture.Budget.Truncated = true; }
            return HmiInspectionRules.CatalogSelection(family, candidates, complete && !capture.Budget.Truncated, familyRuntimes, "nativeOwningDeviceCatalog");
        }
        private static JObject HmiDiagnostic(string operation, Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            string type = error.GetType().Name;
            string code = type.IndexOf("License", StringComparison.OrdinalIgnoreCase) >= 0 ? "HmiLicenseUnavailable" : type.IndexOf("Access", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0 ? "HmiAccessDenied" : error is FileNotFoundException || error is FileLoadException ? "HmiPrerequisiteMissing" : error is BridgeException bridge ? bridge.Code : "HmiNativeReadFailed";
            return new JObject { ["code"] = code, ["operation"] = operation, ["nativeException"] = error.GetType().FullName, ["reason"] = error.Message, ["retrySafe"] = true };
        }

        private HmiNativeEntry HmiAdd(Project project, HmiDiscoveredTarget target, HmiCapture capture, IEngineeringObject native, string path, string parent, string kind, string name, bool budgetAlreadyTaken = false, string screenRole = null)
        {
            if (!budgetAlreadyTaken && !capture.Budget.Take()) return null;
            var identity = HmiIdentity(project, target, kind, path, name);
            var entry = new HmiNativeEntry { Native = native, Identity = identity,
                Row = new JObject { ["identity"] = identity, ["kind"] = kind, ["name"] = name, ["parentPath"] = parent,
                    ["nativeType"] = native.GetType().FullName, ["properties"] = HmiProperties(native, capture),
                    ["source"] = new JObject { ["kind"] = "native", ["scriptsExecuted"] = false } } };
            if (screenRole != null) entry.Row["screenRole"] = screenRole;
            capture.RetainedBytes += Encoding.UTF8.GetByteCount(entry.Row.ToString(Formatting.None));
            if (capture.RetainedBytes > 8 * 1024 * 1024) { capture.Budget.Truncated = true; return null; }
            capture.Entries.Add(entry); return entry;
        }

        private static JObject HmiProperties(IEngineeringObject native, HmiCapture capture)
        {
            var result = new JObject();
            // Public scalar properties and observed read-only attributes only. No method invocation or script evaluation.
            foreach (var property in native.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (capture.Diagnostics.Count >= 100) { capture.Budget.Truncated = true; break; }
                if (!property.CanRead || property.GetIndexParameters().Length != 0 || property.Name == "Parent" || IsSensitiveAttribute(property.Name)) continue;
                var type = property.PropertyType;
                if (!(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid))) continue;
                try { result[property.Name] = HmiScalar(property.GetValue(native, null)); }
                catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic(native.GetType().Name + "." + property.Name, error)); capture.Status = "unavailable"; }
                if (result.Count >= 128) { capture.Status = "truncated"; break; }
            }
            try
            {
                foreach (var attribute in native.GetAttributeInfos().Take(129))
                {
                    if (capture.Diagnostics.Count >= 100) { capture.Budget.Truncated = true; break; }
                    if (result.Count >= 128) { capture.Status = "truncated"; break; }
                    if (result[attribute.Name] != null || (attribute.AccessMode & EngineeringAttributeAccessMode.Read) == 0 || IsSensitiveAttribute(attribute.Name)) continue;
                    try { result[attribute.Name] = HmiScalar(native.GetAttribute(attribute.Name)); }
                    catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic(attribute.Name, error)); capture.Status = "unavailable"; }
                }
            }
            catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic("GetAttributeInfos", error)); capture.Status = "unavailable"; }
            return result;
        }
        private static JToken HmiScalar(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is string text) return text.Length > 8192 ? new JObject { ["value"] = text.Substring(0, 8192), ["truncated"] = true, ["originalLength"] = text.Length } : (JToken)new JValue(text);
            Type type = value.GetType();
            if (type.IsEnum || value is Guid) return new JValue(value.ToString());
            if (type.IsPrimitive || value is decimal) return JToken.FromObject(value);
            return new JObject { ["nativeType"] = type.FullName, ["expanded"] = false, ["reason"] = "Non-scalar native value; no proxy serialization." };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HmiCaptureClassic(Project project, HmiDiscoveredTarget target, string section, JObject args, HmiCapture capture, bool includeScreenOwner = false)
        {
            var hmi = (Siemens.Engineering.Hmi.HmiTarget)target.Software;
            if (section == "connections") foreach (var item in hmi.Connections) { if (HmiAdd(project, target, capture, item, target.Path + "/connections/" + Segment(item.Name), target.Path, "hmiConnection", item.Name) == null) break; }
            else if (section == "tags") HmiClassicFolder(project, target, hmi.TagFolder, target.Path + "/tagTables", "TagTables", "hmiTagTable", capture);
            else if (section == "screens" || section == "items")
            {
                if (section == "screens") HmiClassicScreens(project, target, hmi, capture,
                    args.Value<string>("objectPath") ?? (args["target"] as JObject ?? args["identity"] as JObject)?.Value<string>("objectPath"));
                else
                {
                    string screenPath = args.Value<string>("screenPath");
                    if (string.IsNullOrEmpty(screenPath)) throw new BridgeException("InvalidRequest", "Classic screen item inspection requires an exact screenPath and artifactDirectory for the retained native XML export.");
                    var scan = new HmiCapture(capture.Budget);
                    HmiClassicScreens(project, target, hmi, scan, screenPath);
                    if (scan.Budget.Truncated || !scan.CandidateScopesComplete) throw new BridgeException("TraversalLimit", "Screen lookup is incomplete.");
                    capture.ClassicScreenScopes = scan.ClassicScreenScopes;
                    var selectedScreen = scan.Entries.SingleOrDefault(e => e.Identity.Value<string>("objectPath") == screenPath);
                    if (selectedScreen == null) throw new BridgeException("ObjectNotFound", "The exact Classic screen was not found.");
                    var screen = selectedScreen.Native as Siemens.Engineering.Hmi.Screen.Screen;
                    if (screen == null || selectedScreen.Row.Value<string>("screenRole") != "regular")
                    {
                        if (includeScreenOwner)
                        {
                            HmiAdd(project, target, capture, selectedScreen.Native, screenPath, selectedScreen.Row.Value<string>("parentPath"), "hmiScreen", selectedScreen.Row.Value<string>("name"), screenRole: selectedScreen.Row.Value<string>("screenRole"));
                        }
                        capture.Status = "unsupported";
                        capture.Diagnostics.Add(new JObject { ["code"] = "HmiScreenItemsUnsupported", ["screenRole"] = selectedScreen.Row["screenRole"].DeepClone(),
                            ["reason"] = "This Classic screen role is inventoried; its item/binding XML grammar is not qualified. Select the screens section for exact native scalar metadata." });
                        return;
                    }
                    var file = NewInspectionArtifact(args, "hmi-screen"); screen.Export(file, ExportOptions.WithDefaults);
                    var artifact = FileArtifact(file); capture.Artifacts.Add(artifact);
                    if (includeScreenOwner)
                    {
                        var owner = HmiAdd(project, target, capture, screen, screenPath, screenPath.Substring(0, screenPath.LastIndexOf('/')), "hmiScreen", screen.Name);
                        if (owner == null) return;
                        HmiAttachMetadata(owner, "behaviorCoverage", new JObject { ["status"] = "unresolved", ["sourceArtifact"] = artifact.DeepClone(),
                            ["reason"] = "Classic screen behavior XML is retained in the native export. No qualified event/script/navigation XML parser is available." }, capture);
                    }
                    if (file.Length > 16 * 1024 * 1024) throw new BridgeException("HmiExportLimit", "Native screen export exceeds the 16 MiB parser bound; retained artifact is available.");
                    using (var reader = XmlReader.Create(file.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }))
                        foreach (var row in HmiInspectionRules.ClassicItems(XDocument.Load(reader), screenPath, artifact.Value<string>("sha256"), capture.Budget.Take))
                        {
                            var identity = HmiIdentity(project, target, row.Value<string>("kind"), row.Value<string>("objectPath"), row.Value<string>("name"));
                            identity["exportSha256"] = artifact["sha256"]; row["identity"] = identity;
                            identity["screenPath"] = screenPath;
                            capture.RetainedBytes += Encoding.UTF8.GetByteCount(row.ToString(Formatting.None));
                            if (capture.RetainedBytes > 8 * 1024 * 1024) { capture.Budget.Truncated = true; break; }
                            capture.Entries.Add(new HmiNativeEntry { Identity = identity, Row = row });
                        }
                    capture.Diagnostics.Add(new JObject { ["code"] = "HmiXmlCoverage", ["reason"] = "Named Hmi.Screen payload objects parsed from retained native XML. Unknown binding/event/type shapes remain unresolved; no general XML preservation or mutation qualification is inferred." });
                }
            }
            else if (section == "scripts") HmiClassicFolder(project, target, hmi.VBScriptFolder, target.Path + "/scripts", "VBScripts", "hmiScript", capture);
            else if (section == "resources")
            {
                HmiComposition(project, target, hmi, "TextLists", target.Path + "/textLists", "hmiResource", capture);
                HmiComposition(project, target, hmi, "GraphicLists", target.Path + "/graphicLists", "hmiResource", capture);
            }
            else { capture.Status = "unsupported"; capture.Diagnostics.Add(new JObject { ["code"] = "HmiSectionUnsupported", ["reason"] = "Installed Classic HmiTarget exposes no public alarm composition. No private project database is inspected." }); }
        }

        private void HmiClassicScreens(Project project, HmiDiscoveredTarget target, Siemens.Engineering.Hmi.HmiTarget hmi, HmiCapture capture, string selectedPath = null)
        {
            capture.ClassicScreenScopes = HmiClassicScreenInventory.Capture(target.Path, property => {
                switch (property)
                {
                    case "ScreenFolder": return hmi.ScreenFolder;
                    case "ScreenTemplateFolder": return hmi.ScreenTemplateFolder;
                    case "ScreenPopupFolder": return hmi.ScreenPopupFolder;
                    case "ScreenSlideinFolder": return hmi.ScreenSlideinFolder;
                    case "ScreenGlobalElements": return hmi.ScreenGlobalElements;
                    case "ScreenOverview": return hmi.ScreenOverview;
                    default: throw new InvalidOperationException("Unknown Classic screen scope.");
                }
            }, (owner, collection) => (IEnumerable)((IEngineeringObject)owner).GetComposition(collection),
                native => HmiSafeAttribute((IEngineeringObject)native, "Name"),
                () => !capture.Budget.Truncated && capture.Budget.Take(),
                (native, path, parent, role, name) => {
                    return HmiAdd(project, target, capture, (IEngineeringObject)native, path, parent, "hmiScreen", name,
                        budgetAlreadyTaken: true, screenRole: role) != null;
                }, (scope, error) => {
                    capture.Status = "unavailable"; capture.CandidateScopesComplete = false;
                    if (capture.Diagnostics.Count < 100) capture.Diagnostics.Add(HmiDiagnostic(scope, error));
                    else capture.Budget.Truncated = true;
                }, selectedPath: selectedPath);
            if (capture.ClassicScreenScopes.Any(s => s.Value<string>("status") == "truncated"))
            { capture.Budget.Truncated = true; capture.CandidateScopesComplete = false; }
        }

        private void HmiClassicFolder(Project project, HmiDiscoveredTarget target, IEngineeringObject folder, string path, string collection, string kind, HmiCapture capture, int depth = 0)
        {
            if (depth > 32 || !capture.Budget.Take()) { capture.Budget.Truncated = true; return; }
            foreach (IEngineeringObject item in (IEnumerable)folder.GetComposition(collection))
            {
                string name = HmiSafeAttribute(item, "Name");
                string itemPath = HmiInspectionRules.NamedChildPath(path, name);
                if (itemPath == null) { if (!HmiMissingName(capture, collection, item)) return; continue; }
                if (HmiAdd(project, target, capture, item, itemPath, path, kind, name) == null) return;
                if (kind == "hmiTagTable") HmiComposition(project, target, item, "Tags", itemPath + "/tags", "hmiTag", capture);
            }
            foreach (IEngineeringObject child in (IEnumerable)folder.GetComposition("Folders"))
            {
                if (capture.Budget.Truncated) return;
                string name = HmiSafeAttribute(child, "Name");
                string childPath = HmiInspectionRules.NamedChildPath(path + "/folders", name);
                if (childPath == null) { if (!HmiMissingName(capture, "Folders", child)) return; continue; }
                HmiClassicFolder(project, target, child, childPath, collection, kind, capture, depth + 1);
            }
        }
        private static bool HmiMissingName(HmiCapture capture, string composition, IEngineeringObject native)
        {
            capture.Status = "unavailable"; capture.CandidateScopesComplete = false;
            if (!capture.Budget.Take() || capture.Diagnostics.Count >= 100) { capture.Budget.Truncated = true; return false; }
            capture.Diagnostics.Add(new JObject { ["code"] = "HmiIdentityUnavailable", ["composition"] = composition, ["nativeType"] = native.GetType().FullName,
                ["reason"] = "The native name is missing or unreadable; this object and its descendants cannot receive an exact path." });
            return true;
        }
        private void HmiComposition(Project project, HmiDiscoveredTarget target, IEngineeringObject owner, string collection, string path, string kind, HmiCapture capture)
        {
            foreach (IEngineeringObject native in (IEnumerable)owner.GetComposition(collection))
            {
                string name = HmiSafeAttribute(native, "Name") ?? HmiSafeAttribute(native, "PropertyName");
                if (string.IsNullOrEmpty(name)) { if (!HmiMissingName(capture, collection, native)) break; continue; }
                if (HmiAdd(project, target, capture, native, path + "/" + Segment(name), path, kind, name) == null) break;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HmiCaptureUnified(Project project, HmiDiscoveredTarget target, string section, JObject args, HmiCapture capture, bool includeScreenOwner = false)
        {
            var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)target.Software;
            switch (section)
            {
                case "connections": HmiComposition(project, target, hmi, "Connections", target.Path + "/connections", "hmiConnection", capture); break;
                case "tags":
                    HmiComposition(project, target, hmi, "TagTables", target.Path + "/tagTables", "hmiTagTable", capture);
                    foreach (var tag in hmi.Tags)
                    {
                        string parent = target.Path + "/tagTables/" + Segment(tag.TagTableName) + "/tags";
                        var row = HmiAdd(project, target, capture, tag, parent + "/" + Segment(tag.Name), parent, "hmiTag", tag.Name);
                        if (row == null) break;
                        HmiUnifiedTagMembers(project, target, tag, row.Identity.Value<string>("objectPath"), capture, 0);
                    }
                    break;
                case "screens":
                    string exactScreen = args.Value<string>("objectPath") ?? (args["target"] as JObject ?? args["identity"] as JObject)?.Value<string>("objectPath");
                    foreach (var screen in hmi.Screens)
                    {
                        var row = HmiAdd(project, target, capture, screen, target.Path + "/screens/" + Segment(screen.Name), target.Path + "/screens", "hmiScreen", screen.Name);
                        if (row == null) break;
                        if (exactScreen == row.Identity.Value<string>("objectPath")) HmiUnifiedBehaviors(row, capture);
                        else HmiAttachMetadata(row, "behaviorCoverage", new JObject { ["status"] = "deferred", ["reason"] = "Select this exact screen objectPath or request items with includeScreenOwner to read its behaviors. General screen inventory does not capture script source." }, capture);
                    }
                    break;
                case "items":
                    string screenPath = args.Value<string>("screenPath");
                    if (string.IsNullOrEmpty(screenPath)) throw new BridgeException("InvalidRequest", "Unified screen item inspection requires exact screenPath.");
                    foreach (var screen in hmi.Screens)
                    {
                        if (!capture.Budget.Take()) break;
                        string path = target.Path + "/screens/" + Segment(screen.Name);
                        if (path == screenPath)
                        {
                            if (includeScreenOwner)
                            {
                                var row = HmiAdd(project, target, capture, screen, path, target.Path + "/screens", "hmiScreen", screen.Name);
                                if (row == null) return;
                                HmiUnifiedBehaviors(row, capture);
                            }
                            HmiUnifiedItems(project, target, screen, path, capture, 0); return;
                        }
                    }
                    if (!capture.Budget.Truncated) throw new BridgeException("ObjectNotFound", "The exact Unified screen was not found.");
                    break;
                case "alarms":
                    HmiComposition(project, target, hmi, "DiscreteAlarms", target.Path + "/discreteAlarms", "hmiAlarm", capture);
                    HmiComposition(project, target, hmi, "AnalogAlarms", target.Path + "/analogAlarms", "hmiAlarm", capture);
                    foreach (var alarm in capture.Entries.Where(e => e.Identity.Value<string>("kind") == "hmiAlarm")) HmiUnifiedAlarmParameters(alarm, capture);
                    break;
                case "scripts": HmiComposition(project, target, hmi, "Scripts", target.Path + "/scripts", "hmiScript", capture); break;
                case "resources":
                    HmiComposition(project, target, hmi, "HmiTextLists", target.Path + "/textLists", "hmiResource", capture);
                    HmiComposition(project, target, hmi, "HmiGraphicLists", target.Path + "/graphicLists", "hmiResource", capture);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HmiUnifiedAlarmParameters(HmiNativeEntry entry, HmiCapture capture)
        {
            if (capture.Budget.Truncated) return;
            try
            {
                var alarm = entry.Native as Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.AlarmBase;
                if (alarm == null) return;
                var parameters = alarm.AlarmParameterTags as IList<string>;
                if (parameters == null || parameters.Count > 10)
                {
                    capture.Status = "unavailable";
                    HmiAttachMetadata(entry, "alarmParameterTags", new JObject { ["expanded"] = false, ["reason"] = "Native AlarmParameterTags was not the documented list of at most ten strings." }, capture);
                    return;
                }
                var values = new JArray();
                for (int i = 0; i < parameters.Count; i++) { if (!capture.Budget.Take()) return; values.Add(HmiScalar(parameters[i])); }
                HmiAttachMetadata(entry, "alarmParameterTags", values, capture);
            }
            catch (Exception error) { capture.Status = "unavailable"; if (capture.Diagnostics.Count < 100) capture.Diagnostics.Add(HmiDiagnostic("AlarmParameterTags", error)); else capture.Budget.Truncated = true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HmiUnifiedTagMembers(Project project, HmiDiscoveredTarget target, Siemens.Engineering.HmiUnified.HmiTags.HmiTag tag, string path, HmiCapture capture, int depth)
        {
            if (depth > 32) { capture.Budget.Truncated = true; return; }
            foreach (var member in tag.Members)
            {
                string memberPath = path + "/members/" + Segment(member.Name);
                if (HmiAdd(project, target, capture, member, memberPath, path, "hmiTag", member.Name) == null) return;
                HmiUnifiedTagMembers(project, target, member, memberPath, capture, depth + 1);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HmiUnifiedItems(Project project, HmiDiscoveredTarget target, IEngineeringObject owner, string parent, HmiCapture capture, int depth)
        {
            if (depth > 32) { capture.Budget.Truncated = true; return; }
            var composition = owner.GetCompositionInfos().FirstOrDefault(c => c.Name == "ScreenItems");
            if (composition == null) return;
            foreach (Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBase item in (IEnumerable)owner.GetComposition("ScreenItems"))
            {
                string path = parent + "/items/" + Segment(item.Name);
                var entry = HmiAdd(project, target, capture, item, path, parent, "hmiScreenItem", item.Name);
                if (entry == null) return;
                string screenPrefix = target.Path + "/screens/";
                int screenEnd = path.IndexOf('/', screenPrefix.Length);
                entry.Identity["screenPath"] = screenEnd < 0 ? path : path.Substring(0, screenEnd);
                HmiUnifiedBehaviors(entry, capture);
                if (item is Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer container)
                {
                    var faceplate = HmiReadback(container);
                    faceplate["containedType"] = container.ContainedType;
                    var ports = new JArray();
                    foreach (var port in container.Interface)
                    {
                        if (!capture.Budget.Take()) break;
                        ports.Add(new JObject { ["propertyName"] = port.PropertyName, ["value"] = HmiScalar(port.Value), ["bindingResolved"] = false });
                    }
                    faceplate["interface"] = ports;
                    HmiAttachMetadata(entry, "faceplate", faceplate, capture);
                }
                HmiUnifiedItems(project, target, item, path, capture, depth + 1);
            }
        }

        private HmiNativeEntry HmiResolveNativeObject(Project project, JObject args)
        {
            var capture = new HmiCapture(50000);
            var target = HmiResolveNativeTarget(project, args, capture);
            string kind = args.Value<string>("kind") ?? "hmiDevice", path = args.Value<string>("objectPath") ?? target.Path;
            if (kind == "hmiDevice" && path == target.Path) return new HmiNativeEntry { Native = target.Software, Identity = HmiIdentity(project, target, kind, path, target.Name), Row = HmiDeviceView(project, target) };
            string section;
            try { section = HmiInspectionRules.Section(kind); } catch (ArgumentException error) { throw new BridgeException("UnsupportedObjectKind", error.Message); }
            RequireHmiModule(target.Family);
            if (target.Family == "classic") HmiCaptureClassic(project, target, section, args, capture); else HmiCaptureUnified(project, target, section, args, capture);
            if (capture.Budget.Truncated || !capture.CandidateScopesComplete) throw new BridgeException("TraversalLimit", "HMI object uniqueness cannot be established within the completely named traversal scope.");
            var matches = capture.Entries.Where(e => e.Identity.Value<string>("objectPath") == path && e.Identity.Value<string>("kind") == kind).ToList();
            if (matches.Count != 1) throw new BridgeException(matches.Count == 0 ? "ObjectNotFound" : "AmbiguousTarget", "An exact, unique HMI object matching all supplied selectors was not found.");
            if (args.Value<string>("exportSha256") is string fingerprint && matches[0].Identity.Value<string>("exportSha256") != fingerprint)
                throw new BridgeException("StaleHmiIdentity", "The export-local HMI item identity refers to different native screen content.");
            return matches[0];
        }
        private JObject HmiObjectResolve(JObject args)
        {
            var project = SelectProject(args);
            var selector = (JObject)(args["target"] as JObject ?? args["identity"] as JObject ?? args).DeepClone();
            foreach (string name in new[] { "artifactDirectory", "screenPath" }) if (args[name] != null) selector[name] = args[name].DeepClone();
            var selected = HmiResolveNativeObject(project, selector);
            var result = Envelope(); result["schemaVersion"] = 2; result["identity"] = selected.Identity;
            result["objectId"] = selected.Identity["objectPath"]; result["engineeringType"] = selected.Native?.GetType().FullName ?? selected.Row.Value<string>("nativeType");
            result["candidates"] = new JArray(selected.Identity.DeepClone()); result["nativeObjectAvailable"] = selected.Native != null;
            result["completeness"] = new JObject { ["scopeComplete"] = true, ["truncated"] = false, ["scope"] = "Exact current HMI path within its project copy and session generation." };
            return result;
        }
    }
}
