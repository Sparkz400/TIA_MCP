using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Decides an interrupted apply's project disposition from fresh read-only observations. TIA persists project
    // edits only by Save, so a project last saved before its checkpoint archive was written still is that checkpoint,
    // whatever the change kind; equal fingerprints alone cover only what the fingerprint sees.
    internal static class AdmissionReconcileRules
    {
        // Kinds whose change fingerprint needs prepared in-memory targets; a fresh session cannot recompute their outcome.
        private static readonly HashSet<string> TargetKinds = new HashSet<string>(new[] {
            "hmi_engineering", "hmi_text_list_import", "hmi_classic_import", "hmi_external_tag_create", "hmi_screen_create", "hmi_dynamization_create", "hardware_attributes", "tag_set", "constant_set",
            "block_attributes_set", "project_attributes_set", "connection_set", "software_unit_publication_set",
            "db_snapshot_create", "db_snapshot_load_actual", "simulation_download_online", "testsuite_application_import", "testsuite_system_import",
            "vci_workspace_create", "vci_workspace_delete", "vci_mapping_add", "vci_mapping_remove", "vci_synchronize", "vci_conflict_resolve",
            "webserver_page_set", "umac_role_set", "opcua_server_settings_set" }, StringComparer.Ordinal);

        internal static bool OutcomeRecomputable(string kind) => !TargetKinds.Contains(kind) && !kind.StartsWith("to_", StringComparison.Ordinal);

        /// <summary>"unchanged", "verifiedApplied" or null (held); basis is the proof or the reason for holding.</summary>
        internal static string ApplyProject(bool equalsCheckpoint, DateTime lastModifiedUtc, DateTime checkpointWrittenUtc, bool appliedOutcomeMatches, out string basis)
        {
            if (lastModifiedUtc.Kind != DateTimeKind.Utc || checkpointWrittenUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Save and checkpoint times must be UTC.");
            if (equalsCheckpoint && lastModifiedUtc < checkpointWrittenUtc)
            {
                basis = "Fresh fingerprint equals the retrieved pre-mutation checkpoint and the project was last saved before that archive was written; any unsaved mutation was lost with the interrupted session.";
                return "unchanged";
            }
            if (appliedOutcomeMatches)
            {
                basis = "Journal recorded the applied outcome and the fresh project fingerprint equals it.";
                return "verifiedApplied";
            }
            basis = equalsCheckpoint
                ? "The project was saved after its checkpoint archive was written; an equal fingerprint does not prove the change was not persisted."
                : "The project differs from its checkpoint and does not match a recorded applied outcome.";
            return null;
        }

        // Project.LastModified documents no kind. Unspecified takes the later of its UTC and local readings,
        // so an ambiguous save time can only hold, never release.
        internal static DateTime SavedUtc(DateTime raw) => raw.Kind == DateTimeKind.Local ? raw.ToUniversalTime() : raw.Kind == DateTimeKind.Utc ? raw
            : new DateTime(Math.Max(DateTime.SpecifyKind(raw, DateTimeKind.Utc).Ticks, DateTime.SpecifyKind(raw, DateTimeKind.Local).ToUniversalTime().Ticks), DateTimeKind.Utc);

        // Runtime files PLCSIM creates in an instance's storage at power-on (seen in every provisioning storage on this PC).
        internal static readonly string[] ProvisionRuntimeFiles = { "retain.pms", "sim_hwdb.ini" };

        // A failed provisioning's storage from metadata only; opaque contents are never read. Every entry the recovery baseline
        // recorded outside the memory card (whose own manifest is compared separately) is unchanged, and every new entry is this
        // attempt's own evidence or a PLCSIM runtime file. Returns each difference; empty means the storage holds.
        internal static List<string> ProvisionStorageDifferences(JArray baseline, JArray current, string attemptId)
        {
            bool Card(string path) => path == "SIMATIC_MC" || path.StartsWith("SIMATIC_MC/", StringComparison.Ordinal);
            var before = baseline.OfType<JObject>().Where(e => !Card(e.Value<string>("path"))).ToDictionary(e => e.Value<string>("path"), StringComparer.OrdinalIgnoreCase);
            var after = current.OfType<JObject>().Where(e => !Card(e.Value<string>("path"))).ToDictionary(e => e.Value<string>("path"), StringComparer.OrdinalIgnoreCase);
            var own = new HashSet<string>(new[] { "simulation-provision-" + attemptId + ".json", "simulation-provision-" + attemptId + ".checkpoints.jsonl",
                ".provision.claim", "before-provision.zap21", "simulation-empty-card-" + attemptId }.Concat(ProvisionRuntimeFiles), StringComparer.OrdinalIgnoreCase);
            var differences = new List<string>();
            foreach (var entry in before)
            {
                if (!after.TryGetValue(entry.Key, out var now)) { differences.Add("Missing baseline entry " + entry.Key + "."); continue; }
                bool directory = entry.Value.Value<bool>("directory");
                // A directory's own write time follows its children, which are compared one by one.
                if (now.Value<bool>("directory") != directory || !directory && (now.Value<long?>("bytes") != entry.Value.Value<long?>("bytes")
                    || now.Value<long>("lastWriteUtcTicks") != entry.Value.Value<long>("lastWriteUtcTicks")))
                    differences.Add("Baseline entry " + entry.Key + " changed.");
            }
            foreach (string path in after.Keys.Where(p => !before.ContainsKey(p)))
                if (!own.Contains(path.Split('/')[0]) || path.Contains("/") && !path.StartsWith("simulation-empty-card-" + attemptId + "/", StringComparison.OrdinalIgnoreCase))
                    differences.Add("Unexpected new entry " + path + ".");
            return differences;
        }

        // The provisioning stream durably writes credentialSubmissionMayHaveOccurred=true before SetPlcMasterSecret is entered, so a
        // readable stream that never records it, nor an opened credential, proves no credential reached the runtime protection store.
        internal static string ProvisionCredentialRefusal(IEnumerable<string> streamLines)
        {
            var records = new List<JObject>();
            foreach (string line in streamLines)
                try { records.Add(JObject.Parse(line)); }
                catch (Newtonsoft.Json.JsonException) { return "A provisioning checkpoint record is unreadable; a torn record may be the credential intent."; }
            if (records.Count == 0 || records[0].Value<string>("phase") != "attemptPrepared") return "The provisioning checkpoint stream is missing or does not start with its attempt.";
            foreach (var record in records)
                foreach (string field in new[] { "credentialOpened", "credentialSubmissionAttempted", "credentialSubmissionMayHaveOccurred" })
                    if (record[field]?.Type != JTokenType.Boolean || record.Value<bool>(field))
                        return "The provisioning checkpoint stream records " + field + " at " + record.Value<string>("phase") + "; the runtime protection store may hold the credential.";
            return null;
        }
    }
}
