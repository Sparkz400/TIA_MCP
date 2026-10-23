#if TESTSUITE_QUALIFICATION
using System.ComponentModel;
using System.Text.Json;
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
        if (!options.TestSuiteQualification || context.ExecutionProfile != Profile || context.Run.Definitions.Length != 1 ||
            !new[] { "TiaMcp_Q01_Clean", "TiaMcp_Q01_Violating" }.Contains(context.Run.Definitions[0].Name) ||
            !Path.GetFullPath(context.Operation.Plan.Target.ProjectFilePath).StartsWith(
                Path.Combine(Path.GetFullPath(options.ArtifactsDirectory), "scratch") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("TestSuiteProfileUnqualified", "Only the exact owned, UI-observed qualification fixture is admitted.");
    });
    [McpServerTool(Name = "qualification_testsuite_prepare", Destructive = true, OpenWorld = false)]
    [Description("Qualification build only: prepare the owned fixture, wait for independently reviewed current scope/license evidence, and retain an exact durable review. No executor invocation.")]
    public async Task<object> Prepare(string projectPath, string fixture, string idempotencyKey, int runs = 1, CancellationToken cancellationToken = default)
    {
        if (!options.TestSuiteQualification || runs is < 1 or > 2) throw new BridgeException("InvalidRequest", "One or two qualification runs required.");
        var observed = await bridge.CallAsync("testsuite_qualification_prepare", new { projectPath, fixture, artifactRoot = options.ArtifactsDirectory }, cancellationToken);
        T Read<T>(JsonElement element) => element.Deserialize<T>(BridgeProtocol.Json) ?? throw new BridgeException("InvalidRequest", "Missing native capture.");
        var target = Read<NativeTargetObservation>(observed.GetProperty("target"));
        var d = observed.GetProperty("definition");
        string name = d.GetProperty("name").GetString()!;
        var capture = new TestSuiteDefinitionCapture(d.GetProperty("id").GetString()!, "style", name, ".xml",
            Read<EngineeringObjectReference>(d.GetProperty("reference")), Jobs.ReadBytes(Read<EngineeringArtifactReference>(d.GetProperty("originalArtifact"))),
            Profile, Read<EngineeringObjectReference[]>(d.GetProperty("requiredScope")), [], true, true, []);
        var review = Jobs.Review([capture], Enumerable.Range(0, runs).Select(_ => new TestSuiteStyleSelection([capture.Manifest.Id], [])).ToArray(), target, Profile);
        return new { observation = observed, review, status = Jobs.Prepare(review, review.Sha256, idempotencyKey) };
    }
    [McpServerTool(Name = "qualification_testsuite_execute", Destructive = true, OpenWorld = false)]
    [Description("Qualification build only: one durable style dispatch with native checkpoint, raw retention, preservation and independently observed reopened scope.")]
    public async Task<TestSuiteStyleJobStatus> Execute(string jobId, string reviewedSha256, string operationId, int expectedRevision,
        string? fault = null, CancellationToken cancellationToken = default)
    {
        if (fault != null)
        {
            var status = Jobs.Status(jobId);
            if (status.Job.ReviewSha256 != reviewedSha256 || status.NextOperationId != operationId || status.Operations.Single(o => o.Id == operationId).Revision != expectedRevision ||
                fault is not ("beforeRun" or "afterRun")) throw new BridgeException("InvalidRequest", "Exact unattempted qualification operation and response-loss boundary required.");
            await bridge.CallAsync("testsuite_qualification_fault", new { operationId, fault }, cancellationToken);
        }
        return await Jobs.ExecuteNext(jobId, reviewedSha256, operationId, expectedRevision, Adapter, cancellationToken);
    }
    [McpServerTool(Name = "qualification_testsuite_recover", Destructive = true, OpenWorld = false)]
    [Description("Qualification build only: restore the retained checkpoint once into an exact absent destination. Requires separate reconciliation.")]
    public async Task<TestSuiteStyleJobStatus> Recover(string jobId, string reviewedSha256, string operationId, int expectedRevision, string recoveryLocation,
        bool loseRestoreReply = false, CancellationToken cancellationToken = default)
    {
        var op = Jobs.Status(jobId).Operations.Single(o => o.Id == operationId);
        if (loseRestoreReply)
        {
            if (op.Revision != expectedRevision || op.State is not ("outcomeUnknown" or "recoveryRequired")) throw new BridgeException("InvalidRequest", "Exact recoverable operation required.");
            await bridge.CallAsync("testsuite_qualification_fault", new { operationId, fault = "afterRestore" }, cancellationToken);
        }
        return await Jobs.Recover(jobId, reviewedSha256, operationId, expectedRevision,
            [new(op.Plan.Resources.Single().ResourceKey, recoveryLocation)], Adapter, cancellationToken);
    }
    [McpServerTool(Name = "qualification_testsuite_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Qualification build only: independently reopen the original and each admitted recovery destination, compare complete fixture preservation and read the scope editor again.")]
    public Task<TestSuiteStyleJobStatus> Reconcile(string jobId, string reviewedSha256, string operationId, int expectedRevision, CancellationToken cancellationToken) =>
        Jobs.Reconcile(jobId, reviewedSha256, operationId, expectedRevision, Adapter, cancellationToken);
}
#endif
