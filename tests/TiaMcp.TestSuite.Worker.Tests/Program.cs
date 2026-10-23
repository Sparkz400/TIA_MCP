using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TiaMcp.TestSuite.V21;

internal static class Program
{
    private static readonly List<string> Checks = new List<string>();
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Checks.Add(message); }
    private static void Reject(Action action, string contains)
    {
        try { action(); } catch (Exception error) when ((error.InnerException ?? error).Message.Contains(contains)) { Checks.Add("refused: " + contains); return; }
        throw new Exception("Expected refusal: " + contains);
    }
    private static JObject Context(string state = "acquired") => JObject.Parse("{operation:{id:'operation',planSha256:'plan',revision:1,state:'" + state + "',plan:{exact:'identity'},recoveryDestinations:[{resourceKey:'original',recoveryLocation:'new'}]}}");
    private static JObject Args(StyleWorkerSession session, string state, int revision = 2)
    {
        var context = Context(state); context["operation"]["revision"] = revision;
        return new JObject { ["leaseToken"] = session.Token, ["operation"] = context["operation"].DeepClone(), ["payload"] = context["operation"]["recoveryDestinations"].DeepClone() };
    }
    private static void Ready(StyleWorkerSession s)
    { s.Execute("observe", Args(s, "acquired")); s.Execute("checkpoint", Args(s, "checkpointIntent", 3)); s.Execute("observe", Args(s, "ready", 4)); }
    private static int Main(string[] args)
    {
        var boundary = new Fake { Qualified = false };
        Reject(() => new StyleWorkerSession(boundary, Context()), "ProfileUnqualified");
        Check(boundary.Calls.SequenceEqual(new[] { "qualification" }), "Native qualification precedes any acquisition or observation");
        boundary = new Fake(); var session = new StyleWorkerSession(boundary, Context());
        Ready(session); session.Execute("run", Args(session, "dispatchIntent", 5)); session.Execute("verify", Args(session, "dispatchIntent", 6));
        session.End(session.Token, session.OperationId); session.Dispose();
        Check(boundary.Calls.SequenceEqual(new[] { "qualification", "acquire", "observe", "checkpoint", "observe", "run", "verify", "release" }), "Owning native boundary wires every ordered lifecycle stage and releases once");
        Reject(() => session.Execute("run", Args(session, "dispatchIntent", 7)), "RecoveryRequired");

        foreach (string point in new[] { "observe", "checkpoint", "run", "verify" })
        {
            boundary = new Fake { FailAt = point }; session = new StyleWorkerSession(boundary, Context());
            if (point == "observe") Reject(() => session.Execute("observe", Args(session, "acquired")), "fake " + point);
            else if (point == "checkpoint") { session.Execute("observe", Args(session, "acquired")); Reject(() => session.Execute("checkpoint", Args(session, "checkpointIntent", 3)), "fake " + point); }
            else
            {
                Ready(session);
                if (point == "run") Reject(() => session.Execute("run", Args(session, "dispatchIntent", 5)), "fake " + point);
                else { session.Execute("run", Args(session, "dispatchIntent", 5)); Reject(() => session.Execute("verify", Args(session, "dispatchIntent", 6)), "fake " + point); }
            }
            Reject(() => session.Execute(point, Args(session, "dispatchIntent", 7)), "RecoveryRequired");
            session.End(session.Token, session.OperationId);
            Check(boundary.Calls.Count(c => c == point) == (point == "observe" ? 1 : 1) && boundary.Calls.Last() == "release", point + " failure poisons session without replay and permits exact release");
        }
        foreach (string change in new[] { "leaseToken", "operation.id", "operation.planSha256", "operation.plan.exact", "operation.revision" })
        {
            boundary = new Fake(); session = new StyleWorkerSession(boundary, Context());
            var arguments = Args(session, "acquired"); arguments.SelectToken(change).Replace(change == "operation.revision" ? (JToken)new JValue(0) : new JValue("other"));
            Reject(() => session.Execute("observe", arguments), change == "leaseToken" ? "InvalidLease" : "StaleNativeTarget");
            Check(!boundary.Calls.Contains("observe"), "Changed " + change + " fails before boundary access"); session.Dispose();
        }
        foreach (string stage in new[] { "checkpoint", "run", "verify", "restore", "reconcile" })
        {
            boundary = new Fake(); session = new StyleWorkerSession(boundary, Context());
            Reject(() => session.Execute(stage, Args(session, "acquired")), stage == "checkpoint" ? "Checkpoint requires" : stage == "run" ? "Run requires" : stage == "verify" ? "Verify requires" : stage == "restore" ? "Restore requires" : "Reconciliation requires");
            Check(!boundary.Calls.Contains(stage), stage + " cannot cross missing durable intent"); session.Dispose();
        }
        boundary = new Fake(); session = new StyleWorkerSession(boundary, Context()); Ready(session);
        session.Execute("run", Args(session, "dispatchIntent", 5)); Reject(() => session.Execute("run", Args(session, "dispatchIntent", 5)), "cannot replay");
        Check(boundary.Calls.Count(c => c == "run") == 1, "Repeated native Run invokes boundary once"); session.Dispose();

        boundary = new Fake(); session = new StyleWorkerSession(boundary, Context("recoveryIntent"));
        session.Execute("restore", Args(session, "recoveryIntent")); Reject(() => session.Execute("restore", Args(session, "recoveryIntent")), "cannot replay"); session.Dispose();
        Check(boundary.Calls.Count(c => c == "restore") == 1, "Restoration to reserved destination is one attempt");
        boundary = new Fake { FailAt = "restore" }; session = new StyleWorkerSession(boundary, Context("recoveryIntent"));
        Reject(() => session.Execute("restore", Args(session, "recoveryIntent")), "fake restore");
        Reject(() => session.Execute("restore", Args(session, "recoveryIntent")), "RecoveryRequired"); session.Dispose();
        boundary = new Fake(); session = new StyleWorkerSession(boundary, Context("recoveryIntent"));
        var changedDestination = Args(session, "recoveryIntent"); changedDestination["payload"][0]["recoveryLocation"] = "original";
        Reject(() => session.Execute("restore", changedDestination), "destinations differ"); Check(!boundary.Calls.Contains("restore"), "Caller cannot replace reserved destination"); session.Dispose();
        boundary = new Fake(); session = new StyleWorkerSession(boundary, Context("outcomeUnknown"));
        session.Execute("reconcile", Args(session, "outcomeUnknown")); session.Dispose();
        Check(boundary.Calls.Contains("reconcile") && !boundary.Calls.Contains("run") && !boundary.Calls.Contains("restore"), "Reconciliation observes only and never replays native effects");
        boundary = new Fake { FailAt = "release" }; session = new StyleWorkerSession(boundary, Context());
        Reject(() => session.End(session.Token, session.OperationId), "fake release");
        Reject(() => session.End(session.Token, session.OperationId), "NativeCleanupUnverified");
        Check(boundary.Calls.Count(c => c == "release") == 1, "Lost release never manufactures a later success or repeats native disposal");

        string captures = Path.Combine(Path.GetTempPath(), "ts-worker-capture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(captures);
        string complete = Path.Combine(captures, "complete.json"), partial = Path.Combine(captures, "partial.json");
        byte[] raw = StyleRawCapture.Retain(complete, new JObject { ["scopeVerified"] = false }, messages => messages.Add(new JObject { ["description"] = "original native text", ["nativeTimestampKind"] = "Unknown" }));
        Check(raw.SequenceEqual(File.ReadAllBytes(complete)) && JArray.Parse(File.ReadAllText(complete))[0].Value<bool>("collectionComplete"), "Complete raw capture is flushed byte for byte before transport");
        Reject(() => StyleRawCapture.Retain(partial, new JObject { ["scopeVerified"] = false }, messages => { messages.Add(new JObject { ["description"] = "retained before throw" }); throw new InvalidOperationException("authored enumeration loss"); }), "enumeration loss");
        var retainedPartial = JArray.Parse(File.ReadAllText(partial))[0];
        Check(!retainedPartial.Value<bool>("collectionComplete") && retainedPartial["messages"].Count() == 1 && File.Exists(partial + ".failure.json"), "Interrupted enumeration retains incomplete raw tree and separate exception");
        bool overwriteRefused = false;
        try { StyleRawCapture.Retain(complete, new JObject(), _ => { }); } catch (IOException) { overwriteRefused = true; }
        Check(overwriteRefused && raw.SequenceEqual(File.ReadAllBytes(complete)), "Raw capture never overwrites an earlier receipt");
        string oversized = Path.Combine(captures, "oversized.json");
        Reject(() => StyleRawCapture.RetainBounded(oversized, new JObject { ["scopeVerified"] = false },
            messages => messages.Add(new JObject { ["description"] = new string('x', 5000) }), 128), "TestResultLimit");
        var oversizedFailure = JObject.Parse(File.ReadAllText(oversized + ".failure.json"));
        Check(new FileInfo(oversized).Length == 128 && oversizedFailure.Value<bool>("enumerationReturned") &&
            !oversizedFailure.Value<bool>("captureComplete") && oversizedFailure.Value<int>("retainedBytes") == 128,
            "Encoding overflow retains an exact bounded raw prefix and a flushed failure receipt");
        bool enumeratedAgain = false;
        try { StyleRawCapture.Retain(oversized, new JObject(), _ => enumeratedAgain = true); } catch (IOException) { }
        Check(!enumeratedAgain && new FileInfo(oversized).Length == 128, "A failed raw capture is never enumerated again or overwritten");
        UiEvidenceChecks.Run(Check, Reject);

        // Actual production worker dispatch, with no TiaPortal constructed. No fake constructor gets registered.
        string api = @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48";
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { string path = Path.Combine(api, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(path) ? Assembly.LoadFrom(path) : null; };
        var type = Assembly.Load("TiaMcp.Openness.V21").GetType("TiaMcp.Openness.V21.OpennessSession", true);
        object worker = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { api, false }, null);
        var execute = type.GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Reject(() => execute.Invoke(worker, new object[] { "testsuite_style_begin", Context() }), "No native Test Suite profile");
        Reject(() => execute.Invoke(worker, new object[] { "testsuite_scope_probe", new JObject { ["action"] = "prepare" } }), "unavailable on normal workers");
        object probeWorker = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { api, false, true }, null);
        Reject(() => execute.Invoke(probeWorker, new object[] { "testsuite_scope_probe", new JObject { ["action"] = "run" } }), "Only prepare/read");
        Reject(() => execute.Invoke(probeWorker, new object[] { "testsuite_style_begin", Context() }), "No native Test Suite profile");
        Reject(() => execute.Invoke(probeWorker, new object[] { "testsuite_scope_probe", new JObject { ["action"] = "prepare" } }), "Attach to or explicitly start");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Siemens.Engineering.TestSuite"), "Disabled production dispatch does not load optional Siemens TestSuite product");
        foreach (string stage in new[] { "observe", "checkpoint", "run", "verify", "restore", "reconcile", "end" })
            Reject(() => execute.Invoke(worker, new object[] { "testsuite_style_" + stage, new JObject() }), "No owning-worker Test Suite lease");
        boundary = new Fake(); session = new StyleWorkerSession(boundary, Context());
        type.GetField("_testSuiteSession", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(worker, session);
        foreach (string operation in new[] { "start", "attach", "detach", "project_open", "watch_begin", "mutation_begin", "bridge_info", "testsuite_style_begin" })
            Reject(() => execute.Invoke(worker, new object[] { operation, new JObject() }), "holds exclusive access");
        execute.Invoke(worker, new object[] { "testsuite_style_end", new JObject { ["leaseToken"] = session.Token, ["operationId"] = session.OperationId } });
        Check(type.GetField("_testSuiteSession", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(worker) == null, "Actual registered end clears only the exact released lease");
        if (args.Length == 1)
        {
            using (var writer = new StreamWriter(new FileStream(args[0], FileMode.CreateNew)))
                writer.Write(new JObject { ["checks"] = Checks.Count, ["names"] = new JArray(Checks), ["nativeExecuted"] = false, ["nativeQualified"] = false });
        }
        Console.WriteLine("PASS Test Suite owning-worker fake/reflection boundary: " + Checks.Count + " checks; no native calls.");
        return 0;
    }
    private sealed class Fake : IStyleWorkerBoundary
    {
        public readonly List<string> Calls = new List<string>();
        public bool Qualified = true;
        public string FailAt;
        private JObject Call(string stage) { Calls.Add(stage); if (FailAt == stage) throw new InvalidOperationException("fake " + stage); return new JObject { ["authoredFake"] = stage }; }
        public void RequireQualification(JObject context) { Call("qualification"); if (!Qualified) throw new InvalidOperationException("ProfileUnqualified"); }
        public IDisposable Acquire(JObject context) { Call("acquire"); return new Lease(this); }
        public JObject Observe() => Call("observe");
        public JArray Checkpoint() => new JArray(Call("checkpoint"));
        public JObject Run() => Call("run");
        public JObject Verify() => Call("verify");
        public JObject Restore(JArray destinations) => Call("restore");
        public JObject Reconcile(JObject operation) => Call("reconcile");
        private sealed class Lease : IDisposable { private readonly Fake owner; public Lease(Fake owner) { this.owner = owner; } public void Dispose() { owner.Call("release"); } }
    }
}
