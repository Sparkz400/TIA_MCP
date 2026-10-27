using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Library.MasterCopies;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // All entry points are called by the existing change lifecycle. Optional
        // Siemens HMI types occur only inside family methods, never session fields.
        private JObject PrepareHmiEngineeringMutation(EngineeringChange change, Project project)
        {
            try { HmiEngineeringRules.RequireRequest(change.Payload); }
            catch (ArgumentException ex) { throw new BridgeException("InvalidRequest", ex.Message); }
            var identity = (JObject)change.Payload["target"];
            HmiValidateIdentity(project, identity);
            var discovered = HmiResolveNativeTarget(project, identity);
            var entry = HmiEngineeringObject(project, identity);
            var target = ResolveHmiTarget(project, identity);
            string operation = change.Payload.Value<string>("operation");
            var gate = HmiEngineeringRules.Gate(target.Family, discovered.Profile, operation);
            gate["catalogIdentifier"] = discovered.CatalogIdentifier;
            gate["runtimeVersion"] = discovered.RuntimeVersion;
            gate["nativeAssembly"] = target.Software.GetType().Assembly.FullName;
            change.HmiEvidence = new JObject { ["schemaVersion"] = 1, ["target"] = identity.DeepClone(), ["operation"] = operation,
                ["gate"] = gate, ["runtimeVerified"] = false, ["automaticRetryAllowed"] = false };
            // Capture real native artifacts before rejecting a missing qualification.
            // An incomplete export is useful evidence, never a usable mutation hash.
            change.HmiBeforeManifest = CaptureHmiEngineeringScope(change, project, "before");
            change.HmiEvidence["baseline"] = change.HmiBeforeManifest.DeepClone();
            var native = entry.Native as IEngineeringObject;
            if (native == null) throw new BridgeException("HmiMutationTargetUnsupported", "This HMI object is parsed export content without a qualified native mutation adapter.", change.HmiEvidence);
            var effect = target.Family == "classic" ? PrepareClassicEngineeringBody(change, target.Software, native) : PrepareUnifiedEngineeringBody(change, project, native);
            change.HmiEvidence["proposedEffect"] = effect;
            RequireHmiEngineeringGate(gate, change.HmiEvidence);
            RequireHmiCompleteScope(change.HmiBeforeManifest);
            if (change.HmiBeforeManifest.Value<string>("contentHash") != change.Payload.Value<string>("expectedContentHash"))
                throw new BridgeException("StaleRevision", "HMI native content changed since inspection; prepare from a fresh complete content capture.", change.HmiEvidence);
            return new JObject { ["effects"] = new JArray(effect), ["warnings"] = new JArray("Native compile does not qualify HMI runtime behavior."),
                ["hmiEvidence"] = change.HmiEvidence.DeepClone(), ["compileRequired"] = true, ["saveRequired"] = true,
                ["preservationPolicy"] = "hmi-family-specific-exact-v1; no PLC normalization; Classic export-time DocumentInfo.Created only" };
        }

        private static void RequireHmiEngineeringGate(JObject gate, JObject evidence)
        {
            if (!gate.Value<bool>("available")) throw new BridgeException(gate.Value<string>("reasonCode"), gate.Value<string>("reason"), evidence);
        }

        private JObject CaptureHmiEngineeringScope(EngineeringChange change, Project project, string stage)
        {
            var target = ResolveHmiTarget(project, (JObject)change.Payload["target"]);
            string directory = Path.Combine(change.Directory, "hmi-" + stage + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var artifacts = new JObject();
            var missing = new JArray();
            var capturedTargets = new JArray();
            var typed = new JObject();
            JObject nativeFailure = null;
            try
            {
                var targets = HmiTargets(project).ToArray();
                if (targets.Length > 32) throw new BridgeException("TraversalLimit", "Project-wide HMI preservation capture exceeds 32 native HMI targets.");
                foreach (var current in targets)
                {
                    capturedTargets.Add(HmiTargetView(current));
                    string targetDirectory = Path.Combine(directory, "target-" + capturedTargets.Count);
                    Directory.CreateDirectory(targetDirectory);
                    if (current.Family == "classic") CaptureClassicEngineeringScope(current, targetDirectory, artifacts, missing);
                    else CaptureUnifiedEngineeringScope(current, targetDirectory, artifacts, missing, typed);
                }
                // Typed node hashes join after all exports so they never count against the 200-file export bound.
                foreach (var node in typed.Properties()) artifacts.Add(node.Name, node.Value);
            }
            catch (Exception ex)
            {
                nativeFailure = new JObject { ["type"] = ex.GetType().FullName, ["code"] = (ex as BridgeException)?.Code,
                    ["message"] = ex.Message, ["stage"] = stage, ["completedArtifactCount"] = artifacts.Count };
                missing.Add("Native preservation capture failed; already-produced artifacts are retained and cannot authorize mutation.");
            }
            var manifest = new JObject { ["schemaVersion"] = 1, ["policyVersion"] = HmiPreservationRules.PolicyVersion,
                ["family"] = target.Family, ["hmiPath"] = target.Path, ["projectPath"] = project.Path.FullName,
                ["targets"] = capturedTargets, ["scope"] = "all discovered project HMI targets; complete public export surface per family plus Unified typed object-model readback per node (target-N/typed-model.json), subject to explicit missing coverage",
                ["complete"] = missing.Count == 0, ["artifacts"] = artifacts, ["missingCoverage"] = missing,
                ["contentHash"] = null, ["nativeFailure"] = nativeFailure, ["status"] = nativeFailure != null ? "failed" : missing.Count == 0 ? "complete" : "incomplete",
                ["capturedAtUtc"] = DateTime.UtcNow.ToString("O") };
            // Partial artifact-set hashes are explicitly labelled and cannot serve as
            // expectedContentHash or replace the existing PLC/project fingerprint.
            var hashes = artifacts.Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>("contentSha256") ?? p.Value.Value<string>("sha256"), StringComparer.Ordinal);
            manifest["observedArtifactsHash"] = hashes.Count == 0 ? null : HmiPreservationRules.ScopeHash(target.Family, hashes, true);
            if (missing.Count == 0) manifest["contentHash"] = manifest["observedArtifactsHash"].DeepClone();
            string path = Path.Combine(directory, "manifest.json");
            File.WriteAllText(path, manifest.ToString(Formatting.Indented));
            change.Readbacks.Add(new JObject { ["role"] = "hmi-" + stage + "-preservation-manifest", ["artifact"] = ChangeArtifact(path) });
            return manifest;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CaptureClassicEngineeringScope(HmiTargetSelection target, string directory, JObject artifacts, JArray missing)
        {
            var hmi = (Siemens.Engineering.Hmi.HmiTarget)target.Software;
            foreach (var screen in ClassicScreens(hmi.ScreenFolder.Screens, hmi.ScreenFolder.Folders, "/screens", 0))
                CaptureHmiArtifact(directory, target.Path + screen.Item1, artifacts, f => screen.Item2.Export(f, ExportOptions.WithDefaults));
            foreach (var table in ClassicTagTables(hmi.TagFolder.TagTables, hmi.TagFolder.Folders, "/tagTables", 0))
                CaptureHmiArtifact(directory, target.Path + table.Item1, artifacts, f => table.Item2.Export(f, ExportOptions.WithDefaults));
            foreach (var connection in hmi.Connections)
                CaptureHmiArtifact(directory, target.Path + "/connections/" + Segment(connection.Name), artifacts, f => connection.Export(f, ExportOptions.WithDefaults));
            CaptureClassicEngineeringFolder(hmi.ScreenTemplateFolder, "ScreenTemplates", target.Path + "/screenTemplates", directory, artifacts, 0);
            CaptureClassicEngineeringFolder(hmi.ScreenPopupFolder, "ScreenPopups", target.Path + "/screenPopups", directory, artifacts, 0);
            // Slide-ins have no Name or Folders; their SlideinType is the inventory path segment.
            if (hmi.ScreenSlideinFolder != null)
                foreach (var slidein in hmi.ScreenSlideinFolder.ScreenSlideins)
                    CaptureHmiArtifact(directory, target.Path + "/screenSlideins/" + Segment(slidein.SlideinType.ToString()), artifacts, f => slidein.Export(f, ExportOptions.WithDefaults));
            CaptureClassicEngineeringFolder(hmi.VBScriptFolder, "VBScripts", target.Path + "/scripts", directory, artifacts, 0);
            foreach (var cycle in hmi.Cycles)
                CaptureHmiArtifact(directory, target.Path + "/cycles/" + Segment(cycle.Name), artifacts, f => cycle.Export(f, ExportOptions.WithDefaults));
            foreach (var list in hmi.GraphicLists)
                CaptureHmiArtifact(directory, target.Path + "/graphicLists/" + Segment(list.Name), artifacts, f => list.Export(f, ExportOptions.WithDefaults));
            foreach (var list in hmi.TextLists)
                CaptureHmiArtifact(directory, target.Path + "/textLists/" + Segment(list.Name), artifacts, f => list.Export(f, ExportOptions.WithDefaults));
            if (hmi.ScreenGlobalElements != null)
                CaptureHmiArtifact(directory, target.Path + "/screenGlobalElements", artifacts, f => hmi.ScreenGlobalElements.Export(f, ExportOptions.WithDefaults));
            if (hmi.ScreenOverview != null)
                CaptureHmiArtifact(directory, target.Path + "/screenOverview", artifacts, f => hmi.ScreenOverview.Export(f, ExportOptions.WithDefaults));
            missing.Add("All exposed Classic screen/template/popup/slide-in/global/overview, tag-table, connection, cycle, script and text/graphic-list exports are captured. The installed HmiTarget has no public alarm composition; embedded/resource dependency closure and export stability still need exact-profile native qualification.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CaptureClassicEngineeringFolder(IEngineeringObject folder, string composition, string prefix, string directory, JObject artifacts, int depth)
        {
            if (folder == null) return;
            if (depth > 32) throw new BridgeException("TraversalLimit", "Classic preservation folder nesting exceeds 32.");
            foreach (IEngineeringObject native in (IEnumerable)folder.GetComposition(composition))
            {
                string name = native.GetAttribute("Name") as string;
                if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("HmiIdentityUnavailable", "A native preservation object has no exact name.");
                CaptureHmiArtifact(directory, prefix + "/" + Segment(name), artifacts, file =>
                {
                    if (native is Siemens.Engineering.Hmi.Screen.ScreenTemplate template) template.Export(file, ExportOptions.WithDefaults);
                    else if (native is Siemens.Engineering.Hmi.Screen.ScreenPopup popup) popup.Export(file, ExportOptions.WithDefaults);
                    else if (native is Siemens.Engineering.Hmi.RuntimeScripting.VBScript script) script.Export(file, ExportOptions.WithDefaults);
                    else throw new BridgeException("HmiExportUnsupported", "The exact Classic folder member lacks an allowlisted public export route: " + native.GetType().FullName);
                });
            }
            foreach (IEngineeringObject child in (IEnumerable)folder.GetComposition("Folders"))
            {
                string name = child.GetAttribute("Name") as string;
                if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("HmiIdentityUnavailable", "A native preservation folder has no exact name.");
                CaptureClassicEngineeringFolder(child, composition, prefix + "/folders/" + Segment(name), directory, artifacts, depth + 1);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CaptureUnifiedEngineeringScope(HmiTargetSelection target, string directory, JObject artifacts, JArray missing, JObject typed)
        {
            var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)target.Software;
            int count = 0;
            foreach (var table in hmi.TagTables)
            {
                if (++count > 200) throw new BridgeException("TraversalLimit", "HMI preservation export exceeds 200 native tag tables; narrow the profile before mutation.");
                string exported = Path.Combine(directory, "table-" + count);
                Directory.CreateDirectory(exported);
                table.Tags.Export(new DirectoryInfo(exported));
                foreach (string path in Directory.GetFiles(exported, "*", SearchOption.AllDirectories))
                    AddHmiArtifact(target.Path + "/tagTables/" + Segment(table.Name) + "/export/" + path.Substring(exported.Length + 1).Replace('\\', '/'), path, artifacts);
            }
            missing.Add("Unified tag tables were exported using their documented native composition export. Export stability, full dependency closure and unrelated-resource preservation remain unqualified.");
            // No Screen.Export exists in V21: compare typed readback of every exposed node instead, keyed by its hmi_inspect path.
            var walk = new HmiModelWalk(new ScanBudget(Math.Max(0, 45000 - typed.Count)));
            walk.Capture(hmi, target.Path, HmiModelRoots);
            if (walk.Budget.Truncated) throw new BridgeException("TraversalLimit", "Unified typed preservation readback exceeds 45000 model nodes or 32 composition levels; no truncated scope authorizes mutation.");
            string model = Path.Combine(directory, "typed-model.json");
            File.WriteAllText(model, new JArray(walk.Nodes.Select(n => n.Row)).ToString(Formatting.None));
            if (new FileInfo(model).Length > 64L * 1024 * 1024) throw new BridgeException("TraversalLimit", "Unified typed preservation readback exceeds 64 MiB; no truncated scope authorizes mutation.");
            foreach (var node in walk.Nodes)
            {
                string path = node.Row.Value<string>("path");
                if (typed[path] != null || artifacts[path] != null) throw new BridgeException("AmbiguousTarget", "Duplicate exact HMI typed-readback identity: " + path);
                typed[path] = new JObject { ["sha256"] = HmiPreservationRules.Hash(node.Row.ToString(Formatting.None)), ["source"] = "typedReadback" };
            }
            if (walk.ErrorCount > 0) missing.Add(walk.ErrorCount + " Unified typed attribute read(s) failed; their readError markers are compared, their values are not. See typed-model.json.");
            missing.Add("Unified typed readback compares the public V21 object model (screens, items, dynamizations, event handlers with hashed script code, parts, translations, tags, alarms, logs, audit, runtime settings, groups, lists, script modules) per node. V21 has no Screen.Export, so payloads without a public getter remain uncompared; this readback is not natively qualified.");
        }

        private static void CaptureHmiArtifact(string directory, string identity, JObject artifacts, Action<FileInfo> exporter)
        {
            if (artifacts.Count >= 200) throw new BridgeException("TraversalLimit", "HMI preservation exceeds its 200-artifact native export bound.");
            string path = Path.Combine(directory, "native-" + artifacts.Count + ".xml");
            exporter(new FileInfo(path));
            AddHmiArtifact(identity, path, artifacts);
        }

        private static void AddHmiArtifact(string identity, string path, JObject artifacts)
        {
            if (artifacts.Property(identity, StringComparison.Ordinal) != null) throw new BridgeException("AmbiguousTarget", "Duplicate exact HMI export identity: " + identity);
            if (!File.Exists(path)) throw new BridgeException("HmiExportUnavailable", "The native export did not create its expected artifact.");
            long bytes = new FileInfo(path).Length;
            if (artifacts.Count >= 200 || bytes > 32 * 1024 * 1024 || artifacts.Properties().Sum(p => p.Value.Value<long>("bytes")) + bytes > 32 * 1024 * 1024)
                throw new BridgeException("TraversalLimit", "HMI preservation exceeds 200 artifacts or 32 MiB; no truncated scope authorizes mutation.");
            var artifact = new JObject { ["path"] = path, ["bytes"] = bytes, ["sha256"] = ChangeFileHash(path) };
            if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var xml = HmiPreservationRules.ReadXml(File.ReadAllText(path));
                if (xml.Root?.Name == System.Xml.Linq.XName.Get("Document") && xml.Descendants().Any(e => e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)))
                {
                    artifact["contentSha256"] = HmiPreservationRules.ClassicContentHash(File.ReadAllText(path));
                    artifact["normalization"] = "Classic-only /Document/DocumentInfo/Created export timestamp; no content fields removed";
                }
            }
            artifacts.Add(identity, artifact);
        }

        private static void RequireHmiCompleteScope(JObject manifest)
        {
            if (manifest == null || !manifest.Value<bool>("complete") || string.IsNullOrWhiteSpace(manifest.Value<string>("contentHash")))
                throw new BridgeException("HmiPreservationUnavailable", "Complete family-specific native preservation coverage is required; partial exports and metadata cannot authorize an HMI change.", manifest);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject PrepareClassicEngineeringBody(EngineeringChange change, Siemens.Engineering.HW.Software software, IEngineeringObject native)
        {
            if (change.Payload.Value<string>("operation") == "classic_xml_import") return PrepareClassicXmlImport(change, software, native);
            if (change.Payload.Value<string>("operation") != "scalar_property_set" || !(native is Siemens.Engineering.Hmi.Screen.Screen screen))
                throw new BridgeException("HmiOperationUnsupported", "Classic only has the export-preserving screen BackColor scalar candidate and whole-object classic_xml_import; typed item/tag creation/placement/binding adapters are unavailable.", change.HmiEvidence);
            HmiLibraryLink.RequireNoLink(screen);
            string before = Path.Combine(change.Directory, "hmi-classic-before.xml");
            screen.Export(new FileInfo(before), ExportOptions.WithDefaults);
            var document = HmiPreservationRules.ReadXml(File.ReadAllText(before));
            var owners = document.Descendants("Hmi.Screen.Screen").Where(e => e.Element("AttributeList")?.Element("Name")?.Value == screen.Name).ToArray();
            if (owners.Length != 1) throw new BridgeException("HmiExportUnsupported", "Native screen export cannot establish a unique exported root; no guessed XML mutation is allowed.");
            string value = ((JObject)change.Payload["properties"]).Value<string>("BackColor");
            string observed = owners[0].Element("AttributeList")?.Element("BackColor")?.Value;
            var expected = HmiPreservationRules.PatchClassicScalar(document, "Hmi.Screen.Screen", (string)owners[0].Attribute("ID"), "BackColor", observed, value);
            change.InputPath = Path.Combine(change.Directory, "hmi-classic-expected.xml");
            File.WriteAllText(change.InputPath, expected.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
            change.InputHash = ChangeFileHash(change.InputPath);
            change.Backups.Add(new JObject { ["role"] = "hmi-classic-before", ["artifact"] = ChangeArtifact(before) });
            return new JObject { ["objectPath"] = change.Payload["target"]["objectPath"].DeepClone(), ["property"] = "BackColor", ["before"] = observed, ["after"] = value };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject PrepareUnifiedEngineeringBody(EngineeringChange change, Project project, IEngineeringObject native)
        {
            var payload = change.Payload;
            string operation = payload.Value<string>("operation");
            var result = new JObject { ["operation"] = operation, ["objectPath"] = payload["target"]["objectPath"].DeepClone() };
            if (operation == "tag_create")
            {
                var table = native as Siemens.Engineering.HmiUnified.HmiTags.HmiTagTable ?? throw new BridgeException("HmiTargetMismatch", "tag_create requires an exact native Unified tag-table identity.");
                var target = ResolveHmiTarget(project, (JObject)payload["target"]);
                var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)target.Software;
                if (hmi.Tags.Find(payload.Value<string>("name")) != null) throw new BridgeException("HmiNameConflict", "The requested tag already exists in the HMI namespace.");
                result["name"] = payload["name"].DeepClone(); result["dataType"] = payload["dataType"].DeepClone(); result["scope"] = "internal";
                return result;
            }
            if (operation == "external_tag_create")
            {
                if (!(native is Siemens.Engineering.HmiUnified.HmiTags.HmiTagTable)) throw new BridgeException("HmiTargetMismatch", "external_tag_create requires an exact native Unified tag-table identity.");
                var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)ResolveHmiTarget(project, (JObject)payload["target"]).Software;
                if (hmi.Tags.Find(payload.Value<string>("name")) != null) throw new BridgeException("HmiNameConflict", "The requested tag already exists in the HMI namespace.");
                var connection = HmiResolveNativeObject(project, (JObject)payload["connection"]).Native as Siemens.Engineering.HmiUnified.HmiConnections.HmiConnection
                    ?? throw new BridgeException("DependencyUnresolved", "The connection identity is not an exact native Unified HMI connection.");
                var ownership = new JObject { ["connection"] = connection.Name };
                var plc = HmiProveIntegratedOwner(project, HmiResolveNativeTarget(project, (JObject)payload["target"]), connection.Name, ownership);
                var identity = (JObject)payload["plcTag"];
                // A global DB member uses the natively observed dotted spelling (DB.Member) and its exported declaration type.
                var declaration = identity.Value<string>("kind") == "block" ? HmiDbMemberByIdentity(project, plc, identity, change.Directory) : null;
                var rootTag = declaration == null ? HmiRootPlcTag(project, plc, identity) : null;
                string spelling = declaration?.Value<string>("plcTag") ?? rootTag.Name, plcDataType = declaration?.Value<string>("dataType") ?? rootTag.DataTypeName;
                if (!HmiScalarTypesAgree(payload.Value<string>("dataType"), plcDataType))
                    throw new BridgeException("TargetIncompatible", "The requested HMI data type does not exactly match the PLC declaration type; implicit conversions are not inferred.",
                        new JObject { ["requested"] = payload["dataType"].DeepClone(), ["plcDataType"] = plcDataType });
                result["name"] = payload["name"].DeepClone(); result["dataType"] = payload["dataType"].DeepClone(); result["scope"] = "external";
                result["connection"] = connection.Name; result["plcTag"] = spelling; result["plcName"] = plc.Software.Name; result["plcPath"] = plc.Path;
                result["plcDataType"] = plcDataType; result["plcDeclaration"] = declaration; result["accessMode"] = "SymbolicAccess"; result["connectionOwnership"] = ownership;
                return result;
            }
            if (operation == "connection_create")
            {
                var hmi = native as Siemens.Engineering.HmiUnified.HmiSoftware ?? throw new BridgeException("HmiTargetMismatch", "connection_create requires the exact native Unified HMI software identity (kind hmiDevice).");
                if (hmi.Connections.Find(payload.Value<string>("name")) != null) throw new BridgeException("HmiNameConflict", "The requested connection already exists in this HMI.");
                // Partner/Station/Node are read-only in V21; the partner comes only from the native endpoint pair of this name.
                var ownership = new JObject();
                var plc = HmiProveIntegratedOwner(project, HmiResolveNativeTarget(project, (JObject)payload["target"]), payload.Value<string>("name"), ownership);
                result["name"] = payload["name"].DeepClone(); result["createdPath"] = HmiCreatedPath(payload);
                result["plcName"] = plc.Software.Name; result["plcPath"] = plc.Path; result["connectionOwnership"] = ownership;
                return result;
            }
            if (operation == "object_create")
            {
                var hmi = native as Siemens.Engineering.HmiUnified.HmiSoftware ?? throw new BridgeException("HmiTargetMismatch", "object_create requires the exact native Unified HMI software identity (kind hmiDevice).");
                string composition = payload.Value<string>("composition");
                if (UnifiedCompositionObject(hmi, composition, payload.Value<string>("name"), false) != null) throw new BridgeException("HmiNameConflict", "The requested object already exists in " + composition + ".");
                var type = composition == "DiscreteAlarms" ? typeof(Siemens.Engineering.HmiUnified.HmiAlarm.HmiDiscreteAlarm) : composition == "AnalogAlarms" ? typeof(Siemens.Engineering.HmiUnified.HmiAlarm.HmiAnalogAlarm)
                    : composition == "DataLogs" ? typeof(Siemens.Engineering.HmiUnified.HmiLogging.HmiDataLog) : typeof(Siemens.Engineering.HmiUnified.HmiLogging.HmiAlarmLog);
                HmiTypedValues(type, null, payload["properties"] as JObject);
                HmiRequireReferences(hmi, payload["properties"] as JObject);
                result["composition"] = composition; result["name"] = payload["name"].DeepClone(); result["createdPath"] = HmiCreatedPath(payload);
                result["properties"] = payload["properties"]?.DeepClone();
                return result;
            }
            if (operation == "object_properties_set")
            {
                string family = UnifiedObjectFamily(native) ?? throw new BridgeException("HmiTargetMismatch", "object_properties_set requires an exact native discrete/analog alarm, data/alarm log or the runtime settings.");
                var properties = (JObject)payload["properties"];
                var outside = properties.Properties().FirstOrDefault(p => !HmiEngineeringRules.FamilyProperties[family].Contains(p.Name, StringComparer.Ordinal));
                if (outside != null) throw new BridgeException("InvalidRequest", "Property is outside the " + family + " allowlist: " + outside.Name);
                result["family"] = family; result["before"] = HmiTypedValues(native.GetType(), native, properties); result["after"] = properties.DeepClone();
                HmiRequireReferences((Siemens.Engineering.HmiUnified.HmiSoftware)ResolveHmiTarget(project, (JObject)payload["target"]).Software, properties);
                return result;
            }
            if (operation == "screen_create")
            {
                var hmi = native as Siemens.Engineering.HmiUnified.HmiSoftware ?? throw new BridgeException("HmiTargetMismatch", "screen_create requires the exact native Unified HMI software identity (kind hmiDevice).");
                if (hmi.Screens.Find(payload.Value<string>("name")) != null) throw new BridgeException("HmiNameConflict", "The requested screen already exists in this HMI.");
                var size = (JObject)payload["properties"];
                HmiTypedValues(typeof(Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen), null, size);
                // No device-resolution table is guessed: the size is copied from an existing top-level screen.
                if (!hmi.Screens.Any(s => s.Width == size.Value<long>("Width") && s.Height == size.Value<long>("Height")))
                    throw new BridgeException("DependencyUnresolved", "Width and Height must equal the size of an existing top-level screen of this HMI.", size);
                result["name"] = payload["name"].DeepClone(); result["createdPath"] = HmiCreatedPath(payload); result["properties"] = size.DeepClone();
                return result;
            }
            if (operation == "item_create")
            {
                var screen = native as Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen ?? throw new BridgeException("HmiTargetMismatch", "item_create requires an exact native Unified screen identity (kind hmiScreen).");
                if (screen.ScreenItems.Find(payload.Value<string>("name")) != null) throw new BridgeException("HmiNameConflict", "The requested item already exists on this screen.");
                var rectangle = (JObject)payload["placement"];
                HmiRecipeRules.RequirePlacement(rectangle.Value<long>("left"), rectangle.Value<long>("top"), rectangle.Value<long>("width"), rectangle.Value<long>("height"), screen.Width, screen.Height);
                result["itemType"] = payload["itemType"].DeepClone(); result["name"] = payload["name"].DeepClone(); result["placement"] = rectangle.DeepClone();
                result["screenSize"] = new JObject { ["width"] = screen.Width, ["height"] = screen.Height }; result["createdPath"] = HmiCreatedPath(payload);
                return result;
            }
            if (operation == "event_handler_set")
            {
                if (HmiEventHandler(native, payload.Value<string>("eventType"), false) != null)
                    throw new BridgeException("HmiNameConflict", "The object already handles this event; existing handlers and their scripts are never replaced.");
                string script = payload.Value<string>("script");
                result["eventType"] = payload["eventType"].DeepClone(); result["createdPath"] = HmiCreatedPath(payload);
                result["script"] = new JObject { ["sha256"] = HmiPreservationRules.Hash(script), ["length"] = script.Length, ["executed"] = false };
                return result;
            }
            var item = native as Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBase ?? throw new BridgeException("HmiTargetMismatch", "The operation requires an exact native Unified screen-item identity.");
            if (operation == "scalar_property_set" || operation == "item_properties_set")
            {
                var before = new JObject();
                foreach (var property in ((JObject)payload["properties"]).Properties())
                {
                    object value = native.GetAttribute(property.Name);
                    ConvertChangeValue(property.Value, value?.GetType() ?? throw new BridgeException("HmiAttributeUnavailable", "Native scalar attribute has no readable type."));
                    before[property.Name] = ChangeScalar(value);
                }
                result["before"] = before; result["after"] = payload["properties"].DeepClone();
            }
            else if (operation == "dynamization_set")
            {
                var binding = (JObject)payload["bindings"][0];
                string property = binding.Value<string>("property");
                HmiTypedSetter(item.GetType(), property);
                if (item.Dynamizations.Find(property) != null) throw new BridgeException("HmiNameConflict", "The item already has a dynamization of this property; existing dynamizations are never replaced.");
                var tag = HmiResolveNativeObject(project, (JObject)binding["tagIdentity"]).Native as Siemens.Engineering.HmiUnified.HmiTags.HmiTag
                    ?? throw new BridgeException("DependencyUnresolved", "Dynamization target is not an exact native HMI tag.");
                HmiRecipeRules.RequireTypeCompatibility(binding.Value<string>("dataType"), tag.DataType);
                if (!string.IsNullOrEmpty(tag.Connection) || !string.IsNullOrEmpty(tag.PlcTag))
                    result["plcBackedTag"] = HmiProvePlcBackedTag(project, (JObject)payload["target"], tag.DataType, tag.Connection, tag.PlcTag, tag.AccessMode.ToString(), tag.PlcName, change.Directory);
                result["property"] = property; result["tag"] = tag.Name; result["dataType"] = tag.DataType; result["createdPath"] = HmiCreatedPath(payload);
            }
            else
            {
                var container = item as Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer ?? throw new BridgeException("HmiTargetMismatch", "Placement and interface bindings currently require an existing native Unified faceplate container.");
                if (operation == "item_place")
                {
                    var screen = container.Parent as Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen;
                    if (screen == null) throw new BridgeException("HmiDestinationUnsupported", "Nested placement requires its own native container coordinate qualification.");
                    var rectangle = (JObject)payload["placement"];
                    HmiRecipeRules.RequirePlacement(rectangle.Value<long>("left"), rectangle.Value<long>("top"), rectangle.Value<long>("width"), rectangle.Value<long>("height"), screen.Width, screen.Height);
                    result["before"] = new JObject { ["left"] = container.Left, ["top"] = container.Top, ["width"] = container.Width, ["height"] = container.Height };
                    result["after"] = rectangle.DeepClone();
                }
                else
                {
                    var plcBacked = new JObject();
                    foreach (JObject binding in (JArray)payload["bindings"])
                    {
                        var property = container.Interface.Find(binding.Value<string>("property")) ?? throw new BridgeException("DependencyUnresolved", "The exact native faceplate interface property does not exist.");
                        if (operation == "existing_binding_set" && !JToken.DeepEquals(ChangeScalar(property.Value), binding["expectedValue"]))
                            throw new BridgeException("StaleRevision", "The existing native interface binding changed.");
                        var tag = HmiResolveNativeObject(project, (JObject)binding["tagIdentity"]).Native as Siemens.Engineering.HmiUnified.HmiTags.HmiTag
                            ?? throw new BridgeException("DependencyUnresolved", "Binding target is not an exact native HMI tag.");
                        HmiRecipeRules.RequireTypeCompatibility(binding.Value<string>("dataType"), tag.DataType);
                        if (!string.IsNullOrEmpty(tag.Connection) || !string.IsNullOrEmpty(tag.PlcTag))
                            plcBacked[binding.Value<string>("property")] = HmiProvePlcBackedTag(project, (JObject)payload["target"], tag.DataType, tag.Connection, tag.PlcTag, tag.AccessMode.ToString(), tag.PlcName, change.Directory);
                    }
                    result["bindings"] = payload["bindings"].DeepClone();
                    if (plcBacked.Count > 0) result["plcBackedTags"] = plcBacked;
                    result["interfaceTypeCoverage"] = "Native interface type compatibility still requires independent exact-profile qualification; requested dataType only verifies the resolved HMI tag.";
                }
            }
            return result;
        }

        private void ApplyHmiEngineeringMutation(EngineeringChange change, Project project)
        {
            // Recompute the gate on the server at apply; never trust the serialized
            // preview, a client qualification flag, or a persisted availability bit.
            var target = HmiResolveNativeTarget(project, (JObject)change.Payload["target"]);
            RequireHmiEngineeringGate(HmiEngineeringRules.Gate(target.Family, target.Profile, change.Payload.Value<string>("operation")), change.HmiEvidence);
            RequireHmiCompleteScope(change.HmiBeforeManifest);
            var entry = HmiEngineeringObject(project, (JObject)change.Payload["target"]);
            if (target.Family == "classic") ApplyClassicEngineeringBody(change, target.Software, (IEngineeringObject)entry.Native);
            else ApplyUnifiedEngineeringBody(change, project, (IEngineeringObject)entry.Native);
            change.Stages.Add("hmi_precise_readback_verified");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyClassicEngineeringBody(EngineeringChange change, Siemens.Engineering.HW.Software software, IEngineeringObject native)
        {
            if (change.Payload.Value<string>("operation") == "classic_xml_import") { ApplyClassicXmlImport(change, software, native); return; }
            var screen = native as Siemens.Engineering.Hmi.Screen.Screen ?? throw new BridgeException("HmiTargetMismatch", "Classic screen identity changed.");
            string requested = ((JObject)change.Payload["properties"]).Value<string>("BackColor");
            HmiPreservationRules.ClassicColor(requested);
            string name = screen.Name;
            // Classic BackColor is native XML data, not a documented typed Screen
            // property. Use only its public exact parent composition import route.
            // Preparation has rejected library type instances before this point.
            HmiLibraryLink.RequireNoLink(screen);
            if (screen.Parent is Siemens.Engineering.Hmi.Screen.ScreenFolder root)
            {
                root.Screens.Import(new FileInfo(change.InputPath), ImportOptions.Override);
                screen = root.Screens.Find(name);
            }
            else if (screen.Parent is Siemens.Engineering.Hmi.Screen.ScreenUserFolder folder)
            {
                folder.Screens.Import(new FileInfo(change.InputPath), ImportOptions.Override);
                screen = folder.Screens.Find(name);
            }
            else throw new BridgeException("HmiDestinationUnsupported", "The exact native screen parent has no allowlisted preserving import composition.");
            if (screen == null) throw new BridgeException("PreservationMismatch", "Native screen import did not preserve the exact screen identity.");
            string after = Path.Combine(change.Directory, "hmi-classic-after.xml");
            screen.Export(new FileInfo(after), ExportOptions.WithDefaults);
            HmiPreservationRules.RequireClassicDocumentPreservation(HmiPreservationRules.ReadXml(File.ReadAllText(change.InputPath)), HmiPreservationRules.ReadXml(File.ReadAllText(after)));
            change.Readbacks.Add(new JObject { ["role"] = "hmi-native-readback", ["artifact"] = ChangeArtifact(after) });
        }

        // Classic cycles, Unified logs and runtime settings have no inspection kind; hmiCycle and
        // hmiModelNode address them by their preservation-capture (hmi_inspect section=model) path.
        private HmiNativeEntry HmiEngineeringObject(Project project, JObject identity)
        {
            string kind = identity.Value<string>("kind");
            if (kind != "hmiCycle" && kind != "hmiModelNode") return HmiResolveNativeObject(project, identity);
            var target = HmiResolveNativeTarget(project, identity);
            if (target.Family != (kind == "hmiCycle" ? "classic" : "unified"))
                throw new BridgeException("UnsupportedObjectKind", kind == "hmiCycle" ? "hmiCycle identifies a Classic HMI cycle." : "hmiModelNode writes address Unified data/alarm logs and runtime settings.");
            RequireHmiModule(target.Family);
            string path = identity.Value<string>("objectPath");
            return new HmiNativeEntry { Native = kind == "hmiCycle" ? ClassicCycle(target.Software, target.Path, path) : UnifiedModelObject(target.Software, target.Path, path) };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEngineeringObject UnifiedModelObject(Siemens.Engineering.HW.Software software, string hmiPath, string path)
        {
            var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)software;
            if (path == hmiPath + "/runtimeSettings") return hmi.RuntimeSettings;
            var matches = hmi.DataLogs.Where(l => hmiPath + "/dataLogs/" + Segment(l.Name) == path).Cast<IEngineeringObject>()
                .Concat(hmi.AlarmLogs.Where(l => hmiPath + "/alarmLogs/" + Segment(l.Name) == path)).Take(2).ToArray();
            if (matches.Length != 1) throw new BridgeException(matches.Length == 0 ? "ObjectNotFound" : "AmbiguousTarget", "An exact, unique Unified data/alarm log or runtime settings node matching objectPath was not found.");
            return matches[0];
        }

        // The created object's typed-readback (hmi_inspect section=model) path. Dynamizations and
        // event handlers are keyed by their PropertyName and EventType, as in the model walk.
        private static string HmiCreatedPath(JObject payload)
        {
            string operation = payload.Value<string>("operation");
            switch (operation)
            {
                case "item_create": return payload["target"].Value<string>("objectPath") + "/" + HmiModelWalk.CompositionSegment("ScreenItems") + "/" + Segment(payload.Value<string>("name"));
                case "dynamization_set": return payload["target"].Value<string>("objectPath") + "/" + HmiModelWalk.CompositionSegment("Dynamizations") + "/" + Segment(payload["bindings"][0].Value<string>("property"));
                case "event_handler_set": return payload["target"].Value<string>("objectPath") + "/" + HmiModelWalk.CompositionSegment("EventHandlers") + "/" + Segment(payload.Value<string>("eventType"));
            }
            string composition = operation == "connection_create" ? "Connections" : operation == "screen_create" ? "Screens" : payload.Value<string>("composition");
            return payload["target"].Value<string>("hmiPath") + "/" + HmiModelWalk.CompositionSegment(composition) + "/" + Segment(payload.Value<string>("name"));
        }

        // The allowlisted item types (HmiEngineeringRules.ItemTypes) through the documented generic Create<T>(name).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBase UnifiedCreateItem(Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBaseComposition items, string itemType, string name)
        {
            switch (itemType)
            {
                case "HmiRectangle": return items.Create<Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle>(name);
                case "HmiText": return items.Create<Siemens.Engineering.HmiUnified.UI.Shapes.HmiText>(name);
                case "HmiButton": return items.Create<Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton>(name);
                case "HmiIOField": return items.Create<Siemens.Engineering.HmiUnified.UI.Widgets.HmiIOField>(name);
                case "HmiBar": return items.Create<Siemens.Engineering.HmiUnified.UI.Widgets.HmiBar>(name);
                case "HmiGauge": return items.Create<Siemens.Engineering.HmiUnified.UI.Widgets.HmiGauge>(name);
                case "HmiSlider": return items.Create<Siemens.Engineering.HmiUnified.UI.Widgets.HmiSlider>(name);
                case "HmiToggleSwitch": return items.Create<Siemens.Engineering.HmiUnified.UI.Widgets.HmiToggleSwitch>(name);
                default: throw new BridgeException("InvalidRequest", "itemType is outside the allowlist.");
            }
        }

        // Item subclasses redeclare EventHandlers, so the most-derived declaration is used; its documented
        // Create/Find take the object's event-type enum. Custom control containers (string-keyed) are not mapped.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object HmiEventHandler(IEngineeringObject native, string eventType, bool create)
        {
            if (!(native is Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen) && !(native is Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBase))
                throw new BridgeException("HmiTargetMismatch", "event_handler_set requires an exact native Unified screen or screen-item identity.");
            System.Reflection.PropertyInfo property = null;
            for (var type = native.GetType(); property == null && type != null; type = type.BaseType)
                property = type.GetProperty("EventHandlers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            object handlers = property?.GetValue(native, null) ?? throw new BridgeException("HmiTargetMismatch", "The exact native object documents no EventHandlers composition.");
            var method = property.PropertyType.GetMethod(create ? "Create" : "Find");
            var parameters = method?.GetParameters();
            if (parameters?.Length != 1 || !parameters[0].ParameterType.IsEnum)
                throw new BridgeException("HmiTargetMismatch", "Only event-type-keyed handler compositions are mapped; custom control containers are not.");
            var names = Enum.GetNames(parameters[0].ParameterType).Where(n => n != "None").ToArray();
            if (!names.Contains(eventType, StringComparer.Ordinal))
                throw new BridgeException("InvalidRequest", "eventType is not a documented event of this object.", new JObject { ["eventType"] = eventType, ["allowed"] = new JArray(names) });
            return method.Invoke(handlers, new[] { Enum.Parse(parameters[0].ParameterType, eventType, false) });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEngineeringObject UnifiedCompositionObject(Siemens.Engineering.HmiUnified.HmiSoftware hmi, string composition, string name, bool create)
        {
            switch (composition)
            {
                case "DiscreteAlarms": return create ? hmi.DiscreteAlarms.Create(name) : hmi.DiscreteAlarms.Find(name);
                case "AnalogAlarms": return create ? hmi.AnalogAlarms.Create(name) : hmi.AnalogAlarms.Find(name);
                case "DataLogs": return create ? hmi.DataLogs.Create(name) : hmi.DataLogs.Find(name);
                case "AlarmLogs": return create ? hmi.AlarmLogs.Create(name) : hmi.AlarmLogs.Find(name);
                default: throw new BridgeException("InvalidRequest", "The composition has no documented Create(name).");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string UnifiedObjectFamily(IEngineeringObject native)
        {
            switch (native)
            {
                case Siemens.Engineering.HmiUnified.HmiAlarm.HmiDiscreteAlarm _: return "DiscreteAlarms";
                case Siemens.Engineering.HmiUnified.HmiAlarm.HmiAnalogAlarm _: return "AnalogAlarms";
                case Siemens.Engineering.HmiUnified.HmiLogging.HmiDataLog _: return "DataLogs";
                case Siemens.Engineering.HmiUnified.HmiLogging.HmiAlarmLog _: return "AlarmLogs";
                case Siemens.Engineering.HmiUnified.RuntimeSettings.HmiRuntimeSetting _: return "RuntimeSettings";
                default: return null;
            }
        }

        // Name-keyed references must resolve to an existing object of this HMI before any write.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HmiRequireReferences(Siemens.Engineering.HmiUnified.HmiSoftware hmi, JObject properties)
        {
            foreach (var p in properties?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                string value = p.Value.Value<string>();
                if (p.Name == "RaisedStateTag" ? hmi.Tags.Find(value) == null : p.Name == "AlarmClass" ? hmi.AlarmClasses.Find(value) == null : p.Name == "StartScreen" && hmi.Screens.Find(value) == null)
                    throw new BridgeException("DependencyUnresolved", p.Name + " must name an existing object of this HMI; screens inside screen groups are not resolved.", new JObject { ["property"] = p.Name, ["value"] = value });
            }
        }

        // Documented typed setter for Property or Part.Property, resolved on the declared types.
        private static System.Reflection.PropertyInfo HmiTypedSetter(Type type, string name)
        {
            System.Reflection.PropertyInfo property = null;
            foreach (string part in name.Split('.'))
            {
                property = type.GetProperty(part, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?? throw new BridgeException("HmiAttributeUnavailable", "No public typed property " + name + " on " + type.FullName);
                type = property.PropertyType;
            }
            if (!property.CanWrite) throw new BridgeException("HmiAttributeUnavailable", "The typed property is read-only: " + name);
            return property;
        }

        private static object HmiTypedOwner(object native, string name)
        {
            string[] parts = name.Split('.');
            for (int i = 0; i < parts.Length - 1; i++)
                native = native.GetType().GetProperty(parts[i]).GetValue(native, null) ?? throw new BridgeException("HmiAttributeUnavailable", "The native part is unavailable: " + parts[i]);
            return native;
        }

        // Validates every requested value against its typed setter; returns the observed values when native is given.
        private static JObject HmiTypedValues(Type type, object native, JObject properties)
        {
            var observed = new JObject();
            foreach (var p in properties?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                var setter = HmiTypedSetter(type, p.Name);
                ConvertChangeValue(p.Value, setter.PropertyType);
                if (native != null) observed[p.Name] = ChangeScalar(setter.GetValue(HmiTypedOwner(native, p.Name), null));
            }
            return observed;
        }

        private static JObject HmiSetTypedValues(object native, JObject properties)
        {
            var readback = new JObject();
            foreach (var p in properties?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                var setter = HmiTypedSetter(native.GetType(), p.Name);
                object owner = HmiTypedOwner(native, p.Name);
                setter.SetValue(owner, ConvertChangeValue(p.Value, setter.PropertyType), null);
                readback[p.Name] = ChangeScalar(setter.GetValue(owner, null));
                if (!JToken.DeepEquals(readback[p.Name], p.Value)) throw new BridgeException("PreservationMismatch", "Unified typed readback differs: " + p.Name, readback);
            }
            return readback;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEngineeringObject ClassicCycle(Siemens.Engineering.HW.Software software, string hmiPath, string path)
        {
            var matches = ((Siemens.Engineering.Hmi.HmiTarget)software).Cycles.Where(c => hmiPath + "/cycles/" + Segment(c.Name) == path).Take(2).ToArray();
            if (matches.Length != 1) throw new BridgeException(matches.Length == 0 ? "ObjectNotFound" : "AmbiguousTarget", "An exact, unique Classic cycle matching objectPath was not found.");
            return matches[0];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Action<FileInfo> ClassicObjectExport(IEngineeringObject native)
        {
            switch (native)
            {
                case Siemens.Engineering.Hmi.Tag.TagTable x: return f => x.Export(f, ExportOptions.WithDefaults);
                case Siemens.Engineering.Hmi.RuntimeScripting.VBScript x: return f => x.Export(f, ExportOptions.WithDefaults);
                case Siemens.Engineering.Hmi.TextGraphicList.TextList x: return f => x.Export(f, ExportOptions.WithDefaults);
                case Siemens.Engineering.Hmi.TextGraphicList.GraphicList x: return f => x.Export(f, ExportOptions.WithDefaults);
                case Siemens.Engineering.Hmi.Cycle.Cycle x: return f => x.Export(f, ExportOptions.WithDefaults);
                case Siemens.Engineering.Hmi.Communication.Connection x: return f => x.Export(f, ExportOptions.WithDefaults);
                default: return HmiClassicScreenExport(native) ?? throw new BridgeException("HmiExportUnsupported", "The exact Classic object has no allowlisted public export: " + native.GetType().FullName);
            }
        }

        // The exact object's owning composition: Import(Override), then re-find the same identity.
        // Owners outside this allowlist fail closed during preparation, before any write.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Func<FileInfo, IEngineeringObject> ClassicXmlImportRoute(Siemens.Engineering.HW.Software software, IEngineeringObject native)
        {
            var hmi = (Siemens.Engineering.Hmi.HmiTarget)software;
            var parent = native.Parent;
            switch (native)
            {
                case Siemens.Engineering.Hmi.Screen.Screen x when parent is Siemens.Engineering.Hmi.Screen.ScreenFolder p:
                { string name = x.Name; return f => { p.Screens.Import(f, ImportOptions.Override); return p.Screens.Find(name); }; }
                case Siemens.Engineering.Hmi.Screen.ScreenTemplate x when parent is Siemens.Engineering.Hmi.Screen.ScreenTemplateFolder p:
                { string name = x.Name; return f => { p.ScreenTemplates.Import(f, ImportOptions.Override); return p.ScreenTemplates.Find(name); }; }
                case Siemens.Engineering.Hmi.Screen.ScreenPopup x when parent is Siemens.Engineering.Hmi.Screen.ScreenPopupFolder p:
                { string name = x.Name; return f => { p.ScreenPopups.Import(f, ImportOptions.Override); return p.ScreenPopups.Find(name); }; }
                case Siemens.Engineering.Hmi.Screen.ScreenSlidein x when parent is Siemens.Engineering.Hmi.Screen.ScreenSlideinSystemFolder p:
                { var type = x.SlideinType; return f => { p.ScreenSlideins.Import(f, ImportOptions.Override); return p.ScreenSlideins.Find(type); }; }
                case Siemens.Engineering.Hmi.Screen.ScreenGlobalElements _:
                    return f => { hmi.ImportScreenGlobalElements(f, ImportOptions.Override); return hmi.ScreenGlobalElements; };
                case Siemens.Engineering.Hmi.Screen.ScreenOverview _:
                    return f => { hmi.ImportScreenOverview(f, ImportOptions.Override); return hmi.ScreenOverview; };
                case Siemens.Engineering.Hmi.Tag.TagTable x when parent is Siemens.Engineering.Hmi.Tag.TagFolder p:
                { string name = x.Name; return f => { p.TagTables.Import(f, ImportOptions.Override); return p.TagTables.Find(name); }; }
                case Siemens.Engineering.Hmi.RuntimeScripting.VBScript x when parent is Siemens.Engineering.Hmi.RuntimeScripting.VBScriptFolder p:
                { string name = x.Name; return f => { p.VBScripts.Import(f, ImportOptions.Override); return p.VBScripts.Find(name); }; }
                case Siemens.Engineering.Hmi.TextGraphicList.TextList x:
                { string name = x.Name; return f => { hmi.TextLists.Import(f, ImportOptions.Override); return hmi.TextLists.Find(name); }; }
                case Siemens.Engineering.Hmi.TextGraphicList.GraphicList x:
                { string name = x.Name; return f => { hmi.GraphicLists.Import(f, ImportOptions.Override); return hmi.GraphicLists.Find(name); }; }
                case Siemens.Engineering.Hmi.Cycle.Cycle x:
                { string name = x.Name; return f => { hmi.Cycles.Import(f, ImportOptions.Override); return hmi.Cycles.Find(name); }; }
                case Siemens.Engineering.Hmi.Communication.Connection x:
                { string name = x.Name; return f => { hmi.Connections.Import(f, ImportOptions.Override); return hmi.Connections.Find(name); }; }
                default: throw new BridgeException("HmiDestinationUnsupported", "The exact Classic object has no allowlisted owning-composition import route: " + native.GetType().FullName);
            }
        }

        // Screens and VB scripts can be library type instances; their XML alone cannot preserve the link.
        private static void RequireClassicImportUnlinked(IEngineeringObject native)
        {
            if (native is Siemens.Engineering.Hmi.Screen.Screen || native is Siemens.Engineering.Hmi.RuntimeScripting.VBScript) HmiLibraryLink.RequireNoLink(native);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject PrepareClassicXmlImport(EngineeringChange change, Siemens.Engineering.HW.Software software, IEngineeringObject native)
        {
            ClassicXmlImportRoute(software, native);
            RequireClassicImportUnlinked(native);
            string before = Path.Combine(change.Directory, "hmi-classic-before.xml");
            ClassicObjectExport(native)(new FileInfo(before));
            change.Backups.Add(new JObject { ["role"] = "hmi-classic-before", ["artifact"] = ChangeArtifact(before) });
            string xml = change.Payload.Value<string>("xml");
            System.Xml.Linq.XElement current;
            try { current = HmiPreservationRules.ClassicImportRoot(HmiPreservationRules.ReadXml(File.ReadAllText(before))); HmiPreservationRules.ClassicImportRoot(HmiPreservationRules.ReadXml(xml), current); }
            catch (ArgumentException error) { throw new BridgeException("XmlScopeMismatch", error.Message, change.HmiEvidence); }
            change.InputPath = Path.Combine(change.Directory, "hmi-classic-expected.xml");
            File.WriteAllText(change.InputPath, xml);
            change.InputHash = ChangeFileHash(change.InputPath);
            return new JObject { ["operation"] = "classic_xml_import", ["objectPath"] = change.Payload["target"]["objectPath"].DeepClone(),
                ["nativeType"] = native.GetType().FullName, ["exportRoot"] = current.Name.LocalName, ["beforeSha256"] = ChangeFileHash(before), ["inputSha256"] = change.InputHash,
                ["declaredDelta"] = "The exact object's native re-export must equal the supplied document apart from DocumentInfo and formatting; every other HMI export must stay identical." };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyClassicXmlImport(EngineeringChange change, Siemens.Engineering.HW.Software software, IEngineeringObject native)
        {
            RequireClassicImportUnlinked(native);
            var imported = ClassicXmlImportRoute(software, native)(new FileInfo(change.InputPath))
                ?? throw new BridgeException("PreservationMismatch", "Native import did not preserve the exact Classic object identity.");
            string after = Path.Combine(change.Directory, "hmi-classic-after.xml");
            ClassicObjectExport(imported)(new FileInfo(after));
            change.Readbacks.Add(new JObject { ["role"] = "hmi-native-readback", ["artifact"] = ChangeArtifact(after) });
            EngineeringSources.RequireNativePreservation(HmiPreservationRules.ReadXml(File.ReadAllText(change.InputPath)), HmiPreservationRules.ReadXml(File.ReadAllText(after)));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyUnifiedEngineeringBody(EngineeringChange change, Project project, IEngineeringObject native)
        {
            var effect = PrepareUnifiedEngineeringBody(change, project, native);
            var payload = change.Payload;
            string operation = payload.Value<string>("operation");
            if (operation == "external_tag_create")
            {
                var tag = ((Siemens.Engineering.HmiUnified.HmiTags.HmiTagTable)native).Tags.Create(payload.Value<string>("name"));
                // Order observed natively by HmiSeeder: Connection, then PlcTag; the symbolic
                // setter derives DataType/PlcName and later DataType writes are refused.
                tag.Connection = effect.Value<string>("connection");
                tag.PlcTag = effect.Value<string>("plcTag");
                try
                {
                    HmiRecipeRules.RequireExternalTagReadback(payload.Value<string>("name"), payload.Value<string>("dataType"), effect.Value<string>("connection"),
                        effect.Value<string>("plcTag"), effect.Value<string>("plcName"), tag.Name, tag.DataType, tag.Connection, tag.PlcTag, tag.AccessMode.ToString(), tag.PlcName);
                }
                catch (ArgumentException error) { throw new BridgeException("PreservationMismatch", error.Message); }
                change.HmiEvidence["createdTagReadback"] = new JObject { ["name"] = tag.Name, ["dataType"] = tag.DataType, ["connection"] = tag.Connection,
                    ["plcTag"] = tag.PlcTag, ["plcName"] = tag.PlcName, ["accessMode"] = tag.AccessMode.ToString(), ["address"] = tag.Address, ["runtimeVerified"] = false };
            }
            else if (operation == "tag_create")
            {
                var tag = ((Siemens.Engineering.HmiUnified.HmiTags.HmiTagTable)native).Tags.Create(payload.Value<string>("name"));
                // Native internal defaults are localized (e.g. a nonempty German
                // pseudo-connection). Observe the freshly created defaults and
                // verify no binding is introduced; never compare an English label.
                string defaultConnection = tag.Connection, defaultPlcTag = tag.PlcTag, defaultAddress = tag.Address;
                if (!string.IsNullOrEmpty(defaultPlcTag) || !string.IsNullOrEmpty(defaultAddress))
                    throw new BridgeException("PreservationMismatch", "New native tag unexpectedly has a PLC reference/address before type assignment.");
                tag.DataType = payload.Value<string>("dataType");
                if (tag.Name != payload.Value<string>("name") || tag.DataType != payload.Value<string>("dataType") || tag.Connection != defaultConnection || tag.PlcTag != defaultPlcTag || tag.Address != defaultAddress)
                    throw new BridgeException("PreservationMismatch", "Native internal-tag readback differs from its prepared name/type/connection scope.");
                change.HmiEvidence["createdTagReadback"] = new JObject { ["name"] = tag.Name, ["dataType"] = tag.DataType,
                    ["nativeDefaultConnection"] = defaultConnection, ["connection"] = tag.Connection, ["plcTag"] = tag.PlcTag, ["address"] = tag.Address };
            }
            else if (operation == "connection_create")
            {
                var connection = ((Siemens.Engineering.HmiUnified.HmiSoftware)native).Connections.Create(payload.Value<string>("name"));
                var readback = new JObject { ["name"] = connection.Name, ["partner"] = connection.Partner, ["station"] = connection.Station, ["node"] = connection.Node,
                    ["communicationDriver"] = connection.CommunicationDriver, ["runtimeVerified"] = false };
                change.HmiEvidence["createdConnectionReadback"] = readback;
                if (connection.Name != effect.Value<string>("name") || connection.Partner != effect.Value<string>("plcName") || string.IsNullOrEmpty(connection.CommunicationDriver))
                    throw new BridgeException("PreservationMismatch", "The created connection does not read back the proven partner PLC and a communication driver.", readback);
            }
            else if (operation == "object_create")
            {
                var created = UnifiedCompositionObject((Siemens.Engineering.HmiUnified.HmiSoftware)native, payload.Value<string>("composition"), payload.Value<string>("name"), true);
                if (HmiSafeAttribute(created, "Name") != payload.Value<string>("name")) throw new BridgeException("PreservationMismatch", "The created object's native name differs from the request.");
                change.HmiEvidence["createdObjectReadback"] = HmiSetTypedValues(created, payload["properties"] as JObject);
            }
            else if (operation == "object_properties_set") change.HmiEvidence["objectReadback"] = HmiSetTypedValues(native, (JObject)payload["properties"]);
            else if (operation == "screen_create")
            {
                var screen = ((Siemens.Engineering.HmiUnified.HmiSoftware)native).Screens.Create(payload.Value<string>("name"));
                if (screen.Name != payload.Value<string>("name")) throw new BridgeException("PreservationMismatch", "The created screen's native name differs from the request.");
                change.HmiEvidence["createdScreenReadback"] = HmiSetTypedValues(screen, (JObject)payload["properties"]);
            }
            else if (operation == "item_create")
            {
                var p = (JObject)payload["placement"];
                var created = UnifiedCreateItem(((Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen)native).ScreenItems, payload.Value<string>("itemType"), payload.Value<string>("name"));
                if (created.Name != payload.Value<string>("name")) throw new BridgeException("PreservationMismatch", "The created item's native name differs from the request.");
                var readback = HmiSetTypedValues(created, new JObject { ["Left"] = p["left"].DeepClone(), ["Top"] = p["top"].DeepClone(), ["Width"] = p["width"].DeepClone(), ["Height"] = p["height"].DeepClone() });
                readback["nativeType"] = created.GetType().FullName;
                change.HmiEvidence["createdItemReadback"] = readback;
            }
            else if (operation == "dynamization_set")
            {
                var binding = (JObject)payload["bindings"][0];
                string property = binding.Value<string>("property");
                var tag = (Siemens.Engineering.HmiUnified.HmiTags.HmiTag)HmiResolveNativeObject(project, (JObject)binding["tagIdentity"]).Native;
                var dynamization = ((Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBase)native).Dynamizations.Create<Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization>(property);
                dynamization.Tag = tag.Name;
                var readback = new JObject { ["propertyName"] = dynamization.PropertyName, ["dynamizationType"] = dynamization.DynamizationType.ToString(), ["tag"] = dynamization.Tag,
                    ["dataType"] = dynamization.DataType, ["useIndirectAddressing"] = dynamization.UseIndirectAddressing, ["readOnly"] = dynamization.ReadOnly };
                change.HmiEvidence["dynamizationReadback"] = readback;
                if (dynamization.PropertyName != property || dynamization.DynamizationType != Siemens.Engineering.HmiUnified.UI.Dynamization.DynamizationType.Tag || dynamization.Tag != tag.Name || dynamization.UseIndirectAddressing)
                    throw new BridgeException("PreservationMismatch", "The created tag dynamization does not read back the exact property and resolved HMI tag.", readback);
            }
            else if (operation == "event_handler_set")
            {
                string code = payload.Value<string>("script");
                object handler = HmiEventHandler(native, payload.Value<string>("eventType"), true);
                var script = handler?.GetType().GetProperty("Script")?.GetValue(handler, null) as Siemens.Engineering.HmiUnified.UI.Dynamization.Script.IHmiScript
                    ?? throw new BridgeException("HmiAttributeUnavailable", "The created event handler exposes no script.");
                // Untrusted text is stored verbatim; the bridge never executes, evaluates or syntax-checks it.
                script.ScriptCode = code;
                var readback = new JObject { ["eventType"] = Convert.ToString(handler.GetType().GetProperty("EventType")?.GetValue(handler, null)),
                    ["scriptSha256"] = HmiPreservationRules.Hash(script.ScriptCode ?? ""), ["scriptLength"] = script.ScriptCode?.Length ?? 0, ["async"] = script.Async, ["executed"] = false };
                change.HmiEvidence["eventHandlerReadback"] = readback;
                if (script.ScriptCode != code || readback.Value<string>("eventType") != payload.Value<string>("eventType"))
                    throw new BridgeException("PreservationMismatch", "The created event handler does not read back the exact event type and script text.", readback);
            }
            else if (operation == "scalar_property_set" || operation == "item_properties_set")
            {
                foreach (var property in ((JObject)payload["properties"]).Properties())
                {
                    native.SetAttribute(property.Name, ConvertChangeValue(property.Value, native.GetAttribute(property.Name).GetType()));
                    if (!JToken.DeepEquals(ChangeScalar(native.GetAttribute(property.Name)), property.Value)) throw new BridgeException("PreservationMismatch", "Unified scalar readback differs: " + property.Name);
                }
            }
            else
            {
                var container = (Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer)native;
                if (operation == "item_place")
                {
                    var p = (JObject)payload["placement"];
                    container.Left = p.Value<int>("left"); container.Top = p.Value<int>("top"); container.Width = p.Value<uint>("width"); container.Height = p.Value<uint>("height");
                    if (container.Left != p.Value<int>("left") || container.Top != p.Value<int>("top") || container.Width != p.Value<uint>("width") || container.Height != p.Value<uint>("height"))
                        throw new BridgeException("PreservationMismatch", "Native placement normalized the requested rectangle.");
                }
                else foreach (JObject binding in (JArray)payload["bindings"])
                {
                    var tag = (Siemens.Engineering.HmiUnified.HmiTags.HmiTag)HmiResolveNativeObject(project, (JObject)binding["tagIdentity"]).Native;
                    var property = container.Interface.Find(binding.Value<string>("property"));
                    property.Value = tag.Name;
                    if (!Equals(property.Value, tag.Name)) throw new BridgeException("PreservationMismatch", "Native interface binding does not match the exact resolved HMI tag.");
                }
            }
        }

        private JObject CompileHmiEngineeringChange(EngineeringChange change, Project project)
        {
            var selected = ResolveHmiTarget(project, (JObject)change.Payload["target"]);
            var compiler = ((IEngineeringServiceProvider)selected.Software).GetService<ICompilable>() ?? throw new BridgeException("HmiCompileUnavailable", "The selected HMI profile has no native compiler service.");
            var result = compiler.Compile();
            return new JObject { ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount, ["targets"] = new JArray(ChangeCompilation(result, selected.Path)), ["runtimeVerified"] = false };
        }

        private string HmiEngineeringFingerprint(EngineeringChange change, Project project)
        {
            var manifest = CaptureHmiEngineeringScope(change, project, "fingerprint");
            RequireHmiCompleteScope(manifest);
            return manifest.Value<string>("contentHash");
        }

        private void VerifyHmiEngineeringPreservation(EngineeringChange change, Project project)
        {
            var after = CaptureHmiEngineeringScope(change, project, "after");
            RequireHmiCompleteScope(after); RequireHmiCompleteScope(change.HmiBeforeManifest);
            var beforeHashes = ((JObject)change.HmiBeforeManifest["artifacts"]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>("contentSha256") ?? p.Value.Value<string>("sha256"), StringComparer.Ordinal);
            var afterHashes = ((JObject)after["artifacts"]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>("contentSha256") ?? p.Value.Value<string>("sha256"), StringComparer.Ordinal);
            // Until an operation-specific artifact delta has native qualification,
            // every other artifact is required to be identical, including unknowns.
            string changed = change.Payload["target"].Value<string>("objectPath");
            string operation = change.Payload.Value<string>("operation");
            try
            {
                // Tag creation changes the table's export files and adds the created tag's typed nodes, both keyed below its objectPath.
                if (operation == "tag_create" || operation == "external_tag_create")
                    HmiPreservationRules.RequireUnrelatedPreservedOutside(beforeHashes, afterHashes, changed + "/export/", changed + "/tags/" + Segment(change.Payload.Value<string>("name")));
                else if (operation == "connection_create" || operation == "object_create" || operation == "screen_create" || operation == "item_create" || operation == "dynamization_set" || operation == "event_handler_set")
                    HmiPreservationRules.RequireOnlyCreatedSubtree(beforeHashes, afterHashes, HmiCreatedPath(change.Payload));
                else
                {
                    // Bindings write the container's typed faceplate interface entries.
                    var paths = new List<string> { changed };
                    if (change.Payload["bindings"] is JArray bindings) paths.AddRange(bindings.Select(b => changed + "/interface/" + Segment(b.Value<string>("property"))).Where(beforeHashes.ContainsKey));
                    HmiPreservationRules.RequireUnrelatedPreserved(beforeHashes, afterHashes, paths, Array.Empty<string>());
                }
            }
            catch (ArgumentException error) { throw new BridgeException("PreservationMismatch", error.Message, after); }
            change.HmiEvidence["after"] = after;
            change.HmiEvidence["preservationVerified"] = true;
            change.Stages.Add("hmi_unrelated_content_preserved");
        }

        private void VerifyHmiEngineeringRecovery(EngineeringChange change, Project recovered)
        {
            var actual = CaptureHmiEngineeringScope(change, recovered, "recovery");
            RequireHmiCompleteScope(actual); RequireHmiCompleteScope(change.HmiBeforeManifest);
            if (actual.Value<string>("contentHash") != change.HmiBeforeManifest.Value<string>("contentHash"))
                throw new BridgeException("PreservationMismatch", "Retrieved HMI content does not match the pre-change complete native content hash; the recovered copy is retained for inspection.", actual);
            change.HmiEvidence["recovery"] = actual;
            change.HmiEvidence["recoveryVerified"] = true;
            change.Stages.Add("hmi_recovered_content_equality_verified");
        }

        // Called by the library subsystem before any HMI native write. Its exact
        // type/version lifecycle remains there; these primitives never publish,
        // release, transfer, update or select a library version.
        private void RequireHmiLibraryHandoff(Project project, JObject payload, LibraryTypeVersion version = null, MasterCopy master = null)
        {
            var target = ResolveHmiTarget(project, payload["target"] as JObject);
            string action = HmiAction(payload);
            string operation = action == "tag_create" ? "tag_create" : action == "faceplate_bind" ? "explicit_binding_set" : "item_place";
            var contract = new JObject { ["schemaVersion"] = 1, ["owner"] = "library-subsystem", ["destinationOwner"] = "hmi-adapter",
                ["contract"] = "contracts/hmi-library-handoff.v1.schema.json",
                ["requiredSource"] = new JArray("source.family", "source.typeGuid", "source.versionGuid", "source.libraryRevision", "dependencyClosure.complete", "dependencyClosure.revision"),
                ["requiredDestination"] = new JArray("destination", "preservation.contentFingerprint", "placement", "bindings"),
                ["lifecycle"] = "existing resource-aware library change prepare/apply/status/restore", ["newLifecycleCreated"] = false };
            var handoff = payload["libraryHandoff"] as JObject;
            if (handoff != null)
            {
                if (handoff.Value<int?>("schemaVersion") != 1) throw new BridgeException("InvalidRequest", "libraryHandoff.schemaVersion must be 1.");
                var source = handoff["source"] as JObject ?? throw new BridgeException("LibraryHandoffRequired", "A resolved native library source is required.");
                var destination = handoff["destination"] as JObject ?? throw new BridgeException("LibraryHandoffRequired", "An exact HMI destination identity is required.");
                HmiValidateIdentity(project, destination);
                if (destination.Value<string>("hmiPath") != target.Path || source.Value<string>("family") != target.Family)
                    throw new BridgeException("TargetIncompatible", "Library handoff and resolved HMI destination belong to different native families or HMI software.");
                if (version != null && (source.Value<string>("typeGuid") != version.TypeObject.Guid.ToString("D") || source.Value<string>("versionGuid") != version.Guid.ToString("D")))
                    throw new BridgeException("LibraryIdentityMismatch", "Handoff type/version GUIDs do not match the library subsystem's resolved native source.");
                if ((handoff["dependencyClosure"] as JObject)?.Value<bool?>("complete") != true)
                    throw new BridgeException("DependencyUnresolved", "The library subsystem must resolve a complete exact dependency closure before HMI placement.");
                contract["sourceMatchesResolvedVersion"] = version != null;
                contract["sourceIsMasterCopy"] = master != null;
                contract["handoffProvided"] = true;
            }
            else contract["handoffProvided"] = false;
            var observedTarget = HmiResolveNativeTarget(project, (JObject)payload["target"]);
            var gate = HmiEngineeringRules.Gate(target.Family, observedTarget.Profile, operation);
            gate["catalogIdentifier"] = observedTarget.CatalogIdentifier;
            gate["runtimeVersion"] = observedTarget.RuntimeVersion;
            RequireHmiEngineeringGate(gate, new JObject { ["target"] = HmiTargetView(target), ["hmiAction"] = action, ["handoffContract"] = contract, ["gate"] = gate });
        }
    }
}
