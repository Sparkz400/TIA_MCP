using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Shared #221 policy: a change compiles the same scope before and after its mutation and fails only on
    // introduced or worsened findings, a changed compile scope or incomplete diagnostics. Unchanged findings
    // elsewhere in that scope are reported, not treated as the change's failure.
    //
    // TIA's software compile is incremental and reports a warning only for an object it recompiles: a compile with
    // nothing changed reports none, and recompiling an object reports its old warnings again. A change that names its
    // own objects (blocks, types, tag tables) therefore gets warnings of other objects listed as revealed, not
    // introduced: they may have existed before, and they neither fail the change nor clear verified. Errors are
    // reported again on every compile until fixed, so a new error is always introduced.
    internal static class CompileBaselineRules
    {
        internal const int MaxListed = 50;
        internal const string RevealedNote = "Warnings of objects outside this change's own objects. TIA reports a warning only when it recompiles the object, so these may have existed before the change; they do not fail it. A warning missing from a later compile is likewise not proof that it is resolved.";

        internal static bool NoNewProblems(JObject before, JObject after) => Compare(before, after).Value<bool>("verified");

        // Kinds that passed with warnings before #221 keep doing so: a clean compile passes as it always did, and a
        // failing one passes only when it introduced no error. Tag-table imports use the stricter NoNewProblems.
        internal static bool Accepts(JObject before, JObject after, JObject comparison) =>
            CompilationEvidenceRules.IsSuccessful(after) || (comparison.Value<bool>("complete") && comparison.Value<int>("introducedErrorCount") == 0);

        /// <param name="ownObjectNames">Names of the blocks, types or tag tables the change itself writes; null or empty keeps every new finding introduced.</param>
        internal static JObject Compare(JObject before, JObject after, IEnumerable<string> ownObjectNames = null)
        {
            var own = (ownObjectNames ?? Enumerable.Empty<string>()).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            var result = new JObject { ["verified"] = false, ["complete"] = false, ["scopeUnchanged"] = false };
            if (!CompilationEvidenceRules.IsCompleted(before) || !CompilationEvidenceRules.IsCompleted(after)) return result;
            var old = ((JArray)before["targets"]).OfType<JObject>().ToDictionary(t => t.Value<string>("target"), StringComparer.Ordinal);
            var current = ((JArray)after["targets"]).OfType<JObject>().ToList();
            bool scope = old.Count == current.Count && current.All(t => old.ContainsKey(t.Value<string>("target")));
            result["scopeUnchanged"] = scope;
            if (!scope) return result;
            var existing = new List<string>(); var resolved = new List<string>(); var introduced = new List<string>(); var revealed = new List<string>();
            foreach (var target in current)
            {
                var available = Problems(old[target.Value<string>("target")]); var now = Problems(target);
                if (available == null || now == null) return result;
                foreach (var problem in now)
                    if (available.Remove(problem)) existing.Add(problem); else if (Revealed(problem, own)) revealed.Add(problem); else introduced.Add(problem);
                resolved.AddRange(available);
            }
            result["complete"] = true;
            result["verified"] = introduced.Count == 0;
            result["existingCount"] = existing.Count; result["resolvedCount"] = resolved.Count; result["introducedCount"] = introduced.Count;
            result["introducedErrorCount"] = introduced.Count(p => JArray.Parse(p)[0].Value<string>() == "Error");
            result["existing"] = Listed(existing); result["resolved"] = Listed(resolved); result["introduced"] = Listed(introduced);
            result["revealedCount"] = revealed.Count;
            if (revealed.Count > 0) { result["revealed"] = Listed(revealed); result["revealedNote"] = RevealedNote; }
            return result;
        }

        // A compiler row names its object as "Name (FC1)" or by its plain name somewhere in its parent path.
        private static bool Revealed(string problem, string[] own)
        {
            if (own.Length == 0) return false;
            var row = JArray.Parse(problem);
            if (row[0].Value<string>() != "Warning") return false;
            return !((JArray)row[2]).Any(segment => segment.Type == JTokenType.String && own.Any(name =>
                string.Equals(segment.Value<string>(), name, StringComparison.OrdinalIgnoreCase) ||
                segment.Value<string>().StartsWith(name + " (", StringComparison.OrdinalIgnoreCase)));
        }

        private static JArray Listed(List<string> rows) => new JArray(rows.Take(MaxListed).Select(r => JArray.Parse(r)));

        // Identity is state, message and the path of parent rows; a repeated identical finding counts each time.
        private static List<string> Problems(JObject target)
        {
            var rows = (JArray)target["messages"];
            var result = new List<string>(); var parents = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; int depth = row.Value<int>("depth");
                if (parents.Count > depth) parents.RemoveRange(depth, parents.Count - depth);
                parents.Add(row.Value<string>("path"));
                string state = row.Value<string>("state"), description = row.Value<string>("description");
                if (state != "Error" && state != "Warning") continue;
                if (i + 1 < rows.Count && rows[i + 1].Value<int>("depth") > depth && string.IsNullOrEmpty(description)) continue;
                string summary = "Compiling finished (errors: " + target.Value<int>("errorCount") + "; warnings: " + target.Value<int>("warningCount") + ")";
                if (i == rows.Count - 1 && depth == 0 && string.IsNullOrEmpty(row.Value<string>("path")) && description == summary) continue;
                if (string.IsNullOrEmpty(description)) return null;
                result.Add(new JArray(state, description, new JArray(parents)).ToString(Formatting.None));
            }
            // Missing diagnostics must not make a nonzero error count look unchanged.
            if (result.Count < target.Value<long>("errorCount") + target.Value<long>("warningCount")) return null;
            return result;
        }
    }
}
