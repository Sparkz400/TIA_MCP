using System.Collections.Generic;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    /// <summary>
    /// Deliberately narrow boundary around TiaPortalProcess. Its Dispose method terminates TIA,
    /// unlike disposing an Openness TiaPortal attachment. Never put these process proxies in
    /// using statements, call Dispose, or expose them outside this class. Regression tests
    /// inspect this class's IL to reject disposal calls without running Siemens code.
    /// </summary>
    internal static class PortalProcesses
    {
        internal static IReadOnlyList<PortalProcessInfo> Discover()
        {
            var result = new List<PortalProcessInfo>();
            var processes = TiaPortal.GetProcesses();
            // Indexing avoids a generated enumerator Dispose, keeping this boundary disposal-free.
            for (int i = 0; i < processes.Count; i++)
            {
                var process = processes[i];
                result.Add(new PortalProcessInfo
                {
                    ProcessId = process.Id,
                    Mode = process.Mode.ToString(),
                    ExecutablePath = process.Path?.FullName,
                    ProjectPath = process.ProjectPath?.FullName
                });
            }
            return result;
        }

        internal static TiaPortal Attach(int processId)
        {
            var processes = TiaPortal.GetProcesses();
            for (int i = 0; i < processes.Count; i++)
            {
                var process = processes[i];
                if (process.Id == processId) return process.Attach();
            }
            throw new BridgeException("ProcessNotFound", "No attachable TIA V21 process has that processId.");
        }

        internal static int CurrentId(TiaPortal portal) => portal.GetCurrentProcess().Id;
    }

    internal sealed class PortalProcessInfo
    {
        internal int ProcessId;
        internal string Mode;
        internal string ExecutablePath;
        internal string ProjectPath;
    }
}
