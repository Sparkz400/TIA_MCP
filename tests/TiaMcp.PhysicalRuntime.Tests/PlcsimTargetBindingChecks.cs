using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

/// <summary>Owned-PLCSIM binding and gated Web mutation contracts; no simulator, TIA or network.</summary>
public static class PlcsimTargetBindingChecks
{
    public static int Run(string root)
    {
        int checks = 0;
        void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
        void Fails(Action action, string code)
        { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        string creation = new('a', 32), marker = new('b', 64);
        string Identity(string name = "Sim1", bool owned = true, string addresses = "\"192.168.0.10\",\"0.0.0.0\",\"10.0.0.1\"") =>
            $$$"""{"instanceId":3,"instanceName":"{{{name}}}","creationId":"{{{creation}}}","markerSha256":"{{{marker}}}","controllerAddresses":[{{{addresses}}}],"instance":{"id":3,"name":"{{{name}}}","owned":{{{(owned ? "true" : "false")}}},"storagePath":"C:\\sim\\Sim1"}}""";
        var observed = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
        var binding = PlcsimTargetBindings.Observed(Json(Identity()), 3, "Sim1", creation, observed);
        Check(binding.ControllerAddresses.SequenceEqual(["10.0.0.1", "192.168.0.10"]), "Placeholder address dropped and addresses ordered");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity()), 3, "Sim2", creation, observed), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity(owned: false)), 3, "Sim1", creation, observed), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity(addresses: "\"0.0.0.0\"")), 3, "Sim1", creation, observed), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity()), 3, "Sim1", new('c', 32), observed), "PlcsimTargetBindingInvalid");

        Check(PlcsimTargetBindings.Covers(binding, "https://192.168.0.10") && PlcsimTargetBindings.Covers(binding, "opc.tcp://10.0.0.1:4840/"), "Owned controller addresses bind Web and UA endpoints");
        Check(!PlcsimTargetBindings.Covers(binding, "https://192.168.0.11") && !PlcsimTargetBindings.Covers(binding, "https://plc.example"), "Other addresses and host names stay unbound");
        Fails(() => PlcsimTargetBindings.RequireEndpoint(binding, "https://192.168.0.11"), "RuntimeTargetNotOwnedPlcsim");
        Fails(() => PlcsimTargetBindings.RequireSameInstance(binding, binding with { MarkerSha256 = new('c', 64) }), "PlcsimTargetBindingStale");
        PlcsimTargetBindings.RequireSameInstance(binding, binding with { ObservedUtc = observed.AddMinutes(1), ControllerAddresses = [.. binding.ControllerAddresses] });

        Directory.CreateDirectory(root);
        var reference = PlcsimTargetBindings.Retain(root, binding);
        string sha256 = (string)reference["sha256"]!;
        Check((string?)reference["path"] == "runtime-plcsim-bindings/" + sha256 + ".json" && PlcsimTargetBindings.Retain(root, binding).ToJsonString() == reference.ToJsonString(), "Content-addressed and idempotent");
        var reopened = PlcsimTargetBindings.Read(root, sha256);
        Check(reopened.MarkerSha256 == marker && reopened.ControllerAddresses.SequenceEqual(binding.ControllerAddresses) && reopened.ProgramFingerprintSha256 is null, "Binding reopens exactly");
        File.AppendAllText(Path.Combine(root, "runtime-plcsim-bindings", sha256 + ".json"), " ");
        Fails(() => PlcsimTargetBindings.Read(root, sha256), "ArtifactChanged");

        string fingerprint = new('d', 64);
        string Compare(string name, string address, bool available = true) =>
            $$$"""{"instanceId":3,"instanceName":"{{{name}}}","fingerprint":{"available":{{{(available ? "true" : "false")}}},"address":"{{{address}}}","sha256":"{{{fingerprint}}}"}}""";
        Check(PlcsimTargetBindings.Fingerprint(binding, Json(Compare("Sim1", "192.168.0.10"))) == fingerprint, "Fingerprint of the same instance at its own address");
        Check(PlcsimTargetBindings.Fingerprint(binding, Json(Compare("Sim1", "192.168.0.10", available: false))) is null, "No exposed fingerprint stays unobserved");
        Fails(() => PlcsimTargetBindings.Fingerprint(binding, Json(Compare("Other", "192.168.0.10"))), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Fingerprint(binding, Json(Compare("Sim1", "192.168.0.99"))), "PlcsimTargetBindingInvalid");
        Check(PlcsimTargetBindings.ProgramIdentity(binding with { ProgramFingerprintSha256 = fingerprint })!["value"]!.GetValue<string>() == fingerprint, "Bound program identity is the online fingerprint");

        string id = new('4', 32);
        var context = WebRuntimeProtocol.Context("https://192.168.0.10", new('1', 32), new('2', 32), new('3', 64));
        var symbol = WebRuntimeProtocol.Symbol(context, "\"DB\".Speed", "simple", Json("{\"name\":\"Speed\",\"datatype\":\"Int\"}"));
        var write = WebRuntimeProtocol.Write(id, symbol, Json("42"));
        Check(JsonElement.DeepEquals(write, Json($$$"""{"jsonrpc":"2.0","id":"{{{id}}}","method":"PlcProgram.Write","params":{"var":"\"DB\".Speed","value":42,"mode":"simple"}}""")), "Documented PlcProgram.Write shape");
        Check(WebRuntimeProtocol.ChangeOperatingMode(id, "stop").GetProperty("params").GetProperty("mode").GetString() == "stop", "Documented RUN/STOP request");
        Fails(() => WebRuntimeProtocol.ChangeOperatingMode(id, "hold"), "RuntimeOperatingModeInvalid");
        Fails(() => WebRuntimeProtocol.Write(id, symbol with { Representation = "raw" }, Json("[1]")), "RuntimeWriteInvalid");
        Check(WebRuntimeProtocol.MutationPermission("PlcProgram.Write") == "write_value" && WebRuntimeProtocol.MutationPermission("Plc.RequestChangeOperatingMode") == "change_operating_mode", "Documented permissions");
        Fails(() => WebRuntimeProtocol.MutationPermission("Alarms.Acknowledge"), "RuntimeMethodUnsupported");
        ReadOnlyMemory<byte> Response(string body) => Encoding.UTF8.GetBytes($$$"""{"jsonrpc":"2.0","id":"{{{id}}}",{{{body}}}}""");
        Check(WebRuntimeProtocol.MutationResult(Response("\"result\":true"), id) is ("accepted", null), "True acknowledges the request");
        Check(WebRuntimeProtocol.MutationResult(Response("\"error\":{\"code\":2}"), id) is ("permissionDenied", 2), "Provider denial stays explicit");
        Fails(() => WebRuntimeProtocol.MutationResult(Response("\"result\":false"), id), "RuntimeResponseInvalid");
        return checks;
    }
}
