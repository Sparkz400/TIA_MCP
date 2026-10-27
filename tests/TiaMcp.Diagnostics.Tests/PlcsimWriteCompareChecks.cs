using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

/// <summary>Plan-signal write admission and receipts, and before/after comparison of two retained PLCSIM acquisitions.</summary>
internal static class PlcsimWriteCompareChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("PLCSIM write/compare check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        string Id(JsonNode node) => node["id"]!.GetValue<string>();
        JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
        using var f = new RuntimeAnalysisChecks.CaptureFixture();
        string root = f.Source.Root, caseId = f.Plan.Association!.CaseId, creation = Guid.NewGuid().ToString("N"), name = "TiaMcp_" + Guid.NewGuid().ToString("N");
        var enable = new DiagnosticPlcsimWrite("enable", "\"DB_Line\".Conv2.Enable", "bool", "Bool", J("true"), J("false"), "Stimulate the enable path.");
        var step = new DiagnosticPlcsimWrite("step", "\"DB_Line\".Conv2.Step", "uint16", "UInt", J("1"), J("65535"), "Set the step the pattern predicts.");
        (JsonObject, RuntimeCaptureAssociation) Admit(params DiagnosticPlcsimWrite[] writes) => DiagnosticPlcsimWrites.Admit(root, caseId, 4, name, creation, writes);

        // Admission reuses the PLCSIM capture contract and re-resolves every signal against the saved case.
        var (arguments, association) = Admit(enable, step);
        Check(arguments["creationId"]!.GetValue<string>() == creation && arguments["writes"]!.AsArray().Count == 2 &&
            arguments["writes"]![1]!["expected"]!.GetValue<int>() == 1 && arguments["writes"]![1]!["value"]!.GetValue<int>() == 65535);
        Check(association.CaseId == caseId && association.Signals.Select(s => s.SignalId).SequenceEqual(["enable", "step"]) && !association.ProgramMatchVerified);
        Reject("RuntimeWriteLimit", () => Admit());
        Reject("RuntimeWriteLimit", () => Admit(Enumerable.Repeat(step, 17).ToArray()));
        Reject("RuntimeCaptureAssociationMismatch", () => Admit(enable with { SignalId = "unknown" }));
        Reject("RuntimeCaptureAssociationMismatch", () => Admit(step with { Type = "int16", Datatype = "Int" }));
        Reject("PlcsimCaptureInvalid", () => Admit(enable with { Datatype = "UInt" }));
        Reject("PlcsimCaptureInvalid", () => Admit(enable, step with { Symbol = enable.Symbol }));
        Reject("InvalidRequest", () => Admit(enable with { Value = J("1") }));
        Reject("InvalidRequest", () => Admit(step with { Value = J("70000") }));
        Reject("InvalidRequest", () => Admit(step with { Expected = default }));
        Reject("RuntimeCaptureIdentityInvalid", () => DiagnosticPlcsimWrites.Admit(root, caseId, 4, name, "not-a-creation-id", [enable]));

        // Receipts keep the worker's guarded outcome for the exact instance and never claim more.
        JsonElement Response(string state, int id = 4) => J($$"""{"instanceId":{{id}},"instanceName":"{{name}}","state":"{{state}}","dispatched":2,"items":[]}""");
        var receipt = DiagnosticPlcsimWrites.Receipt(arguments, association, Response("written"));
        Check((string?)receipt["schemaVersion"] == "diagnostic-plcsim-write.v1" && (string?)receipt["caseId"] == caseId &&
            receipt["association"]!["signals"]!.AsArray().Count == 2 && !receipt["physicalPlc"]!.GetValue<bool>() && !receipt["forced"]!.GetValue<bool>());
        Check((string?)DiagnosticPlcsimWrites.Receipt(arguments, association, Response("preconditionMismatch"))["state"] == "preconditionMismatch");
        Reject("PlcsimResponseInvalid", () => DiagnosticPlcsimWrites.Receipt(arguments, association, Response("written", 5)));
        Reject("PlcsimResponseInvalid", () => DiagnosticPlcsimWrites.Receipt(arguments, association, Response("unknown")));

        // Two monitor captures of one experiment, before and after a stimulus, compared against the case predictions.
        var source = new SimulatorCaptureSource(4, name, [new(enable.Symbol, "bool", "Bool"), new(step.Symbol, "uint16", "UInt")]);
        var service = new DiagnosticRuntimeAcquisitions(root);
        string experiment = Id(new DiagnosticExperiments(root).Plan(caseId, ExperimentChecks.Request(f.Source)));
        string Acquire(string experimentId, params (bool Enable, int Step)[] cycles)
        {
            string[] ids = [Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")];
            var request = new RuntimeCaptureRequest(creation, ids, 100, 10, 100, 1 << 20, f.Source.ReviewId, "simple",
                new RuntimeCaptureAssociationRequest(caseId, [new(ids[0], "enable", "Explicit PLCSIM symbol"), new(ids[1], "step", "Explicit PLCSIM symbol")]),
                Source: "plcsim", Simulator: source);
            var signals = SimulatorCaptureContracts.Signals(request);
            var writer = f.Store.Create(new RuntimeCapturePlan(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), f.Start, request, signals,
                RuntimeCaptureAssociations.Resolve(root, request, signals)));
            for (int cycle = 0; cycle < cycles.Length; cycle++)
            {
                var values = new JsonArray(source.Signals.Zip(new JsonNode[] { cycles[cycle].Enable, cycles[cycle].Step }).Select(p => (JsonNode?)new JsonObject {
                    ["symbol"] = p.First.Symbol, ["type"] = p.First.Type, ["observedAtUtc"] = "2026-10-18T01:00:00.0000000Z",
                    ["completedAtUtc"] = "2026-10-18T01:00:00.0010000Z", ["quality"] = "good", ["value"] = p.Second }).ToArray());
                var reads = SimulatorCaptureContracts.Reads(request, JsonSerializer.SerializeToElement(new JsonObject {
                    ["instanceId"] = 4, ["instanceName"] = name, ["syncPoint"] = null, ["values"] = values }));
                for (int i = 0; i < 2; i++) writer.Append(new(writer.Rows, "read", ids[i], cycle, cycle * 100, cycle * 100 + i + 1, f.Start, reads[i].Result, reads[i].Error, 0));
            }
            writer.Finish("stopped", cycles.Length * 100);
            return Id(service.Acquire(f.Store, experimentId, writer.Reference, new JsonObject { ["reason"] = "Retain one side of a before/after comparison.",
                ["units"] = new JsonObject { ["enable"] = "1", ["step"] = "count" } }));
        }
        string before = Acquire(experiment, (true, 65535), (true, 65535)), after = Acquire(experiment, (true, 1), (true, 2));
        var reason = new JsonObject { ["reason"] = "Compare the predicted pattern before and after the PLCSIM stimulus." };
        var compared = service.Compare(f.Store, before, after, reason);
        var comparison = compared["comparison"]!;
        JsonNode Hypothesis(string id) => comparison["hypotheses"]!.AsArray().Single(h => (string?)h!["id"] == id)!;
        Check((string?)Hypothesis("pattern")["before"]!["pattern"] == "matchedOnly" && Hypothesis("pattern")["before"]!["matchedCycles"]!.GetValue<int>() == 2 &&
            (string?)Hypothesis("pattern")["after"]!["pattern"] == "contradictedOnly" && Hypothesis("pattern")["patternChanged"]!.GetValue<bool>() &&
            (string?)Hypothesis("pattern")["verdict"] == "unresolved");
        Check((string?)Hypothesis("numeric")["before"]!["pattern"] == "matchedOnly" && (string?)Hypothesis("numeric")["after"]!["pattern"] == "notApplicable");
        Check(comparison["sameCaptureContext"]!.GetValue<bool>() && comparison["sameSignals"]!.GetValue<bool>() && (string?)comparison["verdict"] == "unresolved" &&
            !comparison["causationProven"]!.GetValue<bool>() && !comparison["bugFixProven"]!.GetValue<bool>() && (string?)comparison["before"]!["acquisitionId"] == before);
        Check(Id(service.Compare(f.Store, before, after, reason)) == Id(compared) &&
            new DiagnosticStore(root).Read(Id(compared), "diagnostic-runtime-acquisition-comparison.v1")["hypotheses"] is JsonArray);
        Reject("MeasurementLineageMismatch", () => service.Compare(f.Store, before, before, reason));
        var other = ExperimentChecks.Request(f.Source); other["reason"] = "A separate experiment on the same case.";
        string otherAcquisition = Acquire(Id(new DiagnosticExperiments(root).Plan(caseId, other)), (true, 1));
        Reject("MeasurementLineageMismatch", () => service.Compare(f.Store, before, otherAcquisition, reason));
        Reject("InvalidRequest", () => service.Compare(f.Store, before, after, new JsonObject { ["reason"] = "x", ["windowStartSeconds"] = 0 }));
    }
}
