using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal static class SimulationDownloadEvidenceRules
    {
        internal static bool IsSuccessfulForInstance(JObject report, JObject marker) =>
            report != null && marker != null && report.Property("failure") == null &&
            report["completed"]?.Type == JTokenType.Boolean && report.Value<bool>("completed") &&
            report["instanceId"]?.Type == JTokenType.Integer && marker["instanceId"]?.Type == JTokenType.Integer &&
            JToken.DeepEquals(report["instanceId"], marker["instanceId"]) &&
            report["instanceName"]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(report.Value<string>("instanceName")) &&
            JToken.DeepEquals(report["instanceName"], marker["instanceName"]) &&
            CompilationEvidenceRules.IsSuccessfulTarget(report["compilation"] as JObject, "errors", "truncated") &&
            CompilationEvidenceRules.IsSuccessfulTarget(report["download"] as JObject, "errors", "truncated");
    }
}
