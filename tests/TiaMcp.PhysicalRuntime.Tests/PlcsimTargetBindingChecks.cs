using System.Globalization;
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
        void Check(bool condition, string label) { checks++; if (!condition) throw new InvalidOperationException(label); }
        void Fails(Action action, string code)
        { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new InvalidOperationException("Expected " + code); }
        JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        string creation = new('a', 32), marker = new('b', 64);
        const string Plcsim = "Siemens PLCSIM Virtual Ethernet Adapter"; // the Windows adapter description
        string Identity(string name = "Sim1", bool owned = true, string addresses = "\"192.168.0.10\",\"0.0.0.0\",\"10.0.0.1\"", string communication = "TCPIP") =>
            $$$"""{"instanceId":3,"instanceName":"{{{name}}}","creationId":"{{{creation}}}","markerSha256":"{{{marker}}}","controllerAddresses":[{{{addresses}}}],"communicationInterface":"{{{communication}}}","instance":{"id":3,"name":"{{{name}}}","owned":{{{(owned ? "true" : "false")}}},"storagePath":"C:\\sim\\Sim1"}}""";
        var observed = DateTimeOffset.Parse("2026-10-27T12:00:00Z", CultureInfo.InvariantCulture);
        var binding = PlcsimTargetBindings.Observed(Json(Identity()), 3, "Sim1", creation, observed);
        Check(binding.ControllerAddresses.SequenceEqual(["10.0.0.1", "192.168.0.10"]), "Placeholder address dropped and addresses ordered");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity()), 3, "Sim2", creation, observed), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity(owned: false)), 3, "Sim1", creation, observed), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity(addresses: "\"0.0.0.0\"")), 3, "Sim1", creation, observed), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity()), 3, "Sim1", new('c', 32), observed), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity(communication: "None")), 3, "Sim1", creation, observed), "PlcsimTargetBindingInvalid");
        Fails(() => PlcsimTargetBindings.Observed(Json(Identity().Replace(",\"communicationInterface\":\"TCPIP\"", "")), 3, "Sim1", creation, observed), "PlcsimTargetBindingInvalid");
        Check(binding.CommunicationInterface == "TCPIP", "The observed interface is recorded");

        // Softbus is local to this PC: it never backs a network endpoint, whatever its addresses.
        var softbus = PlcsimTargetBindings.Observed(Json(Identity(addresses: "\"192.168.0.1\"", communication: "Softbus")), 3, "Sim1", creation, observed);
        Fails(() => PlcsimTargetBindings.RequireEndpoint(softbus, "opc.tcp://192.168.0.1:4840/", Plcsim), "PlcsimEndpointUnreachable");
        Check(!PlcsimTargetBindings.Covers(softbus, "https://192.168.0.1"), "A Softbus binding covers no endpoint");
        var tcpip = PlcsimTargetBindings.Observed(Json(Identity(addresses: "\"192.168.0.1\"")), 3, "Sim1", creation, observed);
        Fails(() => PlcsimTargetBindings.RequireEndpoint(tcpip, "opc.tcp://192.168.0.2:4840/", Plcsim), "RuntimeTargetNotOwnedPlcsim");
        PlcsimTargetBindings.RequireEndpoint(tcpip, "opc.tcp://192.168.0.1:4840/", Plcsim);
        PlcsimTargetBindings.RequireEndpoint(tcpip, "https://192.168.0.1", "siemens plcsim virtual ethernet adapter #2");
        // The same factory-default address on a plant network card is a physical PLC, whatever the binding says; an unknown route never qualifies.
        Fails(() => PlcsimTargetBindings.RequireEndpoint(tcpip, "https://192.168.0.1", "Intel(R) Ethernet Connection I219-LM"), "RuntimeTargetNotOwnedPlcsim");
        Fails(() => PlcsimTargetBindings.RequireEndpoint(tcpip, "https://192.168.0.1", ""), "RuntimeTargetNotOwnedPlcsim");
        Fails(() => PlcsimTargetBindings.RequireEndpoint(tcpip, "https://192.168.0.1", null), "RuntimeTargetRouteUnverified");
        // A session keeps the route it connected on: opened through a plant card, it stays refused after the PLCSIM adapter became the best route.
        PlcsimTargetBindings.RequireEndpoint(tcpip, "https://192.168.0.1", Plcsim, Plcsim);
        Fails(() => PlcsimTargetBindings.RequireEndpoint(tcpip, "https://192.168.0.1", "Intel(R) Ethernet Connection I219-LM", Plcsim), "RuntimeTargetNotOwnedPlcsim");
        Fails(() => PlcsimTargetBindings.RequireEndpoint(tcpip, "https://192.168.0.1", null, Plcsim), "RuntimeTargetRouteUnverified");
        Fails(() => PlcsimTargetBindings.RequireEndpoint(tcpip, "https://192.168.0.1", Plcsim, "Intel(R) Ethernet Connection I219-LM"), "RuntimeTargetNotOwnedPlcsim");
        Fails(() => PlcsimTargetBindings.RequireSameInstance(tcpip, tcpip with { CommunicationInterface = "Softbus" }), "PlcsimTargetBindingStale");

        Check(PlcsimTargetBindings.Covers(binding, "https://192.168.0.10") && PlcsimTargetBindings.Covers(binding, "opc.tcp://10.0.0.1:4840/"), "Owned controller addresses bind Web and UA endpoints");
        Check(!PlcsimTargetBindings.Covers(binding, "https://192.168.0.11") && !PlcsimTargetBindings.Covers(binding, "https://plc.example"), "Other addresses and host names stay unbound");
        Fails(() => PlcsimTargetBindings.RequireEndpoint(binding, "https://192.168.0.11", Plcsim), "RuntimeTargetNotOwnedPlcsim");
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

        // A binding retained before the interface was recorded stays readable but cannot back a network endpoint.
        string legacySha256 = (string)PlcsimTargetBindings.Retain(root, binding with { CommunicationInterface = null })["sha256"]!;
        Check(!File.ReadAllText(Path.Combine(root, "runtime-plcsim-bindings", legacySha256 + ".json")).Contains("communicationInterface", StringComparison.Ordinal) &&
            legacySha256 != sha256, "Legacy bytes omit the interface and hash differently");
        var legacy = PlcsimTargetBindings.Read(root, legacySha256);
        Check(legacy.CommunicationInterface is null, "Legacy binding reopens without an interface");
        Fails(() => PlcsimTargetBindings.RequireEndpoint(legacy, "https://192.168.0.10", Plcsim), "PlcsimEndpointUnreachable");

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
        Check(WebRuntimeProtocol.MutationPermission("Alarms.Acknowledge") == "acknowledge_alarms", "Documented acknowledge_alarms permission");
        Fails(() => WebRuntimeProtocol.MutationPermission("Plc.CreateBackup"), "RuntimeMethodUnsupported"); // a ticket creator, not a boolean mutation
        Check(JsonElement.DeepEquals(WebRuntimeProtocol.AcknowledgeAlarm(id, "9979413824317259784"),
            Json($$$"""{"jsonrpc":"2.0","id":"{{{id}}}","method":"Alarms.Acknowledge","params":{"id":"9979413824317259784"}}""")), "Documented Alarms.Acknowledge shape");
        Fails(() => WebRuntimeProtocol.AcknowledgeAlarm(id, "1\" , \"x\":2"), "RuntimeAlarmIdInvalid");
        Check(JsonElement.DeepEquals(WebRuntimeProtocol.AlarmById(id, "en-US", "12"),
            Json($$$"""{"jsonrpc":"2.0","id":"{{{id}}}","method":"Alarms.Browse","params":{"language":"en-US","alarm_id":"12"}}""")), "Documented single-alarm Alarms.Browse");
        Check(JsonElement.DeepEquals(WebRuntimeProtocol.TicketRequest(id, "serviceData", 49),
            Json($$$"""{"jsonrpc":"2.0","id":"{{{id}}}","method":"Modules.DownloadServiceData","params":{"hwid":49}}""")), "Documented service-data ticket request");
        Check(!WebRuntimeProtocol.TicketRequest(id, "backup").TryGetProperty("params", out _) && !WebRuntimeProtocol.TicketRequest(id, "profiling").TryGetProperty("params", out _), "Backup and profiling take no parameters");
        Fails(() => WebRuntimeProtocol.TicketRequest(id, "serviceData"), "RuntimeTicketSelectionInvalid");
        Fails(() => WebRuntimeProtocol.TicketRequest(id, "backup", 1), "RuntimeTicketSelectionInvalid");
        Fails(() => WebRuntimeProtocol.TicketRequest(id, "restore"), "RuntimeTicketKindUnsupported");
        Check(WebRuntimeProtocol.TicketKind("backup") == ("Plc.CreateBackup", "backup_plc") && WebRuntimeProtocol.TicketKind("serviceData").Permission == "download_service_data" &&
            WebRuntimeProtocol.TicketKind("profiling") == ("PlcProgram.DownloadProfilingData", "read_value"), "Documented ticket methods and permissions");
        ReadOnlyMemory<byte> Reply(string body) => Encoding.UTF8.GetBytes($$$"""{"jsonrpc":"2.0","id":"{{{id}}}",{{{body}}}}""");
        Check(WebRuntimeProtocol.TicketResult(Reply("\"result\":\"NDU2Nzg5MDEyMzQ1Njc4OTAxMjM0\""), id) is ("created", null, "NDU2Nzg5MDEyMzQ1Njc4OTAxMjM0"), "A ticket string is returned");
        Check(WebRuntimeProtocol.TicketResult(Reply("\"error\":{\"code\":1004}"), id) is ("plcNotInStop", 1004, null), "Backup refuses outside STOP explicitly");
        Fails(() => WebRuntimeProtocol.TicketResult(Reply("\"result\":\"a&b=c\""), id), "RuntimeResponseInvalid"); // would add a query parameter
        Fails(() => WebRuntimeProtocol.TicketResult(Reply("\"result\":true"), id), "RuntimeResponseInvalid");
        Check(WebRuntimeProtocol.AlarmState(Reply("\"result\":{\"entries\":[{\"id\":\"12\",\"acknowledgement\":{\"state\":\"not_acknowledged\"}}]}"), id, "12") is ("read", null, "not_acknowledged"), "Alarm acknowledgement state is read");
        Check(WebRuntimeProtocol.AlarmState(Reply("\"result\":{\"entries\":[{\"id\":\"12\"}]}"), id, "12") is ("read", null, null), "An alarm without an acknowledgement object has nothing to acknowledge");
        Check(WebRuntimeProtocol.AlarmState(Reply("\"result\":{\"entries\":[]}"), id, "12") is ("alarmNotListed", null, null), "An empty list is not an acknowledgement");
        Check(WebRuntimeProtocol.AlarmState(Reply("\"error\":{\"code\":800}"), id, "12") is ("invalidAlarmId", 800, null), "Error 800 is an invalid alarm id");
        Fails(() => WebRuntimeProtocol.AlarmState(Reply("\"result\":{\"entries\":[{\"id\":\"13\"}]}"), id, "12"), "RuntimeResponseInvalid");
        ReadOnlyMemory<byte> Response(string body) => Encoding.UTF8.GetBytes($$$"""{"jsonrpc":"2.0","id":"{{{id}}}",{{{body}}}}""");
        Check(WebRuntimeProtocol.MutationResult(Response("\"result\":true"), id) is ("accepted", null), "True acknowledges the request");
        Check(WebRuntimeProtocol.MutationResult(Response("\"error\":{\"code\":2}"), id) is ("permissionDenied", 2), "Provider denial stays explicit");
        Fails(() => WebRuntimeProtocol.MutationResult(Response("\"result\":false"), id), "RuntimeResponseInvalid");
        return checks;
    }
}
