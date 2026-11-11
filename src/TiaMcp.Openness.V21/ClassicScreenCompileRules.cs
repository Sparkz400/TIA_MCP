using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    // V21 Classic labels the timestamp/size container with its children's Warning state.
    // It is compile metadata, not a diagnostic. Keep original evidence; normalize a clone only.
    internal static class ClassicScreenCompileRules
    {
        internal const string PathFailureText = "Cannot compile. Cannot access the project path.";
        internal const string GenerationSkippedText = "No HMI runtime-relevant changes have been made since the last error-free HMI compilation. New compilation is not necessary.";

        // The compiler could not run; its result says nothing about the project content. The name is historical: on
        // 11 November 2026 one apply got this result for the post-import and for the rollback compile, two minutes apart.
        internal static bool IsTransientPathFailure(JObject result) => result?["targets"] is JArray targets
            && targets.OfType<JObject>().Any(t => (t["messages"] as JArray)?.OfType<JObject>().Any(m =>
                m.Value<string>("state") == "Error" && m.Value<string>("description") == PathFailureText) == true);

        // V21 answers a compile of an HMI without runtime-relevant changes from its last result. Such a result is a valid
        // diagnostics reference (observed 10 November 2026: equal to a later real rollback compile), but generated nothing,
        // so it does not show that the compiler can run in this TIA session.
        internal static bool SkippedGeneration(JObject result) => result?["targets"] is JArray targets
            && targets.OfType<JObject>().Any(t => (t["messages"] as JArray)?.OfType<JObject>().Any(m =>
                m.Value<string>("state") == "Information" && m.Value<string>("description") == GenerationSkippedText) == true);

        /// <summary>Why the compile after the import does not verify the edit, when the reason is the compiler rather than the edit; otherwise null.</summary>
        internal static string FinalCompileUnavailable(JObject compilation) => IsTransientPathFailure(compilation)
            ? "The HMI compiler could not run after the import (\"" + PathFailureText + "\"). Its result says nothing about the edited screens. "
                + "Verified screen rollback is attempted; inspect the retained outcome."
            : null;

        /// <summary>Null when the rollback compile restored the baseline diagnostics exactly; otherwise the reason, naming a compiler that could not run apart from a real difference.</summary>
        internal static string RollbackCompileFailure(JObject baseline, JObject rollback, bool scopeRestored)
        {
            if (IsTransientPathFailure(rollback))
                return "The HMI compiler could not run for the rollback compile (\"" + PathFailureText + "\"), so the restored diagnostics could not be compared with the baseline."
                    + (scopeRestored ? " Every attempted screen was re-imported and read back, and the regular-screen scope equals the pre-import capture. The rollback was not saved: the saved project is still the checkpoint."
                        + " Closing the project without saving returns to it; compile the HMI again after reopening." : "");
            return Completed(rollback) && ProblemsUnchanged(baseline, rollback) ? null : "Rollback compiler diagnostics do not match the successful baseline.";
        }

        // A returned compiler result with diagnostics is distinct from a compiler
        // that could not run. Keep the complete original tree in the journal.
        internal static bool Completed(JObject result)
        {
            var tree = ComparisonTree(result);
            if (tree == null || !HmiTextListImportRules.CompileProblemsUnchanged(tree, tree) || IsTransientPathFailure(result)) return false;
            long errors = 0, warnings = 0;
            foreach (JObject target in result["targets"])
            {
                var messages = (JArray)target["messages"];
                string text = messages.Last?.Value<string>("description") ?? "";
                // Classic V21 ends an HMI compile that has errors with "Compiling aborted (...)" (observed 9 November 2026,
                // 144 errors / 169 warnings, complete untruncated rows). Its counters are checked exactly like "finished".
                var summary = Regex.Match(text, @"\A(?:Compiling (?:finished|aborted) \(errors: (?<e>[0-9]+); warnings: (?<w>[0-9]+)\)|Compilation completed: (?<e>[0-9]+) errors, (?<w>[0-9]+) warnings\.)\z");
                if (!summary.Success || !long.TryParse(summary.Groups["e"].Value, out long e) || !long.TryParse(summary.Groups["w"].Value, out long w)
                    || e != target.Value<int>("errorCount") || w != target.Value<int>("warningCount")) return false;
                errors += e; warnings += w;
            }
            return errors == result.Value<int>("errorCount") && warnings == result.Value<int>("warningCount");
        }

        internal static bool NoNewProblems(JObject before, JObject after) => NoNewProblems(before, after, null, out _);

        // V21 stops an HMI compile that finds errors ("Compiling aborted") before some later checks. When an edit removes the
        // last error, those checks run and report findings that existed all along (observed 10 November 2026: 110 errors -> 0
        // revealed 18 warnings in other screens and project graphics). With editedScreens given, such a baseline-aborted,
        // now error-free target accepts new warnings whose owner hierarchy names no edited screen and returns them in
        // `unmasked`. Every error, every new finding inside an edited screen, and every other case still fails.
        internal static bool NoNewProblems(JObject before, JObject after, ICollection<string> editedScreens, out JArray unmasked)
        {
            unmasked = new JArray();
            if (!Completed(before) || !Completed(after)) return false;
            var b = ProblemSet(before); var a = ProblemSet(after);
            if (!b.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(a.Keys.OrderBy(x => x, StringComparer.Ordinal))) return false;
            foreach (var target in a)
            {
                var bt = before["targets"].Single(t => t.Value<string>("target") == target.Key);
                var at = after["targets"].Single(t => t.Value<string>("target") == target.Key);
                bool unmasking = editedScreens != null && editedScreens.Count > 0 && bt.Value<int>("errorCount") > 0 && at.Value<int>("errorCount") == 0
                    && (bt["messages"] as JArray)?.Last?.Value<string>("description")?.StartsWith("Compiling aborted (", StringComparison.Ordinal) == true;
                var remaining = new List<string>(b[target.Key]);
                foreach (string diagnostic in target.Value)
                {
                    if (remaining.Remove(diagnostic)) continue;
                    if (!unmasking || !OutsideEditedScreens(diagnostic, editedScreens, out JArray row)) return false;
                    unmasked.Add(row);
                }
                if (at.Value<int>("errorCount") > bt.Value<int>("errorCount") || !unmasking && at.Value<int>("warningCount") > bt.Value<int>("warningCount")) return false;
            }
            return true;
        }

        // A ProblemSet entry is [state, description, owner hierarchy as a JSON array]; only a warning none of whose owners is an edited screen qualifies.
        private static bool OutsideEditedScreens(string diagnostic, ICollection<string> editedScreens, out JArray row)
        {
            row = JArray.Parse(diagnostic);
            if (row.Count != 3 || row[0].Value<string>() != "Warning" || row[2].Type != JTokenType.String) return false;
            var owners = JArray.Parse(row[2].Value<string>());
            return owners.Count > 0 && owners.All(o => o.Type == JTokenType.Null || !editedScreens.Contains(o.Value<string>()));
        }

        /// <summary>The native names of every screen a hmi_classic_screen_edit payload edits (target and additionalScreens),
        /// taken from the last segment of each required objectPath (escaped with Uri.EscapeDataString), never from the optional name.</summary>
        internal static HashSet<string> EditedScreenNames(JObject payload)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<JToken> { payload?["target"] };
            if (payload?["additionalScreens"] is JArray extra) targets.AddRange(extra.Select(m => m["target"]));
            foreach (var target in targets)
            {
                string path = (target as JObject)?.Value<string>("objectPath");
                int slash = path?.LastIndexOf('/') ?? -1;
                string name = slash < 0 ? null : Uri.UnescapeDataString(path.Substring(slash + 1));
                if (string.IsNullOrEmpty(name) || path.IndexOf("/screens/", StringComparison.Ordinal) < 0)
                    throw new ArgumentException("Every edited screen needs an exact screen objectPath.");
                names.Add(name);
            }
            return names;
        }

        private static Dictionary<string, List<string>> ProblemSet(JObject compilation)
        {
            var tree = ComparisonTree(compilation);
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (int t = 0; t < tree["targets"].Count(); t++)
            {
                var rows = (JArray)tree["targets"][t]["messages"];
                var problems = new List<string>();
                for (int i = 0; i < rows.Count - 1; i++)
                {
                    var row = rows[i]; string state = row.Value<string>("state");
                    if (state != "Error" && state != "Warning") continue;
                    if (string.IsNullOrEmpty(row.Value<string>("description")) && rows[i + 1].Value<int>("depth") > row.Value<int>("depth")) continue;
                    problems.Add(new JArray(state, row["description"], row["path"]).ToString(Formatting.None));
                }
                result.Add(compilation["targets"][t].Value<string>("target"), problems);
            }
            return result;
        }

        internal static bool ProblemsUnchanged(JObject before, JObject after)
        {
            var b = ComparisonTree(before); var a = ComparisonTree(after);
            return b != null && a != null
                && b.Value<int>("errorCount") == a.Value<int>("errorCount")
                && b.Value<int>("warningCount") == a.Value<int>("warningCount")
                && HmiTextListImportRules.CompileProblemsUnchanged(b, a);
        }

        private static JObject ComparisonTree(JObject original)
        {
            if (original == null || !Count(original, "errorCount") || !Count(original, "warningCount")) return null;
            var result = (JObject)original.DeepClone();
            if (!(result["targets"] is JArray targets) || targets.Count == 0 || targets.Any(t => !(t is JObject))) return null;
            if (targets.Any(t => string.IsNullOrWhiteSpace(t.Value<string>("target"))) || targets.Select(t => t.Value<string>("target")).Distinct(StringComparer.Ordinal).Count() != targets.Count) return null;
            foreach (JObject target in targets)
            {
                if (!Count(target, "errorCount") || !Count(target, "warningCount") || !(target["messages"] is JArray messages)
                    || messages.Any(m => !(m is JObject))) return null;
                var ancestors = new List<string>();
                for (int i = 0; i < messages.Count; i++)
                {
                    var row = (JObject)messages[i];
                    if (row["state"]?.Type != JTokenType.String || !new[] { "Success", "Information", "Warning", "Error" }.Contains(row.Value<string>("state"))
                        || row["children"] != null || row["description"]?.Type != JTokenType.String
                        || (row["path"] != null && row["path"].Type != JTokenType.Null && row["path"].Type != JTokenType.String)) return null;
                    int? depth = row.Value<int?>("depth");
                    if (depth == null || depth < 0 || depth > 64 || depth > ancestors.Count) return null;
                    if (ancestors.Count > depth) ancestors.RemoveRange(depth.Value, ancestors.Count - depth.Value);
                    string path = row.Value<string>("path");
                    // Exact observed container shape: root owner -> timestamp/size -> compile-start Information.
                    // The same string as a leaf warning, or elsewhere in the tree, remains a real diagnostic.
                    if (depth == 1 && i > 0 && i + 1 < messages.Count
                        && messages[i - 1].Value<int?>("depth") == 0
                        && string.IsNullOrEmpty(messages[i - 1].Value<string>("description"))
                        && path != null && ancestors.Count == 1 && path == ancestors[0]
                        && messages[i + 1].Value<int?>("depth") == 2
                        && messages[i + 1].Value<string>("state") == "Information"
                        && messages[i + 1].Value<string>("description") == "Software compilation started."
                        && IsTimestampHeader(row.Value<string>("description")))
                        row["state"] = "Information";
                    // Re-importing a screen renames its generated event trigger after the screen's current name
                    // (observed 9 November 2026: "trigger 3074_Setup_Infeed_..." became "trigger 3024_Setup_..."
                    // after an in-place rollback). The owning object path, event, state and text stay significant.
                    if (path == null && ancestors.Count > 0 && !string.IsNullOrEmpty(ancestors[ancestors.Count - 1]))
                    {
                        var trigger = TriggerLabel.Match(row.Value<string>("description"));
                        if (trigger.Success) row["description"] = trigger.Groups["pre"].Value + "<trigger>" + trigger.Groups["post"].Value;
                    }
                    // Native leaf paths are often null; retain their complete owner hierarchy.
                    ancestors.Add(path);
                    row["path"] = new JArray(ancestors).ToString(Formatting.None);
                }
                // Target-level counters are part of the compared summary, not just the aggregate.
                target["target"] = new JArray(target.Value<string>("target"), target.Value<int>("errorCount"), target.Value<int>("warningCount")).ToString(Formatting.None);
            }
            return result;
        }

        private static readonly Regex TriggerLabel = new Regex(@"\A(?<pre>.*\bfor trigger )(?<name>[^,\r\n]+)(?<post>, event '[^']+'\..*)\z", RegexOptions.Singleline);

        private static bool Count(JObject value, string key) => value[key]?.Type == JTokenType.Integer && value.Value<long>(key) >= 0 && value.Value<long>(key) <= int.MaxValue;
        private static bool IsTimestampHeader(string text)
        {
            var match = Regex.Match(text ?? "", @"\ATime stamp: (?<time>[0-9]{2}\.[0-9]{2}\.[0-9]{4} [0-9]{2}:[0-9]{2}) - [0-9]+ bytes used\z");
            return match.Success && DateTime.TryParseExact(match.Groups["time"].Value, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }
    }
}
