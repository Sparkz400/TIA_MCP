using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.Connection;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;
using Device = TiaMcp.Simulation.SimulationRuntimePlaceholder.Device;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private const string SimulationRuntimeManagerProcess = "Siemens.Simatic.Simulation.Runtime.Manager";
        private SimulationDiscoveryBaseline _simulationDiscoveryBaseline;

        private sealed class SimulationDiscoveryBaseline
        {
            // Openness returns a new Project object per access (live run 23 Oct), so the project is bound by
            // path; the worker's own _portal field changes on tia_start, attach and detach.
            internal object Portal;
            internal string ProjectPath, PlcPath;
            internal Stopwatch Age;
            internal Device? Placeholder;
            internal int ManagerProcessId;
            internal DateTime ManagerStartUtc;
            internal JObject Evidence;
        }

        // Read-only. Must run before sim_create: records what PN/IE / PLCSIM discovery shows while
        // no PLCSIM instance is registered, bound to the exact Runtime Manager process.
        private JObject SimulationDiscoveryBaselineOperation(JObject args)
        {
            _simulationDiscoveryBaseline = null; // a new attempt, even a refused one, never leaves an older baseline usable
            if (!_ownsPortal)
                throw new BridgeException("ScratchIsolationRequired", "The discovery baseline requires a worker-owned TIA connection to the validated scratch project.");
            var plc = SelectPlc(args);
            var provider = ScratchPlcOnlineProvider(plc);
            RetainSimulationRuntimeUntilWorkerExit();
            if ((SimulationRuntimeManager.Version >> 16) != 8)
                throw new BridgeException("SimulatorVersionMismatch", "PLCSIM Advanced runtime V8 is required.");
            if (SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus)
                throw new BridgeException("SoftbusRequired", "The PLCSIM Advanced network mode must already be Softbus; it is never changed.");
            Action guard = () =>
            {
                if (SimulationRuntimeManager.RegisteredInstanceInfo.Length != 0)
                    throw new BridgeException("SimulationRegistrationsPresent", "The discovery baseline must be observed while no PLCSIM instance is registered.");
                if (provider.State != OnlineState.Offline)
                    throw new BridgeException("OfflineRequired", "The scratch CPU must remain offline during the discovery baseline.");
            };
            var manager = SimulationRuntimeManagerIdentity();
            var observations = new JArray();
            string previous = null, classification = null;
            Device[] devices = null, previousDevices = null;
            var clock = Stopwatch.StartNew();
            bool stable = SimulationDiscoveryWait.ObserveEmpty(guard, () =>
            {
                devices = PlcsimDevices(provider, out bool routePresent);
                classification = SimulationRuntimePlaceholder.ClassifyBaseline(devices);
                bool same = previous == null || (classification == previous &&
                    (classification == "empty" || SimulationRuntimePlaceholder.Matches(previousDevices[0], devices[0])));
                observations.Add(new JObject { ["observedAtUtc"] = DateTime.UtcNow.ToString("O"), ["routePresent"] = routePresent,
                    ["classification"] = classification, ["accessibleDevices"] = DevicesJson(devices) });
                previous = classification; previousDevices = devices;
                return classification != null && same;
            }, (attempt, ok, consecutive, elapsed) => { }, () => clock.ElapsedMilliseconds, delay => System.Threading.Thread.Sleep(delay));
            if (!stable)
                throw new BridgeException("SimulationDiscoveryBaselineUnavailable", "Two consecutive identical snapshots showing nothing or only one all-zero Runtime Manager entry were not observed. No placeholder is accepted.");
            if (!SimulationRuntimeManagerIdentity().Equals(manager))
                throw new BridgeException("SimulationRuntimeManagerChanged", "The PLCSIM Advanced Runtime Manager process changed during the discovery baseline.");
            var evidence = new JObject {
                ["observedAtUtc"] = DateTime.UtcNow.ToString("O"), ["project"] = ProjectInfo(plc.Project), ["plcPath"] = plc.Path,
                ["classification"] = classification, ["registeredInstanceCount"] = 0,
                ["placeholder"] = classification == "placeholder" ? DevicesJson(devices)[0] : null,
                ["runtimeManager"] = new JObject { ["processId"] = manager.Id, ["startTimeUtc"] = manager.StartUtc.ToString("O") },
                ["observations"] = observations,
                ["scope"] = "Read-only PN/IE / PLCSIM (1) discovery before any instance registration. One provisioning attempt in this worker may exclude only this exact entry, only while the same Runtime Manager process runs." };
            evidence["lifetimeMs"] = SimulationRuntimePlaceholder.BaselineLifetimeMs;
            _simulationDiscoveryBaseline = new SimulationDiscoveryBaseline {
                Portal = _portal, Age = Stopwatch.StartNew(),
                ProjectPath = plc.Project.Path.FullName, PlcPath = plc.Path, ManagerProcessId = manager.Id, ManagerStartUtc = manager.StartUtc,
                Placeholder = classification == "placeholder" ? devices[0] : (Device?)null, Evidence = evidence };
            var result = Envelope();
            result["baseline"] = evidence.DeepClone();
            return result;
        }

        // Called first in provisioning: the baseline is consumed by any attempt, even one refused early.
        private SimulationDiscoveryBaseline TakeSimulationDiscoveryBaseline()
        {
            var baseline = _simulationDiscoveryBaseline;
            _simulationDiscoveryBaseline = null;
            return baseline;
        }

        private SimulationDiscoveryBaseline UsableSimulationDiscoveryBaseline(SimulationDiscoveryBaseline baseline, PlcSelection plc) =>
            baseline != null && SimulationRuntimePlaceholder.BaselineApplies(baseline.Age.ElapsedMilliseconds,
                ReferenceEquals(baseline.Portal, _portal),
                SameSimulationPath(baseline.ProjectPath, plc.Project.Path.FullName),
                baseline.PlcPath == plc.Path) ? baseline : null;

        // The recorded placeholder, only while the exact same Runtime Manager process is running.
        // Any failure to read that process means nothing is excluded.
        private static Device? AcceptedSimulationPlaceholder(SimulationDiscoveryBaseline baseline)
        {
            if (baseline?.Placeholder == null || baseline.Age.ElapsedMilliseconds > SimulationRuntimePlaceholder.BaselineLifetimeMs) return null;
            try
            {
                var manager = SimulationRuntimeManagerIdentity();
                return manager.Id == baseline.ManagerProcessId && manager.StartUtc == baseline.ManagerStartUtc ? baseline.Placeholder : null;
            }
            catch (Exception error) when (error is BridgeException || error is System.ComponentModel.Win32Exception || error is InvalidOperationException)
            { return null; }
        }

        // Excludes at most the single exact placeholder; a read that hit its limit is never narrowed.
        private static ConfigurationAccessibleDevice[] WithoutSimulationPlaceholder(ConfigurationAccessibleDevice[] devices, Device? placeholder, bool truncated)
        {
            switch (SimulationRuntimePlaceholder.Decide(devices.Select(ToDevice).ToArray(), placeholder, truncated, out int index))
            {
                case SimulationRuntimePlaceholder.Exclusion.Single: return devices.Where((device, i) => i != index).ToArray();
                case SimulationRuntimePlaceholder.Exclusion.RefuseTruncated:
                    throw new BridgeException("SimulationProvisionTargetAmbiguous", "PLCSIM discovery reached its read limit; the Runtime Manager placeholder is not excluded from a truncated list. No credential was submitted.");
                default: return devices;
            }
        }

        private static (int Id, DateTime StartUtc) SimulationRuntimeManagerIdentity()
        {
            var processes = Process.GetProcessesByName(SimulationRuntimeManagerProcess);
            try
            {
                if (processes.Length != 1)
                    throw new BridgeException("SimulationRuntimeManagerAmbiguous", "Exactly one PLCSIM Advanced Runtime Manager process is required.");
                return (processes[0].Id, processes[0].StartTime.ToUniversalTime());
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private OnlineProvider ScratchPlcOnlineProvider(PlcSelection plc)
        {
            var budget = new ScanBudget(50000);
            var hardware = DeviceItems(plc.Project, budget).Where(entry =>
            {
                var software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                return software != null && entry.Path + "/plc/" + Segment(software.Name) == plc.Path;
            }).Take(2).ToArray();
            if (budget.Truncated || hardware.Length != 1)
                throw new BridgeException("AmbiguousTarget", "Cannot uniquely resolve the scratch PLC hardware.");
            var provider = hardware[0].Item.GetService<OnlineProvider>();
            if (provider == null)
                throw new BridgeException("UnsupportedCapability", "The selected scratch CPU has no OnlineProvider service.");
            if (provider.State != OnlineState.Offline)
                throw new BridgeException("OfflineRequired", "The selected scratch CPU must be offline.");
            return provider;
        }

        private static Device[] PlcsimDevices(OnlineProvider provider, out bool routePresent)
        {
            var pc = provider.Configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1);
            routePresent = pc != null;
            return pc == null ? new Device[0] : pc.GetAccessibleDevices().Take(65).Select(ToDevice).ToArray();
        }

        private static Device ToDevice(ConfigurationAccessibleDevice device) =>
            new Device(device.Name, device.Address, device.MACAddress, device.DeviceSeries);

        private static JArray DevicesJson(Device[] devices) => new JArray(devices.Take(64).Select(device => new JObject {
            ["name"] = device.Name, ["address"] = device.Address, ["macAddress"] = device.MacAddress, ["deviceSeries"] = device.DeviceSeries }));
    }
}
