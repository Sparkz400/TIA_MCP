using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaMcp.Openness.V21
{
    // All calls run on the owning worker's STA. A local startup argument enables
    // qualification fixtures; normal sessions require fixed installation admission.
    internal sealed partial class OpennessSession
    {
        private IDisposable _translationLease;
        private string _translationToken, _translationEpoch = Guid.NewGuid().ToString("N");
        private JObject _translationContext;
        private Project _translationProject;
        private bool _translationImportAttempted, _translationArchiveAttempted, _translationRestoreAttempted;
        private bool _translationQualification, _translationSourceDriftAttempted;

        private void TranslationQualified(bool fixtureOnly = false)
        {
            if (_translationQualification) return;
            if (fixtureOnly) throw new BridgeException("TranslationImportProfileUnqualified", "Native fixture qualification requires explicit local qualification mode.");
            TranslationInstalledEntry(null);
        }
        private static void TranslationWrite(string path, JToken value)
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { byte[] bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.None)); file.Write(bytes, 0, bytes.Length); file.Flush(true); }
        }
        private string TranslationDirectory(JObject args, string name)
        {
            string root = Path.GetFullPath(args.Value<string>("artifactDirectory") ?? args.Value<string>("artifactRoot"));
            if (!ProjectPathIsAbsolute(root)) throw new BridgeException("InvalidArtifact", "Absolute artifact root required.");
            string path = Path.Combine(root, "translation-native", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        private Project TranslationOwned(JObject args, bool saved)
        {
            EnsureAttached(); var project = SelectProject(args);
            if (!_ownsPortal || _portal.Projects.Count != 1 ||
                (_translationQualification ? !WatchOwnsScratch(project) : TranslationInstalledEntry(args.Value<string>("projectPath")) == null) ||
                project.Path.FullName != args.Value<string>("projectPath") || (saved && project.IsModified))
                throw new BridgeException("ProjectIsolationRequired", "Exact saved worker-owned translation fixture required.");
            EnsureProjectOffline(project); if (!_translationQualification) TranslationProductionScope(args, null);
            var budget = new ScanBudget(10000);
            var owners = DeviceItems(project, budget).Select(x => new { x.Path, Software = x.Item.GetService<SoftwareContainer>()?.Software }).Where(x => x.Software != null)
                .Select(x => x.Software is PlcSoftware ? x.Path + "/plc/" + Segment(x.Software.Name) : null).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            // Whole-project import reaches every owner, so all must be named; only PLC compile and peers are covered.
            if (budget.Truncated || !owners.SequenceEqual(TranslationOwners(args).OrderBy(x => x, StringComparer.Ordinal)))
                throw new BridgeException("TranslationScopeUnqualified", "Every software owner must be an explicitly named PLC (plcPaths); HMI and other owners are not admitted.");
            return project;
        }
        private static string[] TranslationOwners(JObject args)
        {
            string plc = args.Value<string>("plcPath");
            var owners = (args["plcPaths"] as JArray)?.Select(x => x.Type == JTokenType.String ? x.Value<string>() : null).ToArray() ?? new[] { plc };
            if (owners.Length == 0 || owners.Length > 32 || owners.Any(string.IsNullOrWhiteSpace) || owners.Distinct(StringComparer.Ordinal).Count() != owners.Length || plc != null && !owners.Contains(plc))
                throw new BridgeException("TranslationScopeUnqualified", "One to 32 distinct exact PLC owner paths, including plcPath, are required.");
            return owners;
        }
        private static string[] TranslationCultures(JObject args) =>
            new[] { args.Value<string>("sourceCulture") ?? "de-DE", args.Value<string>("targetCulture") ?? "en-GB" };
        // The installed admission contract is whole-project de-DE/en-GB, one PLC, target-only; generalized selectors stay qualification-only.
        private static void TranslationProductionScope(JObject selector, JObject context)
        {
            if (TranslationOwners(selector).Length != 1 || !TranslationCultures(selector).SequenceEqual(new[] { "de-DE", "en-GB" }) ||
                context?.Value<bool?>("updateSourceLanguage") == true)
                throw new BridgeException("TranslationAdmissionMismatch", "Production admission covers only one PLC, de-DE/en-GB and target-only import.");
        }
        private JObject TranslationFixture(JObject args)
        {
            TranslationQualified(true); var project = TranslationOwned(args, true);
            string directory = TranslationDirectory(args, "fixture");
            // A durable per-project marker forbids repeated seeding, even after a lost reply.
            string marker = Path.Combine(project.Path.DirectoryName, "translation-seed-intent.json");
            using (_portal.ExclusiveAccess("TIA MCP: disposable bilingual fixture"))
            {
                if (File.Exists(marker)) throw new BridgeException("RecoveryRequired", "Fixture seed already attempted.");
                project.Archive(new DirectoryInfo(directory), "before-seed.zap21", ProjectArchivationMode.Compressed);
                TranslationWrite(marker, new JObject { ["state"] = "dispatchIntent", ["projectPath"] = project.Path.FullName,
                    ["checkpoint"] = FileArtifact(new FileInfo(Path.Combine(directory, "before-seed.zap21"))), ["automaticReplay"] = false });
                var de = project.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo("de-DE"));
                var en = project.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo("en-GB"));
                if (project.LanguageSettings.ReferenceLanguage.Culture.Name != "de-DE") throw new BridgeException("TranslationScopeUnqualified", "German reference language required.");
                if (!project.LanguageSettings.ActiveLanguages.Contains(en)) project.LanguageSettings.ActiveLanguages.Add(en);
                var plc = SelectPlc(args).Software;
                foreach (string name in new[] { "Main", "TiaMcp_IsEnabled", "TiaMcp_EnableGate" })
                {
                    var block = plc.BlockGroup.Blocks.Find(name);
                    block.Comment.Items.Find(de).Text = "Prueftext " + name + " {0}";
                    block.Comment.Items.Find(en).Text = "Verification text " + name + " {0}";
                }
                var compile = ChangeCompilation(plc.GetService<ICompilable>().Compile(), args.Value<string>("plcPath"));
                TranslationWrite(Path.Combine(directory, "compile.json"), compile);
                if (!CompilationEvidenceRules.IsSuccessfulTarget(compile)) throw new BridgeException("CompileFailed", "Fixture compile failed.");
                project.Save(); string path = project.Path.FullName; project.Close();
                project = _portal.Projects.Open(new FileInfo(path)); _scratchProject = project;
                _watchOwnedScratchPaths.Add(path); _translationEpoch = Guid.NewGuid().ToString("N");
                var result = new JObject { ["completed"] = true, ["projectPath"] = path, ["seedMarker"] = FileArtifact(new FileInfo(marker)),
                    ["checkpoint"] = FileArtifact(new FileInfo(Path.Combine(directory, "before-seed.zap21"))), ["savedAndReopened"] = true };
                TranslationWrite(Path.Combine(directory, "result.json"), result); return result;
            }
        }
        private JObject TranslationCapture(JObject args)
        {
            TranslationQualified(); var project = TranslationOwned(args, true);
            using (_portal.ExclusiveAccess("TIA MCP: complete translation capture")) return TranslationObserve(project, args);
        }
        private JObject TranslationCategories(JObject args)
        {
            TranslationQualified(true); var project = TranslationOwned(args, true);
            var plc = SelectPlc(args).Software;
            var budget = new ScanBudget(10000);
            var owner = DeviceItems(project, budget).Single(x => Equals(x.Item.GetService<SoftwareContainer>()?.Software, plc)).Item;
            string directory = TranslationDirectory(args, "categories");
            using (_portal.ExclusiveAccess("TIA MCP: populated translation category control"))
            {
                string populated = Path.Combine(directory, "populated"); Directory.CreateDirectory(populated);
                var main = TranslationPopulatedCategoryProbe.Run(project, plc, owner, populated);
                string matrix = Path.Combine(directory, "matrix"); Directory.CreateDirectory(matrix);
                var all = TranslationCategoryProbe.Run(project, plc, owner, matrix);
                return new JObject { ["populated"] = main, ["matrix"] = all, ["directory"] = directory, ["projectModified"] = project.IsModified };
            }
        }
        private JObject TranslationObserve(Project project, JObject args)
        {
            EnsureProjectOffline(project);
            string directory = TranslationDirectory(args, "observe");
            var settings = project.LanguageSettings; var pair = TranslationCultures(args);
            var cultures = settings.ActiveLanguages.Select(x => x.Culture.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            // A bilingual export cannot observe a third language, so exactly the pair must be active.
            if (pair[0] == pair[1] || !cultures.SequenceEqual(pair.OrderBy(x => x, StringComparer.Ordinal)) || settings.ReferenceLanguage.Culture.Name != pair[0])
                throw new BridgeException("TranslationScopeUnqualified", "Exactly the source/target pair must be active, with the source as reference language.");
            string workbook = Path.Combine(directory, "all.xlsx");
            // This profile is explicitly whole-project. Restricted overloads are never substituted.
            project.ExportProjectTexts(new FileInfo(workbook), CultureInfo.GetCultureInfo(pair[0]), CultureInfo.GetCultureInfo(pair[1]));
            var owners = TranslationOwners(args);
            var peers = new JArray(); var receipts = new JArray(); JToken hardware = null;
            for (int index = 0; index < owners.Length; index++)
            {
                var selector = (JObject)args.DeepClone(); selector["projectPath"] = project.Path.FullName; selector["plcPath"] = owners[index];
                selector["artifactDirectory"] = directory; selector["tableName"] = "__translation_absent_control__";
                selector["group"] = "root"; selector["allowAbsent"] = true;
                var snapshot = WatchSnapshot(selector);
                string snapshotPath = snapshot["receipt"].Value<string>("path"), prefix = owners.Length == 1 ? "" : owners[index] + "|";
                foreach (string path in Directory.GetFiles(Path.GetDirectoryName(snapshotPath), "*.xml").OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal))
                    peers.Add(new JObject { ["identity"] = prefix + Path.GetFileName(path), ["profile"] = "tia21-simaticml-translation-v1", ["artifact"] = FileArtifact(new FileInfo(path)) });
                var manifest = (JObject)snapshot["peers"].DeepClone();
                foreach (var property in manifest.Properties().Where(x => x.Name.StartsWith("block:") || x.Name.StartsWith("type:") || x.Name.StartsWith("tags:")).ToArray()) property.Remove();
                string manifestPath = Path.Combine(directory, owners.Length == 1 ? "native-inventory.json" : "native-inventory-" + index + ".json"); TranslationWrite(manifestPath, manifest);
                peers.Add(new JObject { ["identity"] = prefix + "native-inventory", ["profile"] = "exact", ["artifact"] = FileArtifact(new FileInfo(manifestPath)) });
                hardware = hardware ?? snapshot["observation"]["hardwareIdentity"].DeepClone(); receipts.Add(snapshot["receipt"].DeepClone());
            }
            var result = new JObject { ["workbook"] = FileArtifact(new FileInfo(workbook)), ["peers"] = peers,
                ["projectPath"] = project.Path.FullName, ["projectName"] = project.Name, ["plcPath"] = args["plcPath"], ["plcPaths"] = new JArray(owners),
                ["sessionEpoch"] = _translationEpoch, ["workerSha256"] = ChangeFileHash(typeof(OpennessSession).Assembly.Location),
                ["hardwareIdentity"] = hardware, ["offline"] = true,
                ["activeCultures"] = new JArray(cultures), ["categoryIds"] = new JArray(project.TextCategories.Select(x => x.Identifier).OrderBy(x => x, StringComparer.Ordinal)),
                ["processId"] = _processId, ["modified"] = project.IsModified, ["nativeSnapshot"] = receipts.Count == 1 ? receipts[0] : receipts };
            TranslationWrite(Path.Combine(directory, "observation.json"), result); return result;
        }
        private JObject TranslationBegin(JObject args)
        {
            TranslationQualified(); EnsureAttached();
            if (_translationLease != null || _watchLease != null || _sharedMutationLease != null || _testSuiteSession != null || !_ownsPortal)
                throw new BridgeException("ResourceBusy", "An owned idle native instance is required.");
            var context = (JObject)args["context"]; var op = (JObject)context["operation"];
            var selector = (JObject)args["selector"]; TranslationCheckRecord(context, op);
            if (!_translationQualification)
            {
                var entry = TranslationInstalledEntry(selector.Value<string>("projectPath"));
                if (entry.Value<string>("plcPath") != selector.Value<string>("plcPath") ||
                    op["plan"]["target"].Value<string>("adapterSha256") != ChangeFileHash(typeof(OpennessSession).Assembly.Location))
                    throw new BridgeException("TranslationAdmissionMismatch", "Installed native selector or owning worker changed.");
                TranslationProductionScope(selector, context);
            }
            if (op.Value<string>("state") == "acquired")
            {
                _translationProject = TranslationOwned(selector, true);
                if (op["plan"]["target"].Value<string>("sessionEpoch") != _translationEpoch)
                    throw new BridgeException("StaleNativeTarget", "Translation native epoch changed.");
            }
            else
            {
                if (_portal.Projects.Count > 1) throw new BridgeException("ProjectIsolationRequired", "Only the admitted fixture may be open.");
                if (_portal.Projects.Count == 1)
                {
                    var opened = _portal.Projects.First();
                    if (opened.Path.FullName != selector.Value<string>("projectPath")) throw new BridgeException("IdentityMismatch", "Unrelated project is open.");
                    opened.Close();
                }
                _translationProject = null;
            }
            _translationLease = _portal.ExclusiveAccess("TIA MCP: durable translation import");
            _translationContext = (JObject)context.DeepClone(); _translationContext["selector"] = selector.DeepClone();
            _translationToken = Guid.NewGuid().ToString("N");
            _translationImportAttempted = _translationArchiveAttempted = _translationRestoreAttempted = _translationSourceDriftAttempted = false;
            return new JObject { ["leaseToken"] = _translationToken, ["operationId"] = op["id"], ["planSha256"] = op["planSha256"] };
        }
        private static void TranslationCheckRecord(JObject context, JObject operation)
        {
            string root = Path.GetFullPath(context.Value<string>("artifactRoot")), id = operation.Value<string>("id");
            if (id == null || id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c))) throw new BridgeException("InvalidOperation", "Exact operation ID required.");
            var files = Directory.GetFiles(Path.Combine(root, "engineering-operations", id), "*.json").OrderBy(x => x, StringComparer.Ordinal).ToArray();
            JObject latest = null;
            if (files.Length != 0)
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(files.Last()))) { DateParseHandling = DateParseHandling.None, MaxDepth = 128 })
                    latest = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (latest == null || !JToken.DeepEquals(latest, operation))
                throw new BridgeException("StaleOperation", "Worker requires the exact latest durable operation revision.");
            if (operation["plan"].Value<string>("operation") != "translation.import")
                throw new BridgeException("InvalidOperation", "Translation operation contract required.");
        }
        private JObject TranslationStage(JObject args)
        {
            if (_translationLease == null || args.Value<string>("leaseToken") != _translationToken)
                throw new BridgeException("InvalidLease", "Exact translation lease required.");
            var op = (JObject)args["operation"]; TranslationCheckRecord(_translationContext, op);
            if (op.Value<string>("id") != _translationContext["operation"].Value<string>("id") ||
                op.Value<string>("planSha256") != _translationContext["operation"].Value<string>("planSha256"))
                throw new BridgeException("IdentityMismatch", "Translation operation changed.");
            var selector = (JObject)_translationContext["selector"];
            string stage = args.Value<string>("stage"), state = op.Value<string>("state");
            var payload = args["payload"] as JObject ?? new JObject();
            string directory = TranslationDirectory(selector, stage);
            JToken value;
            switch (stage)
            {
                case "observe": value = TranslationObserve(_translationProject, selector); break;
                case "sourceDrift":
                    if (!_translationQualification || state != "ready" || _translationSourceDriftAttempted || ((JArray)op["checkpoints"]).Count != 1)
                        throw new BridgeException("TranslationQualificationCaseInvalid", "Source drift requires one retained checkpoint and explicit qualification mode.");
                    var sourceBlock = SelectPlc(selector).Software.BlockGroup.Blocks.Find("Main");
                    var sourceItem = sourceBlock.Comment.Items.Find(_translationProject.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo("de-DE")));
                    string beforeSource = sourceItem.Text;
                    if (beforeSource != "Prueftext Main {0}") throw new BridgeException("TranslationQualificationCaseInvalid", "Exact seeded German Main source required.");
                    _translationSourceDriftAttempted = true;
                    var sourceIntent = new JObject { ["operationId"] = op["id"], ["planSha256"] = op["planSha256"],
                        ["before"] = beforeSource, ["after"] = "Qualification source drift Main {0}", ["checkpoint"] = op["checkpoints"][0].DeepClone(), ["automaticReplay"] = false };
                    TranslationWrite(Path.Combine(directory, "source-drift-intent.json"), sourceIntent);
                    sourceItem.Text = sourceIntent.Value<string>("after");
                    value = new JObject { ["intent"] = FileArtifact(new FileInfo(Path.Combine(directory, "source-drift-intent.json"))),
                        ["observedSource"] = sourceItem.Text, ["saved"] = false }; break;
                case "checkpoint":
                    if (state != "checkpointIntent" || _translationArchiveAttempted) throw new BridgeException("RecoveryRequired", "Checkpoint requires one durable intent.");
                    _translationArchiveAttempted = true;
                    _translationProject.Archive(new DirectoryInfo(directory), "checkpoint.zap21", ProjectArchivationMode.Compressed);
                    value = FileArtifact(new FileInfo(Path.Combine(directory, "checkpoint.zap21"))); break;
                case "import":
                    if (state != "dispatchIntent" || _translationImportAttempted) throw new BridgeException("RecoveryRequired", "Import requires one durable dispatch intent.");
                    string candidate = payload.Value<string>("path");
                    if (ChangeFileHash(candidate) != _translationContext.Value<string>("candidateSha256")) throw new BridgeException("ArtifactChanged", "Candidate bytes differ.");
                    _translationImportAttempted = true;
                    // Source-text mode comes only from the approved review bound into the operation context.
                    bool updateSource = _translationContext.Value<bool?>("updateSourceLanguage") == true;
                    using (var held = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var returned = _translationProject.ImportProjectTexts(new FileInfo(candidate), updateSource);
                        var raw = new JObject { ["state"] = returned.State.ToString(), ["path"] = returned.Path.FullName,
                            ["candidateSha256"] = ChangeFileHash(candidate), ["updateSourceLanguage"] = updateSource, ["operationId"] = op["id"], ["planSha256"] = op["planSha256"] };
                        TranslationWrite(Path.Combine(directory, "native-return.json"), raw);
                        if (!File.Exists(returned.Path.FullName)) throw new BridgeException("TranslationLogMissing", "Native return retained; complete native log is absent.");
                        File.Copy(returned.Path.FullName, Path.Combine(directory, "complete-log"));
                        value = new JObject { ["raw"] = FileArtifact(new FileInfo(Path.Combine(directory, "native-return.json"))),
                            ["log"] = FileArtifact(new FileInfo(Path.Combine(directory, "complete-log"))) };
                    }
                    break;
                case "compile":
                    var compilations = new JArray(TranslationOwners(selector).Select(path => {
                        var owner = (JObject)selector.DeepClone(); owner["plcPath"] = path;
                        return ChangeCompilation(SelectPlc(owner).Software.GetService<ICompilable>().Compile(), path); }));
                    value = new JObject { ["passed"] = compilations.All(c => CompilationEvidenceRules.IsSuccessfulTarget((JObject)c)), ["compilations"] = compilations };
                    break;
                case "save": _translationProject.Save(); value = new JObject { ["saved"] = true, ["modified"] = _translationProject.IsModified }; break;
                case "reopen":
                if (_translationProject != null) { _translationProject.Close(); _scratchProject = null; }
                    _translationProject = _portal.Projects.Open(new FileInfo(selector.Value<string>("projectPath")));
                    _scratchProject = _translationProject; _translationEpoch = Guid.NewGuid().ToString("N");
                    _watchOwnedScratchPaths.Add(_translationProject.Path.FullName);
                    value = TranslationObserve(_translationProject, selector); break;
                case "restore":
                    if (state != "recoveryIntent" || _translationRestoreAttempted || _portal.Projects.Count != 0)
                        throw new BridgeException("RecoveryRequired", "Restoration requires one intent and an empty owned instance.");
                    string destination = payload.Value<string>("destination"), archive = payload.Value<string>("archive");
                    if (!((JArray)op["recoveryDestinations"]).Any(d => d.Value<string>("recoveryLocation") == destination) ||
                        File.Exists(destination) || Directory.Exists(Path.GetDirectoryName(destination)) || ChangeFileHash(archive) != payload.Value<string>("sha256"))
                        throw new BridgeException("InvalidRecovery", "Exact reserved absent destination and original archive required.");
                    _translationRestoreAttempted = true;
                    string container = Path.GetDirectoryName(Path.GetDirectoryName(destination));
                    Directory.CreateDirectory(container);
                    var restored = _portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(container));
                    value = new JObject { ["projectPath"] = restored.Path.FullName, ["expectedPath"] = destination, ["oneRetrieve"] = true };
                    TranslationWrite(Path.Combine(directory, "restore-return.json"), value); restored.Close();
                    if (value.Value<string>("projectPath") != destination) throw new BridgeException("RecoveryPathMismatch", "Returned restoration location differs; inspect without replay.");
                    break;
                case "recovered":
                    string recovered = payload.Value<string>("destination");
                    if (!((JArray)op["recoveryDestinations"]).Any(d => d.Value<string>("recoveryLocation") == recovered)) throw new BridgeException("InvalidRecovery", "Unreserved recovery path.");
                    if (_translationProject != null) { _translationProject.Close(); _translationProject = null; _scratchProject = null; }
                    if (!File.Exists(recovered))
                    {
                        if (Directory.Exists(Path.GetDirectoryName(recovered))) throw new BridgeException("RecoveryIncomplete", "Partial recovery directory exists.");
                        value = new JObject { ["absent"] = true, ["path"] = recovered }; break;
                    }
                    var copy = _portal.Projects.Open(new FileInfo(recovered)); _translationEpoch = Guid.NewGuid().ToString("N");
                    try { value = TranslationObserve(copy, selector); } finally { copy.Close(); }
                    break;
                default: throw new BridgeException("InvalidRequest", "Unknown translation stage.");
            }
            TranslationWrite(Path.Combine(directory, "return.json"), value);
            return new JObject { ["value"] = value, ["receipt"] = FileArtifact(new FileInfo(Path.Combine(directory, "return.json"))) };
        }
        private JObject TranslationEnd(JObject args)
        {
            if (_translationLease == null || args.Value<string>("leaseToken") != _translationToken) throw new BridgeException("InvalidLease", "Exact translation end required.");
            string id = _translationContext["operation"].Value<string>("id"); TranslationRelease();
            return new JObject { ["released"] = true, ["operationId"] = id };
        }
        private void TranslationRelease()
        {
            if (_translationLease == null) return;
            _translationLease.Dispose(); _translationLease = null; _translationToken = null; _translationContext = null;
            _translationProject = null;
        }
    }
}
