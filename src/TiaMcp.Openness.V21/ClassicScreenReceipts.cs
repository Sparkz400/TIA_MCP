using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    internal static class ClassicScreenReceipts
    {
        internal static JObject Compact(JObject full)
        {
            var result = (JObject)full.DeepClone();
            CompactNode(result);
            result["responseDetail"] = "summary";
            result["fullEvidence"] = "The immutable preview and native exports are retained in journalPath and readback artifacts. change_status(includeDetails=true) returns full details.";
            return result;
        }
        private static void CompactNode(JToken node)
        {
            if (node is JObject obj)
            {
                // Summarize full scope manifests, preserving scope/hash/completeness/diagnostics.
                if (obj["contentHash"] != null && obj["artifacts"] is JObject artifacts)
                { obj["artifactCount"] = artifacts.Count; obj.Remove("artifacts"); }
                foreach (var property in obj.Properties().ToArray())
                {
                    if (property.Name == "xml" && property.Value.Type == JTokenType.String)
                    { obj["xmlCharacters"] = ((string)property.Value).Length; property.Remove(); }
                    else CompactNode(property.Value);
                }
            }
            else if (node is JArray array) foreach (var child in array) CompactNode(child);
        }
    }
}
