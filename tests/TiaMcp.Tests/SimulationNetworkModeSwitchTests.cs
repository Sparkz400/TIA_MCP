using TiaMcp.Simulation;

namespace TiaMcp.Tests;

// sim_network_mode_set decisions without PLCSIM: what is refused, what is a no-op and what is set.
internal static class SimulationNetworkModeSwitchTests
{
    private static readonly string[] None = [];

    private static void Refused(string code, SimulationNetworkModeSwitch.Decision? decision)
    {
        Check.Equal("refuse", decision?.Outcome);
        Check.Equal(code, decision!.Code);
        Check.Equal(true, !string.IsNullOrWhiteSpace(decision.Message) && !string.IsNullOrWhiteSpace(decision.Remediation));
    }

    internal static Task RefusesBeforeAnyChange()
    {
        // The settable modes are the V8 ENetworkMode names except UnknownState.
        Check.Equal("Softbus,TCPIPSingleAdapter,TCPIPMultipleAdapter", string.Join(",", SimulationNetworkModeSwitch.SettableModes));
        foreach (string target in new[] { "", "TCPIP", "tcpipsingleadapter", "UnknownState", "Softbus ", "PLCSIM" })
            Refused("InvalidRequest", SimulationNetworkModeSwitch.Decide(target, true, null, "Softbus", None));
        Refused("InvalidRequest", SimulationNetworkModeSwitch.Decide("Softbus", true, "softbus", "Softbus", None));
        // The acknowledgement is required even for a no-op and before the observed state is compared.
        Refused("AcknowledgementRequired", SimulationNetworkModeSwitch.Decide("TCPIPSingleAdapter", false, null, "Softbus", None));
        Refused("AcknowledgementRequired", SimulationNetworkModeSwitch.Decide("Softbus", false, "Softbus", "Softbus", None));
        Refused("AcknowledgementRequired", SimulationNetworkModeSwitch.CheckRequest("TCPIPSingleAdapter", false, "Softbus"));
        Check.Equal<SimulationNetworkModeSwitch.Decision?>(null, SimulationNetworkModeSwitch.CheckRequest("TCPIPSingleAdapter", true, "UnknownState"));
        // The caller acts on what it saw: a different current mode is stale, whatever the target.
        Refused("SimulationNetworkModeStale", SimulationNetworkModeSwitch.Decide("TCPIPSingleAdapter", true, "Softbus", "TCPIPMultipleAdapter", None));
        Refused("SimulationNetworkModeStale", SimulationNetworkModeSwitch.Decide("Softbus", true, "TCPIPSingleAdapter", "Softbus", None));
        // An unknown current mode is not switched from.
        Refused("SimulationNetworkModeUnknown", SimulationNetworkModeSwitch.Decide("Softbus", true, null, "UnknownState", None));
        Refused("SimulationNetworkModeUnknown", SimulationNetworkModeSwitch.Decide("Softbus", true, "UnknownState", "UnknownState", None));
        Refused("SimulationNetworkModeUnknown", SimulationNetworkModeSwitch.Decide("Softbus", true, null, "42", None));
        // Any registered instance, owned or not, blocks the switch (the API itself refuses only a running one).
        Refused("SimulationRegistrationsPresent", SimulationNetworkModeSwitch.Decide("TCPIPSingleAdapter", true, "Softbus", "Softbus", ["TiaMcp_1"]));
        Refused("SimulationRegistrationsPresent", SimulationNetworkModeSwitch.Decide("Softbus", true, null, "TCPIPSingleAdapter", ["Someone else's PLC", "TiaMcp_2"]));
        return Task.CompletedTask;
    }

    internal static Task SetsOnlyWhenEveryRuleHolds()
    {
        // No-op: the mode already matches, even with registered instances.
        Check.Equal("unchanged", SimulationNetworkModeSwitch.Decide("Softbus", true, "Softbus", "Softbus", ["TiaMcp_1"])?.Outcome);
        Check.Equal("unchanged", SimulationNetworkModeSwitch.Decide("TCPIPSingleAdapter", true, null, "TCPIPSingleAdapter", None)?.Outcome);
        foreach (string from in SimulationNetworkModeSwitch.SettableModes)
            foreach (string to in SimulationNetworkModeSwitch.SettableModes)
            {
                var decision = SimulationNetworkModeSwitch.Decide(to, true, from, from, None);
                Check.Equal(from == to ? "unchanged" : "set", decision?.Outcome);
                Check.Equal(from == to ? "unchanged" : "set", SimulationNetworkModeSwitch.Decide(to, true, null, from, None)?.Outcome);
            }
        Check.Equal(false, SimulationNetworkModeSwitch.Changed("Softbus", "Softbus"));
        Check.Equal(true, SimulationNetworkModeSwitch.Changed("Softbus", "TCPIPSingleAdapter"));
        return Task.CompletedTask;
    }

    internal static Task ReadbackAndFailuresAreReported()
    {
        Check.Equal<SimulationNetworkModeSwitch.Decision?>(null, SimulationNetworkModeSwitch.CheckReadback("TCPIPSingleAdapter", "TCPIPSingleAdapter"));
        Refused("SimulationNetworkModeReadbackMismatch", SimulationNetworkModeSwitch.CheckReadback("TCPIPSingleAdapter", "Softbus"));
        Refused("SimulationNetworkModeReadbackMismatch", SimulationNetworkModeSwitch.CheckReadback("TCPIPSingleAdapter", "UnknownState"));
        Refused("SimulationNetworkModeReadbackMismatch", SimulationNetworkModeSwitch.CheckReadback("Softbus", null!));
        Check.Equal(true, SimulationNetworkModeSwitch.ReadbackWindowMs > 0 && SimulationNetworkModeSwitch.ReadbackWindowMs <= 10000 && SimulationNetworkModeSwitch.ReadbackIntervalMs > 0);
        // Each documented setter error has its own remediation; anything else gets the generic one.
        var remediations = new[] { "InstanceAlreadyRunning", "VirtualSwitchMisconfigured", "PCAPDriverNotRunning", "AccessDenied", "InternalError" }
            .Select(SimulationNetworkModeSwitch.SetFailureRemediation).ToArray();
        Check.Equal(5, remediations.Distinct().Count());
        Check.Equal(true, remediations[2].Contains("net start npcap", StringComparison.Ordinal));
        Check.Equal(SimulationNetworkModeSwitch.SetFailureRemediation("InternalError"), SimulationNetworkModeSwitch.SetFailureRemediation("Unexpected"));
        return Task.CompletedTask;
    }
}
