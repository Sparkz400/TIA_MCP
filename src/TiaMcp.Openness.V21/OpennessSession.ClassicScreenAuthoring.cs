using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject ClassicScreenCapture(JObject args)
        {
            var project = SelectProject(args);
            var identity = args["target"] as JObject ?? throw new BridgeException("InvalidRequest", "An exact HMI identity is required.");
            EnsureProjectOffline(project);
            using (SharedMutationExclusive("TIA MCP: capture Classic screen baseline"))
            {
                _hmiLeaseObservations = new HmiLeaseObservations();
                var discovered = HmiResolveNativeTarget(project, identity);
                if (discovered.Family != "classic" || discovered.Profile != "classic-rt-advanced")
                    throw new BridgeException("HmiMutationTargetUnsupported", "Screen capture is bounded to Classic RT Advanced.");
                var change = new EngineeringChange { Id = Guid.NewGuid().ToString("N"), Kind = "hmi_classic_screen_edit",
                    Directory = Path.Combine(ChangeText(args, "artifactDirectory", 4096), "screen-capture-" + Guid.NewGuid().ToString("N")),
                    Payload = new JObject { ["target"] = identity.DeepClone() } };
                Directory.CreateDirectory(change.Directory);
                var manifest = CaptureHmiEngineeringScope(change, project, "capture");
                RequireHmiScope(change, manifest);
                manifest["identity"] = HmiIdentity(project, discovered, "hmiDevice", discovered.Path, discovered.Name);
                var requiredTags = args["tagReferences"] == null ? null : new HashSet<string>(ChangeStringArray(args, "tagReferences", 4096, 4096), StringComparer.Ordinal);
                manifest["tagArtifacts"] = args.Value<bool?>("includeTags") == false ? new JArray() : MeasureChange(change, "capture-tags", () => CaptureClassicTagEvidence(project, identity, change.Directory, requiredTags));
                manifest["tagCoverage"] = requiredTags == null ? (JToken)"all tables" : new JArray(requiredTags.OrderBy(x => x, StringComparer.Ordinal));
                if (args["area"] != null) manifest["areaPreparation"] = MeasureChange(change, "capture-area", () => CaptureClassicArea(project, args, change.Directory, manifest));
                string path = Path.Combine(change.Directory, "capture.json");
                File.WriteAllText(path, manifest.ToString());
                return new JObject { ["contentHash"] = manifest["contentHash"].DeepClone(), ["capture"] = ChangeArtifact(path),
                    ["identity"] = manifest["identity"].DeepClone(), ["screens"] = manifest["artifacts"].DeepClone(),
                    ["areaPreparation"] = manifest["areaPreparation"]?.DeepClone(),
                    ["scopePolicy"] = HmiPreservationRules.ClassicScreenPolicy, ["nativeMutationPerformed"] = false,
                    ["runtimeVerified"] = false };
            }
        }

        // Read-only: the same family-specific preservation capture change_prepare runs for kind, without a change record,
        // so expectedContentHash can be read instead of provoked with a failed prepare. Nothing is mutated or saved.
        private JObject HmiContentFingerprint(JObject args)
        {
            var project = SelectProject(args);
            var identity = args["target"] as JObject ?? throw new BridgeException("InvalidRequest", "An exact HMI identity (target) is required.");
            string kind = ChangeText(args, "kind", 64);
            if (!kind.StartsWith("hmi_", StringComparison.Ordinal) || kind == "hmi_compile")
                throw new BridgeException("InvalidRequest", "kind must be the HMI change kind you will prepare, for example hmi_screen_create; the hash depends on its scope policy.");
            HmiValidateIdentity(project, identity);
            EnsureProjectOffline(project);
            using (SharedMutationExclusive("TIA MCP: read HMI content fingerprint"))
            {
                if (kind == "hmi_classic_screen_edit") _hmiLeaseObservations = new HmiLeaseObservations();
                HmiResolveNativeTarget(project, identity);
                var change = new EngineeringChange { Id = Guid.NewGuid().ToString("N"), Kind = kind,
                    Directory = Path.Combine(ChangeText(args, "artifactDirectory", 4096), "fingerprint-" + Guid.NewGuid().ToString("N")),
                    Payload = new JObject { ["target"] = identity.DeepClone(), ["operation"] = args["operation"]?.DeepClone() } };
                Directory.CreateDirectory(change.Directory);
                var manifest = CaptureHmiEngineeringScope(change, project, "fingerprint");
                // Same acceptance as change_prepare (RequireHmiScope): no native failure, and either a complete capture or only
                // the kind's standing notes missing with the hash equal to the observed artifacts. nativeFailure is a JSON null when absent.
                bool failed = manifest["nativeFailure"] is JObject;
                bool accepted = !failed && !string.IsNullOrWhiteSpace(manifest.Value<string>("contentHash"))
                    && (HmiEngineeringRules.StandingNotes(kind) == null ? manifest.Value<bool>("complete")
                        : HmiEngineeringRules.ScopeAccepted(kind, manifest) && manifest.Value<string>("contentHash") == manifest.Value<string>("observedArtifactsHash"));
                return new JObject { ["kind"] = kind, ["target"] = identity.DeepClone(), ["contentHash"] = manifest["contentHash"]?.DeepClone(),
                    ["usableAsExpectedContentHash"] = accepted, ["complete"] = manifest["complete"]?.DeepClone(), ["scopeAccepted"] = accepted,
                    ["scopeAcceptance"] = manifest["scopeAcceptance"]?.DeepClone(), ["missingCoverage"] = manifest["missingCoverage"]?.DeepClone(),
                    ["status"] = manifest["status"]?.DeepClone(), ["nativeFailure"] = manifest["nativeFailure"]?.DeepClone(),
                    ["artifactCount"] = (manifest["artifacts"] as JObject)?.Count ?? 0,
                    ["manifest"] = ChangeArtifact(Path.Combine(change.Directory, Directory.GetDirectories(change.Directory).Select(d => Path.GetFileName(d)).FirstOrDefault() ?? "", "manifest.json")),
                    ["note"] = "complete is false when only the kind's standing notes are missing; scopeAccepted then says the hash still authorizes this kind. change_prepare recaptures and refuses with StaleRevision if content changed in between.",
                    ["nativeMutationPerformed"] = false, ["runtimeVerified"] = false };
            }
        }

        // No TIA mutation, no generated control implementation. Composition starts from captured native objects.
        private JObject ClassicScreenCompose(JObject args)
        {
            string root = ChangeText(args, "artifactDirectory", 4096);
            string capturePath = RequireScreenRecoveryFile(root, args["capture"] as JObject);
            var manifest = JObject.Parse(File.ReadAllText(capturePath));
            ClassicScreenRecoveryRules.RequireManifest(manifest);
            var documents = ReadClassicScreenDocuments(root, manifest);
            string targetPath = ChangeText(args, "screenPath", 4096);
            if (!documents.TryGetValue(targetPath, out var original)) throw new BridgeException("ObjectNotFound", "screenPath must name a captured regular screen.");
            try
            {
                var composed = ClassicScreenComposer.Compose(original, documents, args);
                var tags = ReadClassicTagEvidence(root, manifest["tagArtifacts"] as JArray ?? new JArray());
                var check = ClassicScreenPreflight.Check(composed, original, documents.Values, tags);
                string output = Path.Combine(root, "screen-composed-" + Guid.NewGuid().ToString("N") + ".xml");
                composed.Save(output);
                var layout = ClassicScreenPreview.Review(composed, documents.Values.SelectMany(d => d.Descendants("MultilingualTextItem")).Select(m => ClassicScreenPreflight.Attribute(m,"Culture")));
                string preview = Path.ChangeExtension(output, ".html"), review = Path.ChangeExtension(output, ".layout.json");
                File.WriteAllText(preview, ClassicScreenPreview.Html(layout)); File.WriteAllText(review, layout.ToString());
                return new JObject { ["xmlArtifact"] = ChangeArtifact(output), ["screenPath"] = targetPath,
                    ["expectedContentHash"] = manifest["contentHash"].DeepClone(), ["preflight"] = check,
                    ["previewArtifact"] = ChangeArtifact(preview), ["layoutArtifact"] = ChangeArtifact(review), ["layoutWarningCount"] = layout["warnings"].Count(),
                    ["nativeMutationPerformed"] = false, ["nextStep"] = "Prepare hmi_classic_screen_edit with this XML and the capture hash; preparation revalidates the native state." };
            }
            catch (ArgumentException error) { throw new BridgeException("HmiPreflightFailed", error.Message); }
        }

        private readonly ClassicScreenDocumentCache _classicDocuments = new ClassicScreenDocumentCache();
        private Dictionary<string, XDocument> ReadClassicScreenDocuments(string directory, JObject manifest)
            => ((JObject)manifest["artifacts"]).Properties().ToDictionary(p => p.Name,
                p => _classicDocuments.Read(p.Value.Value<string>("sha256"), File.ReadAllBytes(RequireScreenRecoveryFile(directory, (JObject)p.Value))), StringComparer.Ordinal);

        private static HashSet<string> ReadClassicTagEvidence(string directory, JArray artifacts)
            => ClassicScreenPreflight.NativeTags(artifacts.OfType<JObject>().Select(a => HmiPreservationRules.ReadXml(File.ReadAllText(RequireScreenRecoveryFile(directory, a)))));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JArray CaptureClassicTagEvidence(Project project, JObject identity, string directory, ISet<string> requiredReferences = null)
        {
            var target = ResolveHmiTarget(project, identity);
            var hmi = (Siemens.Engineering.Hmi.HmiTarget)target.Software;
            var artifacts = new JArray();
            var capture = new HmiPreservationCapture(directory, project.Path.FullName, "classic", target.Path, "tag-preflight");
            var files = new JObject();
            capture.Capture(() => {
                foreach (var table in ClassicTagTables(hmi.TagFolder.TagTables, hmi.TagFolder.Folders, "/tagTables", 0))
                {
                    // Native tag names select tables; the exported member paths remain
                    // the binding authority. Unmatched references still fail preflight.
                    if (requiredReferences != null && !table.Item2.Tags.Any(t => requiredReferences.Any(r => ClassicScreenPreflight.UsesRoot(r, t.Name)))) continue;
                    CaptureHmiArtifact(directory, target.Path + table.Item1, files, capture, f => table.Item2.Export(f, ExportOptions.WithDefaults));
                }
            }, () => files.Count);
            if (capture.Failure != null) throw new BridgeException("HmiCaptureIncomplete", "Native tag evidence could not be captured completely.", capture.Failure);
            foreach (var file in files.Properties()) artifacts.Add(file.Value.DeepClone());
            return artifacts;
        }

        private void PreflightClassicScreenBatch(EngineeringChange change, Project project)
        {
            var originals = ReadClassicScreenDocuments(change.Directory, change.HmiBeforeManifest);
            var candidates = change.HmiScreenMembers.Select(m => HmiPreservationRules.ReadXml(File.ReadAllText(m.InputPath))).ToArray();
            var inherited = new HashSet<string>(originals.Values.SelectMany(ClassicScreenPreflight.References), StringComparer.Ordinal);
            var tags = new HashSet<string>(StringComparer.Ordinal);
            if (candidates.SelectMany(ClassicScreenPreflight.References).Any(r => !inherited.Contains(r)))
            {
                string directory = Path.Combine(change.Directory, "tag-preflight"); Directory.CreateDirectory(directory);
                var needed = new HashSet<string>(candidates.SelectMany(ClassicScreenPreflight.References).Where(r => !inherited.Contains(r)), StringComparer.Ordinal);
                var artifacts = MeasureChange(change, "preflight-tags", () => CaptureClassicTagEvidence(project, (JObject)change.Payload["target"], directory, needed));
                change.HmiEvidence["tagArtifacts"] = artifacts;
                tags = ReadClassicTagEvidence(change.Directory, artifacts);
            }
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    string path = change.HmiScreenMembers[i].Payload["target"].Value<string>("objectPath");
                    change.HmiEvidence["screens"][i]["preflight"] = ClassicScreenPreflight.Check(candidates[i], originals[path], originals.Values, tags);
                }
            }
            catch (ArgumentException error) { throw new BridgeException("HmiPreflightFailed", error.Message, change.HmiEvidence); }
        }
    }
}
