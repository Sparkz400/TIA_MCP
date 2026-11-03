using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    internal static class ClassicScreenRecoveryRules
    {
        internal const string VerificationMode = "classic-screen-terminal-in-open-project-v1";

        internal static void RequireUnlinkedArtifact(string path)
        {
            for (FileSystemInfo item = new FileInfo(path); item != null;
                item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Screen artifacts cannot follow symbolic links or junctions.");
        }

        internal static JObject TerminalManifest(JObject journal)
        {
            if (journal?.Value<string>("kind") != "hmi_classic_screen_edit"
                || journal["preview"]?["payload"]?.Value<string>("operation") != "classic_xml_import")
                throw new ArgumentException("Only the bounded Classic regular-screen import has in-place terminal reconciliation.");
            string state = journal.Value<string>("state");
            var stages = new HashSet<string>((journal["stages"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            string[] required = state == "applied"
                ? new[] { "recovery_archive_created", "hmi_precise_readback_verified", "compile_finished", "hmi_unrelated_content_preserved", "changed_project_saved", "hmi_saved_screen_content_verified" }
                : state == "rolled_back"
                    ? new[] { "recovery_archive_created", "hmi_rollback_imported", "hmi_rollback_compiled", "hmi_rollback_saved", "hmi_rollback_verified" }
                    : throw new ArgumentException("The change has no verified terminal screen outcome; an in-progress or failed rollback cannot release ownership.");
            if (required.Any(s => !stages.Contains(s)) || string.IsNullOrWhiteSpace(journal.Value<string>("afterFingerprint")))
                throw new ArgumentException("The terminal journal lacks required native verification stages.");
            var evidence = journal["hmiEvidence"] as JObject;
            // Legacy single-screen journals predate member receipts; a batch must never take that fallback.
            if ((evidence?["screens"] != null || journal["preview"]["payload"]["additionalScreens"] != null
                || evidence?["proposedEffect"]?["screenCount"] != null) && !(evidence?["screens"] is JArray))
                throw new ArgumentException("The screen workflow requires its retained member receipts.");
            if (evidence?["screens"] is JArray members)
            {
                var requests = ClassicScreenBatchRules.Members((JObject)journal["preview"]["payload"]);
                if (members.Count != requests.Length) throw new ArgumentException("Every prepared batch member requires a terminal receipt.");
                bool touched = false, untouched = false;
                for (int i = 0; i < members.Count; i++)
                {
                    if (!(members[i] is JObject) || !JToken.DeepEquals(members[i]["target"], requests[i]["target"])) throw new ArgumentException("A batch receipt identifies a different screen.");
                    string memberState = members[i].Value<string>("state");
                    bool dispatched = stages.Contains("hmi_screen_" + i + "_import_started");
                    if (dispatched != (memberState != "prepared"))
                        throw new ArgumentException("A member receipt contradicts its native dispatch stage.");
                    if (state == "applied")
                    {
                        if (memberState != "readback_verified" || !stages.Contains("hmi_screen_" + i + "_readback_verified"))
                            throw new ArgumentException("Every applied screen requires its own verified native readback.");
                    }
                    else if (memberState == "prepared") untouched = true;
                    else
                    {
                        if (untouched || memberState != "rollback_readback_verified" || !stages.Contains("hmi_screen_" + i + "_rollback_readback_verified"))
                            throw new ArgumentException("Every attempted screen requires its own verified rollback; untouched members must follow the attempted prefix.");
                        touched = true;
                    }
                }
                if (state == "rolled_back" && !touched) throw new ArgumentException("A rollback receipt must identify at least one restored attempted screen.");
            }
            var compilation = state == "applied" ? journal["compile"] as JObject : evidence?["rollbackCompile"] as JObject;
            if (compilation?.Value<int?>("errorCount") != 0
                || !ClassicScreenCompileRules.ProblemsUnchanged(evidence?["baselineCompile"] as JObject, compilation))
                throw new ArgumentException("The terminal outcome lacks complete, unchanged successful compiler diagnostics.");
            var manifest = evidence?[state == "applied" ? "after-save" : "rollback-after-save"] as JObject;
            RequireManifest(manifest);
            return manifest;
        }

        internal static Dictionary<string, string> RequireManifest(JObject manifest)
        {
            if (!HmiEngineeringRules.ScopeAccepted("hmi_classic_screen_edit", manifest)
                || !(manifest?["artifacts"] is JObject artifacts))
                throw new ArgumentException("A complete declared Classic screen scope is required for recovery.");
            var hashes = artifacts.Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>("contentSha256"), StringComparer.Ordinal);
            string hash = HmiPreservationRules.ScopeHash("classic", hashes, true, HmiPreservationRules.ClassicScreenPolicy);
            if (hash != manifest.Value<string>("contentHash") || hash != manifest.Value<string>("observedArtifactsHash"))
                throw new ArgumentException("The recovery manifest's artifact set does not match its recorded content hash.");
            return hashes;
        }

        internal static void RequireRollbackScope(JObject baseline, JObject current, string target)
        {
            HmiPreservationRules.RequireUnrelatedPreserved(RequireManifest(baseline), RequireManifest(current), new[] { target }, Array.Empty<string>());
        }

        internal static void RequireRestoredScope(JObject baseline, JObject current)
        {
            HmiPreservationRules.RequireUnrelatedPreserved(RequireManifest(baseline), RequireManifest(current), Array.Empty<string>(), Array.Empty<string>());
        }
    }
}
