using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime.Tests;

// runtime_ua_call, runtime_ua_conditions and runtime_ua_history_probe against the actual encrypted fixture server.
internal static class MethodConditionChecks
{
    private const string Ua = "http://opcfoundation.org/UA/";
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
    private static OpcUaRuntimeCallArgument Arg(string encoding, object value) => new(encoding, JsonSerializer.SerializeToElement(value));
    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    internal static async Task Run(FixtureServer server, string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "method-condition-checks");
        await using var sessions = new OpcUaRuntimeSessionStore(Path.Combine(root, "sessions"));
        void Save(string name, JsonElement value) => File.WriteAllText(Path.Combine(root, name + ".json"), value.GetRawText());
        int CallServices() => server.Audit.Count(x => Json(x).TryGetProperty("operation", out var o) && o.GetString() == "Call");
        async Task Throws(Func<Task> action, string code, string name)
        {
            string? actual = null;
            try { await action(); } catch (BridgeException e) { actual = e.Code; }
            check(actual == code, name + " (" + actual + ")");
        }
        string attempt = Guid.NewGuid().ToString("N");
        await sessions.Connect(attempt, server.Selection);
        var instance = FixtureServer.PlcNode("\"ServerMethod_DB\""); var method = FixtureServer.PlcNode("\"ServerMethod_DB\".Method");
        var limits = FixtureServer.PlcNode("\"Limits_DB\"");

        int calls = server.MethodCalls;
        var called = Json(await sessions.Call(attempt, instance, method, [Arg("decimalInteger", "41"), Arg("boolean", true)])); Save("called", called);
        var outputs = called.GetProperty("outputs");
        check(S(called, "state") == "called" && called.GetProperty("dispatched").GetBoolean() && S(called, "statusCode") == "0x00000000" &&
            S(outputs[0], "name") == "OutInt" && S(outputs[0], "encoding") == "decimalInteger" && S(outputs[0], "value") == "42" &&
            outputs[1].GetProperty("value").GetBoolean() == false, "Server method called with typed inputs; exact typed outputs");
        check(called.GetProperty("method").GetProperty("inputArguments").EnumerateArray().Select(a => S(a, "builtInType")).SequenceEqual(["Int16", "Boolean"]),
            "Declared InputArguments are retained beside the result");
        check(server.MethodCalls == calls + 1, "One Call executes the method exactly once");

        async Task Refused(OpcUaRuntimeNode o, OpcUaRuntimeNode m, OpcUaRuntimeCallArgument[] a, string code, string name)
        {
            int executed = server.MethodCalls, services = CallServices();
            var r = Json(await sessions.Call(attempt, o, m, a));
            check(S(r, "state") == "refused" && !r.GetProperty("dispatched").GetBoolean() && S(r, "refusal") == code &&
                server.MethodCalls == executed && CallServices() == services, name);
        }
        await Refused(new(Ua, "numeric", "2253"), new(Ua, "numeric", "11492"), [], "UaMethodNotServerMethod", "A standard Server-object method is not a server method: no arbitrary NodeId calls");
        await Refused(limits, method, [Arg("decimalInteger", "41"), Arg("boolean", true)], "UaMethodNotServerMethod", "A method of another instance is refused for this object");
        await Refused(instance, instance, [], "UaMethodNotServerMethod", "An object is not a method");
        await Refused(instance, method, [Arg("decimalInteger", "41")], "UaMethodArgumentInvalid", "Argument count is checked against InputArguments");
        await Refused(instance, method, [Arg("boolean", true), Arg("boolean", true)], "UaMethodArgumentInvalid", "Argument encoding must fit the declared type");
        await Refused(instance, method, [Arg("decimalInteger", "40000"), Arg("boolean", true)], "UaMethodArgumentInvalid", "An out-of-range Int16 is refused before dispatch");
        await Refused(limits, FixtureServer.PlcNode("\"Limits_DB\".Disabled"), [Arg("decimalInteger", "1")], "UaMethodNotExecutable", "A non-executable method is refused");
        await Refused(limits, FixtureServer.PlcNode("\"Limits_DB\".Dated"), [Arg("string", "2026-10-31T00:00:00Z")], "UaMethodArgumentUnsupported", "A method with an unsupported argument type is refused");
        await Refused(instance, FixtureServer.PlcNode("\"ServerMethod_DB\".Vector"), [Arg("decimalInteger", "1")], "UaMethodArgumentUnsupported",
            "A scalar-typed argument with an array ValueRank is refused");
        server.Mode = "duplicateArguments";
        await Refused(instance, method, [Arg("decimalInteger", "41"), Arg("boolean", true)], "UaMethodArgumentsUnreadable", "InputArguments listed twice is refused, not a failure");
        server.Mode = "normal";
        int services = CallServices();
        await Throws(() => sessions.Call(attempt, instance, method, Enumerable.Repeat(Arg("boolean", true), 17).ToArray()), "UaRequestLimit", "More than 16 inputs refused before network");
        await Throws(() => sessions.Call(attempt, instance, method, [new("array", JsonSerializer.SerializeToElement(1))]), "UaRequestLimit", "Only scalar input encodings are accepted");
        check(CallServices() == services, "Input refusals send no Call service");
        var failed = Json(await sessions.Call(attempt, instance, method, [Arg("decimalInteger", "32767"), Arg("boolean", false)])); Save("method-failed", failed);
        check(S(failed, "state") == "methodFailed" && failed.GetProperty("dispatched").GetBoolean() && S(failed, "statusCode") == "0x803c0000",
            "The method's own Bad result is returned as methodFailed, not retried");
        check((await sessions.Status(attempt)).State == "connected", "Refusals and a method failure keep the session usable");

        var history = Json(await sessions.HistoryProbe(attempt, FixtureServer.Node("int64"))); Save("history-probe", history);
        var raw = history.GetProperty("historyReadRaw");
        check(S(history.GetProperty("product"), "historyRead") == "refused" && S(history.GetProperty("product"), "code") == "UaHistoryReadUnsupported" &&
            history.GetProperty("capabilities").GetArrayLength() == 2 && raw.GetProperty("valueCount").ValueKind == JsonValueKind.Null &&
            (raw.GetProperty("serviceStatusCode").ValueKind == JsonValueKind.String || (Convert.ToUInt32(S(raw, "statusCode"), 16) & 0x80000000) != 0),
            "HistoryRead probe records the server's refusal and returns no history values");

        var idle = Json(await sessions.Conditions(attempt, 1500, 16)); Save("conditions-idle", idle);
        string[] Kinds(JsonElement e) => e.GetProperty("events").EnumerateArray().Select(x => S(x, "kind")).ToArray();
        check(idle.GetProperty("refresh").GetProperty("startObserved").GetBoolean() && idle.GetProperty("refresh").GetProperty("endObserved").GetBoolean() &&
            !Kinds(idle).Contains("condition"), "Idle ConditionRefresh: RefreshStart and RefreshEnd only");
        server.SetAlarm(true, false);
        var active = Json(await sessions.Conditions(attempt, 1500, 16)); Save("conditions-active", active);
        var conditions = active.GetProperty("events").EnumerateArray().Where(x => S(x, "kind") == "condition").ToArray();
        check(conditions.Length == 1 && conditions[0].GetProperty("activeState").GetBoolean() && !conditions[0].GetProperty("ackedState").GetBoolean() &&
            conditions[0].GetProperty("retain").GetBoolean() && conditions[0].GetProperty("severity").GetInt32() == 900 &&
            S(conditions[0], "sourceName") == "Synthetic alarms" && S(conditions[0].GetProperty("message"), "text") == "Overtemperature <b>" &&
            conditions[0].GetProperty("conditionId").Deserialize<OpcUaRuntimeNode>(BridgeProtocol.Json) == FixtureServer.Node("alarms.Overtemperature"),
            "An active unacknowledged condition is refreshed with its ConditionType and AlarmConditionType fields");
        server.SetAlarm(false, true);
        var cleared = Json(await sessions.Conditions(attempt, 1500, 16)); Save("conditions-cleared", cleared);
        check(!Kinds(cleared).Contains("condition") && cleared.GetProperty("refresh").GetProperty("endObserved").GetBoolean(),
            "An inactive acknowledged condition is no longer retained");
        await Throws(() => sessions.Conditions(attempt, 100, 16), "UaRequestLimit", "The condition window is bounded");
        await Throws(() => sessions.Conditions(attempt, 1500, 0), "UaRequestLimit", "The condition count is bounded");
        server.Mode = "conditionRefreshBad";
        var badRefresh = Json(await sessions.Conditions(attempt, 1000, 16)); Save("conditions-refresh-bad", badRefresh);
        server.Mode = "subscriptionStatus";
        var ended = Json(await sessions.Conditions(attempt, 3000, 16)); Save("conditions-status-bad", ended);
        server.Mode = "normal";
        check(S(badRefresh.GetProperty("refresh"), "statusCode") == "0x80280000" && !badRefresh.GetProperty("refresh").GetProperty("startObserved").GetBoolean(),
            "A Bad ConditionRefresh result is reported, not hidden");
        check(ended.GetProperty("subscriptionEnded").GetBoolean() && ended.GetProperty("publishes").GetInt32() == 1 &&
            ended.GetProperty("statusChanges").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["0x800a0000"]), "A Bad subscription status ends the window");
        check((await sessions.Status(attempt)).State == "connected", "Condition snapshots leave the session connected");

        // A cancelled snapshot still deletes its subscription before anything closes the session.
        int audited = server.Audit.Count;
        using (var cancel = new CancellationTokenSource(800))
            await Throws(() => sessions.Conditions(attempt, 5000, 16, cancel.Token), "UaCancelled", "A cancelled condition snapshot reports UaCancelled");
        await sessions.Disconnect(attempt, acknowledgeUnknown: true);
        var operations = server.Audit.Skip(audited).Select(x => Json(x).TryGetProperty("operation", out var o) ? o.GetString() : null).ToList();
        int deleted = operations.IndexOf("SubscriptionDeleteRequested"), closed = operations.IndexOf("CloseSession");
        check(deleted >= 0 && closed > deleted, "A cancelled snapshot deletes its subscription before the session closes");

        // Namespace guards: no PLC namespace is a refusal that keeps the session; a foreign namespace 0 is no session at all.
        server.Mode = "noPlcNamespace";
        attempt = Guid.NewGuid().ToString("N");
        await sessions.Connect(attempt, server.Selection);
        await Refused(instance, method, [Arg("decimalInteger", "41"), Arg("boolean", true)], "UaMethodNotServerMethod", "A server without the PLC namespace refuses the call");
        check((await sessions.Status(attempt)).State == "connected", "The missing PLC namespace keeps the session usable");
        await sessions.Disconnect(attempt);
        server.Mode = "foreignNamespaceZero";
        attempt = Guid.NewGuid().ToString("N");
        check((await sessions.Connect(attempt, server.Selection)).State != "connected", "No session opens when namespace index 0 is not the standard namespace");
        await sessions.Disconnect(attempt, acknowledgeUnknown: true);
        server.Mode = "normal";
        attempt = Guid.NewGuid().ToString("N");
        await sessions.Connect(attempt, server.Selection);
        server.Mode = "foreignNamespaceZero"; // Without the index-0 rule this would be UaSessionIdentityChanged.
        await Throws(() => sessions.Read(attempt, [FixtureServer.Node("bool")]), "UaSessionIdentityMismatch", "The session check requires the standard namespace at index 0");
        await sessions.Disconnect(attempt, acknowledgeUnknown: true);
        server.Mode = "normal";
        attempt = Guid.NewGuid().ToString("N");
        await sessions.Connect(attempt, server.Selection);

        // A lost Call response: the session becomes unknown and the identical call is refused, never retried.
        server.CallDelayMs = 1500;
        calls = server.MethodCalls;
        using (var cancel = new CancellationTokenSource(300))
            await Throws(() => sessions.Call(attempt, instance, method, [Arg("decimalInteger", "1"), Arg("boolean", true)], cancel.Token), "UaCancelled", "A cancelled Call reports its outcome unknown");
        check((await sessions.Status(attempt)).State == "remoteSessionUnknown", "The session is remoteSessionUnknown after a lost Call response");
        await Throws(() => sessions.Call(attempt, instance, method, [Arg("decimalInteger", "1"), Arg("boolean", true)]), "UaSessionUnavailable", "The lost Call is not retried");
        for (int i = 0; i < 50 && server.MethodCalls == calls; i++) await Task.Delay(100);
        await Task.Delay(500);
        check(server.MethodCalls == calls + 1, "The server executed the lost call exactly once");
        server.CallDelayMs = 0;
        await sessions.Disconnect(attempt, acknowledgeUnknown: true);
    }
}
