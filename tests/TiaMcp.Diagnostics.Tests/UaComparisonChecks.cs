using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

internal static class UaComparisonChecks
{
    internal static JsonObject? Samples;
    internal static JsonObject Request() => JsonNode.Parse("""
        {"reason":"Compare retained runs </pre><script>throw 1</script> ``` # text","beforeAnchorSeconds":0,"afterAnchorSeconds":0,
         "windowStartSeconds":0,"windowEndSeconds":0.15,"signalMap":[{"before":"enable","after":"enable"},{"before":"step","after":"step"}]}
        """)!.AsObject();
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new Exception("UA comparison check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        string Id(JsonNode receipt) => receipt["id"]!.GetValue<string>();
        using var f = new Fixture();
        var store = new DiagnosticStore(f.Root);
        string experiment = Id(new DiagnosticExperiments(f.Root).Plan(Id(f.Plan()), ExperimentChecks.Request(f)));
        var bindingRequest = UaBindingChecks.Request();
        string binding = Id(new DiagnosticUaBindings(f.Root).Plan(experiment, bindingRequest));
        var nodes = bindingRequest["mappings"]!.AsArray().Select(m => m!["node"]!.Deserialize<OpcUaRuntimeNode>(BridgeProtocol.Json)!).ToArray();
        using var captures = new RuntimeCaptureStore(Path.Combine(f.Root, "runtime-ua-acquisition"));
        var observe = new DiagnosticUaObservations(f.Root, r => DiagnosticUaCaptureSource.ReadPolling(f.Root, captures, r));
        var comparisons = new DiagnosticUaComparisons(f.Root, observe);
        var time = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        string Capture(char id, string bind, bool after, bool empty = false)
        {
            var request = new OpcUaCaptureRequest(new('a',32), nodes, 100, 10, DiagnosticBindingId: bind);
            var session = new OpcUaRuntimeStatus("opcua-runtime-session.v1", request.AttemptId, new('b',32),
                new("opc.tcp://localhost:4840", "urn:fixture", new('a',64)), "connected", null, true, time);
            var writer = captures.Create(new(new(id,32), session.HostId, time, OpcUaCaptureContracts.StorageRequest(request), [], UaContext: new(request, session)));
            if (empty) return writer.Reference;
            for (int cycle = 0; cycle < 3; cycle++)
                for (int i = 0; i < 2; i++)
                {
                    bool uncertain = cycle == 1 && i == 0;
                    bool floating = id == '7' && i == 1;
                    string? read = JsonSerializer.Serialize(new OpcUaRuntimeReadResult(request.AttemptId, time, time.AddMilliseconds(1),
                        [new(nodes[i], "value", i == 0 ? "Boolean" : floating ? "Double" : "UInt64", new("http://opcfoundation.org/UA/", "numeric", i == 0 ? "1" : floating ? "11" : "9"), -1, [],
                            uncertain ? "0x40000000" : "0x00000000", uncertain ? "Uncertain" : "Good", cycle == 0 ? time : null, null, 12, 34,
                            i == 0 ? "boolean" : floating ? "roundTripDouble" : cycle == 2 ? "null" : "decimalInteger",
                            i == 0 ? !after : floating ? cycle == 0 ? "1E+100" : cycle == 1 ? "1E-100" : "1.25" : cycle == 2 ? null : cycle == 0 ? "18446744073709551615" : after ? "0" : "9007199254740993", null)]), BridgeProtocol.Json);
                    string? error = cycle == 2 && i == 0 ? "UaTimeout" : null;
                    writer.Append(new(writer.Rows, "read", OpcUaCaptureContracts.SelectionId(nodes[i]), cycle, cycle * 100, cycle * 100 + i + 1 + (id == '9' && i == 1 ? 0.0001 : 0), time, error is null ? read : null, error, 0));
                }
            writer.Append(new(writer.Rows, "gap", null, 3, 300, 500, time, null, null, 2)); writer.Finish("stopped", 501);
            return writer.Reference;
        }
        string before = Capture('c', binding, false), after = Capture('d', binding, true), empty = Capture('e', binding, false, true);
        var request = Request(); var receipt = comparisons.Compare(before, after, request); var doc = store.Read(Id(receipt));
        Check(doc["signals"]![0]!["before"]!["trueValues"]!.GetValue<int>() == 1 && doc["signals"]![0]!["after"]!["falseValues"]!.GetValue<int>() == 1);
        Check(doc["signals"]![0]!["before"]!["uncertain"]!.GetValue<int>() == 1);
        Check(doc["signals"]![1]!["before"]!["numericRanges"]![0]!["minimum"]!.GetValue<string>() == "9007199254740993");
        Check(doc["signals"]![1]!["before"]!["numericRanges"]![0]!["maximum"]!.GetValue<string>() == "18446744073709551615");
        Check(doc["signals"]![1]!["after"]!["numericRanges"]![0]!["minimum"]!.GetValue<string>() == "0");
        Check(doc["beforeWindow"]!["fullPrefixEvidence"]!["gapRows"]!.GetValue<int>() == 1 && doc["beforeWindow"]!["selectedRowSequences"]!.AsArray().Count == 4);
        Check(!doc["beforeEvents"]!["monitoringRequested"]!.GetValue<bool>() && !doc["identicalPrefix"]!.GetValue<bool>() && !doc["sameCapture"]!.GetValue<bool>());
        Check(doc["verdict"]!.GetValue<string>() == "unresolved" && !doc["clockSynchronizationVerified"]!.GetValue<bool>());
        var alignedRequest = request.DeepClone().AsObject(); alignedRequest["beforeAnchorSeconds"] = 0.001m; alignedRequest["afterAnchorSeconds"] = 0.101m;
        alignedRequest["windowStartSeconds"] = 0; alignedRequest["windowEndSeconds"] = 0.001m;
        var aligned = store.Read(Id(comparisons.Compare(before, after, alignedRequest)));
        Check(JsonNode.DeepEquals(aligned["beforeWindow"]!["selectedRowSequences"],new JsonArray(0,1)) && JsonNode.DeepEquals(aligned["afterWindow"]!["selectedRowSequences"],new JsonArray(2,3)));
        string fractionalCapture = Capture('9',binding,false);
        var fractional = store.Read(Id(comparisons.Compare(fractionalCapture, after, alignedRequest)));
        Check(JsonNode.DeepEquals(fractional["beforeWindow"]!["selectedRowSequences"],new JsonArray(0)) && fractional["signals"]![1]!["before"]!["observations"]!.GetValue<int>()==0);
        var wide = request.DeepClone().AsObject(); wide["windowEndSeconds"] = 1;
        var wider = store.Read(Id(comparisons.Compare(before, after, wide)));
        Check(wider["signals"]![0]!["before"]!["hostReadErrors"]!.GetValue<int>() == 1 && wider["signals"]![1]!["after"]!["nullValues"]!.GetValue<int>() == 1);
        string floatingCapture = Capture('7', binding, false);
        var floatingDoc = store.Read(Id(comparisons.Compare(floatingCapture, after, wide)));
        Check(floatingDoc["signals"]![1]!["before"]!["numericNotRepresentable"]!.GetValue<int>() == 2 && floatingDoc["signals"]![1]!["before"]!["numericRanges"]![0]!["count"]!.GetValue<int>() == 1);
        Check(floatingDoc["signals"]![1]!["before"]!["numericRanges"]![0]!["minimum"]!.GetValue<string>() == "1.25");
        Check(Id(comparisons.Compare(before, after, request)) == Id(receipt));
        var same = store.Read(Id(comparisons.Compare(before, before, request)));
        Check(same["sameCapture"]!.GetValue<bool>() && same["identicalPrefix"]!.GetValue<bool>());
        var blank = store.Read(Id(comparisons.Compare(empty, after, request)));
        Check(blank["beforeWindow"]!["uncommittedDataUnknown"]!.GetValue<bool>() && !blank["beforeWindow"]!["recordedRowsSpanRequestedWindow"]!.GetValue<bool>() && blank["signals"]![0]!["before"]!["observations"]!.GetValue<int>() == 0);
        foreach (Action<JsonObject> change in new Action<JsonObject>[] {
            r => r["extra"] = true, r => r["signalMap"]![0]!["unknown"] = true })
        { var bad = request.DeepClone().AsObject(); change(bad); Reject("MeasurementFormatUnsupported", () => comparisons.Compare(before, after, bad)); }
        foreach (Action<JsonObject> change in new Action<JsonObject>[] {
            r => r["windowEndSeconds"] = 0, r => r["beforeAnchorSeconds"] = 0.0001m, r => r["afterAnchorSeconds"] = 1000001,
            r => r["allowChangedSource"] = "true", r => r["signalMap"] = new JsonArray() })
        { var bad = request.DeepClone().AsObject(); change(bad); Reject("InvalidRequest", () => comparisons.Compare(before, after, bad)); }
        foreach (Action<JsonObject> change in new Action<JsonObject>[] {
            r => r["signalMap"]![1]!["before"] = "enable", r => r["signalMap"]![0]!["after"] = "missing", r => r["signalMap"]![0]!["after"] = "step" })
        { var bad = request.DeepClone().AsObject(); change(bad); Reject("MeasurementLineageMismatch", () => comparisons.Compare(before, after, bad)); }
        foreach (string format in new[] { "json", "html", "markdown" })
        {
            var export = comparisons.Export(Id(receipt), format); string path = export["artifact"]!["path"]!.GetValue<string>();
            Check(format != "json" || File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(receipt["artifact"]!["path"]!.GetValue<string>())));
            Check(File.ReadAllText(path).Contains("18446744073709551615", StringComparison.Ordinal));
            Check(format != "html" || !File.ReadAllText(path).Contains("<script>", StringComparison.Ordinal));
        }
        foreach (Action<JsonObject> change in new Action<JsonObject>[] {
            d => d["signals"]![0]!["before"]!["trueValues"] = 99, d => d["eventGroups"] = new JsonArray(new JsonObject()),
            d => d["beforeWindow"]!["fullPrefixEvidence"]!["gapRows"] = 0, d => d["bugFixProven"] = true,
            d => d["before"]!["captureSource"]!["rows"]![0]!["elapsedMilliseconds"] = 100 })
        { var bad = doc.DeepClone().AsObject(); change(bad); Reject("UaDiagnosticReportMismatch", () => comparisons.Export(Id(store.Save(bad)), "json")); }
        string bindingPath = Path.Combine(f.Root, "trace-diagnostics", binding + ".json"); var raw = File.ReadAllBytes(bindingPath);
        File.AppendAllText(bindingPath, " "); Reject("ArtifactChanged", () => comparisons.Export(Id(receipt), "json")); File.WriteAllBytes(bindingPath, raw);
        string originalPlan = Path.Combine(f.Root, "runtime-ua-acquisition", "runtime-captures", new string('c',32), "plan.json");
        var planBytes = File.ReadAllBytes(originalPlan); File.Delete(originalPlan);
        try { comparisons.Export(Id(receipt), "json"); throw new Exception("Missing original plan must refuse."); }
        catch (BridgeException) { checks++; } finally { File.WriteAllBytes(originalPlan, planBytes); }
        var largeRequest = new OpcUaCaptureRequest(new('a',32), nodes, 100, 60, DiagnosticBindingId: binding);
        var largeSession = new OpcUaRuntimeStatus("opcua-runtime-session.v1", largeRequest.AttemptId, new('b',32), new("opc.tcp://localhost:4840", "urn:fixture", new('a',64)), "connected", null, true, time);
        var large = captures.Create(new(new('8',32), largeSession.HostId, time, OpcUaCaptureContracts.StorageRequest(largeRequest), [], UaContext: new(largeRequest, largeSession)));
        string largeRead = JsonSerializer.Serialize(new OpcUaRuntimeReadResult(largeRequest.AttemptId, time, time,
            [new(nodes[0], "value", "String", new("http://opcfoundation.org/UA/", "numeric", "12"), -1, [], "0x00000000", "Good", time, time, 0, 0, "string", new string('Z',8000), null)]), BridgeProtocol.Json);
        for (int i = 0; i < 240; i++) large.Append(new(large.Rows,"read",OpcUaCaptureContracts.SelectionId(nodes[0]),i,i*100,i*100+1,time,largeRead,null,0));
        large.Finish("stopped",24001);
        _ = observe.Observe(large.Reference);
        int retainedBefore = Directory.GetFiles(Path.Combine(f.Root,"trace-diagnostics")).Length;
        Reject("ArtifactCapacity", () => comparisons.Compare(large.Reference, large.Reference, request));
        Check(Directory.GetFiles(Path.Combine(f.Root,"trace-diagnostics")).Length == retainedBefore);
        f.ReplaceXml("FB_Conveyor", xml => xml + "\n<!-- changed saved review -->");
        string nextExperiment = Id(new DiagnosticExperiments(f.Root).Plan(Id(f.Plan()), ExperimentChecks.Request(f)));
        string nextBinding = Id(new DiagnosticUaBindings(f.Root).Plan(nextExperiment, bindingRequest));
        string changedSource = Capture('f', nextBinding, true);
        Reject("ProgramRevisionMismatch", () => comparisons.Compare(before, changedSource, request));
        var admitted = request.DeepClone().AsObject(); admitted["allowChangedSource"] = true;
        Check(store.Read(Id(comparisons.Compare(before, changedSource, admitted)))["sourceChanged"]!.GetValue<bool>());
        Samples = new JsonObject { ["authority"] = "syntheticManagedProducer", ["observed"] = doc, ["empty"] = blank, ["numericLimits"] = floatingDoc, ["aligned"] = aligned, ["fractional"] = fractional };
        Console.WriteLine($"PASS UA comparison: {checks} exact window, mapping, quality, evidence and refusal checks.");
    }
}
