using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Independent artifact comparison; a supplied observation never becomes a live API probe.</summary>
public sealed class TraceReadback(string artifactsRoot)
{
    public JsonObject Check(string tracePlanId, JsonObject artifact)
    {
        D.Required(artifact, "sha256", 64);
        var observed = JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, DiagnosticStore.MaxBytes))?.AsObject()
            ?? throw new BridgeException("InvalidRequest", "Expected a readback JSON object.");
        if (D.Text(observed, "schemaVersion") != "trace-readback.v1") throw new BridgeException("InvalidRequest", "Expected trace-readback.v1.");
        var store = new DiagnosticStore(artifactsRoot); var plan = store.Read(tracePlanId, "trace-plan.v1");
        if (D.Text(observed, "tracePlanId") != tracePlanId || D.Text(observed, "reviewId") != D.Text(plan, "reviewId"))
            throw new BridgeException("TracePlanStale", "Readback must name the exact trace plan and source review.");
        D.Required(observed, "nativeTraceIdentity", 4096); D.Required(observed, "targetIdentity", 4096);
        D.Required(observed, "observationMethod", 2000); D.Required(observed, "programIdentity", 2000);
        var proofs = observed["evidenceArtifacts"] as JsonArray;
        if (proofs is null || proofs.Count is < 1 or > 16) throw new BridgeException("InvalidRequest", "Supply 1..16 hashed host-owned readback evidence artifacts.");
        foreach (var proof in proofs.OfType<JsonObject>()) { D.Required(proof, "sha256", 64); _ = ProjectReviewStore.ReadArtifact(artifactsRoot, proof, 32 * 1024 * 1024); }
        if (proofs.Any(p => p is not JsonObject)) throw new BridgeException("InvalidRequest", "Invalid evidence artifact.");
        var differences = new JsonArray();
        foreach (var group in new[] { (Name: "project", Fields: new[] { "projectPath", "host" }),
            (Name: "target", Fields: new[] { "plcPath", "scopePath", "blockPath", "dbPath", "memberPath", "namespace" }),
            (Name: "settings", Fields: new[] { "task", "sampleEvery", "preSamples", "postSamples", "trigger" }) })
        {
            if (observed[group.Name] is not JsonObject) throw new BridgeException("InvalidRequest", "Readback " + group.Name + " is required.");
            foreach (string field in group.Fields)
                if (!JsonNode.DeepEquals(observed[group.Name]?[field], plan[group.Name == "settings" ? "requestedSettings" : group.Name]?[field]))
                    differences.Add(new JsonObject { ["field"] = group.Name + "." + field, ["reason"] = "Requested identity/settings differ from supplied readback." });
        }
        var expected = D.Rows(plan["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var actual = observed["signals"] as JsonArray ?? throw new BridgeException("InvalidRequest", "Readback signals are required.");
        if (actual.Count > 64 || actual.Select(s => D.Text(s, "id")).Distinct(StringComparer.Ordinal).Count() != actual.Count)
            throw new BridgeException("InvalidRequest", "Readback signal IDs must be unique and bounded to 64.");
        foreach (var signal in actual)
        {
            string id = D.Required(signal!, "id", 64);
            if (!expected.TryGetValue(id, out var wanted)) { differences.Add(new JsonObject { ["signalId"] = id, ["reason"] = "Unexpected signal." }); continue; }
            foreach (string field in new[] { "storagePath", "memberPath", "datatype", "namespace", "scopePath" })
                if (!JsonNode.DeepEquals(signal![field], wanted[field])) differences.Add(new JsonObject { ["signalId"] = id, ["field"] = field, ["reason"] = "Signal identity/type differs." });
            if (signal!["observable"]?.GetValue<bool>() != true) differences.Add(new JsonObject { ["signalId"] = id, ["reason"] = "Signal observability was not positively observed." });
        }
        foreach (string missing in expected.Keys.Except(actual.Select(s => D.Text(s, "id")), StringComparer.Ordinal))
            differences.Add(new JsonObject { ["signalId"] = missing, ["reason"] = "Requested signal missing from readback." });
        if (observed["programMatchVerified"]?.GetValue<bool>() != true)
            differences.Add(new JsonObject { ["field"] = "programMatchVerified", ["reason"] = "Online/offline program match remains unverified by the observer." });
        var limits = observed["limits"] as JsonObject ?? throw new BridgeException("InvalidRequest", "Readback requires observed target limits.");
        foreach (string field in new[] { "maxSignals", "maxSamples", "minSampleEvery", "maxSampleEvery", "freeSlotsBefore", "requiredSampleCapacity" })
            if (limits[field] is not JsonValue value || !value.TryGetValue<long>(out long n) || n < 0)
                throw new BridgeException("InvalidRequest", "Observed nonnegative integer target limit required: " + field);
        var settings = plan["requestedSettings"]!;
        D.Required(limits, "sampleWindowSemantics", 2000);
        long count = limits["requiredSampleCapacity"]!.GetValue<long>();
        if (count < Math.Max(settings["preSamples"]!.GetValue<int>(), settings["postSamples"]!.GetValue<int>()))
            differences.Add(new JsonObject { ["field"] = "requiredSampleCapacity", ["reason"] = "Supplied capacity is smaller than a requested window component; reconcile native trigger/sample counting semantics." });
        if (limits["maxSignals"]!.GetValue<long>() < expected.Count || limits["maxSamples"]!.GetValue<long>() < count
            || limits["freeSlotsBefore"]!.GetValue<long>() < 1)
            differences.Add(new JsonObject { ["field"] = "limits", ["reason"] = "Requested signals/sample window or new trace slot exceeds supplied resource limits; preserve existing traces." });
        long reduction = settings["sampleEvery"]!.GetValue<int>();
        if (reduction < limits["minSampleEvery"]!.GetValue<long>() || reduction > limits["maxSampleEvery"]!.GetValue<long>())
            differences.Add(new JsonObject { ["field"] = "sampleEvery", ["reason"] = "Requested sampling reduction exceeds supplied limits." });
        if (observed["triggerSupported"]?.GetValue<bool>() != true || observed["samplingTaskVerified"]?.GetValue<bool>() != true)
            differences.Add(new JsonObject { ["field"] = "acquisition", ["reason"] = "Trigger/task support was not positively observed." });
        var report = new JsonObject { ["schemaVersion"] = "trace-readback-check.v1", ["tracePlanId"] = tracePlanId, ["readbackArtifact"] = artifact.DeepClone(),
            ["matchesSuppliedReadback"] = differences.Count == 0, ["differences"] = differences, ["observation"] = observed,
            ["nativeProbePerformed"] = false, ["nativeQualification"] = false, ["mutationAttempted"] = false,
            ["limits"] = "Checks independent artifact fields, not authenticity or live target state. Sample-window semantics and CPU effects remain observer/target responsibilities; this receipt never authorizes transfer/arming or proves capture." };
        var receipt = store.Save(report); receipt["readbackCheck"] = report; return receipt;
    }
}
