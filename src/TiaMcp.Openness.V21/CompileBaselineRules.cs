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
    internal static class CompileBaselineRules
    {
        internal const int MaxListed = 50;

        internal static bool NoNewProblems(JObject before, JObject after) => Compare(before, after).Value<bool>("verified");

        // Kinds that passed with warnings before #221 keep doing so: a clean compile passes as it always did, and a
        // failing one passes only when it introduced no error. Tag-table imports use the stricter NoNewProblems.
        internal static bool Accepts(JObject before, JObject after, JObject comparison) =>
            CompilationEvidenceRules.IsSuccessful(after) || (comparison.Value<bool>("complete") && comparison.Value<int>("introducedErrorCount") == 0);

        internal static JObject Compare(JObject before, JObject after)
        {
            var result = new JObject { ["verified"] = false, ["complete"] = false, ["scopeUnchanged"] = false };
            if (!CompilationEvidenceRules.IsCompleted(before) || !CompilationEvidenceRules.IsCompleted(after)) return result;
            var old = ((JArray)before["targets"]).OfType<JObject>().ToDictionary(t => t.Value<string>("target"), StringComparer.Ordinal);
            var current = ((JArray)after["targets"]).OfType<JObject>().ToList();
            bool scope = old.Count == current.Count && current.All(t => old.ContainsKey(t.Value<string>("target")));
            result["scopeUnchanged"] = scope;
            if (!scope) return result;
            var existing = new List<string>(); var resolved = new List<string>(); var introduced = new List<string>();
            foreach (var target in current)
            {
                var available = Problems(old[target.Value<string>("target")]); var now = Problems(target);
                if (available == null || now == null) return result;
                foreach (var problem in now)
                    if (available.Remove(problem)) existing.Add(problem); else introduced.Add(problem);
                resolved.AddRange(available);
            }
            result["complete"] = true;
            result["verified"] = introduced.Count == 0;
            result["existingCount"] = existing.Count; result["resolvedCount"] = resolved.Count; result["introducedCount"] = introduced.Count;
            result["introducedErrorCount"] = introduced.Count(p => JArray.Parse(p)[0].Value<string>() == "Error");
            result["existing"] = Listed(existing); result["resolved"] = Listed(resolved); result["introduced"] = Listed(introduced);
            return result;
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
