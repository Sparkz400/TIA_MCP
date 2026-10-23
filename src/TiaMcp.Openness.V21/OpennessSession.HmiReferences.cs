using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static string HmiStringProperty(JObject properties, string name)
            => properties[name]?.Type == JTokenType.String ? properties.Value<string>(name) : null;
        private static void HmiAddRelationship(HmiCapture capture, JArray edges, JObject edge)
        {
            int size = System.Text.Encoding.UTF8.GetByteCount(edge.ToString(Formatting.None));
            if (edges.Count >= 2000 || capture.RelationBytes + size > 4 * 1024 * 1024)
            {
                capture.Budget.Truncated = true;
                if (!capture.Diagnostics.OfType<JObject>().Any(d => d.Value<string>("code") == "HmiRelationshipLimit"))
                    capture.Diagnostics.Add(new JObject { ["code"] = "HmiRelationshipLimit", ["reason"] = "Relationship capture reached 2000 edges or 4 MiB; omitted edges are not claimed complete." });
                return;
            }
            capture.RelationBytes += size; edges.Add(edge);
        }
        private JObject HmiCapabilities(JObject args)
        {
            var modules = new JArray();
            foreach (string family in new[] { "classic", "unified" })
            {
                string path = Path.Combine(_apiDirectory, HmiAssemblyName(family) + ".dll");
                var module = new JObject { ["family"] = family, ["assemblyPath"] = path, ["installed"] = File.Exists(path),
                    ["apiAvailability"] = File.Exists(path) ? "available" : "unavailable", ["bridgeImplementation"] = "partial", ["nativeQualification"] = "unverified",
                    ["license"] = "notProbed", ["protection"] = "targetSpecific", ["runtimeVerified"] = false };
                if (File.Exists(path))
                {
                    try
                    {
                        var identity = HmiModuleIdentity(family, path);
                        module["assemblyVersion"] = identity["assemblyVersion"].DeepClone(); module["assemblySha256"] = FileArtifact(new FileInfo(path))["sha256"];
                        module["assemblyIdentity"] = identity;
                        if (!identity.Value<bool>("accepted")) { module["apiAvailability"] = "unavailable"; module["diagnostic"] = identity.DeepClone(); }
                    }
                    catch (Exception error) { module["diagnostic"] = HmiDiagnostic("optionalAssembly", error); module["apiAvailability"] = "unavailable"; }
                }
                else module["diagnostic"] = new JObject { ["code"] = "HmiPrerequisiteMissing", ["requiredAssembly"] = Path.GetFileName(path), ["family"] = family };
                string document = Path.ChangeExtension(path, ".xml");
                if (File.Exists(document)) module["documentation"] = FileArtifact(new FileInfo(document));
                modules.Add(module);
            }
            var profiles = new JArray();
            foreach (string profile in HmiInspectionRules.Profiles)
            {
                bool classic = profile.StartsWith("classic-", StringComparison.Ordinal);
                profiles.Add(new JObject { ["profile"] = profile, ["family"] = classic ? "classic" : "unified", ["apiAvailability"] = "targetConditional",
                    ["bridgeImplementation"] = "partial", ["nativeQualification"] = "unverified", ["currentTargetAvailability"] = "notProbed",
                    ["operations"] = new JArray(new[] { "devices", "connections", "tags", "screens", "items", "alarms", "scripts", "resources", "references" }.Select(section => new JObject {
                        ["operation"] = section, ["implementation"] = classic && section == "alarms" ? "unsupported" : "implemented",
                        ["api"] = classic && section == "alarms" ? "notExposed" : classic && section == "items" ? "nativeXmlExport" : "nativeObjectModel",
                        ["qualification"] = "unverified", ["limitations"] = section == "references" ? "Exact native object references and explicit native foreign keys only; dynamic and names-only PLC endpoints remain unresolved." : section == "items" && classic ? "Exact screen and retained export required; unknown XML references remain unresolved." : "Separate native fixture, license, target version and protection checks required." })),
                    ["engineeringOperations"] = new JArray(HmiEngineeringRules.Operations.Select(operation => HmiEngineeringRules.Gate(classic ? "classic" : "unified", profile, operation))) });
            }
            var result = Envelope(); result["schemaVersion"] = 1; result["modules"] = modules; result["profiles"] = profiles;
            result["sessionGeneration"] = _hmiSessionGeneration;
            var selector = args["target"] as JObject ?? args["identity"] as JObject ?? args;
            if (selector.Value<string>("hmiPath") != null)
            {
                var project = SelectProject(args); var capture = new HmiCapture(50000);
                var selected = HmiResolveNativeTarget(project, selector, capture);
                result["target"] = HmiDeviceView(project, selected);
                result["engineeringOperations"] = new JArray(HmiEngineeringRules.Operations.Select(operation => HmiEngineeringRules.Gate(selected.Family, selected.Profile, operation)));
                result["targetAttributes"] = HmiProperties(selected.Software, capture);
                result["diagnostics"] = capture.Diagnostics;
                foreach (JObject profile in profiles) if (profile.Value<string>("profile") == selected.Profile) profile["currentTargetAvailability"] = "available";
            }
            result["licenseDiagnostic"] = "Module presence does not establish license availability. Native enumeration and lookup report license/access failures separately; a null SoftwareContainer.Software is not proof the device is absent.";
            result["optionalStartup"] = "No optional HMI type is used in the dispatcher, session fields, or discovery. Family bodies are entered only after installed-module checks; Siemens binaries are not bundled.";
            return result;
        }

        private JObject HmiReferences(JObject args)
        {
            if (args.Value<int?>("schemaVersion") is int version && version != 1) throw new BridgeException("UnsupportedSchemaVersion", "HMI reference request schemaVersion must be 1.");
            args = (JObject)args.DeepClone();
            var requestedIdentity = args["target"] as JObject ?? args["identity"] as JObject;
            if (args["screenPath"] == null && requestedIdentity?["screenPath"] != null) args["screenPath"] = requestedIdentity["screenPath"].DeepClone();
            var project = SelectProject(args); var capture = new HmiCapture(HmiBound(args, "maxVisited", 5000, 1, 50000));
            var target = HmiResolveNativeTarget(project, args["target"] as JObject ?? args["identity"] as JObject ?? args, capture); RequireHmiModule(target.Family);
            var sections = new List<string> { "connections", "tags" };
            if (args.Value<string>("screenPath") != null) sections.Add("items");
            if (args.Value<bool?>("includeAlarms") == true) sections.Add("alarms");
            foreach (string section in sections)
            {
                if (capture.Budget.Truncated) break;
                try { if (target.Family == "classic") HmiCaptureClassic(project, target, section, args, capture, includeScreenOwner: true); else HmiCaptureUnified(project, target, section, args, capture, includeScreenOwner: true); }
                catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic(section, error)); capture.Status = "unavailable"; capture.CandidateScopesComplete = false; }
            }
            var edges = new JArray();
            bool foreignKeyScopeComplete = !capture.Budget.Truncated && capture.CandidateScopesComplete;
            var connectionIdentities = capture.Entries.Where(e => e.Identity.Value<string>("kind") == "hmiConnection")
                .GroupBy(e => e.Identity.Value<string>("name"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(e => e.Identity).ToList(), StringComparer.Ordinal);
            var tagIdentities = capture.Entries.Where(e => e.Identity.Value<string>("kind") == "hmiTag" && !e.Identity.Value<string>("objectPath").Contains("/members/"))
                .GroupBy(e => e.Identity.Value<string>("name"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(e => e.Identity).ToList(), StringComparer.Ordinal);
            foreach (var entry in capture.Entries)
            {
                if (capture.Budget.Truncated) break;
                string path = entry.Identity.Value<string>("objectPath"), kind = entry.Identity.Value<string>("kind");
                var properties = entry.Row["properties"] as JObject ?? new JObject();
                if (kind == "hmiAlarm" && target.Family == "unified")
                    foreach (var edge in HmiInspectionRules.UnifiedAlarmReferences(entry.Row.Value<string>("nativeType"), path, properties, entry.Row["alarmParameterTags"],
                        spelling => tagIdentities.TryGetValue(spelling, out var matches) ? matches : Enumerable.Empty<JObject>(), foreignKeyScopeComplete))
                    { if (capture.Budget.Truncated) break; HmiAddRelationship(capture, edges, edge); }
                if (kind == "hmiTag")
                {
                    string connection = HmiStringProperty(properties, "Connection");
                    if (!string.IsNullOrEmpty(connection))
                        HmiAddRelationship(capture, edges, HmiInspectionRules.Reference(path, "hmiTagConnection", "Connection", connection,
                            connectionIdentities.TryGetValue(connection, out var connectionMatches) ? connectionMatches : Enumerable.Empty<JObject>(), false, candidatesComplete: foreignKeyScopeComplete));
                    string plcTag = HmiStringProperty(properties, "PlcTag");
                    if (!string.IsNullOrEmpty(plcTag))
                    {
                        var edge = HmiInspectionRules.Reference(path, "hmiPlcDeclaration", "PlcTag", plcTag, Enumerable.Empty<JObject>(), properties.Value<bool?>("DBNameMultiplexing") == true);
                        edge["reason"] = "The native symbolic spelling is retained. An exact PLC declaration requires a native engineering-object relation; matching PLC/tag names alone is insufficient.";
                        edge["connection"] = connection; edge["plcName"] = properties["PlcName"]; HmiAddRelationship(capture, edges, edge);
                    }
                }
                if (kind == "hmiConnection")
                {
                    string partner = HmiStringProperty(properties, "Partner");
                    if (!string.IsNullOrEmpty(partner))
                    {
                        var edge = HmiInspectionRules.Reference(path, "hmiConnectionPlc", "Partner/Station/Node", partner, Enumerable.Empty<JObject>(), false);
                        edge["nativeStation"] = properties["Station"]; edge["nativeNode"] = properties["Node"];
                        edge["reason"] = "Native connection endpoint names are evidence of configuration, but are not an exact project object identity. Native cross-reference object endpoints are resolved separately.";
                        HmiAddRelationship(capture, edges, edge);
                    }
                }
                foreach (JObject dynamic in entry.Row["dynamizations"] as JArray ?? new JArray())
                {
                    if (capture.Budget.Truncated) break;
                    var values = dynamic["properties"] as JObject ?? new JObject(); string tag = HmiStringProperty(values, "Tag");
                    bool indirect = dynamic.Value<string>("resolution") != "explicitTagReference";
                    var candidates = tag != null && tagIdentities.TryGetValue(tag, out var tagMatches) ? tagMatches : Enumerable.Empty<JObject>();
                    var edge = HmiInspectionRules.Reference(path, "hmiScreenItemTag", "Dynamizations/" + dynamic.Value<string>("property") + "/Tag", tag, candidates, indirect, candidatesComplete: foreignKeyScopeComplete);
                    edge["nativeDynamizationType"] = dynamic["nativeType"]; edge["rawProperties"] = values.DeepClone(); HmiAddRelationship(capture, edges, edge);
                }
                foreach (JObject raw in entry.Row["referencePayloads"] as JArray ?? new JArray())
                {
                    if (capture.Budget.Truncated) break;
                    var edge = HmiInspectionRules.Reference(path, "hmiNativePayload", raw.Value<string>("property"), raw.Value<string>("nativeSpelling"), Enumerable.Empty<JObject>(), false, "unresolved");
                    edge["sourceArtifact"] = entry.Row["source"]?.DeepClone(); edge["rawPayload"] = raw.DeepClone(); HmiAddRelationship(capture, edges, edge);
                }
                if (target.Family == "classic" && entry.Row["behaviorCoverage"] is JObject classicBehavior)
                {
                    var edge = HmiInspectionRules.UnresolvedNativeReference(path, "hmiNativePayload", "ScreenExport/Behaviors", classicBehavior, "Screen event/script/navigation XML is retained but has no qualified semantic parser.");
                    edge["sourceArtifact"] = classicBehavior["sourceArtifact"]?.DeepClone(); HmiAddRelationship(capture, edges, edge);
                }
                foreach (var property in properties.Properties().Where(p => p.Value.Type == JTokenType.String &&
                    (p.Name.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0 || p.Name.IndexOf("Navigation", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    if (capture.Budget.Truncated) break;
                    HmiAddRelationship(capture, edges, HmiInspectionRules.UnresolvedNativeReference(path, "hmiNativePayload", property.Name, property.Value,
                        "Observed screen/navigation-related native text. No target or navigation semantics are inferred from this property name or value."));
                }
                foreach (string field in new[] { "dynamizations", "eventHandlers", "propertyEventHandlers" })
                    foreach (JObject behavior in entry.Row[field] as JArray ?? new JArray())
                    {
                        if (capture.Budget.Truncated) break;
                        if (!(behavior["script"] is JObject script)) continue;
                        var edge = HmiInspectionRules.Reference(path, "hmiScriptBehavior", field + "/Script", null, Enumerable.Empty<JObject>(), true);
                        edge["nativeBehaviorType"] = behavior["nativeType"]; edge["rawScript"] = script.DeepClone();
                        edge["eventMetadata"] = behavior["properties"]?.DeepClone();
                        edge["reason"] = "Native script source and hash are retained. Tag/navigation expressions have no qualified static parser and are not executed.";
                        HmiAddRelationship(capture, edges, edge);
                    }
                if (entry.Row["faceplate"] is JObject faceplate)
                {
                    bool exact = faceplate.Value<string>("versionGuid") != null;
                    HmiAddRelationship(capture, edges, new JObject { ["sourcePath"] = path, ["relation"] = "hmiFaceplateTypeVersion", ["evidence"] = exact ? "nativeConfirmed" : "unresolved",
                        ["resolution"] = exact ? "resolvedLibraryReference" : "unresolved", ["target"] = null, ["targetCandidates"] = new JArray(),
                        ["libraryReference"] = faceplate.DeepClone(), ["sourceProperty"] = "LibraryTypeInstanceInfo.LibraryTypeVersion", ["lifecycleOwner"] = "librarySubsystem", ["automaticRewiringAllowed"] = false });
                    foreach (JObject port in faceplate["interface"] as JArray ?? new JArray())
                    {
                        if (capture.Budget.Truncated) break;
                        var edge = HmiInspectionRules.UnresolvedNativeReference(path, "hmiFaceplatePort", "Interface/" + port.Value<string>("propertyName"), port["value"],
                            "Native interface value retained. Port type/direction and binding semantics have no qualified resolver; matching a tag name does not prove a port binding.");
                        edge["lifecycleOwner"] = "librarySubsystem"; HmiAddRelationship(capture, edges, edge);
                    }
                }
            }
            HmiNativeReferenceEdges(project, target, capture, edges, args);
            int limit = HmiBound(args, "limit", 100, 1, 200), offset = HmiBound(args, "offset", 0, 0, 50000), maxBytes = HmiBound(args, "maxBytes", 262144, 4096, 1048576);
            var page = new JArray(); int bytes = 0;
            foreach (JObject edge in edges.Skip(offset).Take(limit))
            {
                int size = System.Text.Encoding.UTF8.GetByteCount(edge.ToString(Formatting.None));
                if (bytes + size > maxBytes) break; page.Add(edge); bytes += size;
            }
            if (page.Count == 0 && offset < edges.Count)
            {
                capture.Budget.Truncated = true;
                capture.Diagnostics.Add(new JObject { ["code"] = "HmiRelationshipTooLarge", ["reason"] = "The next relationship exceeds maxBytes; request a larger byte limit. No empty page is claimed complete." });
            }
            string revision = ChangeHash(edges.ToString(Formatting.None));
            if (args.Value<string>("expectedMetadataRevision") is string expected && expected != revision) throw new BridgeException("HmiMetadataChanged", "Relationship metadata changed between pages.");
            var result = Envelope(); result["schemaVersion"] = 1; result["target"] = HmiDeviceView(project, target); result["relationships"] = page;
            result["offset"] = offset; result["nextOffset"] = offset + page.Count < edges.Count && page.Count > 0 ? (JToken)(offset + page.Count) : null;
            result["metadataRevision"] = revision; result["observedAtUtc"] = DateTime.UtcNow.ToString("O"); result["diagnostics"] = capture.Diagnostics; result["artifacts"] = capture.Artifacts;
            result["coverage"] = new JObject { ["status"] = capture.Budget.Truncated ? "truncated" : capture.Status, ["complete"] = false,
                ["scopeComplete"] = !capture.Budget.Truncated && capture.Status == "complete", ["truncated"] = capture.Budget.Truncated || page.Count + offset < edges.Count,
                ["candidateScopesComplete"] = !capture.Budget.Truncated && capture.CandidateScopesComplete,
                ["reasons"] = new JArray("Dynamic expressions, script behavior, names-only PLC endpoints, missing native cross-reference providers and unknown XML bindings remain unresolved.", "Only explicitly requested screen items and optional alarms are traversed.") };
            result["scriptsExecuted"] = false; result["runtimeVerified"] = false; return result;
        }

        private void HmiNativeReferenceEdges(Project project, HmiDiscoveredTarget target, HmiCapture capture, JArray edges, JObject args)
        {
            // Native endpoint equality anchors both cross-references and configured symbolic keys.
            // Textual Path/Name in cross-reference output is evidence, never an identity fallback.
            var known = new List<HmiNativeEntry>(capture.Entries.Where(e => e.Native != null));
            var plcEndpoints = new List<Tuple<Siemens.Engineering.HW.DeviceItem, PlcSelection>>();
            var providers = new List<IEngineeringObject> { target.Software };
            int maximumQueries = HmiBound(args, "maxNativeQueries", 4, 1, 16);
            try
            {
                foreach (var hardware in DeviceItems(project, capture.Budget))
                {
                    var software = hardware.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                    if (software == null) continue;
                    var plc = new PlcSelection { Project = project, Software = software, Path = hardware.Path + "/plc/" + Segment(software.Name) };
                    plcEndpoints.Add(Tuple.Create(hardware.Item, plc));
                    if (providers.Count < maximumQueries) providers.Add(software);
                    known.Add(new HmiNativeEntry { Native = software, Identity = IdentityInfo(project, plc.Path, plc.Path, plc.Path, "plc", software.Name) });
                    foreach (var scope in SoftwareScopes(plc, capture.Budget))
                    {
                        foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", capture.Budget))
                            foreach (var tag in table.Item.Tags)
                            {
                                if (!capture.Budget.Take()) break;
                                known.Add(new HmiNativeEntry { Native = tag, Identity = IdentityInfo(project, plc.Path, scope.Path, table.Path + "/tags/" + Segment(tag.Name), "tag", tag.Name) });
                            }
                        foreach (var block in ScopeBlocks(scope, capture.Budget))
                            known.Add(new HmiNativeEntry { Native = block.Item, Identity = IdentityInfo(project, plc.Path, scope.Path, block.Path, "block", block.Item.Name) });
                    }
                    if (capture.Budget.Truncated) break;
                }
                if (!capture.Budget.Truncated) HmiIntegratedRelationships(project, target, capture, edges, known, plcEndpoints, args);
                foreach (var provider in providers)
                {
                    if (capture.Budget.Truncated) break;
                    try
                    {
                        var service = (provider as IEngineeringServiceProvider)?.GetService<CrossReferenceService>();
                        if (service == null) { capture.Diagnostics.Add(new JObject { ["code"] = "HmiNativeReferenceProviderUnavailable", ["nativeType"] = provider.GetType().FullName }); continue; }
                        HmiCrossReferenceSources(service.GetCrossReferences(CrossReferenceFilter.AllObjects).Sources, known, capture, edges, 0);
                    }
                    catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic("CrossReferenceService", error)); capture.Status = "unavailable"; }
                }
            }
            catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic("PLC identity traversal", error)); capture.Status = "unavailable"; capture.CandidateScopesComplete = false; }
        }

        private static void HmiCrossReferenceSources(IEnumerable<SourceObject> sources, List<HmiNativeEntry> known, HmiCapture capture, JArray edges, int depth)
        {
            if (depth > 32) { capture.Budget.Truncated = true; return; }
            foreach (var source in sources)
            {
                if (!capture.Budget.Take()) return;
                var sourceMatches = source.UnderlyingObject == null ? new List<HmiNativeEntry>() : known.Where(e => Equals(e.Native, source.UnderlyingObject)).ToList();
                foreach (var reference in source.References)
                {
                    if (!capture.Budget.Take()) return;
                    var targetMatches = reference.UnderlyingObject == null ? new List<HmiNativeEntry>() : known.Where(e => Equals(e.Native, reference.UnderlyingObject)).ToList();
                    if (!sourceMatches.Any(e => e.Identity.Value<string>("kind").StartsWith("hmi", StringComparison.Ordinal)) && !targetMatches.Any(e => e.Identity.Value<string>("kind").StartsWith("hmi", StringComparison.Ordinal))) continue;
                    var edge = HmiInspectionRules.Reference(sourceMatches.Count == 1 ? sourceMatches[0].Identity.Value<string>("objectPath") : null,
                        "hmiNativeCrossReference", "CrossReferenceService.UnderlyingObject", reference.Name, targetMatches.Select(e => e.Identity), false);
                    edge["source"] = sourceMatches.Count == 1 ? sourceMatches[0].Identity.DeepClone() : null;
                    if (sourceMatches.Count != 1) { edge["resolution"] = "unresolved"; edge["evidence"] = "unresolved"; }
                    edge["nativeSource"] = new JObject { ["path"] = source.Path, ["name"] = source.Name, ["type"] = source.TypeName };
                    edge["nativeTarget"] = new JObject { ["path"] = reference.Path, ["name"] = reference.Name, ["type"] = reference.TypeName };
                    var locations = new JArray();
                    foreach (var location in reference.Locations)
                    {
                        if (!capture.Budget.Take()) break;
                        locations.Add(new JObject { ["name"] = location.Name, ["address"] = location.Address, ["access"] = location.Access.ToString(),
                            ["referenceLocation"] = location.ReferenceLocation, ["referenceType"] = location.ReferenceType.ToString(), ["referencedAsName"] = location.ReferencedAsName,
                            ["memberResolved"] = false });
                    }
                    edge["locations"] = locations; HmiAddRelationship(capture, edges, edge);
                }
                HmiCrossReferenceSources(source.Children, known, capture, edges, depth + 1);
            }
        }
    }
}
