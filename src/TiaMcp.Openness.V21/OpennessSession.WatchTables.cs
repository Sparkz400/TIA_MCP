using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcp.Core.WatchTables;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private string _watchEpoch = Guid.NewGuid().ToString("N");
        private IDisposable _watchLease;
        private string _watchToken;
        private JObject _watchSelector, _watchBefore;
        private bool _watchDispatched;
        private readonly HashSet<string> _watchOwnedScratchPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private JObject WatchRead(JObject args)
        {
            EnsureAttached();
            using (_portal.ExclusiveAccess("TIA MCP: watch snapshot")) return WatchSnapshot(args);
        }
        private JObject WatchSnapshot(JObject args)
        {
            var selected = SelectPlc(args); EnsureProjectOffline(selected.Project);
            if (args.Value<string>("projectPath") != null && !string.Equals(args.Value<string>("projectPath"), selected.Project.Path.FullName, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("IdentityMismatch", "Exact physical destination project required.");
            string groupPath = args.Value<string>("group") ?? "root", tableName = ChangeText(args, "tableName", 128);
            bool foundGroup = false;
            var directory = Path.Combine(ArtifactDirectory(args), "w_" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(directory);
            var tables = new JArray(); var peers = new JObject();
            PlcWatchTable target = null;
            foreach (var group in WatchGroups(selected.Software.WatchAndForceTableGroup, "root", 0))
            {
                if (group.Item2 == groupPath) foundGroup = true;
                if (peers.Count > 1000 || tables.Count + group.Item1.WatchTables.Count + group.Item1.ForceTables.Count > 1000)
                    throw new BridgeException("TraversalLimit", "Watch table/group count exceeds 1000.");
                foreach (var watch in group.Item1.WatchTables)
                {
                    string path = Path.Combine(directory, "watch-" + tables.Count + ".xml"); watch.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    var entry = new JObject { ["kind"] = "watch", ["exists"] = true, ["group"] = group.Item2, ["name"] = watch.Name, ["artifact"] = FileArtifact(new FileInfo(path)),
                        ["semanticSha256"] = WatchTableDocument.SemanticHash(File.ReadAllBytes(path)), ["isConsistent"] = watch.IsConsistent,
                        ["rows"] = new JArray(watch.Entries.Select(WatchRow)) };
                    tables.Add(entry);
                    if (group.Item2 == (args.Value<string>("group") ?? "root") && watch.Name == args.Value<string>("tableName")) target = watch;
                    else peers["watch:" + group.Item2 + "/" + watch.Name] = entry["semanticSha256"].DeepClone();
                }
                foreach (var force in group.Item1.ForceTables)
                {
                    string path = Path.Combine(directory, "force-" + tables.Count + ".xml"); force.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    string hash = WatchTableDocument.SemanticHash(File.ReadAllBytes(path));
                    tables.Add(new JObject { ["kind"] = "force", ["group"] = group.Item2, ["name"] = force.Name, ["artifact"] = FileArtifact(new FileInfo(path)), ["semanticSha256"] = hash });
                    peers["force:" + group.Item2 + "/" + force.Name] = hash;
                }
                peers["group:" + group.Item2] = group.Item1.Name;
            }
            if (!foundGroup) throw new BridgeException("WatchGroupNotFound", "The exact existing destination group is required.");
            if (target == null && args.Value<bool?>("allowAbsent") != true) throw new BridgeException("WatchTableNotFound", "Exact watch table/group is required.");
            if (target == null && tables.Any(t => string.Equals(t.Value<string>("name"), tableName, StringComparison.OrdinalIgnoreCase)))
                throw new BridgeException("WatchTableCollision", "A watch or force table already occupies this name in the selected PLC.");
            var budget = new ScanBudget(10000);
            var scopes = SoftwareScopes(selected, budget);
            foreach (var scope in scopes)
            {
                foreach (var block in ScopeBlocks(scope, budget))
                {
                    string path = Path.Combine(directory, "block-" + peers.Count + ".xml"); block.Item.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    peers["block:" + block.Path] = WatchXmlHash(path);
                }
                foreach (var type in ScopeTypes(scope, budget))
                {
                    string path = Path.Combine(directory, "type-" + peers.Count + ".xml"); type.Item.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    peers["type:" + type.Path] = WatchXmlHash(path);
                }
                foreach (var tags in TagTables(scope.Tags, scope.Path + "/tags", budget))
                {
                    string path = Path.Combine(directory, "tags-" + peers.Count + ".xml"); tags.Item.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    peers["tags:" + tags.Path] = WatchXmlHash(path);
                }
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Watch preservation manifest exceeds bound.");
            peers["hardware"] = CaptureHardwareEngineering(selected.Project);
            peers["languages"] = new JObject { ["active"] = new JArray(selected.Project.LanguageSettings.ActiveLanguages.Select(l => l.Culture.Name)),
                ["reference"] = selected.Project.LanguageSettings.ReferenceLanguage.Culture.Name, ["editing"] = selected.Project.LanguageSettings.EditingLanguage.Culture.Name };
            var access = new JArray();
            var ownerBudget = new ScanBudget(10000);
            var owners = DeviceItems(selected.Project, ownerBudget).ToArray();
            if (ownerBudget.Truncated) throw new BridgeException("TraversalLimit", "Hardware/access manifest is incomplete.");
            foreach (var owner in owners)
            {
                var manager = owner.Item.GetService<WatchAndForceTableAccessManager>();
                access.Add(new JObject { ["owner"] = owner.Path, ["available"] = manager != null,
                    ["watch"] = manager == null ? null : new JArray(manager.WatchtableAccessRules.Select(r => new JObject { ["table"] = r.WatchTable.Name, ["access"] = r.Access.ToString() })),
                    ["force"] = manager == null ? null : new JArray(manager.ForcetableAccessRules.Select(r => new JObject { ["table"] = r.ForceTable.Name, ["access"] = r.Access.ToString() })) });
            }
            peers["access"] = access;
            JToken selectedTable;
            if (target != null) selectedTable = tables.Single(t => t.Value<string>("kind") == "watch" && t.Value<string>("name") == tableName && t.Value<string>("group") == groupPath);
            else
            {
                string absent = Path.Combine(directory, "absent.txt"); File.WriteAllBytes(absent, WatchTableDocument.AbsentBytes(groupPath, tableName));
                selectedTable = new JObject { ["kind"] = "watch", ["exists"] = false, ["group"] = groupPath, ["name"] = tableName,
                    ["artifact"] = FileArtifact(new FileInfo(absent)), ["semanticSha256"] = ChangeFileHash(absent), ["isConsistent"] = true, ["rows"] = new JArray() };
            }
            string content = selectedTable.Value<string>("semanticSha256");
            string peerHash = ChangeHash(peers.ToString(Formatting.None));
            string source = ChangeHash(peerHash + content);
            string ownerIdentity = string.Join("|", owners.Select(o => o.Item.TypeIdentifier));
            string profileIdentity = ChangeHash(WatchTableDocument.Profile + "|" + ChangeFileHash(typeof(TiaPortal).Assembly.Location) + "|" + ChangeFileHash(typeof(PlcWatchTable).Assembly.Location));
            var observation = new JObject {
                ["reference"] = new JObject { ["object"] = new JObject { ["projectPath"] = selected.Project.Path.FullName, ["plcPath"] = selected.Path, ["scopePath"] = selected.Path,
                    ["objectPath"] = selected.Path + "/watch/" + groupPath + "/" + Segment(tableName), ["kind"] = "watchTable" },
                    ["snapshot"] = new JObject { ["observationId"] = Guid.NewGuid().ToString("N"), ["sourceSha256"] = source, ["tiaVersion"] = 21 }, ["contentSha256"] = content },
                ["projectFilePath"] = selected.Project.Path.FullName, ["sessionEpoch"] = _watchEpoch, ["adapterSha256"] = ChangeFileHash(Assembly.GetExecutingAssembly().Location),
                ["profileSha256"] = profileIdentity, ["hardwareIdentity"] = ownerIdentity, ["nativeIdentity"] = null, ["offline"] = true,
                ["probes"] = new JArray(new JObject { ["name"] = "offline", ["state"] = "observed", ["evidenceSha256"] = source },
                    new JObject { ["name"] = "watchImport", ["state"] = "observed", ["evidenceSha256"] = profileIdentity }) };
            var result = new JObject { ["observation"] = observation, ["target"] = selectedTable.DeepClone(), ["tables"] = tables, ["peers"] = peers, ["peerSha256"] = peerHash,
                ["projectModified"] = selected.Project.IsModified, ["project"] = ProjectInfo(selected.Project), ["processId"] = _processId,
                ["coverage"] = "All watch/force documents, exported blocks/types/tag tables, configured hardware, languages and exposed watch/force access assignments for this bounded fixture. Unexposed vendor data is not claimed." };
            File.WriteAllText(Path.Combine(directory, "snapshot.json"), result.ToString(Formatting.Indented));
            result["receipt"] = FileArtifact(new FileInfo(Path.Combine(directory, "snapshot.json"))); return result;
        }
        private JObject WatchBegin(JObject args)
        {
            if (_watchLease != null) throw new BridgeException("ResourceBusy", "An existing native lease cannot be replaced.");
            var selected = SelectPlc(args);
            if (!WatchOwnsScratch(selected.Project)) throw new BridgeException("ProjectIsolationRequired", "First mutation profile requires this worker's newly created owned scratch project.");
            if (selected.Project.IsModified) throw new BridgeException("UnsavedProject", "Save the authored scratch fixture before preparation.");
            _watchLease = _portal.ExclusiveAccess("TIA MCP: durable watch dispatch");
            try { VerifyWatchSource(args); _watchBefore = WatchSnapshot(args); _watchSelector = (JObject)args.DeepClone(); _watchToken = Guid.NewGuid().ToString("N"); _watchDispatched = false;
                var result = (JObject)_watchBefore.DeepClone(); result["leaseToken"] = _watchToken; return result; }
            catch { WatchRelease(); throw; }
        }
        private void WatchCheckLease(JObject args)
        { if (_watchLease == null || args.Value<string>("leaseToken") != _watchToken) throw new BridgeException("InvalidLease", "Exact active native lease required."); }
        private JObject WatchCheckpoint(JObject args)
        {
            WatchCheckLease(args);
            VerifyWatchSource(_watchSelector);
            EnsureWatchTargetFresh(_watchBefore, WatchSnapshot(_watchSelector));
            var project = SelectProject(_watchSelector);
            string directory = ArtifactDirectory(args); string name = "watch-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".zap21";
            WatchQualificationInterrupt(args, "checkpoint.beforeArchive", project.Path.FullName, null);
            project.Archive(new DirectoryInfo(directory), name, ProjectArchivationMode.Compressed);
            WatchQualificationInterrupt(args, "checkpoint.afterArchive", project.Path.FullName, Path.Combine(directory, name));
            return new JObject { ["checkpoint"] = FileArtifact(new FileInfo(Path.Combine(directory, name))), ["snapshot"] = WatchSnapshot(_watchSelector) };
        }
        private JObject WatchApply(JObject args)
        {
            WatchCheckLease(args); if (_watchDispatched) throw new BridgeException("RecoveryRequired", "Native watch dispatch is one-use and cannot replay.");
            VerifyWatchSource(_watchSelector);
            var current = WatchSnapshot(_watchSelector);
            EnsureWatchTargetFresh(_watchBefore, current);
            string path = args.Value<string>("inputPath");
            if (ChangeFileHash(path) != args.Value<string>("inputSha256")) throw new BridgeException("ArtifactChanged", "Prepared XML bytes changed.");
            var input = new WatchTableDocument(File.ReadAllBytes(path));
            if (input.IsForce || input.Name != _watchSelector.Value<string>("tableName")) throw new BridgeException("InvalidInput", "Only the exact reviewed watch table may be imported.");
            var selected = SelectPlc(_watchSelector);
            var group = WatchGroups(selected.Software.WatchAndForceTableGroup, "root", 0).Single(g => g.Item2 == (_watchSelector.Value<string>("group") ?? "root")).Item1;
            _watchDispatched = true;
            WatchQualificationInterrupt(args, "import.before", selected.Project.Path.FullName, null);
            bool creating = _watchBefore["target"].Value<bool?>("exists") == false;
            if (creating && WatchGroups(selected.Software.WatchAndForceTableGroup, "root", 0).Any(g =>
                g.Item1.WatchTables.Any(t => string.Equals(t.Name, input.Name, StringComparison.OrdinalIgnoreCase)) ||
                g.Item1.ForceTables.Any(t => string.Equals(t.Name, input.Name, StringComparison.OrdinalIgnoreCase))))
                throw new BridgeException("WatchTableCollision", "Creation cannot replace an existing watch/force table.");
            var imported = group.WatchTables.Import(new FileInfo(path), creating ? ImportOptions.None : ImportOptions.Override);
            WatchQualificationInterrupt(args, "import.after", selected.Project.Path.FullName, null);
            if (imported.Count != 1 || imported[0].Name != input.Name) throw new BridgeException("UnexpectedEffects", "Native import returned an unexpected table set.");
            if (_componentQualificationEnabled && args.Value<bool?>("qualificationInterruptAfterImport") == true)
            {
                selected.Project.Save();
                File.WriteAllText(Path.Combine(ArtifactDirectory(_watchSelector), "interrupted-after-native-import.json"), new JObject {
                    ["projectPath"] = selected.Project.Path.FullName, ["processId"] = _processId, ["saved"] = true, ["automaticReplay"] = false }.ToString());
                Environment.Exit(73); // qualification-only actual worker loss after saved native effect, before readback/response
            }
            var after = WatchSnapshot(_watchSelector);
            if (after.Value<string>("peerSha256") != _watchBefore.Value<string>("peerSha256") || after["target"].Value<string>("semanticSha256") != WatchTableDocument.SemanticHash(input.ToBytes()))
                throw new BridgeException("VerificationFailed", "Native readback or peer preservation differs from reviewed input.", after);
            WatchQualificationInterrupt(args, "save.before", selected.Project.Path.FullName, null);
            selected.Project.Save();
            WatchQualificationInterrupt(args, "save.after", selected.Project.Path.FullName, null);
            after = WatchSnapshot(_watchSelector);
            if (after.Value<string>("peerSha256") != _watchBefore.Value<string>("peerSha256") || after["target"].Value<string>("semanticSha256") != WatchTableDocument.SemanticHash(input.ToBytes()))
                throw new BridgeException("VerificationFailed", "Saved readback or peer preservation differs from reviewed input.", after);
            after["saved"] = true; after["automaticReplay"] = false; return after;
        }
        // Reacquire under the held exclusive lease immediately before either native effect.
        // Equal XML alone cannot identify a project, PLC, object, process or adapter session.
        private static void EnsureWatchTargetFresh(JObject expected, JObject current)
        {
            var before = expected["observation"] as JObject;
            var actual = current["observation"] as JObject;
            if (before == null || actual == null || (expected.Value<int?>("processId") ?? 0) <= 0 ||
                expected.Value<int?>("processId") != current.Value<int?>("processId") ||
                current.Value<bool?>("projectModified") != false || actual.Value<bool?>("offline") != true ||
                current["target"]?.Value<bool?>("isConsistent") != true)
                throw new BridgeException("StaleNativeTarget", "Exact saved offline native target and lease process are required.");
            foreach (string path in new[] { "reference.contentSha256", "reference.snapshot.sourceSha256" })
                if (actual.SelectToken(path)?.Type != JTokenType.String || !JToken.DeepEquals(before.SelectToken(path), actual.SelectToken(path)))
                    throw new BridgeException("StaleContent", "Native content or full preservation manifest changed before the native effect.");
            if (!JToken.DeepEquals(expected["peerSha256"], current["peerSha256"]) ||
                !JToken.DeepEquals(actual.SelectToken("reference.contentSha256"), current["target"]?["semanticSha256"]))
                throw new BridgeException("StaleContent", "Native target export and preservation observation disagree.");
            var expectedIdentity = (JObject)before.DeepClone(); var actualIdentity = (JObject)actual.DeepClone();
            foreach (var observation in new[] { expectedIdentity, actualIdentity })
                (observation.SelectToken("reference.snapshot") as JObject)?.Remove("observationId");
            if (!JToken.DeepEquals(expectedIdentity, actualIdentity))
                throw new BridgeException("StaleNativeTarget", "Native project/PLC/object, epoch, binary, profile, hardware or probes changed before the native effect.");
        }
        private JObject WatchEnd(JObject args) { WatchCheckLease(args); WatchRelease(); return new JObject { ["released"] = true }; }
        private JObject WatchRestore(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0) throw new BridgeException("ProjectIsolationRequired", "Restore requires an empty owned native instance.");
            string archive = args.Value<string>("archivePath"), destination = args.Value<string>("destination");
            if (!ProjectPathIsAbsolute(destination) || Directory.Exists(destination) || File.Exists(destination) || ChangeFileHash(archive) != args.Value<string>("expectedSha256"))
                throw new BridgeException("CheckpointInvalid", "Exact checkpoint hash and unused absolute destination required.");
            Directory.CreateDirectory(destination);
            WatchQualificationInterrupt(args, "retrieve.before", null, destination);
            var restored = _portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(destination));
            WatchQualificationInterrupt(args, "retrieve.after", restored.Path.FullName, destination);
            return new JObject { ["project"] = ProjectInfo(restored), ["destination"] = destination, ["processId"] = _processId, ["sessionEpoch"] = _watchEpoch };
        }
        private void WatchQualificationInterrupt(JObject args, string point, string projectPath, string output)
        {
            if (!_componentQualificationEnabled || args.Value<string>("qualificationInterruptAt") != point) return;
            string marker = args.Value<string>("qualificationMarkerPath");
            if (!ProjectPathIsAbsolute(marker) || File.Exists(marker)) throw new BridgeException("InvalidFixture", "An unused absolute interruption marker is required.");
            var receipt = new JObject { ["point"] = point, ["projectPath"] = projectPath, ["output"] = output,
                ["processId"] = _processId, ["workerProcessId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["workerSha256"] = ChangeFileHash(Assembly.GetExecutingAssembly().Location), ["automaticReplay"] = false };
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { var bytes = Encoding.UTF8.GetBytes(receipt.ToString(Formatting.Indented)); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            Environment.Exit(73);
        }
        private void WatchRelease() { _watchLease?.Dispose(); _watchLease = null; _watchToken = null; _watchBefore = null; _watchSelector = null; }
        private JObject WatchFixtureSeed(JObject args)
        {
            var selected = SelectPlc(args);
            if (!_componentQualificationEnabled || !WatchOwnsScratch(selected.Project) || selected.Project.IsModified)
                throw new BridgeException("ProjectIsolationRequired", "Qualification-only seed requires newly created saved owned scratch.", new JObject {
                    ["qualificationEnabled"] = _componentQualificationEnabled, ["ownsPortal"] = _ownsPortal, ["ownsScratch"] = WatchOwnsScratch(selected.Project), ["modified"] = selected.Project.IsModified });
            using (SharedMutationExclusive("TIA MCP: authored watch fixture"))
            {
                string path = args.Value<string>("inputPath"); if (ChangeFileHash(path) != args.Value<string>("inputSha256")) throw new BridgeException("ArtifactChanged", "Seed hash mismatch.");
                var doc = new WatchTableDocument(File.ReadAllBytes(path));
                if (doc.IsForce || doc.Name != "MilestoneWatch" || selected.Software.WatchAndForceTableGroup.WatchTables.Count != 0) throw new BridgeException("InvalidFixture", "Fresh fixed-name fixture required.");
                selected.Project.Archive(new DirectoryInfo(ArtifactDirectory(args)), "before-watch-seed.zap21", ProjectArchivationMode.Compressed);
                foreach (string culture in new[] { "en-GB", "nl-NL" })
                {
                    var language = selected.Project.LanguageSettings.Languages.Find(System.Globalization.CultureInfo.GetCultureInfo(culture));
                    if (!selected.Project.LanguageSettings.ActiveLanguages.Contains(language)) selected.Project.LanguageSettings.ActiveLanguages.Add(language);
                }
                selected.Software.WatchAndForceTableGroup.WatchTables.Import(new FileInfo(path), ImportOptions.None);
                doc.Rename("UnchangedPeer"); doc.SetComment(0, "en-GB", "Unchanged peer {0}");
                string peerPath = Path.Combine(ArtifactDirectory(args), "peer-seed.xml"); File.WriteAllBytes(peerPath, doc.ToBytes());
                selected.Software.WatchAndForceTableGroup.WatchTables.Import(new FileInfo(peerPath), ImportOptions.None);
                selected.Project.Save(); _watchOwnedScratchPaths.Add(selected.Project.Path.FullName); return WatchSnapshot(args);
            }
        }
        private bool WatchOwnsScratch(Project project) => _ownsPortal &&
            ((_scratchProject != null && string.Equals(project.Path.FullName, _scratchProject.Path.FullName, StringComparison.OrdinalIgnoreCase)) || _watchOwnedScratchPaths.Contains(project.Path.FullName));
        private static IEnumerable<Tuple<PlcWatchAndForceTableGroup, string>> WatchGroups(PlcWatchAndForceTableGroup group, string path, int depth)
        {
            if (depth > 32) throw new BridgeException("TraversalLimit", "Watch groups exceed bound."); yield return Tuple.Create(group, path);
            foreach (var child in group.Groups) foreach (var item in WatchGroups(child, path + "/" + Segment(child.Name), depth + 1)) yield return item;
        }
        private static JObject WatchRow(PlcTableCommentEntry row)
        {
            var result = new JObject { ["kind"] = row.GetType().Name };
            var watch = row as PlcWatchTableEntry;
            if (watch != null) { result["name"] = watch.Name; result["address"] = watch.Address; result["displayFormat"] = watch.DisplayFormat.ToString(); result["modifyValue"] = watch.ModifyValue; result["modifyIntention"] = watch.ModifyIntention; }
            return result;
        }
        private static string WatchXmlHash(string path)
        {
            var document = XDocument.Load(path, LoadOptions.PreserveWhitespace); var created = document.Root.Element("DocumentInfo")?.Element("Created"); if (created != null) created.Value = "";
            return ChangeHash(document.ToString(SaveOptions.DisableFormatting));
        }
    }
}
