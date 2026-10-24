using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Library transactions write several projects and global libraries; each admitted resource is proven on its own.
        private void AdmissionReconcileLibraryChange(AdmissionReconcileCheck check, string operation, JObject journalRow)
        {
            string directory = journalRow.Value<string>("path"), id = Path.GetFileName(directory).Substring("change-".Length);
            JObject journal;
            try { journal = LibraryJournalStore.Read(Path.Combine(directory, "journal.json"), id); }
            catch (Exception error) when (error is InvalidDataException || error is Newtonsoft.Json.JsonException) { check.HoldAll("The library journal is unreadable: " + error.Message); return; }
            var checkpoints = (journal["checkpoints"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
            check.Evidence(journalRow, "journal", new JObject { ["state"] = journal["state"], ["checkpoints"] = checkpoints.Length, ["recoveryAttempts"] = journal["recoveryAttempts"]?.DeepClone() });
            Func<JObject, bool> valid = c => File.Exists(c.Value<string>("archivePath")) && ChangeFileHash(c.Value<string>("archivePath")) == c.Value<string>("sha256");
            var recorded = new HashSet<string>(checkpoints.Select(c => Path.GetFullPath(c.Value<string>("archivePath"))), StringComparer.OrdinalIgnoreCase);
            bool unrecorded = Directory.GetFiles(directory, "*.za?21").Any(f => !recorded.Contains(Path.GetFullPath(f)));
            if (checkpoints.All(valid) && !unrecorded) check.Prove(journalRow, "verifiedApplied", "Library journal is readable and bound to its change; every recorded checkpoint archive matches its hash.");
            else check.Hold(journalRow, "A checkpoint archive is missing, changed or unrecorded in the library journal.");
            Func<string, JObject> checkpointFor = original => checkpoints.FirstOrDefault(c => SameSimulationPath(c.Value<string>("originalPath"), original) ||
                c.Value<string>("kind") == "library" && SameSimulationPath(Path.GetDirectoryName(c.Value<string>("originalPath") ?? ""), original));

            if (operation == "change_apply")
            {
                foreach (var row in check.Rows("checkpointCompileSave").Concat(check.Rows("libraryResource")))
                {
                    var checkpoint = checkpointFor(row.Value<string>("path"));
                    if (checkpoint == null) { check.NotSavedSince(row, "The project is only read by this library transaction and was not saved since admission."); continue; }
                    if (!valid(checkpoint)) { check.Hold(row, "Its checkpoint archive is missing or changed."); continue; }
                    string reference = check.ArchiveFingerprint(row, checkpoint.Value<string>("archivePath"));
                    var current = check.OpenProject(row, row.Value<string>("path"), null);
                    if (reference == null || current == null) continue;
                    if (current.Value<string>("fingerprint") == reference) check.Prove(row, "unchanged", "Fresh fingerprint equals the retrieved pre-transaction checkpoint.");
                    else check.Hold(row, "The project differs from its pre-transaction checkpoint.");
                }
                foreach (var row in check.Rows("libraryResourceAndCompanions"))
                {
                    var checkpoint = checkpointFor(row.Value<string>("path"));
                    if (checkpoint == null) { check.NotWrittenSince(row, "The library is only read by this transaction; no file under it was written since admission."); continue; }
                    if (!valid(checkpoint)) { check.Hold(row, "Its checkpoint archive is missing or changed."); continue; }
                    string reference = AdmissionReconcileLibraryRevision(check, row, checkpoint.Value<string>("archivePath"), true);
                    string current = reference == null ? null : AdmissionReconcileLibraryRevision(check, row, checkpoint.Value<string>("originalPath"), false);
                    if (reference == null || current == null) continue;
                    if (current == reference) check.Prove(row, "unchanged", "Fresh library revision equals the retrieved pre-transaction checkpoint.");
                    else check.Hold(row, "The global library differs from its pre-transaction checkpoint.");
                }
                foreach (var row in check.Rows("retainedSourceSnapshot"))
                    check.NotWrittenSince(row, "The retained source snapshot was not written since admission.");
                foreach (var row in check.Rows("createNewDestination"))
                    check.Destination(row, null, null, _ => false);
                return;
            }
            var attempts = (journal["recoveryAttempts"] as JArray)?.LastOrDefault()?["resources"] as JArray ?? new JArray();
            foreach (var row in check.Rows("preserveOriginalDuringRecovery"))
            {
                // Recovery only retrieves archives into new directories; it never opens, saves or closes an original.
                if (row.Value<string>("kind") == "project") check.NotSavedSince(row, "Recovery never writes the original project; it was not saved since admission.");
                else check.NotWrittenSince(row, "Recovery never writes the original library; no file under it was written since admission.");
            }
            foreach (var row in check.Rows("readCheckpoint"))
            {
                var checkpoint = checkpoints.FirstOrDefault(c => SameSimulationPath(c.Value<string>("archivePath"), row.Value<string>("path")));
                if (checkpoint != null && valid(checkpoint)) check.Prove(row, "unchanged", "The checkpoint archive still has its recorded SHA-256.");
                else check.Hold(row, "The checkpoint archive is missing, changed or not recorded in the journal.");
            }
            foreach (var row in check.Rows("createNewDestination"))
            {
                var attempt = attempts.OfType<JObject>().FirstOrDefault(r => SameSimulationPath(r.Value<string>("destination"), row.Value<string>("path")));
                var checkpoint = attempt == null ? null : checkpoints.FirstOrDefault(c => SameSimulationPath(c.Value<string>("archivePath"), attempt.Value<string>("archivePath")));
                if (row.Value<string>("kind") == "project")
                    check.Destination(row, () => checkpoint != null && valid(checkpoint) ? check.ArchiveFingerprint(row, checkpoint.Value<string>("archivePath")) : null,
                        "The recovered project equals its retrieved checkpoint (fresh fingerprints are equal).");
                else check.Destination(row, null, "The recovered library equals its retrieved checkpoint (fresh library revisions are equal).", null, path => {
                    if (checkpoint == null || !valid(checkpoint)) return false;
                    string reference = AdmissionReconcileLibraryRevision(check, row, checkpoint.Value<string>("archivePath"), true);
                    return reference != null && reference == AdmissionReconcileLibraryRevision(check, row, path, false);
                });
            }
        }

        /// <summary>Location-independent revision of a global library, opened read-only or retrieved from an archive into a new reconcile directory.</summary>
        private string AdmissionReconcileLibraryRevision(AdmissionReconcileCheck check, JObject row, string path, bool archive)
        {
            try
            {
                var library = archive ? _portal.GlobalLibraries.Retrieve(new FileInfo(path), new DirectoryInfo(check.NextRetrieveDirectory()), OpenMode.ReadOnly)
                    : _portal.GlobalLibraries.Open(new FileInfo(path), OpenMode.ReadOnly);
                try
                {
                    string revision = LibraryRevision(new ManagedLibrary { Native = library, Global = library });
                    check.Evidence(row, archive ? "checkpointLibraryRevision" : "libraryRevision", revision);
                    return revision;
                }
                finally { (library as UserGlobalLibrary)?.Close(); }
            }
            catch (Exception error) { check.Hold(row, "The library could not be read for comparison: " + error.Message); return null; }
        }
    }
}
