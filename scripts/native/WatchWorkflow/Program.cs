using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.WatchTables;
using TiaMcp.Core.EngineeringJobs;

if (args.Length != 4) throw new ArgumentException("worker.exe artifact-root genuine-watch.xml normal|interrupt");
string root = Path.GetFullPath(args[1]);
if (Directory.Exists(root) && args[3] is not ("recover" or "reconcile")) throw new InvalidOperationException("Fresh run directory required.");
Directory.CreateDirectory(root);
void Save(string name, object value) { File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(value, BridgeProtocol.Json)); }
await using var bridge = new BridgeClient(new BridgeOptions(Path.GetFullPath(args[0]), TimeoutSeconds: 300, WorkerPrefixArguments: ["--component-qualification"]));
var raw = new FaultBridge(bridge) { Interrupt = args[3] == "interrupt", Point = args[3], Marker = Path.Combine(root, "interruption-marker.json") };
var dispatcher = new WatchTableDispatcher(raw, root, allowUnqualifiedDocumentEdits: true);
async Task<JsonNode> Call(string operation, object? value = null)
{
    var result = await dispatcher.CallAsync(operation, value); var node = JsonNode.Parse(result.GetRawText())!;
    Save("step-" + operation + "-" + Guid.NewGuid().ToString("N")[..5], node); return node;
}
async Task Close()
{
    var state = await Call("session_status"); int? pid = state["processId"]?.GetValue<int>();
    await Call("detach");
    if (pid is not null) { try { using var process = Process.GetProcessById(pid.Value); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); } catch (ArgumentException) { } }
}
async Task Reopen(string path)
{
    await Close(); await Call("start"); await Call("project_open", new { path });
}
try
{
    if (args[3] == "reconcile")
    {
        var operation = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "operation.json")))!;
        string recoveryId = operation["id"]!.GetValue<string>(); string original = operation["projectPath"]!.GetValue<string>();
        var store = new EngineeringOperationStore(root); var record = store.Read(recoveryId);
        try { await Call("watch_apply", new { operationId = recoveryId, reviewedPlanSha256 = operation["digest"]!.GetValue<string>() }); throw new Exception("Unexpected replay"); }
        catch (BridgeException e) when (e.Code == "RecoveryRequired") { Save("replay-refusal", new { code = e.Code, operationId = recoveryId, nativeDispatchInvoked = false }); }
        var competitor = store.Prepare(record.Plan, "matrix-competitor");
        try { store.Acquire(competitor.Id, competitor.Revision); throw new Exception("Competing owner acquired interrupted project"); }
        catch (BridgeException e) when (e.Code == "ResourceBusy") { Save("competing-owner-refusal", new { code = e.Code, operationId = recoveryId, competitorId = competitor.Id, hostProcessId = Environment.ProcessId }); }
        await Call("start");
        if (record.RecoveryDestinations.Count != 0)
        {
            try { await Call("watch_recover", new { operationId = recoveryId }); throw new Exception("Unexpected retrieve replay"); }
            catch (BridgeException e) when (e.Code == "RecoveryRequired") { Save("retrieve-replay-refusal", new { code = e.Code, nativeRetrieveInvoked = false }); }
            string destination = record.RecoveryDestinations.Single().RecoveryLocation;
            var projects = Directory.Exists(destination) ? Directory.GetFiles(destination, "*.ap21", SearchOption.AllDirectories) : [];
            if (projects.Length == 1)
            {
                await Call("project_open", new { path = projects[0] });
                Save("located", await Call("watch_reconcile_recovery", new { operationId = recoveryId, role = "restored" }));
                await Reopen(projects[0]); Save("restored-verified", await Call("watch_reconcile_recovery", new { operationId = recoveryId, role = "restored" }));
                await Reopen(original); Save("reconciled", await Call("watch_reconcile_recovery", new { operationId = recoveryId, role = "original" }));
            }
            else
            {
                await Call("project_open", new { path = original });
                Save("reconciled", await Call("watch_reconcile_interrupted", new { operationId = recoveryId,
                    destinationDisposition = Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() ? "retainedPartial" : "verifiedAbsent" }));
            }
        }
        else if (record.Checkpoints.Count == 0)
        {
            await Call("project_open", new { path = original });
            Save("reconciled", await Call("watch_reconcile_interrupted", new { operationId = recoveryId }));
        }
        else
        {
            var recoveredMatrix = await Call("watch_recover", new { operationId = recoveryId }); Save("recovered", recoveredMatrix);
            string restoredPath = recoveredMatrix["recovered"]!["project"]!["path"]!.GetValue<string>();
            await Reopen(restoredPath); Save("restored-verified", await Call("watch_reconcile_recovery", new { operationId = recoveryId, role = "restored" }));
            await Reopen(original); Save("reconciled", await Call("watch_reconcile_recovery", new { operationId = recoveryId, role = "original" }));
        }
        await Close(); Save("matrix-reconciliation-result", new { completed = true, operationId = recoveryId, hostProcessId = Environment.ProcessId, automaticReplay = false }); return 0;
    }
    if (args[3] == "recover")
    {
        var operation = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "operation.json")))!;
        string recoveryId = operation["id"]!.GetValue<string>(); string original = operation["projectPath"]!.GetValue<string>();
        try
        {
            await Call("watch_apply", new { operationId = recoveryId, reviewedPlanSha256 = operation["digest"]!.GetValue<string>() });
            throw new InvalidOperationException("An uncertain native mutation was replayed.");
        }
        catch (BridgeException refused) when (refused.Code == "RecoveryRequired")
        { Save("replay-refusal", new { operationId = recoveryId, code = refused.Code, message = refused.Message, nativeDispatchInvoked = false }); }
        await Call("start"); var recoveryResult = await Call("watch_recover", new { operationId = recoveryId }); Save("recovered", recoveryResult);
        string restoredPath = recoveryResult["recovered"]!["project"]!["path"]!.GetValue<string>();
        await Reopen(restoredPath); Save("restored-verified", await Call("watch_reconcile_recovery", new { operationId = recoveryId, role = "restored" }));
        await Reopen(original); Save("reconciled", await Call("watch_reconcile_recovery", new { operationId = recoveryId, role = "original" }));
        await Close(); Save("interruption-recovery-result", new { completed = true, id = recoveryId, original, recoveryPath = restoredPath, automaticReplay = false }); return 0;
    }
    Save("admission", new { worker = args[0], workerSha256 = WatchTableDocument.Hash(File.ReadAllBytes(args[0])), mode = args[3], root, nativeSequential = true, hostProcessId = Environment.ProcessId,
        effects = "New owned scratch; derived native watch seed; one reviewed FALSE to TRUE ModifyValue edit; save/reopen; restore archive to new directory; no online action" });
    await Call("start"); var scratch = await Call("scratch_create", new { artifactDirectory = Path.Combine(root, "s") });
    string projectPath = scratch["project"]!["path"]!.GetValue<string>();
    if (args[3] == "evidence")
    {
        Save("ready-for-probes", new { projectPath, processId = scratch["processId"]!.GetValue<int>() });
        Console.WriteLine("Owned evidence scratch ready: " + root);
        using (var bound = new CancellationTokenSource(TimeSpan.FromMinutes(40)))
            while (!File.Exists(Path.Combine(root, "continue-probes"))) await Task.Delay(500, bound.Token);
        await Close(); Save("result", new { completed = true, projectPath, evidenceOnly = true, nativeOnline = false }); return 0;
    }
    // Derive only observed grammar from the genuine capture. Replace all customer selectors and discard unused rows.
    var seed = new WatchTableDocument(File.ReadAllBytes(args[2]));
    while (seed.Count > 1) seed.Remove(seed.Count - 1);
    seed.Rename("MilestoneWatch"); seed.SetField(0, "Name", "\"TiaMcp_EnableGate_DB\".Active"); seed.SetField(0, "ModifyValue", "FALSE");
    seed.SetComment(0, "de-DE", "Freigabestatus {0}"); seed.SetComment(0, "en-GB", "Enable status {0}"); seed.SetComment(0, "nl-NL", "Schakelstatus {0}");
    bool expanded = args[3].StartsWith("comment-", StringComparison.Ordinal) || args[3] is "clone" or "reorder" or "delete";
    if (expanded)
    {
        seed.Duplicate(0, 1); seed.Duplicate(0, 2); seed.SetField(1, "ModifyValue", "TRUE");
        foreach (int row in new[] { 1, 2 })
            foreach (string culture in new[] { "de-DE", "en-GB", "nl-NL" }) seed.SetComment(row, culture, "Owned row " + row + " / " + culture + " {0}");
    }
    var seedPath = Path.Combine(root, "seed.xml"); File.WriteAllBytes(seedPath, seed.ToBytes());
    var seeded = await dispatcher.CallAsync("qualification_watch_fixture_seed", new { inputPath = seedPath, inputSha256 = seed.Sha256, tableName = "MilestoneWatch", artifactDirectory = Path.Combine(root, "seed") });
    Save("seeded", seeded);
    await Reopen(projectPath);
    var durableBaseline = await Call("watch_read", new { tableName = "MilestoneWatch" }); Save("durable-baseline", durableBaseline);
    Save("ready-for-probes", new { projectPath, processId = durableBaseline["processId"]!.GetValue<int>(), plcPath = durableBaseline["observation"]!["reference"]!["object"]!["plcPath"]!.GetValue<string>() });
    Console.WriteLine("Seeded owned scratch; awaiting continue-probes file: " + root);
    using (var bound = new CancellationTokenSource(TimeSpan.FromMinutes(40)))
        while (!File.Exists(Path.Combine(root, "continue-probes"))) await Task.Delay(500, bound.Token);
    var edit = new JsonObject { ["tableName"] = "MilestoneWatch", ["ordinal"] = 0, ["modifyValue"] = "TRUE", ["idempotencyKey"] = "first-watch-" + args[3] };
    if (args[3].StartsWith("comment-", StringComparison.Ordinal))
    {
        edit["editKind"] = "comment"; edit["culture"] = args[3] switch { "comment-de" => "de-DE", "comment-en" => "en-GB", "comment-nl" => "nl-NL", _ => throw new Exception("Unknown comment mode") };
        edit["text"] = args[3] switch { "comment-de" => "Geprüfte Freigabe {0}\nÄnderung – Ω", "comment-en" => "Reviewed enable {0}\nChange – Ω", _ => "Gecontroleerde vrijgave {0}\nWijziging – Ω" };
    }
    if (args[3] == "clone") { edit["editKind"] = "clone"; edit["insertAt"] = 3; }
    if (args[3] == "delete") { edit["editKind"] = "delete"; edit["ordinal"] = 1; }
    if (args[3] == "reorder") { edit["editKind"] = "reorder"; edit["order"] = new JsonArray(2, 0, 1); }
    var prepared = await Call("watch_prepare", edit);
    Save("review", prepared); Console.WriteLine("Prepared; awaiting approval digest file: " + root);
    string digest = prepared["operation"]!["planSha256"]!.GetValue<string>();
    using (var bound = new CancellationTokenSource(TimeSpan.FromMinutes(40)))
        while (!File.Exists(Path.Combine(root, "approved-digest"))) await Task.Delay(500, bound.Token);
    if (File.ReadAllText(Path.Combine(root, "approved-digest")).Trim() != digest) throw new InvalidOperationException("Review digest differs.");
    string id = prepared["operation"]!["id"]!.GetValue<string>();
    Save("operation", new { id, projectPath, digest });
    var applied = await Call("watch_apply", new { operationId = id, reviewedPlanSha256 = digest }); Save("applied", applied);
    await Reopen(projectPath);
    Save("verified", await Call("watch_verify_reopen", new { operationId = id }));
    await Close(); await Call("start");
    var recovered = await Call("watch_recover", new { operationId = id }); Save("recovered", recovered);
    string recoveryPath = recovered["recovered"]!["project"]!["path"]!.GetValue<string>();
    await Reopen(recoveryPath);
    Save("restored-verified", await Call("watch_reconcile_recovery", new { operationId = id, role = "restored" }));
    await Reopen(projectPath);
    Save("reconciled", await Call("watch_reconcile_recovery", new { operationId = id, role = "original" }));
    await Close(); Save("result", new { completed = true, projectPath, recoveryPath, id, nativeOnline = false });
    Console.WriteLine("Completed verified recovery."); return 0;
}
catch (Exception error)
{
    Save("failure", new { error = error.ToString(), automaticReplay = false }); Console.Error.WriteLine(error);
    try { await Close(); } catch { }
    return 1;
}

sealed class FaultBridge(IBridgeClient inner) : IBridgeClient
{
    public bool Interrupt { get; init; }
    public string? Point { get; init; }
    public string? Marker { get; init; }
    public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
    {
        if (operation is "watch_checkpoint" or "watch_apply_native" or "watch_restore_native")
        {
            var input = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject();
            input["qualificationInterruptAt"] = Point; input["qualificationMarkerPath"] = Marker; arguments = input;
        }
        if (Interrupt && operation == "watch_apply_native")
        {
            var input = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject(); input["qualificationInterruptAfterImport"] = true; arguments = input;
        }
        var result = await inner.CallAsync(operation, arguments, cancellationToken);
        if (Point == "host." + operation)
        {
            File.WriteAllText(Marker!, JsonSerializer.Serialize(new { point = Point, hostProcessId = Environment.ProcessId,
                response = result, automaticReplay = false }, BridgeProtocol.Json));
            Environment.Exit(74);
        }
        return result;
    }
}
