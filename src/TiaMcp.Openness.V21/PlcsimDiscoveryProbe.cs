using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Online;
using Siemens.Simatic.Simulation.Runtime;

namespace TiaMcp.Openness.V21
{
    // Diagnostic: what does PN/IE / PLCSIM discovery return before, with, and after one owned OFF
    // Softbus instance? Never touches existing instances, network mode or existing projects.
    internal static class PlcsimDiscoveryProbe
    {
        private const int SnapshotsPerState = 3;

        internal static int Run(string outputDirectory)
        {
            var root = Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
            if (root.EnumerateFileSystemInfos().Any()) throw new InvalidOperationException("The probe output directory must be new or empty.");
            var result = new JObject { ["schemaVersion"] = "plcsim-discovery-probe.v1", ["startedAtUtc"] = Now(), ["completed"] = false, ["states"] = new JArray() };
            var states = (JArray)result["states"];
            IInstance owned = null;
            string ownedName = "TiaMcpProbe_" + Guid.NewGuid().ToString("N");
            try
            {
                using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
                {
                    var project = portal.Projects.Create(new DirectoryInfo(Path.Combine(root.FullName, "project")), "PlcsimProbe");
                    var cpu = portal.HardwareCatalog.Find("1511-1")
                        .Where(e => string.Equals(e.TypeName, "CPU 1511-1 PN", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(e => Version.TryParse(e.Version.TrimStart('V'), out var v) ? v : new Version(0, 0))
                        .ThenBy(e => e.ArticleNumber, StringComparer.Ordinal).First();
                    var device = project.Devices.CreateWithItem(cpu.TypeIdentifier, "ProbePLC", "ProbeStation");
                    var provider = AllItems(device.DeviceItems).Select(i => i.GetService<OnlineProvider>()).First(p => p != null);
                    result["cpu"] = cpu.TypeIdentifier;

                    states.Add(Observe("A-nothing-touched", provider));
                    result["runtimeVersion"] = SimulationRuntimeManager.Version.ToString("X8"); // initializes the PLCSIM Advanced API
                    result["networkMode"] = SimulationRuntimeManager.NetworkMode.ToString();
                    var preexisting = new JArray(SimulationRuntimeManager.RegisteredInstanceInfo.Select(i => new JObject { ["id"] = i.ID, ["name"] = i.Name }));
                    var stateB = Observe("B-runtime-initialized", provider);
                    stateB["registeredInstances"] = preexisting;
                    states.Add(stateB);

                    if (preexisting.Count > 0 || SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus)
                        result["stateCSkipped"] = "Existing registrations or non-Softbus network mode; nothing foreign is touched or changed.";
                    else
                    {
                        owned = SimulationRuntimeManager.RegisterInstance(ECPUType.CPU1511, ownedName);
                        owned.StoragePath = Directory.CreateDirectory(Path.Combine(root.FullName, "sim", ownedName)).FullName;
                        var stateC = Observe("C-owned-off-softbus-instance", provider);
                        stateC["ownedInstance"] = new JObject { ["id"] = owned.ID, ["name"] = ownedName,
                            ["operatingState"] = owned.OperatingState.ToString(), ["communicationInterface"] = owned.CommunicationInterface.ToString() };
                        states.Add(stateC);
                        owned.UnregisterInstance();
                        owned = null;
                        // Self-check: the probe must leave no registration behind.
                        result["ownedInstanceUnregistered"] = !SimulationRuntimeManager.RegisteredInstanceInfo.Any(i => i.Name == ownedName);
                        if (!result.Value<bool>("ownedInstanceUnregistered")) throw new InvalidOperationException("Owned probe instance is still registered.");
                        states.Add(Observe("D-after-unregister", provider));
                    }
                    project.Close();
                }
                result["completed"] = true;
            }
            catch (Exception error)
            {
                result["failure"] = new JObject { ["type"] = error.GetType().FullName, ["message"] = error.Message };
            }
            finally
            {
                if (owned != null)
                {
                    try { owned.UnregisterInstance(); result["ownedInstanceUnregisteredInCleanup"] = true; }
                    catch (Exception error) { result["ownedCleanupFailure"] = error.GetType().FullName + ": " + error.Message; }
                }
                if (SimulationRuntimeManager.IsInitialized) SimulationRuntimeManager.Shutdown();
                result["completedAtUtc"] = Now();
                File.WriteAllText(Path.Combine(root.FullName, "result.json"), result.ToString());
            }
            return result.Value<bool>("completed") ? 0 : 1;
        }

        private static JObject Observe(string label, OnlineProvider provider)
        {
            var state = new JObject { ["state"] = label, ["simulatorProcesses"] = SimulatorProcesses(), ["snapshots"] = new JArray() };
            for (int i = 0; i < SnapshotsPerState; i++)
            {
                if (i > 0) Thread.Sleep(1000);
                var mode = provider.Configuration.Modes.Find("PN/IE");
                var pc = mode?.PcInterfaces.Find("PLCSIM", 1);
                var devices = pc?.GetAccessibleDevices().Take(65).ToArray();
                ((JArray)state["snapshots"]).Add(new JObject {
                    ["observedAtUtc"] = Now(),
                    ["pnIeInterfaces"] = mode == null ? null : new JArray(mode.PcInterfaces.Select(p => p.Name + " (" + p.Number + ")")),
                    ["plcsimRoute"] = pc != null,
                    ["devices"] = devices == null ? null : new JArray(devices.Select(d => new JObject {
                        ["name"] = d.Name, ["address"] = d.Address, ["macAddress"] = d.MACAddress, ["deviceSeries"] = d.DeviceSeries })) });
            }
            return state;
        }

        private static JArray SimulatorProcesses() => new JArray(Process.GetProcesses()
            .Where(p => p.ProcessName.IndexOf("sim", StringComparison.OrdinalIgnoreCase) >= 0 || p.ProcessName.IndexOf("Simulation", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(p => p.ProcessName + " (" + p.Id + ")").OrderBy(n => n, StringComparer.Ordinal));

        private static System.Collections.Generic.IEnumerable<DeviceItem> AllItems(DeviceItemComposition items) =>
            items.SelectMany(i => new[] { i }.Concat(AllItems(i.DeviceItems)));

        private static string Now() => DateTime.UtcNow.ToString("O");
    }
}
