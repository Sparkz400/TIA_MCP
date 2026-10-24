using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Upload;
using Siemens.Simatic.Simulation.Runtime;

namespace TiaMcp.Openness.V21
{
    // Diagnostic: does the all-zero PLCSIM discovery entry follow the Runtime Manager's own lifetime?
    // Each phase reads discovery from a fresh owned Portal (discovery is cached per Portal process).
    // Requires zero registered instances; never creates, starts or changes a simulated CPU.
    internal static class PlcsimPlaceholderIdentityProbe
    {
        private const string ManagerProcess = "Siemens.Simatic.Simulation.Runtime.Manager";

        internal static int Run(string outputDirectory)
        {
            var root = Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
            if (root.EnumerateFileSystemInfos().Any()) throw new InvalidOperationException("The probe output directory must be new or empty.");
            var result = new JObject { ["schemaVersion"] = "plcsim-placeholder-identity.v1", ["startedAtUtc"] = Now(), ["completed"] = false, ["phases"] = new JArray() };
            Action save = () => File.WriteAllText(Path.Combine(root.FullName, "result.json"), result.ToString());
            var phases = (JArray)result["phases"];
            try
            {
                phases.Add(Observe(root, "a-before-initialization")); save();
                if (SimulationRuntimeManager.RegisteredInstanceInfo.Length != 0)
                    throw new InvalidOperationException("Registered PLCSIM instances exist; the probe needs zero.");
                phases.Add(Observe(root, "b-initialized-zero-instances")); save();
                SimulationRuntimeManager.Shutdown();
                var clock = Stopwatch.StartNew();
                while (Managers().Length != 0 && clock.ElapsedMilliseconds < 180000) Thread.Sleep(1000);
                phases.Add(Observe(root, "c-after-shutdown")); save();
                if (SimulationRuntimeManager.RegisteredInstanceInfo.Length != 0)
                    throw new InvalidOperationException("Registered PLCSIM instances appeared after reinitialization.");
                phases.Add(Observe(root, "d-reinitialized-zero-instances")); save();
                SimulationRuntimeManager.Shutdown();
                result["completed"] = true;
            }
            catch (Exception ex) { result["failure"] = ex.GetType().FullName + ": " + ex.Message; }
            result["finishedAtUtc"] = Now();
            save();
            return result.Value<bool>("completed") ? 0 : 1;
        }

        private static JObject Observe(DirectoryInfo root, string label)
        {
            var phase = new JObject { ["label"] = label, ["observedAtUtc"] = Now(), ["managers"] = ManagerIdentities(), ["snapshots"] = new JArray() };
            string directory = Path.Combine(root.FullName, label);
            Directory.CreateDirectory(directory);
            using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                phase["portalProcessId"] = PortalProcesses.CurrentId(portal);
                var project = portal.Projects.Create(new DirectoryInfo(directory), "P");
                var discovery = project.GetService<StationUploadProvider>()
                    ?? throw new InvalidOperationException("The empty project has no discovery service.");
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) Thread.Sleep(1000);
                    var pc = discovery.Configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1);
                    var devices = pc?.GetAccessibleDevices().Take(9).ToArray();
                    ((JArray)phase["snapshots"]).Add(new JObject { ["routePresent"] = pc != null,
                        ["devices"] = devices == null ? null : new JArray(devices.Select(d => new JObject {
                            ["name"] = d.Name, ["address"] = d.Address, ["macAddress"] = d.MACAddress, ["deviceSeries"] = d.DeviceSeries })) });
                }
                project.Close();
            }
            phase["managersAfter"] = ManagerIdentities();
            return phase;
        }

        private static Process[] Managers() => Process.GetProcessesByName(ManagerProcess);

        private static JArray ManagerIdentities()
        {
            var processes = Managers();
            try { return new JArray(processes.Select(p => new JObject { ["processId"] = p.Id, ["startedAtUtc"] = p.StartTime.ToUniversalTime().ToString("O") })); }
            finally { foreach (var p in processes) p.Dispose(); }
        }

        private static string Now() => DateTime.UtcNow.ToString("O");
    }
}
