using System.Text.Json.Nodes;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

public sealed partial class WatchTableDispatcher
{
    private async Task<object> ReconcileAdmission(JsonObject args, CancellationToken token)
    {
        var admissions = new EngineeringMutationAdmissionStore(root);
        string id = Text(args, "admissionId");
        var record = admissions.Read(id);
        if (record.Scope.Operation == SimulationProvisionReconciliation.Operation) return await ReconcileProvision(args, token);
        var plan = AdmissionReconciliation.Plan(record, admissions.AdmittedAt(id));
        string retrieve = PackageStorage.Under(root, "ar_" + Guid.NewGuid().ToString("N").Substring(0, 12));
        var request = plan.NativeRequest(retrieve);
        var verification = Node(await native.CallAsync("engineering_admission_reconcile_verify", request, token));
        string directory = PackageStorage.Under(root, "admission-reconciliation/" + record.Id); Directory.CreateDirectory(directory);
        string attempt = Guid.NewGuid().ToString("N");
        var observed = Save(directory, attempt + ".json", new { admissionId = record.Id, request, verification, automaticReplay = false });
        var reasons = plan.Refusals(verification);
        if (reasons.Count > 0)
            return new { released = false, held = true, reasons, evidence = observed, admission = record, automaticReplay = false };
        var dispositions = plan.Dispositions(verification, (index, resource, row) =>
            Save(directory, $"{attempt}-r{index:D2}.json", new { admissionId = record.Id, resource, observation = row, verification = observed }));
        var completed = admissions.Reconcile(record.Id, record.Revision, dispositions);
        return new { released = true, held = false, admission = completed, evidence = observed, automaticReplay = false };
    }
}
