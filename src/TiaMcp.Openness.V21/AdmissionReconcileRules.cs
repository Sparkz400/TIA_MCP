using System;
using System.Collections.Generic;

namespace TiaMcp.Openness.V21
{
    // Decides an interrupted apply's project disposition from fresh read-only observations. TIA persists project
    // edits only by Save, so a project last saved before its checkpoint archive was written still is that checkpoint,
    // whatever the change kind; equal fingerprints alone cover only what the fingerprint sees.
    internal static class AdmissionReconcileRules
    {
        // Kinds whose change fingerprint needs prepared in-memory targets; a fresh session cannot recompute their outcome.
        private static readonly HashSet<string> TargetKinds = new HashSet<string>(new[] {
            "hmi_engineering", "hardware_attributes", "network_configure", "tag_set", "constant_set",
            "block_attributes_set", "project_attributes_set", "connection_set", "software_unit_publication_set",
            "db_snapshot_create", "db_snapshot_load_actual", "testsuite_application_import", "testsuite_system_import",
            "vci_workspace_create", "vci_workspace_delete", "vci_mapping_add", "vci_mapping_remove", "vci_synchronize",
            "webserver_page_set", "umac_role_set" }, StringComparer.Ordinal);

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
    }
}
