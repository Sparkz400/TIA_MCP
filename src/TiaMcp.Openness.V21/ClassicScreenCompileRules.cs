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
            foreach (JObject target in targets)
            {
                if (!Count(target, "errorCount") || !Count(target, "warningCount") || !(target["messages"] is JArray messages)
                    || messages.Any(m => !(m is JObject))) return null;
                var ancestors = new List<string>();
                for (int i = 0; i < messages.Count; i++)
                {
                    var row = (JObject)messages[i];
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
                    // Native leaf paths are often null; retain their complete owner hierarchy.
                    ancestors.Add(path);
                    row["path"] = new JArray(ancestors).ToString(Formatting.None);
                }
                // Target-level counters are part of the compared summary, not just the aggregate.
                target["target"] = new JArray(target.Value<string>("target"), target.Value<int>("errorCount"), target.Value<int>("warningCount")).ToString(Formatting.None);
            }
            return result;
        }

        private static bool Count(JObject value, string key) => value[key]?.Type == JTokenType.Integer && value.Value<long>(key) >= 0 && value.Value<long>(key) <= int.MaxValue;
        private static bool IsTimestampHeader(string text)
        {
            var match = Regex.Match(text ?? "", @"\ATime stamp: (?<time>[0-9]{2}\.[0-9]{2}\.[0-9]{4} [0-9]{2}:[0-9]{2}) - [0-9]+ bytes used\z");
            return match.Success && DateTime.TryParseExact(match.Groups["time"].Value, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }
    }
}
