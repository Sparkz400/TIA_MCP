using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.EngineeringJobs;

// File-only independent acceptance. No native call and no caller-provided success flag can release ownership.
public static class ComponentWindowEvidence
{
    public static void ValidateChild(string root, ComponentWindowReservation r, EngineeringArtifactReference response)
    {
        var value = Read(root, response); var window = Object(value, "componentWindow"); string step = r.Children.Last().Step;
        Need(Text(window, "manifestSha256") == r.ManifestSha256 && Text(window, "step") == step, "Child response does not bind its exact parent/step.");
        Need(window["processId"]?.GetValue<int>() > 0 && !string.IsNullOrWhiteSpace(Text(window, "sessionEpoch")), "Child response lacks native process/session identity.");
        if (step is "start" or "applyLibrary" or "verifyImport" or "prepareGraphical" or "applyGraphical" or "verifyInstallation")
        {
            Need(window["processId"]!.GetValue<int>() == r.Manifest.InitialNativeProcessId && Text(window, "sessionEpoch") == r.Manifest.InitialNativeSessionEpoch,
                "Installation child moved to another native session.");
            var record = Object(value, "record");
            Need(Text(value, "changeId") == "lib-" + r.Manifest.ComponentId && Text(record, "operationId") == r.Manifest.ComponentId,
                "Child component identity differs.");
            string expected = step switch { "start" => "library_prepared", "applyLibrary" => "library_applied", "verifyImport" => "import_verified",
                "prepareGraphical" => "graphical_prepared", "applyGraphical" => "graphical_applied", _ => "installation_verified" };
            Need(Text(record, "state") == expected && Text(value, "state") == expected, "Native child is not in its expected completed state.");
            var contract = Object(record, "contract");
            Need(Text(contract, "planChangeId") == r.Manifest.PlanId && Text(contract, "planPreviewHash") == r.Manifest.PlanPreviewHash &&
                Text(contract, "projectPath") == r.Manifest.ProjectPath, "Child contract differs from the admitted source plan/project.");
            foreach (string name in new[] { "library", "graphical" })
            {
                if (record[name]?["prepared"] is not JsonObject prepared) continue;
                string innerId = Text(prepared, "changeId"), preview = Text(prepared, "previewHash");
                EngineeringIdentityGuard.Sha256(preview, "innerPreviewHash");
                string directory = Path.Combine(r.Manifest.Workspace, "changes", "change-" + innerId);
                ComponentQualificationWindowStore.RequireContained(r.Manifest, [new("workspace", directory, "innerJournal")]);
                Need(EngineeringResourceKey.ForPath("workspace", Text(prepared, "journalPath")) == EngineeringResourceKey.ForPath("workspace", Path.Combine(directory, "journal.json")),
                    "An actual prepared inner journal escaped the dedicated descendant workspace.");
                var durable = JsonNode.Parse(File.ReadAllText(Text(prepared, "journalPath")))!.AsObject();
                Need(Text(durable, "changeId") == innerId && Text(durable, "previewHash") == preview, "Actual inner durable journal identity differs.");
                if (record[name]?["dispatch"] is JsonObject dispatch)
                {
                    Need(Text(dispatch, "changeId") == innerId && Text(dispatch, "previewHash") == preview &&
                        Text(dispatch, "idempotencyKey") == r.Manifest.ComponentId + ":" + name &&
                        dispatch["checkpointSave"]?.GetValue<bool>() == true && dispatch["compile"]?.GetValue<bool>() == true && dispatch["save"]?.GetValue<bool>() == true,
                        "The actual inner-step identity/key or mandatory checkpoint/compile/save differs.");
                    if (record[name]?["result"] is JsonObject applied)
                    {
                        Need(Text(applied, "changeId") == innerId && Text(applied, "previewHash") == preview && Text(applied, "state") == "applied", "An inner applied receipt is absent/mismatched.");
                        string? keyHash = applied["journal"]?["idempotencyKeyHash"]?.GetValue<string>() ?? applied["componentIdempotencyKeyHash"]?.GetValue<string>();
                        Need(keyHash == ComponentQualificationWindowStore.TokenHash(r.Manifest.ComponentId + ":" + name), "Inner applied receipt uses another actual idempotency key.");
                        ValidateCompile(applied["journal"]?["compile"] as JsonObject ?? applied["compile"] as JsonObject);
                        if (name == "library")
                        {
                            string claim = Path.Combine(r.Manifest.Workspace, "changes", "library-key-" + keyHash + ".claim");
                            Need(File.Exists(claim) && File.ReadAllText(claim) == innerId && durable["idempotencyKeyHash"]?.GetValue<string>() == keyHash,
                                "The actual first inner durable idempotency claim is absent or outside its admitted ancestry.");
                            ValidateCheckpoint(r.Manifest, record, applied["journal"] as JsonObject ?? applied, true);
                        }
                        else
                        {
                            Need(durable["idempotencyKey"]?.GetValue<string>() == r.Manifest.ComponentId + ":graphical", "The actual typed journal is not bound to its inner dispatch key.");
                            var typed = (applied["journal"] as JsonObject ?? applied)["typedQualification"];
                            Need(typed?["engineeringVerified"]?.GetValue<bool>() == true && typed?["preservationVerified"]?.GetValue<bool>() == true && typed?["saved"]?.GetValue<bool>() == true,
                                "Typed native result is missing compiler, preservation, readback or save evidence.");
                        }
                    }
                }
            }
        }
        if (step is "start" or "verifyInstallation" or "observeOriginal" or "recover" or "observeRestored" or "observeRecoveryOriginal")
        {
            var observation = Object(window, "observation"); ValidateSnapshot(r.Manifest, observation);
            Need(observation["processId"]!.GetValue<int>() == window["processId"]!.GetValue<int>() && Text(observation, "sessionEpoch") == Text(window, "sessionEpoch"),
                "Native peer capture belongs to another child process/session.");
        }
        if (step == "recover")
        {
            Need(Text(value, "state") == "restored_to_new_locations" && value["resources"] is JsonArray { Count: 1 }, "Recovery must retain its exact single project receipt.");
            var anchor = Object(value, "coordinatorBeforeRecovery"); var inner = Object(anchor, "firstInnerRecoveryStatus");
            ValidateCheckpoint(r.Manifest, anchor, inner["journal"] as JsonObject ?? inner, false);
            Need(JsonNode.DeepEquals(anchor["initialCheckpoint"], value["firstCheckpointEvidence"]), "Recovery did not retain the exact validated first checkpoint.");
            var start = Read(root, r.Children.Single(c => c.Step == "start").Response!);
            Need(JsonNode.DeepEquals(start["record"]?["library"]?["prepared"], anchor["library"]?["prepared"]), "Recovery anchor changed the first reviewed inner preparation.");
        }
    }

    public static void Validate(string root, ComponentWindowReservation r, EngineeringArtifactReference completion)
    {
        var final = Read(root, completion);
        Need(Text(final, "manifestSha256") == r.ManifestSha256 && final["childReceipts"] is JsonArray receipts && receipts.Count == r.Children.Count,
            "Completion does not bind every retained child receipt.");
        var children = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (int i = 0; i < r.Children.Count; i++)
        {
            var child = r.Children[i]; Need(child.State is "returned" or "reconciled" && child.Response is not null, "An unresolved child cannot complete the lifetime.");
            Need(final["childReceipts"]![i]!.GetValue<string>() == child.Response!.Sha256, "Completion replaced a child receipt.");
            var prefix = r with { Children = r.Children.Take(i + 1).ToArray() };
            ValidateChild(root, prefix, child.Response); children.Add(child.Step, Read(root, child.Response));
        }
        JsonObject Observation(string step) => Object(Object(children[step], "componentWindow"), "observation");
        var before = Observation("start"); var after = Observation("verifyInstallation"); var original = Observation("observeOriginal");
        var restored = Observation("recover"); var reopened = Observation("observeRestored");
        Need(Text(before, "projectPath") == r.Manifest.ProjectPath && Text(after, "projectPath") == r.Manifest.ProjectPath && Text(original, "projectPath") == r.Manifest.ProjectPath,
            "Original native observations identify another project.");
        Need(Text(before, "nativeBirthUtc") == Text(after, "nativeBirthUtc"), "Installation observations do not share their admitted exact native lifetime.");
        string recoveredPath = Text(restored, "projectPath");
        Need(recoveredPath != r.Manifest.ProjectPath && Text(reopened, "projectPath") == recoveredPath &&
            IsUnder(Path.Combine(r.Manifest.Workspace, "recovery"), recoveredPath), "Recovery escaped its pre-admitted new destination.");
        Need(Text(original, "nativeBirthUtc") != Text(after, "nativeBirthUtc") && Text(reopened, "nativeBirthUtc") != Text(restored, "nativeBirthUtc") &&
            Text(original, "sessionEpoch") != Text(after, "sessionEpoch") && Text(reopened, "sessionEpoch") != Text(restored, "sessionEpoch"), "Fresh native lifecycles are missing.");
        Compare(after, original); Compare(before, restored); Compare(before, reopened);
        CompareVersions(after, original); CompareVersions(before, restored); CompareVersions(before, reopened);
        var installed = Object(children["verifyInstallation"], "record");
        var pinnedVersions = (JsonArray)installed["contract"]!["plan"]!["versions"]!;
        Need(pinnedVersions.Count > 0 && before["componentVersions"] is JsonArray beforeVersions && beforeVersions.Count == pinnedVersions.Count &&
            beforeVersions.All(v => v!["exists"]?.GetValue<bool>() == false), "The initial checkpoint must precede every exact component dependency.");
        var afterVersions = (JsonArray)after["componentVersions"]!;
        Need(afterVersions.Count == pinnedVersions.Count && afterVersions.All(v => v!["exists"]?.GetValue<bool>() == true &&
            v["state"]?.GetValue<string>() == "Committed" && v["doNotUse"]?.GetValue<bool>() == false), "Installed dependency native library state is incomplete.");
        Need(pinnedVersions.Select(v => v!["typeGuid"]!.GetValue<string>() + ":" + v["versionGuid"]!.GetValue<string>()).Order().SequenceEqual(
            afterVersions.Select(v => v!["typeGuid"]!.GetValue<string>() + ":" + v["versionGuid"]!.GetValue<string>()).Order()), "Reopened component type/version pins differ.");
        var baselineKeys = Objects(before).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var allowedNew = new HashSet<string>(StringComparer.Ordinal);
        var evidence = Object(installed, "installationEvidence");
        foreach (var v in (JsonArray)evidence["versions"]!) foreach (var p in (JsonArray)v!["objectPaths"]!)
        {
            string path = p!.GetValue<string>(); string[] matches = Objects(after).Where(kv => kv.Value!["path"]!.GetValue<string>() == path).Select(kv => kv.Key).ToArray();
            Need(matches.Length == 1, "An installed dependency is absent/ambiguous in the complete native observation."); allowedNew.Add(matches[0]);
        }
        var plan = Object(Object(installed, "contract"), "plan"); string caller = Text(Object(plan, "caller"), "blockPath");
        bool absent = plan["caller"]!["initiallyAbsent"]!.GetValue<bool>();
        if (absent) allowedNew.Add("block:" + caller);
        allowedNew.Add("block:" + Text(Object(plan, "instanceDb"), "path"));
        var actualNew = Objects(after).Select(x => x.Key).Where(k => !baselineKeys.Contains(k)).ToHashSet(StringComparer.Ordinal);
        Need(actualNew.SetEquals(allowedNew), "Unexpected added/missing peer identities after component installation.");
        CompareMetadata(before, after);
        foreach (var peer in Objects(before))
        {
            Need(Objects(after).ContainsKey(peer.Key), "A baseline peer was deleted.");
            if (!absent && peer.Key == "block:" + caller) continue; // The exact reviewed caller is independently checked against its prepared native artifact below.
            CompareObject((JsonObject)peer.Value!, (JsonObject)Objects(after)[peer.Key]!);
        }
        var graphical = Object(installed, "graphical"); var preview = Object(Object(graphical, "prepared"), "preview");
        CompareArtifact(Object(Object(preview, "typed"), "nativeArtifact"), Object((JsonObject)Objects(after)["block:" + caller]!, "artifact"));
        foreach (var effect in (JsonArray)preview["effects"]!)
            if (effect?["action"]?.GetValue<string>() == "createInstanceDB")
                CompareArtifact(Object(effect!.AsObject(), "artifact"), Object((JsonObject)Objects(after)["block:" + Text(effect.AsObject(), "path")]!, "artifact"));
        var checkpoint = Object(installed, "initialCheckpoint"); var first = Object(Object(installed, "library"), "prepared");
        Need(Text(checkpoint, "changeId") == Text(first, "changeId") && Text(checkpoint, "previewHash") == Text(first, "previewHash") &&
            Text(checkpoint, "originalPath") == r.Manifest.ProjectPath && IsUnder(Path.Combine(r.Manifest.Workspace, "changes", "change-" + Text(first, "changeId")), Text(checkpoint, "archivePath")),
            "Recovery anchor is not the admitted first inner import checkpoint.");
        VerifyNativeArtifact(new JsonObject { ["path"] = Text(checkpoint, "archivePath"), ["sha256"] = Text(checkpoint, "sha256") });
        var recovery = children["recover"]["resources"]![0]!.AsObject();
        Need(Text(recovery, "kind") == "project" && Text(recovery, "archiveSha256") == Text(checkpoint, "sha256") && recovery["retrieved"]?.GetValue<bool>() == true &&
            Text(Object(recovery, "project"), "path") == recoveredPath, "Actual recovered project/checkpoint identity differs.");
        Need(final["sourceSnapshotSha256"]?.GetValue<string>() == r.Manifest.SourceSnapshotSha256, "Original source content drifted.");
        Need(SourceHash(r.Manifest.SourceSnapshotPath, r.Manifest.SourceSnapshotIsDirectory) == r.Manifest.SourceSnapshotSha256, "Retained source snapshot bytes changed.");
        Need(SourceHash(r.Manifest.OriginalSourcePath, r.Manifest.OriginalSourceIsDirectory) == r.Manifest.SourceSnapshotSha256, "Original source bytes changed.");
        Need(ProjectFileHash(r.Manifest.ProjectPath) == Text(original, "projectFileSha256"), "Saved original project changed after fresh native verification.");
        Need(ProjectFileHash(recoveredPath) == Text(reopened, "projectFileSha256"), "Saved recovered project changed after fresh native verification.");
    }

    // Narrow recovery-only release: the original, recovered and fresh-reopened project must all equal the baseline.
    // A dispatch marker without an exact terminal native receipt remains ambiguous even if Retrieve succeeded.
    public static void ValidateRecovery(string root, ComponentWindowReservation r, EngineeringArtifactReference completion)
    {
        var final = Read(root, completion);
        Need(Text(final, "manifestSha256") == r.ManifestSha256 && final["childReceipts"] is JsonArray list && list.Count == r.Children.Count,
            "Recovery reconciliation does not bind every retained outcome.");
        var children = new Dictionary<string, JsonObject>();
        for (int i = 0; i < r.Children.Count; i++)
        {
            var c = r.Children[i]; Need(final["childReceipts"]![i]?.GetValue<string>() == c.Response?.Sha256, "Recovery reconciliation replaced a child receipt.");
            if (c.State is "returned" or "reconciled") { ValidateChild(root, r with { Children = r.Children.Take(i + 1).ToArray() }, c.Response!); children[c.Step] = Read(root, c.Response!); }
            else Need(c.State == "outcomeUnknown" && c.Step is "applyLibrary" or "verifyImport" or "prepareGraphical" or "applyGraphical" or "verifyInstallation", "Unresolved observation/recovery cannot release ownership.");
        }
        JsonObject Observation(string step) => Object(Object(children[step], "componentWindow"), "observation");
        var before = Observation("start"); var restored = Observation("recover"); var reopened = Observation("observeRestored"); var original = Observation("observeRecoveryOriginal");
        Need(Text(original, "projectPath") == r.Manifest.ProjectPath && Text(before, "projectPath") == r.Manifest.ProjectPath &&
            Text(reopened, "projectPath") == Text(restored, "projectPath") && IsUnder(Path.Combine(r.Manifest.Workspace, "recovery"), Text(restored, "projectPath")), "Recovery-only exact project identities differ.");
        Need(Text(before, "nativeBirthUtc") != Text(original, "nativeBirthUtc") && Text(restored, "nativeBirthUtc") != Text(reopened, "nativeBirthUtc") &&
            Text(before, "sessionEpoch") != Text(original, "sessionEpoch") && Text(restored, "sessionEpoch") != Text(reopened, "sessionEpoch"), "Recovery-only fresh native lifecycles are absent.");
        Compare(before, original); Compare(before, restored); Compare(before, reopened); CompareVersions(before, original); CompareVersions(before, restored); CompareVersions(before, reopened);
        var recovery = children["recover"]; var coordinator = Object(recovery, "coordinatorBeforeRecovery");
        var statuses = coordinator["innerRecoveryStatuses"] as JsonArray;
        Need(statuses is { Count: > 0 and <= 2 }, "Actual pre-recovery inner native journals are required.");
        int expected = coordinator["graphical"]?["prepared"] is JsonObject ? 2 : 1; Need(statuses!.Count == expected, "An actual prepared inner step is missing from recovery reconciliation.");
        for (int i = 0; i < statuses.Count; i++)
        {
            string name = i == 0 ? "library" : "graphical"; var prepared = Object(Object(coordinator, name), "prepared"); var status = statuses[i]!.AsObject(); var journal = status["journal"] as JsonObject ?? status;
            Need(Text(journal, "changeId") == Text(prepared, "changeId") && Text(journal, "previewHash") == Text(prepared, "previewHash"), "Recovery-only journal identity changed.");
            if (i == 0)
            {
                Need(journal["state"]?.GetValue<string>() is "prepared" or "recovery_required" or "failed" &&
                    !(journal["events"] as JsonArray ?? new JsonArray()).Any(e => e?["stage"]?.GetValue<string>() == "native_mutation" && e?["state"]?.GetValue<string>() == "dispatching"),
                    "An actual first native mutation dispatch remains ambiguous or lies outside baseline-preserved recovery reconciliation.");
            }
            else Need(Text(journal, "state") == "prepared" && journal["idempotencyKey"] is null, "An actual typed mutation dispatch remains ambiguous.");
        }
        var resource = recovery["resources"]![0]!.AsObject(); var checkpoint = Object(recovery, "firstCheckpointEvidence");
        Need(Text(resource, "archiveSha256") == Text(checkpoint, "sha256") && resource["retrieved"]?.GetValue<bool>() == true && Text(Object(resource, "project"), "path") == Text(restored, "projectPath"), "Exact recovered checkpoint receipt differs.");
        Need(final["sourceSnapshotSha256"]?.GetValue<string>() == r.Manifest.SourceSnapshotSha256 && SourceHash(r.Manifest.SourceSnapshotPath, r.Manifest.SourceSnapshotIsDirectory) == r.Manifest.SourceSnapshotSha256 &&
            SourceHash(r.Manifest.OriginalSourcePath, r.Manifest.OriginalSourceIsDirectory) == r.Manifest.SourceSnapshotSha256, "Recovery-only source resources changed.");
        Need(ProjectFileHash(r.Manifest.ProjectPath) == Text(original, "projectFileSha256") && ProjectFileHash(Text(reopened, "projectPath")) == Text(reopened, "projectFileSha256"), "Saved original/recovered files changed after current native observation.");
    }

    private static void ValidateSnapshot(ComponentWindowManifest m, JsonObject snapshot)
    {
        Need(snapshot["scopeComplete"]?.GetValue<bool>() == true && snapshot["isModified"]?.GetValue<bool>() == false && Text(snapshot, "plcPath") == m.PlcPath,
            "Saved, complete native scope is required; empty/partial controls cannot qualify.");
        EngineeringIdentityGuard.Sha256(Text(snapshot, "projectFileSha256"), "projectFileSha256");
        Need(snapshot["processId"]?.GetValue<int>() > 0 && DateTime.TryParse(Text(snapshot, "nativeBirthUtc"), out _) && !string.IsNullOrWhiteSpace(Text(snapshot, "sessionEpoch")), "Exact native observation process identity is required.");
        var objects = Objects(snapshot); Need(objects.Count is > 0 and <= 2000, "A populated complete native peer inventory is required.");
        var inventory = Object(snapshot, "inventory");
        Need(inventory["completeness"]?["scopeComplete"]?.GetValue<bool>() == true && inventory["completeness"]?["truncated"]?.GetValue<bool>() == false &&
            inventory["technologyObjects"] is JsonArray { Count: 0 } && inventory["softwareScopes"] is JsonArray { Count: 1 }, "Unsupported/incomplete PLC scope cannot pass this profile.");
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (collection, kind) in new[] { ("blocks", "block"), ("types", "type"), ("tagTables", "tagTable") })
            foreach (var row in (JsonArray)inventory[collection]!) Need(expected.Add(kind + ":" + Text(row!.AsObject(), "path")), "Duplicate native peer identity.");
        Need(expected.SetEquals(objects.Select(x => x.Key)), "Snapshot is missing a native inventory peer.");
        foreach (var pair in objects)
        {
            var row = pair.Value!.AsObject(); Need(pair.Key == Text(row, "kind") + ":" + Text(row, "path"), "Peer identity changed.");
            var artifact = Object(row, "artifact"); Need(IsUnder(Path.Combine(m.Workspace, "captures"), Text(artifact, "path")), "Native readback escaped the dedicated capture subtree.");
            VerifyNativeArtifact(artifact);
        }
        var metadata = Object(snapshot, "peerMetadata");
        Need(metadata["hardware"] is JsonArray { Count: > 0 and <= 10000 } && metadata["softwareOwners"] is JsonArray { Count: 1 } &&
            metadata["access"] is JsonArray { Count: > 0 and <= 10000 } && metadata["watchGroups"] is JsonObject { Count: > 0 and <= 1000 },
            "Complete bounded hardware, software-owner, watch-group and access preservation evidence is required.");
        var hardwarePaths = ((JsonArray)metadata["hardware"]!).Select(h => Text(h!.AsObject(), "path")).ToArray();
        var ownerPaths = ((JsonArray)metadata["access"]!).Select(h => Text(h!.AsObject(), "owner")).ToArray();
        Need(hardwarePaths.Distinct(StringComparer.Ordinal).Count() == hardwarePaths.Length && ownerPaths.Distinct(StringComparer.Ordinal).Count() == ownerPaths.Length &&
            hardwarePaths.ToHashSet(StringComparer.Ordinal).SetEquals(ownerPaths) && hardwarePaths.Contains(Text(metadata["softwareOwners"]![0]!.AsObject(), "owner")),
            "Hardware/software/access owner identities are missing or ambiguous.");
        var languages = Object(metadata, "languages");
        Need(languages["active"] is JsonArray { Count: > 0 } active && active.Any(l => l?.GetValue<string>() == Text(languages, "reference")) &&
            active.Any(l => l?.GetValue<string>() == Text(languages, "editing")), "Native active/reference/editing language evidence is incomplete.");
        var tables = Object(snapshot, "watchForceTables");
        Need(tables.Count <= 1000 && metadata["watchTableInventory"] is JsonArray list && list.Count == tables.Count &&
            list.Select(t => t!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(tables.Select(t => t.Key)), "A watch/force peer document is missing from its complete inventory.");
        foreach (var pair in tables)
        {
            var row = pair.Value!.AsObject();
            Need(Text(row, "kind") is "watch" or "force" && pair.Key == Text(row, "kind") + ":" + Text(row, "path") &&
                Object(metadata, "watchGroups").ContainsKey(Text(row, "group")), "Watch/force peer identity changed.");
            var artifact = Object(row, "artifact"); Need(IsUnder(Path.Combine(m.Workspace, "captures"), Text(artifact, "path")), "Watch/force native readback escaped the capture subtree."); VerifyNativeArtifact(artifact);
        }
        Need(snapshot["componentVersions"] is JsonArray versions && versions.Count is > 0 and <= 1000, "Exact component dependency presence/absence inventory is required.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in (JsonArray)snapshot["componentVersions"]!)
        {
            var version = item!.AsObject(); Need(identities.Add(Text(version, "typeGuid") + ":" + Text(version, "versionGuid")), "Duplicate native dependency version.");
            if (version["exists"]?.GetValue<bool>() == true)
            {
                var artifact = Object(version, "artifact"); Need(IsUnder(Path.Combine(m.Workspace, "captures"), Text(artifact, "path")), "Native library-version readback escaped its capture subtree."); VerifyNativeArtifact(artifact);
                Need(version["objectPaths"] is JsonArray { Count: > 0 } paths && paths.Select(p => p!.GetValue<string>()).Distinct().Count() == paths.Count &&
                    paths.All(p => objects.Any(o => o.Value?["path"]?.GetValue<string>() == p!.GetValue<string>())), "Native dependency uses are missing, duplicated or outside the complete scope.");
            }
            else Need(version["exists"]?.GetValue<bool>() == false, "Dependency absence must be explicit native evidence.");
        }
    }
    private static void ValidateCheckpoint(ComponentWindowManifest manifest, JsonObject record, JsonObject journal, bool applied)
    {
        var prepared = Object(Object(record, "library"), "prepared");
        Need(Text(journal, "changeId") == Text(prepared, "changeId") && Text(journal, "previewHash") == Text(prepared, "previewHash"), "First checkpoint journal identity differs.");
        var events = journal["events"] as JsonArray ?? new JsonArray();
        int At(string stage, string state) => events.Select((e, i) => (e, i)).Where(p => p.e?["stage"]?.GetValue<string>() == stage && p.e?["state"]?.GetValue<string>() == state).Select(p => p.i).DefaultIfEmpty(-1).First();
        int archive = At("checkpoint_project_archive", "completed"), mutation = At("native_mutation", "dispatching"), compile = At("compile", "completed"), save = At("save_project", "completed");
        Need(archive >= 0 && (mutation < 0 || mutation > archive) && (!applied || mutation > archive && compile > mutation && save > compile), "First checkpoint must precede native mutation; applied receipt requires subsequent compilation and save.");
        var checkpoints = (journal["checkpoints"] as JsonArray ?? new JsonArray()).Where(c => c?["kind"]?.GetValue<string>() == "project" && c?["originalPath"]?.GetValue<string>() == manifest.ProjectPath).ToArray();
        Need(checkpoints.Length == 1, "Exactly one initial project checkpoint is required.");
        var checkpoint = checkpoints[0]!.DeepClone().AsObject(); checkpoint["changeId"] = Text(prepared, "changeId"); checkpoint["previewHash"] = Text(prepared, "previewHash");
        Need(JsonNode.DeepEquals(checkpoint, record["initialCheckpoint"]) && IsUnder(Path.Combine(manifest.Workspace, "changes", "change-" + Text(prepared, "changeId")), Text(checkpoint, "archivePath")), "First checkpoint metadata or ancestry changed.");
        VerifyNativeArtifact(new JsonObject { ["path"] = Text(checkpoint, "archivePath"), ["sha256"] = Text(checkpoint, "sha256") });
    }
    private static void Compare(JsonObject expected, JsonObject actual)
    {
        var a = Objects(expected); var b = Objects(actual);
        Need(a.Select(x => x.Key).ToHashSet(StringComparer.Ordinal).SetEquals(b.Select(x => x.Key)), "Fresh native peer identities differ.");
        foreach (var pair in a) CompareObject(pair.Value!.AsObject(), b[pair.Key]!.AsObject());
        CompareMetadata(expected, actual);
    }
    private static void CompareMetadata(JsonObject expected, JsonObject actual)
    {
        Need(JsonNode.DeepEquals(Object(expected, "peerMetadata"), Object(actual, "peerMetadata")), "Configured hardware, languages, software owners or watch/force groups/access changed.");
        var a = Object(expected, "watchForceTables"); var b = Object(actual, "watchForceTables");
        Need(a.Select(x => x.Key).ToHashSet(StringComparer.Ordinal).SetEquals(b.Select(x => x.Key)), "Watch/force native peer identities differ.");
        foreach (var pair in a)
        {
            var left = pair.Value!.DeepClone().AsObject(); var right = b[pair.Key]!.DeepClone().AsObject();
            CompareObject(left, right); left.Remove("artifact"); right.Remove("artifact");
            Need(JsonNode.DeepEquals(left, right), "Watch/force peer metadata changed.");
        }
    }
    private static void CompareVersions(JsonObject expected, JsonObject actual)
    {
        var a = expected["componentVersions"] as JsonArray ?? throw new BridgeException("ComponentAcceptanceIncomplete", "Exact native dependency versions are absent.");
        var b = actual["componentVersions"] as JsonArray ?? throw new BridgeException("ComponentAcceptanceIncomplete", "Exact fresh native dependency versions are absent.");
        Need(a.Count == b.Count, "Fresh native dependency inventory differs.");
        for (int i = 0; i < a.Count; i++)
        {
            var left = a[i]!.DeepClone().AsObject(); var right = b[i]!.DeepClone().AsObject();
            if (left["exists"]?.GetValue<bool>() == true) CompareArtifact(Object(left, "artifact"), Object(right, "artifact"));
            left.Remove("artifact"); right.Remove("artifact"); Need(JsonNode.DeepEquals(left, right), "Exact dependency native metadata/use links differ after fresh reopen.");
        }
    }
    private static void ValidateCompile(JsonObject? compile)
    {
        bool Messages(JsonArray? messages, int depth) => depth <= 64 && messages is not null && messages.All(m => m is JsonObject row &&
            row["state"]?.GetValue<string>() is "Success" or "Information" or "Warning" && (!row.ContainsKey("children") || Messages(row["children"] as JsonArray, depth + 1)));
        Need(compile is not null && compile["errorCount"]?.ToJsonString() == "0" && compile["error"] is null &&
            compile["targets"] is JsonArray { Count: > 0 } targets && targets.All(t => t is JsonObject target &&
                !string.IsNullOrWhiteSpace(target["target"]?.GetValue<string>()) && target["errorCount"]?.ToJsonString() == "0" && target["error"] is null &&
                target["state"]?.GetValue<string>() is "Success" or "Information" or "Warning" && target["messagesTruncated"]?.GetValue<bool>() == false && Messages(target["messages"] as JsonArray, 0)),
            "Actual inner compile evidence is erroneous, absent or truncated.");
    }
    private static string SourceHash(string path, bool directory)
    {
        if (!directory) return PackageStorage.FileHash(path);
        PackageStorage.EnsureNoLinks(path); var rows = new List<JsonObject>(); long bytes = 0; var stack = new Stack<string>(); stack.Push(path);
        while (stack.TryPop(out var current))
        {
            PackageStorage.EnsureNoLinks(current);
            foreach (string child in Directory.GetDirectories(current)) stack.Push(child);
            foreach (string file in Directory.GetFiles(current))
            {
                _ = EngineeringResourceKey.ForPath("workspace", file); bytes += new FileInfo(file).Length;
                Need(rows.Count < 20000 && bytes <= 4L * 1024 * 1024 * 1024, "Source directory scope is excessive.");
                rows.Add(new JsonObject { ["path"] = Path.GetRelativePath(path, file).Replace('\\', '/'), ["sha256"] = PackageStorage.FileHash(file) });
            }
        }
        return ComponentQualificationWindowStore.NativeHash(new JsonArray(rows.OrderBy(r => r["path"]!.GetValue<string>(), StringComparer.Ordinal).Select(r => (JsonNode)r).ToArray()));
    }
    private static void CompareObject(JsonObject a, JsonObject b)
    { Need(Text(a, "kind") == Text(b, "kind") && Text(a, "path") == Text(b, "path"), "Native peer identity differs."); CompareArtifact(Object(a, "artifact"), Object(b, "artifact")); }
    private static void CompareArtifact(JsonObject a, JsonObject b)
    { VerifyNativeArtifact(a); VerifyNativeArtifact(b); Need(XNode.DeepEquals(Xml(Text(a, "path")), Xml(Text(b, "path"))), "Unchanged native XML peer or exact reviewed output differs."); }
    private static void VerifyNativeArtifact(JsonObject a)
    {
        string path = Text(a, "path"), hash = Text(a, "sha256"); _ = EngineeringResourceKey.ForPath("workspace", path);
        EngineeringIdentityGuard.Sha256(hash, "nativeArtifactSha256"); Need(File.Exists(path) && new FileInfo(path).Length <= 32 * 1024 * 1024 && PackageStorage.FileHash(path) == hash, "Native artifact is absent, changed, or oversized.");
    }
    private static XDocument Xml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace); Need(document.Root?.Name == "Document", "Expected native Document envelope.");
        XElement Normalize(XElement source, bool inherited)
        {
            bool preserve = source.Attribute(XNamespace.Xml + "space")?.Value == "preserve" || source.Attribute(XNamespace.Xml + "space") is null && inherited;
            var result = new XElement(source.Name, source.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a.Name, a.Value)));
            foreach (string prefix in source.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Value).Concat(source.Nodes().OfType<XText>().Select(t => t.Value))
                .SelectMany(v => Regex.Matches(v, @"(?<![\p{L}\p{N}_])([\p{L}_][\p{L}\p{N}_.-]*):[\p{L}_][\p{L}\p{N}_.-]*").Select(match => match.Groups[1].Value)).Distinct().Order())
                if (prefix != "xml" && source.GetNamespaceOfPrefix(prefix) is { } binding) result.Add(new XAttribute(XNamespace.Xmlns + prefix, binding.NamespaceName));
            bool elements = source.HasElements, mixed = source.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
            foreach (var node in source.Nodes())
                if (node is XElement element) result.Add(Normalize(element, preserve));
                else if (node is XText text)
                {
                    if (elements && !mixed && !preserve && node is not XCData && string.IsNullOrWhiteSpace(text.Value)) continue;
                    if (result.LastNode is XText previous) previous.Value += text.Value; else result.Add(new XText(text.Value));
                }
                else if (node is XComment comment) result.Add(new XComment(comment.Value));
                else if (node is XProcessingInstruction instruction) result.Add(new XProcessingInstruction(instruction.Target, instruction.Data));
            return result;
        }
        var output = new XDocument();
        foreach (var node in document.Nodes())
            if (node is XElement element) { var normalized = Normalize(element, false); normalized.Elements("DocumentInfo").Remove(); output.Add(normalized); }
            else if (node is XComment comment) output.Add(new XComment(comment.Value));
            else if (node is XProcessingInstruction instruction) output.Add(new XProcessingInstruction(instruction.Target, instruction.Data));
        return output;
    }
    internal static JsonObject Read(string root, EngineeringArtifactReference a)
    { ComponentQualificationWindowStore.VerifyArtifact(root, a); return JsonNode.Parse(File.ReadAllText(ComponentQualificationWindowStore.EvidencePath(root, a)))!.AsObject(); }
    private static JsonObject Objects(JsonObject snapshot) => Object(snapshot, "objects");
    private static bool IsUnder(string parent, string path) => EngineeringResourceKey.ForPath("workspace", path).StartsWith(EngineeringResourceKey.ForPath("workspace", parent) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static JsonObject Object(JsonObject value, string name) => value[name] as JsonObject ?? throw new BridgeException("ComponentAcceptanceIncomplete", "Required evidence object absent: " + name);
    private static string Text(JsonObject value, string name) => value[name]?.GetValue<string>() ?? throw new BridgeException("ComponentAcceptanceIncomplete", "Required evidence field absent: " + name);
    // TIA may keep an open project file with write access; a shared read of the saved
    // project is still exact because every caller compares against a fresh native observation.
    private static string ProjectFileHash(string path)
    {
        PackageStorage.EnsureNoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file));
    }
    private static void Need(bool condition, string message) { if (!condition) throw new BridgeException("ComponentAcceptanceIncomplete", message); }
}
