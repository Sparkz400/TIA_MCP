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
            var entry = HmiResolveNativeObject(project, identity);
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
            var effect = target.Family == "classic" ? PrepareClassicEngineeringBody(change, native) : PrepareUnifiedEngineeringBody(change, project, native);
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
            CaptureClassicEngineeringFolder(hmi.ScreenSlideinFolder, "ScreenSlideins", target.Path + "/screenSlideins", directory, artifacts, 0);
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
                    else if (native is Siemens.Engineering.Hmi.Screen.ScreenSlidein slidein) slidein.Export(file, ExportOptions.WithDefaults);
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
        private JObject PrepareClassicEngineeringBody(EngineeringChange change, IEngineeringObject native)
        {
            if (change.Payload.Value<string>("operation") != "scalar_property_set" || !(native is Siemens.Engineering.Hmi.Screen.Screen screen))
                throw new BridgeException("HmiOperationUnsupported", "Classic only has an export-preserving screen BackColor scalar candidate; typed item/tag creation/placement/binding adapters are unavailable.", change.HmiEvidence);
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
                var plcTag = HmiRootPlcTag(project, plc, (JObject)payload["plcTag"]);
                if (!HmiScalarTypesAgree(payload.Value<string>("dataType"), plcTag.DataTypeName))
                    throw new BridgeException("TargetIncompatible", "The requested HMI data type does not exactly match the PLC declaration type; implicit conversions are not inferred.",
                        new JObject { ["requested"] = payload["dataType"].DeepClone(), ["plcDataType"] = plcTag.DataTypeName });
                result["name"] = payload["name"].DeepClone(); result["dataType"] = payload["dataType"].DeepClone(); result["scope"] = "external";
                result["connection"] = connection.Name; result["plcTag"] = plcTag.Name; result["plcName"] = plc.Software.Name; result["plcPath"] = plc.Path;
                result["plcDataType"] = plcTag.DataTypeName; result["accessMode"] = "SymbolicAccess"; result["connectionOwnership"] = ownership;
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
                    foreach (JObject binding in (JArray)payload["bindings"])
                    {
                        var property = container.Interface.Find(binding.Value<string>("property")) ?? throw new BridgeException("DependencyUnresolved", "The exact native faceplate interface property does not exist.");
                        if (operation == "existing_binding_set" && !JToken.DeepEquals(ChangeScalar(property.Value), binding["expectedValue"]))
                            throw new BridgeException("StaleRevision", "The existing native interface binding changed.");
                        var tag = HmiResolveNativeObject(project, (JObject)binding["tagIdentity"]).Native as Siemens.Engineering.HmiUnified.HmiTags.HmiTag
                            ?? throw new BridgeException("DependencyUnresolved", "Binding target is not an exact native HMI tag.");
                        HmiRecipeRules.RequireTypeCompatibility(binding.Value<string>("dataType"), tag.DataType);
                        if (!string.IsNullOrEmpty(tag.Connection) || !string.IsNullOrEmpty(tag.PlcTag))
                            throw new BridgeException("DependencyUnresolved", "This tag's connection ownership is not completely established. A nonempty Connection may be a localized internal pseudo-connection, a broken reference or an external PLC connection; names and caller assertions cannot decide this. Resolve native ownership and any exact PLC declaration/type evidence before binding.",
                                new JObject { ["connection"] = tag.Connection, ["plcTag"] = tag.PlcTag, ["resolution"] = "unresolved", ["localeIndependentInternalProofRequired"] = true });
                    }
                    result["bindings"] = payload["bindings"].DeepClone();
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
            var entry = HmiResolveNativeObject(project, (JObject)change.Payload["target"]);
            if (target.Family == "classic") ApplyClassicEngineeringBody(change, (IEngineeringObject)entry.Native);
            else ApplyUnifiedEngineeringBody(change, project, (IEngineeringObject)entry.Native);
            change.Stages.Add("hmi_precise_readback_verified");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyClassicEngineeringBody(EngineeringChange change, IEngineeringObject native)
        {
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
