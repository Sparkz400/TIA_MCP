using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure admission check over retained native evidence. Never repair a native
    // summary: a zero count can coexist with Error messages after HMI retrieval.
    internal static class CompilationEvidenceRules
    {
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
