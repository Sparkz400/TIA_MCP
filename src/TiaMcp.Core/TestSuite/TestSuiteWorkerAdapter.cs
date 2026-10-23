using System.Text.Json;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.TestSuite;

// Only trusted in-process code can supply admission. No wire field or Host option opens this gate.
internal sealed class TestSuiteWorkerAdapter(IBridgeClient bridge, Action<TestSuiteStyleContext, string> admission) : ITestSuiteStyleAdapter
{
    public void RequireQualification(TestSuiteStyleContext context, string action) => admission(context, action);

    public async Task<ITestSuiteStyleSession> OpenExclusive(TestSuiteStyleContext context, CancellationToken token)
    {
        RequireQualification(context, "openExclusive");
        var response = await bridge.CallAsync("testsuite_style_begin", new
        {
            context.ArtifactRoot, context.Run, context.Operation, context.ExecutionProfile,
            definitionBundle = context.Definitions.Bytes
        }, token);
        var opened = TestSuiteStyleJobs.Parse<Opened>(JsonSerializer.SerializeToUtf8Bytes(response));
        if (string.IsNullOrWhiteSpace(opened.LeaseToken) || opened.OperationId != context.Operation.Id ||
            opened.PlanSha256 != context.Operation.PlanSha256)
            throw new BridgeException("InvalidLease", "Owning worker did not acknowledge the exact Test Suite operation. Reconciliation is required; begin is never retried.");
        return new Session(bridge, context, opened.LeaseToken);
    }

    internal sealed record Opened(string LeaseToken, string OperationId, string PlanSha256);
    private sealed record Raw(EngineeringArtifactReference RawResults);
    private sealed record Reply(JsonElement Value);
    private sealed record Closed(bool Released, string OperationId);

    private sealed class Session(IBridgeClient bridge, TestSuiteStyleContext context, string lease) : ITestSuiteStyleSession
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        private bool closed, failed, released;
        private int runAttempted, checkpointAttempted, restoreAttempted, verifyAttempted;

        private async Task<T> Call<T>(string operation, object? payload, CancellationToken token, bool irrevocable = false)
        {
            await serial.WaitAsync(token);
            try
            {
                if (closed || failed) throw new BridgeException("RecoveryRequired", "Closed or interrupted native sessions cannot be reused.");
                // Re-read the authoritative journal after each flushed intent. Never send the stale opening revision.
                var current = new EngineeringOperationStore(context.ArtifactRoot).Read(context.Operation.Id);
                var result = await bridge.CallAsync(operation, new { leaseToken = lease, operation = current, payload },
                    irrevocable ? CancellationToken.None : token);
                var envelope = TestSuiteStyleJobs.Parse<Reply>(JsonSerializer.SerializeToUtf8Bytes(result));
                return TestSuiteStyleJobs.Parse<T>(JsonSerializer.SerializeToUtf8Bytes(envelope.Value));
            }
            catch { failed = true; throw; }
            finally { serial.Release(); }
        }

        public Task<TestSuiteStyleObservation> Observe(CancellationToken token) => Call<TestSuiteStyleObservation>("testsuite_style_observe", null, token);
        public Task<EngineeringArtifactReference[]> Checkpoint(CancellationToken token)
        {
            Once(ref checkpointAttempted);
            return Call<EngineeringArtifactReference[]>("testsuite_style_checkpoint", null, token, irrevocable: true);
        }
        public async Task<byte[]> Run(CancellationToken token)
        {
            Once(ref runAttempted);
            var result = await Call<Raw>("testsuite_style_run", null, token, irrevocable: true);
            // Read the exact flushed worker file, including malformed/unknown/duplicate JSON fields.
            // Host durable jobs retain these bytes before parsing. No JSON round trip of the native result.
            try { return new TestSuiteStyleJobs(context.ArtifactRoot).ReadBytes(result.RawResults); }
            catch { failed = true; throw; }
        }
        public Task<TestSuiteStyleVerification> Verify(CancellationToken token)
        {
            Once(ref verifyAttempted);
            return Call<TestSuiteStyleVerification>("testsuite_style_verify", null, token, irrevocable: true);
        }
        public Task<EngineeringArtifactReference> Restore(EngineeringRecoveryDestination[] destinations, CancellationToken token)
        {
            Once(ref restoreAttempted);
            return Call<EngineeringArtifactReference>("testsuite_style_restore", destinations, token, irrevocable: true);
        }
        public Task<TestSuiteStyleReconciliation> Reconcile(CancellationToken token) => Call<TestSuiteStyleReconciliation>("testsuite_style_reconcile", null, token);
        private static void Once(ref int attempted)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) throw new BridgeException("RecoveryRequired", "Native stage is one-attempt only, including lost responses.");
        }
        public async ValueTask DisposeAsync()
        {
            await serial.WaitAsync(CancellationToken.None);
            try
            {
                if (closed)
                {
                    if (!released) throw new BridgeException("NativeCleanupUnverified", "The previous worker lease release was not confirmed; it cannot be retried or reported successful.");
                    return;
                }
                closed = true; // A lost end response is not permission to retry or replace the worker.
                var result = await bridge.CallAsync("testsuite_style_end", new { leaseToken = lease, operationId = context.Operation.Id }, CancellationToken.None);
                var receipt = TestSuiteStyleJobs.Parse<Closed>(JsonSerializer.SerializeToUtf8Bytes(result));
                if (!receipt.Released || receipt.OperationId != context.Operation.Id)
                    throw new BridgeException("NativeCleanupUnverified", "Owning worker did not prove Test Suite lease release.");
                released = true;
            }
            finally { serial.Release(); }
        }
    }
}
