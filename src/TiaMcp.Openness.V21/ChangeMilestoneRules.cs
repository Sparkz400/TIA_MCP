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
                    : failed ? "The project edit did not complete; inspect recovery before anything else."
                    : state == "prepared" ? "Prepared only; nothing in the project has changed." : "In progress: " + state + ".",
                ["notCovered"] = "This reports the project edit only. Repository changes, MCP qualification and the build actually running in this Host are separate; see host_build_identity." };
        }

        private static JObject Row(string name, string status) => new JObject { ["milestone"] = name, ["status"] = status };
    }
}
