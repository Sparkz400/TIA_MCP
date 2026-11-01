using System.Text.Json.Nodes;

namespace TiaMcp.Core.EngineeringJobs;

/// <summary>
/// Releases a failed credential-provisioning admission only on fresh native evidence:
/// the project matches its pre-provision archive and was not saved after it, the card manifest
/// matches the original, the rest of the storage matches its pre-attempt inventory, the write-ahead
/// stream never armed the credential, and any discovery observer project is absent or empty.
/// Every other outcome keeps the reservation.
/// </summary>
public sealed record SimulationProvisionReconciliation(string AdmissionId, string ScopeSha256, int ProvisioningProcessId,
    string StorageKey, string ProjectKey, string ProjectPath, string? ObserverKey, string? ObserverPath, string ReportPath)
{
    public const string Operation = "simulation_provision_scratch_credential";

    public static SimulationProvisionReconciliation Plan(string artifacts, EngineeringMutationAdmission record)
    {
        if (record.Scope.Operation != Operation || record.State != "outcomeUnknown")
            throw new BridgeException("InvalidOperationState", "Only an outcomeUnknown credential-provisioning admission can be reconciled.");
        var resources = record.Scope.Resources;
        var storage = resources.Where(r => r.Kind == "workspace" && r.Effect == "ownedSimulatorStorageAndCard").ToArray();
        var project = resources.Where(r => r.Kind == "project" && r.Effect == "simulationCompilationOrCredentialPreparation").ToArray();
        var observer = resources.Where(r => r.Kind == "project" && r.Effect == "createNewDestination").ToArray();
        if (storage.Length != 1 || project.Length != 1 || observer.Length > 1 || storage.Length + project.Length + observer.Length != resources.Count)
            throw new BridgeException("ReconciliationUnsupported", "The admission scope is not a recognized provisioning scope.");
        string storagePath = Path.GetFullPath(storage[0].Path);
        _ = PackageStorage.Under(artifacts, PackageStorage.Relative(Path.GetRelativePath(artifacts, storagePath)));
        // The worker writes one report per attempt; only the one bound to this exact scope counts.
        var reports = Directory.Exists(storagePath)
            ? Directory.GetFiles(storagePath, "simulation-provision-*.json").Where(path => !path.EndsWith(".checkpoints.jsonl", StringComparison.Ordinal)).ToArray()
            : [];
        if (reports.Length > 16) throw new BridgeException("ReconciliationIncomplete", "Too many provisioning reports to select one exactly.");
        var bound = reports.Where(path => ScopeOf(path) == record.Scope.ScopeSha256).ToArray();
        if (bound.Length != 1) throw new BridgeException("ReconciliationIncomplete", "Exactly one provisioning report must belong to this admission.");
        string Key(EngineeringMutationResource r) => EngineeringResourceKey.ForPath(r.Kind, r.Path);
        return new(record.Id, record.Scope.ScopeSha256, record.Scope.ProcessId, Key(storage[0]), Key(project[0]), project[0].Path,
            observer.Length == 1 ? Key(observer[0]) : null, observer.Length == 1 ? observer[0].Path : null, bound[0]);
    }

    private static string? ScopeOf(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 16 * 1024 * 1024) return null;
        try { return JsonNode.Parse(File.ReadAllText(path))?["recoveryBaseline"]?["nativeScope"]?["scopeSha256"]?.GetValue<string>(); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public JsonObject NativeRequest(string retrieveDirectory) => new()
    {
        ["reportPath"] = ReportPath, ["projectPath"] = ProjectPath, ["observerProjectPath"] = ObserverPath,
        ["scopeSha256"] = ScopeSha256, ["retrieveDirectory"] = retrieveDirectory
    };

    /// <summary>Every reason the fresh observation does not prove a safe release; empty means release.</summary>
    public IReadOnlyList<string> Refusals(JsonObject verification)
    {
        var reasons = new List<string>();
        bool Is(string name) => verification[name]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        if (verification["scopeSha256"]?.GetValue<string>() != ScopeSha256) reasons.Add("Verification belongs to a different admission scope.");
        if (verification["processId"]?.GetValue<int?>() is not int process || process == ProvisioningProcessId)
            reasons.Add("Verification did not run in a fresh TIA session.");
        if (verification["nativeMutationInvoked"]?.GetValueKind() != System.Text.Json.JsonValueKind.False) reasons.Add("Verification must not invoke native mutation.");
        if (!Is("archiveHashMatches")) reasons.Add("The pre-provision project archive is missing or changed.");
        if (!Is("cardManifestMatches")) reasons.Add("The memory card manifest differs from the original.");
        if (!Is("projectFingerprintMatches")) reasons.Add("The project differs from its pre-provision archive.");
        if (!Is("projectSavedBeforeArchive")) reasons.Add("The project was saved after its pre-provision archive was written; an equal fingerprint does not prove nothing was persisted.");
        if (!Is("storageMatchesBaseline")) reasons.Add("The simulator storage differs from its pre-attempt inventory beyond this attempt's own files.");
        if (!Is("credentialNeverArmed")) reasons.Add("The write-ahead stream does not prove the credential was never submitted; the runtime protection store may hold it.");
        if (ObserverPath is not null && !Is("observerProjectEmpty") && !Is("observerProjectAbsent")) reasons.Add("The discovery observer project is not empty.");
        return reasons;
    }

    public IReadOnlyList<ResourceReconciliation> Dispositions(JsonObject verification, Func<string, string, EngineeringArtifactReference> evidence)
    {
        var rows = new List<ResourceReconciliation>
        {
            new(ProjectKey, "unchanged", null, evidence("project", "Fresh session fingerprint (engineering and project library) equals the retrieved pre-provision archive, and the project was last saved before that archive was written.")),
            new(StorageKey, "verifiedApplied", null, evidence("storage", "Memory card manifest equals the original generated card; every other pre-attempt entry is unchanged by metadata, the only new entries are this attempt's files and PLCSIM's runtime files (contents not read), and the write-ahead stream never opened or armed the credential."))
        };
        if (ObserverKey is not null)
            rows.Add(verification["observerProjectEmpty"]?.GetValueKind() == System.Text.Json.JsonValueKind.True
                ? new(ObserverKey, "verifiedApplied", null, evidence("observer", "Discovery observer project exists, opens and contains no device."))
                : new(ObserverKey, "unchanged", null, evidence("observer", "Discovery observer destination was never created.")));
        return rows;
    }
}
