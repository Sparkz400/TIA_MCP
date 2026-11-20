using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Host;

// Host-side safety write gate: safetyAcknowledged is a client flag, so only --enable-safety-changes admits a safety change_prepare.
internal static class SafetyChangeChecks
{
    private sealed class CountingBridge : IBridgeClient
    {
        public int Calls;
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "prepared" }));
        }
    }

    internal static async Task Run(Action<bool, string> check)
    {
        HostOptions Options(params string[] args) => HostOptions.Parse(args);
        async Task<(string? Code, int Calls)> Prepare(HostOptions options, string kind, string payload)
        {
            using var body = JsonDocument.Parse(payload);
            var bridge = new CountingBridge();
            var result = await new TiaTools(bridge, options).ChangePrepare(kind, body.RootElement, default);
            var content = result.StructuredContent;
            string? code = content is { } json && json.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
            return (code, bridge.Calls);
        }
        var off = Options(); var on = Options("--enable-safety-changes");
        foreach (var kind in new[] { "safety_import_xml", "safety_fparameter_set", "safety_drive_tags" })
        {
            var refused = await Prepare(off, kind, """{"safetyAcknowledged":true}""");
            check(refused == ("SafetyChangesDisabled", 0), kind + " is refused without the launch option and never reaches the worker (" + refused + ")");
            var admitted = await Prepare(on, kind, """{"safetyAcknowledged":true}""");
            check(admitted.Code != "SafetyChangesDisabled" && admitted.Calls == 1, kind + " is prepared with --enable-safety-changes");
        }
        var rename = await Prepare(off, "tag_set", """{"memberName":"A","safetyAcknowledged":true}""");
        check(rename == ("SafetyChangesDisabled", 0), "A safety tag rename is refused without the launch option");
        var plain = await Prepare(off, "tag_set", """{"memberName":"A"}""");
        check(plain.Calls == 1 && plain.Code != "SafetyChangesDisabled", "An ordinary tag_set is not a safety write");
    }
}
