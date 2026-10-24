using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
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

        internal static int Run(string outputDirectory, bool startCpu = false, bool visible = false, bool lateDiscovery = false, bool configureRoute = false, bool freshPortal = false)
        {
            if (lateDiscovery && !startCpu) throw new ArgumentException("Late discovery requires the explicit empty startup probe.");
            if (configureRoute && (!startCpu || lateDiscovery || visible)) throw new ArgumentException("Configured discovery requires headless startup with the pre-start baseline.");
            if (freshPortal && !configureRoute) throw new ArgumentException("Fresh Portal comparison requires the configured discovery baseline.");
            var root = Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
            if (root.EnumerateFileSystemInfos().Any()) throw new InvalidOperationException("The probe output directory must be new or empty.");
            var result = new JObject { ["schemaVersion"] = "plcsim-discovery-probe.v1", ["startedAtUtc"] = Now(), ["completed"] = false, ["states"] = new JArray() };
            result["emptyStartupRequested"] = startCpu;
            result["portalMode"] = visible ? "WithUserInterface" : "WithoutUserInterface";
            result["preStartDiscoverySkipped"] = lateDiscovery;
            result["routeConfigurationRequested"] = configureRoute;
            result["freshPortalRequested"] = freshPortal;
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

                    states.Add(lateDiscovery ? Skipped("A-nothing-touched") : Observe("A-nothing-touched", provider, projectDiscovery));
                    runtimeInitializationAttempted = true;
                    result["runtimeVersion"] = SimulationRuntimeManager.Version.ToString("X8"); // initializes the PLCSIM Advanced API
                    result["networkMode"] = SimulationRuntimeManager.NetworkMode.ToString();
                    var preexisting = new JArray(SimulationRuntimeManager.RegisteredInstanceInfo.Select(i => new JObject { ["id"] = i.ID, ["name"] = i.Name }));
                    var stateB = lateDiscovery ? Skipped("B-runtime-initialized") : Observe("B-runtime-initialized", provider, projectDiscovery);
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
                        var stateC = lateDiscovery ? Skipped("C-owned-off-softbus-instance") : Observe("C-owned-off-softbus-instance", provider, projectDiscovery);
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
                            if (lateDiscovery)
                            {
                                result["discoveryMetadata"] = new JObject {
                                    ["plc"] = DescribeDiscovery(provider, provider.Configuration),
                                    ["project"] = DescribeDiscovery(projectDiscovery, projectDiscovery.Configuration) };
                                checkpoint();
                            }
                            // Compare a provider whose configuration was not queried before startup.
                            var download = AllItems(device.DeviceItems).Select(i => i.GetService<DownloadProvider>()).First(p => p != null);
                            result["downloadProviderFirstObservation"] = ObserveConfiguration(download.Configuration);
                            states.Add(Observe("C3-after-first-download-provider-query", provider, projectDiscovery));
                            checkpoint();
                            if (configureRoute)
                            {
                                VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                var configuration = provider.Configuration;
                                var pc = configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1);
                                var targets = pc?.TargetInterfaces.Take(2).ToArray();
                                if (provider.State != OnlineState.Offline || targets == null || targets.Length != 1 || string.IsNullOrWhiteSpace(targets[0].Name))
                                    throw new InvalidOperationException("One named PLCSIM target interface and an offline scratch connection are required.");
                                result["routeConfiguration"] = new JObject { ["targetInterface"] = targets[0].Name,
                                    ["onlineStateBefore"] = provider.State.ToString(), ["applyAttempted"] = true };
                                checkpoint();
                                var applied = configuration.ApplyConfiguration(targets[0]);
                                result["routeConfiguration"]["applied"] = applied;
                                result["routeConfiguration"]["onlineStateAfter"] = provider.State.ToString();
                                VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                if (!applied || provider.State != OnlineState.Offline)
                                    throw new InvalidOperationException("Scratch route configuration failed or changed the offline state.");
                                states.Add(Observe("C4-after-route-configuration", provider, projectDiscovery));
                                VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                checkpoint();
                                project.Save();
                                if (project.IsModified) throw new InvalidOperationException("Scratch project did not reach saved state.");
                                var savedPath = project.Path;
                                var deviceName = device.Name;
                                result["projectReload"] = new JObject { ["path"] = savedPath.FullName, ["saved"] = true, ["closeAttempted"] = true };
                                checkpoint();
                                project.Close();
                                result["projectReload"]["closed"] = true;
                                checkpoint();
                                project = OpenProbeProject(portal, savedPath, deviceName);
                                device = project.Devices.Single();
                                provider = AllItems(device.DeviceItems).Select(i => i.GetService<OnlineProvider>()).First(p => p != null);
                                projectDiscovery = project.GetService<StationUploadProvider>();
                                if (projectDiscovery == null || provider.State != OnlineState.Offline)
                                    throw new InvalidOperationException("Reopened scratch discovery services are unavailable or online.");
                                result["projectReload"]["reopened"] = true;
                                VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                states.Add(Observe("C5-after-project-reload", provider, projectDiscovery));
                                VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                checkpoint();
                                if (freshPortal)
                                {
                                    var nonce = Guid.NewGuid().ToString("N");
                                    File.WriteAllText(Path.Combine(root.FullName, "second-start-intent.json"), new JObject { ["nonce"] = nonce }.ToString());
                                    using (var comparisonPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
                                    {
                                        var comparisonId = PortalProcesses.CurrentId(comparisonPortal);
                                        if (comparisonId == result.Value<int>("processId")) throw new InvalidOperationException("Fresh Portal process identity was reused.");
                                        result["comparisonProcessId"] = comparisonId;
                                        File.WriteAllText(Path.Combine(root.FullName, "second-start-return.json"), new JObject { ["processId"] = comparisonId, ["nonce"] = nonce }.ToString());
                                        var receiptPath = Path.Combine(root.FullName, "second-handle-captured.json");
                                        var wait = Stopwatch.StartNew();
                                        while (!File.Exists(receiptPath) && wait.ElapsedMilliseconds < 30000) Thread.Sleep(100);
                                        var receipt = JObject.Parse(File.ReadAllText(receiptPath));
                                        if (receipt.Value<int>("processId") != comparisonId || receipt.Value<string>("nonce") != nonce || receipt.Value<bool>("originalHandleRetained") != true)
                                            throw new InvalidOperationException("Fresh Portal original handle receipt does not match this attempt.");
                                        result["comparisonHandleReceiptVerified"] = true;
                                        VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                        var discoveryDirectory = Path.Combine(root.FullName, "discovery");
                                        Directory.CreateDirectory(discoveryDirectory);
                                        var discoveryProject = comparisonPortal.Projects.Create(new DirectoryInfo(discoveryDirectory), "DiscoveryProbe");
                                        var discoveryProvider = discoveryProject.GetService<StationUploadProvider>();
                                        if (discoveryProvider == null) throw new InvalidOperationException("Empty-project discovery service is unavailable.");
                                        var discoverySamples = new JArray();
                                        result["emptyProjectDiscovery"] = new JObject { ["projectPath"] = discoveryProject.Path.FullName,
                                            ["originalProjectStillOpen"] = project.Path.FullName == savedPath.FullName && !project.IsModified,
                                            ["snapshots"] = discoverySamples };
                                        if (!result["emptyProjectDiscovery"].Value<bool>("originalProjectStillOpen"))
                                            throw new InvalidOperationException("Original scratch project changed before independent discovery.");
                                        for (int i = 0; i < SnapshotsPerState; i++)
                                        {
                                            if (i > 0) Thread.Sleep(1000);
                                            VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                            discoverySamples.Add(ObserveConfiguration(discoveryProvider.Configuration));
                                            VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                            checkpoint();
                                        }
                                        discoveryProject.Close();
                                        result["emptyProjectDiscovery"]["closed"] = true;
                                        project.Close();
                                        var comparisonProject = OpenProbeProject(comparisonPortal, savedPath, deviceName);
                                        var comparisonProvider = AllItems(comparisonProject.Devices.Single().DeviceItems).Select(i => i.GetService<OnlineProvider>()).First(p => p != null);
                                        var comparisonDiscovery = comparisonProject.GetService<StationUploadProvider>();
                                        if (comparisonDiscovery == null || comparisonProvider.State != OnlineState.Offline)
                                            throw new InvalidOperationException("Fresh Portal discovery services are unavailable or online.");
                                        states.Add(Observe("C6-fresh-portal", comparisonProvider, comparisonDiscovery));
                                        VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                        if (comparisonProject.IsModified) throw new InvalidOperationException("Fresh Portal observation modified the scratch project.");
                                        comparisonProject.Close();
                                        checkpoint();
                                    }
                                    result["comparisonPortalConnectionDisposed"] = true;
                                    project = OpenProbeProject(portal, savedPath, deviceName);
                                    provider = AllItems(project.Devices.Single().DeviceItems).Select(i => i.GetService<OnlineProvider>()).First(p => p != null);
                                    projectDiscovery = project.GetService<StationUploadProvider>();
                                    if (projectDiscovery == null || provider.State != OnlineState.Offline)
                                        throw new InvalidOperationException("Original Portal discovery services are unavailable or online.");
                                    VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                    states.Add(Observe("C7-original-portal-after-comparison", provider, projectDiscovery));
                                    VerifySoleOwned(owned, ownedName, EOperatingState.Stop);
                                    checkpoint();
                                }
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

        private static Project OpenProbeProject(TiaPortal portal, FileInfo path, string deviceName)
        {
            var project = portal.Projects.Open(path);
            if (project.Path.FullName != path.FullName || project.IsModified || project.Devices.Count != 1 || project.Devices.Single().Name != deviceName)
                throw new InvalidOperationException("Reopened scratch project identity or saved state changed.");
            return project;
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

        private static JObject Skipped(string label) => new JObject {
            ["state"] = label, ["discoverySkipped"] = true,
            ["scope"] = "Delayed first enumeration experiment only; no pre-start discovery isolation proof and no provisioning authority." };

        private static JObject DescribeDiscovery(IEngineeringObject provider, ConnectionConfiguration configuration)
        {
            var mode = configuration.Modes.Find("PN/IE");
            var pc = mode?.PcInterfaces.Find("PLCSIM", 1);
            var result = new JObject();
            foreach (var entry in new[] { new { Name = "provider", Target = provider },
                new { Name = "configuration", Target = (IEngineeringObject)configuration },
                new { Name = "mode", Target = (IEngineeringObject)mode },
                new { Name = "pcInterface", Target = (IEngineeringObject)pc } })
            {
                if (entry.Target == null) { result[entry.Name] = null; continue; }
                var actions = entry.Target.GetInvocationInfos().Take(65).ToArray();
                var attributes = entry.Target.GetAttributeInfos().Take(65).ToArray();
                result[entry.Name] = new JObject {
                    ["type"] = entry.Target.GetType().FullName, ["actionsExecuted"] = false,
                    ["truncated"] = actions.Length > 64 || attributes.Length > 64,
                    ["actions"] = new JArray(actions.Take(64).Select(a => new JObject { ["name"] = a.Name, ["returnType"] = a.Type?.FullName })),
                    ["attributes"] = new JArray(attributes.Take(64).Select(a => new JObject { ["name"] = a.Name, ["accessMode"] = a.AccessMode.ToString() })) };
            }
            return result;
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
