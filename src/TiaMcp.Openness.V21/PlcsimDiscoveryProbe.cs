using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.HW;
using Siemens.Engineering.Online;
using Siemens.Engineering.Upload;
using Siemens.Simatic.Simulation.Runtime;

namespace TiaMcp.Openness.V21
{
    // Diagnostic of one owned Softbus instance; empty startup is opt-in.
    // Never touches existing instances, network mode or existing projects.
    internal static class PlcsimDiscoveryProbe
    {
        private const int SnapshotsPerState = 3;

        internal static int Run(string outputDirectory, bool startCpu = false, bool visible = false)
        {
            var root = Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
            if (root.EnumerateFileSystemInfos().Any()) throw new InvalidOperationException("The probe output directory must be new or empty.");
            var result = new JObject { ["schemaVersion"] = "plcsim-discovery-probe.v1", ["startedAtUtc"] = Now(), ["completed"] = false, ["states"] = new JArray() };
            result["emptyStartupRequested"] = startCpu;
            result["portalMode"] = visible ? "WithUserInterface" : "WithoutUserInterface";
            Action checkpoint = () => File.WriteAllText(Path.Combine(root.FullName, "result.json"), result.ToString());
            var states = (JArray)result["states"];
            IInstance owned = null;
            string ownedName = "TiaMcpProbe_" + Guid.NewGuid().ToString("N");
            bool powerOnAttempted = false;
            bool runtimeInitializationAttempted = false;
            try
            {
                File.WriteAllText(Path.Combine(root.FullName, "start-intent.json"), new JObject { ["startedAtUtc"] = Now() }.ToString());
                using (var ownedVisiblePortal = visible ? new OwnedQualificationPortal() : null)
                using (var headlessPortal = visible ? null : new TiaPortal(TiaPortalMode.WithoutUserInterface))
                {
                    var portal = ownedVisiblePortal?.Connection ?? headlessPortal;
                    result["processId"] = PortalProcesses.CurrentId(portal);
                    File.WriteAllText(Path.Combine(root.FullName, "start-return.json"), new JObject { ["processId"] = result["processId"] }.ToString());
                    var projectDirectory = Path.Combine(root.FullName, "project");
                    Directory.CreateDirectory(projectDirectory);
                    var project = portal.Projects.Create(new DirectoryInfo(projectDirectory), "PlcsimProbe");
                    var cpu = portal.HardwareCatalog.Find("1511-1")
                        .Where(e => string.Equals(e.TypeName, "CPU 1511-1 PN", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(e => Version.TryParse(e.Version.TrimStart('V'), out var v) ? v : new Version(0, 0))
                        .ThenBy(e => e.ArticleNumber, StringComparer.Ordinal).First();
                    var device = project.Devices.CreateWithItem(cpu.TypeIdentifier, "ProbePLC", "ProbeStation");
                    var provider = AllItems(device.DeviceItems).Select(i => i.GetService<OnlineProvider>()).First(p => p != null);
                    var projectDiscovery = project.GetService<StationUploadProvider>();
                    if (projectDiscovery == null) throw new InvalidOperationException("Project discovery service is unavailable.");
                    result["cpu"] = cpu.TypeIdentifier;

                    states.Add(Observe("A-nothing-touched", provider, projectDiscovery));
                    runtimeInitializationAttempted = true;
                    result["runtimeVersion"] = SimulationRuntimeManager.Version.ToString("X8"); // initializes the PLCSIM Advanced API
                    result["networkMode"] = SimulationRuntimeManager.NetworkMode.ToString();
                    var preexisting = new JArray(SimulationRuntimeManager.RegisteredInstanceInfo.Select(i => new JObject { ["id"] = i.ID, ["name"] = i.Name }));
                    var stateB = Observe("B-runtime-initialized", provider, projectDiscovery);
                    stateB["registeredInstances"] = preexisting;
                    states.Add(stateB);

                    if (preexisting.Count > 0 || SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus)
                    {
                        result["stateCSkipped"] = "Existing registrations or non-Softbus network mode; nothing foreign is touched or changed.";
                        if (startCpu) throw new InvalidOperationException("Empty startup probe requires zero registrations and existing Softbus mode.");
                    }
                    else
                    {
                        owned = SimulationRuntimeManager.RegisterInstance(ECPUType.CPU1511, ownedName);
                        owned.StoragePath = Directory.CreateDirectory(Path.Combine(root.FullName, "sim", ownedName)).FullName;
                        result["ownedInstance"] = new JObject { ["id"] = owned.ID, ["name"] = ownedName, ["storagePath"] = owned.StoragePath };
                        var stateC = Observe("C-owned-off-softbus-instance", provider, projectDiscovery);
                        stateC["ownedInstance"] = new JObject { ["id"] = owned.ID, ["name"] = ownedName,
                            ["operatingState"] = owned.OperatingState.ToString(), ["communicationInterface"] = owned.CommunicationInterface.ToString() };
                        states.Add(stateC);
                        if (startCpu)
                        {
                            VerifySoleOwned(owned, ownedName, EOperatingState.Off);
                            ERuntimeErrorCode? license = null;
                            try { license = owned.LicenseStatus; } catch { }
                            if (license == ERuntimeErrorCode.WarningTrialModeActive || license == ERuntimeErrorCode.WarningRunningOnTiaPortalTestSuite)
                                throw new InvalidOperationException("Full license required; no trial activation attempted.");
                            powerOnAttempted = true;
                            result["stage"] = "beforeEmptyPowerOn"; checkpoint();
                            var status = owned.PowerOn(60000);
                            result["powerOnRuntimeCode"] = status.ToString();
                            if (status != ERuntimeErrorCode.OK) throw new InvalidOperationException("Empty startup did not return OK.");
                            VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                            result["stage"] = "observeEmptyStartup";
                            var startup = new JObject { ["state"] = "C2-owned-empty-stop-softbus-instance", ["snapshots"] = new JArray() };
                            states.Add(startup);
                            var clock = Stopwatch.StartNew();
                            for (int i = 0; i < 61; i++)
                            {
                                if (i > 0) Thread.Sleep(1000);
                                VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                var sample = Observe("startup", provider, projectDiscovery, 1);
                                sample["elapsedMs"] = clock.ElapsedMilliseconds;
                                sample["controllerAddresses"] = new JArray(owned.ControllerIP ?? new string[0]);
                                sample["controllerName"] = owned.ControllerName;
                                sample["communicationInterface"] = owned.CommunicationInterface.ToString();
                                ((JArray)startup["snapshots"]).Add(sample);
                                VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                checkpoint();
                            }
                            owned.PowerOff(60000);
                            VerifySoleOwned(owned, ownedName, EOperatingState.Off);
                            result["ownedPowerOffVerified"] = true;
                            powerOnAttempted = false;
                        }
                        owned.UnregisterInstance();
                        owned = null;
                        // Self-check: the probe must leave no registration behind.
                        result["ownedInstanceUnregistered"] = !SimulationRuntimeManager.RegisteredInstanceInfo.Any(i => i.Name == ownedName);
                        if (!result.Value<bool>("ownedInstanceUnregistered")) throw new InvalidOperationException("Owned probe instance is still registered.");
                        states.Add(Observe("D-after-unregister", provider, projectDiscovery));
                    }
                    project.Close();
                }
                result["ownedPortalConnectionDisposed"] = true;
                result["explicitOwnedSdkProcessDisposeCompleted"] = visible;
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
                    try
                    {
                        VerifyOwned(owned, ownedName);
                        if (powerOnAttempted)
                        {
                            owned.PowerOff(60000);
                            if (owned.OperatingState != EOperatingState.Off) throw new InvalidOperationException("Owned instance did not reach OFF during cleanup.");
                            result["ownedPowerOffVerified"] = true;
                        }
                        VerifyOwned(owned, ownedName);
                        owned.UnregisterInstance();
                        result["ownedInstanceUnregisteredInCleanup"] = !SimulationRuntimeManager.RegisteredInstanceInfo.Any(i => i.Name == ownedName);
                        if (!result.Value<bool>("ownedInstanceUnregisteredInCleanup")) throw new InvalidOperationException("Owned registration remains after cleanup.");
                    }
                    catch (Exception error) { result["completed"] = false; result["ownedCleanupFailure"] = error.GetType().FullName + ": " + error.Message; }
                }
                if (runtimeInitializationAttempted) SimulationRuntimeManager.Shutdown();
                result["completedAtUtc"] = Now();
                File.WriteAllText(Path.Combine(root.FullName, "result.json"), result.ToString());
            }
            return result.Value<bool>("completed") ? 0 : 1;
        }

        private static void VerifySoleOwned(IInstance instance, string name, EOperatingState expected)
        {
            VerifyOwned(instance, name);
            var registered = SimulationRuntimeManager.RegisteredInstanceInfo;
            if (registered.Length != 1 || registered[0].ID != instance.ID || registered[0].Name != name ||
                instance.OperatingState != expected || SimulationRuntimeManager.NetworkMode != ENetworkMode.Softbus)
                throw new InvalidOperationException("The sole owned simulator identity, state or Softbus mode changed.");
        }

        private static void VerifyOwned(IInstance instance, string name)
        {
            if (SimulationRuntimeManager.RegisteredInstanceInfo.Count(i => i.ID == instance.ID && i.Name == name) != 1)
                throw new InvalidOperationException("Owned simulator registration identity changed; no cleanup mutation attempted.");
        }

        private static JObject Observe(string label, OnlineProvider provider, StationUploadProvider projectDiscovery, int count = SnapshotsPerState)
        {
            var state = new JObject { ["state"] = label, ["simulatorProcesses"] = SimulatorProcesses(), ["snapshots"] = new JArray() };
            for (int i = 0; i < count; i++)
            {
                if (i > 0) Thread.Sleep(1000);
                var sample = ObserveConfiguration(provider.Configuration);
                sample["stationUploadProvider"] = ObserveConfiguration(projectDiscovery.Configuration);
                ((JArray)state["snapshots"]).Add(sample);
            }
            return state;
        }

        private static JObject ObserveConfiguration(ConnectionConfiguration configuration)
        {
            var mode = configuration.Modes.Find("PN/IE");
            var pc = mode?.PcInterfaces.Find("PLCSIM", 1);
            var devices = pc?.GetAccessibleDevices().Take(65).ToArray();
            return new JObject {
                ["observedAtUtc"] = Now(),
                ["pnIeInterfaces"] = mode == null ? null : new JArray(mode.PcInterfaces.Select(p => p.Name + " (" + p.Number + ")")),
                ["plcsimRoute"] = pc != null,
                ["configuredAddresses"] = pc == null ? null : new JArray(pc.Addresses.Select(a => a.Name)),
                ["targetInterfaces"] = pc == null ? null : new JArray(pc.TargetInterfaces.Select(t => t.Name)),
                ["truncated"] = devices != null && devices.Length > 64,
                ["devices"] = devices == null ? null : new JArray(devices.Select(d => new JObject {
                    ["name"] = d.Name, ["address"] = d.Address, ["macAddress"] = d.MACAddress, ["deviceSeries"] = d.DeviceSeries })) };
        }

        private static JArray SimulatorProcesses()
        {
            var processes = Process.GetProcesses();
            try { return new JArray(processes
                .Where(p => p.ProcessName.IndexOf("sim", StringComparison.OrdinalIgnoreCase) >= 0 || p.ProcessName.IndexOf("Simulation", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(p => p.ProcessName + " (" + p.Id + ")").OrderBy(n => n, StringComparer.Ordinal)); }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private static System.Collections.Generic.IEnumerable<DeviceItem> AllItems(DeviceItemComposition items) =>
            items.SelectMany(i => new[] { i }.Concat(AllItems(i.DeviceItems)));

        private static string Now() => DateTime.UtcNow.ToString("O");
    }
}
