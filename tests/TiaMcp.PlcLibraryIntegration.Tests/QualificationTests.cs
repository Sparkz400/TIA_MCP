using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class QualificationTests
{
    internal static void Run(Action<string, Action> test)
    {
        void Reject(string code, Action action) { try { action(); } catch (PlcComponentException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
        test("qualification launch flag is mandatory even in an owned scratch", () => Reject("ComponentQualificationDisabled", () => PlcComponentQualificationGate.RequireOwned(false, true, true, 1)));
        foreach (var row in new[] { (false, true, 1), (true, false, 1), (true, true, 0), (true, true, 2) })
            test("qualification rejects ownership boundary " + row, () => Reject("OwnedScratchRequired", () => PlcComponentQualificationGate.RequireOwned(true, row.Item1, row.Item2, row.Item3)));
        test("qualification allows exact owned single scratch with explicit flag", () => PlcComponentQualificationGate.RequireOwned(true, true, true, 1));
        foreach (string state in new[] { "library_prepared", "graphical_prepared", "dispatching_library", "dispatching_graphical", "outcome_unknown", "recovery_required" })
            test("read-only preparation cannot dispatch from " + state, () => Reject("ComponentStepReviewRequired", () => PlcComponentQualificationGate.RequireReadOnlyAdvance(state)));
        foreach (string state in new[] { "created", "library_applied", "import_verified", "graphical_applied", "installation_verified" })
            test("read-only advancement permits " + state, () => PlcComponentQualificationGate.RequireReadOnlyAdvance(state));
        var record = JObject.Parse("{state:'graphical_prepared',graphical:{prepared:{previewHash:'exact-fresh-hash'}}}");
        JObject Request() => JObject.Parse("{previewHash:'exact-fresh-hash',idempotencyKey:'review',checkpointSave:true,compile:true,save:true}");
        test("qualification apply requires freshly reviewed step hash", () => { var a = Request(); a["previewHash"] = "old-import-hash"; Reject("ComponentStepReviewRequired", () => PlcComponentQualificationGate.RequireApply(record, a)); });
        foreach (string field in new[] { "checkpointSave", "compile", "save" })
            test("qualification requires true Boolean " + field, () => { var a = Request(); a[field] = "true"; Reject("InvalidRequest", () => PlcComponentQualificationGate.RequireApply(record, a)); });
        foreach (string state in new[] { "library_applied", "graphical_applied", "outcome_unknown", "dispatching_library" })
            test("qualification apply never replays " + state, () => { var r = (JObject)record.DeepClone(); r["state"] = state; Reject("ComponentOutcomeUnknown", () => PlcComponentQualificationGate.RequireApply(r, Request())); });
        test("qualification accepts exact fresh approval and persistence options", () => PlcComponentQualificationGate.RequireApply(record, Request()));
    }
}
