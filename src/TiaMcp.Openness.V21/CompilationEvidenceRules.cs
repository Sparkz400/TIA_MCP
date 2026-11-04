using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure admission check over retained native evidence. Never repair a native
    // summary: a zero count can coexist with Error messages after HMI retrieval.
    internal static class CompilationEvidenceRules
    {
        // Compile-only operations report complete native diagnostics, including errors,
        // without treating ordinary compiler findings as an unknown mutation outcome.
        internal static bool IsCompleted(JObject compilation)
        {
            if (compilation?["error"] != null || !(compilation?["targets"] is JArray targets) || targets.Count == 0) return false;
            if (targets.OfType<JObject>().Select(t => t.Value<string>("target")).Distinct(StringComparer.Ordinal).Count() != targets.Count) return false;
            long errors = 0, warnings = 0;
            foreach (var token in targets)
            {
                if (!(token is JObject target) || target["error"] != null || string.IsNullOrWhiteSpace(target.Value<string>("target"))
                    || target["errorCount"]?.Type != JTokenType.Integer || target.Value<long>("errorCount") < 0
                    || target["warningCount"]?.Type != JTokenType.Integer || target.Value<long>("warningCount") < 0
                    || target["messagesTruncated"]?.Type != JTokenType.Boolean || target.Value<bool>("messagesTruncated")
                    || !(target["messages"] is JArray messages) || !new[] { "Success", "Information", "Warning", "Error" }.Contains(target.Value<string>("state"))) return false;
                int depth = 0;
                foreach (var row in messages)
                {
                    if (!(row is JObject) || !new[] { "Success", "Information", "Warning", "Error" }.Contains(row.Value<string>("state"))
                        || row["depth"]?.Type != JTokenType.Integer || row.Value<int>("depth") < 0 || row.Value<int>("depth") > depth || row.Value<int>("depth") > 64
                        || row["children"] != null || (row.Value<string>("description") ?? "").StartsWith("Cannot compile.", StringComparison.Ordinal)) return false;
                    depth = row.Value<int>("depth") + 1;
                }
                errors += target.Value<long>("errorCount"); warnings += target.Value<long>("warningCount");
            }
            return compilation["errorCount"]?.Type == JTokenType.Integer && compilation["warningCount"]?.Type == JTokenType.Integer
                && errors == compilation.Value<long>("errorCount") && warnings == compilation.Value<long>("warningCount");
        }

        internal static JObject SingleTarget(JObject diagnostics, string target)
        {
            if (diagnostics == null || string.IsNullOrWhiteSpace(target) || diagnostics["targets"] != null)
                throw new ArgumentException("One compiler result and an exact target are required.");
            // Preserve the existing flat scratch fields while adding the shared target envelope.
            return new JObject(diagnostics) { ["targets"] = new JArray(new JObject(diagnostics) { ["target"] = target }) };
        }

        internal static bool IsSuccessful(JObject compilation)
        {
            if (compilation == null || !IsZero(compilation["errorCount"]) || compilation["error"] != null)
                return false;
            var targets = compilation["targets"] as JArray;
            return targets != null && targets.Count != 0 && targets.All(token => token is JObject target &&
                target["target"]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(target.Value<string>("target")) &&
                IsSuccessfulTarget(target));
        }

        internal static bool IsSuccessfulTarget(JObject result, string errorCountField = "errorCount", string truncatedField = "messagesTruncated") =>
            result != null && result["error"] == null && IsZero(result[errorCountField]) &&
            result["state"]?.Type == JTokenType.String &&
            (result.Value<string>("state") == "Success" || result.Value<string>("state") == "Information" || result.Value<string>("state") == "Warning") &&
            result[truncatedField]?.Type == JTokenType.Boolean && result.Value<bool>(truncatedField) == false &&
            MessagesSuccessful(result["messages"], 0);

        private static bool IsZero(JToken value) => value?.Type == JTokenType.Integer && value.ToString() == "0";

        private static bool MessagesSuccessful(JToken value, int depth)
        {
            if (depth > 64 || !(value is JArray messages)) return false;
            foreach (var token in messages)
            {
                if (!(token is JObject message) || message["state"]?.Type != JTokenType.String) return false;
                string state = message.Value<string>("state");
                if (state != "Success" && state != "Information" && state != "Warning") return false;
                // Production messages are flattened with depth. Also inspect any
                // nested diagnostic tree supplied by independently captured data.
                if (message.Property("children") != null && !MessagesSuccessful(message["children"], depth + 1)) return false;
            }
            return true;
        }
    }
}
