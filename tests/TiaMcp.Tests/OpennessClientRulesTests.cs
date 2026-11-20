using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class OpennessClientRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private static OpennessClientRules.SessionView Session(int id, bool active, int attachedSecondsAgo, bool? running = true, string? name = null) => new()
    {
        ProcessId = id, IsActive = active, AttachTimeUtc = Now.AddSeconds(-attachedSecondsAgo), UtilizationSeconds = 12.34,
        AccessLevel = "Published", TrustAuthority = "Signed", Version = "21.0", ProcessRunning = running, ProcessName = name,
        ProcessPath = name == null ? null : @"C:\Tools\" + name + ".exe",
    };

    // This worker's own session is never a competitor; the others are listed oldest first and only those in a call are busy.
    internal static Task OtherClientsAndBusyClients()
    {
        var sessions = new[] { Session(30, false, 10), Session(40, true, 600, running: false, name: "StaleHelper"), Session(99, true, 5), null! };
        var others = OpennessClientRules.Others(sessions, ownProcessId: 99);
        Check.Equal("40,30", string.Join(",", others.Select(s => s.ProcessId)));
        var busy = OpennessClientRules.Busy(others);
        Check.Equal("40", string.Join(",", busy.Select(s => s.ProcessId)));
        Check.Equal(0, OpennessClientRules.Others(null!, 99).Count);

        var row = OpennessClientRules.Row(others[0], Now);
        Check.Equal(600.0, (double)row["attachedForSeconds"]!);
        Check.Equal(false, (bool)row["processRunning"]!);
        Check.Equal(true, (bool)row["isActive"]!);
        Check.Equal("StaleHelper", (string?)row["processName"]);
        Check.Equal(12.3, (double)row["utilizationSeconds"]!);
        return Task.CompletedTask;
    }

    // The refusal names the busy client and an exited one, and says that nothing is ever disconnected.
    internal static Task BusyRefusalNamesTheClientAndNeverEvicts()
    {
        var others = OpennessClientRules.Others(new[] { Session(30, false, 10), Session(40, true, 600, running: false, name: "StaleHelper") }, 99);
        var busy = OpennessClientRules.Busy(others);
        var details = OpennessClientRules.BusyRefusal(1234, busy, others, 15, Now);
        Check.Equal(1234, (int)details["processId"]!);
        Check.Equal(15, (int)details["waitedSeconds"]!);
        Check.Equal(1, ((JArray)details["busyClients"]!).Count);
        Check.Equal(2, ((JArray)details["attachedSessions"]!).Count);
        Check.Equal("40", string.Join(",", ((JArray)details["exitedClientProcessIds"]!).Select(t => (int)t)));
        string remediation = (string)details["remediation"]!;
        Check.Equal(true, remediation.Contains("40 (StaleHelper)") && remediation.Contains("never disconnects"));
        string message = OpennessClientRules.BusyMessage(1234, busy, 15);
        Check.Equal(true, message.Contains("process 40") && message.Contains("15 s") && message.Contains("nothing was attached"));
        Check.Equal(false, OpennessClientRules.BusyMessage(1234, busy, 0).Contains("after waiting"));
        return Task.CompletedTask;
    }

    // Absent keeps the old attach; 0..60 integers check first; anything else is refused before any native call.
    internal static Task WaitSecondsAreBounded()
    {
        Check.Equal<int?>(null, OpennessClientRules.WaitSeconds(null));
        Check.Equal<int?>(null, OpennessClientRules.WaitSeconds(JValue.CreateNull()));
        Check.Equal<int?>(0, OpennessClientRules.WaitSeconds(new JValue(0)));
        Check.Equal<int?>(60, OpennessClientRules.WaitSeconds(new JValue(60)));
        foreach (var bad in new JToken[] { new JValue(-1), new JValue(61), new JValue(1.5), new JValue("5"), new JValue(true) })
        {
            try { OpennessClientRules.WaitSeconds(bad); throw new InvalidOperationException("Expected a refusal for " + bad); }
            catch (BridgeException error) { Check.Equal("InvalidRequest", error.Code); }
        }
        return Task.CompletedTask;
    }
}
