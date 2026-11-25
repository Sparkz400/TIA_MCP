using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
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

        // sim_network_mode_set: machine-wide, so the Host refuses a missing acknowledgement and unknown mode names before any
        // bridge call (NativeWorkersDisabled would mean it was dispatched; every worker call starts PLCSIM Runtime Manager).
        var method = typeof(SimulatorTools).GetMethod(nameof(SimulatorTools.NetworkModeSet))!;
        var tool = method.GetCustomAttribute<McpServerToolAttribute>()!;
        string description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        check(tool is { Name: "sim_network_mode_set", Destructive: true, ReadOnly: false, OpenWorld: false } && ToolGroups.Of(tool.Name) == "simulation",
            "sim_network_mode_set is a destructive, closed-world tool in the simulation group");
        check(description.Contains("MACHINE-WIDE", StringComparison.Ordinal) && description.Contains("acknowledgeMachineWide", StringComparison.Ordinal)
            && description.Contains("SimulationRegistrationsPresent", StringComparison.Ordinal) && description.Length <= 600, "sim_network_mode_set's description states the scope, the acknowledgement and the refusals");
        check(method.GetParameters().Single(p => p.Name == "acknowledgeMachineWide").DefaultValue is false, "acknowledgeMachineWide defaults to false");
        await Expect("AcknowledgementRequired", 60, t => t.NetworkModeSet("TCPIPSingleAdapter", default), "sim_network_mode_set without acknowledgement is refused before the bridge");
        await Expect("AcknowledgementRequired", 60, t => t.NetworkModeSet("Softbus", default, false, "Softbus"), "An unacknowledged no-op is refused before the bridge too");
        foreach (string mode in new[] { "", "TCPIP", "UnknownState", "softbus", "TCPIPSingleAdapter " })
            await Expect("InvalidRequest", 60, t => t.NetworkModeSet(mode, default, true), "sim_network_mode_set refuses mode '" + mode + "' before the bridge");
        await Expect("InvalidRequest", 60, t => t.NetworkModeSet("Softbus", default, true, "PLCSIM"), "sim_network_mode_set refuses an unknown expectedCurrentMode before the bridge");
        await Expect("NativeWorkersDisabled", 60, t => t.NetworkModeSet("TCPIPSingleAdapter", default, true), "An acknowledged switch is dispatched");
        await Expect("InvalidRequest", 9, t => t.NetworkModeSet("TCPIPSingleAdapter", default, true), "The 5 s readback must end 5 s before the bridge timeout");
        await Expect("NativeWorkersDisabled", 10, t => t.NetworkModeSet("TCPIPSingleAdapter", default, true), "A switch whose readback fits the bridge timeout is dispatched");
        await Expect("AcknowledgementRequired", 9, t => t.NetworkModeSet("TCPIPSingleAdapter", default), "The acknowledgement is refused first, whatever the timeout");
        await Expect("NativeWorkersDisabled", 60, t => t.NetworkModeSet("Softbus", default, true, "UnknownState"), "expectedCurrentMode UnknownState is passed to the worker, which decides");
        {
            var options = new HostOptions("", Path.GetTempPath(), null, 60, false, "", null) { NoNativeWorkers = true };
            await using var simulator = new SimulatorClient(options);
            var refusal = await new SimulatorTools(simulator, options).NetworkModeSet("TCPIPSingleAdapter", default);
            var details = refusal.StructuredContent!.Value.GetProperty("error").GetProperty("details");
            check(refusal.IsError == true && details.GetProperty("changed").GetBoolean() == false && details.GetProperty("machineWide").GetBoolean()
                && details.GetProperty("remediation").GetString()!.Contains("acknowledgeMachineWide", StringComparison.Ordinal), "The acknowledgement refusal carries changed false and a remediation");
        }
        // The arguments sent are exactly the caller's: nothing is defaulted to acknowledged or to an observed mode.
        static (string, object?)[] Sent(object arguments) => arguments.GetType().GetProperties().Select(p => (p.Name, p.GetValue(arguments))).ToArray();
        foreach (string mode in SimulatorTools.NetworkModes)
        {
            check(Sent(SimulatorTools.NetworkModeArguments(mode, true, null)).SequenceEqual(new (string, object?)[] { ("mode", mode), ("acknowledgeMachineWide", true), ("expectedCurrentMode", null) }),
                "sim_network_mode_set sends mode " + mode + ", the acknowledgement and no expected mode");
            check(Sent(SimulatorTools.NetworkModeArguments(mode, true, "TCPIPMultipleAdapter")).SequenceEqual(new (string, object?)[] { ("mode", mode), ("acknowledgeMachineWide", true), ("expectedCurrentMode", "TCPIPMultipleAdapter") }),
                "sim_network_mode_set forwards expectedCurrentMode unchanged for " + mode);
        }
        check(SimulatorTools.NetworkModes.SequenceEqual(new[] { "Softbus", "TCPIPSingleAdapter", "TCPIPMultipleAdapter" }), "The Host offers the settable V8 ENetworkMode names");
        bool hostRefused = false;
        try { SimulatorTools.NetworkModeArguments("Softbus", false, null); } catch (BridgeException error) { hostRefused = error.Code == "AcknowledgementRequired"; }
        check(hostRefused, "No argument object is built without the acknowledgement");
    }
}
