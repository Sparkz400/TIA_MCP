using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class MutationAdmissionChecks
{
    internal static void Probe(string[] args)
    {
        try
        {
            if (args[1] == "operation")
            {
                var store = new EngineeringOperationStore(args[2]); var record = store.Read(args[3]);
                store.Acquire(record.Id, record.Revision); Console.WriteLine("acquired");
            }
            else
            {
                var scope = JsonSerializer.Deserialize<EngineeringMutationScope>(File.ReadAllText(args[3]), BridgeProtocol.Json)!;
                var record = new EngineeringMutationAdmissionStore(args[2]).Acquire(scope, new('a', 64));
                Console.WriteLine("admitted:" + record.Id);
            }
        }
        catch (BridgeException error) { Console.WriteLine(error.Code); }
    }

    internal static async Task Run()
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; }
        void Reject(Action action, string code)
        {
            try { action(); } catch (BridgeException error) when (error.Code == code) { passed++; return; }
            throw new Exception("Expected " + code);
        }
        string root = Path.Combine(Path.GetTempPath(), "mutation-admission-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        EngineeringArtifactReference Evidence(string name, string content)
        {
            string path = Path.Combine(root, name); File.WriteAllText(path, content);
            return new(name, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        var input = Evidence("input.json", "synthetic review input");
        var response = Evidence("returned.json", "synthetic legacy response; no native qualification");
        var proof = Evidence("independent.json", "synthetic trusted-adapter independent observations");
        string project = Path.Combine(root, "target", "project.ap21"), unrelated = Path.Combine(root, "other", "project.ap21");
        EngineeringMutationScope Scope(string path) => new("change_apply", new('b', 64), new('c', 64), "worker-a", new('d', 64), 123, new('f', 64),
            [new("project", path, "checkpointMutationCompileSave")]);
        var expected = Scope(project); var admissionStore = new EngineeringMutationAdmissionStore(root);
        var record = admissionStore.Acquire(expected, new('e', 64));
        Check(record.State == "dispatchIntent" && record.ResourceKeys.Single() == EngineeringResourceKey.ForPath("project", project), "Exact native resource committed before dispatch");
        Reject(() => new EngineeringMutationAdmissionStore(root).Acquire(expected, new('f', 64)), "ResourceBusy");
        var distinct = admissionStore.Acquire(Scope(unrelated), new('f', 64));
        Check(distinct.State == "dispatchIntent", "Disjoint project is admitted concurrently");
        admissionStore.Returned(distinct.Id, distinct.Revision, response);
        record = admissionStore.Unknown(record.Id, record.Revision);
        Check(new EngineeringMutationAdmissionStore(root).Read(record.Id).State == "outcomeUnknown", "Unknown ownership survives store reconstruction");
        Reject(() => admissionStore.Returned(record.Id, record.Revision, response), "RecoveryRequired");
        Reject(() => admissionStore.Reconcile(record.Id, record.Revision, []), "ReconciliationIncomplete");
        Reject(() => admissionStore.Reconcile(record.Id, record.Revision, [new(record.ResourceKeys.Single(), "retainedPartial", null, proof)]), "ReconciliationIncomplete");
        Reject(() => admissionStore.Reconcile(record.Id, record.Revision - 1, [new(record.ResourceKeys.Single(), "unchanged", null, proof)]), "RevisionConflict");
        var reference = new EngineeringObjectReference(new(project, "/plc/one", "/root", "/watch/table", "watchTable"), new("capture", new('a', 64)), new('b', 64));
        var native = new NativeTargetObservation(reference, project, "epoch", new('c', 64), new('d', 64), "CPU/FW", null, true, []);
        var plan = new EngineeringOperationPlan("watch.test", native, [new(EngineeringResourceKey.ForPath("project", project), "project", "write", "projectArchiveToNewLocation")], [input], [], ["reopen"]);
        var operations = new EngineeringOperationStore(root); var operation = operations.Prepare(plan, "watch-conflict");
        Reject(() => operations.Acquire(operation.Id, operation.Revision), "ResourceBusy");
        string scopeFile = Path.Combine(root, "scope.json"); File.WriteAllText(scopeFile, JsonSerializer.Serialize(expected, BridgeProtocol.Json));
        string Child(params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--admission-probe");
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; string stdout = process.StandardOutput.ReadToEnd(), stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Admission child timed out."); }
            if (process.ExitCode != 0) throw new Exception("Admission child failed: " + stderr);
            return stdout.Trim();
        }
        Check(Child("operation", root, operation.Id) == "ResourceBusy", "Separate process cannot acquire operation over unknown admission");
        Check(Child("admission", root, scopeFile) == "ResourceBusy", "Separate process cannot acquire competing admission after Host restart");
        record = admissionStore.Reconcile(record.Id, record.Revision, [new(record.ResourceKeys.Single(), "unchanged", null, proof)]);
        Check(record.State == "reconciled", "Independent complete evidence releases unknown dispatch");
        operation = operations.Acquire(operation.Id, operation.Revision);
        Check(Child("admission", root, scopeFile) == "ResourceBusy", "Separate process admission cannot cross acquired shared operation");
        operations.Stop(operation.Id, operation.Revision);
        record = admissionStore.Acquire(expected, new('9', 64));
        record = admissionStore.Returned(record.Id, record.Revision, response);
        Check(record.State == "returned", "Retained successful return ends dispatch reservation without qualification");
        var next = admissionStore.Acquire(expected, new('8', 64)); admissionStore.Returned(next.Id, next.Revision, response);
        Reject(() => admissionStore.Acquire(expected with { Resources = [new("project", "relative.ap21", "write")] }, new('7', 64)), "InvalidResourceKey");
        Reject(() => admissionStore.Acquire(expected with { Resources = [expected.Resources[0], expected.Resources[0]] }, new('7', 64)), "InvalidMutationScope");
        Reject(() => admissionStore.Acquire(expected with { Operation = "read" }, new('7', 64)), "InvalidMutationScope");
        var destination = Path.Combine(root, "recovery");
        var recovery = admissionStore.Acquire(expected with { Resources = [new("project", destination, "retrieve")] }, new('7', 64));
        Reject(() => admissionStore.Acquire(Scope(Path.Combine(destination, "nested", "retrieved.ap21")), new('6', 64)), "ResourceBusy");
        admissionStore.Returned(recovery.Id, recovery.Revision, response);
        // Both APIs must use this exact OS lock rather than separate, race-prone admission locks.
        using (var held = new FileStream(Path.Combine(root, "engineering-operations", "store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Check(Child("admission", root, scopeFile) == "IntakeBusy", "Admission contends on exact operation store OS lock across processes");
            Check(Child("operation", root, operation.Id) == "IntakeBusy", "Operation contends on same OS lock across processes");
        }
        // A holder that lets go within the bound (a status poll, another Host) is waited out, so a returned dispatch is still recorded.
        var waited = admissionStore.Acquire(Scope(Path.Combine(root, "waited.ap21")), new('4', 64));
        var lockWait = PackageStorage.LockWait; PackageStorage.LockWait = TimeSpan.FromSeconds(5);
        var briefHolder = new FileStream(Path.Combine(root, "engineering-operations", "store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var release = Task.Delay(200).ContinueWith(_ => briefHolder.Dispose());
            Check(admissionStore.Returned(waited.Id, waited.Revision, response).State == "returned", "A store lock released within the bound is waited out instead of losing the outcome");
            await release;
        }
        finally { briefHolder.Dispose(); PackageStorage.LockWait = lockWait; }
        string corrupt = Path.Combine(root, "engineering-admissions", record.Id, "00000001.json");
        var tampered = JsonNode.Parse(File.ReadAllText(corrupt))!.AsObject(); tampered["requestSha256"] = new string('0', 64); File.WriteAllText(corrupt, tampered.ToJsonString());
        Reject(() => admissionStore.Read(record.Id), "JournalInvalid");
        Reject(() => admissionStore.Acquire(Scope(Path.Combine(root, "third.ap21")), new('1', 64)), "JournalInvalid");

        // A scan reads a finished admission for its chain only: a resource that can no longer prove its identity (here a new
        // hard link; likewise a removed drive) and a deleted response no longer block every later mutation.
        string scanRoot = Path.Combine(root, "finished"); Directory.CreateDirectory(scanRoot);
        var scans = new EngineeringMutationAdmissionStore(scanRoot);
        void KeepResponse() => File.Copy(Path.Combine(root, response.RelativePath), Path.Combine(scanRoot, response.RelativePath), true);
        KeepResponse();
        string linked = Path.Combine(scanRoot, "linked.ap21"); File.WriteAllText(linked, "finished synthetic project");
        var finished = scans.Acquire(Scope(linked), new('5', 64)); finished = scans.Returned(finished.Id, finished.Revision, response);
        File.Delete(Path.Combine(scanRoot, response.RelativePath));
        if (OperatingSystem.IsWindows() && !EngineeringOperationChecks.CreateHardLink(Path.Combine(scanRoot, "alias.ap21"), linked, IntPtr.Zero))
            throw new Exception("Could not create the isolated hard-link fixture.");
        Check(scans.Acquire(Scope(Path.Combine(scanRoot, "later.ap21")), new('6', 64)).State == "dispatchIntent", "A finished admission whose resource and response are gone does not block unrelated mutations");
        Reject(() => scans.Read(finished.Id), OperatingSystem.IsWindows() ? "InvalidResourceKey" : "ArtifactChanged");
        string removed = Path.Combine(scanRoot, "removed", "project.ap21"); Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
        scans.Acquire(Scope(removed), new('7', 64)); Directory.Delete(Path.GetDirectoryName(removed)!);
        Reject(() => scans.Acquire(Scope(removed), new('8', 64)), "ResourceBusy");
        KeepResponse();
        int bound = EngineeringOperationStore.ArchiveAbove; EngineeringOperationStore.ArchiveAbove = 0;
        try
        {
            var archived = scans.Acquire(Scope(Path.Combine(scanRoot, "archived.ap21")), new('9', 64));
            archived = scans.Returned(archived.Id, archived.Revision, response);
            Check(!Directory.Exists(Path.Combine(scanRoot, "engineering-admissions", archived.Id)) && Directory.Exists(Path.Combine(scanRoot, "engineering-admissions", ".archive", archived.Id)) &&
                scans.Read(archived.Id).State == "returned" && scans.AdmittedAt(archived.Id) <= archived.RecordedAt, "A finished admission moves into .archive, out of the growth bound, and still reads by id");
            Check(scans.Acquire(Scope(Path.Combine(scanRoot, "after-archive.ap21")), new('b', 64)).State == "dispatchIntent", "Scans skip the .archive folder");
        }
        finally { EngineeringOperationStore.ArchiveAbove = bound; }
        // A clock stepped back after the intent was stamped keeps the returned outcome valid.
        var stamped = scans.Acquire(Scope(Path.Combine(scanRoot, "clock.ap21")), new('a', 64));
        File.WriteAllText(Path.Combine(scanRoot, "engineering-admissions", stamped.Id, "00000000.json"),
            JsonSerializer.Serialize(stamped with { RecordedAt = DateTimeOffset.UtcNow.AddDays(1) }, BridgeProtocol.Json));
        Check(scans.Returned(stamped.Id, stamped.Revision, response).State == "returned", "A backward clock step does not turn a returned dispatch into JournalInvalid");

        string dispatcherRoot = Path.Combine(root, "dispatcher"); Directory.CreateDirectory(dispatcherRoot);
        var successfulNative = new AdmissionNative(Scope(Path.Combine(dispatcherRoot, "fixture.ap21")));
        var dispatcher = new WatchTableDispatcher(successfulNative, dispatcherRoot);
        var result = await dispatcher.CallAsync("change_apply", new { changeId = "synthetic", previewHash = new string('1', 64) });
        Check(result.GetProperty("legacyShape").GetString() == "preserved" && successfulNative.Calls.SequenceEqual(["mutation_begin", "mutation_dispatch", "mutation_end"]), "Legacy dispatch uses native scope lease and preserves response shape");
        var failedNative = new AdmissionNative(Scope(Path.Combine(dispatcherRoot, "failed.ap21"))) { FailDispatch = true };
        try { await new WatchTableDispatcher(failedNative, dispatcherRoot).CallAsync("change_apply", new { changeId = "synthetic-failure" }); throw new Exception("Expected transport failure"); }
        catch (IOException) { passed++; }
        string[] unresolved = Directory.GetDirectories(Path.Combine(dispatcherRoot, "engineering-admissions"));
        var pending = unresolved.Select(p => new EngineeringMutationAdmissionStore(dispatcherRoot).Read(Path.GetFileName(p))).Single(r => r.State == "outcomeUnknown");
        Check(pending.ResourceKeys.Single() == EngineeringResourceKey.ForPath("project", Path.Combine(dispatcherRoot, "failed.ap21")), "Lost response retains exact failed resource ownership");
        var competitor = new AdmissionNative(failedNative.Scope);
        try { await new WatchTableDispatcher(competitor, dispatcherRoot).CallAsync("change_apply", new { changeId = "competing" }); throw new Exception("Expected conflict"); }
        catch (BridgeException error) when (error.Code == "ResourceBusy") { passed++; }
        Check(!competitor.Calls.Contains("mutation_dispatch") && competitor.Calls.Last() == "mutation_end", "Competing Host never dispatches and releases only its native lease");
        var nestedNative = new AdmissionNative(Scope(Path.Combine(dispatcherRoot, "nested.ap21")));
        string nestedArtifacts = Path.Combine(dispatcherRoot, "nested-artifacts");
        await new WatchTableDispatcher(nestedNative, dispatcherRoot).CallAsync("change_apply", new { artifactDirectory = nestedArtifacts });
        Check(nestedNative.Arguments!.Value.GetProperty("arguments").GetProperty("artifactDirectory").GetString() == nestedArtifacts, "Existing artifact subdirectory contract is preserved");
        var outsideNative = new AdmissionNative(Scope(Path.Combine(dispatcherRoot, "outside.ap21")));
        try { await new WatchTableDispatcher(outsideNative, dispatcherRoot).CallAsync("change_apply", new { artifactDirectory = Path.Combine(root, "outside") }); throw new Exception("Expected outside-root refusal"); }
        catch (BridgeException error) when (error.Code == "ArtifactRootMismatch") { passed++; }
        Check(outsideNative.Calls.Count == 0, "Outside artifact root is refused before any native scope call");
        foreach (JsonElement? nativeDetails in new JsonElement?[] {
            JsonSerializer.SerializeToElement(new { stage = "discoverExactRuntimeAddress", originalProgramCardRestored = true, downloadTicket = "private-ticket-sentinel" }),
            JsonSerializer.SerializeToElement("retained native detail"), JsonSerializer.SerializeToElement<object?>(null), null })
        {
            var provisionNative = new AdmissionNative(Scope(Path.Combine(dispatcherRoot, Guid.NewGuid() + ".ap21")) with {
                Operation = "simulation_provision_scratch_credential" }) {
                DispatchError = new BridgeOperationException("SimulationProvisionTargetAmbiguous", "Original provisioning failure: private-message-sentinel", nativeDetails) };
            var provisionDispatcher = new WatchTableDispatcher(provisionNative, dispatcherRoot);
            try { await provisionDispatcher.CallAsync("simulation_provision_scratch_credential", new { }); throw new Exception("Expected provisioning failure"); }
            catch (BridgeOperationException error)
            {
                Check(error.Code == "SimulationProvisionTargetAmbiguous" && error.Message == "Original provisioning failure: private-message-sentinel", "Admission diagnostics preserve native error identity");
                var details = error.Details!.Value;
                var admission = details.GetProperty("mutationAdmission");
                string id = admission.GetProperty("admissionId").GetString()!;
                Check(admission.GetProperty("statusTool").GetString() == "engineering_operation_status" &&
                    !admission.GetProperty("automaticReplay").GetBoolean(), "Failure identifies the existing read-only status tool and no-replay boundary");
                var retained = await provisionDispatcher.CallAsync("engineering_operation_status", new { operationId = id });
                Check(retained.GetProperty("state").GetString() == "outcomeUnknown" &&
                    retained.GetProperty("scope").GetProperty("operation").GetString() == "simulation_provision_scratch_credential", "Reported admission identifies the actual durable failed provisioning");
                var reopened = new EngineeringMutationAdmissionStore(dispatcherRoot).Read(id);
                string failureText = File.ReadAllText(Path.Combine(dispatcherRoot, reopened.Response!.RelativePath));
                var failure = JsonNode.Parse(failureText)!.AsObject();
                Check(failure["nativeErrorCode"]?.GetValue<string>() == "SimulationProvisionTargetAmbiguous", "Retained failure preserves the native error code across Host restart");
                string? expectedDetailsHash = nativeDetails?.ValueKind switch {
                    JsonValueKind.Object => "3952f1313583c3ebda7b7a9787fd5a57d85eeb6625a98fc501991585548ff9e6",
                    JsonValueKind.String => "127ce1cc043b48dadbf2e51c6129d4e2f9fd9791354307d4c974a8ff5115d511",
                    JsonValueKind.Null => "6b4e98e14b161756a369417681401d993ac9fdd04374ad2da30575a933a218bd",
                    _ => null };
                Check(failure["nativeDetailsSha256"]?.GetValue<string>() == expectedDetailsHash, "Retained fingerprint binds exact native details and distinguishes absent from JSON null");
                Check(!failureText.Contains("private-ticket-sentinel") && !failureText.Contains("private-message-sentinel") &&
                    !failureText.Contains("retained native detail"), "Durable failure excludes raw native diagnostic text and ticket material");
                Check(provisionNative.Calls.SequenceEqual(["mutation_begin", "mutation_dispatch", "mutation_end"]), "Status inspection performs no native replay");
                if (nativeDetails?.ValueKind == JsonValueKind.Object)
                    Check(details.GetProperty("stage").GetString() == "discoverExactRuntimeAddress" && details.GetProperty("originalProgramCardRestored").GetBoolean(), "Native object details retain their existing shape");
                else if (nativeDetails?.ValueKind == JsonValueKind.String)
                    Check(details.GetProperty("nativeDetails").GetString() == "retained native detail", "Non-object native details are retained");
                Reject(() => new EngineeringMutationAdmissionStore(dispatcherRoot).Acquire(provisionNative.Scope, new('a', 64)), "ResourceBusy");
            }
        }
        // A live worker's structured change failure is returned: the journal's recovery state drives change_restore.
        string failedProject = Path.Combine(dispatcherRoot, "verification-failed.ap21");
        var verificationFailed = new AdmissionNative(Scope(failedProject)) {
            DispatchError = new BridgeOperationException("MutationVerificationFailed", "Applied change failed native readback", JsonSerializer.SerializeToElement(new { state = "recovery_required" })) };
        try { await new WatchTableDispatcher(verificationFailed, dispatcherRoot).CallAsync("change_apply", new { changeId = "failed-apply" }); throw new Exception("Expected verification failure"); }
        catch (BridgeOperationException error) when (error.Code == "MutationVerificationFailed")
        {
            string id = error.Details!.Value.GetProperty("mutationAdmission").GetProperty("admissionId").GetString()!;
            var returned = new EngineeringMutationAdmissionStore(dispatcherRoot).Read(id);
            Check(returned.State == "returned" && JsonNode.Parse(File.ReadAllText(Path.Combine(dispatcherRoot, returned.Response!.RelativePath)))!["nativeOutcome"]!.GetValue<string>() == "returnedFailure",
                "Structured change_apply failure from a live worker is a returned outcome with its failure proof");
            Check(error.Details!.Value.GetProperty("state").GetString() == "recovery_required", "Native failure details keep the journal state for the caller");
        }
        var restore = new AdmissionNative(Scope(failedProject) with { Operation = "change_restore", ScopeSha256 = new('5', 64) });
        var restored = await new WatchTableDispatcher(restore, dispatcherRoot).CallAsync("change_restore", new { changeId = "failed-apply" });
        Check(restored.GetProperty("legacyShape").GetString() == "preserved" && restore.Calls.Contains("mutation_dispatch"), "Restore of a cleanly failed apply is admitted and dispatched");
        var failedRestore = new AdmissionNative(Scope(Path.Combine(dispatcherRoot, "restore-failed.ap21")) with { Operation = "change_restore" }) {
            DispatchError = new BridgeOperationException("RecoveryRequired", "Archive recovery did not complete") };
        try { await new WatchTableDispatcher(failedRestore, dispatcherRoot).CallAsync("change_restore", new { changeId = "failed-restore" }); throw new Exception("Expected restore failure"); }
        catch (BridgeOperationException error) when (error.Code == "RecoveryRequired")
        { Check(new EngineeringMutationAdmissionStore(dispatcherRoot).Read(error.Details!.Value.GetProperty("mutationAdmission").GetProperty("admissionId").GetString()!).State == "returned", "Structured change_restore failure is returned too"); }
        // Transport loss and deliberate simulator retention stay unknown and keep their reservation.
        foreach (var (route, failure) in new (string, Exception)[] {
            ("change_apply", new BridgeException("WorkerDisconnected", "The TIA worker connection failed")),
            ("change_apply", new BridgeException("TimeoutOutcomeUnknown", "The bridge call was interrupted")),
            ("change_restore", new BridgeException("CancelledOutcomeUnknown", "The bridge call was interrupted")),
            ("simulation_download", new BridgeOperationException("SimulationDownloadFailed", "Card generation failed")) })
        {
            string held = Path.Combine(dispatcherRoot, Guid.NewGuid().ToString("N") + ".ap21");
            var heldNative = new AdmissionNative(Scope(held) with { Operation = route }) { DispatchError = failure };
            try { await new WatchTableDispatcher(heldNative, dispatcherRoot).CallAsync(route, new { }); throw new Exception("Expected failure"); }
            catch (BridgeOperationException error)
            {
                string id = error.Details!.Value.GetProperty("mutationAdmission").GetProperty("admissionId").GetString()!;
                Check(new EngineeringMutationAdmissionStore(dispatcherRoot).Read(id).State == "outcomeUnknown", $"{route} {failure.GetType().Name} {((BridgeException)failure).Code} stays outcomeUnknown");
            }
            var follower = new AdmissionNative(Scope(held) with { Operation = "change_restore", ScopeSha256 = new('4', 64) });
            try { await new WatchTableDispatcher(follower, dispatcherRoot).CallAsync("change_restore", new { }); throw new Exception("Expected ResourceBusy"); }
            catch (BridgeException error) when (error.Code == "ResourceBusy") { passed++; }
            Check(!follower.Calls.Contains("mutation_dispatch"), "A held reservation blocks restore before dispatch");
        }
        Console.WriteLine($"PASS Shared legacy mutation admission: {passed} checks, including cross-process operation/admission conflicts; no native execution.");
        await RecoveryBoundaryAuditChecks.Run();
    }

    private sealed class AdmissionNative(EngineeringMutationScope scope) : IBridgeClient
    {
        internal EngineeringMutationScope Scope => scope;
        internal bool FailDispatch;
        internal Exception? DispatchError;
        internal JsonElement? Arguments;
        internal List<string> Calls { get; } = [];
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(operation);
            if (operation == "mutation_begin") Arguments = JsonSerializer.SerializeToElement(arguments, BridgeProtocol.Json);
            if (operation == "mutation_dispatch" && FailDispatch) throw new IOException("Synthetic lost native response");
            if (operation == "mutation_dispatch" && DispatchError is not null) throw DispatchError;
            object value = operation switch {
                "mutation_begin" => new { leaseToken = "synthetic-lease", scope },
                "mutation_dispatch" => new { legacyShape = "preserved", nativeQualified = false },
                "mutation_end" => new { released = true },
                _ => throw new Exception("Unadmitted native operation: " + operation)
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(value, BridgeProtocol.Json));
        }
    }
}
