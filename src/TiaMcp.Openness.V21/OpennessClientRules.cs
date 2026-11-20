using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Which other Openness clients are attached to a TIA Portal, from the public TiaPortalProcess.AttachedSessions
    /// view. A client in the middle of a call (IsActive) has been reported to delay an attach until the call returns (not
    /// yet reproduced here); a client whose process has exited is reported as such. This only reports and waits: another
    /// client's session is never disconnected (TiaPortalSession.Dispose would do that) and no process is ended.</summary>
    internal static class OpennessClientRules
    {
        internal const int MaxWaitSeconds = 60;

        internal sealed class SessionView
        {
            internal int ProcessId;
            internal string ProcessPath;
            internal bool IsActive;
            internal DateTime AttachTimeUtc;
            internal double UtilizationSeconds;
            internal string AccessLevel;
            internal string TrustAuthority;
            internal string Version;
            // Filled from the Windows process table, outside the Siemens boundary; null when not checked.
            internal bool? ProcessRunning;
            internal string ProcessName;
        }

        /// <summary>waitForIdleSeconds: absent keeps the plain attach; 0..60 checks for a busy client first and waits that long.</summary>
        internal static int? WaitSeconds(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            if (value.Type != JTokenType.Integer || value.Value<long>() < 0 || value.Value<long>() > MaxWaitSeconds)
                throw new BridgeException("InvalidRequest", "waitForIdleSeconds must be an integer from 0 to " + MaxWaitSeconds + ".");
            return value.Value<int>();
        }

        /// <summary>The sessions of clients other than this worker, oldest first.</summary>
        internal static IReadOnlyList<SessionView> Others(IEnumerable<SessionView> sessions, int ownProcessId) =>
            (sessions ?? Enumerable.Empty<SessionView>()).Where(s => s != null && s.ProcessId != ownProcessId)
                .OrderBy(s => s.AttachTimeUtc).ThenBy(s => s.ProcessId).ToList();

        internal static IReadOnlyList<SessionView> Busy(IEnumerable<SessionView> others) => others.Where(s => s.IsActive).ToList();

        internal static JObject Row(SessionView session, DateTime nowUtc) => new JObject
        {
            ["processId"] = session.ProcessId,
            ["processName"] = session.ProcessName,
            ["processPath"] = session.ProcessPath,
            ["processRunning"] = session.ProcessRunning,
            ["isActive"] = session.IsActive,
            ["attachedAtUtc"] = session.AttachTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            ["attachedForSeconds"] = Math.Max(0, Math.Round((nowUtc - session.AttachTimeUtc).TotalSeconds)),
            ["utilizationSeconds"] = Math.Round(session.UtilizationSeconds, 1),
            ["accessLevel"] = session.AccessLevel,
            ["trustAuthority"] = session.TrustAuthority,
            ["version"] = session.Version,
        };

        internal static JArray Rows(IEnumerable<SessionView> sessions, DateTime nowUtc) => new JArray(sessions.Select(s => Row(s, nowUtc)));

        internal static JObject BusyRefusal(int processId, IReadOnlyList<SessionView> busy, IReadOnlyList<SessionView> others, int waitedSeconds, DateTime nowUtc)
        {
            var exited = others.Where(s => s.ProcessRunning == false).Select(s => s.ProcessId).ToList();
            string names = string.Join(", ", busy.Select(s => s.ProcessId + (s.ProcessName == null ? "" : " (" + s.ProcessName + ")")));
            return new JObject
            {
                ["processId"] = processId,
                ["waitedSeconds"] = waitedSeconds,
                ["busyClients"] = Rows(busy, nowUtc),
                ["attachedSessions"] = Rows(others, nowUtc),
                ["exitedClientProcessIds"] = new JArray(exited),
                ["remediation"] = "Another Openness client (process " + names + ") is in the middle of a call in this TIA Portal, and an attach may wait until that call returns. "
                    + "Let it finish and retry, retry with a longer waitForIdleSeconds (up to " + MaxWaitSeconds + "), or end that client yourself if it is stale. "
                    + "This tool never disconnects another client or ends its process.",
            };
        }

        internal static string BusyMessage(int processId, IReadOnlyList<SessionView> busy, int waitedSeconds) =>
            "TIA Portal process " + processId + " is processing a call from another Openness client (process "
            + string.Join(", ", busy.Select(s => s.ProcessId.ToString(CultureInfo.InvariantCulture)))
            + (waitedSeconds > 0 ? ") after waiting " + waitedSeconds + " s" : ")") + "; nothing was attached.";
    }
}
