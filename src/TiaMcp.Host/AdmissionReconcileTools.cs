using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "engineering_admission_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Release an unresolved mutation admission (outcomeUnknown, or dispatchIntent left by a Host that died) for simulation_download, change_apply, change_restore, scratch_create, project_retrieve, project_copy, library_open or credential provisioning, only on fresh evidence. In an empty worker-owned TIA instance started with tia_start, after the admitted TIA process has exited, every admitted resource must be proven unchanged or verifiedApplied: projects by fingerprint against retrieved checkpoint archives or their recorded outcome, or by not being saved since admission; archives, inputs and journals by hash or write time; new destinations absent, empty or a verified complete result. Otherwise the reservation stays held and the reasons are returned. Never replays, saves, powers or downloads.")]
    public Task<CallToolResult> EngineeringAdmissionReconcile(string admissionId, CancellationToken cancellationToken)
        => Call("engineering_admission_reconcile", new { admissionId }, cancellationToken);
}
