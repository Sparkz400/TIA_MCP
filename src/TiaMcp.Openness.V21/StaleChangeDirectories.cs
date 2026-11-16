using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Removes change directories that can never be applied: preparations (state prepared or prepare_failed) that no
    /// worker holds in memory anymore. change_apply needs the in-memory change of the worker that prepared it, so such a
    /// directory is dead once that worker is gone. A change that ever reached checkpointing, applying, applied, restoring,
    /// restored, recovery_required, not_applied or rolled_back is never touched: change_status, change_restore and admission
    /// reconciliation read those. Each removal is logged to pruned-changes.jsonl in the changes directory.</summary>
    internal static class StaleChangeDirectories
    {
        internal static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);
        internal const int MaxPerSweep = 200;
        private static readonly Regex Name = new Regex("\\Achange-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant);
        private static readonly string[] Prunable = { "prepared", "prepare_failed" };

        /// <summary>Whether a journal (and its last write) lets the directory be removed. Pure, for tests.</summary>
        internal static bool CanPrune(JObject journal, DateTime journalWrittenUtc, DateTime nowUtc, ISet<string> heldIds)
        {
            if (journal == null) return false;
            string id = journal.Value<string>("changeId"), state = journal.Value<string>("state");
            if (string.IsNullOrEmpty(id) || heldIds.Contains(id) || !Prunable.Contains(state, StringComparer.Ordinal)) return false;
            // A preparation that ever recorded a checkpoint, archive or idempotency key went further than prepare.
            if (journal["idempotencyKey"]?.Type == JTokenType.String || journal["recoveryArchive"]?.Type == JTokenType.String) return false;
            return nowUtc - journalWrittenUtc >= MinimumAge;
        }

        /// <summary>Sweeps one changes directory; never throws, since a failed cleanup must not fail the preparation that triggered it.</summary>
        internal static int Sweep(string changesDirectory, ISet<string> heldIds, DateTime nowUtc)
        {
            int removed = 0;
            try
            {
                var root = new DirectoryInfo(changesDirectory);
                if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
                foreach (var directory in root.EnumerateDirectories("change-*"))
                {
                    if (removed >= MaxPerSweep) break;
                    if (!Name.IsMatch(directory.Name) || (directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var journalFile = new FileInfo(Path.Combine(directory.FullName, "journal.json"));
                    if (!journalFile.Exists || journalFile.Length > 64L * 1024 * 1024) continue;
                    JObject journal;
                    try { journal = JObject.Parse(File.ReadAllText(journalFile.FullName)); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException) { continue; }
                    if (journal.Value<string>("changeId") != directory.Name.Substring("change-".Length)) continue;
                    if (!CanPrune(journal, journalFile.LastWriteTimeUtc, nowUtc, heldIds)) continue;
                    // No reparse points anywhere below, so the delete cannot leave the changes directory.
                    if (directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(entry => (entry.Attributes & FileAttributes.ReparsePoint) != 0)) continue;
                    try
                    {
                        directory.Delete(true);
                        removed++;
                        var record = new JObject { ["changeId"] = journal["changeId"], ["kind"] = journal["kind"], ["state"] = journal["state"],
                            ["journalWrittenUtc"] = journalFile.LastWriteTimeUtc.ToString("O"), ["prunedAtUtc"] = nowUtc.ToString("O"),
                            ["reason"] = "Preparation older than " + MinimumAge.TotalHours + " h that no worker holds; it can never be applied." };
                        File.AppendAllText(Path.Combine(root.FullName, "pruned-changes.jsonl"), record.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            return removed;
        }
    }
}
