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
                manifest["tagArtifacts"] = args.Value<bool?>("includeTags") == false ? new JArray() : CaptureClassicTagEvidence(project, identity, change.Directory);
                string path = Path.Combine(change.Directory, "capture.json");
                File.WriteAllText(path, manifest.ToString());
                return new JObject { ["contentHash"] = manifest["contentHash"].DeepClone(), ["capture"] = ChangeArtifact(path),
                    ["identity"] = manifest["identity"].DeepClone(), ["screens"] = manifest["artifacts"].DeepClone(),
                    ["scopePolicy"] = HmiPreservationRules.ClassicScreenPolicy, ["nativeMutationPerformed"] = false,
                    ["runtimeVerified"] = false };
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
                return new JObject { ["xmlArtifact"] = ChangeArtifact(output), ["screenPath"] = targetPath,
                    ["expectedContentHash"] = manifest["contentHash"].DeepClone(), ["preflight"] = check,
                    ["nativeMutationPerformed"] = false, ["nextStep"] = "Prepare hmi_classic_screen_edit with this XML and the capture hash; preparation revalidates the native state." };
            }
            catch (ArgumentException error) { throw new BridgeException("HmiPreflightFailed", error.Message); }
        }

        private static Dictionary<string, XDocument> ReadClassicScreenDocuments(string directory, JObject manifest)
            => ((JObject)manifest["artifacts"]).Properties().ToDictionary(p => p.Name,
                p => HmiPreservationRules.ReadXml(File.ReadAllText(RequireScreenRecoveryFile(directory, (JObject)p.Value))), StringComparer.Ordinal);

        private static HashSet<string> ReadClassicTagEvidence(string directory, JArray artifacts)
            => ClassicScreenPreflight.NativeTags(artifacts.OfType<JObject>().Select(a => HmiPreservationRules.ReadXml(File.ReadAllText(RequireScreenRecoveryFile(directory, a)))));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JArray CaptureClassicTagEvidence(Project project, JObject identity, string directory)
        {
            var target = ResolveHmiTarget(project, identity);
            var hmi = (Siemens.Engineering.Hmi.HmiTarget)target.Software;
            var artifacts = new JArray();
            var capture = new HmiPreservationCapture(directory, project.Path.FullName, "classic", target.Path, "tag-preflight");
            var files = new JObject();
            capture.Capture(() => {
                foreach (var table in ClassicTagTables(hmi.TagFolder.TagTables, hmi.TagFolder.Folders, "/tagTables", 0))
                    CaptureHmiArtifact(directory, target.Path + table.Item1, files, capture, f => table.Item2.Export(f, ExportOptions.WithDefaults));
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
                var artifacts = CaptureClassicTagEvidence(project, (JObject)change.Payload["target"], directory);
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
