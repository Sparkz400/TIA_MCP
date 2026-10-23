using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal static class HmiProjectModelRules
    {
        internal static bool ScreenItemsSupported(string family, string role)
            => family != "classic" || role == null || role == "regular";
        // Repeated native exports can have different export-time hashes. They are
        // evidence on the observation, not new semantic relationship identities.
        internal static string EdgeKey(JObject relation)
        {
            var key = new JObject
            {
                ["sourcePath"] = relation["sourcePath"]?.DeepClone(), ["relation"] = relation["relation"]?.DeepClone(),
                ["sourceProperty"] = relation["sourceProperty"]?.DeepClone(), ["nativeSpelling"] = relation["nativeSpelling"]?.DeepClone(),
                ["targetPath"] = (relation["target"] as JObject)?["objectPath"]?.DeepClone()
            };
            // Library references have no captured target path; their native GUID pair distinguishes observations.
            var library = relation["libraryReference"] as JObject;
            if (relation.Value<string>("relation") == "hmiFaceplateTypeVersion" &&
                (library?["typeGuid"]?.Type == JTokenType.String || library?["versionGuid"]?.Type == JTokenType.String))
            {
                key["libraryTypeGuid"] = library?["typeGuid"]?.DeepClone();
                key["libraryVersionGuid"] = library?["versionGuid"]?.DeepClone();
            }
            return key.ToString(Formatting.None);
        }

        internal static string CoverageState(string state) => state == "truncated" ? "partial" : state == "failed" || state == null ? "unavailable" : state;
        internal static string ProviderState(string state, int retainedRows) => state == "complete" ? "available" : state == "truncated" ? "partial" : state == "unsupported" ? "unsupported"
            : retainedRows > 0 && (state == "unavailable" || state == "failed") ? "partial" : "unavailable";

        internal static JArray Warnings(JToken diagnostics) => new JArray((diagnostics as JArray ?? new JArray()).Take(100)
            .Select(d => d.Type == JTokenType.String ? d.Value<string>() : d.ToString(Formatting.None)).Select(s => s.Length > 2048 ? s.Substring(0, 2048) : s));

        // Export-local items need not exist in the metadata tree. Retain their
        // evidence without inventing endpoints, and debit the normal graph limits.
        internal static void RetainUncaptured(JObject result, JArray candidates, string scope, string[] kinds, string direction, int maxEdges)
        {
            var retained = new JArray(); result["unresolvedObservations"] = retained;
            var selected = new HashSet<string>((result["edges"] as JArray ?? new JArray()).OfType<JObject>().Select(e => e.Value<string>("edgeId")), StringComparer.Ordinal);
            var allowed = kinds == null || kinds.Length == 0 ? null : new HashSet<string>(kinds, StringComparer.Ordinal);
            foreach (JObject edge in candidates.OfType<JObject>())
            {
                if (selected.Contains(edge.Value<string>("edgeId")) || allowed != null && !allowed.Contains(edge.Value<string>("kind"))) continue;
                if (edge["source"]?.Type != JTokenType.Null && edge["target"]?.Type != JTokenType.Null) continue;
                string source = edge["evidence"]?.Value<string>("sourcePath");
                string target = (edge["evidence"]?["native"]?["target"] as JObject)?.Value<string>("objectPath");
                bool fromScope = Within(source, scope), toScope = Within(target, scope);
                if (direction == "in" ? !toScope : direction == "out" ? !fromScope : !fromScope && !toScope) continue;
                if (selected.Count >= maxEdges) { Truncated(result); break; }
                retained.Add(edge.DeepClone());
                if (Size(result) > 512 * 1024 - 1024) { retained.Last.Remove(); Truncated(result); break; }
                selected.Add(edge.Value<string>("edgeId"));
            }
            // Include adapter evidence in the byte counter, not just the graph core.
            int size;
            do { size = Size(result); if (result.Value<int?>("payloadBytes") == size) break; result["payloadBytes"] = size; } while (true);
        }

        private static bool Within(string path, string scope) => path != null && scope != null && (path == scope || path.StartsWith(scope + "/", StringComparison.Ordinal));
        private static int Size(JObject result) => Encoding.UTF8.GetByteCount(result.ToString(Formatting.None));
        private static void Truncated(JObject result) { result["truncated"] = true; result["continuationAvailable"] = true; result["narrowScopeSuggestion"] = "Select a specific HMI screen or member, or narrow relationship kinds."; }
    }
}
