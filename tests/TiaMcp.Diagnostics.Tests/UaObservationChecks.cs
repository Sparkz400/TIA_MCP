using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

internal static class UaObservationChecks
{
    internal static JsonObject? Samples;
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("UA observation check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        string Id(JsonNode receipt) => receipt["id"]!.GetValue<string>();
        using var f = new Fixture();
        string experiment = Id(new DiagnosticExperiments(f.Root).Plan(Id(f.Plan()), ExperimentChecks.Request(f)));
        var bindingRequest = UaBindingChecks.Request();
        bindingRequest["reason"] = "Literal </pre><script>throw 1</script> ``` # text";
        string binding = Id(new DiagnosticUaBindings(f.Root).Plan(experiment, bindingRequest));
        var nodes = bindingRequest["mappings"]!.AsArray().Select(m => m!["node"]!.Deserialize<OpcUaRuntimeNode>(BridgeProtocol.Json)!).ToArray();
        var store = new DiagnosticStore(f.Root);
        using var captures = new RuntimeCaptureStore(Path.Combine(f.Root, "runtime-ua-acquisition"));
        var service = new DiagnosticUaObservations(f.Root, r => DiagnosticUaCaptureSource.ReadPolling(f.Root, captures, r));
        var request = new OpcUaCaptureRequest(new('a',32), nodes, 100, 10, DiagnosticBindingId: binding);
        var time = DateTimeOffset.Parse("2026-10-22T00:00:00Z");
        var session = new OpcUaRuntimeStatus("opcua-runtime-session.v1", request.AttemptId, new('b',32),
            new("opc.tcp://localhost:4840", "urn:fixture", new('a',64)), "connected", null, true, time);
        var plan = new RuntimeCapturePlan(new('c',32), session.HostId, time, OpcUaCaptureContracts.StorageRequest(request), [], UaContext: new(request, session));
        var writer = captures.Create(plan);
        string prefix = writer.Reference;
        string empty = Id(service.Observe(prefix));
        var emptyDoc = store.Read(empty);
        Check(emptyDoc["captureSource"]!["rows"]!.AsArray().Count == 0 && emptyDoc["captureSource"]!["uncommittedDataUnknown"]!.GetValue<bool>());
        void Append(int cycle, int node, string quality, string outcome, string? encoding, object? value, string? error = null)
        {
            string? read = error is null ? JsonSerializer.Serialize(new OpcUaRuntimeReadResult(request.AttemptId, time, time.AddMilliseconds(1),
                [new(nodes[node], outcome, node == 0 ? "Boolean" : "UInt64", new("http://opcfoundation.org/UA/", "numeric", node == 0 ? "1" : "9"), -1, [],
                    quality == "Bad" ? "0x80000000" : quality == "Uncertain" ? "0x40000000" : "0x00000000", quality,
                    cycle == 0 ? time : null, null, 12, 34, encoding, value, outcome == "value" ? null : "UaUnsupportedValue")]), BridgeProtocol.Json) : null;
            writer.Append(new(writer.Rows, "read", OpcUaCaptureContracts.SelectionId(nodes[node]), cycle, cycle * 100, cycle * 100 + node + 1, time, read, error, 0));
        }
        Append(0, 0, "Good", "value", "boolean", true);
        Append(0, 1, "Good", "value", "decimalInteger", "18446744073709551615");
        Append(1, 0, "Uncertain", "value", "boolean", false);
        Append(1, 1, "Bad", "itemError", null, null);
        Append(2, 0, "Good", "value", "null", null);
        Append(2, 1, "Good", "value", null, null, "UaTimeout");
        Append(2, 1, "Good", "value", "null", null);
        writer.Append(new(writer.Rows, "gap", null, 2, 200, 500, time, null, null, 2));
        writer.Finish("stopped", 501);
        var receipt = service.Observe(writer.Reference); var doc = store.Read(Id(receipt));
        var enable = doc["signals"]![0]!; var step = doc["signals"]![1]!;
        Check(enable["observationCount"]!.GetValue<int>() == 3 && enable["goodValues"]!.GetValue<int>() == 1 && enable["uncertain"]!.GetValue<int>() == 1 && enable["nullValues"]!.GetValue<int>() == 1);
        Check(step["goodValues"]!.GetValue<int>() == 1 && step["bad"]!.GetValue<int>() == 1 && step["hostReadErrors"]!.GetValue<int>() == 1);
        Check(step["firstObservation"]!["value"]!["value"]!.GetValue<string>() == "18446744073709551615" && step["savedDatatype"]!.GetValue<string>() == "UInt");
        Check(enable["missingSourceTimestamps"]!.GetValue<int>() == 2 && enable["missingServerTimestamps"]!.GetValue<int>() == 3);
        Check(doc["events"]!["gapRows"]!.GetValue<int>() == 1 && doc["captureSource"]!["rows"]!.AsArray().Count == 8);
        Check(step["nullValues"]!.GetValue<int>() == 1 && step["lastObservation"]!["value"]!["value"] is null);
        Check(doc["hypotheses"]!.AsArray().All(h => h!["verdict"]!.GetValue<string>() == "unresolved"));
        Check(Id(service.Observe(prefix)) == empty && Id(service.Export(empty, "json")) == empty);
        foreach (string field in new[] { "atomicAcrossSignals", "scanSynchronized", "programMatchVerified", "addressMatchVerified", "datatypeMatchVerified", "sourceAuthenticityVerified", "physicalQualified", "clockSynchronizationVerified", "assertionsAdmitted", "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted", "sourceCompletenessVerified" }) Check(!doc[field]!.GetValue<bool>());
        byte[] original = File.ReadAllBytes(receipt["artifact"]!["path"]!.GetValue<string>());
        foreach (string format in new[] { "json", "markdown", "html" })
        {
            var exported = service.Export(Id(receipt), format);
            string path = exported["artifact"]!["path"]!.GetValue<string>();
            if (format == "json") Check(File.ReadAllBytes(path).SequenceEqual(original));
            else Check(File.ReadAllText(path).Contains("18446744073709551615", StringComparison.Ordinal));
            if (format == "html") Check(!File.ReadAllText(path).Contains("<script>", StringComparison.Ordinal));
        }
        foreach (Action<JsonObject> mutate in new Action<JsonObject>[] {
            d => d["signals"]![0]!["goodValues"] = 999, d => d["hypotheses"]![0]!["verdict"] = "supported",
            d => d["bindingArtifact"]!["base64"] = "e30=", d => d["captureSource"]!["rows"]![0]!["elapsedMilliseconds"] = 99,
            d => d["sourceCompletenessVerified"] = true, d => d["signals"]![1]!["firstObservation"]!["value"]!["value"] = "1" })
        {
            var changed = doc.DeepClone().AsObject(); mutate(changed);
            Reject("UaDiagnosticReportMismatch", () => service.Export(Id(store.Save(changed)), "json"));
        }
        string bindingPath = Path.Combine(f.Root, "trace-diagnostics", binding + ".json");
        byte[] bindingBytes = File.ReadAllBytes(bindingPath); File.AppendAllText(bindingPath, " ");
        Reject("ArtifactChanged", () => service.Export(Id(receipt), "html")); File.WriteAllBytes(bindingPath, bindingBytes);
        var unbound = request with { DiagnosticBindingId = null };
        var unboundWriter = captures.Create(plan with { CaptureId = new('d',32), UaContext = new(unbound, session) });
        unboundWriter.Finish("stopped", 0);
        Reject("UaDiagnosticBindingRequired", () => service.Observe(unboundWriter.Reference));
        Samples = new JsonObject { ["authority"] = "syntheticManagedProducer", ["empty"] = emptyDoc.DeepClone(), ["observed"] = doc.DeepClone() };
        Console.WriteLine($"PASS UA observations: {checks} original-prefix, quality, exact value, export, tamper and compatibility checks.");
    }
}
