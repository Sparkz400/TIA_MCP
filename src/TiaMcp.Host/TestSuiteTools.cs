using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.TestSuite;

namespace TiaMcp.Host;

// TiaTools is already registered. Production qualification refuses before the lazy owning bridge.
public sealed partial class TiaTools
{
    private sealed record StyleDefinitionInput(TestSuiteDefinitionManifest Manifest, EngineeringArtifactReference OriginalArtifact);
    private sealed record StylePlanRequest(NativeTargetObservation Target, string ExecutionProfile,
        StyleDefinitionInput[] Definitions, TestSuiteStyleSelection[] Runs);
    private sealed record ApplicationPlanRequest(NativeTargetObservation Target, string ExecutionProfile, string ExecutionMode,
        string SimulatorTicket, bool WholeGroup, StyleDefinitionInput[] Definitions, TestSuiteStyleSelection[] Runs);
    private sealed record SystemPlanRequest(NativeTargetObservation Target, string ExecutionProfile, string SimulatorTicket,
        string ServerAddress, bool WholeGroup, StyleDefinitionInput[] Definitions, TestSuiteStyleSelection[] Runs);
    private TestSuiteStyleJobs StyleJobs() => new(options.ArtifactsDirectory);
    private static CallToolResult StyleResult(Func<object> action)
    {
        try { return Result(JsonSerializer.Serialize(action(), BridgeProtocol.Json), false); }
        catch (BridgeException error) { return Result(JsonSerializer.Serialize(new { error = new { code = error.Code, message = error.Message } }, BridgeProtocol.Json), true); }
    }
    private static async Task<CallToolResult> StyleResultAsync(Func<Task<TestSuiteStyleJobStatus>> action)
    {
        try { return Result(JsonSerializer.Serialize(await action(), BridgeProtocol.Json), false); }
        catch (BridgeException error) { return Result(JsonSerializer.Serialize(new { error = new { code = error.Code, message = error.Message } }, BridgeProtocol.Json), true); }
    }

    [McpServerTool(Name = "testsuite_style_plan_prepare", Destructive = false, OpenWorld = false)]
    [Description("Retain a complete bounded offline style review. request contains target: exact captured NativeTargetObservation; executionProfile; definitions:[{manifest,originalArtifact:{relativePath,sha256}}]; runs:[{selectedDefinitionIds,excludedDefinitionIds}]. Original files must be under this Host's artifact root. Maximum 32 runs, 1000 definitions, 32 MiB originals. Retains exact requested scope, closure, immutable original bytes, effects, checkpoint and verification obligations. Caller observations do not establish native product/license/scope qualification.")]
    public CallToolResult TestSuiteStylePlanPrepare(JsonElement request) => StyleResult(() =>
    {
        var input = TestSuiteStyleJobs.Parse<StylePlanRequest>(Encoding.UTF8.GetBytes(request.GetRawText()));
        var definitions = StyleCaptures(input.Definitions);
        var jobs = StyleJobs();
        var review = jobs.Review(definitions, input.Runs, input.Target, input.ExecutionProfile);
        return new { review, completePlan = jobs.ReadReview(review), qualifiedProfiles = TestSuiteProductionAdapter.QualifiedProfiles, nativeExecutionAuthorized = false };
    });

    [McpServerTool(Name = "testsuite_application_plan_prepare", Destructive = false, OpenWorld = false)]
    [Description("Retain a complete bounded application-test review (TS-05). request is the style plan request plus executionMode: systemManaged or externallyManaged, simulatorTicket: the downloadTicket of an owned sim_create instance, wholeGroup: true to run the project's whole application group. Each run dispatches one native TestCaseExecutor.Run: one case, a case list, one set, a set list or the whole group; cases and sets never mix. Use the testsuite_style_job_* tools with the returned review; they carry application reviews unchanged. The worker verifies the ticket-owned PLCSIM instance before SetScope and Run. Native execution stays disabled until qualified.")]
    public CallToolResult TestSuiteApplicationPlanPrepare(JsonElement request) => StyleResult(() =>
    {
        var input = TestSuiteStyleJobs.Parse<ApplicationPlanRequest>(Encoding.UTF8.GetBytes(request.GetRawText()));
        var definitions = StyleCaptures(input.Definitions);
        var jobs = StyleJobs();
        var review = jobs.Review(definitions, input.Runs, input.Target, input.ExecutionProfile,
            new TestSuiteApplicationBinding(input.ExecutionMode, input.SimulatorTicket, input.WholeGroup));
        return new { review, completePlan = jobs.ReadReview(review), qualifiedProfiles = TestSuiteProductionAdapter.QualifiedProfiles, nativeExecutionAuthorized = false };
    });

    [McpServerTool(Name = "testsuite_application_inventory", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the project's Test Suite application group: test cases with their GetScope target, test sets and counts, bounded by maxItems with explicit truncation. export=true also saves each listed case as .tat (TestCase.SaveToFile) under the Host export directory with SHA-256; sets have no Openness export route. No import, SetScope or execution.")]
    public Task<CallToolResult> TestSuiteApplicationInventory(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200, bool export = false)
        => Call("testsuite_application_inventory", new { projectName, maxItems = Limit(maxItems), export, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "testsuite_system_plan_prepare", Destructive = false, OpenWorld = false)]
    [Description("Retain a complete bounded system-test review (TS-06). request is the style plan request plus simulatorTicket: the downloadTicket of an owned sim_create instance, serverAddress: the canonical opc.tcp://<IPv4>:<port>/ address of that instance, wholeGroup: true to run the project's whole system group. Definitions use the system (.tst) family. Each run dispatches one native SystemTestCaseExecutor.Run: one case, a case list or the whole group. Use the testsuite_style_job_* tools with the returned review. The worker verifies the ticket-owned PLCSIM instance and that serverAddress is one of its controller addresses before SetScope(StandardSIMATIC) and Run. Native execution stays disabled until qualified.")]
    public CallToolResult TestSuiteSystemPlanPrepare(JsonElement request) => StyleResult(() =>
    {
        var input = TestSuiteStyleJobs.Parse<SystemPlanRequest>(Encoding.UTF8.GetBytes(request.GetRawText()));
        var definitions = StyleCaptures(input.Definitions);
        var jobs = StyleJobs();
        var review = jobs.Review(definitions, input.Runs, input.Target, input.ExecutionProfile,
            new TestSuiteApplicationBinding("endpoint", input.SimulatorTicket, input.WholeGroup) { ServerAddress = input.ServerAddress });
        return new { review, completePlan = jobs.ReadReview(review), qualifiedProfiles = TestSuiteProductionAdapter.QualifiedProfiles, nativeExecutionAuthorized = false };
    });

    [McpServerTool(Name = "testsuite_system_inventory", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the project's Test Suite system group: test cases with their OPC UA server address, interface type and interface folder, and the count, bounded by maxItems with explicit truncation. export=true also saves each listed case as .tst (SystemTestCase.SaveToFile) under the Host export directory with SHA-256. No import, SetScope or execution.")]
    public Task<CallToolResult> TestSuiteSystemInventory(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200, bool export = false)
        => Call("testsuite_system_inventory", new { projectName, maxItems = Limit(maxItems), export, artifactDirectory = ExportDirectory }, cancellationToken);

    private TestSuiteDefinitionCapture[] StyleCaptures(StyleDefinitionInput[] inputs)
    {
        if (inputs is null || inputs.Length is < 1 or > 1000 || inputs.Any(d => d?.Manifest is null || d.OriginalArtifact is null) ||
            inputs.Sum(d => (long)d.Manifest.OriginalBytes) > TestSuiteDefinitionBundle.MaxOriginalBytes)
            throw new BridgeException("TestSuiteJobLimit", "Supply bounded exact definition manifests and original artifacts.");
        var jobs = StyleJobs();
        return inputs.Select(d =>
        {
            var m = d.Manifest;
            var capture = new TestSuiteDefinitionCapture(m.Id, m.Family, m.Name, m.Extension, m.Reference,
                jobs.ReadBytes(d.OriginalArtifact), m.ProfileId, m.RequiredScope, m.Dependencies, m.ScopeComplete, m.EffectsReviewed, m.UnresolvedSyntax);
            if (TestSuiteStyleOperation.CanonicalHash(capture.Manifest) != TestSuiteStyleOperation.CanonicalHash(m))
                throw new BridgeException("InvalidDefinitionBundle", "Original bytes must match the complete reviewed manifest.");
            return capture;
        }).ToArray();
    }

    [McpServerTool(Name = "testsuite_style_plan_review", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(TiaMcp.Core.TestSuite.TestSuiteStyleReview))]
    [Description("Reopen and validate the complete style review, every requested scope, frozen definition bundle and durable effect/checkpoint plan. No pagination or omitted run membership; no native calls.")]
    public CallToolResult TestSuiteStylePlanReview(string relativePath, string sha256) => StyleResult(() => StyleJobs().ReadReview(new(relativePath, sha256)));

    [McpServerTool(Name = "testsuite_style_job_prepare", Destructive = false, OpenWorld = false)]
    [Description("Prepare the reviewed complete style queue in the shared durable operation store. Exact review hash and idempotency key required. Does not reserve the project, checkpoint, start a worker or execute; repeated keys cannot change membership.")]
    public CallToolResult TestSuiteStyleJobPrepare(string relativePath, string sha256, string reviewedSha256, string idempotencyKey)
        => StyleResult(() => StyleJobs().Prepare(new(relativePath, sha256), reviewedSha256, idempotencyKey));

    [McpServerTool(Name = "testsuite_style_job_status", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(TiaMcp.Core.TestSuite.TestSuiteStyleJobStatus))]
    [Description("Reopen durable Test Suite queue progress, stop request, operation revisions and unknown outcomes. Never dispatches, resumes or releases native ownership.")]
    public CallToolResult TestSuiteStyleJobStatus(string jobId) => StyleResult(() => StyleJobs().Status(jobId));

    [McpServerTool(Name = "testsuite_style_job_stop_request", Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(TiaMcp.Core.TestSuite.TestSuiteStyleJobStatus))]
    [Description("Persist a stop-between-runs request across Host restarts. Stops remaining unattempted runs. An active checkpoint or blocking native Run is not cancelled; unknown outcomes retain ownership until reconciliation.")]
    public CallToolResult TestSuiteStyleJobStop(string jobId, string reviewedSha256) => StyleResult(() => StyleJobs().RequestStop(jobId, reviewedSha256));

    [McpServerTool(Name = "testsuite_style_job_execute_next", Destructive = true, OpenWorld = false)]
    [Description("Explicitly admit the next reviewed style run once, with an exact operation revision. Native qualification is mandatory before native access or checkpoint side effects. No production profiles are currently qualified; this entry refuses locally without launching a worker. Lost responses cannot authorize redispatch.")]
    public Task<CallToolResult> TestSuiteStyleJobExecute(string jobId, string reviewedSha256, string operationId, int expectedRevision, CancellationToken cancellationToken)
        => StyleResultAsync(() => StyleJobs().ExecuteNext(jobId, reviewedSha256, operationId, expectedRevision, new TestSuiteProductionAdapter(bridge), cancellationToken));

    [McpServerTool(Name = "testsuite_style_job_results", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Retrieve retained raw/result/scope/assessment artifact references and shared operation evidence. Revalidates hashes and rederives results from original raw bytes. Assertion failure, incomplete evidence, execution failure and unknown outcomes remain separate; reconciled does not mean passed.")]
    public CallToolResult TestSuiteStyleJobResults(string jobId, string operationId) => StyleResult(() => StyleJobs().Results(jobId, operationId));

    [McpServerTool(Name = "testsuite_style_job_mark_interrupted", Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(TiaMcp.Core.TestSuite.TestSuiteStyleJobStatus))]
    [Description("Explicitly mark a retained in-flight checkpoint/dispatch/recovery intent unknown after interruption. Requires current revision and no active execution lock. Keeps all resource reservations and results; never invokes native code or replays a call.")]
    public CallToolResult TestSuiteStyleJobInterrupted(string jobId, string operationId, int expectedRevision)
        => StyleResult(() => StyleJobs().MarkInterrupted(jobId, operationId, expectedRevision));

    [McpServerTool(Name = "testsuite_style_job_recover", Destructive = true, OpenWorld = false)]
    [Description("Explicit checkpoint recovery to exact distinct destinations through a qualified adapter. destinations:[{resourceKey,recoveryLocation}]. Reserves destinations and flushes recovery intent before one restore; never overwrites the original or retries a lost response. Production recovery profiles remain unqualified and refuse locally.")]
    public Task<CallToolResult> TestSuiteStyleJobRecover(string jobId, string reviewedSha256, string operationId, int expectedRevision, JsonElement destinations, CancellationToken cancellationToken)
        => StyleResultAsync(() => StyleJobs().Recover(jobId, reviewedSha256, operationId, expectedRevision,
            TestSuiteStyleJobs.Parse<EngineeringRecoveryDestination[]>(Encoding.UTF8.GetBytes(destinations.GetRawText())), new TestSuiteProductionAdapter(bridge), cancellationToken));

    [McpServerTool(Name = "testsuite_style_job_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Request independent native reconciliation of original resources and every admitted recovery destination. Does not accept caller-authored passing receipts. Qualification is mandatory; production profiles currently refuse locally. Releases shared reservations only after complete evidence; never replays execution or restoration.")]
    public Task<CallToolResult> TestSuiteStyleJobReconcile(string jobId, string reviewedSha256, string operationId, int expectedRevision, CancellationToken cancellationToken)
        => StyleResultAsync(() => StyleJobs().Reconcile(jobId, reviewedSha256, operationId, expectedRevision, new TestSuiteProductionAdapter(bridge), cancellationToken));
}
