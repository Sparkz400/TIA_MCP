using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.UnifiedRuntime;

/// <summary>WinCC Unified GraphQL alarm rules and client against a scripted server (no TLS, no runtime).</summary>
internal static class UnifiedRuntimeChecks
{
    internal static async Task Run()
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; }
        void Refused(Action action, string code)
        {
            try { action(); } catch (BridgeException error) when (error.Code == code) { passed++; return; }
            throw new Exception("Expected " + code);
        }

        // Endpoint: https://host/graphql/ only; the manual forbids localhost and 127.0.0.1.
        Check(UnifiedAlarmRules.Endpoint("https://PC-B01/graphql").ToString() == "https://pc-b01/graphql/", "Endpoint is canonical");
        foreach (string bad in new[] { "http://pc-b01/graphql/", "https://localhost/graphql/", "https://127.0.0.1/graphql/", "https://pc-b01/other/", "https://u:p@pc-b01/graphql/", "https://pc-b01/graphql/?x=1" })
            Refused(() => UnifiedAlarmRules.Endpoint(bad), "UnifiedEndpointInvalid");

        // Languages: the user's UMC language, never the server's en-US default; exact ISO case.
        Check(UnifiedAlarmRules.Languages(null, "de-DE").SequenceEqual(new[] { "de-DE" }), "The UMC user language is the default");
        Check(UnifiedAlarmRules.Languages(["en-US", "de-DE"], "de-DE").SequenceEqual(new[] { "en-US", "de-DE" }), "Requested languages win");
        Refused(() => UnifiedAlarmRules.Languages(null, null), "UnifiedLanguageRequired");
        Refused(() => UnifiedAlarmRules.Languages(["de-de"], null), "UnifiedLanguageInvalid");
        Refused(() => UnifiedAlarmRules.Languages(["de-DE", "de-DE"], null), "UnifiedLanguageInvalid");

        string[] schema = ["name", "path", "instanceID", "state", "eventText", "priority", "raiseTime", "languages", "error"];
        var active = UnifiedAlarmRules.ActiveRequest(schema, ["de-DE"], null, null);
        string query = active["query"]!.GetValue<string>();
        Check(query.Contains("activeAlarms(") && query.Contains("path") && !query.Contains("alarmClassName"), "Only fields the server's schema has are selected");
        Check(active["variables"]!["filterLanguage"] is null && active["variables"]!["languages"]![0]!.GetValue<string>() == "de-DE", "No filter sends no filterLanguage; languages are explicit");
        var filtered = UnifiedAlarmRules.ActiveRequest(schema, ["de-DE"], "path LIKE '*::Tag01:*'", null);
        Check(filtered["variables"]!["filterLanguage"]!.GetValue<string>() == "de-DE", "A filter uses the first language, not en-US");
        Refused(() => UnifiedAlarmRules.ActiveRequest(["state"], ["de-DE"], null, null), "UnifiedSchemaUnexpected");

        // Acknowledge by the fully qualified path; a plain name is refused with the route to the path.
        var ack = UnifiedAlarmRules.AcknowledgeRequest(schema, [new("HMI_RT_1::Tag01:Alarm_Tag01", 5), new("HMI_RT_1::Motor.Speed:High", null)]);
        var input = ack["variables"]!["input"]!.AsArray();
        Check(input[0]!["name"]!.GetValue<string>() == "HMI_RT_1::Tag01:Alarm_Tag01" && input[0]!["instanceID"]!.GetValue<int>() == 5 &&
            input[1]!["instanceID"]!.GetValue<int>() == 0 && ack["query"]!.GetValue<string>().Contains("error{code description}"), "Acknowledge sends path and instanceID and asks for item errors");
        Refused(() => UnifiedAlarmRules.AcknowledgeRequest(schema, [new("Alarm_Tag01", null)]), "UnifiedAlarmPathRequired");
        Refused(() => UnifiedAlarmRules.AcknowledgeRequest(schema, [new("HMI_RT_1::Tag01:", null)]), "UnifiedAlarmPathRequired");
        Refused(() => UnifiedAlarmRules.AcknowledgeRequest(schema, [new("S::T:A", 1), new("S::T:A", 1)]), "InvalidRequest");
        Refused(() => UnifiedAlarmRules.AcknowledgeRequest(schema, []), "InvalidRequest");

        // Rows key translatable texts by language and carry the acknowledge input; logged rows have none.
        using var rows = JsonDocument.Parse("""[{"name":"Alarm_Tag01","path":"HMI_RT_1::Tag01:Alarm_Tag01","instanceID":3,"eventText":["Übertemperatur","Overtemperature"],"languages":["de-DE","en-US"]},{"name":"B","instanceID":1}]""");
        var shaped = UnifiedAlarmRules.Rows(rows.RootElement, ["de-DE"], active: true, maxItems: 1, out int total);
        Check(total == 2 && shaped.Count == 1, "Rows are bounded and the server's count is kept");
        Check(shaped[0]!["eventText"]!["de-DE"]!.GetValue<string>() == "Übertemperatur" && shaped[0]!["eventText"]!["en-US"]!.GetValue<string>() == "Overtemperature",
            "Texts are keyed by the languages the server returned");
        Check(shaped[0]!["acknowledge"]!["path"]!.GetValue<string>() == "HMI_RT_1::Tag01:Alarm_Tag01" && shaped[0]!["acknowledge"]!["instanceID"]!.GetValue<int>() == 3,
            "An active row names the exact acknowledge input");

        // Client: login sends the password only in the login request and keeps no server text from a failed login.
        var server = new ScriptedServer();
        server.Responses.Enqueue("""{"data":{"login":{"token":"t0k","user":{"name":"op","fullName":"Op","language":"de-DE","autoLoggOffSec":600},"error":{"code":"0","description":"Success"}}}}""");
        server.Responses.Enqueue("""{"data":{"active":{"fields":[{"name":"name"},{"name":"path"},{"name":"instanceID"},{"name":"eventText"},{"name":"languages"}]},"logged":{"fields":[{"name":"name"},{"name":"instanceID"}]}}}""");
        var client = new UnifiedGraphQlClient(new Uri("https://pc-b01/graphql/"), server);
        await client.LoginAsync("op", Encoding.UTF8.GetBytes("\"s3cret\""), CancellationToken.None);
        Check(client.UserLanguage == "de-DE" && client.AutoLogoffSeconds == 600 && client.ActiveFields.Contains("path") && client.LoggedFields.Length == 2,
            "Login reads the UMC language and the alarm fields");
        Check(server.Bodies[0].Contains("s3cret") && server.Authorization[0] is null && server.Bodies.Skip(1).All(b => !b.Contains("s3cret")) &&
            server.Authorization[1] == "Bearer t0k", "Only the login request carries the password; later requests carry the bearer token");
        server.Responses.Enqueue("""{"data":{"activeAlarms":[{"name":"Alarm_Tag01","path":"HMI_RT_1::Tag01:Alarm_Tag01","instanceID":1}]}}""");
        var data = await client.QueryAsync(UnifiedAlarmRules.ActiveRequest(client.ActiveFields, ["de-DE"], null, null), CancellationToken.None);
        Check(data.GetProperty("activeAlarms").GetArrayLength() == 1, "An active alarm query returns its data");
        server.Responses.Enqueue("""{"errors":[{"message":"At least one of the requested languages is invalid.","extensions":{"code":"INTERNAL_SERVER_ERROR"}}],"data":{"activeAlarms":null}}""");
        try { await client.QueryAsync(UnifiedAlarmRules.ActiveRequest(client.ActiveFields, ["en-US"], null, null), CancellationToken.None); throw new Exception("Expected UnifiedQueryFailed"); }
        catch (BridgeOperationException error) when (error.Code == "UnifiedQueryFailed") { Check(error.Message.Contains("languages is invalid"), "A top-level GraphQL error reaches the caller with its message"); }
        server.Status = HttpStatusCode.Unauthorized; server.Responses.Enqueue("{}");
        try { await client.QueryAsync(UnifiedAlarmRules.ActiveRequest(client.ActiveFields, ["de-DE"], null, null), CancellationToken.None); throw new Exception("Expected UnifiedSessionExpired"); }
        catch (BridgeException error) when (error.Code == "UnifiedSessionExpired") { passed++; }

        var refused = new ScriptedServer();
        refused.Responses.Enqueue("""{"data":{"login":{"token":null,"error":{"code":"101","description":"Incorrect credentials provided"}}}}""");
        var denied = new UnifiedGraphQlClient(new Uri("https://pc-b01/graphql/"), refused);
        try { await denied.LoginAsync("op", Encoding.UTF8.GetBytes("\"wrong\""), CancellationToken.None); throw new Exception("Expected UnifiedAuthenticationDenied"); }
        catch (BridgeException error) when (error.Code == "UnifiedAuthenticationDenied") { Check(!error.Message.Contains("wrong"), "A refused login names the code, never the secret"); }
        var echo = new ScriptedServer();
        echo.Responses.Enqueue("""{"errors":[{"message":"Variable password got invalid value wrong"}]}""");
        try { await new UnifiedGraphQlClient(new Uri("https://pc-b01/graphql/"), echo).LoginAsync("op", Encoding.UTF8.GetBytes("\"wrong\""), CancellationToken.None); throw new Exception("Expected UnifiedLoginFailed"); }
        catch (BridgeException error) when (error.Code == "UnifiedLoginFailed") { Check(!error.Message.Contains("wrong"), "A login error never returns the server's text, which can echo the password"); }
        try { await denied.LoginAsync("op", Encoding.UTF8.GetBytes("\"x\""), CancellationToken.None); throw new Exception("Expected one login"); }
        catch (BridgeException error) when (error.Code == "UnifiedLoginAlreadyAttempted") { passed++; }

        Console.WriteLine($"PASS Unified runtime alarms: {passed} checks; scripted GraphQL server, no runtime or TLS.");
    }

    private sealed class ScriptedServer : HttpMessageHandler
    {
        internal Queue<string> Responses { get; } = new();
        internal List<string> Bodies { get; } = [];
        internal List<string?> Authorization { get; } = [];
        internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Authorization.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(Status) { Content = new StringContent(Responses.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }
}
