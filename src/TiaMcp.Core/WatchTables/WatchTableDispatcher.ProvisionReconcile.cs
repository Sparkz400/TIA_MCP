using System.Text.Json.Nodes;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

public sealed partial class WatchTableDispatcher
{
    private async Task<object> ReconcileProvision(JsonObject args, CancellationToken token)
    {
        var admissions = new EngineeringMutationAdmissionStore(root);
        var record = admissions.Read(Text(args, "admissionId"));
        var plan = SimulationProvisionReconciliation.Plan(root, record);
        string retrieve = PackageStorage.Under(root, "pr_" + Guid.NewGuid().ToString("N").Substring(0, 12));
        var request = plan.NativeRequest(retrieve);
        var verification = Node(await native.CallAsync("simulation_provision_reconcile_verify", request, token));
        string directory = PackageStorage.Under(root, "provision-reconciliation/" + record.Id); Directory.CreateDirectory(directory);
        string attempt = Guid.NewGuid().ToString("N");
        var observed = Save(directory, attempt + ".json", new { admissionId = record.Id, request, verification, automaticReplay = false });
        var reasons = plan.Refusals(verification);
        if (reasons.Count > 0)
            return new { released = false, held = true, reasons, evidence = observed, admission = record, automaticReplay = false };
        var dispositions = plan.Dispositions((role, basis) =>
            Save(directory, attempt + "-" + role + ".json", new { admissionId = record.Id, role, basis, observation = observed }));
        var completed = admissions.Reconcile(record.Id, record.Revision, dispositions);
        return new { released = true, held = false, admission = completed, evidence = observed, automaticReplay = false };
    }
}
