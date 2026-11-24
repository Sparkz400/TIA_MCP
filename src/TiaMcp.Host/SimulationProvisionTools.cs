using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "simulation_provision_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List credential-provisioning attempts (newest first, or one admissionId) with their stage (held, released, provisioned, notProvisioned), the scratch project, the owned storage and instance name, what that storage holds (download and provisioning claims, card, reports, last failure code) and the exact next calls back to a provisioned instance. Reads the Host's admission records and storage folders only; starts no worker and checks no PLCSIM registration (sim_status does).")]
    public CallToolResult SimulationProvisionStatus(string? admissionId = null, int maxItems = 10)
    {
        try { return Result(JsonSerializer.Serialize(SimulationProvisionRecovery.Status(options.ArtifactsDirectory, admissionId, maxItems), BridgeProtocol.Json), false); }
        catch (BridgeException error) { return Failure(error); }
    }

    [McpServerTool(Name = "simulation_provision_recover", Destructive = true, OpenWorld = false)]
    [Description("Recover a held (outcomeUnknown) credential-provisioning attempt in one call: tia_detach (refused with UnsavedProject while an open project has unsaved edits), tia_start, simulation_provision_reconcile for admissionId (a held result stops here), project_open of the scratch project. Stops at the first failure. Never powers, downloads, provisions, saves or replays; a new instance and provisioning attempt (with renewed user TLS authorization) are separate calls.")]
    public async Task<CallToolResult> SimulationProvisionRecover(string admissionId, CancellationToken cancellationToken)
    {
        try { return Result(JsonSerializer.Serialize(await SimulationProvisionRecovery.Recover(bridge, options.ArtifactsDirectory, admissionId, cancellationToken), BridgeProtocol.Json), false); }
        catch (BridgeException error) { return Failure(error); }
    }

}
