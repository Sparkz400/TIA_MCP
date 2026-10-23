using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ScaleQualification
{
    internal static int Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("Choose a fresh scale output directory.");
        Directory.CreateDirectory(directory);
        var rows = new JArray();
        var report = new JObject { ["completed"] = false, ["nativeExecuted"] = false,
            ["scope"] = "Actual pure planner with generated observed-input fixtures; no native collection, checkpoint or combined apply",
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["cases"] = rows };
        void Save() => File.WriteAllText(Path.Combine(directory, "result.json"), report.ToString(Formatting.Indented));
        Save();
        try
        {
            _ = new Fixture().Build(); // Exclude initial JIT from the measured runs.
            foreach (var scenario in new[] { (Versions: 1, Objects: 10000, Plans: 1),
                                             (Versions: 1000, Objects: 10000, Plans: 1),
                                             (Versions: 10, Objects: 10000, Plans: 100) })
            {
                var fixture = Create(scenario.Versions, scenario.Objects);
                string input = fixture.Versions.ToString(Formatting.None) + fixture.Inventory.ToString(Formatting.None)
                    + fixture.Fb.ToString() + fixture.Caller.ToString();
                string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
                var samples = new JArray(); var plans = new JArray();
                Console.WriteLine($"Planning {scenario.Plans} component(s), {scenario.Versions} versions, {scenario.Objects} observed objects");
                for (int i = 0; i < scenario.Plans; i++)
                {
                    fixture.Payload["integration"]["instanceDb"]["name"] = "Motor_Instance_" + i;
                    long allocated = GC.GetTotalAllocatedBytes(true);
                    var clock = Stopwatch.StartNew();
                    var plan = fixture.Build();
                    clock.Stop();
                    long allocation = GC.GetTotalAllocatedBytes(true) - allocated;
                    if (plan.Value<string>("status") != "planned_only" || plan.Value<bool>("nativeMutationDispatched")
                        || plan.Value<bool>("applySupported") || ((JArray)plan["versions"]).Count != scenario.Versions
                        || ((JArray)plan["steps"]).Count != 2)
                        throw new Exception("Scale plan lost its exact closure or changed execution eligibility.");
                    string serialized = plan.ToString(Formatting.None);
                    samples.Add(new JObject { ["milliseconds"] = clock.Elapsed.TotalMilliseconds,
                        ["allocatedBytes"] = allocation, ["serializedBytes"] = Encoding.UTF8.GetByteCount(serialized) });
                    plans.Add(plan);
                }
                string after = fixture.Versions.ToString(Formatting.None) + fixture.Inventory.ToString(Formatting.None)
                    + fixture.Fb.ToString() + fixture.Caller.ToString();
                if (input != after) throw new Exception("Planning changed original observed inputs.");
                string planFile = $"plans-{scenario.Versions}-{scenario.Plans}.json";
                File.WriteAllText(Path.Combine(directory, planFile), plans.ToString(Formatting.None));
                rows.Add(new JObject { ["versionCount"] = scenario.Versions, ["objectCount"] = scenario.Objects,
                    ["componentPlans"] = scenario.Plans, ["inputSha256"] = hash, ["inputsUnchanged"] = true,
                    ["planFile"] = planFile, ["samples"] = samples,
                    ["processPeakWorkingSetBytes"] = Process.GetCurrentProcess().PeakWorkingSet64 });
                Save();
            }
            var excess = Create(1001, 10000);
            try { excess.Build(); throw new Exception("Over-limit dependency closure was accepted."); }
            catch (TiaMcp.Openness.V21.PlcIntegrationPlanException error) when (error.Code == "InvalidIntegrationRecipe")
            { report["overLimitClosureRefused"] = true; }
            report["completed"] = true;
        }
        catch (Exception error) { report["failure"] = error.ToString(); }
        Save();
        Console.WriteLine(report.Value<bool>("completed") ? "Scale planner qualification passed" : report.Value<string>("failure"));
        return report.Value<bool>("completed") ? 0 : 1;
    }

    private static Fixture Create(int versions, int objects)
    {
        var fixture = new Fixture();
        var inventory = (JArray)fixture.Inventory["objects"];
        for (int i = inventory.Count; i < objects; i++)
            inventory.Add(new JObject { ["name"] = "Existing_" + i, ["path"] = "/plc/blocks/Existing_" + i,
                ["kind"] = "DB", ["number"] = i, ["language"] = "DB", ["protected"] = false, ["namespace"] = "" });
        for (int i = 1; i < versions; i++)
        {
            string type = $"{i:x8}-3333-3333-3333-333333333333", version = $"{i:x8}-4444-4444-4444-444444444444";
            var pin = new JObject { ["typeGuid"] = type, ["versionGuid"] = version };
            ((JArray)fixture.Payload["integration"]["dependencyPins"]).Add(pin.DeepClone());
            ((JArray)fixture.Versions[i - 1]["requiredVersionGuids"]).Add(version);
            pin["typeName"] = "Dependency_" + i; pin["state"] = "Committed"; pin["doNotUse"] = false;
            pin["contentSha256"] = new string('b', 64); pin["requiredVersionGuids"] = new JArray();
            fixture.Versions.Add(pin);
        }
        return fixture;
    }
}
