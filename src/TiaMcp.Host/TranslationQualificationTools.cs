using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Translations;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Host;

// The unchanged executable exposes these tools only in explicit local
// qualification mode. Normal registration never includes this tool class.
[McpServerToolType]
public sealed class TranslationQualificationTools(IBridgeClient bridge, HostOptions options)
{
    private string Root => options.ArtifactsDirectory;
    private TranslationImportWorkflow Runner => new(Root);
    private TranslationWorkerAdapter Adapter(string projectPath, string plcPath, string? sourceCulture = null, string? targetCulture = null, string[]? plcPaths = null) =>
        new(bridge, Root, projectPath, plcPath, (context, action) => {
            if (!context.Operation.Plan.Target.ProjectFilePath.StartsWith(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ProjectIsolationRequired", "Qualification requires the newly owned fixture beneath this run root.");
        }, sourceCulture ?? "de-DE", targetCulture ?? "en-GB", plcPaths);
    private TranslationWorkerAdapter Adapter(string id)
    {
        var op = Runner.Status(id); var selector = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "translation-qualification", id + ".json")))!;
        if (selector["planSha256"]!.GetValue<string>() != op.PlanSha256) throw new BridgeException("IdentityMismatch", "Qualification selector changed.");
        return Adapter(op.Plan.Target.ProjectFilePath, selector["plcPath"]!.GetValue<string>(), selector["sourceCulture"]?.GetValue<string>(),
            selector["targetCulture"]?.GetValue<string>(), selector["plcPaths"]?.AsArray().Select(p => p!.GetValue<string>()).ToArray());
    }
    [McpServerTool(Name = "qualification_translation_fixture", Destructive = true, OpenWorld = false)]
    [Description("Explicit qualification runtime only: seed and reopen the exact newly owned bilingual scratch.")]
    public Task<JsonElement> Fixture(string projectPath, string plcPath, CancellationToken cancellationToken) =>
        bridge.CallAsync("translation_fixture", new { projectPath, plcPath, artifactDirectory = Root }, cancellationToken);

    [McpServerTool(Name = "qualification_translation_categories", Destructive = false, OpenWorld = false)]
    [Description("Explicit qualification runtime only: retained populated category controls and filtering matrix.")]
    public Task<JsonElement> Categories(string projectPath, string plcPath, CancellationToken cancellationToken) =>
        bridge.CallAsync("translation_categories", new { projectPath, plcPath, artifactDirectory = Root }, cancellationToken);

    [McpServerTool(Name = "qualification_translation_capture", Destructive = false, OpenWorld = false)]
    [Description("Explicit qualification runtime only: complete live bilingual text and peer capture for one active culture pair (default de-DE/en-GB) and every named PLC owner.")]
    public async Task<object> Capture(string projectPath, string plcPath, CancellationToken cancellationToken,
        string? sourceCulture = null, string? targetCulture = null, string[]? plcPaths = null)
    {
        var (observation, profile) = await Adapter(projectPath, plcPath, sourceCulture, targetCulture, plcPaths).Capture(cancellationToken);
        var workbook = TranslationWorkbook.Open(observation.Workbook, profile, observation.Target.Reference, Sha256(observation.Workbook));
        return new { observation.Target, profile, rows = profile.Rows.Select(r => new { r.NativeKey, r.Category,
            source = workbook.ReadText(r.SourceCell), target = workbook.ReadText(r.Targets.Single().Location) }).ToArray(),
            nativeImportExecuted = false };
    }
    [McpServerTool(Name = "qualification_translation_prepare", Destructive = false, OpenWorld = false)]
    [Description("Explicit qualification runtime only: bind fresh capture to an explicit no-op or single edit and exact durable review. updateSourceLanguage=true edits the source cell under the separately approved source-text mode; otherwise the target cell.")]
    public async Task<object> Prepare(string projectPath, string plcPath, string idempotencyKey,
        string? nativeKey = null, string? replacement = null, string? sourceCulture = null, string? targetCulture = null,
        string[]? plcPaths = null, bool updateSourceLanguage = false, CancellationToken cancellationToken = default)
    {
        var (observation, profile) = await Adapter(projectPath, plcPath, sourceCulture, targetCulture, plcPaths).Capture(cancellationToken);
        var workflow = new TranslationWorkflowStore(Root); var uploads = new ArtifactUploadStore(Root, options.LibraryLimits);
        var capture = workflow.Capture(uploads.Upload(observation.Workbook, "original.xlsx"), uploads.Upload(TranslationWorkerAdapter.JsonBytes(profile), "profile.json"),
            uploads.Upload(TranslationWorkerAdapter.JsonBytes(observation.Target.Reference), "project.json"));
        var workbook = TranslationWorkbook.Open(observation.Workbook, profile, observation.Target.Reference, Sha256(observation.Workbook));
        TranslationEdit[] edits = [];
        if (nativeKey != null)
        {
            if (string.IsNullOrWhiteSpace(replacement)) throw new BridgeException("InvalidEdit", "A nonempty replacement is required.");
            string culture = updateSourceLanguage ? observation.Scope.SourceCulture : observation.Scope.TargetCulture;
            var pair = workbook.Read(nativeKey, culture);
            edits = [new(nativeKey, culture, TranslationEditKind.Set, replacement, pair.Source.Sha256, pair.Target.Sha256)];
        }
        else if (replacement != null) throw new BridgeException("InvalidEdit", "Exact native key required.");
        var review = workflow.Prepare(capture["recordRef"]!.GetValue<string>(), uploads.Upload(TranslationWorkerAdapter.JsonBytes(
            new TranslationProposalBatch(edits, updateSourceLanguage ? new TranslationReviewOptions(UpdateSourceLanguage: true) : null)), "proposals.json"));
        string reviewRef = review["recordRef"]!.GetValue<string>(), reviewHash = review["metadata"]!["reviewSha256"]!.GetValue<string>();
        var approval = workflow.Approve(reviewRef, reviewHash, "User-authorized disposable-fixture coordinator", true);
        var candidate = workflow.Candidate(reviewRef, reviewHash, approval["recordSha256"]!.GetValue<string>());
        var operation = Runner.Prepare(candidate["recordRef"]!.GetValue<string>(), candidate["recordSha256"]!.GetValue<string>(), observation, idempotencyKey);
        string folder = Path.Combine(Root, "translation-qualification"); Directory.CreateDirectory(folder);
        using (var file = new FileStream(Path.Combine(folder, operation.Id + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { file.Write(TranslationWorkerAdapter.JsonBytes(new { plcPath, operation.PlanSha256, sourceCulture, targetCulture, plcPaths })); file.Flush(true); }
        var binaries = new TranslationProductionRegistry(AppContext.BaseDirectory, options.WorkerPath, TranslationNativeTools.ApiDirectory(options)).Binaries();
        var binding = new TranslationImportArtifacts(Root).Read<TranslationImportBinding>(operation.Plan.Inputs[0]);
        TranslationAdmissionKey? admissionKey = null; string? admissionRefusal = null;
        // Generalized cultures, owners and source mode are qualification evidence only; no production key exists for them.
        try { admissionKey = TranslationProductionAdmission.Describe(new(Root, operation, binding.CandidateRef, binding.CandidateReceiptSha256,
            binding.Candidate.Sha256, binding.Baseline.Scope), binaries); }
        catch (BridgeException error) when (error.Code == "TranslationAdmissionScopeUnqualified") { admissionRefusal = error.Message; }
        return new { operation, capture, review, approval, candidate, edits, admissionKey, admissionKeySha256 = admissionKey is null ? null : HashJson(admissionKey), admissionRefusal };
    }
    [McpServerTool(Name = "qualification_translation_apply", Destructive = true, OpenWorld = false)]
    [Description("Explicit qualification runtime only: one durable import lifecycle; optional exact retained response-loss, pre-checkpoint or pre-dispatch interruption case.")]
    public Task<CallToolResult> Apply(string operationId, string planSha256, int revision,
        string? loseReplyAfterStage = null, bool interruptBeforeDispatch = false, bool driftSourceAfterCheckpoint = false,
        bool interruptBeforeCheckpoint = false, CancellationToken cancellationToken = default) => RunApply(async () => {
        if (loseReplyAfterStage is not null && loseReplyAfterStage is not ("import" or "compile" or "save" or "reopen") ||
            (loseReplyAfterStage is null ? 0 : 1) + (interruptBeforeDispatch ? 1 : 0) + (driftSourceAfterCheckpoint ? 1 : 0) + (interruptBeforeCheckpoint ? 1 : 0) > 1)
            throw new BridgeException("TranslationQualificationCaseInvalid", "Choose exactly one supported lost-response stage, pre-checkpoint or pre-dispatch interruption.");
        var adapter = Adapter(operationId);
        if (loseReplyAfterStage is not null || interruptBeforeDispatch || driftSourceAfterCheckpoint || interruptBeforeCheckpoint)
        {
            var operation = Runner.Status(operationId);
            if (operation.Revision != revision || operation.PlanSha256 != planSha256 || operation.State != "prepared")
                throw new BridgeException("TranslationQualificationCaseInvalid", "Failure injection requires the exact unattempted reviewed operation.");
            string folder = Path.Combine(Root, "translation-qualification"); Directory.CreateDirectory(folder);
            using var file = new FileStream(Path.Combine(folder, operationId + "-failure-injection.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(TranslationWorkerAdapter.JsonBytes(new { operationId, planSha256, revision, loseReplyAfterStage,
                interruptBeforeDispatch, driftSourceAfterCheckpoint, interruptBeforeCheckpoint, productionEnabled = false, automaticReplay = false })); file.Flush(true);
        }
        adapter.DriftSourceAfterCheckpoint = driftSourceAfterCheckpoint;
        if (loseReplyAfterStage is not null) adapter.AfterNativeReturn = stage => {
            if (stage == loseReplyAfterStage) throw new IOException("Qualification: native " + stage + " returned and original response was retained; delivery interrupted.");
        };
        // "acquired" precedes the native lease and checkpoint: the exact history reconcile_before_checkpoint accepts.
        var runner = interruptBeforeDispatch || interruptBeforeCheckpoint ? new TranslationImportWorkflow(Root, stage => {
            if (interruptBeforeDispatch && stage == "checkpointRetained") throw new IOException("Qualification: interrupted after retained checkpoint and before dispatch intent.");
            if (interruptBeforeCheckpoint && stage == "acquired") throw new IOException("Qualification: interrupted after acquisition and before checkpoint intent.");
        }) : Runner;
        return await runner.Apply(operationId, planSha256, revision, adapter, cancellationToken);
    });

    private static async Task<CallToolResult> RunApply(Func<Task<EngineeringOperationRecord>> action)
    {
        object value;
        bool failed = false;
        try { value = await action(); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or
            ArgumentException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            failed = true;
            value = new { error = new {
                code = (error as BridgeException)?.Code ?? (error is OperationCanceledException ? "OperationCancelled" : "TranslationQualificationFailed"),
                message = error is BridgeException ? error.Message :
                    "Qualification apply failed. Inspect the retained operation and reconcile uncertain outcomes; never automatically replay native work.",
                details = (error as BridgeOperationException)?.Details } };
        }
        string json = JsonSerializer.Serialize(value, BridgeProtocol.Json);
        return new CallToolResult { IsError = failed, Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }

    [McpServerTool(Name = "qualification_translation_recover", Destructive = true, OpenWorld = false)]
    [Description("Explicit qualification runtime only: one original-checkpoint restoration to an absent reserved destination; optional retained reply-loss test.")]
    public Task<EngineeringOperationRecord> Recover(string operationId, string planSha256, int revision, string newProjectPath,
        bool loseRestoreReply = false, CancellationToken cancellationToken = default)
    {
        var adapter = Adapter(operationId);
        if (loseRestoreReply) adapter.AfterNativeReturn = stage => { if (stage == "restore") throw new IOException("Qualification: native restore returned and evidence retained, response delivery interrupted."); };
        return Runner.Recover(operationId, planSha256, revision, newProjectPath, adapter, cancellationToken);
    }
    [McpServerTool(Name = "qualification_translation_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Explicit qualification runtime only: fresh original and restored observations, without import or restore replay.")]
    public Task<EngineeringOperationRecord> Reconcile(string operationId, string planSha256, int revision, CancellationToken cancellationToken) =>
        Runner.Reconcile(operationId, planSha256, revision, Adapter(operationId), cancellationToken);

    [McpServerTool(Name = "qualification_translation_status", Destructive = false, OpenWorld = false)]
    [Description("Explicit qualification runtime only: retained operation state; never resumes native work.")]
    public EngineeringOperationRecord Status(string operationId) => Runner.Status(operationId);

    [McpServerTool(Name = "qualification_translation_reconcile_before_checkpoint", Destructive = false, OpenWorld = false)]
    [Description("Explicit qualification runtime only: explicit fresh unchanged reconciliation after a pre-checkpoint worker fix, bound to original exact closed-process evidence.")]
    public Task<EngineeringOperationRecord> ReconcileBeforeCheckpoint(string operationId, int revision, string originalCloseSha256, CancellationToken cancellationToken)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Root, "exact-close.json"));
        if (Sha256(bytes) != originalCloseSha256) throw new BridgeException("ArtifactChanged", "Original exact lifetime evidence changed.");
        return Runner.ReconcileBeforeCheckpoint(operationId, revision, Adapter(operationId), bytes, cancellationToken);
    }
    [McpServerTool(Name = "qualification_translation_resume_closed_fixture", Destructive = false, OpenWorld = false)]
    [Description("Explicit qualification runtime only: reopen an already reconciled fixture after exact prior process closure; verify current content without replaying the completed import.")]
    public Task<EngineeringArtifactReference> ResumeClosedFixture(string operationId, int revision, string closeRelativePath, string originalCloseSha256, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(Path.Combine(Root, closeRelativePath));
        if (!path.StartsWith(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidArtifact", "Lifetime receipt must be inside the original run root.");
        TranslationRetention.NoLinks(path); byte[] bytes = File.ReadAllBytes(path);
        if (Sha256(bytes) != originalCloseSha256) throw new BridgeException("ArtifactChanged", "Original exact lifetime evidence changed.");
        return Runner.ResumeClosedFixture(operationId, revision, Adapter(operationId), bytes, cancellationToken);
    }
}
