using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // --hmi-text-list-qualification <profile>: admits that profile only in a project this worker created or retrieved.
        internal string HmiTextListQualificationProfile { get; set; }
        private readonly HashSet<string> _retrievedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string TextListQualificationProfile(Project project) => OwnedQualificationProfile(HmiTextListQualificationProfile, project);
        // A qualification profile applies only in a worker-owned Portal, to a project this worker created or retrieved.
        private string OwnedQualificationProfile(string profile, Project project) => profile != null && _ownsPortal &&
            (WatchOwnsScratch(project) || _retrievedProjectPaths.Contains(project.Path.FullName)) ? profile : null;

        // Change kind hmi_text_list_import: a Host-resolved hmi_text_list_author/generate_set candidate pair imported through
        // HmiTextListComposition.Import, the route the independent seeder qualified (local.hmi.text-list-import.20261029).
        // The gate is open for this kind only; screens and every other HMI write stay behind the closed preservation gate.
        private JObject PrepareHmiTextListImportMutation(EngineeringChange change, Project project)
        {
            var payload = change.Payload;
            try { HmiTextListImportRules.RequireRequest(payload); }
            catch (ArgumentException ex) { throw new BridgeException("InvalidRequest", ex.Message); }
            var identity = (JObject)payload["target"];
            var discovered = HmiResolveNativeTarget(project, identity);
            if (discovered.Family != "unified") throw new BridgeException("HmiTargetMismatch", "hmi_text_list_import requires a Unified HMI.");
            var gate = HmiTextListImportRules.Gate(discovered.Family, discovered.Profile, TextListQualificationProfile(project));
            gate["catalogIdentifier"] = discovered.CatalogIdentifier;
            gate["runtimeVersion"] = discovered.RuntimeVersion;
            var candidate = (JObject)payload["candidate"];
            change.HmiEvidence = new JObject { ["schemaVersion"] = 1, ["target"] = identity.DeepClone(), ["operation"] = HmiTextListImportRules.Operation, ["gate"] = gate,
                ["candidate"] = new JObject { ["recordId"] = candidate["recordId"]?.DeepClone(), ["recordKind"] = candidate["recordKind"]?.DeepClone(), ["outputId"] = candidate["outputId"]?.DeepClone(),
                    ["lists"] = candidate["lists"].DeepClone(), ["files"] = new JArray(((JArray)candidate["files"]).Select(f => new JObject { ["fileName"] = f["fileName"].DeepClone(), ["sha256"] = f["sha256"].DeepClone() })) },
                ["runtimeVerified"] = false, ["automaticRetryAllowed"] = false };
            change.HmiBeforeManifest = CaptureHmiEngineeringScope(change, project, "before");
            change.HmiEvidence["baseline"] = change.HmiBeforeManifest.DeepClone();
            RequireHmiEngineeringGate(gate, change.HmiEvidence);
            RequireHmiScope(change, change.HmiBeforeManifest);
            if (change.HmiBeforeManifest.Value<string>("contentHash") != payload.Value<string>("expectedContentHash"))
                throw new BridgeException("StaleRevision", "HMI native content differs from expectedContentHash; prepare again with hmiEvidence.baseline.contentHash after reviewing it.", change.HmiEvidence);
            // V21 exports every text in every active project language; another language set cannot read back exactly.
            var active = project.LanguageSettings.ActiveLanguages.Select(l => l.Culture.Name).ToArray();
            var languages = ((JArray)candidate["languages"]).Values<string>().ToArray();
            if (!new HashSet<string>(active, StringComparer.Ordinal).SetEquals(languages) || languages.Length != active.Length)
                throw new BridgeException("HmiTextListLanguageMismatch", "The candidate's languages must be exactly the project's active languages.",
                    new JObject { ["candidate"] = new JArray(languages), ["active"] = new JArray(active) });
            string directory = Path.Combine(change.Directory, "text-list-candidate");
            Directory.CreateDirectory(directory);
            var copied = new List<string>();
            foreach (JObject file in (JArray)candidate["files"])
            {
                string destination = Path.Combine(directory, file.Value<string>("fileName"));
                File.Copy(file.Value<string>("path"), destination);
                if (ChangeFileHash(destination) != file.Value<string>("sha256") || new FileInfo(destination).Length != file.Value<long>("bytes"))
                    throw new BridgeException("ArtifactChanged", "The candidate file differs from its retained record: " + file.Value<string>("fileName"));
                copied.Add(destination);
            }
            change.InputPath = copied[0];
            change.InputHash = ChangeFileHash(copied[0]);
            change.Backups.Add(new JObject { ["role"] = "hmi-text-list-candidate-library", ["artifact"] = ChangeArtifact(copied[1]) });
            SortedDictionary<string, string> expected;
            try { expected = HmiTextListImportRules.Resolve(ReadTextListFile(copied[0]), ReadTextListFile(copied[1])); }
            catch (FormatException ex) { throw new BridgeException("HmiTextListCandidateUnsupported", ex.Message); }
            if (!new HashSet<string>(expected.Keys, StringComparer.Ordinal).SetEquals(((JArray)candidate["lists"]).Values<string>()))
                throw new BridgeException("HmiTextListCandidateUnsupported", "The candidate files do not declare exactly the record's lists.");
            var before = ExportTextLists(change, ResolveHmiTarget(project, identity), "before");
            if (!HmiTextListImportRules.ExportCoversTypedLists(((JObject)change.HmiBeforeManifest["artifacts"]).Properties().Select(p => p.Name), discovered.Path, before.Keys))
                throw new BridgeException("HmiTextListExportIncomplete", "The native text-list export does not hold every text list of the HMI (V21 skips lists with formatted text); an unexported list could change unseen.", change.HmiEvidence);
            var effect = new JObject { ["operation"] = HmiTextListImportRules.Operation, ["hmiPath"] = discovered.Path, ["configuration"] = Path.GetFileName(copied[0]),
                ["created"] = new JArray(expected.Keys.Where(k => !before.ContainsKey(k)).ToArray()), ["replaced"] = new JArray(expected.Keys.Where(before.ContainsKey).ToArray()),
                ["unchangedPeers"] = new JArray(before.Keys.Where(k => !expected.ContainsKey(k)).ToArray()), ["recordId"] = candidate["recordId"].DeepClone(), ["outputId"] = candidate["outputId"]?.DeepClone() };
            change.HmiEvidence["proposedEffect"] = effect;
            var warnings = new JArray("Native compile does not qualify HMI runtime behavior.");
            if (((JArray)effect["replaced"]).Count > 0) warnings.Add("Import replaces the listed existing text lists entirely; the checkpoint archive is the only way back. Renames and deletions of other lists are never performed.");
            return new JObject { ["effects"] = new JArray(effect), ["warnings"] = warnings, ["hmiEvidence"] = change.HmiEvidence.DeepClone(), ["compileRequired"] = true, ["saveRequired"] = true,
                ["preservationPolicy"] = "Imported lists must read back exactly as the candidate and every other text list unchanged (meaning of the export: entries with defaults applied, texts per language); every other captured HMI artifact byte- or typed-readback-identical; the HMI device's compiler errors and warnings unchanged by the import." };
        }

        private void ApplyHmiTextListImportMutation(EngineeringChange change, Project project)
        {
            var identity = (JObject)change.Payload["target"];
            var discovered = HmiResolveNativeTarget(project, identity);
            RequireHmiEngineeringGate(HmiTextListImportRules.Gate(discovered.Family, discovered.Profile, TextListQualificationProfile(project)), change.HmiEvidence);
            RequireHmiScope(change, change.HmiBeforeManifest);
            var target = ResolveHmiTarget(project, identity);
            // ValidatePreparedFiles re-hashed both candidate files before the checkpoint.
            string library = change.Backups.OfType<JObject>().Single(b => b.Value<string>("role") == "hmi-text-list-candidate-library")["artifact"].Value<string>("path");
            var expected = HmiTextListImportRules.Resolve(ReadTextListFile(change.InputPath), ReadTextListFile(library));
            // The compile before the import is the reference for "unchanged compiler problems".
            change.HmiEvidence["baselineCompile"] = CompileHmiTextListTarget(project, (JObject)change.Payload["target"]);
            // Preservation compares with the compiled state, so compiler effects are never attributed to the import.
            var compiled = CaptureHmiEngineeringScope(change, project, "pre-import");
            RequireHmiScope(change, compiled);
            change.HmiEvidence["preImport"] = compiled;
            change.HmiEvidence["baselineCompileChangedCapture"] = compiled.Value<string>("contentHash") != change.HmiBeforeManifest.Value<string>("contentHash");
            change.Stages.Add("hmi_baseline_compiled");
            WriteChangeJournal(change);
            var before = ExportTextLists(change, target, "pre-import");
            change.Stages.Add("hmi_text_list_import_started");
            WriteChangeJournal(change);
            bool accepted = ImportTextLists(target, Path.GetDirectoryName(change.InputPath), Path.GetFileName(change.InputPath));
            change.HmiEvidence["importAccepted"] = accepted;
            change.Stages.Add("hmi_text_list_import_returned");
            WriteChangeJournal(change);
            if (!accepted) throw new BridgeException("HmiTextListImportRefused", "HmiTextListComposition.Import reported failure.", change.HmiEvidence);
            var after = ExportTextLists(change, target, "after");
            var evaluation = HmiTextListImportRules.Evaluate(before, after, expected);
            change.HmiEvidence["importReadback"] = evaluation;
            if (!evaluation.Value<bool>("exact")) throw new BridgeException("PreservationMismatch", "Imported text lists do not read back exactly, or another text list changed.", evaluation);
            change.Stages.Add("hmi_text_lists_read_back_exactly");
        }

        private JObject CompileHmiTextListTarget(Project project, JObject identity)
        {
            var selected = ResolveHmiTarget(project, identity);
            var result = LibraryHmiCompiler(selected).Compile();
            return new JObject { ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount,
                ["targets"] = new JArray(ChangeCompilation(result, selected.DevicePath)), ["compileScope"] = "owningDeviceHardwareAndSoftware", ["runtimeVerified"] = false };
        }

        // Text lists by name (meaning of the native export); the export files are retained as readbacks.
        private SortedDictionary<string, string> ExportTextLists(EngineeringChange change, HmiTargetSelection target, string stage)
        {
            string directory = Path.Combine(change.Directory, "hmi-text-lists-" + stage + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var files = ExportUnifiedTextLists(target.Software, directory);
            foreach (string path in files) change.Readbacks.Add(new JObject { ["role"] = "hmi-text-lists-" + stage, ["artifact"] = ChangeArtifact(path) });
            if (files.Length == 0) return new SortedDictionary<string, string>(StringComparer.Ordinal);
            try { return HmiTextListImportRules.Resolve(ReadTextListFile(files[0]), ReadTextListFile(files[1])); }
            catch (FormatException ex) { throw new BridgeException("HmiTextListExportUnsupported", "The native text-list export uses YAML outside the qualified subset: " + ex.Message); }
        }

        // Returns the configuration file and its TextLibrary file, or nothing when the HMI has no text lists.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string[] ExportUnifiedTextLists(Siemens.Engineering.HW.Software software, string directory)
        {
            var hmi = (Siemens.Engineering.HmiUnified.HmiSoftware)software;
            if (hmi.HmiTextLists.Count == 0) return new string[0];
            hmi.HmiTextLists.Export(new DirectoryInfo(directory), "TextLists");
            var files = Directory.GetFiles(directory).Select(Path.GetFileName).ToArray();
            string configuration = Path.Combine(directory, "TextLists.hmi.yml"), library = Path.Combine(directory, "TextLists.TextLibrary.hmi.yml");
            if (files.Length != 2 || !files.Contains("TextLists.hmi.yml") || !files.Contains("TextLists.TextLibrary.hmi.yml") || Directory.GetDirectories(directory).Length != 0)
                throw new BridgeException("HmiTextListExportUnsupported", "The native text-list export did not produce exactly its configuration and TextLibrary files.");
            return new[] { configuration, library };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ImportTextLists(HmiTargetSelection target, string directory, string configuration)
            => ((Siemens.Engineering.HmiUnified.HmiSoftware)target.Software).HmiTextLists.Import(new DirectoryInfo(directory), configuration);

        private static string ReadTextListFile(string path)
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new BridgeException("TraversalLimit", "A text-list file exceeds 32 MiB.");
            try { return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)); }
            catch (DecoderFallbackException) { throw new BridgeException("HmiTextListExportUnsupported", "A text-list file is not UTF-8: " + Path.GetFileName(path)); }
        }
    }
}
