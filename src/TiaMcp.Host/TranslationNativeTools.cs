using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Translations;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class TranslationNativeTools(IBridgeClient bridge, HostOptions options)
{
    private string Root => options.ArtifactsDirectory;
    private TranslationImportWorkflow Runner => new(Root);
    private TranslationProductionRegistry Registry => new(AppContext.BaseDirectory, options.WorkerPath,
        options.ApiDirectory ?? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48");
    private TranslationWorkerAdapter Worker(string profileId)
    {
        var entry = Registry.Resolve(profileId);
        return new(bridge, Root, entry.ProjectPath, entry.PlcPath, (context, _) => Registry.RequireAdmission(profileId, context));
    }
    private TranslationProductionImportAdapter Adapter(string profileId) => new(Registry, Worker(profileId), profileId);

    [McpServerTool(Name = "translation_native_capture", ReadOnly = true, OpenWorld = false)]
    [Description("Capture complete bilingual texts and peers for an exact operator-admitted project. Missing installed admission refuses before native dispatch.")]
    public Task<CallToolResult> Capture(string profileId, CancellationToken cancellationToken) => Run(async () => {
        var (observation, profile) = await Worker(profileId).Capture(cancellationToken);
        var uploads = new ArtifactUploadStore(Root);
        string Upload(byte[] bytes, string name)
        { var value = uploads.Begin(name, bytes.Length, Sha256(bytes)); uploads.Write(value.UploadId, 0, Convert.ToBase64String(bytes)); return uploads.Commit(value.UploadId).ArtifactRef!; }
        var capture = new TranslationWorkflowStore(Root).Capture(Upload(observation.Workbook, "native-original.xlsx"),
            Upload(TranslationWorkerAdapter.JsonBytes(profile), "native-profile.json"), Upload(TranslationWorkerAdapter.JsonBytes(observation.Target.Reference), "native-project.json"));
        return new { profileId, capture, nativeImportExecuted = false, requiresExactCandidateApproval = true };
    });

    [McpServerTool(Name = "translation_native_prepare", Destructive = false, OpenWorld = false)]
    [Description("Bind an explicitly approved offline candidate to fresh native capture and the exact installed translation profile; returns a durable plan without importing.")]
    public Task<CallToolResult> Prepare(string profileId, string candidateRef, string candidateReceiptSha256,
        string idempotencyKey, CancellationToken cancellationToken) => Run(async () => {
        var (observed, _) = await Worker(profileId).Capture(cancellationToken);
        var op = Runner.Prepare(candidateRef, candidateReceiptSha256, observed, idempotencyKey);
        var binding = new TranslationImportArtifacts(Root).Read<TranslationImportBinding>(op.Plan.Inputs[0]);
        Registry.RequireAdmission(profileId, new(Root, op, candidateRef, candidateReceiptSha256, binding.Candidate.Sha256, binding.Baseline.Scope));
        return op;
    });

    [McpServerTool(Name = "translation_native_apply", Destructive = true, OpenWorld = false)]
    [Description("Import one exact admitted reviewed translation plan once, with retained checkpoint, full preservation, compile/save/fresh reopen and durable unknown-outcome handling.")]
    public Task<CallToolResult> Apply(string profileId, string operationId, string planSha256, int revision, CancellationToken cancellationToken) =>
        Run(async () => await Runner.Apply(operationId, planSha256, revision, Adapter(profileId), cancellationToken));

    [McpServerTool(Name = "translation_native_status", ReadOnly = true, OpenWorld = false)]
    [Description("Read and integrity-check an exact retained translation operation; never starts a worker, resumes or replays native work.")]
    public CallToolResult Status(string operationId)
    {
        try { return Format(Runner.Status(operationId), false); }
        catch (Exception error) when (Handled(error)) { return Failure(error); }
    }

    [McpServerTool(Name = "translation_native_recover", Destructive = true, OpenWorld = false)]
    [Description("Restore an admitted operation's original checkpoint once into a distinct absent reserved directory; response loss is reconciled without retrieving again.")]
    public Task<CallToolResult> Recover(string profileId, string operationId, string planSha256, int revision,
        string newProjectPath, CancellationToken cancellationToken) =>
        Run(async () => await Runner.Recover(operationId, planSha256, revision, newProjectPath, Adapter(profileId), cancellationToken));

    [McpServerTool(Name = "translation_native_reconcile", ReadOnly = true, OpenWorld = false)]
    [Description("Read-only fresh original/recovered observations for an admitted unresolved translation operation; release ownership only after complete verification and cleanup.")]
    public Task<CallToolResult> Reconcile(string profileId, string operationId, string planSha256, int revision, CancellationToken cancellationToken) =>
        Run(async () => await Runner.Reconcile(operationId, planSha256, revision, Adapter(profileId), cancellationToken));

    [McpServerTool(Name = "project_text_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read-only native project-text export for an explicit active sourceCulture and 1..32 active targetCultures into the artifact directory, each workbook with path and SHA-256. Without owners the whole project is exported with the three-argument overload, one bilingual workbook per target. With owners (exact hardware, /plc/ or /hmi/ paths from project_inventory, at most 64) one multi-target workbook uses the five-argument overload with every project text category. Optional categoryIds add a filtered export of the same owners and targets; it is evidence only: the full export always runs too and both are compared by native key, reporting leaked, dropped and changed rows (maxItems keys each; the complete comparison is retained as comparison.json). Never imports, saves or activates languages; no admission is required.")]
    public Task<CallToolResult> ProjectTextExport(string sourceCulture, string[] targetCultures, CancellationToken cancellationToken,
        string? projectName = null, string[]? owners = null, string[]? categoryIds = null, int maxItems = 200) => Run(async () =>
        ProjectTextExportReport.Build(Root, await bridge.CallAsync("project_text_export",
            new { projectName, sourceCulture, targetCultures, owners, categoryIds, artifactDirectory = Root }, cancellationToken), Math.Clamp(maxItems, 1, 1000)));

    private static async Task<CallToolResult> Run(Func<Task<object>> action)
    {
        try { return Format(await action(), false); }
        catch (Exception error) when (Handled(error)) { return Failure(error); }
    }

    private static bool Handled(Exception error) => error is BridgeException or IOException or UnauthorizedAccessException or
        ArgumentException or JsonException or InvalidOperationException or OperationCanceledException;

    private static CallToolResult Failure(Exception error) => Format(new { error = new {
        code = (error as BridgeException)?.Code ?? (error is OperationCanceledException ? "OperationCancelled" : "TranslationNativeFailed"),
        message = error is BridgeException ? error.Message :
            "Translation operation failed. Read retained operation status and reconcile uncertain outcomes; never automatically replay native work.",
        details = (error as BridgeOperationException)?.Details } }, true);

    private static CallToolResult Format(object value, bool error)
    {
        string json = JsonSerializer.Serialize(value, BridgeProtocol.Json);
        return new CallToolResult { IsError = error, Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
