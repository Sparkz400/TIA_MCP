using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Translations;
using static TiaMcp.Translations.TranslationJson;

if (args.Length == 2 && args[0] == "--worker-gate")
{
    var bridge = new BridgeClient(new(args[1]));
    try
    {
        foreach (string operation in new[] { "translation_fixture", "translation_capture", "translation_categories", "translation_begin" })
        {
            try { await bridge.CallAsync(operation, new { }); throw new Exception("Production worker gate opened: " + operation); }
            catch (BridgeException error) when (error.Code == "TranslationImportProfileUnqualified") { Console.WriteLine("PASS production worker refuses " + operation + " before native lookup"); }
        }
    }
    finally { await bridge.DisposeAsync(); }
    return bridge.WorkerShutdownSucceeded == true ? 0 : 1;
}

if (args.Length == 2 && args[0] == "--crash-child")
{
    var child = new Fixture(args[1]); var operation = child.Prepare();
    File.WriteAllText(Path.Combine(args[1], "operation-id.txt"), operation.Id);
    var crash = new TranslationImportWorkflow(child.Root, point => { if (point == "dispatchIntent") Environment.Exit(73); });
    await crash.Apply(operation.Id, operation.PlanSha256, operation.Revision, child.Adapter);
    return 99;
}
bool admissionOnly = args.Length == 2 && args[0] == "--admission-only";
string output = admissionOnly ? Path.GetFullPath(args[1]) : args.Length == 1 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "translation-import-" + Guid.NewGuid().ToString("N"));
if (Directory.Exists(output)) throw new ArgumentException("Use a fresh output directory.");
Directory.CreateDirectory(output);
var tests = new List<(string Name, Func<Task> Body)>();
void Test(string name, Func<Task> body) => tests.Add((name, body));
Fixture New(bool edit = true) => new(Path.Combine(output, Guid.NewGuid().ToString("N")), edit);
void True(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
async Task Reject(string code, Func<Task> action)
{
    try { await action(); } catch (BridgeException error) when (error.Code == code) { return; }
    throw new Exception("Expected " + code);
}
async Task Interrupted(Func<Task> action)
{
    try { await action(); } catch (IOException) { return; }
    throw new Exception("Expected injected interruption");
}
async Task<(Fixture Fixture, EngineeringOperationRecord Operation, byte[] Proof)> BeforeCheckpoint()
{
    var f = New(false); string worker = Path.Combine(f.Root, "synthetic-old-worker"); File.WriteAllText(worker, "Synthetic worker identity, never executable");
    var observed = Fixture.Bind(f.Observation() with { Target = f.Target with { AdapterSha256 = Sha256(File.ReadAllBytes(worker)) } });
    var op = f.Runner.Prepare(f.CandidateRef, f.CandidateReceipt, observed, "pre-checkpoint");
    await Interrupted(() => new TranslationImportWorkflow(f.Root, p => { if (p == "acquired") throw new IOException("synthetic pre-checkpoint interruption"); })
        .Apply(op.Id, op.PlanSha256, op.Revision, f.Adapter));
    return (f, f.Runner.Status(op.Id), ClosedProof(worker));
}
byte[] ClosedProof(string worker) => Fixture.Json(new { tree = new { completed = true, forcedTermination = false, startupOutcomeUnknown = false,
        exits = new[] { "host", "worker", "native" }.Select(role => new { role, identity = new { exe = worker },
            proof = new { completed = true, processExitVerified = true, birthAndImageVerified = true, sameOsHandleRetained = true,
                handleClosed = true, exitCode = 0, deadlineOrCancellationObserved = false } }).ToArray() } });

Test("completed owned fixture resumes by observation without repeating import or recovery", async () => {
    string root = Path.Combine(output, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    string worker = Path.Combine(root, "synthetic-worker"); File.WriteAllText(worker, "Synthetic test worker, never executable");
    var f = new Fixture(root, false, adapterHash: Sha256(File.ReadAllBytes(worker)));
    var op = await f.Apply(f.Prepare());
    var recovery = await f.Runner.Recover(op.Id, op.PlanSha256, op.Revision, f.RecoveryPath, f.Adapter);
    var done = await f.Runner.Reconcile(op.Id, op.PlanSha256, recovery.Revision, f.Adapter);
    var evidence = await f.Runner.ResumeClosedFixture(op.Id, done.Revision, f.Adapter, ClosedProof(worker));
    True(File.Exists(Path.Combine(root, evidence.RelativePath)) && f.Adapter.Imports == 1 && f.Adapter.Restores == 1 && f.Runner.Status(op.Id).Revision == done.Revision);
});
Test("owned fixture resume cannot adopt an unresolved operation", async () => {
    var f = New(); var op = f.Prepare();
    await Reject("TranslationReconciliationUnqualified", () => f.Runner.ResumeClosedFixture(op.Id, op.Revision, f.Adapter, []));
    True(f.Adapter.Opens == 0);
});

Test("explicit pre-checkpoint adapter transition observes unchanged content without replay", async () => {
    var (f, op, proof) = await BeforeCheckpoint();
    var done = await f.Runner.ReconcileBeforeCheckpoint(op.Id, op.Revision, f.Adapter, proof);
    True(done.State == "reconciled" && done.Reconciliation.Single().Disposition == "unchanged" && f.Adapter.Imports == 0 && f.Adapter.Checkpoints == 0);
});
Test("pre-checkpoint transition refuses incomplete original lifetime proof", async () => {
    var (f, op, proof) = await BeforeCheckpoint(); var altered = JsonNode.Parse(proof)!;
    altered["tree"]!["exits"]![0]!["proof"]!["handleClosed"] = false;
    await Reject("NativeCleanupUnverified", () => f.Runner.ReconcileBeforeCheckpoint(op.Id, op.Revision, f.Adapter, Encoding.UTF8.GetBytes(altered.ToJsonString())));
    True(f.Adapter.Opens == 0 && f.Runner.Status(op.Id).State == "outcomeUnknown");
});
Test("pre-checkpoint transition refuses changed original texts and retains ownership", async () => {
    var (f, op, proof) = await BeforeCheckpoint(); f.Adapter.Current = Fixture.Change(f.Original, "F2", "foreign edit");
    await Reject("TranslationReconciliationUnqualified", () => f.Runner.ReconcileBeforeCheckpoint(op.Id, op.Revision, f.Adapter, proof));
    True(f.Runner.Status(op.Id).State == "outcomeUnknown" && f.Adapter.Imports == 0);
});

Test("production gate refuses before native lease, resource acquisition or checkpoint", async () => {
    var f = New(); var op = f.Prepare();
    await Reject("TranslationImportProfileUnqualified", () => f.Runner.Apply(op.Id, op.PlanSha256, op.Revision, new TranslationProductionImportAdapter()));
    True(f.Runner.Status(op.Id).State == "prepared" && f.Adapter.Opens == 0 && new EngineeringOperationStore(f.Root).Pending().Count == 0);
    True(TranslationProductionImportAdapter.QualifiedProfiles.Length == 0);
});
Test("native peer comparison retains all content except creation time and exact approved culture text", () => {
    var f = New(); var candidate = new TranslationWorkflowStore(f.Root).NativeCandidate(f.CandidateRef, f.CandidateReceipt);
    byte[] Peer(string time, string culture, string text, string code = "unchanged")
    {
        var items = new[] { "de-DE", "en-GB" }.Select(c => new XElement("MultilingualTextItem",
            new XElement("AttributeList", new XElement("Culture", c),
                new XElement("Text", c == culture ? text : c == "en-GB" ? "Ready" : "Freigabe"))));
        var comment = new XElement("MultilingualText", new XAttribute("CompositionName", "Comment"), new XElement("ObjectList", items));
        var block = new XElement("SW.Blocks.OB", new XElement("AttributeList", new XElement("Name", "Main"), new XElement("Number", "1")), new XElement("ObjectList", comment));
        return Encoding.UTF8.GetBytes(new XElement("Document", new XElement("DocumentInfo", new XElement("Created", time)),
            new XElement("Code", code), block).ToString(SaveOptions.DisableFormatting));
    }
    byte[] Normalize(byte[] bytes) => TranslationNativeComparison.Peer(bytes, "tia21-simaticml-translation-v1", candidate);
    var before = Peer("old", "en-GB", "Ready"); var after = Peer("new", "en-GB", "Kühlung Ω bereit");
    True(Normalize(before).SequenceEqual(Normalize(after)));
    True(!Normalize(before).SequenceEqual(Normalize(Peer("new", "en-GB", "Unexpected"))));
    True(!Normalize(before).SequenceEqual(Normalize(Peer("new", "en-GB", "Kühlung Ω bereit", "changed logic"))));
    True(!Normalize(Peer("old", "de-DE", "Ready")).SequenceEqual(Normalize(Peer("new", "de-DE", "Kühlung Ω bereit"))));
    var unrelated = XElement.Parse(Encoding.UTF8.GetString(before));
    unrelated.Element("SW.Blocks.OB")!.Element("ObjectList")!.Add(new XElement("MultilingualText", new XAttribute("CompositionName", "Title"),
        new XElement("ObjectList", new XElement("MultilingualTextItem", new XElement("AttributeList", new XElement("Culture", "en-GB"), new XElement("Text", "Ready"))))));
    var changedTitle = new XElement(unrelated); changedTitle.Descendants("Text").Last().Value = "Kühlung Ω bereit";
    True(!Normalize(Encoding.UTF8.GetBytes(unrelated.ToString())).SequenceEqual(Normalize(Encoding.UTF8.GetBytes(changedTitle.ToString()))));
    True(TranslationNativeComparison.Peer(before, "exact", candidate).SequenceEqual(before));
    return Task.CompletedTask;
});
Test("unknown native peer profile cannot bypass preservation", async () => {
    var f = New(); var value = Fixture.Bind(f.Observation() with { Peers = [f.Peers[0] with { ComparisonProfile = "ignore-everything" }, f.Peers[1]] });
    await Reject("TranslationPeerProfileUnqualified", () => { f.Runner.Prepare(f.CandidateRef, f.CandidateReceipt, value, "profile"); return Task.CompletedTask; });
});
foreach (bool edit in new[] { false, true }) Test("one " + (edit ? "nonempty edit" : "no-op") + " dispatch, full readback, save, reopen and cleanup", async () => {
    var f = New(edit); var op = f.Prepare(); var done = await f.Apply(op);
    True(done.State == "succeeded" && f.Adapter.Imports == 1 && f.Adapter.Checkpoints == 1 && f.Adapter.Saves == 1 && f.Adapter.Closes == 1 && f.Adapter.Disposals == 1);
    True(!f.Adapter.CancellableMutation && done.Evidence.Any(e => e.Gate == "dispatch.completeLog"));
    True(Fixture.Cell(f.Adapter.Current, "F2") == (edit ? "Kühlung Ω bereit" : "Ready"));
    True(Fixture.Cell(f.Adapter.Current, "E2") == "Freigabe" && Fixture.Cell(f.Adapter.Current, "F3") == "Peer text");
    True(new TranslationImportWorkflow(f.Root).Status(op.Id).State == "succeeded");
    await Reject("RecoveryRequired", () => f.Apply(done)); True(f.Adapter.Imports == 1);
});
Test("exact operation review hash and revision are mandatory", async () => {
    var f = New(); var op = f.Prepare();
    await Reject("ReviewApprovalMismatch", () => f.Runner.Apply(op.Id, new('0', 64), 0, f.Adapter));
    await Reject("RevisionConflict", () => f.Runner.Apply(op.Id, op.PlanSha256, 1, f.Adapter));
    True(f.Adapter.Opens == 0);
});
Test("candidate receipt approval and byte tampering cannot acquire", async () => {
    var f = New(); await Reject("ReviewApprovalMismatch", () => { f.Runner.Prepare(f.CandidateRef, new('0', 64), f.Observation(), "bad"); return Task.CompletedTask; });
    var op = f.Prepare(); string path = Path.Combine(f.Root, "translation-workflow", "candidate", f.CandidateRef.Split(':')[2], "candidate.xlsx");
    File.AppendAllText(path, "corrupt"); await Reject("TranslationIntegrityFailure", () => f.Apply(op)); True(f.Adapter.Opens == 0);
});
Test("tampered retained original peer bytes refuse before native lease", async () => {
    var f = New(); var op = f.Prepare(); File.AppendAllText(Path.Combine(f.Root, f.Peers[0].Evidence.RelativePath), "tampered");
    await Reject("TranslationIntegrityFailure", () => f.Apply(op)); True(f.Adapter.Opens == 0);
});
Test("no approval cannot become a native candidate", async () => {
    var f = New(); await Reject("InvalidTranslationReference", () => { f.Runner.Prepare("upload:any", f.CandidateReceipt, f.Observation(), "bad"); return Task.CompletedTask; });
});
foreach (string shape in new[] { "source", "clear", "empty-target" }) Test("initial native packet refuses " + shape + " writes", async () => {
    var f = new Fixture(Path.Combine(output, Guid.NewGuid().ToString("N")), true, shape);
    await Reject("TranslationImportShapeUnqualified", () => { f.Prepare(); return Task.CompletedTask; }); True(f.Adapter.Opens == 0);
});
Test("complete initial fresh export detects unchanged mapped-peer drift", async () => {
    var f = New(); var observed = f.Observation(Fixture.Change(f.Original, "F3", "changed peer"));
    await Reject("StaleWorkbook", () => { f.Runner.Prepare(f.CandidateRef, f.CandidateReceipt, observed, "drift"); return Task.CompletedTask; });
});
Test("native receipt must bind actual workbook and full peers", async () => {
    var f = New(); var observation = f.Observation() with { Workbook = Fixture.Change(f.Original, "F2", "forged") };
    await Reject("TranslationObservationMismatch", () => { f.Runner.Prepare(f.CandidateRef, f.CandidateReceipt, observation, "forged"); return Task.CompletedTask; });
});
Test("unsupported peer hash claims cannot replace original export evidence", async () => {
    var f = New(); var value = Fixture.Bind(f.Observation() with { Peers = [f.Peers[0] with { Sha256 = new('0', 64) }, f.Peers[1]] });
    await Reject("TranslationPreservationFailure", () => { f.Runner.Prepare(f.CandidateRef, f.CandidateReceipt, value, "bad-peer"); return Task.CompletedTask; });
});
foreach (string stage in new[] { "acquired", "checkpointIntent", "checkpointReturned", "checkpointRetained", "dispatchIntent", "rawResultRetained", "readbackRetained", "compiled", "saved", "reopened", "closed", "completed" })
Test("interruption at " + stage + " retains outcome and never replays", async () => {
    var f = New(); var op = f.Prepare();
    var runner = new TranslationImportWorkflow(f.Root, point => { if (point == stage) throw new IOException("interrupted:" + point); });
    await Interrupted(() => runner.Apply(op.Id, op.PlanSha256, op.Revision, f.Adapter));
    var status = new TranslationImportWorkflow(f.Root).Status(op.Id);
    string expected = stage == "completed" ? "succeeded" : new[] { "acquired", "checkpointIntent", "checkpointReturned", "checkpointRetained", "dispatchIntent" }.Contains(stage) ? "outcomeUnknown" : "recoveryRequired";
    True(status.State == expected, status.State + " at " + stage);
    int imports = f.Adapter.Imports;
    await Reject("RecoveryRequired", () => f.Apply(status)); True(f.Adapter.Imports == imports && imports <= 1);
    True(stage == "acquired" || f.Adapter.Disposals == 1);
});
foreach (int observation in new[] { 1, 2 }) foreach (string what in new[] { "source", "target", "peer-row", "languages", "categories", "objects", "peers", "epoch", "adapter", "profile", "offline", "path", "key", "hidden-sheet" })
Test($"fresh observation {observation} rejects {what} drift before dispatch", async () => {
    var f = New(); var op = f.Prepare();
    f.Adapter.Alter = (n, value) => n == observation ? f.Drift(value, what) : value;
    string code = what switch { "source" or "target" or "peer-row" => "StaleWorkbook", "languages" or "categories" or "objects" or "profile" => "TranslationScopeMismatch",
        "peers" => "TranslationPreservationFailure", "key" => "StaleTextKey", "hidden-sheet" => "UnsupportedTranslationScopeLayout", "path" => "IdentityMismatch", _ => "StaleNativeTarget" };
    await Reject(code, () => f.Apply(op)); True(f.Adapter.Imports == 0 && f.Adapter.Checkpoints == observation - 1);
    True(new EngineeringOperationStore(f.Root).Pending().Count == 1);
});
Test("shared project ownership excludes a competing operation", async () => {
    var f = New(); var first = f.Prepare(); var second = f.Prepare("other");
    await Interrupted(() => new TranslationImportWorkflow(f.Root, p => { if (p == "acquired") throw new IOException(); }).Apply(first.Id, first.PlanSha256, 0, f.Adapter));
    await Reject("ResourceBusy", () => f.Apply(second)); True(f.Adapter.Opens == 0);
});
Test("same controller lock excludes reentrant apply", async () => {
    var f = New(); var op = f.Prepare(); bool refused = false;
    await new TranslationImportWorkflow(f.Root, p => { if (p != "acquired") return;
        try { f.Apply(op).GetAwaiter().GetResult(); } catch (BridgeException e) when (e.Code == "TranslationImportBusy") { refused = true; }
    }).Apply(op.Id, op.PlanSha256, 0, f.Adapter);
    True(refused && f.Adapter.Imports == 1);
});
foreach (string what in new[] { "source", "target", "peer-row", "peers", "languages", "key", "hidden-sheet" })
Test("returned import with " + what + " damage keeps recovery ownership and prevents save", async () => {
    var f = New(); var op = f.Prepare(); f.Adapter.Alter = (n, value) => n == 3 ? f.Drift(value, what) : value;
    try { await f.Apply(op); throw new Exception("Accepted damage"); } catch (BridgeException) { }
    var status = f.Runner.Status(op.Id);
    True(status.State == "recoveryRequired" && f.Adapter.Imports == 1 && f.Adapter.Saves == 0 && status.Evidence.Any(e => e.Gate == "dispatch.readback"));
});
foreach (string fault in new[] { "result", "malformed-result", "log", "compile", "save", "reopen", "cleanup", "dispose", "compile-lost", "save-lost", "reopen-lost", "cleanup-lost" })
Test("incomplete " + fault + " never releases ownership", async () => {
    var f = New(); var op = f.Prepare(); f.Adapter.Fault = fault;
    try { await f.Apply(op); throw new Exception("Accepted " + fault); } catch (Exception e) when (e is BridgeException or System.Text.Json.JsonException or IOException) { }
    True(f.Runner.Status(op.Id).State == (fault == "dispose" || fault.EndsWith("-lost") ? "outcomeUnknown" : "recoveryRequired") && f.Adapter.Imports == 1 && new EngineeringOperationStore(f.Root).Pending().Count == 1);
});
Test("cancel before dispatch never imports; after dispatch token is not forwarded", async () => {
    var f = New(); var op = f.Prepare(); using var cancellation = new CancellationTokenSource();
    var runner = new TranslationImportWorkflow(f.Root, p => { if (p == "checkpointRetained") cancellation.Cancel(); });
    try { await runner.Apply(op.Id, op.PlanSha256, 0, f.Adapter, cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    True(f.Adapter.Imports == 0 && f.Runner.Status(op.Id).State == "outcomeUnknown");
    var g = New(); var next = g.Prepare(); using var afterIntent = new CancellationTokenSource();
    await new TranslationImportWorkflow(g.Root, p => { if (p == "dispatchIntent") afterIntent.Cancel(); }).Apply(next.Id, next.PlanSha256, 0, g.Adapter, afterIntent.Token);
    True(g.Adapter.Imports == 1 && !g.Adapter.CancellableMutation);
});
Test("lost import response retains unknown even if native fixture changed", async () => {
    var f = New(); var op = f.Prepare(); f.Adapter.Fault = "import-lost";
    await Interrupted(() => f.Apply(op)); True(f.Runner.Status(op.Id).State == "outcomeUnknown" && f.Adapter.Imports == 1);
    f.Adapter.Fault = null; var status = f.Runner.Status(op.Id);
    var done = await f.Runner.Reconcile(op.Id, op.PlanSha256, status.Revision, f.Adapter);
    True(done.State == "reconciled" && done.Reconciliation.Single().Disposition == "verifiedApplied" && f.Adapter.Imports == 1);
});
Test("checkpoint recovery reserves new copy and requires independent fresh reopen", async () => {
    var f = New(); var op = await f.Apply(f.Prepare());
    await Reject("InvalidRecovery", () => f.Runner.Recover(op.Id, op.PlanSha256, op.Revision, f.Target.ProjectFilePath, f.Adapter));
    var recovery = await f.Runner.Recover(op.Id, op.PlanSha256, op.Revision, f.RecoveryPath, f.Adapter);
    True(recovery.State == "recoveryIntent" && f.Adapter.Restores == 1 && new EngineeringOperationStore(f.Root).Pending().Count == 1);
    await Reject("RecoveryRequired", () => f.Runner.Recover(op.Id, op.PlanSha256, recovery.Revision, f.RecoveryPath, f.Adapter));
    var done = await new TranslationImportWorkflow(f.Root).Reconcile(op.Id, op.PlanSha256, recovery.Revision, f.Adapter);
    True(done.State == "reconciled" && done.RecoveryResolutions.Single().Disposition == "verifiedRestored" && f.Adapter.Restores == 1 && f.Adapter.Imports == 1);
});
foreach (string point in new[] { "recoveryIntent", "restore-lost", "recoveryReturned" })
Test("recovery interruption " + point + " reconciles existing destination without replay", async () => {
    var f = New(); var op = await f.Apply(f.Prepare()); f.Adapter.Fault = point == "restore-lost" ? point : null;
    var runner = new TranslationImportWorkflow(f.Root, p => { if (p == point) throw new IOException("recovery interrupted"); });
    await Interrupted(() => runner.Recover(op.Id, op.PlanSha256, op.Revision, f.RecoveryPath, f.Adapter));
    var status = runner.Status(op.Id); True(status.State == "outcomeUnknown"); int count = f.Adapter.Restores;
    await Reject("RecoveryRequired", () => f.Runner.Recover(op.Id, op.PlanSha256, status.Revision, f.RecoveryPath, f.Adapter));
    f.Adapter.Fault = null; var done = await f.Runner.Reconcile(op.Id, op.PlanSha256, status.Revision, f.Adapter);
    True(done.State == "reconciled" && f.Adapter.Restores == count && done.RecoveryResolutions.Single().Disposition == (point == "recoveryIntent" ? "verifiedAbsent" : "verifiedRestored"));
});
foreach (string fault in new[] { "recovery-content", "recovery-path", "recovery-epoch", "recovery-peer", "recovery-absence" })
Test("reconciliation refuses " + fault + " and keeps both reservations", async () => {
    var f = New(); var op = await f.Apply(f.Prepare()); var recovery = await f.Runner.Recover(op.Id, op.PlanSha256, op.Revision, f.RecoveryPath, f.Adapter);
    f.Adapter.Fault = fault;
    try { await f.Runner.Reconcile(op.Id, op.PlanSha256, recovery.Revision, f.Adapter); throw new Exception("Accepted recovery damage"); }
    catch (BridgeException) { }
    True(f.Runner.Status(op.Id).State == "recoveryIntent" && new EngineeringOperationStore(f.Root).Pending().Count == 1);
});
Test("archive tampering refuses recovery before opening a native lease", async () => {
    var f = New(); var op = await f.Apply(f.Prepare());
    var artifactStore = new TranslationImportArtifacts(f.Root); var checkpoint = artifactStore.Read<TranslationRetainedCheckpoint>(op.Checkpoints.Single());
    File.AppendAllText(Path.Combine(f.Root, checkpoint.Archive.RelativePath), "tampered"); int opened = f.Adapter.Opens;
    await Reject("TranslationIntegrityFailure", () => f.Runner.Recover(op.Id, op.PlanSha256, op.Revision, f.RecoveryPath, f.Adapter)); True(f.Adapter.Opens == opened);
});
Test("existing recovery directory is refused before native lease or reservation", async () => {
    var f = New(); var op = await f.Apply(f.Prepare()); Directory.CreateDirectory(Path.GetDirectoryName(f.RecoveryPath)!);
    await Reject("TranslationRecoveryDestinationExists", () => f.Runner.Recover(op.Id, op.PlanSha256, op.Revision, f.RecoveryPath, f.Adapter));
    True(f.Adapter.Restores == 0 && f.Runner.Status(op.Id).State == "succeeded");
});
Test("actual managed process loss at flushed dispatch intent survives restart", async () => {
    string childRoot = Path.Combine(output, "process-loss");
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(typeof(Fixture).Assembly.Location); start.ArgumentList.Add("--crash-child"); start.ArgumentList.Add(childRoot);
    using var process = Process.Start(start)!; await process.WaitForExitAsync(); True(process.ExitCode == 73);
    string id = File.ReadAllText(Path.Combine(childRoot, "operation-id.txt")); var runner = new TranslationImportWorkflow(childRoot); var op = runner.Status(id);
    True(op.State == "dispatchIntent" && new EngineeringOperationStore(childRoot).Pending().Count == 1);
    await Reject("RecoveryRequired", () => runner.Apply(id, op.PlanSha256, op.Revision, new TranslationProductionImportAdapter()));
    True(runner.MarkInterrupted(id, op.Revision).State == "outcomeUnknown");
});

if (admissionOnly) tests.Clear();
TranslationAdmissionChecks.Register(Test, () => new Fixture(Path.Combine(output, Guid.NewGuid().ToString("N")), exactAdmission: true));
TranslationRegistryChecks.Register(Test, output);
var results = new JsonArray(); int failed = 0;
foreach (var test in tests)
{
    try { await test.Body(); Console.WriteLine("PASS " + test.Name); results.Add(new JsonObject { ["name"] = test.Name, ["passed"] = true }); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error); results.Add(new JsonObject { ["name"] = test.Name, ["passed"] = false, ["error"] = error.ToString() }); }
}
File.WriteAllText(Path.Combine(output, "verification.json"), new JsonObject { ["passed"] = tests.Count - failed, ["failed"] = failed,
    ["nativeExecuted"] = false, ["syntheticAdapter"] = true, ["checks"] = results }.ToJsonString());
Console.WriteLine($"{tests.Count - failed}/{tests.Count} translation import checks passed; synthetic callbacks only, production native gate closed.");
return failed == 0 ? 0 : 1;

internal sealed class Fixture
{
    internal string Root { get; }
    internal string RecoveryPath => Path.Combine(Root, "restored", "Fixture.ap21");
    internal TranslationImportWorkflow Runner { get; }
    internal FakeAdapter Adapter { get; }
    internal WorkbookProfile Profile { get; }
    internal NativeTargetObservation Target { get; }
    internal TranslationImportScope Scope { get; } = new("de-DE", "en-GB", ["de-DE", "en-GB", "nl-NL"], ["plc:Fixture"], ["BlockCommentCategoryData"]);
    internal TranslationImportPeer[] Peers { get; }
    internal byte[] Original { get; }
    internal string CandidateRef { get; }
    internal string CandidateReceipt { get; }
    internal Fixture(string root, bool edit = true, string? shape = null, string? adapterHash = null, bool exactAdmission = false)
    {
        Root = root; Directory.CreateDirectory(root);
        if (exactAdmission) Scope = new("de-DE", "en-GB", ["de-DE", "en-GB"], ["whole-project:legacy-bilingual"], ["BlockCommentCategoryData"]);
        var nativeArtifacts = new TranslationImportArtifacts(root);
        var nativePeers = new[] { ("all-other-project-content", "Synthetic complete unaffected export"), ("untouched:nl-NL", "Synthetic original Dutch text") };
        Peers = nativePeers.Select(p => { var evidence = nativeArtifacts.RetainBytes(Encoding.UTF8.GetBytes(p.Item2)); return new TranslationImportPeer(p.Item1, evidence.Sha256, evidence); }).ToArray();
        Profile = new("synthetic-native-layout", "synthetic", "de-DE", [Row("K-1", 2), Row("K-2", 3)]);
        var identity = new EngineeringObjectReference(new(Path.Combine(root, "original", "Fixture.ap21"), null, null, "/project/Fixture", "project"), new("capture-1", new('a', 64)), new('b', 64));
        Target = new(identity, identity.Object.ProjectPath, "native-epoch-1", adapterHash ?? new('e', 64), HashJson(Profile), "synthetic-CPU", "native-project-1", true,
            new[] { "translation.languages", "translation.scope", "translation.preservation" }.Select(n => new TargetProbe(n, "observed", new('f', 64))).ToArray());
        Original = shape == "empty-target" ? Change(Workbook(), "F2", "") : Workbook(); var store = new TranslationWorkflowStore(root); var upload = new ArtifactUploadStore(root);
        string Upload(byte[] bytes, string name) { var item = upload.Begin(name, bytes.Length, Sha256(bytes)); upload.Write(item.UploadId, 0, Convert.ToBase64String(bytes)); return upload.Commit(item.UploadId).ArtifactRef!; }
        var capture = store.Capture(Upload(Original, "original.xlsx"), Upload(Json(Profile), "profile.json"), Upload(Json(identity), "identity.json"));
        string culture = shape == "source" ? "de-DE" : "en-GB";
        var wb = TranslationWorkbook.Open(Original, Profile, identity, Sha256(Original)); var pair = wb.Read("K-1", culture);
        var edits = edit ? new[] { new TranslationEdit("K-1", culture, shape == "clear" ? TranslationEditKind.Clear : TranslationEditKind.Set,
            shape == "clear" ? null : "Kühlung Ω bereit", pair.Source.Sha256, pair.Target.Sha256) } : [];
        var review = store.Prepare(capture["recordRef"]!.GetValue<string>(), Upload(Json(new TranslationProposalBatch(edits, new(UpdateSourceLanguage: shape == "source"))), "proposals.json"));
        string reference = review["recordRef"]!.GetValue<string>(), digest = review["metadata"]!["reviewSha256"]!.GetValue<string>();
        var approval = store.Approve(reference, digest, "synthetic test reviewer", true);
        var candidate = store.Candidate(reference, digest, approval["recordSha256"]!.GetValue<string>());
        CandidateRef = candidate["recordRef"]!.GetValue<string>(); CandidateReceipt = candidate["recordSha256"]!.GetValue<string>();
        Runner = new(root); Adapter = new(this);
    }
    private static TranslationRow Row(string key, int row) => new(key, "<BlockCommentCategoryData>", new("xl/worksheets/sheet1.xml", "C" + row),
        new("xl/worksheets/sheet1.xml", "E" + row), [new("en-GB", new("xl/worksheets/sheet1.xml", "F" + row), AllowClear: true)]);
    internal EngineeringOperationRecord Prepare(string key = "one") => Runner.Prepare(CandidateRef, CandidateReceipt, Observation(), key);
    internal Task<EngineeringOperationRecord> Apply(EngineeringOperationRecord op) => Runner.Apply(op.Id, op.PlanSha256, op.Revision, Adapter);
    internal TranslationImportObservation Observation(byte[]? bytes = null, NativeTargetObservation? target = null) => Bind(new(target ?? Target, bytes ?? Original, Scope, Peers, []));
    internal static TranslationImportObservation Bind(TranslationImportObservation o) => o with { Receipt = Json(new TranslationObservationReceipt(o.Target, Sha256(o.Workbook), HashJson(o.Scope), HashJson(o.Peers))) };
    internal TranslationImportObservation Drift(TranslationImportObservation v, string what) => Bind(what switch {
        "source" => v with { Workbook = Change(v.Workbook, "E2", "drift") }, "target" => v with { Workbook = Change(v.Workbook, "F2", "drift") },
        "peer-row" => v with { Workbook = Change(v.Workbook, "F3", "drift") }, "key" => v with { Workbook = Change(v.Workbook, "C2", "foreign") },
        "languages" => v with { Scope = Scope with { ActiveCultures = ["de-DE", "en-GB"] } },
        "categories" => v with { Scope = Scope with { CategoryIds = ["Other"] } }, "objects" => v with { Scope = Scope with { ObjectSelections = ["Other"] } },
        "peers" => v with { Peers = [ChangedPeer(), Peers[1]] },
        "epoch" => v with { Target = v.Target with { SessionEpoch = "other" } }, "adapter" => v with { Target = v.Target with { AdapterSha256 = new('0', 64) } },
        "profile" => v with { Target = v.Target with { ProfileSha256 = new('0', 64) } }, "offline" => v with { Target = v.Target with { Offline = false } },
        "path" => v with { Target = v.Target with { ProjectFilePath = Path.Combine(Root, "other.ap21") } },
        "hidden-sheet" => v with { Workbook = Modify(v.Workbook, "xl/workbook.xml", doc => doc.Descendants(S + "sheet").Last().SetAttributeValue("state", "hidden")) },
        _ => throw new Exception("Unknown drift") });
    private TranslationImportPeer ChangedPeer()
    {
        var evidence = new TranslationImportArtifacts(Root).RetainBytes(Encoding.UTF8.GetBytes("Changed native peer export"));
        return Peers[0] with { Sha256 = evidence.Sha256, Evidence = evidence };
    }
    internal static byte[] Json<T>(T value) => Encoding.UTF8.GetBytes(CanonicalJson.Serialize(Node(value)));
    internal static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace D = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static byte[] Workbook()
    {
        XElement CellNode(string address, string text) => new(S + "c", new XAttribute("r", address), new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", text)));
        var headers = new[] { "Category", "ViewPath", "Internal ID", "Substitutions", "de-DE*", "en-GB" }.Concat(Enumerable.Range(0, 16).Select(n => "ref=\"" + n + "\""));
        XElement Header() => new(S + "row", new XAttribute("r", 1), headers.Select((v, i) => CellNode(((char)('A' + i)) + "1", v)));
        XElement RowNode(int row, string[] values) => new(S + "row", new XAttribute("r", row), values.Select((v, i) => CellNode(((char)('A' + i)) + row.ToString(), v)));
        var sheets = new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", D), new XElement(S + "sheets",
            new[] { "User Texts", "System Texts" }.Select((n, i) => new XElement(S + "sheet", new XAttribute("name", n), new XAttribute("sheetId", i + 1), new XAttribute(D + "id", "rId" + (i + 1))))));
        var p = new Dictionary<string, byte[]>();
        void Part(string name, XElement xml) => p.Add(name, Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
        Part("xl/workbook.xml", sheets);
        Part("xl/worksheets/sheet1.xml", new(S + "worksheet", new XElement(S + "sheetData", Header(),
            RowNode(2, ["<BlockCommentCategoryData>", "Fixture\\PLC\\Programmbausteine\\Main\\Bausteinkommentar", "K-1", "", "Freigabe", "Ready"]),
            RowNode(3, ["<BlockCommentCategoryData>", "Fixture\\PLC\\Main\\Two", "K-2", "", "Peerquelle", "Peer text"]))));
        Part("xl/worksheets/sheet2.xml", new(S + "worksheet", new XElement(S + "sheetData", Header())));
        Part("xl/_rels/workbook.xml.rels", new(R + "Relationships", Enumerable.Range(1, 2).Select(i => new XElement(R + "Relationship", new XAttribute("Id", "rId" + i), new XAttribute("Type", D.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/sheet" + i + ".xml")))));
        Part("_rels/.rels", new(R + "Relationships", new XElement(R + "Relationship", new XAttribute("Id", "root"), new XAttribute("Type", D.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))));
        XNamespace content = "http://schemas.openxmlformats.org/package/2006/content-types";
        Part("[Content_Types].xml", new(content + "Types", new XElement(content + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")), new XElement(content + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml"))));
        return Pack(p);
    }
    internal static Dictionary<string, byte[]> Parts(byte[] bytes) { using var zip = new ZipArchive(new MemoryStream(bytes)); return zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }); }
    internal static byte[] Pack(Dictionary<string, byte[]> parts) { using var m = new MemoryStream(); using (var z = new ZipArchive(m, ZipArchiveMode.Create, true)) foreach (var p in parts) { using var s = z.CreateEntry(p.Key).Open(); s.Write(p.Value); } return m.ToArray(); }
    internal static byte[] Modify(byte[] bytes, string part, Action<XElement> change) { var parts = Parts(bytes); var xml = XElement.Parse(Encoding.UTF8.GetString(parts[part])); change(xml); parts[part] = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)); return Pack(parts); }
    internal static byte[] Change(byte[] bytes, string cell, string value) => Modify(bytes, "xl/worksheets/sheet1.xml", doc => doc.Descendants(S + "c").Single(c => (string?)c.Attribute("r") == cell).Descendants(S + "t").Single().Value = value);
    internal static string Cell(byte[] bytes, string cell) => XElement.Parse(Encoding.UTF8.GetString(Parts(bytes)["xl/worksheets/sheet1.xml"])).Descendants(S + "c").Single(c => (string?)c.Attribute("r") == cell).Descendants(S + "t").Single().Value;
}

internal sealed class FakeAdapter(Fixture fixture) : ITranslationImportAdapter, ITranslationImportSession
{
    internal byte[] Current = fixture.Original;
    internal int Opens, Imports, Checkpoints, Saves, Restores, Closes, Disposals, Observations;
    internal bool CancellableMutation, Restored;
    internal string? Fault;
    internal Func<int, TranslationImportObservation, TranslationImportObservation>? Alter;
    private TranslationImportContext context = null!;
    public void RequireQualification(TranslationImportContext c, string action) { }
    public Task<ITranslationImportSession> OpenExclusive(TranslationImportContext c, CancellationToken token) { Opens++; context = c; return Task.FromResult<ITranslationImportSession>(this); }
    public Task<TranslationImportObservation> Observe(CancellationToken token)
    { token.ThrowIfCancellationRequested(); var value = fixture.Observation(Current); Observations++; return Task.FromResult(Alter?.Invoke(Observations, value) ?? value); }
    public Task<TranslationImportCheckpoint> Checkpoint(CancellationToken token)
    {
        Checkpoints++; CancellableMutation |= token.CanBeCanceled;
        var bytes = Encoding.UTF8.GetBytes("SYNTHETIC CHECKPOINT, NEVER A TIA ARCHIVE");
        var wb = TranslationWorkbook.Open(fixture.Original, fixture.Profile, fixture.Target.Reference, Sha256(fixture.Original));
        return Task.FromResult(new TranslationImportCheckpoint(bytes, Fixture.Json(new TranslationCheckpointReceipt(context.Operation.Id,
            context.Operation.PlanSha256, context.Operation.Plan.Resources.Single().ResourceKey, fixture.Target.SessionEpoch, Sha256(bytes), TranslationCategoryScope.NativeContentSha256(wb)))));
    }
    public Task<TranslationImportReturn> Import(byte[] exactCandidate, CancellationToken token)
    {
        Imports++; CancellableMutation |= token.CanBeCanceled; Current = exactCandidate.ToArray();
        if (Fault == "import-lost") throw new IOException("native return lost");
        byte[] log = Encoding.UTF8.GetBytes("Complete synthetic log\n");
        byte[] raw = Fixture.Json(new TranslationImportResultReceipt(context.Operation.Id, context.Operation.PlanSha256,
            context.Operation.Plan.Resources.Single().ResourceKey, Sha256(exactCandidate), Fault != "result", true, 0, Fault == "log" ? new('0', 64) : Sha256(log)));
        return Task.FromResult(new TranslationImportReturn(Fault == "malformed-result" ? Encoding.UTF8.GetBytes("{") : raw, log));
    }
    private byte[] Step(string step)
    {
        if (Fault == step + "-lost") throw new IOException(step + " response lost");
        return Fixture.Json(new TranslationImportStepReceipt(context.Operation.Id, context.Operation.PlanSha256,
            context.Operation.Plan.Resources.Single().ResourceKey, step, Fault == step ? "failed" : "passed", "Synthetic exact " + step + " evidence; no native qualification"));
    }
    public Task<byte[]> Compile(CancellationToken token) => Task.FromResult(Step("compile"));
    public Task<byte[]> Save(CancellationToken token) { Saves++; return Task.FromResult(Step("save")); }
    public Task<TranslationImportObservation> Reopen(CancellationToken token)
    {
        if (Fault == "reopen-lost") throw new IOException("reopen response lost");
        return Task.FromResult(fixture.Observation(Current, fixture.Target with {
        SessionEpoch = Fault == "reopen" ? fixture.Target.SessionEpoch : "reopened-" + Opens,
        Reference = fixture.Target.Reference with { Snapshot = fixture.Target.Reference.Snapshot with { ObservationId = "fresh-" + Opens } } }));
    }
    public Task<byte[]> Restore(byte[] archive, EngineeringRecoveryDestination destination, CancellationToken token)
    { Restores++; CancellableMutation |= token.CanBeCanceled; Restored = true; if (Fault == "restore-lost") throw new IOException("restore returned but response lost"); return Task.FromResult(Encoding.UTF8.GetBytes("synthetic restore return")); }
    public Task<TranslationRecoveryReadback> ObserveRecovered(EngineeringRecoveryDestination destination, bool freshReopen, CancellationToken token)
    {
        if (!Restored || Fault == "recovery-absence") return Task.FromResult(new TranslationRecoveryReadback(null,
            Fixture.Json(new TranslationRecoveryAbsenceReceipt(context.Operation.Id, context.Operation.PlanSha256, destination.ResourceKey,
                Fault == "recovery-absence" ? "foreign" : destination.RecoveryLocation, fixture.Target.AdapterSha256, "absent", "independent synthetic filesystem absence"))));
        string path = Fault == "recovery-path" ? Path.Combine(fixture.Root, "wrong.ap21") : destination.RecoveryLocation;
        var target = fixture.Target with { ProjectFilePath = path, SessionEpoch = freshReopen && Fault != "recovery-epoch" ? "recovered-reopen" : "recovered",
            Reference = fixture.Target.Reference with { Object = fixture.Target.Reference.Object with { ProjectPath = path },
                Snapshot = fixture.Target.Reference.Snapshot with { ObservationId = freshReopen ? "restored-fresh" : "restored" } } };
        var value = fixture.Observation(Fault == "recovery-content" ? Current : fixture.Original, target);
        if (Fault == "recovery-peer") value = fixture.Drift(value, "peers");
        return Task.FromResult(new TranslationRecoveryReadback(value, null));
    }
    public Task<byte[]> Close(CancellationToken token) { Closes++; return Task.FromResult(Step("cleanup")); }
    public ValueTask DisposeAsync() { Disposals++; if (Fault == "dispose") throw new IOException("lease cleanup failed"); return ValueTask.CompletedTask; }
}
