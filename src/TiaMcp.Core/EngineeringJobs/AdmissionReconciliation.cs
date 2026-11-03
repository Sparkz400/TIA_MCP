using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.EngineeringJobs;

/// <summary>
/// Trusted reconciliation of an unresolved shared mutation admission. A fresh worker-owned TIA session
/// reports one disposition per admitted resource. General recovery requires the original process to exit;
/// a terminal Classic screen journal has a stricter exclusive readback route in the still-open project.
/// Every resource must be unchanged or verified applied, with no mutation or replay by verification.
/// </summary>
public sealed record AdmissionReconciliation(EngineeringMutationAdmission Record, DateTimeOffset AdmittedAt)
{
    public static readonly IReadOnlyList<string> Operations =
        ["simulation_download", "change_apply", "change_restore", "scratch_create", "project_retrieve", "project_copy", "project_upgrade", "library_open"];

    public static AdmissionReconciliation Plan(EngineeringMutationAdmission record, DateTimeOffset admittedAt)
    {
        if (!Operations.Contains(record.Scope.Operation))
            throw new BridgeException("ReconciliationUnsupported", "No trusted reconciliation adapter exists for " + record.Scope.Operation + ".");
        // A dispatchIntent is left when the Host itself died; native verification must prove a terminal outcome.
        if (record.State is not ("outcomeUnknown" or "dispatchIntent"))
            throw new BridgeException("InvalidOperationState", "Only an unresolved (dispatchIntent or outcomeUnknown) admission can be reconciled.");
        return new(record, admittedAt);
    }

    public JsonObject NativeRequest(string retrieveDirectory) => new()
    {
        ["operation"] = Record.Scope.Operation, ["scopeSha256"] = Record.Scope.ScopeSha256,
        ["admittedProcessId"] = Record.Scope.ProcessId, ["admittedAtUtc"] = AdmittedAt.UtcDateTime.ToString("O"),
        ["observationSha256"] = Record.Scope.ObservationSha256, ["retrieveDirectory"] = retrieveDirectory,
        ["resources"] = JsonSerializer.SerializeToNode(Record.Scope.Resources, BridgeProtocol.Json)
    };

    /// <summary>Every reason the fresh observation does not prove a safe release; empty means release.</summary>
    public IReadOnlyList<string> Refusals(JsonObject verification)
    {
        var reasons = new List<string>();
        if (Value<string>(verification, "scopeSha256") != Record.Scope.ScopeSha256) reasons.Add("Verification belongs to a different admission scope.");
        bool openScreen = VerifiedOpenScreen(verification);
        if (Value<int>(verification, "processId") is var process && (process <= 0 || !openScreen && process == Record.Scope.ProcessId))
            reasons.Add("Verification did not run in a fresh TIA session.");
        if (!openScreen && verification["originalProcessRunning"]?.GetValueKind() != JsonValueKind.False)
            reasons.Add("The admitted TIA process may still be running; its dispatch could still be in progress.");
        if (verification["verificationMode"] is not null && !openScreen)
            reasons.Add("The in-place screen outcome lacks its exact terminal, artifact, exclusive-readback and saved-state proofs.");
        if (verification["nativeMutationInvoked"]?.GetValueKind() != JsonValueKind.False) reasons.Add("Verification must not invoke native mutation.");
        var rows = verification["resources"] as JsonArray ?? [];
        if (rows.Count != Record.Scope.Resources.Count) reasons.Add("Verification reported a different resource set than the admission.");
        foreach (var resource in Record.Scope.Resources)
        {
            var matches = Rows(rows, resource);
            if (matches.Length != 1) { reasons.Add($"No single fresh observation for {resource.Kind} {resource.Path}."); continue; }
            if (Disposition(matches[0]) is not null) continue;
            var held = (matches[0]["reasons"] as JsonArray)?.Select(r => r?.GetValue<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToArray() ?? [];
            reasons.Add($"{resource.Kind} {resource.Path} ({resource.Effect}): " + (held.Length == 0 ? "not proven unchanged or applied." : string.Join(" ", held)));
        }
        return reasons;
    }

    private bool VerifiedOpenScreen(JsonObject v) => Record.Scope.Operation == "change_apply"
        && Value<string>(v, "verificationMode") == "classic-screen-terminal-in-open-project-v1"
        && Value<string>(v, "changeKind") == "hmi_classic_screen_edit"
        && Value<string>(v, "terminalState") is ("applied" or "rolled_back")
        && Value<string>(v, "scopePolicy") == "classic-regular-screens-selected-hmi-v1"
        && new[] { "nativeExclusiveAccess", "retainedArtifactsVerified", "terminalJournalUnchanged", "savedProjectFingerprintMatches", "screenScopeMatches" }
            .All(p => v[p]?.GetValueKind() == JsonValueKind.True)
        && v["projectIsModified"]?.GetValueKind() == JsonValueKind.False
        && Record.Scope.Resources.Count == 3
        && Record.Scope.Resources.Count(r => r.Kind == "project" && r.Effect == "checkpointMutationCompileSave") == 1
        && Record.Scope.Resources.Count(r => r.Kind == "workspace" && r.Effect == "legacyJournalCheckpointOutput") == 1
        && Record.Scope.Resources.Count(r => r.Kind == "workspace" && r.Effect == "reviewedMutationInput") == 1;

    /// <summary>One disposition with its own evidence per admitted resource; call only when Refusals is empty.</summary>
    public IReadOnlyList<ResourceReconciliation> Dispositions(JsonObject verification, Func<int, EngineeringMutationResource, JsonNode, EngineeringArtifactReference> evidence)
    {
        var rows = verification["resources"] as JsonArray ?? [];
        return Record.Scope.Resources.Select((resource, index) =>
        {
            var row = Rows(rows, resource).Single();
            return new ResourceReconciliation(EngineeringResourceKey.ForPath(resource.Kind, resource.Path),
                Disposition(row) ?? throw new BridgeException("ReconciliationIncomplete", "Resource is not proven."), null, evidence(index, resource, row.DeepClone()));
        }).ToArray();
    }

    private static JsonObject[] Rows(JsonArray rows, EngineeringMutationResource resource) => rows.OfType<JsonObject>()
        .Where(r => Value<string>(r, "kind") == resource.Kind && Value<string>(r, "path") == resource.Path && Value<string>(r, "effect") == resource.Effect).ToArray();
    private static string? Disposition(JsonObject row) => Value<string>(row, "disposition") is "unchanged" or "verifiedApplied" ? Value<string>(row, "disposition") : null;
    private static T? Value<T>(JsonObject node, string name)
    {
        try { return node[name] is JsonValue value && value.TryGetValue<T>(out var result) ? result : default; }
        catch (InvalidOperationException) { return default; }
    }
}
