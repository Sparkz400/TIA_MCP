using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Who holds a TIA project open, from the public TiaPortal.GetProcesses() view (process ID, mode and the
    /// project each Portal has open). TIA lets one Portal open a project primary at a time; this names the holder before
    /// Projects.Open fails with an unspecific native exception. A Portal that hides its project path is not visible here.</summary>
    internal static class ProjectLockRules
    {
        internal sealed class PortalView
        {
            internal int ProcessId;
            internal string Mode;
            internal string ProjectPath;
        }

        /// <summary>Other Portal processes whose open project is the requested .ap21 file (paths compared case-insensitively).</summary>
        internal static IReadOnlyList<PortalView> Holders(string projectFile, IEnumerable<PortalView> portals, int? ownProcessId)
        {
            string wanted = Normalize(projectFile);
            if (wanted == null) return Array.Empty<PortalView>();
            return portals.Where(p => p != null && p.ProcessId != ownProcessId && Normalize(p.ProjectPath) == wanted)
                .OrderBy(p => p.ProcessId).ToList();
        }

        internal static JObject Refusal(string projectFile, IReadOnlyList<PortalView> holders) => new JObject
        {
            ["projectPath"] = projectFile,
            ["lockOwners"] = new JArray(holders.Select(Owner)),
            ["remediation"] = "Another TIA Portal has this project open; TIA opens a project primary in one Portal at a time. Close it there (or "
                + "tia_attach to process " + holders[0].ProcessId + " to work in that session), open it read-only with readOnly:true, or use project_open_copy.",
        };

        internal static JObject Owner(PortalView holder) => new JObject
        {
            ["processId"] = holder.ProcessId, ["mode"] = holder.Mode, ["projectPath"] = holder.ProjectPath,
            ["userInterface"] = holder.Mode == null ? null : (JToken)(holder.Mode != "WithoutUserInterface"),
        };

        /// <summary>tia_session_status: every visible Portal, which one this worker uses, and the projects each holds.</summary>
        internal static JArray Portals(IEnumerable<PortalView> portals, int? ownProcessId, bool ownsPortal) => new JArray(portals
            .OrderBy(p => p.ProcessId).Select(p =>
            {
                var row = Owner(p);
                row["thisWorker"] = p.ProcessId == ownProcessId;
                row["ownedByThisWorker"] = p.ProcessId == ownProcessId && ownsPortal;
                return row;
            }));

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) { return null; }
        }
    }
}
