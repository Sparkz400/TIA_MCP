using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.OpcUa.Runtime.Tests;

/// <summary>Session liveness, close outside the store gate, password decoding and a stale temporary record.</summary>
internal static class StoreLifecycleChecks
{
    private static string Id() => Guid.NewGuid().ToString("N");
    internal static async Task Run(FixtureServer server, FixtureServer slowTimeout, FixtureServer slowClose, string output, Action<bool, string> check)
    {
        // The server's revised session timeout is recorded, never assumed.
        await using (var store = new OpcUaRuntimeSessionStore(Path.Combine(output, "lifecycle-timeout")))
        {
            string id = Id(); var connected = await store.Connect(id, server.Selection);
            check(connected.SessionTimeoutMs == 60_000 && (await store.Status(id)).SessionTimeoutMs == 60_000, "The server's revised session timeout (60 s of the 600 s requested) is recorded in the status");
            await store.Disconnect(id);
        }
        // A session unused past the server's timeout stops holding its slot and its endpoint.
        slowTimeout.SessionTimeoutOverrideMs = 1000;
        await using (var store = new OpcUaRuntimeSessionStore(Path.Combine(output, "lifecycle-expiry")))
        {
            string lost = Id(); var connected = await store.Connect(lost, slowTimeout.Selection);
            check(connected.State == "connected" && connected.SessionTimeoutMs == 1000, "A server-revised one-second session timeout is recorded");
            var wait = Stopwatch.StartNew(); string state = "connected";
            while (state == "connected" && wait.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(250); state = (await store.Status(lost)).State; }
            var retired = await store.Status(lost);
            check(retired.State == "closed" && retired.ErrorCode is null or "UaSessionTimedOut" && slowTimeout.SessionCount == 0, "A session unused past its revised timeout is retired as closed (" + retired.ErrorCode + ")");
            string fresh = Id();
            check((await store.Connect(fresh, slowTimeout.Selection)).State == "connected", "A fresh attempt to the same endpoint connects after the lost session retired");
            await store.Disconnect(fresh);
        }
        slowTimeout.SessionTimeoutOverrideMs = null;
        // A slow remote close of one session does not hold the store gate for the others.
        slowClose.CloseDelayMs = 3000;
        await using (var store = new OpcUaRuntimeSessionStore(Path.Combine(output, "lifecycle-close")))
        {
            string slow = Id(), quick = Id();
            await store.Connect(slow, slowClose.Selection); await store.Connect(quick, server.Selection);
            var closing = store.Disconnect(slow);
            await Task.Delay(500);
            var clock = Stopwatch.StartNew();
            var status = await store.Status(quick); var read = await store.Read(quick, [FixtureServer.Node("bool")]);
            check(!closing.IsCompleted && clock.ElapsedMilliseconds < 1500 && status.State == "connected" && read.Items[0].Outcome == "value",
                "Another session's status and read complete while a remote close is in flight (" + clock.ElapsedMilliseconds + " ms)");
            check((await closing).State == "closed" && (await store.Status(slow)).State == "closed", "The slow close publishes its outcome");
            await store.Disconnect(quick);
        }
        slowClose.CloseDelayMs = 0;
        // The password is unescaped without a managed string; anything that is not one JSON string is refused.
        byte[] decoded = OpcUaRuntimeClient.DecodePassword("\"p\u00e4ss \\\"x\\\" \ud83d\ude00\""u8);
        check(decoded.SequenceEqual(System.Text.Encoding.UTF8.GetBytes("p\u00e4ss \"x\" \U0001F600")), "A JSON password string decodes to exact UTF-8 bytes");
        foreach (string bad in new[] { "123", "\"a\" \"b\"", "\"unterminated", "[\"a\"]", "\"lone \\ud800\"", "" })
        {
            string? code = null;
            try { OpcUaRuntimeClient.DecodePassword(System.Text.Encoding.UTF8.GetBytes(bad)); } catch (BridgeException e) { code = e.Code; }
            check(code == "UaCredentialInvalid", "A non-string or malformed password JSON is refused: " + bad);
        }
    }
}
