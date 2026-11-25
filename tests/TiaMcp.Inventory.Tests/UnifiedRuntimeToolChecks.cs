using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using TiaMcp.Core;
using TiaMcp.Core.Security;
using TiaMcp.Core.UnifiedRuntime;
using TiaMcp.Host;

/// <summary>
/// runtime_unified_* tools and their session store end to end through runtime_unified_connect, against scripted GraphQL
/// runtimes: no runtime, no real TLS. Each runtime presents a self-signed certificate to the production peer check
/// (UnifiedGraphQlClient.PeerValidation) on every request, as the pinned handler does on each new connection, and refuses
/// the request the way SocketsHttpHandler does when that check fails. The password comes from a current-user DPAPI vault
/// created under the test output, through the production vault code; the user's own vault is never opened.
/// </summary>
internal static class UnifiedRuntimeToolChecks
{
    private const string Secret = "synthetic-only";
    private const string Pin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>One scripted runtime web server: the certificate it presents, the policy errors the platform reports for it and
    /// the GraphQL answers it gives.</summary>
    private sealed class Runtime(X509Certificate2 certificate, SslPolicyErrors errors = SslPolicyErrors.RemoteCertificateChainErrors)
    {
        internal Queue<string> Responses { get; } = new();
        internal List<string> Bodies { get; } = [];
        internal List<string?> Authorization { get; } = [];
        internal int Handshakes { get; private set; }

        internal async Task<HttpResponseMessage> Answer(HttpRequestMessage request, RemoteCertificateValidationCallback peerCheck, CancellationToken cancellationToken)
        {
            Handshakes++;
            if (!peerCheck(request, certificate, null, errors))
                throw new HttpRequestException("The SSL connection could not be established.", new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Authorization.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Responses.Dequeue(), Encoding.UTF8, "application/json") };
        }

        internal void LoginAnswers()
        {
            Responses.Enqueue("""{"data":{"login":{"token":"t0k","user":{"name":"op","fullName":"Op","language":"de-DE","autoLoggOffSec":600},"error":{"code":"0","description":"Success"}}}}""");
            Responses.Enqueue("""{"data":{"active":{"fields":[{"name":"name"},{"name":"path"},{"name":"instanceID"},{"name":"state"},{"name":"eventText"},{"name":"languages"}]},"logged":{"fields":[{"name":"name"},{"name":"instanceID"},{"name":"raiseTime"}]}}}""");
        }
    }

    /// <summary>The client side of one connection: the production peer check of the pin the client was created with.</summary>
    private sealed class Transport(Runtime runtime, RemoteCertificateValidationCallback peerCheck) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => runtime.Answer(request, peerCheck, cancellationToken);
    }

    private static X509Certificate2 Certificate(string host, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256).CreateSelfSigned(notBefore, notAfter);
    }

    private static string PinOf(X509Certificate2 certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));

    /// <summary>A login whose credential reference is well formed but held by no vault: for refusals before the vault.</summary>
    private static UnifiedLoginSelection Login(string endpoint, string user = "op", string vault = "default")
    {
        var binding = UnifiedRuntimeSessions.Binding(endpoint, Pin, user);
        return new(endpoint, Pin, user, vault, new("protected-credential-reference.v1", new string('a', 32), new string('b', 32), ProtectedCredentialGuard.BindingHash(binding), new string('c', 64)));
    }

    private static string? Code(CallToolResult result) =>
        result.StructuredContent is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
    private static JsonElement Body(CallToolResult result) => result.StructuredContent!.Value;

    internal static async Task Run(Action<bool, string> check, string vaults)
    {
        const string endpoint = "https://pc-b01/graphql/";
        string Id(char c) => new(c, 32);
        var none = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;

        // Scripted runtimes by host name. pc-local stands for a runtime on this PC (the endpoint rule forbids 127.0.0.1).
        var good = Certificate("pc-b01", now.AddDays(-1), now.AddDays(30));
        var expired = Certificate("pc-expired", now.AddDays(-30), now.AddDays(-1));
        var notYetValid = Certificate("pc-not-yet", now.AddDays(1), now.AddDays(30));
        var runtimes = new Dictionary<string, Runtime>(StringComparer.Ordinal)
        {
            ["pc-b01"] = new(good), ["192.0.2.1"] = new(good), ["pc-local"] = new(good), ["pc-b05"] = new(good), ["pc-refused"] = new(good),
            ["pc-other-cert"] = new(Certificate("pc-other-cert", now.AddDays(-1), now.AddDays(30))),
            ["pc-expired"] = new(expired), ["pc-not-yet"] = new(notYetValid),
            ["pc-wrong-name"] = new(good, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch),
        };
        int clients = 0;
        UnifiedGraphQlClient ClientFor(Uri uri, string pin)
        {
            clients++;
            var peerCheck = UnifiedGraphQlClient.PeerValidation(pin, out var verified);
            return new UnifiedGraphQlClient(uri, new Transport(runtimes[uri.Host], peerCheck), verified);
        }
        Directory.CreateDirectory(vaults);
        var vault = new CredentialVault(Path.Combine(vaults, "default"));
        UnifiedLoginSelection Stored(string host, string pin, string user = "op", string secret = "\"" + Secret + "\"")
        {
            string uri = "https://" + host + "/graphql/";
            var status = vault.Create(UnifiedRuntimeSessions.Binding(uri, pin, user), Encoding.UTF8.GetBytes(secret));
            return new(uri, pin, user, "default", status.Reference);
        }
        Task<(bool IsLocal, string[] Addresses)> Locality(Uri uri, CancellationToken cancellationToken) =>
            uri.Host == "pc-local" ? Task.FromResult((true, new[] { "127.0.0.1" })) : UnifiedRuntimeSessions.LocalEndpoint(uri, cancellationToken);
        using var sessions = new UnifiedRuntimeSessions(ClientFor, name => Path.Combine(vaults, name), Locality);
        var tools = new UnifiedRuntimeTools(sessions);

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
        var unheld = Login(endpoint);
        check(unheld == Login(endpoint) && unheld != Login(endpoint, "other") && unheld != unheld with { VaultName = Id('d') } && unheld != unheld with { Credential = unheld.Credential with { CredentialId = Id('e') } },
            "UnifiedLoginSelection compares endpoint, pin, user, vault and credential by value");
        check(unheld.Credential.BindingSha256 != Login("https://pc-b02/graphql/").Credential.BindingSha256 && unheld.Credential.BindingSha256 != Login(endpoint, "other").Credential.BindingSha256,
            "A credential binding is specific to its endpoint and user");

        // runtime_unified_connect: every refusal before the vault and before a client exists.
        check(Code(await tools.Connect("not-an-id", unheld, none)) == "InvalidRequest" && Code(await tools.Connect(Id('A'), unheld, none)) == "InvalidRequest", "runtime_unified_connect refuses an attemptId that is not 32 lowercase hex characters");
        check(Code(await tools.Connect(Id('1'), unheld with { Endpoint = "https://localhost/graphql/" }, none)) == "UnifiedEndpointInvalid", "runtime_unified_connect refuses localhost");
        check(Code(await tools.Connect(Id('1'), unheld with { PeerCertificateSha256 = "pin" }, none)) == "UnifiedCertificatePinInvalid", "runtime_unified_connect refuses a malformed pin");
        check(Code(await tools.Connect(Id('1'), unheld with { Credential = unheld.Credential with { SchemaVersion = "v0" } }, none)) == "CredentialInputInvalid", "runtime_unified_connect refuses a malformed credential reference");
        check(Code(await tools.Connect(Id('1'), unheld with { User = "other" }, none)) == "RuntimeCredentialContextMismatch" &&
            Code(await tools.Connect(Id('1'), unheld with { Endpoint = "https://pc-b02/graphql/" }, none)) == "RuntimeCredentialContextMismatch" &&
            Code(await tools.Connect(Id('1'), unheld with { PeerCertificateSha256 = new string('f', 64) }, none)) == "RuntimeCredentialContextMismatch",
            "runtime_unified_connect refuses a credential created for another user, endpoint or certificate");
        check(Code(await tools.Connect(Id('1'), unheld with { VaultName = "Default" }, none)) == "InvalidRequest" && Code(await tools.Connect(Id('1'), unheld with { VaultName = "../x" }, none)) == "InvalidRequest",
            "runtime_unified_connect refuses a vault name that is neither default nor an ID");
        check(clients == 0, "No client is created for a request refused before the vault");
        string? unknownVault = Code(await tools.Connect(Id('1'), unheld with { VaultName = Id('d') }, none));
        string? unheldCode = Code(await tools.Connect(Id('1'), unheld, none));
        check(unknownVault == "CredentialUnavailable" && unheldCode is not null && runtimes["pc-b01"].Handshakes == 0 && Code(tools.Status(Id('1'))) == "RuntimeSessionNotFound",
            "A vault that does not exist or a reference the vault does not hold is refused before anything reaches the runtime (" + unheldCode + ")");

        // runtime_unified_connect, refused at the peer: the password never leaves for a server the pin does not name.
        async Task RefusedAtPeer(string host, string pin, string label)
        {
            var result = await tools.Connect(Id('2'), Stored(host, pin), none);
            var runtime = runtimes[host];
            check(Code(result) == "UnifiedTlsUnverified" && runtime.Handshakes == 1 && runtime.Bodies.Count == 0 && Code(tools.Status(Id('2'))) == "RuntimeSessionNotFound" &&
                !Body(result).GetRawText().Contains(Secret), label);
        }
        await RefusedAtPeer("pc-other-cert", PinOf(good), "A server presenting another certificate than the pinned one is refused and receives no password");
        await RefusedAtPeer("pc-expired", PinOf(expired), "An expired certificate is refused even when its pin matches");
        await RefusedAtPeer("pc-not-yet", PinOf(notYetValid), "A certificate that is not yet valid is refused even when its pin matches");
        await RefusedAtPeer("pc-wrong-name", PinOf(good), "A certificate for another host name is refused even when its pin matches");

        // runtime_unified_connect, refused by UMC: the code is reported, the secret and the server's text are not, nothing is kept.
        var refusedRuntime = runtimes["pc-refused"];
        refusedRuntime.Responses.Enqueue("""{"data":{"login":{"token":null,"error":{"code":"101","description":"Incorrect credentials provided"}}}}""");
        var denied = await tools.Connect(Id('2'), Stored("pc-refused", PinOf(good)), none);
        check(Code(denied) == "UnifiedAuthenticationDenied" && Body(denied).GetRawText().Contains("101") && !Body(denied).GetRawText().Contains(Secret) &&
            refusedRuntime.Bodies.Count == 1 && Code(tools.Status(Id('2'))) == "RuntimeSessionNotFound", "A login UMC refuses reports its code and keeps no session");
        refusedRuntime.Responses.Enqueue("""{"errors":[{"message":"Variable password got invalid value synthetic-only"}]}""");
        var echoed = await tools.Connect(Id('2'), Stored("pc-refused", PinOf(good)), none);
        check(Code(echoed) == "UnifiedLoginFailed" && !Body(echoed).GetRawText().Contains(Secret) && Code(tools.Status(Id('2'))) == "RuntimeSessionNotFound",
            "A login error that echoes the password is reported without the server's text");
        var malformedSecret = await tools.Connect(Id('2'), Stored("pc-refused", PinOf(good), secret: Secret), none);
        // Unparseable JSON fails inside the vault callback, which reports any non-coded failure as UnifiedLoginFailed.
        check(Code(malformedSecret) == "UnifiedLoginFailed" && !Body(malformedSecret).GetRawText().Contains(Secret) && refusedRuntime.Bodies.Count == 2,
            "A stored secret that is not JSON is refused before it is sent");
        var numberSecret = await tools.Connect(Id('2'), Stored("pc-refused", PinOf(good), secret: "42"), none);
        check(Code(numberSecret) == "RuntimeCredentialFormatInvalid" && refusedRuntime.Bodies.Count == 2, "A stored secret that is JSON but not a string is refused before it is sent");

        // runtime_unified_connect: pinned peer, login with the protected credential, UMC language and alarm fields.
        var b01 = runtimes["pc-b01"];
        b01.LoginAnswers();
        var login = Stored("pc-b01", PinOf(good));
        var connected = await tools.Connect(Id('1'), login, none);
        var status = Body(connected);
        check(connected.IsError != true && status.GetProperty("schemaVersion").GetString() == "runtime-unified-session.v1" && status.GetProperty("attemptId").GetString() == Id('1') &&
            status.GetProperty("endpoint").GetString() == endpoint && status.GetProperty("user").GetString() == "op" && status.GetProperty("state").GetString() == "connected" &&
            status.GetProperty("peerVerified").GetBoolean() && status.GetProperty("userLanguage").GetString() == "de-DE" && status.GetProperty("autoLogoffSeconds").GetInt32() == 600 &&
            status.GetProperty("activeAlarmFields").GetInt32() == 6 && status.GetProperty("loggedAlarmFields").GetInt32() == 3 && !status.GetProperty("secretReturned").GetBoolean() &&
            DateTimeOffset.TryParse(status.GetProperty("connectedAtUtc").GetString(), out _), "runtime_unified_connect logs in on the pinned peer and reports the UMC language, idle logoff and alarm fields");
        check(b01.Handshakes == 2 && b01.Bodies.Count == 2 && b01.Bodies[0].Contains("\"password\":\"" + Secret + "\"") && b01.Bodies[0].Contains("\"username\":\"op\"") && b01.Authorization[0] is null &&
            !b01.Bodies[1].Contains(Secret) && b01.Authorization[1] == "Bearer t0k", "The vault's password goes only into the login request; the schema read carries the bearer token");
        check(!status.GetRawText().Contains("t0k") && !status.GetRawText().Contains(Secret), "runtime_unified_connect returns no token or password");
        check(Body(tools.Status(Id('1'))).GetRawText() == status.GetRawText() && b01.Bodies.Count == 2, "runtime_unified_status repeats the session without contacting the server");
        b01.Bodies.Clear(); b01.Authorization.Clear();
        var again = await tools.Connect(Id('1'), login, none);
        check(again.IsError != true && Body(again).GetProperty("connectedAtUtc").GetString() == status.GetProperty("connectedAtUtc").GetString() && b01.Bodies.Count == 0,
            "Repeating runtime_unified_connect with the same attemptId and login returns the open session without a second login");
        check(Code(await tools.Connect(Id('1'), Login(endpoint, "other"), none)) == "RuntimeAttemptContextMismatch" && Code(await tools.Connect(Id('1'), login with { VaultName = Id('d') }, none)) == "RuntimeAttemptContextMismatch" &&
            b01.Bodies.Count == 0, "An attemptId cannot be reused for another login");

        // runtime_unified_alarms
        b01.Responses.Enqueue("""{"data":{"activeAlarms":[{"name":"Alarm_Tag01","path":"HMI_RT_1::Tag01:Alarm_Tag01","instanceID":3,"eventText":["Übertemperatur"],"languages":["de-DE"]},{"name":"B","path":"HMI_RT_1::B:B","instanceID":1}]}}""");
        var alarms = Body(await tools.Alarms(Id('1'), none));
        check(alarms.GetProperty("kind").GetString() == "active" && alarms.GetProperty("languageSource").GetString() == "umcUserLanguage" && alarms.GetProperty("languages")[0].GetString() == "de-DE" &&
            alarms.GetProperty("returned").GetInt32() == 2 && !alarms.GetProperty("truncated").GetBoolean() &&
            alarms.GetProperty("alarms")[0].GetProperty("acknowledge").GetProperty("path").GetString() == "HMI_RT_1::Tag01:Alarm_Tag01", "runtime_unified_alarms defaults to active alarms in the UMC user's language");
        using (var sent = JsonDocument.Parse(b01.Bodies.Single()))
            check(sent.RootElement.GetProperty("query").GetString()!.Contains("activeAlarms(") && sent.RootElement.GetProperty("variables").GetProperty("languages")[0].GetString() == "de-DE" &&
                b01.Authorization.Single() == "Bearer t0k", "The active alarm request names its languages explicitly and carries the session's token");
        b01.Bodies.Clear();
        b01.Responses.Enqueue("""{"data":{"loggedAlarms":[{"name":"A","instanceID":1},{"name":"B","instanceID":2}]}}""");
        var logged = Body(await tools.Alarms(Id('1'), none, "logged", ["en-US", "de-DE"], maxItems: 1, startTime: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
        check(logged.GetProperty("languageSource").GetString() == "request" && logged.GetProperty("returned").GetInt32() == 1 && logged.GetProperty("totalReturnedByServer").GetInt32() == 2 &&
            logged.GetProperty("truncated").GetBoolean() && logged.GetProperty("note").GetString()!.Contains("cannot be acknowledged"), "A full page of logged alarms reports truncation and that logged alarms cannot be acknowledged");
        check(b01.Bodies.Single().Contains("loggedAlarms(") && b01.Bodies.Single().Contains("en-US"), "The logged alarm request carries the requested languages");
        b01.Bodies.Clear();
        check(Code(await tools.Alarms(Id('1'), none, maxItems: 0)) == "InvalidRequest" && Code(await tools.Alarms(Id('1'), none, maxItems: UnifiedAlarmRules.MaxAlarms + 1)) == "InvalidRequest" &&
            Code(await tools.Alarms(Id('1'), none, "history")) == "InvalidRequest" && Code(await tools.Alarms(Id('1'), none, startTime: DateTimeOffset.UtcNow)) == "InvalidRequest" &&
            Code(await tools.Alarms(Id('1'), none, endTime: DateTimeOffset.UtcNow)) == "InvalidRequest" && Code(await tools.Alarms(Id('1'), none, languages: ["de-de"])) == "UnifiedLanguageInvalid" &&
            Code(await tools.Alarms(Id('2'), none)) == "RuntimeSessionNotFound" && b01.Bodies.Count == 0, "runtime_unified_alarms refuses bad bounds, kinds, time ranges, languages and unknown sessions without a request");

        // runtime_unified_alarm_acknowledge: the acknowledgement and the route are checked before anything is sent.
        UnifiedAlarmIdentifier[] one = [new("HMI_RT_1::Tag01:Alarm_Tag01", 3)];
        check(Code(await tools.Acknowledge(Id('1'), one, false, none)) == "WriteAcknowledgementRequired" && Code(await tools.Acknowledge(Id('2'), one, false, none)) == "WriteAcknowledgementRequired" &&
            b01.Bodies.Count == 0, "runtime_unified_alarm_acknowledge without acknowledgeWrite is refused before the session is looked up");
        check(Code(await tools.Acknowledge(Id('2'), one, true, none)) == "RuntimeSessionNotFound" && Code(await tools.Acknowledge("x", one, true, none)) == "InvalidRequest", "Acknowledging on an unknown session is refused");
        // 192.0.2.1 (TEST-NET-1) is an address no interface of this PC has; the production DNS/interface check decides.
        var remote = runtimes["192.0.2.1"];
        remote.LoginAnswers();
        check((await tools.Connect(Id('3'), Stored("192.0.2.1", PinOf(good)), none)).IsError != true, "runtime_unified_connect reaches a runtime by its address");
        remote.Bodies.Clear();
        var refused = await tools.Acknowledge(Id('3'), one, true, none);
        check(refused.IsError == true && Code(refused) == "UnifiedWriteRouteRefused" && Body(refused).GetProperty("error").GetProperty("details").GetProperty("host").GetString() == "192.0.2.1" &&
            Body(refused).GetProperty("error").GetProperty("details").GetProperty("addresses")[0].GetString() == "192.0.2.1" && remote.Bodies.Count == 0,
            "runtime_unified_alarm_acknowledge refuses a runtime on another machine and sends nothing to it");
        remote.Responses.Enqueue("""{"data":{"activeAlarms":[]}}""");
        check((await tools.Alarms(Id('3'), none)).IsError != true && remote.Bodies.Count == 1, "Reading alarms from a remote runtime stays allowed");
        check((await UnifiedRuntimeSessions.LocalEndpoint(new Uri("https://127.0.0.1/graphql/"), none)).IsLocal && (await UnifiedRuntimeSessions.LocalEndpoint(new Uri("https://[::1]/graphql/"), none)).IsLocal &&
            !(await UnifiedRuntimeSessions.LocalEndpoint(new Uri("https://192.0.2.1/graphql/"), none)).IsLocal, "Only loopback and this PC's own addresses are local");

        // A runtime on this PC (pc-local resolves to this PC through the test's locality seam).
        var local = runtimes["pc-local"];
        local.LoginAnswers();
        check((await tools.Connect(Id('4'), Stored("pc-local", PinOf(good)), none)).IsError != true, "runtime_unified_connect logs in to the runtime on this PC");
        local.Bodies.Clear();
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

        // Capacity: four sessions; the fifth is refused before the vault is opened or a client is created.
        runtimes["pc-b05"].LoginAnswers();
        check((await tools.Connect(Id('5'), Stored("pc-b05", PinOf(good)), none)).IsError != true, "A fourth Unified session connects");
        int before = clients;
        check(Code(await tools.Connect(Id('6'), Stored("pc-b05", PinOf(good)), none)) == "RuntimeSessionCapacity" && clients == before, "A fifth Unified session is refused");

        // runtime_unified_disconnect
        var closed = Body(await tools.Disconnect(Id('1'), none));
        check(closed.GetProperty("closed").GetBoolean() && closed.GetProperty("attemptId").GetString() == Id('1') && Code(tools.Status(Id('1'))) == "RuntimeSessionNotFound" &&
            Code(await tools.Alarms(Id('1'), none)) == "RuntimeSessionNotFound" && b01.Bodies.Count == 0, "runtime_unified_disconnect drops the session without contacting the server");
        check(!Body(await tools.Disconnect(Id('1'), none)).GetProperty("closed").GetBoolean() && !Body(await tools.Disconnect(Id('9'), none)).GetProperty("closed").GetBoolean(),
            "Disconnecting a closed or unknown session reports closed:false");
        b01.LoginAnswers();
        check(Body(await tools.Connect(Id('1'), login, none)).GetProperty("connectedAtUtc").GetString() != status.GetProperty("connectedAtUtc").GetString() && b01.Bodies.Count == 2,
            "After a disconnect the same attemptId logs in again");
        check(Body(tools.Status(Id('3'))).GetProperty("state").GetString() == "connected" && Body(tools.Status(Id('4'))).GetProperty("state").GetString() == "connected", "Disconnecting one session leaves the others open");
        sessions.Dispose();
        check(Code(tools.Status(Id('3'))) == "RuntimeSessionNotFound" && Code(tools.Status(Id('4'))) == "RuntimeSessionNotFound" && Code(tools.Status(Id('5'))) == "RuntimeSessionNotFound", "Disposing the store drops every session");
    }
}
