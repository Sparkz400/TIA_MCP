using System;
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
        /// <summary>includeSessions reads each Portal's AttachedSessions too; only tia_discover asks for them, so status,
        /// lock and reconcile checks keep their previous native calls.</summary>
        internal static IReadOnlyList<PortalProcessInfo> Discover(bool includeSessions = false)
        {
            var result = new List<PortalProcessInfo>();
            var processes = TiaPortal.GetProcesses();
            // Indexing avoids a generated enumerator Dispose, keeping this boundary disposal-free.
            for (int i = 0; i < processes.Count; i++)
            {
                var process = processes[i];
                IList<TiaPortalSession> attached = null; string unavailable = null;
                if (includeSessions)
                {
                    try { attached = process.AttachedSessions; }
                    catch (Exception error) { unavailable = Describe(error); }
                }
                result.Add(new PortalProcessInfo
                {
                    ProcessId = process.Id,
                    Mode = process.Mode.ToString(),
                    ExecutablePath = process.Path?.FullName,
                    ProjectPath = process.ProjectPath?.FullName,
                    Sessions = includeSessions ? Sessions(attached, ref unavailable) : null,
                    SessionsUnavailable = unavailable
                });
            }
            return result;
        }

        /// <summary>The other Openness clients attached to one Portal, or null when the process is gone.</summary>
        internal static IReadOnlyList<OpennessClientRules.SessionView> AttachedSessions(int processId, out string unavailable)
        {
            unavailable = null;
            var processes = TiaPortal.GetProcesses();
            for (int i = 0; i < processes.Count; i++)
            {
                var process = processes[i];
                if (process.Id != processId) continue;
                IList<TiaPortalSession> attached = null;
                try { attached = process.AttachedSessions; }
                catch (Exception error) { unavailable = Describe(error); }
                return Sessions(attached, ref unavailable);
            }
            return null;
        }

        private static string Describe(Exception error) => error.GetType().Name + ": " + error.Message;

        // TiaPortalSession.Dispose disconnects that other client, so sessions are only read, by index, into plain views.
        private static IReadOnlyList<OpennessClientRules.SessionView> Sessions(IList<TiaPortalSession> sessions, ref string unavailable)
        {
            var result = new List<OpennessClientRules.SessionView>();
            try
            {
                for (int i = 0; sessions != null && i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    result.Add(new OpennessClientRules.SessionView
                    {
                        ProcessId = session.ProcessId,
                        ProcessPath = session.ProcessPath?.FullName,
                        IsActive = session.IsActive,
                        AttachTimeUtc = session.AttachTime.ToUniversalTime(),
                        UtilizationSeconds = session.UtilizationTime.TotalSeconds,
                        AccessLevel = session.AccessLevel.ToString(),
                        TrustAuthority = session.TrustAuthority.ToString(),
                        Version = session.Version
                    });
                }
            }
            catch (Exception error) { unavailable = unavailable ?? Describe(error); }
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
        internal IReadOnlyList<OpennessClientRules.SessionView> Sessions;
        internal string SessionsUnavailable;
    }
}
