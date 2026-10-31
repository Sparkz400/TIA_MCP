using System.Text.Json;
using ModelContextProtocol.Protocol;
using TiaMcp.Host;

// Host-side simulator call admission with native workers disabled: no worker starts and nothing is sent.
internal static class SimulatorToolChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var none = JsonDocument.Parse("[]").RootElement;
        async Task<string?> Code(int timeoutSeconds, Func<SimulatorTools, Task<CallToolResult>> call)
        {
            var options = new HostOptions("", Path.GetTempPath(), null, timeoutSeconds, false, "", null) { NoNativeWorkers = true };
            await using var simulator = new SimulatorClient(options);
            var result = await call(new SimulatorTools(simulator, options));
            return result.StructuredContent?.GetProperty("error").GetProperty("code").GetString();
        }
        async Task Expect(string expected, int timeoutSeconds, Func<SimulatorTools, Task<CallToolResult>> call, string label)
        {
            string? actual = await Code(timeoutSeconds, call);
            check(actual == expected, label + " (" + actual + ")");
        }
        // Waits must end 5 s before the bridge timeout; a call still running then faults the simulator bridge for the whole Host.
        await Expect("InvalidRequest", 60, t => t.State(1, "powerOn", default, 27501), "powerOn and its failure powerOff must fit the bridge timeout");
        await Expect("NativeWorkersDisabled", 60, t => t.State(1, "powerOn", default, 27500), "powerOn at the bridge limit is dispatched");
        await Expect("NativeWorkersDisabled", 60, t => t.State(1, "stop", default, 55000), "Other actions wait once");
        await Expect("InvalidRequest", 120, t => t.State(1, "powerOn", default, 60000), "The default bridge timeout refuses a 60 s powerOn");
        await Expect("InvalidRequest", 60, t => t.Observe(1, default, windowMs: 55001), "An observation window must fit the bridge timeout");
        await Expect("NativeWorkersDisabled", 60, t => t.Observe(1, default, windowMs: 55000), "A fitting observation window is dispatched");
        await Expect("InvalidRequest", 104, t => t.Test(1, none, none, default), "sim_test needs room for its 90 s execution cap");
        await Expect("NativeWorkersDisabled", 105, t => t.Test(1, none, none, default), "sim_test is dispatched with a 105 s bridge timeout");
        await Expect("InvalidRequest", 34, t => t.Release(1, default, true), "sim_release deleteOwned needs room for its 30 s power-off");
        await Expect("NativeWorkersDisabled", 35, t => t.Release(1, default, true), "sim_release deleteOwned is dispatched with a 35 s bridge timeout");
        await Expect("NativeWorkersDisabled", 5, t => t.Release(1, default), "sim_release without deleteOwned waits nothing");
        // The request token never reaches the bridge, so a cancelled request cannot fault it.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Expect("NativeWorkersDisabled", 60, t => t.Status(cancelled.Token), "A cancelled request is not passed to the bridge");
    }
}
