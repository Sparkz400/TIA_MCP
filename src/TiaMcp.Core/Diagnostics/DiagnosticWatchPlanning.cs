using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.WatchTables;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Connects immutable diagnostic cases to the existing pure watch planner.</summary>
public sealed class DiagnosticWatchPlanning(string artifactsRoot)
{
    public JsonObject Plan(string planId, JsonObject request)
    {
        var envelope = Build(planId, request);
        var receipt = new DiagnosticStore(artifactsRoot).Save(envelope);
        var watch = envelope["watchPlan"]!;
        receipt["caseId"] = planId; receipt["reviewId"] = envelope["reviewId"]!.DeepClone();
        receipt["planSha256"] = watch["planSha256"]!.DeepClone();
        receipt["rowCount"] = watch["rows"]!.AsArray().Count;
        receipt["candidateCount"] = watch["candidateCount"]!.DeepClone();
        receipt["unresolvedCount"] = watch["unresolvedSuggestions"]!.AsArray().Count;
        receipt["truncated"] = watch["truncated"]!.DeepClone();
        foreach (string key in new[] { "tiaVersionOrigin", "sourceAuthenticityVerified", "sourceEvidencePolicy", "nativeAccessAttempted", "nativeExecutable", "nativeDocumentGenerated", "qualification" })
            receipt[key] = envelope[key]!.DeepClone();
        return receipt;
    }

    internal JsonObject Build(string planId, JsonObject request)
    {
        D.Bound(request);
        if (request.Any(p => p.Key is not ("tableName" or "tiaVersion" or "maxRows" or "externalStorage")))
            throw new BridgeException("InvalidRequest", "Unknown watch planning request field.");
        string tableName = D.Required(request, "tableName", 128);
        if (request["tiaVersion"] is not JsonValue version || !version.TryGetValue<int>(out int tiaVersion) || tiaVersion is < 1 or > 100)
            throw new BridgeException("InvalidRequest", "Explicit caller-supplied tiaVersion (1..100) is required; it is not verified product compatibility.");
        int maxRows = 64;
        if (request.ContainsKey("maxRows") && (request["maxRows"] is not JsonValue budget || !budget.TryGetValue<int>(out maxRows) || maxRows is < 1 or > 64))
            throw new BridgeException("InvalidRequest", "maxRows must be 1..64.");
        var store = new DiagnosticStore(artifactsRoot);
        var diagnostic = store.Read(planId, "diagnostic-case.v1");
        string reviewId = D.Required(diagnostic, "reviewId", 64);
        var review = new ProjectReviewStore(artifactsRoot).Read(reviewId);
        var evidence = DiagnosticPlanner.Revalidate(diagnostic, review);
        DiagnosticAlarmBindings.RequireMatched(diagnostic);
        if (diagnostic["selectedInstance"] is not JsonObject instance || diagnostic["signals"] is not JsonArray signals || signals.Count == 0)
            throw new BridgeException("InstanceResolutionIncomplete", "Select an exact captured instance and at least one justified signal before watch planning.");
        string projectPath = D.Text(diagnostic["project"], "path");
        if (projectPath.Length == 0) projectPath = D.Required(diagnostic["project"]!, "projectPath", 8192);
        var snapshot = new EngineeringSnapshotIdentity(reviewId, reviewId, tiaVersion);
        EngineeringObjectReference Reference(string path)
        {
            if (!evidence.Nodes.TryGetValue(path, out var node))
                throw new BridgeException("ObjectNotFound", "Watch storage/source must identify an exact captured object.");
            evidence.Documents.TryGetValue(path, out var document);
            string? content = D.Text(document, "status") == "exported" ? D.Required(document!, "utf8Sha256", 64) : null;
            return new(new(projectPath, node["plcPath"]?.GetValue<string>(), node["scopePath"]?.GetValue<string>(), path, D.Required(node, "kind", 64)), snapshot, content);
        }
        var source = Reference(D.Required(diagnostic, "blockPath"));
        var storage = Reference(D.Required(instance, "dbPath"));
        var external = new List<WatchTableExternalStorage>();
        if (request.ContainsKey("externalStorage") && request["externalStorage"] is not JsonArray)
            throw new BridgeException("InvalidRequest", "externalStorage must be an array of exact paths and reasons.");
        var allowances = D.Rows(request["externalStorage"]);
        if (allowances.Count > 64) throw new BridgeException("InvalidRequest", "At most 64 external storage allowances.");
        foreach (var row in allowances)
        {
            if (row is not JsonObject allowance || allowance.Any(p => p.Key is not ("objectPath" or "reason")))
                throw new BridgeException("InvalidRequest", "Each external storage allowance needs objectPath and reason only.");
            string path = D.Required(allowance, "objectPath"), reason = D.Required(allowance, "reason", 2000);
            if (!signals.Any(s => D.Text(s, "storagePath") == path))
                throw new BridgeException("InvalidRequest", "External allowance is unused by this diagnostic case.");
            external.Add(new(Reference(path), reason));
        }
        var members = instance["memberPath"] is JsonArray { Count: 0 } ? [] : D.Path(instance["memberPath"]);
        var watch = WatchTablePlanner.Plan(new WatchTablePlanningRequest {
            ExpectedSource = source, CapturedSource = source, Instance = new(storage, members),
            TableName = tableName, MaxRows = maxRows, DiagnosticCase = diagnostic,
            DiagnosticCaseSha256 = CanonicalJson.Sha256(diagnostic), ExternalStorage = external });
        var envelope = new JsonObject {
            ["schemaVersion"] = "diagnostic-watch-plan.v1", ["caseId"] = planId, ["reviewId"] = reviewId,
            ["tiaVersionOrigin"] = "callerSuppliedUnverified", ["sourceAuthenticityVerified"] = false,
            ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy,
            ["nativeAccessAttempted"] = false, ["nativeExecutable"] = false, ["nativeDocumentGenerated"] = false,
            ["qualification"] = "offlinePlanningOnly", ["watchPlan"] = watch };
        if (diagnostic.ContainsKey("alarmEvidence"))
        {
            if (diagnostic["alarmEvidence"] is not JsonObject alarm) throw new BridgeException("CaptureMismatch", "Malformed retained alarm context.");
            envelope["alarmEvidence"] = alarm.DeepClone();
        }
        if (diagnostic.ContainsKey("alarmBindings")) envelope["alarmBindings"] = diagnostic["alarmBindings"]!.DeepClone();
        return envelope;
    }
}
