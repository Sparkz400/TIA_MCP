using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // #219: what a user needs first: is the project edit done, which step got how far, and what this does not cover.
    internal static class ChangeMilestoneRules
    {
        internal static JObject From(string state, JArray stages, JObject compilation)
        {
            bool Has(string stage) => stages.Any(s => (string)s == stage);
            bool HasSuffix(string suffix) => stages.Any(s => ((string)s ?? "").EndsWith(suffix, System.StringComparison.Ordinal));
            bool failed = state == "recovery_required" || state == "rolled_back" || state == "not_applied";
            string Step(bool done, bool reachedLater) => done ? "done" : failed && !reachedLater ? "notReached" : state == "prepared" ? "pending" : "notReached";
            bool mutated = Has("mutation_applied") || HasSuffix("_imported") || HasSuffix("_created") || Has("compile_only_requested");
            bool compiled = Has("compile_finished");
            bool saved = Has("changed_project_saved") || Has("scoped_restore_saved");
            var milestones = new JArray(
                Row("prepared", state == "preparing" ? "pending" : "done"),
                Row("checkpointSaved", Step(Has("prechange_project_saved"), mutated)),
                Row("recoveryArchiveCreated", Step(Has("recovery_archive_created"), mutated)),
                Row("projectChanged", Step(mutated, compiled)),
                Row("readbackVerified", Step(HasSuffix("_verified") && !Has("scoped_restore_compile_verified") || HasSuffix("verified_content"), saved)),
                Row("compiled", compilation == null && state == "applied" ? "skipped" : Step(compiled, saved)),
                Row("compilerSucceeded", compilation?["compilerSucceeded"] == null ? "unknown" : compilation.Value<bool>("compilerSucceeded") ? "yes" : "no"),
                Row("saved", Step(saved, false)));
            return new JObject {
                ["projectEditComplete"] = state == "applied" && saved || state == "restored",
                ["state"] = state, ["milestones"] = milestones,
                ["summary"] = state == "applied" ? (saved ? "The project edit is applied and saved." : "The project edit is applied but not saved.") +
                        (compilation?["compilerSucceeded"]?.Type == JTokenType.Boolean && !compilation.Value<bool>("compilerSucceeded") ? " The project still has compiler errors; see compile.baselineComparison." : "")
                    : state == "restored" ? "The change was restored."
                    : state == "not_applied" ? "The change failed before the project was changed; prepare a new change."
                    : failed ? "The project edit did not complete; inspect recovery before anything else."
                    : state == "prepared" ? "Prepared only; nothing in the project has changed." : "In progress: " + state + ".",
                ["notCovered"] = "This reports the project edit only. Repository changes, MCP qualification and the build actually running in this Host are separate; see host_build_identity." };
        }

        // A replayed apply reports these recorded outcomes; anything else was attempted and needs explicit recovery.
        internal static bool IsResolvedOutcome(string state) => state == "applied" || state == "restored" || state == "rolled_back" || state == "not_applied";

        // While a change is in one of these states, its TIA process may hold its unsaved edits: no other checkpoint save may run.
        internal static bool IsUnresolved(string state) => state == "recovery_required" || state == "checkpointing" || state == "applying" || state == "restoring";

        /// <summary>True when the change is already restored and the caller only reports it. The previewHash is checked first.</summary>
        internal static bool RestoreAlreadyDone(string state, string requestedPreviewHash, string previewHash)
        {
            if (requestedPreviewHash != previewHash) throw new BridgeException("PreviewMismatch", "Wrong previewHash.");
            return state == "restored";
        }

        internal static void RequireRestorable(string state, bool ownsPortal, Func<bool> archiveIntact, Func<JObject> view)
        {
            if (!ownsPortal) throw new BridgeException("OwnedSessionRequired", "Automatic archive retrieval is limited to worker-owned TIA instances. The checkpoint archive is available for manual recovery in a user-owned instance.", view());
            if (state != "applied" && state != "recovery_required") throw new BridgeException("InvalidChangeState", "This change has no applied state to recover.");
            if (!archiveIntact()) throw new BridgeException("CheckpointInvalid", "The recorded recovery archive is missing or its hash has changed.");
        }

        internal static void RequireSameAttachment(int? preparedProcessId, int? currentProcessId)
        {
            if (preparedProcessId != currentProcessId) throw new BridgeException("SessionChanged", "Change belongs to a different TIA attachment.");
        }

        internal static void RequireSameProject(string projectPath, string changeProjectPath)
        {
            if (!string.Equals(projectPath, changeProjectPath, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("ProjectChanged", "Change belongs to a different project file.");
        }

        internal static void RequireOffline(string devicePath, bool offline)
        {
            if (!offline) throw new BridgeException("OfflineRequired", "Offline engineering changes require the target to be offline. No disconnect or CPU action was performed: " + devicePath);
        }

        private static JObject Row(string name, string status) => new JObject { ["milestone"] = name, ["status"] = status };
    }
}
