using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

/// <summary>Production Host integration. Every native call uses the same serialized bridge; no dispatch resumes automatically.</summary>
public sealed partial class WatchTableDispatcher(IBridgeClient native, string artifactRoot, bool allowUnqualifiedDocumentEdits = false, bool allowComponentQualification = false) : IBridgeClient
{
    private readonly string root = Path.GetFullPath(artifactRoot);
    private readonly EngineeringOperationStore store = new(artifactRoot);
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var args = JsonSerializer.SerializeToNode(arguments ?? new { }, BridgeProtocol.Json)!.AsObject();
            return operation switch
            {
                "watch_read" => Element(await Snapshot(args, cancellationToken)),
                "watch_generate" => Element(await Generate(args, cancellationToken)),
                "watch_prepare_authoring" => Element(await PrepareAuthoring(args, cancellationToken)),
                "watch_prepare" => Element(await Prepare(args, cancellationToken)),
                "watch_prepare_lifecycle" => Element(await PrepareLifecycle(args, cancellationToken)),
                "watch_apply" => Element(await Apply(args, cancellationToken)),
                "watch_verify_reopen" => Element(await Verify(args, cancellationToken)),
                "watch_recover" => Element(await Recover(args, cancellationToken)),
                "watch_reconcile_recovery" => Element(await ReconcileRecovery(args, cancellationToken)),
                "watch_reconcile_interrupted" => Element(await ReconcileInterrupted(args, cancellationToken)),
                "engineering_operation_status" => ReadEngineeringStatus(Text(args, "operationId")),
                "simulation_provision_reconcile" => Element(await ReconcileProvision(args, cancellationToken)),
                "engineering_admission_reconcile" => Element(await ReconcileAdmission(args, cancellationToken)),
                "qualification_watch_fixture_seed" when allowUnqualifiedDocumentEdits => await ForwardAdmitted("watch_fixture_seed", arguments, cancellationToken),
                "qualification_watch_authoring_source_negative" when allowUnqualifiedDocumentEdits && allowComponentQualification => await ForwardAdmitted("watch_authoring_source_negative", arguments, cancellationToken),
                "qualification_watch_authoring_source_reconcile" when allowUnqualifiedDocumentEdits && allowComponentQualification => Element(await ReconcileSourceNegative(args, cancellationToken)),
                "qualification_component_window" => await ComponentWindowControl(args, cancellationToken),
                _ => await Forward(operation, arguments, cancellationToken)
            };
        }
        finally { gate.Release(); }
    }
    private async Task<JsonElement> Forward(string operation, object? arguments, CancellationToken token)
    {
        if (operation.StartsWith("watch_", StringComparison.Ordinal)) throw new BridgeException("InternalOperation", "Native watch protocol is private to the dispatcher.");
        if (operation.StartsWith("component_window_", StringComparison.Ordinal)) throw new BridgeException("InternalOperation", "Native component lifetime protocol is private to the dispatcher.");
        var component = await ForwardComponentQualification(operation, JsonSerializer.SerializeToNode(arguments ?? new { }, BridgeProtocol.Json)!.AsObject(), token);
        if (component is not null) return component.Value;
        return await ForwardAdmitted(operation, arguments, token);
    }
    private JsonElement ReadEngineeringStatus(string id)
    {
        try { return Element(store.Read(id)); }
        catch (BridgeException e) when (e.Code == "OperationNotFound") { return Element(new EngineeringMutationAdmissionStore(root).Read(id)); }
    }
    private async Task<JsonObject> Snapshot(JsonObject args, CancellationToken token)
    {
        var selector = (JsonObject)args.DeepClone(); selector["artifactDirectory"] = Path.Combine(root, "watch-observations");
        return Node(await native.CallAsync("watch_read_native", selector, token));
    }
    private async Task<object> Prepare(JsonObject args, CancellationToken token)
    {
        var before = await Snapshot(args, token);
        var observation = Observation(before);
        int ordinal = args["ordinal"]?.GetValue<int>() ?? 0;
        var path = before["target"]!["artifact"]!["path"]!.GetValue<string>();
        var captured = Artifact(before["target"]!["artifact"]!.AsObject());
        var bytes = File.ReadAllBytes(Resolve(captured));
        if (WatchTableDocument.Hash(bytes) != captured.Sha256 || WatchTableDocument.SemanticHash(bytes) != observation.Reference.ContentSha256)
            throw new BridgeException("ArtifactChanged", "Native capture no longer matches its byte and content identities.");
        var document = new WatchTableDocument(bytes);
        string kind = args["editKind"]?.GetValue<string>() ?? "modifyValue";
        bool reindexDocumentIds = args["reindexDocumentIds"]?.GetValue<bool>() ?? false;
        if (kind != "modifyValue" && !allowUnqualifiedDocumentEdits)
            throw new BridgeException("UnsupportedProfile", "Expanded watch edits require the isolated qualification harness until native lifecycle evidence is accepted.");
        if (reindexDocumentIds && (!allowUnqualifiedDocumentEdits || kind is not ("reorder" or "delete")))
            throw new BridgeException("UnsupportedProfile", "Explicit document ID reindex is isolated to the reviewed reorder/delete qualification profile.");
        JsonObject edit;
        switch (kind)
        {
            case "modifyValue":
                string value = Text(args, "modifyValue");
                if (document.Field(ordinal, "DisplayFormat") != "Bool" || value is not ("TRUE" or "FALSE"))
                    throw new BridgeException("UnsupportedProfile", "Literal edits require an existing Bool row and TRUE/FALSE literal.");
                edit = new() { ["ordinal"] = ordinal, ["field"] = "ModifyValue", ["before"] = document.Field(ordinal, "ModifyValue"), ["after"] = value };
                document.SetField(ordinal, "ModifyValue", value); break;
            case "comment":
                string culture = Text(args, "culture"); string comment = args["text"]?.GetValue<string>() ?? throw new BridgeException("InvalidRequest", "Comment text is required.");
                edit = new() { ["kind"] = kind, ["ordinal"] = ordinal, ["culture"] = culture, ["text"] = comment };
                document.SetComment(ordinal, culture, comment); break;
            case "clone":
                int at = args["insertAt"]?.GetValue<int>() ?? throw new BridgeException("InvalidRequest", "Exact insertion position is required.");
                edit = new() { ["kind"] = kind, ["ordinal"] = ordinal, ["insertAt"] = at }; document.Duplicate(ordinal, at); break;
            case "delete":
                edit = new() { ["kind"] = kind, ["ordinal"] = ordinal }; document.Remove(ordinal); break;
            case "reorder":
                int[] order = args["order"]?.AsArray().Select(x => x!.GetValue<int>()).ToArray() ?? throw new BridgeException("InvalidRequest", "An exact row permutation is required.");
                edit = new() { ["kind"] = kind, ["order"] = JsonSerializer.SerializeToNode(order) }; document.Reorder(order); break;
            default: throw new BridgeException("UnsupportedProfile", "Unsupported reviewed watch edit kind.");
        }
        if (reindexDocumentIds)
            edit["documentIdReindex"] = new JsonObject { ["profile"] = "v21-watch-reference-free-preorder-1",
                ["mapping"] = JsonSerializer.SerializeToNode(document.ReindexReferenceFreeDocumentIds()) };
        if (WatchTableDocument.SemanticHash(bytes) == WatchTableDocument.SemanticHash(document.ToBytes())) throw new BridgeException("NoChange", "The proposed document is unchanged.");
        string directory = Path.Combine(root, "watch", Guid.NewGuid().ToString("N")[..12]); Directory.CreateDirectory(directory);
        var input = SaveBytes(directory, "reviewed.xml", document.ToBytes());
        var proposal = new { selector = new { projectName = args["projectName"]?.GetValue<string>(), plcPath = observation.Reference.Object.PlcPath,
                group = args["group"]?.GetValue<string>() ?? "root", tableName = document.Name }, before,
            edit, expectedSemanticSha256 = WatchTableDocument.SemanticHash(document.ToBytes()), input };
        var review = Save(directory, "review.json", proposal);
        var plan = new EngineeringOperationPlan(kind == "modifyValue" ? "watchLiteralEdit" : "watchDocumentEdit", observation,
            [new(EngineeringResourceKey.ForPath("project", observation.ProjectFilePath), "project", "oneReviewedWatchEdit:" + kind, "projectArchiveToNewLocation")],
            [review, input], ["offline", "watchImport"], ["readback", "preservation", "saved", "freshReopen"]);
        var record = store.Prepare(plan, Text(args, "idempotencyKey"));
        return new { operation = record, review = proposal, reviewArtifact = review, applyRequires = "Exact operationId and reviewed planSha256" };
    }
    private async Task<object> Apply(JsonObject args, CancellationToken token)
    {
        var record = store.Read(Text(args, "operationId"));
        if (!IsWatchPlan(record) || record.PlanSha256 != Text(args, "reviewedPlanSha256")) throw new BridgeException("ReviewMismatch", "Exact reviewed watch plan digest required.");
        if (record.Plan.Operation == "watchDocumentEdit" && !allowUnqualifiedDocumentEdits)
            throw new BridgeException("UnsupportedProfile", "This document-edit plan has no accepted native qualification for production apply.");
        if (record.State != "prepared") throw new BridgeException("RecoveryRequired", "A prior attempt cannot be dispatched again; inspect and reconcile it.");
        var review = LoadReview(record); string directory = DirectoryOf(record);
        if (record.Plan.Operation is "watchAuthoring" or "watchLifecycle") WatchMutationProfiles.RequireVerified(review["edit"]!["requiredProfiles"]!.AsArray(), allowUnqualifiedDocumentEdits);
        record = store.Acquire(record.Id, record.Revision);
        string? lease = null; bool stopAfterRelease = false; Exception? primaryError = null;
        try
        {
            var selector = review["selector"]!.DeepClone().AsObject(); selector["artifactDirectory"] = Path.Combine(root, "watch-observations");
            var begin = Node(await native.CallAsync("watch_begin", selector, token)); lease = Text(begin, "leaseToken");
            record = store.BeginCheckpoint(record.Id, record.Revision, () => Observation(begin));
            var checkpoint = Node(await native.CallAsync("watch_checkpoint", new { leaseToken = lease, artifactDirectory = directory }, token));
            var archive = Artifact(checkpoint["checkpoint"]!.AsObject());
            record = store.Checkpoint(record.Id, record.Revision, [archive]);
            record = store.Dispatch(record.Id, record.Revision, () => Observation(checkpoint["snapshot"]!.AsObject()));
            var input = record.Plan.Inputs[1];
            object request = record.Plan.Operation == "watchLifecycle" ? new { leaseToken = lease, lifecycle = review["edit"] }
                : new { leaseToken = lease, inputPath = Resolve(input), inputSha256 = input.Sha256 };
            var after = Node(await native.CallAsync("watch_apply_native", request, token));
            var evidence = Save(directory, "applied.json", after);
            record = store.RecordDispatchEvidence(record.Id, record.Revision, new("dispatch.readback", evidence));
            if (record.Plan.Operation is "watchAuthoring" or "watchLifecycle")
            {
                ValidateSnapshotIdentity(record, after, record.Plan.Target.ProjectFilePath);
                if (after["saved"]?.GetValue<bool>() != true) throw new BridgeException("VerificationFailed", "Saved authoring readback is required.");
            }
            ValidateResult(review, after);
            // Ownership remains held until a separate fresh-instance read proves persistence.
            return new { operation = record, applied = after, evidence, next = "Detach, start a fresh owned instance, open this exact project, then watch_verify_reopen. Never replay apply." };
        }
        catch (Exception error)
        {
            primaryError = error;
            record = store.Read(record.Id);
            var failure = Save(directory, "failure-" + record.Revision + ".json", new { error = error.Message, type = error.GetType().FullName,
                code = (error as BridgeException)?.Code, details = (error as BridgeOperationException)?.Details, automaticReplay = false });
            if (record.State == "dispatchIntent" && error is BridgeOperationException) store.RecordOutcome(record.Id, record.Revision, false, [new("failure", failure)]);
            else if (record.State is "dispatchIntent" or "checkpointIntent" || record.State == "acquired" && lease is null && error is not BridgeOperationException)
                store.MarkUnknown(record.Id, record.Revision, failure);
            else if (record.State == "acquired") stopAfterRelease = true;
            throw;
        }
        finally
        {
            bool released = lease is null;
            if (lease is not null)
            {
                try { await native.CallAsync("watch_end", new { leaseToken = lease }, CancellationToken.None); released = true; }
                catch (Exception error)
                {
                    // Even a stale-target refusal cannot release ownership until its native lease is accounted for.
                    try
                    {
                        var current = store.Read(record.Id);
                        if (current.State is "acquired" or "ready" or "checkpointIntent" or "dispatchIntent")
                        {
                            var failure = Save(directory, "lease-release-failure-" + Guid.NewGuid().ToString("N") + ".json",
                                new { error = error.Message, type = error.GetType().FullName, automaticReplay = false });
                            store.MarkUnknown(current.Id, current.Revision, failure);
                        }
                    }
                    catch { /* A journal/IO failure still leaves the preceding durable reservation held. */ }
                    if (primaryError is null) throw;
                }
            }
            if (stopAfterRelease && released) store.Stop(record.Id, record.Revision);
        }
    }
    private async Task<object> Verify(JsonObject args, CancellationToken token)
    {
        var record = store.Read(Text(args, "operationId"));
        if (record.State != "dispatchIntent") throw new BridgeException("RecoveryRequired", "Fresh verification requires the original dispatched operation.");
        var review = LoadReview(record);
        JsonObject reopened;
        try { reopened = await Snapshot(review["selector"]!.AsObject(), token); }
        catch (Exception error) when (error is not BridgeOperationException)
        {
            var failure = Save(DirectoryOf(record), "reopen-interrupted-" + Guid.NewGuid().ToString("N") + ".json",
                new { error = error.Message, type = error.GetType().FullName, automaticReplay = false });
            store.MarkUnknown(record.Id, record.Revision, failure); throw;
        }
        var actual = Observation(reopened);
        ValidateSnapshotIdentity(record, reopened, record.Plan.Target.ProjectFilePath);
        if (actual.SessionEpoch == record.Plan.Target.SessionEpoch || reopened["processId"]!.ToJsonString() == review["before"]!["processId"]!.ToJsonString() ||
            actual.AdapterSha256 != record.Plan.Target.AdapterSha256 || actual.ProfileSha256 != record.Plan.Target.ProfileSha256 || actual.HardwareIdentity != record.Plan.Target.HardwareIdentity ||
            reopened["projectModified"]!.GetValue<bool>()) throw new BridgeException("FreshInstanceRequired", "A new native instance with the saved exact target/binary/profile is required.");
        try { ValidateResult(review, reopened); }
        catch (BridgeException error)
        {
            var failure = Save(DirectoryOf(record), "reopen-failure-" + Guid.NewGuid().ToString("N") + ".json", new { error = error.Message, reopened, automaticReplay = false });
            store.RecordOutcome(record.Id, record.Revision, false, [new("failure", failure)]); throw;
        }
        var evidence = Save(DirectoryOf(record), "fresh-reopen-" + Guid.NewGuid().ToString("N") + ".json", reopened);
        record = store.RecordOutcome(record.Id, record.Revision, true, record.Plan.RequiredGates.Select(g => new EngineeringGateEvidence(g, evidence)).ToArray());
        return new { operation = record, reopened };
    }
    private async Task<object> Recover(JsonObject args, CancellationToken token)
    {
        var record = store.Read(Text(args, "operationId"));
        if (!IsWatchPlan(record)) throw new BridgeException("InvalidOperation", "Watch recovery requires a watch plan.");
        if (record.State is "dispatchIntent" or "checkpointIntent") record = store.MarkUnknown(record.Id, record.Revision);
        string directory = Path.Combine(root, "r_" + Guid.NewGuid().ToString("N")[..10]);
        record = store.ReserveRecovery(record.Id, record.Revision, [new(record.Plan.Resources[0].ResourceKey, directory)]);
        try
        {
            var archive = record.Checkpoints.Single();
            var recovered = Node(await native.CallAsync("watch_restore_native", new { archivePath = Resolve(archive), expectedSha256 = archive.Sha256, destination = directory }, token));
            var review = LoadReview(record); var selector = RecoverySelector(review, recovered["project"]!["path"]!.GetValue<string>(), recovered["project"]!["name"]!.GetValue<string>());
            var restored = await Snapshot(selector, token);
            string restoredPath = recovered["project"]?["path"]?.GetValue<string>() ?? throw new BridgeException("RecoveryVerificationFailed", "Retrieved project path is missing.");
            string destinationKey = EngineeringResourceKey.ForPath("project", directory);
            if (!EngineeringResourceKey.ForPath("project", restoredPath).StartsWith(destinationKey + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new BridgeException("RecoveryVerificationFailed", "Retrieved project is outside its admitted destination.");
            ValidateSnapshotIdentity(record, restored, restoredPath);
            var restoredObservation = Observation(restored);
            if (recovered["processId"]?.GetValue<int>() != restored["processId"]!.GetValue<int>() ||
                recovered["sessionEpoch"]?.GetValue<string>() != restoredObservation.SessionEpoch ||
                restored["processId"]!.ToJsonString() == review["before"]!["processId"]!.ToJsonString() ||
                restoredObservation.SessionEpoch == record.Plan.Target.SessionEpoch)
                throw new BridgeException("RecoveryVerificationFailed", "Retrieval receipt must identify the observed fresh native instance and session.");
            ValidateCapturedContent(restored, record.Plan.Target.Reference.ContentSha256!);
            if (Text(restored, "peerSha256") != review["before"]!["peerSha256"]!.GetValue<string>() || restored["target"]!["semanticSha256"]!.GetValue<string>() != record.Plan.Target.Reference.ContentSha256)
                throw new BridgeException("RecoveryVerificationFailed", "Restored native full manifest differs from checkpoint baseline.");
            var proof = Save(DirectoryOf(record), "recovered.json", new { recovered, restored, checkpoint = archive });
            record = store.RecordRecoveryEvidence(record.Id, record.Revision, new("recovery.retrieve", proof));
            // Native restore opens the archive in a fresh owned instance; re-observe original separately before releasing its reservation.
            return new { operation = record, recovered, restored, evidence = proof, next = "Reconcile original and freshly reopened recovery through watch_reconcile_recovery." };
        }
        catch (Exception error)
        {
            var current = store.Read(record.Id);
            if (current.State == "recoveryIntent")
            {
                var failure = Save(DirectoryOf(current), "retrieval-failure-" + current.Revision + ".json",
                    new { error = error.Message, type = error.GetType().FullName, automaticReplay = false });
                store.MarkUnknown(record.Id, current.Revision, failure);
            }
            throw;
        }
    }
    private void ValidateResult(JsonObject review, JsonObject after)
    {
        if (Text(after, "peerSha256") != review["before"]!["peerSha256"]!.GetValue<string>() ||
            !Applied(review, after) || !after["target"]!["isConsistent"]!.GetValue<bool>())
            throw new BridgeException("VerificationFailed", "Native readback, consistency or peer manifest differs from the reviewed change.");
        if (after["target"]?["exists"]?.GetValue<bool>() == false) throw new BridgeException("VerificationFailed", "Reviewed native table is absent.");
        ValidateCapturedContent(after, after["target"]!["semanticSha256"]!.GetValue<string>());
    }
    // Reviewed outcome: the exact XML for document edits, the exact group subtree for lifecycle edits.
    private bool Applied(JsonObject review, JsonObject snapshot)
    {
        var target = snapshot["target"]!.AsObject();
        if (review["mode"]?.GetValue<string>() != "lifecycle") return Text(target, "semanticSha256") == Text(review, "expectedSemanticSha256");
        return target["kind"]?.GetValue<string>() == "watchGroup" && WatchGroupManifest.Matches(
            review["edit"]!["expectedGroup"]!.AsArray().Select(l => l!.GetValue<string>()).ToArray(), ReadRecoverySnapshot(Resolve(Artifact(target["artifact"]!.AsObject()))));
    }
    private static bool IsWatchPlan(EngineeringOperationRecord record) => record.Plan.Operation is "watchLiteralEdit" or "watchDocumentEdit" or "watchAuthoring" or "watchLifecycle";
    private static void ValidateSnapshotIdentity(EngineeringOperationRecord record, JsonObject snapshot, string expectedProjectPath)
    {
        var actual = Observation(snapshot); NativeTargetGuard.Validate(actual);
        if (snapshot["processId"]?.GetValue<int>() is not > 0 ||
            EngineeringResourceKey.ForPath("project", actual.ProjectFilePath) != EngineeringResourceKey.ForPath("project", expectedProjectPath) ||
            EngineeringResourceKey.ForPath("project", actual.Reference.Object.ProjectPath) != EngineeringResourceKey.ForPath("project", actual.ProjectFilePath) ||
            actual.AdapterSha256 != record.Plan.Target.AdapterSha256 || actual.ProfileSha256 != record.Plan.Target.ProfileSha256 ||
            actual.HardwareIdentity != record.Plan.Target.HardwareIdentity || actual.NativeIdentity != record.Plan.Target.NativeIdentity ||
            actual.Reference.Snapshot.TiaVersion != record.Plan.Target.Reference.Snapshot.TiaVersion || !actual.Offline ||
            record.Plan.RequiredProbes.Any(name => !actual.Probes.Any(p => p.Name == name && p.State == "observed")) ||
            snapshot["projectModified"]?.GetValue<bool>() != false || snapshot["target"]?["isConsistent"]?.GetValue<bool>() != true ||
            actual.Reference.ContentSha256 != snapshot["target"]?["semanticSha256"]?.GetValue<string>())
            throw new BridgeException("IdentityMismatch", "Exact saved offline project, native identity, profile, probes and consistent content observation are required.");
        // Only the physical project path may move to an explicitly reserved recovery destination.
        var expected = record.Plan.Target.Reference with { Object = record.Plan.Target.Reference.Object with { ProjectPath = actual.Reference.Object.ProjectPath } };
        EngineeringIdentityGuard.EnsureSameObject(expected, actual.Reference);
    }
    private static void ValidateTypedRows(byte[] bytes, JsonObject snapshot)
    {
        var document = new WatchTableDocument(bytes); var rows = snapshot["target"]!["rows"]!.AsArray();
        if (rows.Count != document.Count) throw new BridgeException("VerificationFailed", "Typed native row count differs from complete XML readback.");
        for (int i = 0; i < document.Count; i++)
        {
            if (rows[i]?["kind"]?.GetValue<string>() != document.RowKind(i)) throw new BridgeException("VerificationFailed", "Native row kind/order differs.");
            if (document.RowKind(i) != "PlcWatchTableEntry") continue;
            foreach (string field in new[] { "Name", "Address", "DisplayFormat", "ModifyValue" })
                if ((rows[i]?[char.ToLowerInvariant(field[0]) + field[1..]]?.GetValue<string>() ?? "") != (document.Field(i, field) ?? ""))
                    throw new BridgeException("VerificationFailed", "Typed native row field/order differs: " + field);
        }
    }
    private async Task<object> ReconcileRecovery(JsonObject args, CancellationToken token)
    {
        var record = store.Read(Text(args, "operationId")); var review = LoadReview(record);
        if (record.State is not ("recoveryIntent" or "outcomeUnknown") || record.RecoveryDestinations.Count != 1)
            throw new BridgeException("InvalidOperationState", "An admitted attempted recovery is required.");
        string role = Text(args, "role"); if (role is not ("original" or "restored")) throw new BridgeException("InvalidRequest", "role must be original or restored.");
        string directory = DirectoryOf(record);
        JsonObject ReadProof(string name)
        {
            var reference = record.Evidence.Single(e => e.Gate == name).Artifact; var proofBytes = File.ReadAllBytes(Resolve(reference));
            if (WatchTableDocument.Hash(proofBytes) != reference.Sha256) throw new BridgeException("ArtifactChanged", "Retained recovery evidence changed.");
            return JsonNode.Parse(proofBytes)!.AsObject();
        }
        object CompleteRecovered()
        {
            var retainedRecovery = ReadProof(record.Evidence.Any(e => e.Gate == "recovery.retrieve") ? "recovery.retrieve" : "recovery.located");
            var original = ReadProof("recovery.original"); var restored = ReadProof("recovery.restored");
            ValidateReconciliationSnapshot(record, review, retainedRecovery, original, "original");
            ValidateReconciliationSnapshot(record, review, retainedRecovery, restored, "restored");
            string disposition = original["target"]!["semanticSha256"]!.GetValue<string>() == record.Plan.Target.Reference.ContentSha256 ? "unchanged" : "verifiedApplied";
            record = store.Reconcile(record.Id, record.Revision, [new(record.Plan.Resources[0].ResourceKey, disposition, null, record.Evidence.Single(e => e.Gate == "recovery.original").Artifact)],
                [new(record.Plan.Resources[0].ResourceKey, record.RecoveryDestinations[0].RecoveryLocation, "verifiedRestored", record.Evidence.Single(e => e.Gate == "recovery.restored").Artifact)]);
            return new { operation = record, verified = "original and fresh recovered project", automaticReplay = false };
        }
        // A Host may exit after persisting the last proof and before the terminal journal entry.
        // Resume solely from both immutable proofs; never repeat retrieval or a native mutation.
        if (record.Evidence.Any(e => e.Gate == "recovery.original") && record.Evidence.Any(e => e.Gate == "recovery.restored"))
            return CompleteRecovered();
        if (record.Evidence.Any(e => e.Gate == "recovery." + role)) throw new BridgeException("EvidenceAlreadyRecorded", "The original recovery observation is immutable.");
        var recoverySelector = RecoverySelector(review);
        // Locate a retrieved project by the active native session, then validate its physical path against the reserved destination below.
        if (role == "restored") { recoverySelector.Remove("projectPath"); recoverySelector.Remove("projectName"); }
        var snapshot = await Snapshot(recoverySelector, token); var observation = Observation(snapshot);
        if (!record.Evidence.Any(e => e.Gate is "recovery.retrieve" or "recovery.located"))
        {
            if (role != "restored") throw new BridgeException("RecoveryObservationRequired", "First locate the already retrieved project inside its admitted destination; never repeat retrieval.");
            string destinationKey = EngineeringResourceKey.ForPath("project", record.RecoveryDestinations[0].RecoveryLocation);
            string actualKey = EngineeringResourceKey.ForPath("project", observation.ProjectFilePath);
            ValidateSnapshotIdentity(record, snapshot, observation.ProjectFilePath);
            if (!actualKey.StartsWith(destinationKey + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(observation.ProjectFilePath), ".ap21", StringComparison.OrdinalIgnoreCase) ||
                observation.AdapterSha256 != record.Plan.Target.AdapterSha256 || observation.ProfileSha256 != record.Plan.Target.ProfileSha256 ||
                observation.HardwareIdentity != record.Plan.Target.HardwareIdentity || !observation.Offline || snapshot["projectModified"]?.GetValue<bool>() != false ||
                observation.Reference.ContentSha256 != record.Plan.Target.Reference.ContentSha256 ||
                record.Plan.RequiredProbes.Any(p => !observation.Probes.Any(v => v.Name == p && v.State == "observed")) ||
                Text(snapshot, "peerSha256") != review["before"]!["peerSha256"]!.GetValue<string>() ||
                snapshot["target"]!["semanticSha256"]!.GetValue<string>() != record.Plan.Target.Reference.ContentSha256 ||
                snapshot["target"]!["isConsistent"]?.GetValue<bool>() != true ||
                snapshot["processId"]?.GetValue<int>() <= 0 || snapshot["processId"]!.ToJsonString() == review["before"]!["processId"]!.ToJsonString() ||
                observation.SessionEpoch == record.Plan.Target.SessionEpoch)
                throw new BridgeException("RecoveryVerificationFailed", "The saved baseline project must be independently observed within the exact admitted destination.");
            ValidateCapturedContent(snapshot, record.Plan.Target.Reference.ContentSha256!);
            var proof = Save(directory, "recovery-located-" + Guid.NewGuid().ToString("N") + ".json", new { recovered = new { project = new { path = observation.ProjectFilePath },
                    processId = snapshot["processId"]!.GetValue<int>(), sessionEpoch = observation.SessionEpoch },
                restored = snapshot, retrievalResponseRetained = false, nativeRestoreInvoked = false, automaticReplay = false });
            record = store.RecordRecoveryEvidence(record.Id, record.Revision, new("recovery.located", proof));
            return new { operation = record, located = true, evidence = proof, next = "Close and reopen this located project in another fresh instance, then verify restored and original separately." };
        }
        var recovery = ReadProof(record.Evidence.Any(e => e.Gate == "recovery.retrieve") ? "recovery.retrieve" : "recovery.located");
        ValidateReconciliationSnapshot(record, review, recovery, snapshot, role);
        var evidence = Save(directory, "reconcile-" + role + "-" + Guid.NewGuid().ToString("N") + ".json", snapshot);
        record = store.RecordRecoveryEvidence(record.Id, record.Revision, new("recovery." + role, evidence));
        if (!record.Evidence.Any(e => e.Gate == "recovery.original") || !record.Evidence.Any(e => e.Gate == "recovery.restored"))
            return new { operation = record, verified = role, evidence, next = "Freshly observe the other resource before reservations can be released." };
        return CompleteRecovered();
    }
    private void ValidateReconciliationSnapshot(EngineeringOperationRecord record, JsonObject review, JsonObject recovery, JsonObject snapshot, string role)
    {
        var observation = Observation(snapshot);
        string expectedPath = role == "original" ? record.Plan.Target.ProjectFilePath : recovery["recovered"]!["project"]!["path"]!.GetValue<string>();
        ValidateSnapshotIdentity(record, snapshot, expectedPath);
        if (snapshot["processId"]?.GetValue<int>() <= 0 || snapshot["processId"]!.ToJsonString() == review["before"]!["processId"]!.ToJsonString() ||
            observation.SessionEpoch == record.Plan.Target.SessionEpoch)
            throw new BridgeException("FreshInstanceRequired", "Recovery reconciliation requires a fresh original/restored instance and session epoch.");
        if (EngineeringResourceKey.ForPath("project", observation.ProjectFilePath) != EngineeringResourceKey.ForPath("project", expectedPath) ||
            observation.AdapterSha256 != record.Plan.Target.AdapterSha256 || observation.ProfileSha256 != record.Plan.Target.ProfileSha256 ||
            observation.HardwareIdentity != record.Plan.Target.HardwareIdentity || snapshot["projectModified"]!.GetValue<bool>())
            throw new BridgeException("IdentityMismatch", "Exact saved original/recovery identity required.");
        if (Text(snapshot, "peerSha256") != review["before"]!["peerSha256"]!.GetValue<string>()) throw new BridgeException("PreservationFailed", "Peer manifest differs.");
        string content = snapshot["target"]!["semanticSha256"]!.GetValue<string>();
        if (role == "restored")
        {
            if (snapshot["processId"]!.ToJsonString() == recovery["recovered"]!["processId"]!.ToJsonString() || observation.SessionEpoch == recovery["recovered"]!["sessionEpoch"]!.GetValue<string>() ||
                content != record.Plan.Target.Reference.ContentSha256) throw new BridgeException("FreshInstanceRequired", "Reopen the recovered project in another fresh instance and verify baseline content.");
        }
        else if (content != record.Plan.Target.Reference.ContentSha256 && !Applied(review, snapshot))
            throw new BridgeException("ReconciliationIncomplete", "Original project has an unreviewed outcome.");
        ValidateCapturedContent(snapshot, content);
    }
    private JsonObject LoadReview(EngineeringOperationRecord record)
    {
        if (!IsWatchPlan(record)) throw new BridgeException("InvalidOperation", "Not a watch plan.");
        var review = record.Plan.Inputs[0]; string path = Resolve(review);
        if (WatchTableDocument.Hash(File.ReadAllBytes(path)) != review.Sha256) throw new BridgeException("ArtifactChanged", "Review changed.");
        return JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    }
    private string DirectoryOf(EngineeringOperationRecord record) => Path.GetDirectoryName(Resolve(record.Plan.Inputs[0]))!;
    private string Resolve(EngineeringArtifactReference artifact) => PackageStorage.Under(root, artifact.RelativePath);
    private EngineeringArtifactReference Artifact(JsonObject artifact)
    {
        string path = Text(artifact, "path"); string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        _ = PackageStorage.Under(root, relative); return new(relative, Text(artifact, "sha256"));
    }
    private EngineeringArtifactReference Save(string directory, string name, object value) => SaveBytes(directory, name, JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json));
    private EngineeringArtifactReference SaveBytes(string directory, string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name); PackageStorage.EnsureNoLinks(path);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(true); }
        return new(Path.GetRelativePath(root, path).Replace('\\', '/'), WatchTableDocument.Hash(bytes));
    }
    private static NativeTargetObservation Observation(JsonObject snapshot) => snapshot["observation"]!.Deserialize<NativeTargetObservation>(BridgeProtocol.Json)!;
    private static JsonObject Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
    private static string Text(JsonObject args, string key) => args[key]?.GetValue<string>() ?? throw new BridgeException("InvalidRequest", key + " is required.");
}
