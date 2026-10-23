using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.Diagnostics;

internal static class DiagnosticReportContext
{
    internal static JsonObject Complete(JsonObject input)
    {
        var request = (JsonObject)input.DeepClone(); string report = D.Required(request, "report", 8000);
        // Report extraction is explicitly lexical. It never converts a user's observation into proof.
        if (request["searchTerms"] is null)
        {
            var stop = new HashSet<string>("the a an and or but although remains occasionally sometimes while with without true false expected actual stops stop starts start does not is are has have its this that then when although".Split(' '), StringComparer.OrdinalIgnoreCase);
            var terms = Regex.Matches(report, @"[\p{L}_][\p{L}\p{N}_\.]{1,127}").Select(m => m.Value)
                .Where(t => !stop.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            if (terms.Length == 0) throw new BridgeException("InvalidRequest", "The report contains no useful literal search term; supply a known equipment/block/tag name.");
            request["searchTerms"] = D.Strings(terms);
        }
        if (request["hypotheses"] is null)
        {
            request["hypotheses"] = new JsonArray(
                Hypothesis("command", "The effective command changes at the consumer despite the reported upstream command."),
                Hypothesis("internalCondition", "An interlock, state or timer condition changes the internal output command."),
                Hypothesis("otherWriter", "Another call or writer changes final storage after the expected command."),
                Hypothesis("feedback", "Feedback or communication differs from the commanded behavior."));
        }
        request["observations"] ??= new JsonArray(new JsonObject { ["text"] = report, ["origin"] = "userReport", ["verified"] = false });
        request["context"] ??= new JsonObject();
        return request;
    }
    private static JsonObject Hypothesis(string id, string statement) => new() { ["id"] = id, ["statement"] = statement, ["origin"] = "genericDiagnosticAlternative", ["verified"] = false };
}
