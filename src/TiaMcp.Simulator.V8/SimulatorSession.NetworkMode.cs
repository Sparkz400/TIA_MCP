using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Simulator.V8
{
    internal sealed partial class SimulatorSession
    {
        // sim_network_mode_set: the only operation that assigns SimulationRuntimeManager.NetworkMode, and only after an explicit
        // machine-wide acknowledgement, with no instance registered. It never sets adapter bindings (SetNetInterfaceBindings
        // needs administrator rights) and no other operation calls it.
        private sealed class NetworkModeRequest
        {
            internal string Mode;
            internal bool Acknowledged;
            internal string ExpectedCurrent;
        }

        // Argument refusals, checked in Execute before the first PLCSIM API call so they never start Runtime Manager.
        private static NetworkModeRequest ParseNetworkModeRequest(JObject args)
        {
            if (args["mode"]?.Type != JTokenType.String) throw new SimulatorException("InvalidRequest", "mode must be a string.");
            if (args["acknowledgeMachineWide"] != null && args["acknowledgeMachineWide"].Type != JTokenType.Boolean && args["acknowledgeMachineWide"].Type != JTokenType.Null)
                throw new SimulatorException("InvalidRequest", "acknowledgeMachineWide must be a boolean.");
            if (args["expectedCurrentMode"] != null && args["expectedCurrentMode"].Type != JTokenType.String && args["expectedCurrentMode"].Type != JTokenType.Null)
                throw new SimulatorException("InvalidRequest", "expectedCurrentMode must be a string.");
            var request = new NetworkModeRequest { Mode = args.Value<string>("mode"), Acknowledged = args.Value<bool?>("acknowledgeMachineWide") ?? false,
                ExpectedCurrent = args.Value<string>("expectedCurrentMode") };
            ThrowIfRefused(SimulationNetworkModeSwitch.CheckRequest(request.Mode, request.Acknowledged, request.ExpectedCurrent), null, null);
            return request;
        }

        private static void ThrowIfRefused(SimulationNetworkModeSwitch.Decision decision, string currentMode, JArray registered)
        {
            if (decision == null || decision.Outcome != "refuse") return;
            var details = new JObject { ["remediation"] = decision.Remediation, ["machineWide"] = true, ["changed"] = false };
            if (currentMode != null) details["networkMode"] = currentMode;
            if (registered != null) details["registeredInstances"] = registered;
            throw new SimulatorException(decision.Code, decision.Message, details);
        }

        private JObject NetworkModeSet(JObject args)
        {
            var request = ParseNetworkModeRequest(args);
            string previous = SimulationRuntimeManager.NetworkMode.ToString();
            var instances = SimulationRuntimeManager.RegisteredInstanceInfo;
            var registered = new JArray(instances.Select(info => new JObject { ["id"] = info.ID, ["name"] = info.Name,
                ["selected"] = _instances.ContainsKey(info.ID), ["owned"] = _instances.ContainsKey(info.ID) && _instances[info.ID].Owned }));
            var decision = SimulationNetworkModeSwitch.Decide(request.Mode, request.Acknowledged, request.ExpectedCurrent, previous,
                instances.Select(info => info.Name).ToArray());
            ThrowIfRefused(decision, previous, registered);
            string observed = previous;
            if (decision.Outcome == "set")
            {
                var target = (ENetworkMode)Enum.Parse(typeof(ENetworkMode), request.Mode, false);
                try { SimulationRuntimeManager.NetworkMode = target; }
                catch (SimulationRuntimeException error)
                {
                    string after = ReadNetworkMode();
                    string code = error.RuntimeErrorCode.ToString();
                    throw new SimulatorException("SimulationNetworkModeSetFailed", "PLCSIM refused the network mode " + request.Mode + ": " + error.Message,
                        new JObject { ["runtimeErrorCode"] = code, ["previousMode"] = previous, ["networkMode"] = after,
                            ["changed"] = after != null && SimulationNetworkModeSwitch.Changed(previous, after), ["machineWide"] = true,
                            ["remediation"] = SimulationNetworkModeSwitch.SetFailureRemediation(code) });
                }
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    observed = ReadNetworkMode();
                    if (observed == request.Mode || clock.ElapsedMilliseconds >= SimulationNetworkModeSwitch.ReadbackWindowMs) break;
                    Thread.Sleep(SimulationNetworkModeSwitch.ReadbackIntervalMs);
                }
                var readback = SimulationNetworkModeSwitch.CheckReadback(request.Mode, observed);
                if (readback != null)
                    throw new SimulatorException(readback.Code, readback.Message, new JObject { ["previousMode"] = previous, ["networkMode"] = observed,
                        ["changed"] = observed != null && SimulationNetworkModeSwitch.Changed(previous, observed), ["readbackVerified"] = false,
                        ["machineWide"] = true, ["remediation"] = readback.Remediation });
            }
            var result = Envelope();
            result["previousMode"] = previous;
            result["mode"] = observed;
            result["changed"] = SimulationNetworkModeSwitch.Changed(previous, observed);
            result["readbackVerified"] = observed == request.Mode;
            result["machineWide"] = true;
            result["tcpIpReady"] = SimulationNetworkAdmission.Refusal(observed, "TCPIP", "TCPIP") == null;
            result["tcpIpRequiredMode"] = "TCPIPSingleAdapter";
            result["plcsimAdapters"] = PlcsimAdapters();
            result["bindingsChanged"] = false;
            result["note"] = "Only the machine-wide network mode was set; adapter bindings, adapter mappings and IP addresses were not touched. " +
                "In a TCP/IP mode PLCSIM communicates over its virtual Ethernet adapter; choose a real adapter, if any, in the PLCSIM Advanced Control Panel. " +
                "Softbus instances need mode Softbus, TCPIP instances from sim_create need TCPIPSingleAdapter.";
            return result;
        }

        private static string ReadNetworkMode()
        {
            try { return SimulationRuntimeManager.NetworkMode.ToString(); }
            catch (Exception) { return null; }
        }
    }
}
