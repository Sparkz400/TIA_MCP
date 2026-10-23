using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

/// <summary>Reconcile an explicitly mapped edited workbook into fresh unapproved proposals. Never writes or imports.</summary>
public static class TranslationReconciliation
{
    public static JsonObject Reconcile(TranslationWorkbook baseline, TranslationWorkbook edited, TranslationWorkbook current)
    {
        EngineeringIdentityGuard.EnsureFresh(baseline.Project, edited.Project);
        EngineeringIdentityGuard.EnsureSameObject(baseline.Project, current.Project);
        Require(baseline.Project.Snapshot.TiaVersion == current.Project.Snapshot.TiaVersion,
            "IdentityMismatch", "Reconciliation cannot cross captured TIA versions.");
        Require(baseline.ProfileSha256 == edited.ProfileSha256 && baseline.ProfileSha256 == current.ProfileSha256,
            "ReconciliationProfileMismatch", "Reconciliation requires the same explicit key, category, language and location map.");

        var changed = new List<(TranslationRow Row, TranslationCell Target, TextCellState Before, TextCellState Edited)>();
        foreach (var (row, target) in baseline.OrderedTargets)
        {
            var before = baseline.ReadText(target.Location);
            var proposed = edited.ReadText(target.Location);
            if (before == proposed) continue;
            Require(proposed.State != "missing", "RemovedTranslationCell", "Deleting a target cell is not an explicit clear operation.");
            var kind = proposed.State == "empty" ? TranslationEditKind.Clear : TranslationEditKind.Set;
            // Refuse flattened rich text and added opaque payload even though the candidate
            // will later be materialized from current, never from these edited package bytes.
            baseline.CheckWritable(row, target.Culture, kind, proposed.Text!);
            edited.CheckWritable(row, target.Culture, kind, proposed.Text!);
            changed.Add((row, target, before, proposed));
        }
        TranslationReview.VerifyPreservation(baseline, edited,
            changed.Select(c => (c.Target.Location, c.Edited.Text!)).ToArray());

        var rows = new JsonArray();
        var proposals = new JsonArray();
        int conflicts = 0, applied = 0;
        foreach (var change in changed)
        {
            var originalSource = baseline.ReadText(change.Row.SourceCell);
            var live = current.Read(change.Row.NativeKey, change.Target.Culture);
            string status;
            string? conflict = null;
            if (originalSource != live.Source) { status = "conflict"; conflict = "SourceTextConflict"; }
            else if (live.Target == change.Edited) { status = "already-applied"; applied++; }
            else if (live.Target != change.Before) { status = "conflict"; conflict = "TargetTextConflict"; }
            else
            {
                status = "proposed";
                var kind = change.Edited.State == "empty" ? TranslationEditKind.Clear : TranslationEditKind.Set;
                try { current.CheckWritable(change.Row, change.Target.Culture, kind, change.Edited.Text!); }
                catch (BridgeException ex) { status = "conflict"; conflict = ex.Code; }
                if (conflict is null)
                    proposals.Add(Node(new TranslationEdit(change.Row.NativeKey, change.Target.Culture, kind,
                        kind == TranslationEditKind.Set ? change.Edited.Text : null, live.Source.Sha256, live.Target.Sha256,
                        Rationale: "Reconciled from edited workbook " + edited.WorkbookSha256 + "; fresh review required.")));
            }
            if (conflict is not null) conflicts++;
            rows.Add(new JsonObject { ["nativeKey"] = change.Row.NativeKey, ["culture"] = change.Target.Culture,
                ["baseline"] = Node(change.Before), ["edited"] = Node(change.Edited), ["current"] = Node(live.Target),
                ["status"] = status, ["conflictCode"] = conflict });
        }
        var report = new JsonObject { ["schemaVersion"] = 1, ["kind"] = "translation-offline-reconciliation",
            ["baselineProject"] = Node(baseline.Project), ["currentProject"] = Node(current.Project),
            ["baselineWorkbookSha256"] = baseline.WorkbookSha256, ["editedWorkbookSha256"] = edited.WorkbookSha256,
            ["currentWorkbookSha256"] = current.WorkbookSha256, ["profileSha256"] = current.ProfileSha256,
            ["rows"] = rows, ["proposals"] = proposals, ["conflicts"] = conflicts, ["alreadyApplied"] = applied,
            ["canReviewProposals"] = conflicts == 0, ["requiresFreshReview"] = true,
            ["preservationVerified"] = true, ["nativeQualified"] = false, ["nativeExecuted"] = false, ["nativeImportAllowed"] = false };
        report["reconciliationSha256"] = CanonicalJson.Sha256(report);
        return report;
    }
}
