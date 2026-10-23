using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

public sealed partial class WatchTableDispatcher
{
    /// <summary>Reconcile observed outcomes only. This route never checkpoints, restores, deletes or replays a native mutation.</summary>
    private async Task<object> ReconcileInterrupted(JsonObject args, CancellationToken token)
    {
        if (args.Any(x => x.Key is not ("operationId" or "destinationDisposition")))
            throw new BridgeException("InvalidRequest", "Interrupted reconciliation accepts an operation ID and optional destination disposition, never a caller-selected recovery path.");
        var record = store.Read(Text(args, "operationId"));
        // A Host may die after durable acquisition but before receiving the native lease token.
        // Explicit reconciliation adopts only the uncertainty, never the old lease or a mutation.
        if (IsWatchPlan(record) && record.State == "acquired") record = store.MarkUnknown(record.Id, record.Revision);
        if (!IsWatchPlan(record) || record.State is not ("outcomeUnknown" or "checkpointIntent" or "ready" or "dispatchIntent" or "recoveryIntent") ||
            record.Plan.Resources.Count != 1 || record.Plan.Resources[0].Kind != "project" ||
            record.Plan.Resources[0].ResourceKey != EngineeringResourceKey.ForPath("project", record.Plan.Target.ProjectFilePath) ||
            record.RecoveryDestinations.Count > 1)
            throw new BridgeException("InvalidOperationState", "Interrupted reconciliation requires one exact in-flight watch project and at most its admitted recovery destination.");
        if (record.Evidence.Any(x => x.Gate is "recovery.retrieve" or "recovery.located"))
            throw new BridgeException("RecoveryProofAlreadyBound", "Use the original/restored reconciliation route for the retained successful retrieval proof.");
        string? destinationDisposition = args["destinationDisposition"]?.GetValue<string>();
        if (record.RecoveryDestinations.Count == 0 && destinationDisposition is not null ||
            record.RecoveryDestinations.Count == 1 && destinationDisposition is not ("verifiedAbsent" or "retainedPartial"))
            throw new BridgeException("InvalidRequest", "An admitted destination requires explicit verifiedAbsent or retainedPartial; an operation without a destination accepts neither.");

        var review = LoadReview(record);
        var snapshot = await Snapshot(review["selector"]!.AsObject(), token);
        var observation = Observation(snapshot); NativeTargetGuard.Validate(observation);
        EngineeringIdentityGuard.EnsureSameObject(record.Plan.Target.Reference, observation.Reference);
        if (EngineeringResourceKey.ForPath("project", observation.ProjectFilePath) != record.Plan.Resources[0].ResourceKey ||
            observation.AdapterSha256 != record.Plan.Target.AdapterSha256 || observation.ProfileSha256 != record.Plan.Target.ProfileSha256 ||
            observation.HardwareIdentity != record.Plan.Target.HardwareIdentity || observation.NativeIdentity != record.Plan.Target.NativeIdentity ||
            observation.Reference.Snapshot.TiaVersion != record.Plan.Target.Reference.Snapshot.TiaVersion || !observation.Offline ||
            snapshot["projectModified"]?.GetValue<bool>() != false ||
            record.Plan.RequiredProbes.Any(name => !observation.Probes.Any(p => p.Name == name && p.State == "observed")))
            throw new BridgeException("IdentityMismatch", "Reconciliation requires the exact saved offline original project, owner, binary, hardware and profile.");
        int processId = snapshot["processId"]?.GetValue<int>() ?? 0;
        int beforeProcessId = review["before"]?["processId"]?.GetValue<int>() ?? 0;
        if (processId <= 0 || beforeProcessId <= 0 || processId == beforeProcessId || observation.SessionEpoch == record.Plan.Target.SessionEpoch)
            throw new BridgeException("FreshInstanceRequired", "Observe the original project in a different native process and session epoch before reconciling interruption.");
        if (Text(snapshot, "peerSha256") != review["before"]!["peerSha256"]!.GetValue<string>() ||
            snapshot["target"]?["isConsistent"]?.GetValue<bool>() != true)
            throw new BridgeException("PreservationFailed", "Original native peers and target consistency must match the retained baseline.");
        string content = snapshot["target"]!["semanticSha256"]!.GetValue<string>();
        string originalDisposition = content == record.Plan.Target.Reference.ContentSha256 ? "unchanged" :
            content == Text(review, "expectedSemanticSha256") ? "verifiedApplied" :
            throw new BridgeException("ReconciliationIncomplete", "The original contains an unreviewed outcome; interruption cannot release its reservation.");
        if (observation.Reference.ContentSha256 != content)
            throw new BridgeException("ArtifactChanged", "Native observation and exported target content disagree.");
        ValidateCapturedContent(snapshot, content);

        InterruptedDestinationManifest? manifest = null;
        if (record.RecoveryDestinations.Count == 1)
        {
            string destination = record.RecoveryDestinations[0].RecoveryLocation;
            if (record.RecoveryDestinations[0].ResourceKey != record.Plan.Resources[0].ResourceKey)
                throw new BridgeException("InvalidRecovery", "The persisted recovery destination belongs to a different resource.");
            manifest = InspectInterruptedDestination(destination, token);
            var second = InspectInterruptedDestination(destination, token);
            if (CanonicalJson.Sha256(JsonSerializer.SerializeToNode(manifest, BridgeProtocol.Json)!) !=
                CanonicalJson.Sha256(JsonSerializer.SerializeToNode(second, BridgeProtocol.Json)!))
                throw new BridgeException("RecoveryInspectionChanged", "Recovery output changed during bounded inspection.");
            bool absentOrEmpty = !manifest.Exists || manifest.Directories.Count == 0 && manifest.Files.Count == 0;
            if (destinationDisposition == "verifiedAbsent" && !absentOrEmpty || destinationDisposition == "retainedPartial" && absentOrEmpty)
                throw new BridgeException("RecoveryDispositionMismatch", "verifiedAbsent requires an absent/empty directory; retainedPartial requires observed retained entries.");
        }
        token.ThrowIfCancellationRequested();
        var proof = Save(DirectoryOf(record), "reconcile-interrupted-" + Guid.NewGuid().ToString("N") + ".json",
            new { original = snapshot, originalDisposition, destinationDisposition, destination = manifest,
                automaticReplay = false, nativeRestoreInvoked = false, restoredProjectVerified = false });
        if (manifest is not null)
            record = store.RecordRecoveryEvidence(record.Id, record.Revision, new("recovery.interrupted." + record.Revision, proof));
        record = store.Reconcile(record.Id, record.Revision,
            [new(record.Plan.Resources[0].ResourceKey, originalDisposition, null, proof)],
            manifest is null ? [] : [new(record.Plan.Resources[0].ResourceKey, record.RecoveryDestinations[0].RecoveryLocation, destinationDisposition!, proof)]);
        return new { operation = record, originalDisposition, destinationDisposition, evidence = proof, automaticReplay = false, restoredProjectVerified = false };
    }

    private static byte[] ReadRecoverySnapshot(string path)
    {
        PackageStorage.EnsureNoLinks(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 16777216) throw new BridgeException("RecoveryInspectionLimit", "Fresh watch export must be between one byte and 16 MiB.");
            byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
        }
        catch (IOException error) { throw new BridgeException("RecoveryInspectionFailed", "Fresh native export is missing, locked or unreadable.", error); }
        catch (UnauthorizedAccessException error) { throw new BridgeException("RecoveryInspectionFailed", "Fresh native export access was denied.", error); }
    }

    private sealed record InterruptedRecoveryFile(string RelativePath, long Bytes, string Sha256);
    private sealed record InterruptedDestinationManifest(string Destination, bool Exists,
        IReadOnlyList<string> Directories, IReadOnlyList<InterruptedRecoveryFile> Files, long TotalBytes,
        string Scope = "retainedRegularFileBytesOnly", bool RestoredProjectVerified = false);

    private InterruptedDestinationManifest InspectInterruptedDestination(string destination, CancellationToken token)
    {
        // Resolve only the persisted admission; never accept a replacement path or inspect outside this artifact root.
        string relative = PackageStorage.Relative(Path.GetRelativePath(root, destination));
        string exact = PackageStorage.Under(root, relative);
        if (EngineeringResourceKey.ForPath("workspace", exact) != EngineeringResourceKey.ForPath("workspace", destination))
            throw new BridgeException("InvalidRecovery", "Recovery output must be the exact admitted local directory inside the artifact root.");
        var files = new List<InterruptedRecoveryFile>(); var directories = new List<string>();
        var pending = new Stack<string>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0; int entries = 0;
        try
        {
            FileAttributes rootAttributes;
            try { rootAttributes = File.GetAttributes(exact); }
            catch (FileNotFoundException) { return new(exact, false, [], [], 0); }
            catch (DirectoryNotFoundException) { return new(exact, false, [], [], 0); }
            if ((rootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 || (rootAttributes & FileAttributes.Directory) == 0)
                throw new BridgeException("RecoveryInspectionUnsafe", "The recovery root must be an ordinary local directory.");
            pending.Push(exact);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested(); PackageStorage.EnsureNoLinks(directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > 4096) throw new BridgeException("RecoveryInspectionLimit", "Recovery inspection is bounded to 4096 file/directory entries.");
                    string name = PackageStorage.Relative(Path.GetRelativePath(exact, path));
                    _ = PackageStorage.Under(exact, name);
                    if (!paths.Add(name.Normalize(NormalizationForm.FormC))) throw new BridgeException("RecoveryInspectionUnsafe", "Recovery output has ambiguous case/Unicode paths.");
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        throw new BridgeException("RecoveryInspectionUnsafe", "Recovery inspection never follows links or device entries.");
                    if ((attributes & FileAttributes.Directory) != 0) { directories.Add(name); pending.Push(path); continue; }
                    _ = EngineeringResourceKey.ForPath("workspace", path); // Refuse multiply linked local files and remote aliases.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > 268435456 || total + stream.Length > 1073741824)
                        throw new BridgeException("RecoveryInspectionLimit", "Recovery inspection is bounded to 256 MiB per file and 1 GiB total.");
                    long size = stream.Length; total += size;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[65536]; long consumed = 0; int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        token.ThrowIfCancellationRequested(); consumed += read;
                        if (consumed > size) throw new BridgeException("RecoveryInspectionChanged", "Recovery file changed while hashing.");
                        hash.AppendData(buffer, 0, read);
                    }
                    if (consumed != size || stream.Length != size) throw new BridgeException("RecoveryInspectionChanged", "Recovery file length changed while hashing.");
                    files.Add(new(name, size, Convert.ToHexStringLower(hash.GetHashAndReset())));
                }
            }
            return new(exact, true, directories.Order(StringComparer.Ordinal).ToArray(), files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray(), total);
        }
        catch (IOException error) { throw new BridgeException("RecoveryInspectionFailed", "Recovery output changed, is locked or could not be read.", error); }
        catch (UnauthorizedAccessException error) { throw new BridgeException("RecoveryInspectionFailed", "Recovery output access was denied.", error); }
    }
}
