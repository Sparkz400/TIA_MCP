using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

internal static class SourceRevalidationChecks
{
    internal static void Run()
    {
        using var f = new Fixture(); SuppliedAlarmChecks.AddEvidence(f);
        var original = f.Planner.Plan(SuppliedAlarmChecks.Request(f))["plan"]!.AsObject();
        var watch = new JsonObject { ["tableName"] = "Source checks", ["tiaVersion"] = 21,
            ["externalStorage"] = new JsonArray(new JsonObject { ["objectPath"] = Fixture.Block("DB_IO"), ["reason"] = "Observe external output." }) };
        var replan = new JsonObject { ["reviewId"] = f.ReviewId, ["blockPath"] = original["blockPath"]!.DeepClone(),
            ["instance"] = f.Request()["instance"]!.DeepClone(), ["reason"] = "Explicit unchanged mapping.",
            ["signalMappings"] = new JsonArray(original["signals"]!.AsArray().Select(s => (JsonNode?)new JsonObject {
                ["id"] = s!["id"]!.DeepClone(), ["storagePath"] = s["storagePath"]!.DeepClone(),
                ["memberPath"] = s["memberPath"]!.DeepClone(), ["reason"] = "Unchanged signal." }).ToArray()) };
        var consumers = new (string Name, Action<string> Run)[] {
            ("trace", id => f.Traces.Prepare(id, f.Settings())),
            ("watch", id => new DiagnosticWatchPlanning(f.Root).Plan(id, watch)),
            ("replan", id => new DiagnosticReplanner(f.Root).Replan(id, replan)) };
        var mutations = new (string Name, Action<JsonObject> Apply)[] {
            ("symbol", p => p["signals"]![0]!["symbolComponents"] = new JsonArray("DifferentDB", "DifferentMember")),
            ("type", p => p["signals"]![0]!["datatype"] = "LReal"),
            ("source line", p => p["signals"]![0]!["evidence"]!["xmlLine"] = 999999),
            ("live claim", p => p["signals"]![0]!["evidence"]!["liveVerified"] = true),
            ("address", p => p["signals"]![0]!["onlineAddress"] = "%M0.0"),
            ("member", p => p["signals"]![0]!["memberPath"] = new JsonArray("MissingMember")),
            ("call", p => p["selectedInstance"]!["callSites"]![0]!["callUid"] = "forged"),
            ("binding", p => p["selectedInstance"]!["callSites"]![0]!["parameterBindings"] = new JsonArray(new JsonObject { ["parameter"] = "forged" })),
            ("root", p => p["selectedInstance"]!["rootCallSites"]![0]!["calleePath"] = Fixture.Block("FB_Conveyor")),
            ("source hash", p => p["sourceRevision"]!["documents"]![0]!["utf8Sha256"] = new string('0', 64)),
            ("source identity", p => p["sourceRevision"]!["reviewId"] = new string('0', 64)),
            ("online claim", p => p["sourceRevision"]!["onlineVerified"] = true),
            ("native claim", p => p["nativeExecutable"] = true),
            ("alarm", p => p["alarmEvidence"]!["object"]!["metadata"]!["text"] = "forged") };
        int rejected = 0; var failures = new List<string>();
        foreach (var consumer in consumers)
        {
            consumer.Run(new DiagnosticStore(f.Root).Save(original)["id"]!.GetValue<string>());
            foreach (var mutation in mutations)
            {
                var changed = original.DeepClone().AsObject(); mutation.Apply(changed);
                string id = new DiagnosticStore(f.Root).Save(changed)["id"]!.GetValue<string>();
                try { consumer.Run(id); }
                catch (BridgeException e) when (e.Code == "CaptureMismatch") { rejected++; continue; }
                catch (Exception e) { failures.Add($"{consumer.Name}/{mutation.Name}: {e.Message}"); continue; }
                failures.Add($"{consumer.Name} accepted rehashed {mutation.Name} evidence.");
            }
        }
        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"PASS source revalidation: {rejected} honestly rehashed mutation refusals and 3 unchanged consumers");
    }
}
