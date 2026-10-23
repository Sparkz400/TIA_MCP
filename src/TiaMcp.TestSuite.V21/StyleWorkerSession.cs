using System;
using Newtonsoft.Json.Linq;

namespace TiaMcp.TestSuite.V21
{
    /// <summary>Trusted owning-worker boundary. Never deserialized from a request or supplied by an MCP client.</summary>
    public interface IStyleWorkerBoundary
    {
        void RequireQualification(JObject context);
        IDisposable Acquire(JObject context);
        JObject Observe();
        JArray Checkpoint();
        JObject Run();
        JObject Verify();
        JObject Restore(JArray destinations);
        JObject Reconcile(JObject operation);
    }

    /// <summary>Serial STA lease protocol; a failed stage poisons the lease, but end still releases it once.</summary>
    public sealed class StyleWorkerSession : IDisposable
    {
        private readonly IStyleWorkerBoundary boundary;
        private readonly JObject context;
        private IDisposable lease;
        private bool failed, disposed, released, checkpointAttempted, runAttempted, verifyAttempted, restoreAttempted;
        private int observations, revision;
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public string OperationId => context["operation"].Value<string>("id");
        public string PlanSha256 => context["operation"].Value<string>("planSha256");

        public StyleWorkerSession(IStyleWorkerBoundary boundary, JObject context)
        {
            this.boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
            this.context = (JObject)(context ?? throw new ArgumentNullException(nameof(context))).DeepClone();
            boundary.RequireQualification(this.context); // Before exclusive access, lookup, archive or restoration.
            if (string.IsNullOrWhiteSpace(OperationId) || string.IsNullOrWhiteSpace(PlanSha256)) throw new InvalidOperationException("Invalid Test Suite operation identity.");
            revision = this.context["operation"].Value<int>("revision");
            lease = boundary.Acquire(this.context) ?? throw new InvalidOperationException("Owning worker returned no exclusive lease.");
        }
        public JObject Opened() => new JObject { ["leaseToken"] = Token, ["operationId"] = OperationId, ["planSha256"] = PlanSha256 };

        public JToken Execute(string stage, JObject arguments)
        {
            if (disposed || failed) throw new InvalidOperationException("RecoveryRequired: closed or interrupted Test Suite lease.");
            try
            {
                if (arguments.Value<string>("leaseToken") != Token) throw new InvalidOperationException("InvalidLease: exact owning session required.");
                var operation = arguments["operation"] as JObject ?? throw new InvalidOperationException("Current durable intent is required.");
                if (operation.Value<string>("id") != OperationId || operation.Value<string>("planSha256") != PlanSha256 ||
                    !JToken.DeepEquals(operation["plan"], context["operation"]["plan"]) || operation.Value<int>("revision") < revision)
                    throw new InvalidOperationException("StaleNativeTarget: durable operation changed or moved backwards.");
                revision = operation.Value<int>("revision");
                string state = operation.Value<string>("state");
                switch (stage)
                {
                    case "observe":
                        if (state != "acquired" && state != "ready" || runAttempted) throw new InvalidOperationException("Observation is outside admission.");
                        var observation = boundary.Observe(); observations++;
                        return observation;
                    case "checkpoint":
                        if (state != "checkpointIntent" || observations != 1) throw new InvalidOperationException("Checkpoint requires an observed native scope and flushed intent.");
                        Once(ref checkpointAttempted); return boundary.Checkpoint();
                    case "run":
                        if (state != "dispatchIntent" || !checkpointAttempted || observations != 2) throw new InvalidOperationException("Run requires checkpoint, fresh observation and flushed dispatch intent.");
                        Once(ref runAttempted); return boundary.Run();
                    case "verify":
                        if (state != "dispatchIntent" || !runAttempted) throw new InvalidOperationException("Verify requires one returned dispatch.");
                        Once(ref verifyAttempted); return boundary.Verify();
                    case "restore":
                        if (state != "recoveryIntent" || runAttempted || checkpointAttempted) throw new InvalidOperationException("Restore requires a separately admitted recovery session.");
                        var destinations = arguments["payload"] as JArray;
                        if (destinations == null || destinations.Count != 1 || !JToken.DeepEquals(destinations, operation["recoveryDestinations"]))
                            throw new InvalidOperationException("Recovery destinations differ from reserved durable intent.");
                        Once(ref restoreAttempted); return boundary.Restore(destinations);
                    case "reconcile":
                        if (state != "outcomeUnknown" && state != "recoveryRequired" && state != "recoveryIntent" && state != "checkpointIntent" && state != "dispatchIntent" && state != "ready")
                            throw new InvalidOperationException("Reconciliation requires retained ownership.");
                        return boundary.Reconcile(operation);
                    default: throw new InvalidOperationException("Unknown Test Suite worker stage.");
                }
            }
            catch { failed = true; throw; }
        }
        private static void Once(ref bool attempted)
        {
            if (attempted) throw new InvalidOperationException("RecoveryRequired: native stage cannot replay.");
            attempted = true;
        }
        public JObject End(string token, string operationId)
        {
            if (token != Token || operationId != OperationId) throw new InvalidOperationException("InvalidLease: exact end identity required.");
            Dispose();
            if (!released) throw new InvalidOperationException("NativeCleanupUnverified: previous release failed.");
            return new JObject { ["released"] = true, ["operationId"] = OperationId };
        }
        public void Dispose()
        {
            if (disposed)
            {
                if (!released) throw new InvalidOperationException("NativeCleanupUnverified: previous release failed.");
                return;
            }
            disposed = true;
            var held = lease; lease = null;
            held?.Dispose(); // Failure propagates; no release receipt and no retry on an uncertain lease.
            released = true;
        }
    }
}
