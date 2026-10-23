using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // No optional HMI type is stored in session fields or mentioned in dispatcher signatures.
        // Family-specific method bodies are JIT compiled only after the installed module check.
        private JObject LibraryHmiQuery(JObject args)
        {
            string action = args.Value<string>("action") ?? "hmi_capabilities";
            if (action == "hmi_capabilities") return LibraryHmiCapabilities();
            var project = SelectProject(args);
            if (action == "hmi_targets")
                return new JObject { ["targets"] = new JArray(HmiTargets(project).Select(HmiTargetView)), ["complete"] = true };
            if (action != "hmi_inventory") throw new BridgeException("InvalidRequest", "Unknown HMI query action.");
            var target = ResolveHmiTarget(project, args["target"] as JObject ?? args);
            int limit = args.Value<int?>("limit") ?? 200;
            if (limit < 1 || limit > 200) throw new BridgeException("InvalidRequest", "HMI inventory limit must be 1–200.");
            RequireHmiModule(target.Family);
            return target.Family == "classic" ? ClassicHmiInventory(target, limit) : UnifiedHmiInventory(target, limit);
        }

        private JObject LibraryHmiCapabilities()
        {
            var modules = new JArray();
            foreach (string family in new[] { "classic", "unified" })
            {
                string path = Path.Combine(_apiDirectory, HmiAssemblyName(family) + ".dll");
                var module = new JObject { ["family"] = family, ["assembly"] = Path.GetFileName(path),
                    ["installedApi"] = File.Exists(path), ["apiAvailability"] = "unavailable", ["nativeQualified"] = false, ["runtimeQualified"] = false };
                if (File.Exists(path))
                {
                    try
                    {
                        var identity = HmiModuleIdentity(family, path);
                        module["assemblyVersion"] = identity["assemblyVersion"].DeepClone(); module["assemblyIdentity"] = identity;
                        if (identity.Value<bool>("accepted")) module["apiAvailability"] = "available";
                        else module["diagnostic"] = identity.DeepClone();
                    }
                    catch (Exception error) { module["diagnostic"] = HmiDiagnostic("optionalAssembly", error); }
                }
                modules.Add(module);
            }
            return new JObject { ["schemaVersion"] = 1, ["modules"] = modules,
                ["operations"] = new JArray(
                    HmiCapability("classic", "inventory", "typed", "Screens, tag tables, tags, connections and supported master-copy destinations."),
                    HmiCapability("classic", "screen_type", "typed", "ScreenComposition.CreateFrom(ScreenLibraryTypeVersion); exact native version readback."),
                    HmiCapability("classic", "master_copy", "typed", "One root screen, screen template, popup, tag, tag table or VB script; native root type and collisions checked."),
                    HmiCapability("classic", "faceplate_binding", "unsupported", "Classic exposes screen XML instead of a faceplate object model. A separately qualified preserving XML adapter is required."),
                    HmiCapability("unified", "inventory", "typed", "Screens, top-level faceplate containers/interfaces, tags and connections."),
                    HmiCapability("unified", "faceplate_create", "typed", "A committed exact project-library version, explicit screen rectangle, and native version service readback are required."),
                    HmiCapability("unified", "faceplate_bind", "typed", "Existing exact-version container and explicit existing tag/interface mapping; no positional interface guesses."),
                    HmiCapability("unified", "tag_create", "typed", "Internal tags with explicit primitive data type; external PLC binding requires a separately verified connection mapping."),
                    HmiCapability("unified", "master_copy", "unsupported", "No Unified CreateFrom(MasterCopy) route exists in the installed public API; Classic operations are not reused.")),
                ["qualification"] = "Implementation is distinct from target qualification. Comfort, Advanced, Professional, Basic, Unified PC and Unified Comfort require independent engineering/runtime fixtures." };
        }

        private static JObject HmiCapability(string family, string operation, string implementation, string limitation)
            => new JObject { ["family"] = family, ["operation"] = operation, ["implementation"] = implementation,
                ["engineeringQualified"] = false, ["runtimeQualified"] = false, ["limitation"] = limitation };

        private static string HmiAssemblyName(string family)
        {
            try { HmiRecipeRules.RequireFamily(family); }
            catch (ArgumentException ex) { throw new BridgeException("TargetIncompatible", ex.Message); }
            return family == "classic" ? "Siemens.Engineering.WinCC" : "Siemens.Engineering.WinCCUnified";
        }

        private void RequireHmiModule(string family)
        {
            string path = Path.Combine(_apiDirectory, HmiAssemblyName(family) + ".dll");
            if (!File.Exists(path)) throw new BridgeException("HmiPrerequisiteMissing", "Install the matching V21 " + family + " engineering product and Openness module on this TIA host.",
                new JObject { ["family"] = family, ["requiredAssembly"] = Path.GetFileName(path), ["retrySafe"] = true });
            var evidence = HmiModuleIdentity(family, path);
            if (!evidence.Value<bool>("accepted")) throw new BridgeException(evidence.Value<string>("code"),
                "The configured optional HMI module does not match the required V21 assembly name, Siemens public key identity or neutral culture.", evidence);
        }

        private static JObject HmiModuleIdentity(string family, string path)
        {
            var observed = AssemblyName.GetAssemblyName(path);
            var core = typeof(TiaPortal).Assembly.GetName();
            string expectedToken = BitConverter.ToString(core.GetPublicKeyToken() ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();
            string observedToken = BitConverter.ToString(observed.GetPublicKeyToken() ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();
            string expectedName = HmiAssemblyName(family);
            string code = HmiAssemblyIdentityRules.Rejection(expectedName, observed.Name, expectedToken, observedToken, observed.Version.Major, observed.CultureName);
            return new JObject { ["accepted"] = code == null, ["code"] = code, ["family"] = family,
                ["requiredAssemblyName"] = expectedName, ["observedAssemblyName"] = observed.Name,
                ["requiredMajorVersion"] = 21, ["observedMajorVersion"] = observed.Version.Major, ["assemblyVersion"] = observed.Version.ToString(),
                ["requiredPublicKeyToken"] = expectedToken, ["observedPublicKeyToken"] = observedToken,
                ["expectedKeySource"] = core.FullName, ["observedCulture"] = observed.CultureName ?? "",
                ["scope"] = "Public assembly metadata identity only; no optional assembly is loaded and no license is inferred." };
        }

        private IEnumerable<HmiTargetSelection> HmiTargets(Project project)
        {
            var budget = new ScanBudget(50000);
            var result = new List<HmiTargetSelection>();
            foreach (var entry in DeviceItems(project, budget))
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software;
                if (software == null) continue;
                string type = software.GetType().FullName;
                string family = type == "Siemens.Engineering.Hmi.HmiTarget" ? "classic" : type == "Siemens.Engineering.HmiUnified.HmiSoftware" ? "unified" : null;
                if (family != null) result.Add(new HmiTargetSelection { Software = software, Family = family,
                    Path = entry.Path + "/hmi/" + Segment(software.Name), Name = software.Name,
                    Device = HmiOwningDevice(entry.Item), DevicePath = entry.Path.Substring(0, entry.Path.IndexOf("/items/", StringComparison.Ordinal)) });
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "HMI device lookup exceeded its bound; no ambiguous target was selected.");
            return result.OrderBy(t => t.Path, StringComparer.Ordinal);
        }

        private HmiTargetSelection ResolveHmiTarget(Project project, JObject target)
        {
            string family = target?.Value<string>("family");
            HmiAssemblyName(family);
            string path = HmiText(target, "hmiPath", 4096);
            var found = HmiTargets(project).SingleOrDefault(t => t.Path == path);
            if (found == null) throw new BridgeException("HmiTargetNotFound", "Choose an exact hmiPath returned by library_query hmi_targets.");
            if (found.Family != family) throw new BridgeException("TargetIncompatible", "The selected HMI target belongs to " + found.Family + ", not " + family + ".");
            RequireHmiModule(family);
            return found;
        }

        private static Device HmiOwningDevice(IEngineeringObject item)
        {
            for (int depth = 0; item != null && depth < 64; depth++, item = item.Parent)
                if (item is Device device) return device;
            throw new BridgeException("HmiTargetNotFound", "The owning HMI device could not be resolved within the native hierarchy.");
        }

        private static JObject HmiTargetView(HmiTargetSelection target) => new JObject { ["family"] = target.Family, ["hmiPath"] = target.Path, ["name"] = target.Name,
            ["compileScope"] = target.Family == "unified" ? target.DevicePath : target.Path,
            ["compileScopeKind"] = target.Family == "unified" ? "owningDeviceHardwareAndSoftware" : "hmiSoftware" };

        private static ICompilable LibraryHmiCompiler(HmiTargetSelection selected)
        {
            // Unified HmiSoftware does not implement IEngineeringServiceProvider. DeviceItem compiles HW only.
            // Device is the documented HW+SW route; expose that broader scope in every preview/readback.
            var provider = selected.Family == "unified" ? (IEngineeringServiceProvider)selected.Device : selected.Software as IEngineeringServiceProvider;
            return provider?.GetService<ICompilable>() ?? throw new BridgeException("HmiCompileUnavailable", "No native compiler service exists for the selected HMI compilation scope.");
        }

        private JObject PrepareLibraryHmiAction(Project project, JObject payload, LibraryTypeVersion version, MasterCopy master)
        {
            RequireHmiLibraryHandoff(project, payload, version, master);
            var target = ResolveHmiTarget(project, payload["target"] as JObject);
            LibraryHmiCompiler(target); // Availability is established before any native mutation.
            try
            {
                return target.Family == "classic" ? PrepareClassicHmi(target, payload, version, master)
                    : PrepareUnifiedHmi(project, target, payload, version, master);
            }
            catch (ArgumentException ex) { throw new BridgeException("IntegrationMappingRequired", ex.Message); }
        }

        private JObject ApplyLibraryHmiAction(Project project, JObject payload, LibraryTypeVersion version, MasterCopy master)
        {
            var target = ResolveHmiTarget(project, payload["target"] as JObject);
            // The enclosing resource-aware executor must compare its prepared fingerprint before calling this method.
            PrepareLibraryHmiAction(project, payload, version, master);
            return target.Family == "classic" ? ApplyClassicHmi(target, payload, version, master)
                : ApplyUnifiedHmi(project, target, payload, version);
        }

        private JObject CompileLibraryHmiTarget(Project project, JObject target)
        {
            var selected = ResolveHmiTarget(project, target);
            var compiler = LibraryHmiCompiler(selected);
            var result = compiler.Compile();
            return new JObject { ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount,
                ["targets"] = new JArray(ChangeCompilation(result, selected.Family == "unified" ? selected.DevicePath : selected.Path)), ["runtimeVerified"] = false };
        }

        private static string HmiText(JObject value, string name, int max = 256)
        {
            if (value == null || value[name]?.Type != JTokenType.String) throw new BridgeException("InvalidRequest", name + " must be an explicit string.");
            string text = value.Value<string>(name);
            if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl)) throw new BridgeException("InvalidRequest", name + " is empty, too long, or contains control characters.");
            return text;
        }

        private static string HmiAction(JObject payload) => payload.Value<string>("hmiAction") ?? "instantiate";

        private static JObject HmiPreview(HmiTargetSelection target, string action, JObject before, JObject effect)
        {
            if (before.DescendantsAndSelf().OfType<JProperty>().Any(p => p.Name.EndsWith("Truncated", StringComparison.OrdinalIgnoreCase)
                && p.Value.Type == JTokenType.Boolean && p.Value.Value<bool>()))
                throw new BridgeException("TraversalLimit", "The HMI change preview exceeded its bounded inventory; truncated evidence cannot authorize a mutation.");
            return new JObject { ["target"] = HmiTargetView(target), ["hmiAction"] = action, ["before"] = before,
                ["effect"] = effect, ["targetFingerprint"] = ChangeHash(before.ToString(Newtonsoft.Json.Formatting.None)),
                ["compileRequired"] = true, ["runtimeVerified"] = false, ["visualVerified"] = false };
        }

        private static JObject HmiReadback(IEngineeringObject item, LibraryTypeVersion expected = null)
        {
            return HmiLibraryLink.Read(item, expected);
        }

        private sealed class HmiTargetSelection
        {
            internal Software Software; internal Device Device; internal string DevicePath; internal string Family; internal string Path; internal string Name;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject ClassicHmiInventory(HmiTargetSelection selected, int limit)
        {
            var hmi = (Siemens.Engineering.Hmi.HmiTarget)selected.Software;
            var items = new JArray(); bool truncated = false;
            foreach (var screen in ClassicScreens(hmi.ScreenFolder.Screens, hmi.ScreenFolder.Folders, "/screens", 0))
            {
                if (items.Count >= limit) { truncated = true; break; }
                items.Add(new JObject { ["kind"] = "screen", ["path"] = screen.Item1, ["name"] = screen.Item2.Name });
            }
            if (!truncated) foreach (var table in ClassicTagTables(hmi.TagFolder.TagTables, hmi.TagFolder.Folders, "/tagTables", 0))
            {
                if (items.Count >= limit) { truncated = true; break; }
                items.Add(new JObject { ["kind"] = "tagTable", ["path"] = table.Item1, ["name"] = table.Item2.Name,
                    ["tags"] = new JArray(table.Item2.Tags.Take(200).Select(t => new JObject { ["name"] = t.Name })), ["tagsTruncated"] = table.Item2.Tags.Count > 200 });
            }
            return new JObject { ["target"] = HmiTargetView(selected), ["items"] = items, ["truncated"] = truncated,
                ["connections"] = new JArray(hmi.Connections.Take(200).Select(c => new JObject { ["name"] = c.Name })),
                ["connectionsTruncated"] = hmi.Connections.Count > 200,
                ["coverage"] = "Screen/tag-table identities and existing connection names; screen children and faceplate bindings require native XML export." };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEnumerable<Tuple<string, Siemens.Engineering.Hmi.Screen.Screen>> ClassicScreens(
            Siemens.Engineering.Hmi.Screen.ScreenComposition screens, Siemens.Engineering.Hmi.Screen.ScreenUserFolderComposition folders, string prefix, int depth)
        {
            if (depth > 32) throw new BridgeException("TraversalLimit", "Classic HMI screen folders exceed 32 levels.");
            foreach (var screen in screens) yield return Tuple.Create(prefix + "/" + Segment(screen.Name), screen);
            foreach (var folder in folders)
                foreach (var screen in ClassicScreens(folder.Screens, folder.Folders, prefix + "/folders/" + Segment(folder.Name), depth + 1)) yield return screen;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEnumerable<Tuple<string, Siemens.Engineering.Hmi.Tag.TagTable>> ClassicTagTables(
            Siemens.Engineering.Hmi.Tag.TagTableComposition tables, Siemens.Engineering.Hmi.Tag.TagUserFolderComposition folders, string prefix, int depth)
        {
            if (depth > 32) throw new BridgeException("TraversalLimit", "Classic HMI tag folders exceed 32 levels.");
            foreach (var table in tables) yield return Tuple.Create(prefix + "/" + Segment(table.Name), table);
            foreach (var folder in folders)
                foreach (var table in ClassicTagTables(folder.TagTables, folder.Folders, prefix + "/folders/" + Segment(folder.Name), depth + 1)) yield return table;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject PrepareClassicHmi(HmiTargetSelection selected, JObject payload, LibraryTypeVersion version, MasterCopy master)
        {
            var hmi = (Siemens.Engineering.Hmi.HmiTarget)selected.Software;
            if (HmiAction(payload) != "instantiate") throw new BridgeException("OperationUnsupported", "Classic currently supports typed screen/script instantiation and explicit master-copy destinations; faceplate binding requires the preserving XML adapter.");
            string destination = HmiText((JObject)payload["target"], "composition");
            string name; string nativeType;
            if (master != null)
            {
                var content = master.ContentDescriptions.ToList();
                if (content.Count != 1) throw new BridgeException("TargetIncompatible", "HMI master-copy instantiation requires exactly one native root.");
                name = content[0].ContentName; nativeType = content[0].ContentType.FullName;
                var allowed = new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["screens"] = "Siemens.Engineering.Hmi.Screen.Screen", ["screenTemplates"] = "Siemens.Engineering.Hmi.Screen.ScreenTemplate",
                    ["screenPopups"] = "Siemens.Engineering.Hmi.Screen.ScreenPopup", ["tagTables"] = "Siemens.Engineering.Hmi.Tag.TagTable",
                    ["tags"] = "Siemens.Engineering.Hmi.Tag.Tag", ["scripts"] = "Siemens.Engineering.Hmi.RuntimeScripting.VBScript" };
                if (!allowed.TryGetValue(destination, out string expected) || expected != nativeType)
                    throw new BridgeException("TargetIncompatible", "Master-copy root type does not match the selected Classic HMI composition.");
            }
            else if (version is Siemens.Engineering.Hmi.Screen.ScreenLibraryTypeVersion && destination == "screens"
                || version is Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryTypeVersion && destination == "scripts")
            {
                if (version.State != LibraryTypeVersionState.Committed) throw new BridgeException("LibraryConflict", "HMI instantiation requires a committed version.");
                name = version.TypeObject.Name; nativeType = version.GetType().FullName;
            }
            else throw new BridgeException("TargetIncompatible", "This Classic HMI type has no typed instantiate route for the selected composition.");
            HmiRecipeRules.RequireName(name, "source native name");
            bool exists;
            switch (destination)
            {
                case "screens": exists = hmi.ScreenFolder.Screens.Find(name) != null; break;
                case "screenTemplates": exists = hmi.ScreenTemplateFolder.ScreenTemplates.Find(name) != null; break;
                case "screenPopups": exists = hmi.ScreenPopupFolder.ScreenPopups.Find(name) != null; break;
                case "tagTables": exists = hmi.TagFolder.TagTables.Find(name) != null; break;
                case "scripts": exists = hmi.VBScriptFolder.VBScripts.Find(name) != null; break;
                case "tags":
                    var table = hmi.TagFolder.TagTables.Find(HmiText((JObject)payload["target"], "tagTable"));
                    if (table == null) throw new BridgeException("HmiTargetNotFound", "The exact root tag table does not exist.");
                    exists = table.Tags.Find(name) != null; break;
                default: throw new BridgeException("TargetIncompatible", "Unsupported Classic destination.");
            }
            if (exists) throw new BridgeException("LibraryConflict", "A same-name HMI object already exists; no overwrite is implicit.");
            return HmiPreview(selected, "instantiate", ClassicHmiInventory(selected, 200), new JObject {
                ["composition"] = destination, ["name"] = name, ["sourceNativeType"] = nativeType,
                ["nativeVersionGuid"] = version?.Guid.ToString("D"), ["preservationCoverage"] = "Native copy; compile and independent screen/resource preservation qualification remain required." });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject ApplyClassicHmi(HmiTargetSelection selected, JObject payload, LibraryTypeVersion version, MasterCopy master)
        {
            var hmi = (Siemens.Engineering.Hmi.HmiTarget)selected.Software;
            string destination = ((JObject)payload["target"]).Value<string>("composition");
            IEngineeringObject created;
            switch (destination)
            {
                case "screens": created = master != null ? hmi.ScreenFolder.Screens.CreateFrom(master) : hmi.ScreenFolder.Screens.CreateFrom((Siemens.Engineering.Hmi.Screen.ScreenLibraryTypeVersion)version); break;
                case "screenTemplates": created = hmi.ScreenTemplateFolder.ScreenTemplates.CreateFrom(master); break;
                case "screenPopups": created = hmi.ScreenPopupFolder.ScreenPopups.CreateFrom(master); break;
                case "tagTables": created = hmi.TagFolder.TagTables.CreateFrom(master); break;
                case "tags": created = hmi.TagFolder.TagTables.Find(((JObject)payload["target"]).Value<string>("tagTable")).Tags.CreateFrom(master); break;
                case "scripts": created = master != null ? hmi.VBScriptFolder.VBScripts.CreateFrom(master) : hmi.VBScriptFolder.VBScripts.CreateFrom((Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryTypeVersion)version); break;
                default: throw new BridgeException("TargetIncompatible", "Unsupported Classic destination.");
            }
            return HmiReadback(created, version);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject UnifiedHmiInventory(HmiTargetSelection selected, int limit)
        {
            var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)selected.Software;
            var screens = new JArray();
            foreach (var screen in hmi.Screens.Take(limit))
                screens.Add(new JObject { ["name"] = screen.Name, ["width"] = screen.Width, ["height"] = screen.Height,
                    ["faceplates"] = new JArray(screen.ScreenItems.OfType<Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer>().Take(200).Select(item => UnifiedFaceplateView(item))),
                    ["screenItemsTruncated"] = screen.ScreenItems.Count > 200 });
            return new JObject { ["target"] = HmiTargetView(selected), ["screens"] = screens,
                ["tags"] = new JArray(hmi.Tags.Take(limit).Select(t => new JObject { ["name"] = t.Name, ["dataType"] = t.DataType,
                    ["hmiDataType"] = t.HmiDataType, ["connection"] = t.Connection, ["plcTag"] = t.PlcTag, ["table"] = t.TagTableName })),
                ["connections"] = new JArray(hmi.Connections.Take(limit).Select(c => new JObject { ["name"] = c.Name, ["partner"] = c.Partner, ["station"] = c.Station, ["driver"] = c.CommunicationDriver })),
                ["truncated"] = hmi.Screens.Count > limit || hmi.Tags.Count > limit || hmi.Connections.Count > limit,
                ["coverage"] = "Top-level screen items and native connection/tag properties; script references, groups and dynamic dependency closure are not inferred." };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject UnifiedFaceplateView(Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer item, LibraryTypeVersion expected = null)
        {
            var result = HmiReadback(item, expected);
            result["name"] = item.Name; result["containedType"] = item.ContainedType;
            result["left"] = item.Left; result["top"] = item.Top; result["width"] = item.Width; result["height"] = item.Height;
            result["interface"] = new JArray(item.Interface.Take(200).Select(p => new JObject { ["property"] = p.PropertyName, ["value"] = p.Value == null ? JValue.CreateNull() : JToken.FromObject(p.Value) }));
            result["interfaceTruncated"] = item.Interface.Count > 200;
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject PrepareUnifiedHmi(Project project, HmiTargetSelection selected, JObject payload, LibraryTypeVersion version, MasterCopy master)
        {
            if (master != null) throw new BridgeException("OperationUnsupported", "Unified does not expose the Classic master-copy composition API.");
            var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)selected.Software;
            var target = (JObject)payload["target"];
            string action = HmiAction(payload);
            if (action == "tag_create")
            {
                string tagName = HmiText(payload, "name"); HmiRecipeRules.RequireName(tagName, "name");
                if (hmi.Tags.Find(tagName) != null) throw new BridgeException("LibraryConflict", "The requested HMI tag already exists.");
                string dataType = HmiText(payload, "dataType");
                if (!new[] { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal" }.Contains(dataType, StringComparer.Ordinal))
                    throw new BridgeException("TargetIncompatible", "This route creates primitive internal tags only; HMI UDT and external PLC bindings need an explicit qualified mapping.");
                if (payload["connection"] != null || payload["plcTag"] != null || payload["address"] != null)
                    throw new BridgeException("IntegrationMappingRequired", "External tag connections are not accepted by the internal-tag route.");
                string table = HmiText(target, "tagTable");
                if (hmi.TagTables.Find(table) == null) throw new BridgeException("HmiTargetNotFound", "The exact Unified tag table does not exist.");
                return HmiPreview(selected, action, UnifiedHmiInventory(selected, 200), new JObject { ["name"] = tagName, ["dataType"] = dataType, ["tagTable"] = table, ["scope"] = "internal" });
            }
            if (action != "faceplate_create" && action != "faceplate_bind") throw new BridgeException("OperationUnsupported", "Supported Unified actions: faceplate_create, faceplate_bind, tag_create.");
            if (version == null || version.State != LibraryTypeVersionState.Committed) throw new BridgeException("LibraryConflict", "Select one exact committed project-library faceplate version.");
            string nativeVersionClass = version.GetType().FullName;
            if (nativeVersionClass.StartsWith("Siemens.Engineering.SW.", StringComparison.Ordinal)
                || nativeVersionClass.StartsWith("Siemens.Engineering.Hmi.", StringComparison.Ordinal))
                throw new BridgeException("TargetIncompatible", "A PLC or Classic HMI library type cannot be used as a Unified faceplate.");
            if (!string.IsNullOrEmpty(version.TypeObject.Namespace) || version.TypeObject.Parent is LibraryTypeUserFolder)
                throw new BridgeException("VersionSelectionUnsupported", "Qualified Unified version references currently require an unnamespaced root project-library type; a folder/namespace reference is not guessed.");
            var localType = project.ProjectLibrary.FindType(version.TypeObject.Guid);
            var local = localType?.Versions.FirstOrDefault(v => v.Guid == version.Guid);
            if (local == null) throw new BridgeException("DependencyUnresolved", "Copy the exact selected type and dependencies to the project library before preparing Unified placement.");
            string screenName = HmiText(target, "screen"); string name = HmiText(target, "container");
            HmiRecipeRules.RequireName(screenName, "screen"); HmiRecipeRules.RequireName(name, "container");
            var screen = hmi.Screens.Find(screenName) ?? throw new BridgeException("HmiTargetNotFound", "The exact Unified screen does not exist.");
            var existing = screen.ScreenItems.Find(name);
            var container = existing as Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer;
            var bindings = payload["bindings"] as JArray ?? new JArray();
            if (bindings.Count > 200) throw new BridgeException("InvalidRequest", "At most 200 explicit interface bindings are accepted.");
            if (action == "faceplate_create")
            {
                if (existing != null) throw new BridgeException("LibraryConflict", "A screen item with the requested container name already exists.");
                var placement = payload["placement"] as JObject ?? throw new BridgeException("IntegrationMappingRequired", "An explicit screen rectangle is required.");
                HmiRecipeRules.RequirePlacement(HmiInteger(placement, "left"), HmiInteger(placement, "top"), HmiInteger(placement, "width"), HmiInteger(placement, "height"), screen.Width, screen.Height);
                // The public API cannot inspect a new container interface without creating it. Do not invent its schema.
                if (bindings.Count != 0) throw new BridgeException("IntegrationMappingRequired", "Create the container as instantiateOnly, inspect its native interface, then prepare faceplate_bind. No unverified interface mapping is applied.");
                if ((payload["integration"] as JObject)?.Value<string>("mode") != "instantiateOnly")
                    throw new BridgeException("IntegrationMappingRequired", "First placement requires integration.mode=instantiateOnly; full integration requires a subsequent native-interface binding preview.");
            }
            else
            {
                if (container == null) throw new BridgeException("HmiTargetNotFound", "The exact screen item is not a faceplate container.");
                var before = UnifiedFaceplateView(container, version);
                var available = container.Interface.Select(p => p.PropertyName).ToList();
                HmiRecipeRules.RequireBindings(bindings.Select(b => HmiText((JObject)b, "property")), available, available);
                var observedTags = new JArray();
                foreach (JObject binding in bindings)
                {
                    string tagName = HmiText(binding, "tag"); var tag = hmi.Tags.Find(tagName);
                    if (tag == null) throw new BridgeException("DependencyUnresolved", "The explicit HMI tag does not exist: " + tagName);
                    HmiRecipeRules.RequireTypeCompatibility(HmiText(binding, "dataType"), tag.DataType);
                    var nativeProperty = (IEngineeringObject)container.Interface.Find(binding.Value<string>("property"));
                    var typeAttribute = nativeProperty.GetAttributeInfos().FirstOrDefault(a => a.Name == "DataType" && (a.AccessMode & EngineeringAttributeAccessMode.Read) != 0);
                    if (typeAttribute == null) throw new BridgeException("IntegrationTypeUnavailable", "The native faceplate interface does not expose its data type. A qualified type-document interface adapter is required before automatic binding.");
                    HmiRecipeRules.RequireTypeCompatibility(Convert.ToString(nativeProperty.GetAttribute("DataType"), System.Globalization.CultureInfo.InvariantCulture), tag.DataType);
                    // A nonempty connection must already be a native configured connection, never a guessed matching name.
                    if (!string.IsNullOrEmpty(tag.Connection) && hmi.Connections.Find(tag.Connection) == null)
                        throw new BridgeException("DependencyUnresolved", "The HMI tag references a missing native connection: " + tag.Connection);
                    observedTags.Add(new JObject { ["name"] = tag.Name, ["dataType"] = tag.DataType, ["hmiDataType"] = tag.HmiDataType,
                        ["connection"] = tag.Connection, ["plcTag"] = tag.PlcTag });
                }
                before["observedTags"] = observedTags;
                return HmiPreview(selected, action, before, new JObject { ["screen"] = screenName, ["container"] = name,
                    ["versionGuid"] = version.Guid.ToString("D"), ["bindings"] = bindings.DeepClone(), ["bindingTypeCoverage"] = "Native interface and HMI tag data types matched; PLC communication requires runtime qualification." });
            }
            return HmiPreview(selected, action, container != null ? UnifiedFaceplateView(container) : UnifiedHmiInventory(selected, 200),
                new JObject { ["screen"] = screenName, ["container"] = name, ["versionGuid"] = version.Guid.ToString("D"),
                    ["containedType"] = HmiRecipeRules.ExactContainedType(version.TypeObject.Name, version.VersionNumber),
                    ["bindings"] = bindings.DeepClone(), ["placement"] = payload["placement"]?.DeepClone(),
                    ["bindingTypeCoverage"] = "Existing HMI tag type checked; native interface compatibility and PLC communication are additionally validated by compile/runtime." });
        }

        private static long HmiInteger(JObject value, string name)
        {
            if (value[name]?.Type != JTokenType.Integer) throw new BridgeException("InvalidRequest", name + " must be an integer.");
            return value.Value<long>(name);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject ApplyUnifiedHmi(Project project, HmiTargetSelection selected, JObject payload, LibraryTypeVersion version)
        {
            var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)selected.Software;
            var target = (JObject)payload["target"]; string action = HmiAction(payload);
            if (action == "tag_create")
            {
                var tags = hmi.TagTables.Find(target.Value<string>("tagTable")).Tags;
                return HmiInternalTagCreation.Create(payload.Value<string>("name"), payload.Value<string>("dataType"),
                    name => tags.Create(name), (tag, dataType) => tag.DataType = dataType,
                    tag => tag.Name, tag => tag.DataType, tag => tag.Connection, tag => tag.PlcTag,
                    connection => hmi.Connections.Find(connection) != null);
            }
            var screen = hmi.Screens.Find(target.Value<string>("screen"));
            var container = action == "faceplate_create" ? screen.ScreenItems.Create<Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer>(target.Value<string>("container"))
                : (Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer)screen.ScreenItems.Find(target.Value<string>("container"));
            if (action == "faceplate_create")
            {
                container.ContainedType = HmiRecipeRules.ExactContainedType(version.TypeObject.Name, version.VersionNumber);
                var placement = (JObject)payload["placement"];
                container.Left = placement.Value<int>("left"); container.Top = placement.Value<int>("top");
                container.Width = placement.Value<uint>("width"); container.Height = placement.Value<uint>("height");
                if (container.Left != placement.Value<int>("left") || container.Top != placement.Value<int>("top")
                    || container.Width != placement.Value<uint>("width") || container.Height != placement.Value<uint>("height"))
                    throw new BridgeException("PreservationMismatch", "Native faceplate sizing normalized the requested rectangle; visual verification and a new preview are required.");
            }
            else foreach (JObject binding in (JArray)payload["bindings"])
            {
                var property = container.Interface.Find(binding.Value<string>("property"));
                property.Value = binding.Value<string>("tag");
                if (!Equals(property.Value, binding.Value<string>("tag"))) throw new BridgeException("PreservationMismatch", "Native interface readback does not match the requested tag binding.");
            }
            return UnifiedFaceplateView(container, version);
        }
    }
}
