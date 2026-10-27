using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

/// <summary>Rows mapped from a fake simulator worker sample feed the unchanged runtime acquisition and export path.</summary>
internal static class PlcsimAcquisitionChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("PLCSIM acquisition check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        string Id(JsonNode node) => node["id"]!.GetValue<string>();
        using var f = new RuntimeAnalysisChecks.CaptureFixture();
        string caseId = f.Plan.Association!.CaseId; string[] ids = [Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")];
        var source = new SimulatorCaptureSource(4, "TiaMcp_" + Guid.NewGuid().ToString("N"),
            [new("\"DB_Line\".Conv2.Enable", "bool", "Bool"), new("\"DB_Line\".Conv2.Step", "uint16", "UInt")], true);
        var request = new RuntimeCaptureRequest(Guid.NewGuid().ToString("N"), ids, 100, 10, 100, 1 << 20, f.Source.ReviewId, "simple",
            new RuntimeCaptureAssociationRequest(caseId, [new(ids[0], "enable", "Explicit PLCSIM symbol"), new(ids[1], "step", "Explicit PLCSIM symbol")]),
            Source: "plcsim", Simulator: source);
        var signals = SimulatorCaptureContracts.Signals(request);
        var plan = new RuntimeCapturePlan(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), f.Start, request, signals,
            RuntimeCaptureAssociations.Resolve(f.Source.Root, request, signals));
        JsonElement Sample(long count, JsonNode? enable, JsonNode? step)
        {
            var values = new JsonArray();
            foreach (var (signal, value) in source.Signals.Zip(new[] { enable, step }))
            {
                var item = new JsonObject { ["symbol"] = signal.Symbol, ["type"] = signal.Type,
                    ["observedAtUtc"] = "2026-10-18T01:00:00.0000000Z", ["completedAtUtc"] = "2026-10-18T01:00:00.0010000Z" };
                if (value is null) { item["quality"] = "bad"; item["errorCode"] = "SignalNameDoesNotExist"; }
                else { item["quality"] = "good"; item["value"] = value; }
                values.Add(item);
            }
            return JsonSerializer.SerializeToElement(new JsonObject { ["instanceId"] = source.InstanceId, ["instanceName"] = source.InstanceName,
                ["syncPoint"] = new JsonObject { ["count"] = count, ["timeSinceSameSyncPointNanoseconds"] = 1000000, ["timeSinceAnySyncPointNanoseconds"] = 1000000 },
                ["values"] = values });
        }
        var writer = f.Store.Create(plan);
        void Cycle(long cycle, JsonNode? enable, JsonNode? step)
        {
            var reads = SimulatorCaptureContracts.Reads(request, Sample(cycle + 1, enable, step));
            for (int i = 0; i < 2; i++) writer.Append(new(writer.Rows, "read", ids[i], cycle, cycle * 100, cycle * 100 + i + 1, f.Start, reads[i].Result, reads[i].Error, 0));
        }
        Cycle(0, true, 65535); Cycle(1, false, 65534); Cycle(2, true, null); Cycle(3, true, 1);
        writer.Finish("stopped", 400);

        string experiment = Id(new DiagnosticExperiments(f.Source.Root).Plan(caseId, ExperimentChecks.Request(f.Source)));
        var acquire = new JsonObject { ["reason"] = "Retain owned PLCSIM sync-point observations without behavioral assertion.",
            ["units"] = new JsonObject { ["enable"] = "1", ["step"] = "count" } };
        var service = new DiagnosticRuntimeAcquisitions(f.Source.Root);
        var receipt = service.Acquire(f.Store, experiment, writer.Reference, acquire);
        var acquisition = receipt["acquisition"]!; var analysis = acquisition["analysis"]!; var rows = acquisition["captureSource"]!["rows"]!.AsArray();
        Check(rows.Count == 8 && rows[0]!["readResultJson"]!.GetValue<string>().Contains("\"syncPoint\":{\"count\":1,"));
        Check(analysis["qualityAssessment"]!["cyclesObserved"]!.GetValue<int>() == 4 && analysis["qualityAssessment"]!["providerFailures"]!.GetValue<int>() == 1);
        Check(analysis["hypotheses"]![1]!["matchedCycles"]!.GetValue<int>() == 1 && analysis["hypotheses"]![1]!["contradictedCycles"]!.GetValue<int>() == 1 &&
            analysis["hypotheses"]![1]!["unknownCycles"]!.GetValue<int>() == 1 && (string?)analysis["hypotheses"]![1]!["verdict"] == "unresolved");
        Check(analysis["capture"]!["context"]!["instanceId"]!.GetValue<int>() == 4 && analysis["capture"]!["context"]!["cycleSynchronous"]!.GetValue<bool>());
        foreach (string flag in new[] { "atomicAcrossSignals", "scanSynchronized", "programMatchVerified", "nativeQualification" }) Check(acquisition[flag]!.GetValue<bool>() == false);
        foreach (string format in new[] { "json", "markdown", "html" })
        {
            var exported = service.Export(f.Store, Id(receipt), format);
            string text = File.ReadAllText(exported["artifact"]!["path"]!.GetValue<string>());
            Check(text.Contains("plcsim-runtime-read.v1") && text.Contains("syncPoint") && text.Contains("unresolved"));
            if (format == "json") Check(Id(exported) == Id(receipt));
        }

        // Retained rows and plans are revalidated against the exact simulator source.
        var tamper = f.Store.Create(plan with { CaptureId = Guid.NewGuid().ToString("N") });
        string step = SimulatorCaptureContracts.Reads(request, Sample(1, true, 65535))[1].Result!;
        Reject("PlcsimCaptureInvalid", () => tamper.Append(new(0, "read", ids[1], 0, 0, 1, f.Start, step.Replace("65535", "70000"), null, 0)));
        Reject("PlcsimCaptureInvalid", () => tamper.Append(new(0, "read", ids[0], 0, 0, 1, f.Start, step, null, 0)));
        Reject("PlcsimCaptureInvalid", () => tamper.Append(new(0, "read", ids[1], 0, 0, 1, f.Start, step.Replace("\"syncPoint\":{", "\"syncPoint\":null,\"x\":{"), null, 0)));
        Reject("PlcsimCaptureInvalid", () => tamper.Append(new(0, "read", ids[1], 0, 0, 1, f.Start, null, "RuntimeTimeout", 0)));
        Check(SimulatorCaptureContracts.Reads(request, Sample(1, true, 70000))[1].Error == "PlcsimResponseInvalid");
        Check(SimulatorCaptureContracts.Reads(request with { Simulator = source with { InstanceId = 5 } }, Sample(1, true, 1)).All(r => r.Error == "PlcsimResponseInvalid"));
        Reject("PlcsimCaptureInvalid", () => f.Store.Create(plan with { CaptureId = Guid.NewGuid().ToString("N"), Signals = [signals[1], signals[0]] }));
        var retyped = request with { Simulator = source with { Signals = [source.Signals[0], new("\"DB_Line\".Conv2.Step", "int16", "Int")] } };
        Reject("RuntimeCaptureAssociationMismatch", () => RuntimeCaptureAssociations.Resolve(f.Source.Root, retyped, SimulatorCaptureContracts.Signals(retyped)));
    }
}
