#if TESTSUITE_QUALIFICATION
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.TestSuite;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class TestSuiteQualificationTools(IBridgeClient bridge, HostOptions options)
{
    private const string Profile = "v21.owned-root-codeblocks.ui-reviewed.qualification-v1";
    private TestSuiteStyleJobs Jobs => new(options.ArtifactsDirectory);
    private ITestSuiteStyleAdapter Adapter => new TestSuiteWorkerAdapter(bridge, (context, action) => {
        if (options.TestSuiteQualification && context.ExecutionProfile == TestSuiteApplicationQualification.Profile)
        { TestSuiteApplicationQualification.Admit(context, options.ArtifactsDirectory); return; }
        if (!options.TestSuiteQualification || context.ExecutionProfile != Profile || context.Application is not null || context.Run.Definitions.Length != 1 ||
            !new[] { "TiaMcp_Q01_Clean", "TiaMcp_Q01_Violating" }.Contains(context.Run.Definitions[0].Name) ||
            !Path.GetFullPath(context.Operation.Plan.Target.ProjectFilePath).StartsWith(
                Path.Combine(Path.GetFullPath(options.ArtifactsDirectory), "scratch") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("TestSuiteProfileUnqualified", "Only the exact owned, UI-observed qualification fixture is admitted.");
    });
    [McpServerTool(Name = "qualification_testsuite_prepare", Destructive = true, OpenWorld = false)]
    [Description("Qualification build only: prepare an owned fixture and retain an exact durable review. Style fixtures (clean, violating) wait for independently reviewed current scope/license evidence. Application fixtures (applicationPass, applicationFail) need simulatorTicket, the downloadTicket of an owned sim_create instance loaded and in RUN; the worker imports both cases externally managed on that instance and reads their scope back natively. No executor invocation.")]
    public Task<CallToolResult> Prepare(string projectPath, string fixture, string idempotencyKey, int runs = 1, string? simulatorTicket = null, CancellationToken cancellationToken = default) => Typed(async () =>
    {
        if (!options.TestSuiteQualification || runs is < 1 or > 2) throw new BridgeException("InvalidRequest", "One or two qualification runs required.");
        bool application = fixture is "applicationPass" or "applicationFail";
        if (application != (simulatorTicket is not null)) throw new BridgeException("InvalidRequest", "Application fixtures need the simulatorTicket of an owned sim_create instance; style fixtures take none.");
        var observed = await bridge.CallAsync("testsuite_qualification_prepare", new { projectPath, fixture, simulatorTicket, artifactRoot = options.ArtifactsDirectory }, cancellationToken);
        T Read<T>(JsonElement element) => element.Deserialize<T>(BridgeProtocol.Json) ?? throw new BridgeException("InvalidRequest", "Missing native capture.");
        var target = Read<NativeTargetObservation>(observed.GetProperty("target"));
        var d = observed.GetProperty("definition");
        string name = d.GetProperty("name").GetString()!;
        string profile = application ? TestSuiteApplicationQualification.Profile : Profile;
        var capture = new TestSuiteDefinitionCapture(d.GetProperty("id").GetString()!, application ? "application" : "style", name, application ? ".tat" : ".xml",
            Read<EngineeringObjectReference>(d.GetProperty("reference")), Jobs.ReadBytes(Read<EngineeringArtifactReference>(d.GetProperty("originalArtifact"))),
            profile, Read<EngineeringObjectReference[]>(d.GetProperty("requiredScope")), [], true, true, []);
        var review = Jobs.Review([capture], Enumerable.Range(0, runs).Select(_ => new TestSuiteStyleSelection([capture.Manifest.Id], [])).ToArray(), target, profile,
            application ? new TestSuiteApplicationBinding("externallyManaged", simulatorTicket!, false) : null);
        return new { observation = observed, review, status = Jobs.Prepare(review, review.Sha256, idempotencyKey) };
    });
    [McpServerTool(Name = "qualification_testsuite_execute", Destructive = true, OpenWorld = false)]
    [Description("Qualification build only: one durable style or application dispatch with native checkpoint, raw retention, preservation and independently observed reopened scope.")]
    public Task<CallToolResult> Execute(string jobId, string reviewedSha256, string operationId, int expectedRevision,
        string? fault = null, CancellationToken cancellationToken = default) => Typed(async () =>
    {
        if (fault != null)
        {
            var status = Jobs.Status(jobId);
            if (status.Job.ReviewSha256 != reviewedSha256 || status.NextOperationId != operationId || status.Operations.Single(o => o.Id == operationId).Revision != expectedRevision ||
                fault is not ("beforeRun" or "afterRun")) throw new BridgeException("InvalidRequest", "Exact unattempted qualification operation and response-loss boundary required.");
            await bridge.CallAsync("testsuite_qualification_fault", new { operationId, fault }, cancellationToken);
        }
        return await Jobs.ExecuteNext(jobId, reviewedSha256, operationId, expectedRevision, Adapter, cancellationToken);
    });
    [McpServerTool(Name = "qualification_testsuite_recover", Destructive = true, OpenWorld = false)]
    [Description("Qualification build only: restore the retained checkpoint once into an exact absent destination. Requires separate reconciliation.")]
    public Task<CallToolResult> Recover(string jobId, string reviewedSha256, string operationId, int expectedRevision, string recoveryLocation,
        bool loseRestoreReply = false, CancellationToken cancellationToken = default) => Typed(async () =>
    {
        var op = Jobs.Status(jobId).Operations.Single(o => o.Id == operationId);
        if (loseRestoreReply)
        {
            if (op.Revision != expectedRevision || op.State is not ("outcomeUnknown" or "recoveryRequired")) throw new BridgeException("InvalidRequest", "Exact recoverable operation required.");
            await bridge.CallAsync("testsuite_qualification_fault", new { operationId, fault = "afterRestore" }, cancellationToken);
        }
        return await Jobs.Recover(jobId, reviewedSha256, operationId, expectedRevision,
            [new(op.Plan.Resources.Single().ResourceKey, recoveryLocation)], Adapter, cancellationToken);
    });
    [McpServerTool(Name = "qualification_testsuite_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Qualification build only: independently reopen the original and each admitted recovery destination and compare complete fixture preservation; style fixtures also read the scope editor again.")]
    public Task<CallToolResult> Reconcile(string jobId, string reviewedSha256, string operationId, int expectedRevision, CancellationToken cancellationToken) =>
        Typed(async () => await Jobs.Reconcile(jobId, reviewedSha256, operationId, expectedRevision, Adapter, cancellationToken));

    // Typed result like TestSuiteScopeProbeTools: a thrown BridgeException (such as QualificationResponseLost) would reach the client as the SDK's generic message without its code.
    private static async Task<CallToolResult> Typed(Func<Task<object>> call)
    {
        string json; bool failed = false;
        try { json = JsonSerializer.Serialize(await call(), BridgeProtocol.Json); }
        catch (BridgeException error) { json = JsonSerializer.Serialize(new { error = new { code = error.Code, message = error.Message } }, BridgeProtocol.Json); failed = true; }
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
#endif
