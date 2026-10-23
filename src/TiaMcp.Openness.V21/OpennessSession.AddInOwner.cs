#nullable disable
using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly string _addinWorkerInstanceId = Guid.NewGuid().ToString("N");
        private CapturedProcessIdentity _portalProcessIdentity;

        private JObject AddInOwnerContext(JObject args)
        {
            if (args.Count != 0) throw new BridgeException("InvalidRequest", "Owner context accepts no caller-selected identity.");
            JObject context;
            if (_portal == null || !_processId.HasValue || _portalProcessIdentity == null)
                context = new JObject { ["state"] = "unavailable", ["reason"] = "NotAttached",
                    ["process"] = null, ["osProcessIdentityVerified"] = false };
            else context = _portalProcessIdentity.Observe(PortalProcesses.CurrentId(_portal));
            using (var worker = Process.GetCurrentProcess()) context["workerProcessId"] = worker.Id;
            context["schemaVersion"] = "addin-owner-context.v1";
            context["workerInstanceId"] = _addinWorkerInstanceId;
            context["sessionGeneration"] = _modelSessionGeneration;
            context["ownsPortalProcess"] = _ownsPortal;
            // A matching native PID is an observation, not attestation of the loaded Add-In or selection.
            context["nativeProcessIdMatched"] = context.Value<bool>("osProcessIdentityVerified");
            context["producerAuthenticated"] = false;
            context["liveSelectionVerified"] = false;
            context["nativeQualified"] = false;
            return context;
        }
    }
}
