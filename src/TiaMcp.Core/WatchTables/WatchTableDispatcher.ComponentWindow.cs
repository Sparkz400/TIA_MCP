using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

public sealed partial class WatchTableDispatcher
{
    private string? componentWindowId;
    private string? componentOwnerCapability;

    private async Task<JsonElement> ComponentWindowControl(JsonObject args, CancellationToken token)
    {
        if (!allowComponentQualification) throw new BridgeException("ComponentQualificationDisabled", "This Host did not explicitly enable the isolated component lifetime.");
        var windows = new ComponentQualificationWindowStore(root); string action = Text(args, "action");
        if (action == "begin")
        {
            if (componentWindowId is not null) throw new BridgeException("ResourceBusy", "This Host already owns a component lifetime.");
            string id = Guid.NewGuid().ToString("N");
            var described = Node(await native.CallAsync("component_window_plan", new { id, artifactDirectory = root,
                planId = Text(args, "planId"), planPreviewHash = Text(args, "planPreviewHash") }, token));
            var manifest = described["manifest"]!.Deserialize<ComponentWindowManifest>(BridgeProtocol.Json)!;
            if (manifest.Id != id || ComponentQualificationWindowStore.ManifestHash(manifest) != Text(described, "manifestSha256"))
                throw new BridgeException("MutationScopeIncomplete", "Native manifest does not bind the exact planned window.");
            var acquired = windows.Acquire(manifest); componentWindowId = acquired.Reservation.Id; componentOwnerCapability = acquired.Capability;
            // Return the owner capability before any child dispatch: a lost start response must not strand the only recovery credential.
            var result = new JsonObject { ["state"] = "reserved", ["reservation"] = JsonSerializer.SerializeToNode(acquired.Reservation, BridgeProtocol.Json), ["nextAction"] = "start" };
            result["componentOwner"] = new JsonObject { ["id"] = componentWindowId, ["capability"] = componentOwnerCapability,
                ["manifestSha256"] = acquired.Reservation.ManifestSha256, ["workspace"] = manifest.Workspace,
                ["coordinationScope"] = "Configured canonical Host store; unknown independently configured stores are not federated." };
            return Element(result);
        }
        string requestedId = Text(args, "id"); var record = windows.Read(requestedId);
        if (action == "status")
        {
            if (args["reconcileReceipt"]?.GetValue<bool>() == true)
            {
                string capability = Text(args, "capability");
                if (record.Children.LastOrDefault()?.State is "dispatchIntent" or "outcomeUnknown")
                {
                    string path = Path.Combine(record.Manifest.Workspace, "responses", record.Children.Last().Step + ".json");
                    if (!File.Exists(path)) throw new BridgeException("ComponentOutcomeUnknown", "No exact retained native response exists; no child is replayed.");
                    var proof = new EngineeringArtifactReference(Path.GetRelativePath(root, path).Replace('\\', '/'), PackageStorage.FileHash(path));
                    record = windows.ReconcileChild(record.Id, record.Revision, capability, proof);
                }
            }
            var status = JsonSerializer.SerializeToNode(record, BridgeProtocol.Json)!.AsObject();
            string coordinator = Path.Combine(record.Manifest.Workspace, "component-journal.json");
            if (File.Exists(coordinator)) status["coordinator"] = JsonNode.Parse(File.ReadAllText(coordinator));
            status["nextStep"] = ComponentQualificationWindowStore.NextStep(record);
            return Element(status);
        }
        string owner = Text(args, "capability");
        if (action == "owner-resume")
        {
            if (componentWindowId is not null && componentWindowId != record.Id) throw new BridgeException("ResourceBusy", "This Host already owns another component lifetime.");
            record = windows.Resume(record.Id, record.Revision, owner); componentWindowId = record.Id; componentOwnerCapability = owner;
            if (record.Children.LastOrDefault()?.State == "dispatchIntent") record = windows.Unknown(record.Id, record.Revision, owner);
            await BindComponentWindow(record, token);
            if (record.State == "active" && record.Children.LastOrDefault()?.State is "returned" or "reconciled")
            {
                if (ComponentQualificationWindowStore.NextStep(record) == "observeOriginal") return await DispatchComponentChild("observeOriginal", new JsonObject(), token);
            }
            if (ComponentQualificationWindowStore.NextStep(record) == "observeRestored") return await DispatchComponentChild("observeRestored", new JsonObject(), token);
            if (ComponentQualificationWindowStore.NextStep(record) == "observeRecoveryOriginal") return await DispatchComponentChild("observeRecoveryOriginal", new JsonObject(), token);
            return Element(record);
        }
        if (componentWindowId != requestedId || componentOwnerCapability != owner) throw new BridgeException("ComponentOwnerResumeRequired", "Bind the retained owner in this Host before further lifetime work.");
        if (action == "start") { await BindComponentWindow(record, token); return await DispatchComponentChild("start", new JsonObject(), token); }
        if (action == "recover") return await DispatchComponentChild("recover", new JsonObject(), token);
        if (action is "complete" or "reconcile-recovery")
        {
            var source = Node(await native.CallAsync("component_window_source", new { manifestSha256 = record.ManifestSha256 }, token));
            var receipt = new JsonObject { ["manifestSha256"] = record.ManifestSha256, ["sourceSnapshotSha256"] = Text(source, "sourceSnapshotSha256"),
                ["sourceObservation"] = source, ["childReceipts"] = new JsonArray(record.Children.Select(c => JsonValue.Create(c.Response?.Sha256)).ToArray()) };
            var proof = Save(ComponentResponseDirectory(record), action + "-" + Guid.NewGuid().ToString("N") + ".json", receipt);
            record = action == "complete" ? windows.Complete(record.Id, record.Revision, owner, proof) : windows.ReconcileRecovery(record.Id, record.Revision, owner, proof);
            return Element(new { state = record.State, reservation = record, qualificationOnly = true, lifecycleEvidenceComplete = action == "complete", installationAccepted = action == "complete", recoveryOnlyReconciled = action == "reconcile-recovery",
                productionApplySupported = false, runtimeVerified = false });
        }
        throw new BridgeException("InvalidRequest", "Component action must be begin, start, status, owner-resume, recover, complete or reconcile-recovery.");
    }

    private async Task BindComponentWindow(ComponentWindowReservation record, CancellationToken token)
    {
        var proof = ComponentReservationProof(record);
        var bound = Node(await native.CallAsync("component_window_bind", new { manifest = record.Manifest, manifestSha256 = record.ManifestSha256,
            reservationPath = proof.Path, reservationSha256 = proof.Sha256 }, token));
        if (bound["bound"]?.GetValue<bool>() != true || Text(bound, "manifestSha256") != record.ManifestSha256 ||
            bound["installationAllowed"]?.GetValue<bool>() != record.InstallationAllowed)
            throw new BridgeException("ComponentReservationRequired", "Worker did not bind the exact durable parent reservation.");
    }

    private async Task<JsonElement> DispatchComponentChild(string step, JsonObject request, CancellationToken token)
    {
        if (componentWindowId is null || componentOwnerCapability is null) throw new BridgeException("MutationScopeIncomplete", "The complete component window is not bound in this Host.");
        var windows = new ComponentQualificationWindowStore(root); var record = windows.Read(componentWindowId);
        var arguments = new JsonObject { ["manifestSha256"] = record.ManifestSha256, ["step"] = step, ["request"] = request.DeepClone() };
        string? lease = null; bool intent = false;
        try
        {
            var begin = Node(await native.CallAsync("mutation_begin", new { operation = "component_window_step", arguments }, token));
            lease = Text(begin, "leaseToken"); var scope = begin["scope"]!.Deserialize<EngineeringMutationScope>(BridgeProtocol.Json)!;
            if (scope.Operation != "component_window_step" || scope.RequestSha256 != ComponentQualificationWindowStore.NativeHash(arguments) || scope.AdapterSha256 != record.Manifest.AdapterSha256)
                throw new BridgeException("MutationScopeIncomplete", "Exact worker, native child request and resource scope are required.");
            record = windows.BeginChild(record.Id, record.Revision, componentOwnerCapability, step, scope.RequestSha256, scope.Resources); intent = true;
            var proof = ComponentReservationProof(record);
            var response = await native.CallAsync("mutation_dispatch", new { leaseToken = lease, scopeSha256 = scope.ScopeSha256,
                reservationPath = proof.Path, reservationSha256 = proof.Sha256 }, token);
            var retained = Save(ComponentResponseDirectory(record), step + "-returned.json", response);
            windows.Returned(record.Id, record.Revision, componentOwnerCapability, retained);
            return response;
        }
        catch (Exception error)
        {
            if (intent)
                try
                {
                    var current = windows.Read(record.Id);
                    if (current.Children.LastOrDefault()?.State == "dispatchIntent")
                    {
                        var failure = Save(ComponentResponseDirectory(record), step + "-failure.json", new { exceptionType = error.GetType().FullName, nativeOutcome = "unknown", automaticReplay = false });
                        windows.Unknown(record.Id, current.Revision, componentOwnerCapability, failure);
                    }
                }
                catch { /* The already durable child intent continues to reserve every parent resource. */ }
            throw;
        }
        finally
        {
            if (lease is not null) try { await native.CallAsync("mutation_end", new { leaseToken = lease }, CancellationToken.None); } catch { }
        }
    }

    private async Task<JsonElement?> ForwardComponentQualification(string operation, JsonObject args, CancellationToken token)
    {
        if (componentWindowId is not null && componentOwnerCapability is not null && operation is "block_read" or "type_read" or "tag_table_read" or "software_inspect")
        {
            var active = new ComponentQualificationWindowStore(root).RequireOwner(componentWindowId, componentOwnerCapability);
            var proof = ComponentReservationProof(active);
            return await native.CallAsync("component_window_read", new { operation, arguments = args, reservationPath = proof.Path, reservationSha256 = proof.Sha256 }, token);
        }
        bool componentPrepare = operation == "change_prepare" && args["kind"]?.GetValue<string>() == "library_component_qualification";
        bool componentApply = operation == "change_apply" && (args["changeId"]?.GetValue<string>() ?? "").StartsWith("lib-component-", StringComparison.Ordinal);
        if (!componentPrepare && !componentApply) return null;
        if (!allowComponentQualification || componentWindowId is null) throw new BridgeException("MutationScopeIncomplete", "Use the launch-gated whole-window begin operation before component preparation/apply.");
        var record = new ComponentQualificationWindowStore(root).Read(componentWindowId);
        string id = componentPrepare ? args["payload"]?["changeId"]?.GetValue<string>() ?? "" : Text(args, "changeId");
        if (id != "lib-" + record.Manifest.ComponentId) throw new BridgeException("ComponentOwnerMismatch", "Request identifies a different component window.");
        if (componentPrepare && args["payload"]?["action"]?.GetValue<string>() != "advance") throw new BridgeException("ComponentReplayRefused", "Start/status reconciliation use the explicit lifetime control surface.");
        string step = componentApply ? record.Children.Count == 1 ? "applyLibrary" : record.Children.Count == 4 ? "applyGraphical" : "invalid"
            : record.Children.Count == 2 ? "verifyImport" : record.Children.Count == 3 ? "prepareGraphical" : record.Children.Count == 5 ? "verifyInstallation" : "invalid";
        if (step == "invalid") throw new BridgeException("ComponentReplayRefused", "A reviewed component stage cannot be replayed or reordered.");
        return await DispatchComponentChild(step, args, token);
    }

    private (string Path, string Sha256) ComponentReservationProof(ComponentWindowReservation record)
    {
        string path = PackageStorage.Under(root, "engineering-component-windows/" + record.Id + "/" + record.Revision.ToString("D8") + ".json");
        return (path, PackageStorage.FileHash(path));
    }
    private string ComponentResponseDirectory(ComponentWindowReservation record)
    { string path = PackageStorage.Under(root, "component-window-responses/" + record.Id); Directory.CreateDirectory(path); return path; }
}
