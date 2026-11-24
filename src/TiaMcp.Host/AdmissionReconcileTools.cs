using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "engineering_admission_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Release an unresolved mutation admission only on independent native evidence; never replays, saves, powers or downloads. A completed hmi_classic_screen_edit is verified in the still-open original project (no TIA restart). Other operations need an empty worker-owned TIA, the original process exited and each resource unchanged or verifiedApplied. Incomplete or in-progress evidence stays held; ResourceBusy reports blockingAdmissionIds.")]
    public Task<CallToolResult> EngineeringAdmissionReconcile(string admissionId, CancellationToken cancellationToken)
        => Call("engineering_admission_reconcile", new { admissionId }, cancellationToken);
}
