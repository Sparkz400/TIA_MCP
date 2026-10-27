using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Kinds whose change fingerprint needs prepared in-memory targets; a fresh session cannot recompute them.
        private static readonly HashSet<string> AdmissionReconcileTargetKinds = new HashSet<string>(new[] {
            "hmi_engineering", "hardware_attributes", "network_configure", "tag_set", "constant_set" }, StringComparer.Ordinal);

        // Read-only evidence for releasing an unresolved shared mutation admission. Runs in a fresh empty owned
        // Portal; it only opens projects, retrieves archives into new reconcile directories and closes them.
        // It never saves, powers, downloads or replays. Every resource row gets a disposition or its reasons.
        private JObject AdmissionReconcileVerify(JObject args)
        {
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 0)
                throw new BridgeException("ProjectIsolationRequired", "Admission reconciliation requires an empty worker-owned TIA instance started with tia_start.");
            string operation = ChangeText(args, "operation", 64), scopeSha256 = ChangeText(args, "scopeSha256", 64);
            string observationSha256 = ChangeText(args, "observationSha256", 64), retrieveRoot = ChangeText(args, "retrieveDirectory", 4096);
            if (!ProjectPathIsAbsolute(retrieveRoot) || File.Exists(retrieveRoot) || Directory.Exists(retrieveRoot))
                throw new BridgeException("MutationScopeStale", "A new absolute reconciliation retrieval directory is required.");
            if (retrieveRoot.Length > 70) throw new BridgeException("RecoveryPathTooLong", "Use a shorter artifact directory for TIA's project path limit.");
            DateTime admittedAt = DateTime.Parse(ChangeText(args, "admittedAtUtc", 64), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            int admittedProcess = args.Value<int?>("admittedProcessId") ?? throw new BridgeException("InvalidRequest", "admittedProcessId is required.");
            var resources = args["resources"] as JArray;
            if (resources == null || resources.Count < 1 || resources.Count > 64) throw new BridgeException("InvalidRequest", "Bounded admitted resources are required.");
            var rows = new JArray();
            foreach (var resource in resources)
            {
                string kind = resource.Value<string>("kind"), path = resource.Value<string>("path"), effect = resource.Value<string>("effect");
                if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(effect) || !ProjectPathIsAbsolute(path))
                    throw new BridgeException("InvalidRequest", "Every admitted resource needs its kind, exact absolute path and effect.");
                rows.Add(new JObject { ["kind"] = kind, ["path"] = path, ["effect"] = effect, ["disposition"] = null, ["reasons"] = new JArray(), ["evidence"] = new JObject() });
            }
            var result = Envelope();
            result["scopeSha256"] = scopeSha256; result["operation"] = operation; result["processId"] = _processId;
            result["nativeMutationInvoked"] = false; result["admittedAtUtc"] = admittedAt.ToString("O");
            result["originalProcessRunning"] = PortalProcesses.Discover().Any(p => p.ProcessId == admittedProcess);
            result["resources"] = rows;
            var check = new AdmissionReconcileCheck(this, rows, retrieveRoot, admittedAt);
            try
            {
                switch (operation)
                {
                    case "change_apply": case "change_restore": AdmissionReconcileChange(check, operation); break;
                    case "simulation_download": AdmissionReconcileDownload(check); break;
                    case "scratch_create": AdmissionReconcileScratch(check); break;
                    case "project_retrieve": AdmissionReconcileRetrieve(check, observationSha256); break;
                    case "library_open": AdmissionReconcileLibraryOpen(check, observationSha256); break;
                }
            }
            catch (Exception error)
            {
                // A damaged journal, report or receipt holds everything with its reason instead of failing the call.
                result["verificationFailure"] = error.GetType().FullName + ": " + error.Message;
                check.HoldAll("Verification could not complete: " + error.Message);
            }
            foreach (JObject row in rows.Where(r => r["disposition"].Type == JTokenType.Null && !((JArray)r["reasons"]).Any()))
                ((JArray)row["reasons"]).Add("No reconciliation rule proves this effect.");
            return result;
        }

        private void AdmissionReconcileChange(AdmissionReconcileCheck check, string operation)
        {
            var journalRow = check.Row("legacyJournalCheckpointOutput") ?? check.Row("legacyRecoveryJournal");
            if (journalRow == null) { check.HoldAll("The admission has no change journal resource."); return; }
            if (Path.GetFileName(journalRow.Value<string>("path")).StartsWith("change-lib-", StringComparison.Ordinal))
            { AdmissionReconcileLibraryChange(check, operation, journalRow); return; }
            string directory = journalRow.Value<string>("path"), journalPath = Path.Combine(directory, "journal.json");
            if (!File.Exists(journalPath) || new FileInfo(journalPath).Length > 8 * 1024 * 1024) { check.HoldAll("The change journal is missing or too large."); return; }
            var journal = JObject.Parse(File.ReadAllText(journalPath));
            check.Evidence(journalRow, "journal", new JObject { ["state"] = journal["state"], ["stages"] = journal["stages"], ["kind"] = journal["kind"] });
            if ("change-" + journal.Value<string>("changeId") != Path.GetFileName(directory)) { check.HoldAll("The change journal does not belong to its directory."); return; }
            string archive = journal.Value<string>("recoveryArchive"), archiveHash = journal.Value<string>("recoveryArchiveSha256");
            bool archiveValid = archive != null && archiveHash != null && File.Exists(archive) && ChangeFileHash(archive) == archiveHash;
            bool partialArchive = archiveHash == null && Directory.GetFiles(directory, "*.zap21").Any();
            if ((archive == null || archiveValid) && !partialArchive)
                check.Prove(journalRow, "verifiedApplied", "Change journal is readable, bound to its change and any recorded checkpoint archive matches its hash.");
            else check.Hold(journalRow, "The change directory holds a missing, changed or unrecorded checkpoint archive.");

            var input = check.Row("reviewedMutationInput");
            if (input != null) check.NotWrittenSince(input, "The prepared input was not written since admission; apply and restore only read it.");

            string projectPath = journal.Value<string>("projectPath");
            foreach (var row in new[] { check.Row("checkpointMutationCompileSave"), check.Row("preserveAndRecover") }.Where(r => r != null))
                if (!SameSimulationPath(row.Value<string>("path"), projectPath)) { check.HoldAll("The change journal names a different project than the admission."); return; }
            string kind = journal.Value<string>("kind") ?? "";
            // The checkpoint comparison sees only what the fingerprint covers; hardware kinds add their configured hardware,
            // PLC alarm kinds the content hash of a fresh native export of their artifact type.
            bool covered = !AdmissionReconcileTargetKinds.Contains(kind) && !kind.StartsWith("to_", StringComparison.Ordinal);
            if (HardwareEngineeringRules.Applies(kind) || IsPlcAlarmKind(kind)) check.FingerprintKind = kind;
            Func<Project, string> outcome = project => {
                if (AdmissionReconcileTargetKinds.Contains(kind) || kind.StartsWith("to_", StringComparison.Ordinal) || journal.Value<string>("afterFingerprint") == null) return null;
                var replay = new EngineeringChange { Kind = kind, Payload = journal["preview"]?["payload"] as JObject ?? new JObject() };
                return EngineeringFingerprint(replay, project);
            };
            if (operation == "change_apply")
            {
                var projectRow = check.Row("checkpointMutationCompileSave");
                if (projectRow == null) return;
                if (!archiveValid) { check.Hold(projectRow, "No complete hash-verified checkpoint archive exists to compare the project with."); return; }
                if (!covered) { check.Hold(projectRow, "The project fingerprint does not cover the effect of change kind " + kind + "."); return; }
                string checkpoint = check.ArchiveFingerprint(projectRow, archive);
                var current = check.OpenProject(projectRow, projectPath, outcome);
                if (current == null || checkpoint == null) return;
                if (current.Value<string>("fingerprint") == checkpoint)
                    check.Prove(projectRow, "unchanged", "Fresh fingerprint equals the retrieved pre-mutation checkpoint; any unsaved mutation was lost with the interrupted session.");
                else if (journal.Value<string>("state") == "applied" && current.Value<string>("outcomeFingerprint") == journal.Value<string>("afterFingerprint"))
                    check.Prove(projectRow, "verifiedApplied", "Journal recorded the applied outcome and the fresh project fingerprint equals it.");
                else check.Hold(projectRow, "The project differs from its checkpoint and does not match a recorded applied outcome.");
                return;
            }
            var original = check.Row("preserveAndRecover");
            if (original != null)
            {
                var current = check.OpenProject(original, projectPath, outcome);
                if (current != null && current.Value<string>("outcomeFingerprint") != null && current.Value<string>("outcomeFingerprint") == journal.Value<string>("afterFingerprint"))
                    check.Prove(original, "unchanged", "The original project still has its recorded outcome fingerprint; recovery preserved it.");
                else if (current != null) check.Hold(original, "The original project no longer matches its recorded outcome, or its fingerprint cannot be recomputed for kind " + kind + ".");
            }
            var archiveRow = check.Row("readCheckpoint");
            if (archiveRow != null)
            {
                if (archiveValid && SameSimulationPath(archive, archiveRow.Value<string>("path"))) check.Prove(archiveRow, "unchanged", "The checkpoint archive still has its recorded SHA-256.");
                else check.Hold(archiveRow, "The checkpoint archive is missing or changed.");
            }
            var destination = check.Row("createNewDestination");
            if (destination != null) check.Destination(destination, () => archiveValid ? check.ArchiveFingerprint(destination, archive) : null,
                "The recovery destination is the retrieved checkpoint (fresh fingerprints are equal).");
        }

        private void AdmissionReconcileDownload(AdmissionReconcileCheck check)
        {
            JObject storageRow = check.Row("ownedSimulatorStorageAndCard"), projectRow = check.Row("simulationCompilationOrCredentialPreparation");
            if (storageRow == null || projectRow == null) return;
            string storage = storageRow.Value<string>("path"), card = Path.Combine(storage, "SIMATIC_MC");
            string reportPath = Path.Combine(storage, "simulation-download.json"), claim = Path.Combine(storage, ".download.claim");
            if (!File.Exists(claim) && !Directory.Exists(card) && !File.Exists(card) && !File.Exists(reportPath))
                check.Prove(storageRow, "unchanged", "No download claim, memory card or report exists: the download never reached the storage.");
            else if (!File.Exists(reportPath)) check.Hold(storageRow, "The storage holds a claim or card without a download report.");
            else
            {
                var report = ReadSimulationCredentialJson(reportPath, 16 * 1024 * 1024);
                var files = Directory.Exists(card) ? SimulationCardFiles(card) : new JArray();
                // JObject.Parse already turns the ISO text into a Date token.
                var completedAt = report["completedAtUtc"]?.Type == JTokenType.Date ? report.Value<DateTime>("completedAtUtc").ToUniversalTime() : (DateTime?)null;
                bool bound = completedAt >= check.AdmittedAt;
                bool complete = report.Value<bool?>("completed") == true && files.Count > 0 && JToken.DeepEquals(files, report["files"]) &&
                    SimulationProvisionManifestHash(files) == report.Value<string>("programRevision");
                bool sameProject = SameSimulationPath(report["project"]?.Value<string>("path"), projectRow.Value<string>("path"));
                check.Evidence(storageRow, "download", new JObject { ["completed"] = report["completed"], ["completedAtUtc"] = report["completedAtUtc"],
                    ["programRevision"] = report["programRevision"], ["cardFiles"] = files.Count, ["manifestMatches"] = complete, ["completedAfterAdmission"] = bound, ["sameProject"] = sameProject });
                if (complete && bound && sameProject)
                    check.Prove(storageRow, "verifiedApplied", "The download report completed after admission and the memory card manifest and program revision match it.");
                else check.Hold(storageRow, "The download report is incomplete, predates the admission, names another project or differs from the memory card.");
            }
            check.NotSavedSince(projectRow, "The download route compiles in memory and never saves; the fresh project was not saved since admission.");
        }

        private void AdmissionReconcileScratch(AdmissionReconcileCheck check)
        {
            var row = check.Row("createNewDestination");
            if (row == null) return;
            string name = Path.GetFileName(row.Value<string>("path"));
            check.Destination(row, null, "The saved scratch project opens with its station and the four generated fixture blocks.", project => {
                if (project.Name != name || project.Devices.Count != 1 || project.Devices.First().Name != "ScratchStation" || project.IsModified) return false;
                var plc = SelectPlc(new JObject { ["projectName"] = project.Name });
                var names = new HashSet<string>(Blocks(plc.Software.BlockGroup, plc.Path + "/blocks", new ScanBudget(1000)).Select(b => b.Item.Name), StringComparer.Ordinal);
                return new[] { "Main", "TiaMcp_IsEnabled", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB" }.All(names.Contains);
            });
        }

        private void AdmissionReconcileRetrieve(AdmissionReconcileCheck check, string observationSha256)
        {
            JObject archiveRow = check.Row("readCheckpoint"), destination = check.Row("createNewDestination");
            if (archiveRow == null || destination == null) return;
            string archive = archiveRow.Value<string>("path");
            bool unchanged = File.Exists(archive) && SharedMutationHash(new JObject { ["archiveSha256"] = ChangeFileHash(archive),
                ["retrievalDestination"] = destination.Value<string>("path") }) == observationSha256;
            if (unchanged) check.Prove(archiveRow, "unchanged", "The archive hash recomputes the admitted observation exactly.");
            else check.Hold(archiveRow, "The archive is missing or no longer has its admitted hash.");
            check.Destination(destination, () => unchanged ? check.ArchiveFingerprint(destination, archive) : null,
                "The destination is the retrieved archive (fresh fingerprints are equal).");
        }

        private void AdmissionReconcileLibraryOpen(AdmissionReconcileCheck check, string observationSha256)
        {
            var projectRow = check.Row("registerProjectLibraryHandle");
            if (projectRow != null)
            {
                // The handle lived only in the interrupted worker; nothing was written.
                var opened = check.OpenProject(projectRow, projectRow.Value<string>("path"), project => SharedMutationHash(new JObject { ["project"] = ProjectInfo(project) }));
                if (opened != null && opened.Value<string>("outcomeFingerprint") == observationSha256)
                    check.Prove(projectRow, "unchanged", "The project opens fresh with its admitted identity; the library handle existed only in the interrupted worker.");
                else if (opened != null) check.Hold(projectRow, "The project identity differs from the admitted observation.");
                return;
            }
            JObject source = check.Row("readSourceAndCompanions"), destination = check.Row("createNewDestination");
            if (source == null || destination == null) return;
            string path = source.Value<string>("path"), root = destination.Value<string>("path");
            bool archive = File.Exists(path);
            string hash = archive ? ChangeFileHash(path) : Directory.Exists(path) ? LibraryDirectoryHash(path) : null;
            bool unchanged = hash != null && SharedMutationHash(new JObject { ["sourceSha256"] = hash, ["workingCopyDestination"] = root }) == observationSha256;
            if (unchanged) check.Prove(source, "unchanged", "The library source hash recomputes the admitted observation exactly.");
            else check.Hold(source, "The library source is missing or changed.");
            string receiptPath = Path.Combine(root, "open-receipt.json");
            if (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any())
            { check.Prove(destination, "unchanged", Directory.Exists(root) ? "The working-copy destination exists but is empty." : "The working-copy destination was never created."); return; }
            var receipt = File.Exists(receiptPath) && new FileInfo(receiptPath).Length < 4 * 1024 * 1024 ? JObject.Parse(File.ReadAllText(receiptPath)) : null;
            string snapshot = Path.Combine(root, "source"), sourceHash = receipt?["library"]?.Value<string>("sourceSha256");
            string snapshotHash = !unchanged || sourceHash == null ? null : archive
                ? (File.Exists(Path.Combine(snapshot, Path.GetFileName(path))) ? ChangeFileHash(Path.Combine(snapshot, Path.GetFileName(path))) : null)
                : (Directory.Exists(snapshot) ? LibraryDirectoryHash(snapshot) : null);
            check.Evidence(destination, "receipt", new JObject { ["state"] = receipt?["state"], ["sourceSha256"] = sourceHash, ["snapshotSha256"] = snapshotHash });
            if (receipt?.Value<string>("state") == "opened" && snapshotHash != null && snapshotHash == sourceHash && sourceHash == hash)
                check.Prove(destination, "verifiedApplied", "The open receipt completed and the managed snapshot equals the unchanged source; the library handle died with the worker.");
            else check.Hold(destination, "The working copy is partial: no completed open receipt with a snapshot equal to the source.");
        }

        private sealed class AdmissionReconcileCheck
        {
            private readonly OpennessSession _session;
            private readonly JArray _rows;
            private readonly string _retrieveRoot;
            private int _retrievals;
            internal readonly DateTime AdmittedAt;
            internal string FingerprintKind = "admission_reconcile";
            internal AdmissionReconcileCheck(OpennessSession session, JArray rows, string retrieveRoot, DateTime admittedAt)
            { _session = session; _rows = rows; _retrieveRoot = retrieveRoot; AdmittedAt = admittedAt; }

            internal JObject Row(string effect) => _rows.OfType<JObject>().SingleOrDefault(r => r.Value<string>("effect") == effect);
            internal JObject[] Rows(string effect) => _rows.OfType<JObject>().Where(r => r.Value<string>("effect") == effect).ToArray();
            internal string NextRetrieveDirectory()
            {
                string target = Path.Combine(_retrieveRoot, (_retrievals++).ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(target); return target;
            }
            internal void Prove(JObject row, string disposition, string basis) { row["disposition"] = disposition; row["basis"] = basis; }
            internal void Hold(JObject row, string reason) { row["disposition"] = null; ((JArray)row["reasons"]).Add(reason); }
            internal void HoldAll(string reason) { foreach (JObject row in _rows) Hold(row, reason); }
            internal void Evidence(JObject row, string name, JToken value) => ((JObject)row["evidence"])[name] = value;

            internal void NotWrittenSince(JObject row, string basis)
            {
                // A directory counts by its newest file; a missing path or a later write holds.
                string path = row.Value<string>("path");
                var files = File.Exists(path) ? new[] { path } : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(100001).ToArray() : new string[0];
                if (files.Length == 0 || files.Length > 100000) { Hold(row, "The file or directory is missing, empty or too large to inspect."); return; }
                var written = files.Max(File.GetLastWriteTimeUtc);
                Evidence(row, "lastWriteUtc", written.ToString("O"));
                if (written < AdmittedAt) Prove(row, "unchanged", basis); else Hold(row, "A file was written after admission.");
            }

            internal void NotSavedSince(JObject row, string basis)
            {
                var opened = OpenProject(row, row.Value<string>("path"), null);
                if (opened == null) return;
                if (DateTime.Parse(opened.Value<string>("lastModifiedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) < AdmittedAt) Prove(row, "unchanged", basis);
                else Hold(row, "The project was saved after admission.");
            }

            /// <summary>Opens and closes one project; records its location-independent fingerprint and save time, plus an optional outcome value.</summary>
            internal JObject OpenProject(JObject row, string path, Func<Project, string> outcome)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Hold(row, "The project file is missing."); return null; }
                try
                {
                    var project = _session._portal.Projects.Open(new FileInfo(path));
                    try
                    {
                        // The API does not document the kind. Unspecified takes the later of its UTC and local readings,
                        // so an ambiguous save time can only hold, never release.
                        var raw = project.LastModified;
                        var modified = raw.Kind == DateTimeKind.Local ? raw.ToUniversalTime() : raw.Kind == DateTimeKind.Utc ? raw
                            : new DateTime(Math.Max(DateTime.SpecifyKind(raw, DateTimeKind.Utc).Ticks, DateTime.SpecifyKind(raw, DateTimeKind.Local).ToUniversalTime().Ticks), DateTimeKind.Utc);
                        var observed = new JObject { ["path"] = project.Path.FullName, ["lastModifiedUtc"] = modified.ToString("O"),
                            ["lastModifiedRaw"] = raw.ToString("O"), ["lastModifiedKind"] = raw.Kind.ToString(), ["isModifiedAfterOpen"] = project.IsModified,
                            ["fingerprint"] = Fingerprint(project) };
                        if (outcome != null) observed["outcomeFingerprint"] = outcome(project);
                        Evidence(row, "project", observed);
                        return observed;
                    }
                    finally { project.Close(); }
                }
                catch (Exception error) { Hold(row, "The project could not be opened read-only for evidence: " + error.Message); return null; }
            }

            /// <summary>Location-independent project fingerprint plus the project library revision, which the engineering fingerprint omits.</summary>
            private string Fingerprint(Project project)
            {
                string engineering = _session.EngineeringFingerprint(new EngineeringChange { Kind = FingerprintKind }, project, true);
                return ChangeHash(engineering + "|" + LibraryRevision(new ManagedLibrary { Native = project.ProjectLibrary, Project = project }));
            }

            /// <summary>Retrieves an archive into a new reconcile directory and returns its location-independent fingerprint.</summary>
            internal string ArchiveFingerprint(JObject row, string archive)
            {
                try
                {
                    string target = NextRetrieveDirectory();
                    var copy = _session._portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(target));
                    try { string value = Fingerprint(copy); Evidence(row, "archiveFingerprint", value); return value; }
                    finally { copy.Close(); }
                }
                catch (Exception error) { Hold(row, "The archive could not be retrieved for comparison: " + error.Message); return null; }
            }

            /// <summary>A new destination is unchanged when absent or empty (an intent record aside); otherwise it must hold exactly one project, or library, proven complete.</summary>
            internal void Destination(JObject row, Func<string> reference, string basis, Func<Project, bool> complete = null, Func<string, bool> library = null)
            {
                string root = row.Value<string>("path");
                if (!Directory.Exists(root) && !File.Exists(root)) { Prove(row, "unchanged", "The new destination was never created."); return; }
                if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                    .Any(entry => !string.Equals(entry, Path.Combine(root, "recovery-intent.json"), StringComparison.OrdinalIgnoreCase)))
                { Prove(row, "unchanged", "The new destination holds no content; no project or library was created."); return; }
                var projects = Directory.Exists(root) ? Directory.GetFiles(root, library != null ? "*.al21" : "*.ap21", SearchOption.AllDirectories) : new string[0];
                if (projects.Length != 1) { Hold(row, "The destination holds partial content without exactly one project or library."); return; }
                if (library != null)
                {
                    if (library(projects[0])) Prove(row, "verifiedApplied", basis); else if (!((JArray)row["reasons"]).Any()) Hold(row, "The destination library differs from its checkpoint.");
                    return;
                }
                if (complete != null)
                {
                    bool proven = false;
                    var opened = OpenProject(row, projects[0], project => (proven = complete(project)) ? "complete" : null);
                    if (opened == null) return;
                    if (proven) Prove(row, "verifiedApplied", basis); else Hold(row, "The destination project is incomplete.");
                    return;
                }
                string expected = reference?.Invoke();
                if (expected == null) { if (!((JArray)row["reasons"]).Any()) Hold(row, "No verified reference exists for the destination content."); return; }
                var current = OpenProject(row, projects[0], null);
                if (current == null) return;
                if (current.Value<string>("fingerprint") == expected) Prove(row, "verifiedApplied", basis);
                else Hold(row, "The destination project differs from its reference.");
            }
        }
    }
}
