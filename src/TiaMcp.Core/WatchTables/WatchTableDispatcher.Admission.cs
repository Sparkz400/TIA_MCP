using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

public sealed partial class WatchTableDispatcher
{
    // Called only from Forward, after the existing dispatcher semaphore is held.
    private async Task<JsonElement> ForwardAdmitted(string operation, object? arguments, CancellationToken token)
    {
        if (operation.StartsWith("mutation_", StringComparison.Ordinal))
            throw new BridgeException("InternalOperation", "Native mutation admission protocol is private to the dispatcher.");
        var admissions = new EngineeringMutationAdmissionStore(root);
        if (operation == "engineering_mutation_admission_status")
        {
            var request = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject();
            return Element(admissions.Read(Text(request, "admissionId")));
        }
        if (!EngineeringMutationAdmissionStore.IsMutation(operation)) return await native.CallAsync(operation, arguments, token);
        var args = JsonSerializer.SerializeToNode(arguments ?? new { }, BridgeProtocol.Json)!.AsObject();
        // Preserve legacy artifact subdirectories, but bind them to this coordinator's ownership root.
        if (args["artifactDirectory"] is not null)
        {
            if (args["artifactDirectory"] is not JsonValue location || !location.TryGetValue<string>(out string? path) || path is null)
                throw new BridgeException("ArtifactRootMismatch", "Mutation artifactDirectory must be an absolute path string.");
            if (!Path.IsPathFullyQualified(path)) throw new BridgeException("ArtifactRootMismatch", "Mutation artifacts require an absolute path within the configured artifact root.");
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!string.Equals(full, Path.TrimEndingDirectorySeparator(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                try { _ = PackageStorage.Under(root, PackageStorage.Relative(Path.GetRelativePath(root, full))); }
                catch (BridgeException) { throw new BridgeException("ArtifactRootMismatch", "Mutation artifacts must stay within the configured artifact root."); }
            }
            PackageStorage.EnsureNoLinks(full); _ = EngineeringResourceKey.ForPath("workspace", full);
        }
        else args["artifactDirectory"] = root;
        string requestHash = CanonicalJson.Sha256(new JsonObject { ["operation"] = operation, ["arguments"] = args.DeepClone() });
        EngineeringMutationAdmission? record = null; string? lease = null;
        try
        {
            var begin = Node(await native.CallAsync("mutation_begin", new { operation, arguments = args }, token));
            lease = Text(begin, "leaseToken");
            var scope = begin["scope"]?.Deserialize<EngineeringMutationScope>(BridgeProtocol.Json)
                ?? throw new BridgeException("InvalidMutationScope", "Native adapter did not return its complete resource scope.");
            if (scope.Operation != operation) throw new BridgeException("InvalidMutationScope", "Native scope belongs to a different operation.");
            record = admissions.Acquire(scope, requestHash);
            // The committed intent now reserves every exact native-resolved resource before dispatch.
            var response = await native.CallAsync("mutation_dispatch", new { leaseToken = lease, scopeSha256 = scope.ScopeSha256 }, token);
            string directory = PackageStorage.Under(root, "mutation-responses/" + record.Id); Directory.CreateDirectory(directory);
            var proof = Save(directory, "returned.json", response);
            record = admissions.Returned(record.Id, record.Revision, proof);
            return response; // Preserve every existing legacy response field and shape.
        }
        catch (Exception error)
        {
            if (record is not null)
            {
                // Failure to retain diagnostics must still leave the already committed dispatchIntent owning its resources.
                try
                {
                    var current = admissions.Read(record.Id);
                    if (current.State == "dispatchIntent")
                    {
                        string directory = PackageStorage.Under(root, "mutation-responses/" + record.Id); Directory.CreateDirectory(directory);
                        var nativeDetails = (error as BridgeOperationException)?.Details;
                        // A live worker's structured change failure is a returned outcome: its journal (recovery_required etc.)
                        // drives change_restore. Transport loss, timeouts and unstructured errors stay unknown; simulation
                        // download and provisioning failures stay unknown on purpose (their storage needs reconciliation).
                        // A structured station-upload failure only read the instance and reports its retained destination.
                        bool returned = error is BridgeOperationException && operation is ("change_apply" or "change_restore" or "plc_station_upload");
                        // Bind a later independent report check without persisting raw diagnostic text or credentials.
                        string? nativeDetailsHash = nativeDetails is null ? null : CanonicalJson.Sha256(new JsonObject {
                            ["details"] = JsonNode.Parse(nativeDetails.Value.GetRawText()) });
                        var proof = Save(directory, "failure.json", new { type = error.GetType().FullName, automaticReplay = false,
                            nativeOutcome = returned ? "returnedFailure" : "unknown", admissionId = record.Id,
                            nativeErrorCode = (error as BridgeException)?.Code, nativeDetailsSha256 = nativeDetailsHash });
                        if (returned) admissions.Returned(record.Id, current.Revision, proof);
                        else admissions.Unknown(record.Id, current.Revision, proof);
                    }
                }
                catch { /* The prior write-ahead intent still holds ownership; never mask the primary exception. */ }
                if (error is BridgeException bridgeError)
                {
                    var nativeDetails = (bridgeError as BridgeOperationException)?.Details;
                    var details = nativeDetails?.ValueKind == JsonValueKind.Object
                        ? JsonNode.Parse(nativeDetails.Value.GetRawText())!.AsObject()
                        : new JsonObject { ["nativeDetails"] = nativeDetails is null ? null : JsonNode.Parse(nativeDetails.Value.GetRawText()) };
                    details["mutationAdmission"] = new JsonObject { ["admissionId"] = record.Id,
                        ["statusTool"] = "engineering_operation_status", ["automaticReplay"] = false };
                    throw new BridgeOperationException(bridgeError.Code, bridgeError.Message, Element(details));
                }
            }
            throw;
        }
        finally
        {
            if (lease is not null) try { await native.CallAsync("mutation_end", new { leaseToken = lease }, CancellationToken.None); }
                catch { /* Native connection failure cannot release durable unknown ownership. */ }
        }
    }
}
