using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Security;
using TiaMcp.Host;

// Production web-session record retention and RUN/STOP outcomes; no endpoint is contacted and no vault is opened.
internal static class RuntimeSessionChecks
{
    private static string Id() => Guid.NewGuid().ToString("N");

    internal static async Task Run(string output, Action<bool, string> check)
    {
        async Task Refused(Func<Task> action, string code, string name)
        {
            string? actual = null;
            try { await action(); } catch (BridgeException e) { actual = e.Code; }
            check(actual == code, name + " (" + actual + ")");
        }

        // Once the RUN/STOP request may have gone out, a failed or cancelled readback is an unknown, dispatched outcome.
        var context = WebRuntimeProtocol.Context("https://plc.example", Id(), Id(), new('a', 64));
        WebRuntimeDiagnosticResult Mode(string value) => new(context, "operatingMode", "Plc.ReadOperatingMode", Id(), "read",
            JsonSerializer.SerializeToElement(value), null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        JsonElement Outcome(Func<WebRuntimeDiagnosticResult> observe, CancellationToken token) => JsonSerializer.SerializeToElement(
            PhysicalRuntimeSessions.RequestMode(Id(), "stop", "run", Mode("stop"), () => ("accepted", null), observe, token), BridgeProtocol.Json);
        var lost = Outcome(() => throw new BridgeException("RuntimeTimeout", "Synthetic lost readback."), CancellationToken.None);
        check(lost.GetProperty("state").GetString() == "unknown" && lost.GetProperty("dispatched").GetBoolean() &&
            lost.GetProperty("dispatch").GetString() == "accepted" && lost.GetProperty("errorCode").GetString() == "RuntimeTimeout",
            "A failed mode readback after an accepted request is unknown and dispatched");
        using (var cancel = new CancellationTokenSource())
        {
            int reads = 0;
            var cancelled = Outcome(() => { if (++reads == 1) cancel.Cancel(); return Mode("stop"); }, cancel.Token);
            check(cancelled.GetProperty("state").GetString() == "unknown" && cancelled.GetProperty("dispatched").GetBoolean() &&
                cancelled.GetProperty("errorCode").GetString() == "RuntimeCancelled" && reads == 1, "Cancellation between mode readbacks is unknown and dispatched");
        }
        var changed = Outcome(() => Mode("run"), CancellationToken.None);
        check(changed.GetProperty("state").GetString() == "changed" && changed.GetProperty("readbackMatches").GetBoolean(), "An observed mode change is still reported as changed");

        // Retention: the oldest resolved records make room for a new attempt; unresolved records are never removed.
        string pin = new('a', 64);
        RuntimeLoginSelection Login(string endpoint) => new(endpoint, pin, "reader", "local", "default", new("protected-credential-reference.v1",
            new('a', 32), new('b', 32), ProtectedCredentialGuard.BindingHash(PhysicalRuntimeSessions.Binding(endpoint, pin, "reader", "local")), new('c', 64)));
        string Record(string root, string id) => Path.Combine(root, "runtime-web", id + ".json");
        string[] Seed(string root, Func<int, (string Endpoint, string State)> pick)
        {
            var ids = Enumerable.Range(0, 1024).Select(_ => Id()).ToArray(); var oldest = DateTime.UtcNow.AddHours(-1);
            for (int i = 0; i < ids.Length; i++)
            {
                var (endpoint, state) = pick(i);
                var status = new RuntimeSessionStatus(ids[i], Id(), WebRuntimeProtocol.Context(endpoint, ids[i], Id(), pin), Login(endpoint), state, null, false, [], "permissionUnknown");
                File.WriteAllBytes(Record(root, ids[i]), JsonSerializer.SerializeToUtf8Bytes(status, CredentialVault.Json));
                File.SetLastWriteTimeUtc(Record(root, ids[i]), oldest.AddSeconds(i));
            }
            return ids;
        }
        string retention = Path.Combine(output, "retention");
        await using (var sessions = new PhysicalRuntimeSessions(new HostOptions("", retention, null, 5, false, "", null)))
        {
            var ids = Seed(retention, i => i == 0 ? ("https://other.example", "remoteSessionUnknown") : ("https://plc.example", "closed"));
            await Refused(() => sessions.Connect(Id(), Login("https://other.example"), default), "RuntimeEndpointSessionUnresolved",
                "A full root admits a new attempt past capacity, and the unresolved endpoint still refuses it");
            check(File.Exists(Record(retention, ids[0])) && !File.Exists(Record(retention, ids[1])) && File.Exists(Record(retention, ids[2])) &&
                Directory.EnumerateFiles(Path.Combine(retention, "runtime-web"), "*.json").Count() == 1023, "Only the oldest resolved record makes room; an older unresolved one stays");
            var repeated = await sessions.Connect(ids[^1], Login("https://plc.example"), default);
            check(repeated.State == "closed" && repeated.AttemptId == ids[^1], "Repeating a retained attempt ID observes its outcome without login");
        }
        string full = Path.Combine(output, "capacity");
        await using (var sessions = new PhysicalRuntimeSessions(new HostOptions("", full, null, 5, false, "", null)))
        {
            Seed(full, i => ($"https://plc{i}.example", "remoteSessionUnknown"));
            await Refused(() => sessions.Connect(Id(), Login("https://plc.example"), default), "RuntimeSessionCapacity", "1,024 unresolved records still refuse a new attempt");
            check(Directory.EnumerateFiles(Path.Combine(full, "runtime-web"), "*.json").Count() == 1024, "A capacity refusal removes no record");
        }
    }
}
