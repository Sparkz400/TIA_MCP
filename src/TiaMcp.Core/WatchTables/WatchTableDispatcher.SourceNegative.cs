using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

public sealed partial class WatchTableDispatcher
{
    private async Task<object> ReconcileSourceNegative(JsonObject args, CancellationToken token)
    {
        var admissions = new EngineeringMutationAdmissionStore(root);
        var record = admissions.Read(Text(args, "admissionId"));
        if (record.Scope.Operation != "watch_authoring_source_negative" || record.State is not ("dispatchIntent" or "outcomeUnknown") ||
            record.Scope.Resources.Count != 1 || record.Scope.Resources[0].Kind != "project")
            throw new BridgeException("InvalidOperationState", "Only an unresolved exact watch source-negative project admission can be observed.");
        string project = record.Scope.Resources[0].Path;
        string marker = PackageStorage.Under(root, "watch-source-negative/negative-intent-" +
            WatchTableDocument.Hash(System.Text.Encoding.UTF8.GetBytes(project)) + ".json");
        var retained = JsonNode.Parse(ReadRecoverySnapshot(marker))!.AsObject();
        var before = retained["before"]!.AsObject(); var expected = Observation(before);
        NativeTargetGuard.Validate(expected);
        if (Text(retained, "requestSha256") != record.Scope.RequestSha256 || expected.ProjectFilePath != project ||
            expected.AdapterSha256 != record.Scope.AdapterSha256 || expected.SessionEpoch != record.Scope.SessionEpoch ||
            before["processId"]?.GetValue<int>() != record.Scope.ProcessId || before["target"]?["exists"]?.GetValue<bool>() != false)
            throw new BridgeException("IdentityMismatch", "Retained pre-effect receipt differs from the exact dispatch admission.");
        ValidateCapturedContent(before, expected.Reference.ContentSha256!);
        var receipt = Artifact(before["receipt"]!.AsObject());
        var receiptBytes = ReadRecoverySnapshot(Resolve(receipt));
        var copy = before.DeepClone().AsObject(); copy.Remove("receipt");
        if (WatchTableDocument.Hash(receiptBytes) != receipt.Sha256 || !JsonNode.DeepEquals(JsonNode.Parse(receiptBytes), copy))
            throw new BridgeException("ArtifactChanged", "Native baseline receipt changed.");
        var binding = retained["sourceBinding"]!.AsObject();
        var selector = new JsonObject { ["projectPath"] = project, ["plcPath"] = binding["plcPath"]!.DeepClone(),
            ["tableName"] = WatchAuthoringProfile.Table, ["group"] = "root", ["allowAbsent"] = true };
        var current = await Snapshot(selector, token); var actual = Observation(current); NativeTargetGuard.Validate(actual);
        if (actual.SessionEpoch == expected.SessionEpoch || current["processId"]?.GetValue<int>() == record.Scope.ProcessId ||
            actual.AdapterSha256 != expected.AdapterSha256 || actual.ProfileSha256 != expected.ProfileSha256 ||
            actual.HardwareIdentity != expected.HardwareIdentity || actual.NativeIdentity != expected.NativeIdentity ||
            actual.ProjectFilePath != project || actual.Reference.Object != expected.Reference.Object || !actual.Offline ||
            actual.Reference.Snapshot.TiaVersion != expected.Reference.Snapshot.TiaVersion ||
            new[] { "offline", "watchImport" }.Any(name => !actual.Probes.Any(p => p.Name == name && p.State == "observed")) ||
            current["projectModified"]?.GetValue<bool>() != false || current["target"]?["isConsistent"]?.GetValue<bool>() != true ||
            current["target"]?["exists"]?.GetValue<bool>() != false || current["target"]?["name"]?.GetValue<string>() != WatchAuthoringProfile.Table ||
            current["target"]?["group"]?.GetValue<string>() != "root" ||
            actual.Reference.ContentSha256 != expected.Reference.ContentSha256 ||
            !JsonNode.DeepEquals(before["peers"], current["peers"]) || Text(before, "peerSha256") != Text(current, "peerSha256"))
            throw new BridgeException("ReconciliationIncomplete", "Fresh saved offline original with every baseline peer is required; changed or unknown outcomes stay owned.");
        ValidateCapturedContent(current, expected.Reference.ContentSha256!);
        string directory = Path.Combine(root, "watch-source-negative-reconciliation", record.Id); Directory.CreateDirectory(directory);
        var evidence = Save(directory, Guid.NewGuid().ToString("N") + ".json", new { before, current, automaticReplay = false, nativeMutationInvoked = false });
        var completed = admissions.Reconcile(record.Id, record.Revision, [new(record.ResourceKeys[0], "unchanged", null, evidence)]);
        return new { admission = completed, evidence, automaticReplay = false, nativeMutationInvoked = false };
    }
}
