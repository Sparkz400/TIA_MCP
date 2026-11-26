using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.EngineeringJobs;

/// <summary>
/// Where each credential-provisioning attempt stands and the exact calls that lead back to a provisioned owned instance.
/// Status reads only the Host's admission records and the owned storage folders they name; it starts no worker.
/// Recover runs only the steps that need no new decision: detach, an empty owned Portal, the evidence-checked reconciliation
/// and reopening the scratch project. A fresh instance, its download and a new provisioning attempt (with a new TLS
/// authorization) stay with the caller.
/// </summary>
public static class SimulationProvisionRecovery
{
    public const string Operation = "simulation_provision_scratch_credential";
    private const string ProjectEffect = "simulationCompilationOrCredentialPreparation";
    private const string StorageEffect = "ownedSimulatorStorageAndCard";

    public static JsonObject Status(string artifactRoot, string? admissionId = null, int maxItems = 10)
    {
        if (maxItems is < 1 or > 50) throw new BridgeException("InvalidRequest", "maxItems must be 1..50.");
        var store = new EngineeringMutationAdmissionStore(artifactRoot);
        var records = admissionId is null ? store.List(Operation) : [store.Read(admissionId)];
        if (records.Any(r => r.Scope.Operation != Operation))
            throw new BridgeException("InvalidRequest", "The admission is not a credential-provisioning attempt; use engineering_operation_status.");
        var ordered = records.OrderByDescending(r => r.RecordedAt).ToArray();
        var attempts = new JsonArray(ordered.Take(maxItems).Select(r => (JsonNode)Attempt(r)).ToArray());
        return new JsonObject
        {
            ["attempts"] = attempts,
            ["totalAttempts"] = ordered.Length,
            ["truncated"] = ordered.Length > maxItems,
            ["held"] = ordered.Count(r => r.State is "dispatchIntent" or "outcomeUnknown"),
            ["note"] = "Read from the Host's admission records and the owned storage folders only; the PLCSIM registration of an instance is not checked here (sim_status lists it). A new provisioning attempt needs the user's TLS authorization again.",
        };
    }

    internal static JsonObject Attempt(EngineeringMutationAdmission record)
    {
        string? project = record.Scope.Resources.FirstOrDefault(x => x.Kind == "project" && x.Effect == ProjectEffect)?.Path;
        string? storage = record.Scope.Resources.FirstOrDefault(x => x.Kind == "workspace" && x.Effect == StorageEffect)?.Path;
        var files = Storage(storage);
        string stage = record.State switch
        {
            "dispatchIntent" or "outcomeUnknown" => "held",
            "reconciled" => "released",
            _ => files["lastAttemptCompleted"]?.GetValue<bool>() == true ? "provisioned" : "notProvisioned",
        };
        string? instanceName = storage is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(storage));
        return new JsonObject
        {
            ["admissionId"] = record.Id,
            ["admissionState"] = record.State,
            ["recordedAtUtc"] = record.RecordedAt.UtcDateTime.ToString("O"),
            ["stage"] = stage,
            ["projectPath"] = project,
            ["storagePath"] = storage,
            ["instanceName"] = instanceName,
            ["storage"] = files,
            ["nextSteps"] = NextSteps(stage, record.Id, project, instanceName),
        };
    }

    private static JsonObject Storage(string? storage)
    {
        var result = new JsonObject { ["exists"] = storage is not null && Directory.Exists(storage) };
        if (storage is null || !Directory.Exists(storage)) return result;
        bool Has(string name) => File.Exists(Path.Combine(storage, name)) || Directory.Exists(Path.Combine(storage, name));
        result["downloadClaimed"] = Has(".download.claim");
        result["generatedCard"] = Has("SIMATIC_MC");
        result["downloadReport"] = Has("simulation-download.json");
        result["provisionClaimed"] = Has(".provision.claim");
        result["projectCheckpoint"] = Has("before-provision.zap21");
        var reports = Directory.GetFiles(storage, "simulation-provision-*.json").Where(f => !f.EndsWith(".checkpoints.jsonl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(File.GetLastWriteTimeUtc).ToArray();
        result["provisionReports"] = reports.Length;
        if (reports.Length > 0)
        {
            result["lastReport"] = reports[^1];
            try
            {
                var report = JsonNode.Parse(File.ReadAllText(reports[^1])) as JsonObject;
                result["lastAttemptCompleted"] = report?["completed"] is JsonValue completed && completed.TryGetValue<bool>(out bool done) && done;
                if (report?["failure"]?["code"] is JsonValue code) result["lastFailureCode"] = code.DeepClone();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { result["lastReportError"] = e.Message; }
        }
        return result;
    }

    private static JsonObject Step(string tool, object? arguments, string why)
        => new() { ["tool"] = tool, ["arguments"] = arguments is null ? null : JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json), ["why"] = why };

    private static JsonArray NextSteps(string stage, string admissionId, string? project, string? instanceName)
    {
        var steps = new JsonArray();
        if (stage == "provisioned")
        {
            steps.Add(Step("sim_state", new { action = "powerOn" }, "Provisioned and returned OFF; power on the owned instance (instanceId from sim_status), then run."));
            return steps;
        }
        if (stage == "held")
        {
            steps.Add(Step("simulation_provision_recover", new { admissionId },
                "Runs tia_detach, tia_start, simulation_provision_reconcile and project_open of the scratch project; refused while any open project has unsaved edits. Or run those four calls yourself."));
        }
        else if (stage == "released" && project is not null)
            steps.Add(Step("project_open", new { path = project }, "Skip when the scratch project is already open in a worker-owned instance."));
        steps.Add(Step("sim_status", null, "Find the instanceId of " + (instanceName ?? "the failed instance") + "."));
        steps.Add(Step("sim_release", new { instanceId = "<from sim_status>", deleteOwned = true }, "An attempted instance is never provisioned again; its storage files are kept."));
        steps.Add(Step("sim_create", null, "A fresh owned instance and its downloadTicket (valid 15 minutes)."));
        steps.Add(Step("simulation_download", new { downloadTicket = "<new ticket>" }, "Generate the card for the fresh instance, with the scratch credentialMetadataPath."));
        steps.Add(Step("project_save", null, "Only when simulation_download reports saveRequiredBeforeProvisioning."));
        steps.Add(Step("simulation_provision_scratch_credential", new { downloadTicket = "<new ticket>" },
            "Needs the user's TLS authorization again (trustOwnedEmptyCpuCertificate): the fresh instance presents a different certificate."));
        return steps;
    }

    /// <summary>Detach, start an empty owned Portal, reconcile the held admission and reopen its scratch project. Stops at the first
    /// failure or a held reconciliation and reports every step; it never powers, downloads, provisions or saves.</summary>
    public static async Task<JsonObject> Recover(IBridgeClient bridge, string artifactRoot, string admissionId, CancellationToken token)
    {
        var store = new EngineeringMutationAdmissionStore(artifactRoot);
        var record = store.Read(admissionId);
        if (record.Scope.Operation != Operation)
            throw new BridgeException("InvalidRequest", "The admission is not a credential-provisioning attempt; use engineering_admission_reconcile.");
        if (record.State is not ("dispatchIntent" or "outcomeUnknown"))
            throw new BridgeOperationException("InvalidOperationState", "The admission is " + record.State + ", not held; nothing to recover. simulation_provision_status lists the next steps.",
                Details(Attempt(record)));
        string? project = record.Scope.Resources.FirstOrDefault(x => x.Kind == "project" && x.Effect == ProjectEffect)?.Path;
        var steps = new JsonArray();
        var result = new JsonObject { ["admissionId"] = admissionId, ["steps"] = steps, ["recovered"] = false, ["automaticReplay"] = false };

        var status = JsonNode.Parse((await bridge.CallAsync("session_status", null, token)).GetRawText()) as JsonObject;
        var unsaved = (status?["projects"] as JsonArray)?.OfType<JsonObject>().Where(p => p["isModified"]?.GetValue<bool>() == true)
            .Select(p => (JsonNode)(p["path"]?.GetValue<string>() ?? p["name"]?.GetValue<string>() ?? "")).ToArray() ?? [];
        if (unsaved.Length > 0)
            throw new BridgeOperationException("UnsavedProject", "tia_detach would discard unsaved edits in the open projects. Save or close them first; nothing was changed.",
                Details(new JsonObject { ["unsavedProjects"] = new JsonArray(unsaved) }));

        async Task<JsonNode?> Run(string tool, string operation, object? arguments)
        {
            var step = new JsonObject { ["tool"] = tool };
            steps.Add(step);
            try
            {
                var value = JsonNode.Parse((await bridge.CallAsync(operation, arguments, token)).GetRawText());
                step["ok"] = true;
                return value;
            }
            catch (BridgeException error)
            {
                step["ok"] = false; step["code"] = error.Code; step["message"] = error.Message;
                result["stoppedAt"] = tool;
                return null;
            }
        }

        if (status?["attached"]?.GetValue<bool>() == true && await Run("tia_detach", "detach", new { waitForExitSeconds = 30, terminateOwnedIfRunning = false }) is null) return Finish();
        if (await Run("tia_start", "start", null) is null) return Finish();
        var reconcile = await Run("simulation_provision_reconcile", "simulation_provision_reconcile", new { admissionId });
        if (reconcile is null) return Finish();
        ((JsonObject)steps[^1]!)["released"] = reconcile["released"]?.DeepClone();
        if (reconcile["released"]?.GetValue<bool>() != true)
        {
            result["stoppedAt"] = "simulation_provision_reconcile";
            result["held"] = true;
            result["reasons"] = reconcile["reasons"]?.DeepClone();
            return Finish();
        }
        if (project is not null && await Run("project_open", "project_open", new { path = project, readOnly = false }) is null) return Finish();
        result["recovered"] = true;
        return Finish();

        JsonObject Finish()
        {
            result["status"] = Attempt(store.Read(admissionId));
            return result;
        }
    }

    private static JsonElement Details(JsonObject value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
}
