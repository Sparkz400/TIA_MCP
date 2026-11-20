using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Other Openness clients of a Portal (OpennessClientRules): reported by tia_discover and tia_attach, and with
    // waitForIdleSeconds checked before the attach, so a client stuck in a call is named instead of blocking the attach.
    internal sealed partial class OpennessSession
    {
        private const int IdlePollMilliseconds = 500;

        private static readonly int WorkerProcessId = Process.GetCurrentProcess().Id;

        // The Windows process table says whether each client still runs; a client whose process exited is stale.
        private static IReadOnlyList<OpennessClientRules.SessionView> OtherClients(IReadOnlyList<OpennessClientRules.SessionView> sessions)
        {
            var others = OpennessClientRules.Others(sessions, WorkerProcessId);
            foreach (var session in others)
            {
                try
                {
                    using (var process = Process.GetProcessById(session.ProcessId))
                    {
                        session.ProcessRunning = !process.HasExited;
                        session.ProcessName = process.ProcessName;
                    }
                }
                catch (ArgumentException) { session.ProcessRunning = false; }
                catch (Exception) { session.ProcessRunning = null; }
            }
            return others;
        }

        private static void AddClients(JObject row, IReadOnlyList<OpennessClientRules.SessionView> sessions, string unavailable)
        {
            row["attachedSessions"] = sessions == null ? null : OpennessClientRules.Rows(OtherClients(sessions), DateTime.UtcNow);
            if (unavailable != null) row["attachedSessionsUnavailable"] = unavailable;
        }

        // Polls until no other client is in a call, or refuses with OpennessClientBusy when waitSeconds have passed.
        private static void WaitForIdlePortal(int processId, int waitSeconds)
        {
            var started = Stopwatch.StartNew();
            while (true)
            {
                var sessions = PortalProcesses.AttachedSessions(processId, out string unavailable)
                    ?? throw new BridgeException("ProcessNotFound", "No attachable TIA V21 process has that processId.");
                if (unavailable != null)
                    throw new BridgeException("OpennessSessionsUnavailable", "TIA did not report the Openness clients of process " + processId + " (" + unavailable + "); nothing was attached. Retry without waitForIdleSeconds to attach without the check.",
                        new JObject { ["processId"] = processId, ["nativeMessage"] = unavailable });
                var others = OtherClients(sessions);
                var busy = OpennessClientRules.Busy(others);
                if (busy.Count == 0) return;
                if (started.Elapsed.TotalSeconds >= waitSeconds)
                {
                    int waited = (int)Math.Round(started.Elapsed.TotalSeconds);
                    throw new BridgeException("OpennessClientBusy", OpennessClientRules.BusyMessage(processId, busy, waited),
                        OpennessClientRules.BusyRefusal(processId, busy, others, waited, DateTime.UtcNow));
                }
                Thread.Sleep(IdlePollMilliseconds);
            }
        }

        // A failed attach names the other clients beside TIA's message; the error code stays EngineeringError.
        private static Exception AttachFailure(int processId, Exception error)
        {
            if (error is BridgeException) return error;
            try
            {
                var sessions = PortalProcesses.AttachedSessions(processId, out _);
                var others = sessions == null ? null : OtherClients(sessions);
                if (others == null || others.Count == 0) return error;
                return new BridgeException("EngineeringError", error.Message, new JObject
                {
                    ["processId"] = processId,
                    ["nativeMessage"] = error.Message,
                    ["attachedSessions"] = OpennessClientRules.Rows(others, DateTime.UtcNow),
                    ["remediation"] = "Other Openness clients are attached to this TIA Portal (attachedSessions). If one is in a call (isActive) or its process has exited (processRunning:false), let it finish or end it yourself, then retry; waitForIdleSeconds checks for a busy client before attaching.",
                });
            }
            catch (Exception) { return error; }
        }
    }
}
