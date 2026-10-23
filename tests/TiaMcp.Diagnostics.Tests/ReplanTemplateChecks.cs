using System.Text.Json.Nodes;
using TiaMcp.Core.Diagnostics;

internal static class ReplanTemplateChecks
{
    internal static void Run()
    {
        using var f = new Fixture();
        foreach (string? templateId in new string?[] { "competingWriters", "timer", null })
        {
            var request = f.Request(); request["templateId"] = templateId;
            request["observations"] = new JsonArray("Prior machine observation");
            request["context"] = new JsonObject { ["timing"] = "Prior machine timing" };
            var original = f.Planner.Plan(request); var plan = original["plan"]!.AsObject();
            var mappings = new JsonArray();
            foreach (var signal in plan["signals"]!.AsArray())
            {
                var members = signal!["memberPath"]!.DeepClone().AsArray();
                if (members[0]!.GetValue<string>() == "Conv2") members[0] = "Conv1";
                mappings.Add(new JsonObject { ["id"] = signal["id"]!.DeepClone(), ["storagePath"] = signal["storagePath"]!.DeepClone(),
                    ["memberPath"] = members, ["reason"] = "Explicit new-instance mapping; shared external signals deliberately retained." });
            }
            var replan = new JsonObject { ["reviewId"] = f.ReviewId, ["blockPath"] = request["blockPath"]!.DeepClone(),
                ["instance"] = new JsonObject { ["dbPath"] = request["instance"]!["dbPath"]!.DeepClone(), ["memberPath"] = new JsonArray("Conv1") },
                ["reason"] = "Reuse the same diagnostic question on a separately resolved machine instance.", ["signalMappings"] = mappings };
            var next = new DiagnosticReplanner(f.Root).Replan(original["id"]!.GetValue<string>(), replan)["plan"]!;
            Check(JsonNode.DeepEquals(plan["template"], next["template"]), "Selected role template disappeared during replanning");
            Check(next["selectedInstance"]!["memberPath"]![0]!.GetValue<string>() == "Conv1", "Instance was not resolved anew");
            Check(next["lineage"]!["previousPlanId"]!.GetValue<string>() == original["id"]!.GetValue<string>(), "Prior case lineage lost");
            Check(JsonNode.DeepEquals(plan["hypotheses"], next["hypotheses"]), "Hypotheses lost");
            Check(!next.ToJsonString().Contains("Prior machine"), "Old machine observations or timing were inherited");
            Check(!next["nativeExecutable"]!.GetValue<bool>(), "Replan claimed native execution readiness");
        }
        Console.WriteLine("PASS Replan role templates: 18 checks; exact new instance and separate old evidence.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
