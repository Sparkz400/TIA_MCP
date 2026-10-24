using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Upload;

namespace TiaMcp.Openness.V21
{
    // A Portal process caches PLCSIM discovery, so the provisioning Portal stays stale
    // after owned startup. A second owned Portal with an empty project observes the
    // endpoint freshly; it only reads discovery and never connects or sees a credential.
    internal sealed class SimulationDiscoveryObserver
    {
        internal const string ProjectName = "D";
        private readonly TiaPortal _portal;
        private readonly Project _project;
        private readonly StationUploadProvider _discovery;
        private readonly Process _process;
        internal JObject Evidence { get; }

        private SimulationDiscoveryObserver(TiaPortal portal, Project project, StationUploadProvider discovery, Process process, JObject evidence)
        {
            _portal = portal; _project = project; _discovery = discovery; _process = process; Evidence = evidence;
        }

        internal static void VerifyDestination(string directory)
        {
            if (!Path.IsPathRooted(directory)) throw new BridgeException("MutationScopeIncomplete", "The discovery observer project needs an exact absolute path.");
            if (Path.Combine(directory, ProjectName, ProjectName + ".ap21").Length > 143)
                throw new BridgeException("ProjectPathTooLong", "Planned discovery observer destination exceeds the native path limit.");
        }

        internal static SimulationDiscoveryObserver Start(string directory, int? provisioningProcessId)
        {
            VerifyDestination(directory);
            if (File.Exists(directory) || Directory.Exists(directory))
                throw new BridgeException("MutationScopeStale", "The admitted discovery observer destination already exists.");
            var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
            Process process = null;
            try
            {
                int id = PortalProcesses.CurrentId(portal);
                // Retain the exact handle at once so exit is checked on this process, never a reused PID.
                process = Process.GetProcessById(id);
                // .NET Framework keeps a by-ID process handle only once Handle is read; without it the exit wait fails after exit.
                _ = process.Handle;
                if (id == provisioningProcessId || process.HasExited)
                    throw new BridgeException("SimulationDiscoveryObserverUnavailable", "The discovery observer did not start as a distinct live Portal process.");
                var evidence = new JObject { ["processId"] = id, ["startedAtUtc"] = process.StartTime.ToUniversalTime().ToString("O"),
                    ["processName"] = process.ProcessName, ["provisioningProcessId"] = provisioningProcessId,
                    ["projectDirectory"] = directory, ["scope"] = "Read-only PLCSIM discovery from an empty project; no device, route, connection or credential." };
                if (!string.Equals(process.ProcessName, "Siemens.Automation.Portal", StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("SimulationDiscoveryObserverUnavailable", "The discovery observer is not a TIA Portal process.");
                Directory.CreateDirectory(directory);
                // Siemens resolves a DirectoryInfo from CreateDirectory by name only; pass an explicit absolute one.
                var project = portal.Projects.Create(new DirectoryInfo(directory), ProjectName);
                var discovery = project.GetService<StationUploadProvider>()
                    ?? throw new BridgeException("SimulationDiscoveryObserverUnavailable", "The discovery observer's empty project has no discovery service.");
                if (project.Devices.Count != 0)
                    throw new BridgeException("SimulationDiscoveryObserverUnavailable", "The discovery observer project is not empty.");
                evidence["projectPath"] = project.Path.FullName;
                return new SimulationDiscoveryObserver(portal, project, discovery, process, evidence);
            }
            catch
            {
                portal.Dispose();
                process?.Dispose();
                throw;
            }
        }

        internal ConfigurationAccessibleDevice[] Read(int take)
        {
            var pc = _discovery.Configuration.Modes.Find("PN/IE")?.PcInterfaces.Find("PLCSIM", 1);
            if (pc == null)
                throw new BridgeException("SimulationProvisionRouteUnavailable", "The discovery observer has no PN/IE / PLCSIM (1) route.");
            return pc.GetAccessibleDevices().Take(take).ToArray();
        }

        // Closes the empty project, disposes the owned Portal and waits for its exact process to exit.
        internal JObject Stop(int exitTimeoutMs = 60000)
        {
            var receipt = new JObject { ["action"] = "stopDiscoveryObserver", ["processId"] = Evidence["processId"].DeepClone() };
            try
            {
                if (_project.IsModified) receipt["projectModifiedBeforeClose"] = true;
                _project.Close();
                receipt["projectClosed"] = true;
            }
            catch (Exception ex) { receipt["projectCloseExceptionType"] = ex.GetType().FullName; }
            try { _portal.Dispose(); receipt["portalDisposed"] = true; }
            catch (Exception ex) { receipt["portalDisposeExceptionType"] = ex.GetType().FullName; }
            try
            {
                bool exited = _process.WaitForExit(exitTimeoutMs);
                receipt["processExitVerified"] = exited;
                if (exited) receipt["exitCode"] = _process.ExitCode;
            }
            catch (Exception ex) { receipt["processExitVerified"] = false; receipt["processWaitExceptionType"] = ex.GetType().FullName; }
            finally { _process.Dispose(); }
            receipt["completed"] = receipt.Value<bool?>("projectClosed") == true && receipt.Value<bool?>("portalDisposed") == true &&
                receipt.Value<bool>("processExitVerified");
            return receipt;
        }
    }
}
