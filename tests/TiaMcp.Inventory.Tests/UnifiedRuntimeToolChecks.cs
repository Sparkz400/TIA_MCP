using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using TiaMcp.Core;
using TiaMcp.Core.Security;
using TiaMcp.Core.UnifiedRuntime;
using TiaMcp.Host;

/// <summary>
/// runtime_unified_* tools and their session store against a scripted GraphQL server: no runtime, no TLS, no vault.
/// UnifiedRuntimeSessions.Connect has no seam for the transport or the vault (it opens the current user's credential
/// vault and a pinned TLS client), so a logged-in session is placed into the store by reflection (Seed); everything up to
/// the vault and everything after the login runs the product code. A change of the store's private shape fails Seed
/// loudly. No check here lets Connect reach the vault.
/// </summary>
internal static class UnifiedRuntimeToolChecks
{
    private sealed class ScriptedServer : HttpMessageHandler
    {
        internal Queue<string> Responses { get; } = new();
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Responses.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }

    private const string Pin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static UnifiedLoginSelection Login(string endpoint, string user = "op", string vault = "default")
    {
        var binding = UnifiedRuntimeSessions.Binding(endpoint, Pin, user);
        return new(endpoint, Pin, user, vault, new("protected-credential-reference.v1", new string('a', 32), new string('b', 32), ProtectedCredentialGuard.BindingHash(binding), new string('c', 64)));
    }

    /// <summary>Logs a client in against the scripted server and stores it as the live session of <paramref name="attemptId"/>.</summary>
    private static async Task<ScriptedServer> Seed(UnifiedRuntimeSessions sessions, string attemptId, Uri endpoint, UnifiedLoginSelection login)
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var server = new ScriptedServer();
        server.Responses.Enqueue("""{"data":{"login":{"token":"t0k","user":{"name":"op","fullName":"Op","language":"de-DE","autoLoggOffSec":600},"error":{"code":"0","description":"Success"}}}}""");
        server.Responses.Enqueue("""{"data":{"active":{"fields":[{"name":"name"},{"name":"path"},{"name":"instanceID"},{"name":"state"},{"name":"eventText"},{"name":"languages"}]},"logged":{"fields":[{"name":"name"},{"name":"instanceID"},{"name":"raiseTime"}]}}}""");
        var constructor = typeof(UnifiedGraphQlClient).GetConstructors(any).SingleOrDefault(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(Uri), typeof(HttpMessageHandler), typeof(Func<bool>)]))
            ?? throw new Exception("Seed: UnifiedGraphQlClient no longer has its (Uri, HttpMessageHandler, Func<bool>) test constructor.");
        var client = (UnifiedGraphQlClient)constructor.Invoke([endpoint, server, null]);
        await client.LoginAsync(login.User, Encoding.UTF8.GetBytes("\"synthetic-only\""), CancellationToken.None);
        var field = typeof(UnifiedRuntimeSessions).GetField("live", any) ?? throw new Exception("Seed: UnifiedRuntimeSessions no longer keeps its sessions in 'live'.");
        var entry = (typeof(UnifiedRuntimeSessions).GetNestedType("Live", BindingFlags.NonPublic) ?? throw new Exception("Seed: UnifiedRuntimeSessions.Live is gone."))
            .GetConstructors(any).Single(c => c.GetParameters().Length == 4).Invoke([client, login, endpoint, DateTimeOffset.UtcNow]);
        ((IDictionary)field.GetValue(sessions)!).Add(attemptId, entry);
        server.Bodies.Clear();
        return server;
    }

    private static string? Code(CallToolResult result) =>
        result.StructuredContent is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
    private static JsonElement Body(CallToolResult result) => result.StructuredContent!.Value;

    internal static async Task Run(Action<bool, string> check)
    {
        const string endpoint = "https://pc-b01/graphql/";
        string Id(char c) => new(c, 32);
        using var sessions = new UnifiedRuntimeSessions();
        var tools = new UnifiedRuntimeTools(sessions);
        var none = CancellationToken.None;

        // runtime_unified_credential_binding: public, canonical, validated.
        var binding = tools.Binding("https://PC-B01/graphql", Pin, "op");
        string bindingText = Body(binding).GetRawText();
        check(binding.IsError != true && bindingText.Contains("siemensWinCCUnifiedGraphQl") && bindingText.Contains("https://pc-b01/graphql/") && bindingText.Contains(Pin) && bindingText.Contains("runtimeLogin") &&
            bindingText.Contains("\"op\""), "runtime_unified_credential_binding names the provider, the canonical endpoint, the pin, the purpose and the user");
        check(bindingText == Body(tools.Binding(endpoint, Pin, "op")).GetRawText(), "The binding of an endpoint does not depend on its spelling");
        foreach (string bad in new[] { "http://pc-b01/graphql/", "https://localhost/graphql/", "https://127.0.0.1/graphql/", "https://pc-b01/other/", "not a uri", "" })
            check(Code(tools.Binding(bad, Pin, "op")) == "UnifiedEndpointInvalid", "runtime_unified_credential_binding refuses the endpoint '" + bad + "'");
        foreach (string bad in new[] { Pin.ToUpperInvariant(), Pin[..63], Pin + "0", Pin[..63] + "g", "" })
            check(Code(tools.Binding(endpoint, bad, "op")) == "UnifiedCertificatePinInvalid", "runtime_unified_credential_binding refuses the pin '" + bad + "'");
        foreach (string bad in new[] { "", " ", "a\nb", new string('u', 257) })
            check(Code(tools.Binding(endpoint, Pin, bad)) == "InvalidRequest", "runtime_unified_credential_binding refuses a user of " + bad.Length + " characters");
        check(tools.Binding(endpoint, Pin, new string('u', 256)).IsError != true, "A 256-character user is the upper bound");

        // UnifiedLoginSelection: value equality is what makes a repeated connect the same login.
        var login = Login(endpoint);
        check(login == Login(endpoint) && login != Login(endpoint, "other") && login != login with { VaultName = Id('d') } && login != login with { Credential = login.Credential with { CredentialId = Id('e') } },
            "UnifiedLoginSelection compares endpoint, pin, user, vault and credential by value");
        check(login.Credential.BindingSha256 != Login("https://pc-b02/graphql/").Credential.BindingSha256 && login.Credential.BindingSha256 != Login(endpoint, "other").Credential.BindingSha256,
            "A credential binding is specific to its endpoint and user");

        // runtime_unified_connect: every refusal before the vault.
        check(Code(await tools.Connect("not-an-id", login, none)) == "InvalidRequest" && Code(await tools.Connect(Id('A'), login, none)) == "InvalidRequest", "runtime_unified_connect refuses an attemptId that is not 32 lowercase hex characters");
        check(Code(await tools.Connect(Id('1'), login with { Endpoint = "https://localhost/graphql/" }, none)) == "UnifiedEndpointInvalid", "runtime_unified_connect refuses localhost");
        check(Code(await tools.Connect(Id('1'), login with { PeerCertificateSha256 = "pin" }, none)) == "UnifiedCertificatePinInvalid", "runtime_unified_connect refuses a malformed pin");
        check(Code(await tools.Connect(Id('1'), login with { Credential = login.Credential with { SchemaVersion = "v0" } }, none)) == "CredentialInputInvalid", "runtime_unified_connect refuses a malformed credential reference");
        check(Code(await tools.Connect(Id('1'), login with { User = "other" }, none)) == "RuntimeCredentialContextMismatch" &&
            Code(await tools.Connect(Id('1'), login with { Endpoint = "https://pc-b02/graphql/" }, none)) == "RuntimeCredentialContextMismatch" &&
            Code(await tools.Connect(Id('1'), login with { PeerCertificateSha256 = new string('f', 64) }, none)) == "RuntimeCredentialContextMismatch",
            "runtime_unified_connect refuses a credential created for another user, endpoint or certificate");
        check(Code(await tools.Connect(Id('1'), login with { VaultName = "Default" }, none)) == "InvalidRequest" && Code(await tools.Connect(Id('1'), login with { VaultName = "../x" }, none)) == "InvalidRequest",
            "runtime_unified_connect refuses a vault name that is neither default nor an ID");

        // Session lifecycle on a seeded session.
        check(Code(tools.Status(Id('1'))) == "RuntimeSessionNotFound" && Code(tools.Status("x")) == "InvalidRequest", "runtime_unified_status of an unknown or malformed attemptId is refused");
        var server = await Seed(sessions, Id('1'), new Uri(endpoint), login);
        var status = Body(tools.Status(Id('1')));
        check(status.GetProperty("schemaVersion").GetString() == "runtime-unified-session.v1" && status.GetProperty("attemptId").GetString() == Id('1') && status.GetProperty("endpoint").GetString() == endpoint &&
            status.GetProperty("user").GetString() == "op" && status.GetProperty("state").GetString() == "connected" && status.GetProperty("userLanguage").GetString() == "de-DE" &&
            status.GetProperty("autoLogoffSeconds").GetInt32() == 600 && status.GetProperty("activeAlarmFields").GetInt32() == 6 && status.GetProperty("loggedAlarmFields").GetInt32() == 3 &&
            !status.GetProperty("secretReturned").GetBoolean() && DateTimeOffset.TryParse(status.GetProperty("connectedAtUtc").GetString(), out _),
            "runtime_unified_status reports the session's endpoint, user, language, idle logoff and alarm fields");
        check(!status.GetRawText().Contains("t0k") && !status.GetRawText().Contains("synthetic-only") && server.Bodies.Count == 0, "runtime_unified_status returns no token or password and does not contact the server");
        var again = await tools.Connect(Id('1'), Login(endpoint), none);
        check(again.IsError != true && Body(again).GetProperty("state").GetString() == "connected" && Body(again).GetProperty("connectedAtUtc").GetString() == status.GetProperty("connectedAtUtc").GetString() &&
            server.Bodies.Count == 0, "Repeating runtime_unified_connect with the same attemptId and login returns the open session without a second login");
        check(Code(await tools.Connect(Id('1'), Login(endpoint, "other"), none)) == "RuntimeAttemptContextMismatch" && Code(await tools.Connect(Id('1'), login with { VaultName = Id('d') }, none)) == "RuntimeAttemptContextMismatch" &&
            server.Bodies.Count == 0, "An attemptId cannot be reused for another login");

        // runtime_unified_alarms
        server.Responses.Enqueue("""{"data":{"activeAlarms":[{"name":"Alarm_Tag01","path":"HMI_RT_1::Tag01:Alarm_Tag01","instanceID":3,"eventText":["Übertemperatur"],"languages":["de-DE"]},{"name":"B","path":"HMI_RT_1::B:B","instanceID":1}]}}""");
        var alarms = Body(await tools.Alarms(Id('1'), none));
        check(alarms.GetProperty("kind").GetString() == "active" && alarms.GetProperty("languageSource").GetString() == "umcUserLanguage" && alarms.GetProperty("languages")[0].GetString() == "de-DE" &&
            alarms.GetProperty("returned").GetInt32() == 2 && !alarms.GetProperty("truncated").GetBoolean() &&
            alarms.GetProperty("alarms")[0].GetProperty("acknowledge").GetProperty("path").GetString() == "HMI_RT_1::Tag01:Alarm_Tag01", "runtime_unified_alarms defaults to active alarms in the UMC user's language");
        using (var sent = JsonDocument.Parse(server.Bodies.Single()))
            check(sent.RootElement.GetProperty("query").GetString()!.Contains("activeAlarms(") && sent.RootElement.GetProperty("variables").GetProperty("languages")[0].GetString() == "de-DE",
                "The active alarm request names its languages explicitly");
        server.Bodies.Clear();
        server.Responses.Enqueue("""{"data":{"loggedAlarms":[{"name":"A","instanceID":1},{"name":"B","instanceID":2}]}}""");
        var logged = Body(await tools.Alarms(Id('1'), none, "logged", ["en-US", "de-DE"], maxItems: 1, startTime: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
        check(logged.GetProperty("languageSource").GetString() == "request" && logged.GetProperty("returned").GetInt32() == 1 && logged.GetProperty("totalReturnedByServer").GetInt32() == 2 &&
            logged.GetProperty("truncated").GetBoolean() && logged.GetProperty("note").GetString()!.Contains("cannot be acknowledged"), "A full page of logged alarms reports truncation and that logged alarms cannot be acknowledged");
        check(server.Bodies.Single().Contains("loggedAlarms(") && server.Bodies.Single().Contains("en-US"), "The logged alarm request carries the requested languages");
        server.Bodies.Clear();
        check(Code(await tools.Alarms(Id('1'), none, maxItems: 0)) == "InvalidRequest" && Code(await tools.Alarms(Id('1'), none, maxItems: UnifiedAlarmRules.MaxAlarms + 1)) == "InvalidRequest" &&
            Code(await tools.Alarms(Id('1'), none, "history")) == "InvalidRequest" && Code(await tools.Alarms(Id('1'), none, startTime: DateTimeOffset.UtcNow)) == "InvalidRequest" &&
            Code(await tools.Alarms(Id('1'), none, endTime: DateTimeOffset.UtcNow)) == "InvalidRequest" && Code(await tools.Alarms(Id('1'), none, languages: ["de-de"])) == "UnifiedLanguageInvalid" &&
            Code(await tools.Alarms(Id('2'), none)) == "RuntimeSessionNotFound" && server.Bodies.Count == 0, "runtime_unified_alarms refuses bad bounds, kinds, time ranges, languages and unknown sessions without a request");

        // runtime_unified_alarm_acknowledge: the acknowledgement and the route are checked before anything is sent.
        UnifiedAlarmIdentifier[] one = [new("HMI_RT_1::Tag01:Alarm_Tag01", 3)];
        check(Code(await tools.Acknowledge(Id('1'), one, false, none)) == "WriteAcknowledgementRequired" && Code(await tools.Acknowledge(Id('2'), one, false, none)) == "WriteAcknowledgementRequired" &&
            server.Bodies.Count == 0, "runtime_unified_alarm_acknowledge without acknowledgeWrite is refused before the session is looked up");
        check(Code(await tools.Acknowledge(Id('2'), one, true, none)) == "RuntimeSessionNotFound" && Code(await tools.Acknowledge("x", one, true, none)) == "InvalidRequest", "Acknowledging on an unknown session is refused");
        // pc-b01 does not resolve to this PC (or not at all); 192.0.2.1 (TEST-NET-1) is an address no interface of this PC has.
        var remoteLogin = Login("https://192.0.2.1/graphql/");
        var remote = await Seed(sessions, Id('3'), new Uri("https://192.0.2.1/graphql/"), remoteLogin);
        var refused = await tools.Acknowledge(Id('3'), one, true, none);
        check(refused.IsError == true && Code(refused) == "UnifiedWriteRouteRefused" && Body(refused).GetProperty("error").GetProperty("details").GetProperty("host").GetString() == "192.0.2.1" &&
            Body(refused).GetProperty("error").GetProperty("details").GetProperty("addresses")[0].GetString() == "192.0.2.1" && remote.Bodies.Count == 0,
            "runtime_unified_alarm_acknowledge refuses a runtime on another machine and sends nothing to it");
        remote.Responses.Enqueue("""{"data":{"activeAlarms":[]}}""");
        check((await tools.Alarms(Id('3'), none)).IsError != true && remote.Bodies.Count == 1, "Reading alarms from a remote runtime stays allowed");
        check((await UnifiedRuntimeSessions.LocalEndpoint(new Uri("https://127.0.0.1/graphql/"), none)).IsLocal && (await UnifiedRuntimeSessions.LocalEndpoint(new Uri("https://[::1]/graphql/"), none)).IsLocal &&
            !(await UnifiedRuntimeSessions.LocalEndpoint(new Uri("https://192.0.2.1/graphql/"), none)).IsLocal, "Only loopback and this PC's own addresses are local");

        // A runtime on this PC. The endpoint rule forbids 127.0.0.1 for a login, so this session exists only through Seed.
        var localUri = new Uri("https://127.0.0.1/graphql/");
        var local = await Seed(sessions, Id('4'), localUri, login);
        foreach (var plain in new UnifiedAlarmIdentifier[][] { [new("Alarm_Tag01", null)], [new("HMI_RT_1::Tag01:", 1)], [new("HMI_RT_1::Tag01:Alarm_Tag01", 1), new("Alarm_Tag02", null)] })
            check(Code(await tools.Acknowledge(Id('4'), plain, true, none)) == "UnifiedAlarmPathRequired" && local.Bodies.Count == 0, "A plain alarm name is refused and nothing is sent: " + plain[^1].Path);
        check(Code(await tools.Acknowledge(Id('4'), [], true, none)) == "InvalidRequest" && Code(await tools.Acknowledge(Id('4'), [one[0], one[0]], true, none)) == "InvalidRequest" &&
            Code(await tools.Acknowledge(Id('4'), Enumerable.Range(0, UnifiedAlarmRules.MaxAcknowledge + 1).Select(i => new UnifiedAlarmIdentifier("S::T:A" + i, null)).ToArray(), true, none)) == "InvalidRequest" &&
            local.Bodies.Count == 0, "Acknowledging no alarm, the same alarm twice or more than 64 alarms is refused and nothing is sent");
        local.Responses.Enqueue("""{"data":{"acknowledgeAlarms":[{"name":"Alarm_Tag01","path":"HMI_RT_1::Tag01:Alarm_Tag01","instanceID":3,"error":{"code":"0","description":""}}]}}""");
        var acknowledged = Body(await tools.Acknowledge(Id('4'), one, true, none));
        check(acknowledged.GetProperty("acknowledged").GetBoolean() && acknowledged.GetProperty("requested").GetInt32() == 1 && acknowledged.GetProperty("itemErrors").GetInt32() == 0 && local.Bodies.Count == 1,
            "An acknowledged alarm on this PC is sent once and reported as acknowledged");
        using (var sent = JsonDocument.Parse(local.Bodies[0]))
            check(sent.RootElement.GetProperty("variables").GetProperty("input")[0].GetProperty("name").GetString() == "HMI_RT_1::Tag01:Alarm_Tag01" &&
                sent.RootElement.GetProperty("variables").GetProperty("input")[0].GetProperty("instanceID").GetInt32() == 3, "The acknowledge request carries the fully qualified path and the instance");
        local.Responses.Enqueue("""{"data":{"acknowledgeAlarms":[{"name":"Alarm_Tag01","path":"HMI_RT_1::Tag01:Alarm_Tag01","instanceID":3,"error":{"code":"305","description":"not acknowledgeable"}}]}}""");
        var failed = Body(await tools.Acknowledge(Id('4'), one, true, none));
        check(!failed.GetProperty("acknowledged").GetBoolean() && failed.GetProperty("itemErrors").GetInt32() == 1, "An item error from the server is not reported as acknowledged");
        local.Responses.Enqueue("""{"data":{"acknowledgeAlarms":[]}}""");
        check(!Body(await tools.Acknowledge(Id('4'), one, true, none)).GetProperty("acknowledged").GetBoolean(), "An empty server result is not reported as acknowledged");

        // Capacity: four sessions; the fifth is refused before the vault is opened.
        await Seed(sessions, Id('5'), new Uri(endpoint), login);
        check(Code(await tools.Connect(Id('6'), login, none)) == "RuntimeSessionCapacity", "A fifth Unified session is refused");

        // runtime_unified_disconnect
        var closed = Body(await tools.Disconnect(Id('1'), none));
        check(closed.GetProperty("closed").GetBoolean() && closed.GetProperty("attemptId").GetString() == Id('1') && Code(tools.Status(Id('1'))) == "RuntimeSessionNotFound" &&
            Code(await tools.Alarms(Id('1'), none)) == "RuntimeSessionNotFound" && server.Bodies.Count == 0, "runtime_unified_disconnect drops the session without contacting the server");
        check(!Body(await tools.Disconnect(Id('1'), none)).GetProperty("closed").GetBoolean() && !Body(await tools.Disconnect(Id('9'), none)).GetProperty("closed").GetBoolean(),
            "Disconnecting a closed or unknown session reports closed:false");
        check(Body(tools.Status(Id('3'))).GetProperty("state").GetString() == "connected" && Body(tools.Status(Id('4'))).GetProperty("state").GetString() == "connected", "Disconnecting one session leaves the others open");
        sessions.Dispose();
        check(Code(tools.Status(Id('3'))) == "RuntimeSessionNotFound" && Code(tools.Status(Id('4'))) == "RuntimeSessionNotFound" && Code(tools.Status(Id('5'))) == "RuntimeSessionNotFound", "Disposing the store drops every session");
    }
}
