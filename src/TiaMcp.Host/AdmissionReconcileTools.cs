using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "engineering_admission_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Release an unresolved mutation admission only on independent native evidence; never replay, save, power or download. For a completed hmi_classic_screen_edit (applied or verified rolled_back), attach to the still-open original project: verifies its terminal journal, checkpoint/input/export hashes, every regular screen and exact saved-state fingerprint under native exclusive access. No TIA restart is required for that route. An in-progress operation, changed project, failed rollback or incomplete evidence remains held. Other supported operations require an empty worker-owned TIA instance and the original process to have exited; each admitted resource must be independently unchanged or verifiedApplied. ResourceBusy reports blockingAdmissionIds. Returns release/hold reasons and retained evidence.")]
    public Task<CallToolResult> EngineeringAdmissionReconcile(string admissionId, CancellationToken cancellationToken)
        => Call("engineering_admission_reconcile", new { admissionId }, cancellationToken);
}
