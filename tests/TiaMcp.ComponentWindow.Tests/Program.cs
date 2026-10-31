using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

// All native identities/artifacts below are explicitly synthetic. No Siemens assembly, worker, or Portal is loaded.
PackageStorage.LockWait = TimeSpan.FromMilliseconds(250); // The contention child holds the store lock past this short bound.
if (args.Length > 0)
{
    try
    {
        string root = args[1]; var childStore = new ComponentQualificationWindowStore(root);
        if (args[0] is "acquire" or "acquire-interrupted")
        {
            var manifest = JsonSerializer.Deserialize<ComponentWindowManifest>(File.ReadAllText(args[2]), BridgeProtocol.Json)!;
            var pair = childStore.Acquire(manifest);
            if (args[0] == "acquire-interrupted")
            {
                var first = childStore.BeginChild(pair.Reservation.Id, 0, pair.Capability, "start", new('a', 64), manifest.Resources);
                first = childStore.Returned(first.Id, first.Revision, pair.Capability, StartResponse((root, manifest), first));
                first = childStore.BeginChild(first.Id, first.Revision, pair.Capability, "applyLibrary", new('b', 64), manifest.Resources);
                childStore.Unknown(first.Id, first.Revision, pair.Capability);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { pair.Reservation.Id, pair.Capability }, BridgeProtocol.Json));
        }
        else if (args[0] == "resume") { childStore.Resume(args[2], int.Parse(args[3]), args[4]); Console.WriteLine("resumed"); }
        else if (args[0] == "compete") { new EngineeringMutationAdmissionStore(root).Acquire(Scope(args[2]), new('c', 64)); Console.WriteLine("unexpectedly-admitted"); }
    }
    catch (BridgeException error) { Console.WriteLine(error.Code); }
    return;
}

int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; }
void Reject(Action action, string code)
{ try { action(); } catch (BridgeException error) when (error.Code == code) { passed++; return; } throw new Exception("Expected " + code); }
string basis = Path.Combine(Path.GetTempPath(), "component-window-offline-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(basis);
string Child(params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); foreach (string arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(30000)) { process.Kill(); throw new Exception("File-only test child timeout"); }
    if (process.ExitCode != 0) throw new Exception(error.Result); return output.Result.Trim();
}
var f = Fixture(Path.Combine(basis, "live")); var store = new ComponentQualificationWindowStore(f.Root);
var acquired = store.Acquire(f.Manifest); var r = acquired.Reservation; string token = acquired.Capability;
Check(!Directory.Exists(f.Manifest.Workspace), "Reservation is durable before any future native workspace is created");
Check(!File.ReadAllText(Path.Combine(f.Root, "engineering-component-windows", r.Id, "00000000.json")).Contains(token), "Only capability SHA-256 is retained");
Reject(() => store.Acquire(f.Manifest with { Id = new('f', 32) }), "ComponentResourceEscape");
Reject(() => store.Resume(r.Id, r.Revision, new('0', 64)), "ComponentOwnerMismatch");
Reject(() => store.Resume(r.Id, r.Revision, token), "ComponentFreshHostRequired");
Check(ComponentQualificationWindowStore.NativeHash(new JsonObject { ["b"] = @"D:\路径", ["a"] = "Ω+<>&'\"" }) == Digest("{\"a\":\"Ω+<>&'\\\"\",\"b\":\"D:\\\\路径\"}"), "Native manifest/request hashing retains Unicode with Newtonsoft-compatible escaping");
Check(Child("resume", f.Root, r.Id, "0", token) == "ResourceBusy", "Exact live Host owner cannot be stolen even with retained capability");
Check(Child("compete", f.Root, f.Manifest.ProjectPath) == "ResourceBusy", "Independent ordinary admission is blocked by parent");
Check(Child("compete", f.Root, Path.Combine(f.Manifest.Workspace, "changes", "future", "journal.json")) == "ResourceBusy", "Uncreated future descendant remains reserved");
Check(Child("compete", f.Root, f.Manifest.SourceSnapshotPath) == "ResourceBusy", "Original immutable source archive is reserved");
Reject(() => store.BeginChild(r.Id, r.Revision, token, "start", new('a', 64), [new("workspace", f.Root, "ancestor")]), "ComponentResourceEscape");
Reject(() => store.BeginChild(r.Id, r.Revision, token, "start", new('a', 64), [new("workspace", Path.Combine(f.Root, "outside"), "escape")]), "ComponentResourceEscape");
r = store.BeginChild(r.Id, r.Revision, token, "start", new('a', 64), f.Manifest.Resources);
Reject(() => store.BeginChild(r.Id, r.Revision, token, "start", new('a', 64), f.Manifest.Resources), "ComponentOutcomeUnknown");
var response = StartResponse(f, r);
r = store.Returned(r.Id, r.Revision, token, response);
Check(r.State == "active" && r.Children[0].State == "returned", "Successful first preparation does not release parent ownership");
Check(Child("compete", f.Root, f.Manifest.ProjectPath) == "ResourceBusy", "Competitor stays blocked between reviewed steps");
Reject(() => store.BeginChild(r.Id, r.Revision, token, "start", new('a', 64), f.Manifest.Resources), "ComponentReplayRefused");
r = store.BeginChild(r.Id, r.Revision, token, "applyLibrary", new('b', 64), f.Manifest.Resources);
Check(r.Children.Last().IdempotencyKeyHash == Digest(f.Manifest.ComponentId + ":library"), "Reservation binds actual inner key, not external review key");
var failure = Evidence(f.Root, "failure.json", new JsonObject { ["lostResponse"] = true });
r = store.Unknown(r.Id, r.Revision, token, failure);
Check(new ComponentQualificationWindowStore(f.Root).Read(r.Id).State == "outcomeUnknown", "Unknown child and parent survive store reconstruction");
Check(Child("compete", f.Root, f.Manifest.ProjectPath) == "ResourceBusy", "Unknown result retains competing-owner exclusion");
Reject(() => store.Returned(r.Id, r.Revision, token, response), "ComponentReplayRefused");
Reject(() => store.BeginChild(r.Id, r.Revision, token, "verifyImport", new('b', 64), f.Manifest.Resources), "ComponentOutcomeUnknown");
Reject(() => store.Complete(r.Id, r.Revision, token, Evidence(f.Root, "fake-success.json", new JsonObject { ["success"] = true })), "ComponentOutcomeUnknown");
Reject(() => store.ReconcileChild(r.Id, r.Revision, token, response), "ComponentAcceptanceIncomplete");

var dead = Fixture(Path.Combine(basis, "dead")); string mf = Path.Combine(dead.Root, "manifest.json"); File.WriteAllText(mf, JsonSerializer.Serialize(dead.Manifest, BridgeProtocol.Json));
var owner = JsonNode.Parse(Child("acquire", dead.Root, mf))!.AsObject(); var deadStore = new ComponentQualificationWindowStore(dead.Root);
var resumed = deadStore.Resume(owner["id"]!.GetValue<string>(), 0, owner["capability"]!.GetValue<string>());
Check(!resumed.InstallationAllowed && resumed.OwnerHostProcessId == Environment.ProcessId, "Exact retained owner resumes only after old process exit, with installation disabled");
Reject(() => deadStore.BeginChild(resumed.Id, resumed.Revision, owner["capability"]!.GetValue<string>(), "start", new('a', 64), dead.Manifest.Resources), "ComponentFreshSessionRequired");
Check(Child("compete", dead.Root, dead.Manifest.ProjectPath) == "ResourceBusy", "Fresh Host owner resume does not release durable reservation");
using (var held = new FileStream(Path.Combine(dead.Root, "engineering-operations", "store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check(Child("compete", dead.Root, dead.Manifest.ProjectPath) == "IntakeBusy", "Parent and ordinary admissions use identical OS store lock");
string revision = Path.Combine(dead.Root, "engineering-component-windows", resumed.Id, "00000001.json");
var changed = JsonNode.Parse(File.ReadAllText(revision))!.AsObject(); changed["installationAllowed"] = true; File.WriteAllText(revision, changed.ToJsonString());
Reject(() => deadStore.Read(resumed.Id), "JournalInvalid");
var interrupted = Fixture(Path.Combine(basis, "interrupted")); string im = Path.Combine(interrupted.Root, "manifest.json"); File.WriteAllText(im, JsonSerializer.Serialize(interrupted.Manifest, BridgeProtocol.Json));
var previousOwner = JsonNode.Parse(Child("acquire-interrupted", interrupted.Root, im))!.AsObject(); var interruptedStore = new ComponentQualificationWindowStore(interrupted.Root);
string iid = previousOwner["id"]!.GetValue<string>(), itoken = previousOwner["capability"]!.GetValue<string>();
var ir = interruptedStore.Read(iid); ir = interruptedStore.Resume(iid, ir.Revision, itoken);
Check(ir.State == "outcomeUnknown" && ComponentQualificationWindowStore.NextStep(ir) == "recover", "Fresh owner of interrupted import can recover without resuming/replaying installation");
ir = interruptedStore.BeginChild(iid, ir.Revision, itoken, "recover", new('e', 64), interrupted.Manifest.Resources);
Check(ir.State == "outcomeUnknown" && ir.Children[1].State == "outcomeUnknown", "Recovery intent preserves the earlier unknown inner dispatch");
ir = interruptedStore.Unknown(iid, ir.Revision, itoken);
Reject(() => interruptedStore.BeginChild(iid, ir.Revision, itoken, "recover", new('e', 64), interrupted.Manifest.Resources), "ComponentReplayRefused");
Check(Child("compete", interrupted.Root, interrupted.Manifest.ProjectPath) == "ResourceBusy", "Interrupted recovery never drops the whole-window reservation");
var acceptance = Fixture(Path.Combine(basis, "acceptance")); passed += AcceptanceChecks.Run(acceptance.Root, acceptance.Manifest);
var dispatchFixture = Fixture(Path.Combine(basis, "dispatcher")); ComponentWindowManifest dispatchManifest = dispatchFixture.Manifest; int plans = 0, dispatched = 0, ended = 0;
JsonObject? dispatchedArguments = null;
var fake = new DelegateBridge((operation, request) =>
{
    if (operation == "component_window_plan")
    {
        plans++; string id = request["id"]!.GetValue<string>(), workspace = Path.Combine(dispatchFixture.Root, "q", "c" + id[..12]);
        dispatchManifest = dispatchManifest with { Id = id, ComponentId = "component-" + id, Workspace = workspace,
            Resources = dispatchManifest.Resources.Select(r => r.Effect == "allPreparationJournalsClaimsCheckpointsReadbackAndRecovery" ? r with { Path = workspace } : r).ToArray() };
        return new JsonObject { ["manifest"] = JsonSerializer.SerializeToNode(dispatchManifest, BridgeProtocol.Json), ["manifestSha256"] = ComponentQualificationWindowStore.ManifestHash(dispatchManifest) };
    }
    if (operation == "component_window_bind") return new JsonObject { ["bound"] = true, ["manifestSha256"] = request["manifestSha256"]!.DeepClone(), ["installationAllowed"] = true };
    if (operation == "mutation_begin")
    {
        dispatchedArguments = request["arguments"]!.DeepClone().AsObject();
        var scope = new EngineeringMutationScope("component_window_step", ComponentQualificationWindowStore.NativeHash(dispatchedArguments), new('f', 64), "synthetic", dispatchManifest.AdapterSha256, 999, new('e', 64), dispatchManifest.Resources);
        return new JsonObject { ["leaseToken"] = "synthetic-lease", ["scope"] = JsonSerializer.SerializeToNode(scope, BridgeProtocol.Json) };
    }
    if (operation == "mutation_dispatch")
    {
        dispatched++; var current = new ComponentQualificationWindowStore(dispatchFixture.Root).Read(dispatchManifest.Id);
        Check(current.Children.Last().State == "dispatchIntent" && current.Children.Last().RequestSha256 == ComponentQualificationWindowStore.NativeHash(dispatchedArguments!), "Dispatcher persists exact intent before private native call");
        Check(File.Exists(request["reservationPath"]!.GetValue<string>()), "Worker receives actual retained parent revision");
        var proof = StartResponse((dispatchFixture.Root, dispatchManifest), current); string receipt = Path.Combine(dispatchManifest.Workspace, "responses", "start.json"); Directory.CreateDirectory(Path.GetDirectoryName(receipt)!); File.Copy(Path.Combine(dispatchFixture.Root, proof.RelativePath), receipt);
        throw new BridgeException("SyntheticLostResponse", "File-only exact native receipt retained, transport response lost");
    }
    if (operation == "mutation_end") { ended++; return new JsonObject(); }
    throw new Exception("Unexpected fake bridge call: " + operation);
});
JsonObject Invoke(WatchTableDispatcher dispatcher, string operation, JsonObject request) => JsonNode.Parse(dispatcher.CallAsync(operation, request).GetAwaiter().GetResult().GetRawText())!.AsObject();
var disabled = new WatchTableDispatcher(fake, dispatchFixture.Root);
Reject(() => Invoke(disabled, "qualification_component_window", new JsonObject { ["action"] = "begin" }), "ComponentQualificationDisabled");
Reject(() => Invoke(disabled, "change_apply", new JsonObject { ["changeId"] = "lib-component-other" }), "MutationScopeIncomplete");
Check(plans == 0 && dispatched == 0, "Ordinary combined apply remains disabled before native dispatch");
var dispatcher = new WatchTableDispatcher(fake, dispatchFixture.Root, allowComponentQualification: true);
Reject(() => Invoke(dispatcher, "component_window_plan", new JsonObject()), "InternalOperation");
var begun = Invoke(dispatcher, "qualification_component_window", new JsonObject { ["action"] = "begin", ["planId"] = dispatchManifest.PlanId, ["planPreviewHash"] = dispatchManifest.PlanPreviewHash });
Check(dispatched == 0 && begun["componentOwner"]?["capability"]?.GetValue<string>().Length == 64, "Owner capability returns durably to caller before any native child dispatch");
string dispatchedId = begun["componentOwner"]!["id"]!.GetValue<string>(), dispatchedToken = begun["componentOwner"]!["capability"]!.GetValue<string>();
JsonObject Control(string action) => new() { ["action"] = action, ["id"] = dispatchedId, ["capability"] = dispatchedToken };
Reject(() => Invoke(dispatcher, "qualification_component_window", Control("start")), "SyntheticLostResponse");
Check(new ComponentQualificationWindowStore(dispatchFixture.Root).Read(dispatchedId).State == "outcomeUnknown" && ended == 1, "Lost child response retains unknown ownership and ends native ExclusiveAccess");
var reconciliation = Control("status"); reconciliation["reconcileReceipt"] = true;
var reconciled = Invoke(dispatcher, "qualification_component_window", reconciliation);
Check(reconciled["children"]![0]!["state"]!.GetValue<string>() == "reconciled" && dispatched == 1, "Exact retained native response reconciles without a second native dispatch");
Reject(() => Invoke(dispatcher, "qualification_component_window", Control("start")), "ComponentReplayRefused");
Check(dispatched == 1 && Child("compete", dispatchFixture.Root, dispatchManifest.ProjectPath) == "ResourceBusy", "Reconciled child stays reserved; replay never reaches native mutation");
// A scan reads a finished window for its chain only: its deleted plan directory no longer blocks every later mutation.
var finished = Fixture(Path.Combine(basis, "finished")); var finishedStore = new ComponentQualificationWindowStore(finished.Root);
var window = finishedStore.Acquire(finished.Manifest).Reservation; var receipt = Evidence(finished.Root, "receipt.json", new JsonObject { ["synthetic"] = true });
ComponentWindowReservation Next(ComponentWindowReservation prior, string transition, Func<ComponentWindowReservation, ComponentWindowReservation> change)
{
    var next = change(prior) with { Revision = prior.Revision + 1, PreviousSha256 = CanonicalJson.Sha256(JsonSerializer.SerializeToNode(prior, BridgeProtocol.Json)!), Transition = transition };
    File.WriteAllText(Path.Combine(finished.Root, "engineering-component-windows", next.Id, next.Revision.ToString("D8") + ".json"), JsonSerializer.Serialize(next, BridgeProtocol.Json));
    return next;
}
foreach (string step in new[] { "start", "applyLibrary", "recover", "observeRestored", "observeRecoveryOriginal" })
{
    if (step == "recover") window = Next(window, "ownerResume", p => p with { InstallationAllowed = false });
    window = Next(window, "childIntent", p => p with { Children = p.Children.Append(new ComponentWindowChild(step, new('a', 64), "dispatchIntent", null, step == "applyLibrary" ? Digest(p.Manifest.ComponentId + ":library") : null)).ToArray() });
    window = Next(window, "childReturned", p => p with { Children = p.Children.Take(p.Children.Count - 1).Append(p.Children.Last() with { State = "returned", Response = receipt }).ToArray() });
}
window = Next(window, "recoveryReconciled", p => p with { State = "recoveryReconciled", Completion = receipt });
Directory.Delete(finished.Manifest.PlanDirectory, true);
Check(new EngineeringMutationAdmissionStore(finished.Root).Acquire(Scope(Path.Combine(finished.Root, "unrelated")), new('c', 64)).State == "dispatchIntent", "A finished window whose plan is gone does not block unrelated mutations");
Reject(() => finishedStore.Read(window.Id), "MutationScopeIncomplete");
// A clock stepped back after the reservation was stamped keeps the next child valid.
var clock = Fixture(Path.Combine(basis, "clock")); var clockStore = new ComponentQualificationWindowStore(clock.Root);
var (stamped, clockToken) = clockStore.Acquire(clock.Manifest);
File.WriteAllText(Path.Combine(clock.Root, "engineering-component-windows", stamped.Id, "00000000.json"), JsonSerializer.Serialize(stamped with { RecordedAt = DateTimeOffset.UtcNow.AddDays(1) }, BridgeProtocol.Json));
Check(clockStore.BeginChild(stamped.Id, stamped.Revision, clockToken, "start", new('a', 64), clock.Manifest.Resources).Children.Count == 1, "A backward clock step does not turn a component child into JournalInvalid");
Console.WriteLine($"PASS Component whole-window reservation: {passed} offline checks; no native execution. Evidence retained at {basis}");

static EngineeringMutationScope Scope(string path) => new("change_apply", new('a', 64), new('b', 64), "synthetic", new('c', 64), 123, new('d', 64), [new("workspace", path, "synthetic competing owner")]);
static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
static (string Root, ComponentWindowManifest Manifest) Fixture(string root)
{
    Directory.CreateDirectory(root); string project = Path.Combine(root, "target", "p.ap21"), plan = Path.Combine(root, "changes", "plan"), source = Path.Combine(root, "library");
    Directory.CreateDirectory(Path.GetDirectoryName(project)!); File.WriteAllText(project, "synthetic project"); Directory.CreateDirectory(plan); Directory.CreateDirectory(source);
    string archive = Path.Combine(root, "source.zal21"), original = Path.Combine(root, "original.zal21"); File.WriteAllText(archive, "synthetic archive"); File.WriteAllText(original, "synthetic archive"); string id = Guid.NewGuid().ToString("N"), workspace = Path.Combine(root, "q", "c" + id[..12]);
    var resources = new EngineeringMutationResource[] { new("project", Path.GetDirectoryName(project)!, "targetProjectAndCompanions"), new("workspace", plan, "reviewedPlanAndSourceChecks"),
        new("library", source, "pinnedWorkingSource"), new("library", archive, "immutableSourceSnapshot"), new("library", original, "immutableOriginalSource"), new("workspace", workspace, "allPreparationJournalsClaimsCheckpointsReadbackAndRecovery") };
    return (root, new(1, id, "component-" + id, workspace, project, "/P/PLC", "lib-plan", new('a', 64), plan, source, archive, Digest("synthetic archive"), false, original, false, "source", new('b', 64), 999, "synthetic-native", resources));
}
static EngineeringArtifactReference Evidence(string root, string name, JsonObject value)
{ string path = Path.Combine(root, name); File.WriteAllText(path, value.ToJsonString()); return new(name, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))); }
static EngineeringArtifactReference StartResponse((string Root, ComponentWindowManifest Manifest) f, ComponentWindowReservation r)
{
    string path = Path.Combine(f.Manifest.Workspace, "captures", "before.xml"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "<Document><SW.Blocks.OB /></Document>");
    var observation = new JsonObject { ["projectPath"] = f.Manifest.ProjectPath, ["plcPath"] = f.Manifest.PlcPath, ["processId"] = 999, ["nativeBirthUtc"] = "2026-01-01T00:00:00Z", ["sessionEpoch"] = "synthetic-native",
        ["scopeComplete"] = true, ["isModified"] = false, ["projectFileSha256"] = Digest("synthetic project"), ["componentVersions"] = new JsonArray(new JsonObject { ["typeGuid"] = "synthetic-type", ["versionGuid"] = "synthetic-version", ["exists"] = false }),
        ["peerMetadata"] = new JsonObject { ["hardware"] = new JsonArray(new JsonObject { ["path"] = "device/CPU" }), ["softwareOwners"] = new JsonArray(new JsonObject { ["owner"] = "device/CPU", ["type"] = "PlcSoftware" }),
            ["watchGroups"] = new JsonObject { ["root"] = "Watch and force tables" }, ["watchTableInventory"] = new JsonArray(), ["access"] = new JsonArray(new JsonObject { ["owner"] = "device/CPU", ["available"] = false }),
            ["languages"] = new JsonObject { ["active"] = new JsonArray("de-DE"), ["reference"] = "de-DE", ["editing"] = "de-DE" } }, ["watchForceTables"] = new JsonObject(),
        ["inventory"] = new JsonObject { ["completeness"] = new JsonObject { ["scopeComplete"] = true, ["truncated"] = false }, ["softwareScopes"] = new JsonArray(new JsonObject()), ["technologyObjects"] = new JsonArray(),
            ["blocks"] = new JsonArray(new JsonObject { ["path"] = "/P/PLC/blocks/Main" }), ["types"] = new JsonArray(), ["tagTables"] = new JsonArray() },
        ["objects"] = new JsonObject { ["block:/P/PLC/blocks/Main"] = new JsonObject { ["path"] = "/P/PLC/blocks/Main", ["kind"] = "block", ["artifact"] = new JsonObject { ["path"] = path, ["sha256"] = Digest("<Document><SW.Blocks.OB /></Document>") } } } };
    string journalPath = Path.Combine(f.Manifest.Workspace, "changes", "change-lib-" + new string('c', 32), "journal.json"); Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
    File.WriteAllText(journalPath, new JsonObject { ["changeId"] = "lib-" + new string('c', 32), ["previewHash"] = new string('d', 64) }.ToJsonString());
    return Evidence(f.Root, "start-response.json", new JsonObject { ["changeId"] = "lib-" + f.Manifest.ComponentId, ["state"] = "library_prepared",
        ["record"] = new JsonObject { ["operationId"] = f.Manifest.ComponentId, ["state"] = "library_prepared", ["contract"] = new JsonObject { ["planChangeId"] = f.Manifest.PlanId, ["planPreviewHash"] = f.Manifest.PlanPreviewHash, ["projectPath"] = f.Manifest.ProjectPath },
            ["library"] = new JsonObject { ["prepared"] = new JsonObject { ["changeId"] = "lib-" + new string('c', 32), ["previewHash"] = new string('d', 64), ["journalPath"] = journalPath } } },
        ["componentWindow"] = new JsonObject { ["manifestSha256"] = r.ManifestSha256, ["step"] = "start", ["processId"] = 999, ["sessionEpoch"] = "synthetic-native", ["observation"] = observation } });
}
