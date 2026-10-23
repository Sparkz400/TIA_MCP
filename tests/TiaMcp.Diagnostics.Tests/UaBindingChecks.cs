using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

internal static class UaBindingChecks
{
    internal static JsonObject? Samples;
    internal static JsonObject Request() => JsonNode.Parse("""
        {"attemptId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","reason":"Observe explicitly selected conveyor declarations through UA.",
         "mappings":[
           {"signalId":"enable","node":{"namespaceUri":"urn:diagnostic:fixture","identifierType":"string","identifier":"Enable"},"unit":"bool","reason":"Selected enable observation"},
           {"signalId":"step","node":{"namespaceUri":"urn:diagnostic:fixture","identifierType":"numeric","identifier":"123"},"unit":"count","reason":"Selected step observation"}]}
        """)!.AsObject();
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("UA binding check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        string Id(JsonNode receipt) => receipt["id"]!.GetValue<string>();
        using var f = new Fixture();
        var experiments = new DiagnosticExperiments(f.Root);
        string experimentId = Id(experiments.Plan(Id(f.Plan()), ExperimentChecks.Request(f)));
        var service = new DiagnosticUaBindings(f.Root);
        var request = Request(); string original = request.ToJsonString();
        var receipt = service.Plan(experimentId, request); string id = Id(receipt);
        var binding = receipt["binding"]!.AsObject();
        Check(original == request.ToJsonString());
        Check(Id(service.Plan(experimentId, request)) == id);
        Check(JsonNode.DeepEquals(binding, new DiagnosticUaBindings(f.Root).Read(id)));
        Check(binding["mappings"]![0]!["declaration"]!["storagePath"]!.GetValue<string>() == Fixture.Block("DB_Line"));
        Check(JsonNode.DeepEquals(binding["mappings"]![0]!["declaration"]!["memberPath"], new JsonArray("Conv2", "Enable")));
        Check(binding["experiment"]!["diagnosticCase"]!["selectedInstance"]!["memberPath"]![0]!.GetValue<string>() == "Conv2");
        foreach (string flag in new[] { "programMatchVerified", "addressMatchVerified", "datatypeMatchVerified", "sourceAuthenticityVerified",
            "physicalQualified", "clockSynchronizationVerified", "assertionsAdmitted", "nativeAccessAttempted" }) Check(binding[flag]!.GetValue<bool>() == false);
        OpcUaRuntimeNode[] nodes = [new("urn:diagnostic:fixture", "string", "Enable"), new("urn:diagnostic:fixture", "numeric", "123")];
        Check(JsonNode.DeepEquals(binding, service.ValidateForCapture(id, new('a', 32), nodes)));
        Reject("UaDiagnosticBindingMismatch", () => service.ValidateForCapture(id, new('b', 32), nodes));
        Reject("UaDiagnosticBindingMismatch", () => service.ValidateForCapture(id, new('a', 32), nodes.Reverse().ToArray()));
        Reject("UaDiagnosticBindingMismatch", () => service.ValidateForCapture(id, new('a', 32), nodes.Take(1).ToArray()));
        Reject("UaDiagnosticBindingMismatch", () => service.ValidateForCapture(id, new('a', 32), [nodes[0], nodes[1] with { NamespaceUri = "urn:other" }]));
        foreach (var (change, code) in new (Action<JsonObject>, string)[] {
            (r => r["mappings"] = new JsonArray(), "InvalidRequest"),
            (r => r["attemptId"] = new string('A',32), "InvalidRequest"),
            (r => r["programMatchVerified"] = true, "MeasurementFormatUnsupported"),
            (r => r["mappings"]![0]!["unit"] = "", "InvalidRequest"),
            (r => r["mappings"]![0]!["node"]!["namespaceIndex"] = 2, "MeasurementFormatUnsupported"),
            (r => r["mappings"]![0]!["node"]!["identifierType"] = "index", "UaCaptureInvalid"),
            (r => r["mappings"]![1]!["node"]!["identifier"] = "0123", "UaCaptureInvalid"),
            (r => r["mappings"]![0]!["signalId"] = "missing", "UaDiagnosticBindingMismatch"),
            (r => r["mappings"]![1]!["signalId"] = "enable", "UaDiagnosticBindingMismatch"),
            (r => r["mappings"]![1]!["node"] = r["mappings"]![0]!["node"]!.DeepClone(), "UaDiagnosticBindingMismatch") })
        {
            int count = Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).Length;
            var changed = request.DeepClone().AsObject(); change(changed);
            Reject(code, () => service.Plan(experimentId, changed));
            Check(Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).Length == count);
        }
        var store = new DiagnosticStore(f.Root);
        foreach (var change in new Action<JsonObject>[] {
            r => r["addressMatchVerified"] = true,
            r => r["mappings"]![0]!["declaration"]!["datatype"] = "UInt",
            r => r["mappings"]![0]!["node"]!["identifier"] = "Other",
            r => r["attemptId"] = new string('b',32),
            r => r["experiment"]!["diagnosticCase"]!["selectedInstance"]!["memberPath"] = new JsonArray("Conv1") })
        {
            var changed = binding.DeepClone().AsObject(); change(changed);
            Reject("UaDiagnosticBindingMismatch", () => service.Read(Id(store.Save(changed))));
        }
        string source = Path.Combine(f.Root, "project-reviews", f.ReviewId + ".json");
        byte[] bytes = File.ReadAllBytes(source); File.AppendAllText(source, " ");
        Reject("ArtifactChanged", () => service.Read(id)); File.WriteAllBytes(source, bytes);
        var oldRequest = new OpcUaCaptureRequest(new('a',32), nodes, 100, 1);
        string oldJson = JsonSerializer.Serialize(oldRequest, BridgeProtocol.Json);
        Check(!oldJson.Contains("diagnosticBindingId", StringComparison.Ordinal));
        Check(JsonSerializer.Deserialize<OpcUaCaptureRequest>(oldJson, BridgeProtocol.Json)!.DiagnosticBindingId is null);
        var bound = oldRequest with { DiagnosticBindingId = id };
        Check(JsonSerializer.Deserialize<OpcUaCaptureRequest>(JsonSerializer.Serialize(bound, BridgeProtocol.Json), BridgeProtocol.Json)!.DiagnosticBindingId == id);
        Check(JsonSerializer.Serialize(OpcUaCaptureContracts.StorageRequest(oldRequest), BridgeProtocol.Json) ==
            JsonSerializer.Serialize(OpcUaCaptureContracts.StorageRequest(bound), BridgeProtocol.Json));
        foreach (string invalid in new[] { "", "../plan", new string('A',64), new string('a',63), new string('g',64) })
            Reject("InvalidRequest", () => OpcUaCaptureContracts.StorageRequest(oldRequest with { DiagnosticBindingId = invalid }));
        var files = new JsonObject();
        foreach (string path in Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".lock", StringComparison.Ordinal)))
            files[Path.GetRelativePath(f.Root, path).Replace('\\', '/')] = Convert.ToBase64String(File.ReadAllBytes(path));
        Samples = new JsonObject { ["authority"] = "syntheticManagedProducer", ["nativeQualification"] = false,
            ["files"] = files, ["experimentId"] = experimentId, ["request"] = request.DeepClone(), ["binding"] = binding.DeepClone(), ["receipt"] = receipt.DeepClone() };
        Console.WriteLine($"PASS UA diagnostic binding: {checks} explicit selection, provenance, refusal and compatibility checks; no network or native access.");
    }
}
