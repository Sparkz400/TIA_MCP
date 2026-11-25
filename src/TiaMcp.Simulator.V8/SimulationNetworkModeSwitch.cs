#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Simulation
{
    // Decision rules of sim_network_mode_set, free of the PLCSIM API so they run offline. Mode names are the exact
    // PLCSIM Advanced V8 .NET ENetworkMode names (UnknownState=-1, TCPIPMultipleAdapter=0, TCPIPSingleAdapter=1,
    // Softbus=2; Siemens.Simatic.Simulation.Runtime.Api.x64 8.0.0.1). The mode is machine-wide: one setting of the
    // S7-PLCSIM Advanced Virtual Switch for every instance and user on the PC.
    internal static class SimulationNetworkModeSwitch
    {
        internal const string UnknownMode = "UnknownState";
        internal static readonly string[] SettableModes = { "Softbus", "TCPIPSingleAdapter", "TCPIPMultipleAdapter" };
        internal static readonly string[] ObservableModes = { "Softbus", "TCPIPSingleAdapter", "TCPIPMultipleAdapter", UnknownMode };
        // The setter's result is read back until it shows the target or this window ends; whether PLCSIM applies the mode
        // synchronously is not documented.
        internal const int ReadbackWindowMs = 5000, ReadbackIntervalMs = 250;

        internal sealed class Decision
        {
            internal string Outcome; // "refuse", "unchanged" or "set"
            internal string Code;
            internal string Message;
            internal string Remediation;
        }

        private static Decision Fail(string code, string message, string remediation) =>
            new Decision { Outcome = "refuse", Code = code, Message = message, Remediation = remediation };

        // Arguments only: what can be refused before the PLCSIM API is touched.
        internal static Decision CheckRequest(string target, bool acknowledged, string expectedCurrent)
        {
            if (Array.IndexOf(SettableModes, target) < 0)
                return Fail("InvalidRequest", "mode must be " + string.Join(", ", SettableModes) + ".", "Pass one of the exact mode names.");
            if (expectedCurrent != null && Array.IndexOf(ObservableModes, expectedCurrent) < 0)
                return Fail("InvalidRequest", "expectedCurrentMode must be " + string.Join(", ", ObservableModes) + " when given.", "Pass networkMode exactly as sim_status reported it.");
            if (!acknowledged)
                return Fail("AcknowledgementRequired", "PLCSIM's network mode is machine-wide: it applies to every PLCSIM instance and user on this PC. Nothing was changed.",
                    "Call again with acknowledgeMachineWide true only after the user has agreed to change the PLCSIM network mode for the whole PC.");
            return null;
        }

        // The full decision on the observed state, before the setter is called. registered: every instance the Runtime Manager lists.
        internal static Decision Decide(string target, bool acknowledged, string expectedCurrent, string current, ICollection<string> registered)
        {
            var request = CheckRequest(target, acknowledged, expectedCurrent);
            if (request != null) return request;
            if (expectedCurrent != null && expectedCurrent != current)
                return Fail("SimulationNetworkModeStale", "PLCSIM's network mode is " + current + ", not the expected " + expectedCurrent + ". Nothing was changed.",
                    "Call sim_status, review the current networkMode with the user and call again with that expectedCurrentMode.");
            if (current == target) return new Decision { Outcome = "unchanged" };
            if (current == UnknownMode || Array.IndexOf(SettableModes, current) < 0)
                return Fail("SimulationNetworkModeUnknown", "PLCSIM reports its network mode as " + (current ?? "null") + ". Nothing was changed.",
                    "Check the S7-PLCSIM Advanced Virtual Switch and the PLCSIM Advanced Control Panel (Online Access); set the mode there if needed.");
            if (registered != null && registered.Count > 0)
                return Fail("SimulationRegistrationsPresent", registered.Count + " PLCSIM instance(s) are registered. The network mode is changed only when none is. Nothing was changed.",
                    "Power off and unregister every PLCSIM instance first (sim_release deleteOwned for owned ones, the PLCSIM Control Panel for the others), then call sim_status and try again.");
            return new Decision { Outcome = "set" };
        }

        // After the setter: null when the mode read back is the target.
        internal static Decision CheckReadback(string target, string observed) =>
            observed == target ? null : Fail("SimulationNetworkModeReadbackMismatch",
                "PLCSIM accepted the network mode " + target + " but reads back " + (observed ?? "null") + " after " + ReadbackWindowMs + " ms.",
                "Call sim_status to see the current mode and check it in the PLCSIM Advanced Control Panel (Online Access) before creating instances.");

        // The setter's documented exceptions (API manual V8.0, NetworkMode { set; }, table 7-21) plus AccessDenied, which the
        // function manual (1.4) suggests for network configuration changes without administrator rights.
        internal static string SetFailureRemediation(string runtimeErrorCode)
        {
            switch (runtimeErrorCode)
            {
                case "InstanceAlreadyRunning": return "A PLCSIM instance is running. Power off and unregister every instance, then call sim_status and try again.";
                case "VirtualSwitchMisconfigured": return "The S7-PLCSIM Advanced Virtual Switch driver is not running or not configured. Repair it (PLCSIM Advanced setup) or set the mode in the PLCSIM Advanced Control Panel.";
                case "PCAPDriverNotRunning": return "The Npcap packet capture driver is not active. An administrator runs 'net start npcap' in an elevated command prompt, then try again.";
                case "AccessDenied": return "The Windows user may not change PLCSIM's network configuration. Set the mode in the PLCSIM Advanced Control Panel as an administrator.";
                default: return "Call sim_status to see the current mode, and set it in the PLCSIM Advanced Control Panel (Online Access) if the switch keeps failing.";
            }
        }

        internal static bool Changed(string previous, string observed) => previous != observed;
    }
}
