using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;

// Deliberately synthetic, file-only evidence. This tests the verifier, not native qualification.
internal static class AcceptanceChecks
{
    internal static int Run(string root, ComponentWindowManifest m)
    {
        int checks = 0; string main = m.PlcPath + "/blocks/Main", fb = m.PlcPath + "/blocks/Component", caller = m.PlcPath + "/blocks/Caller", instance = m.PlcPath + "/blocks/Instance";
        string originalXml = "<?proof retained?><Document xmlns:q=\"urn:retained\"><DocumentInfo><Created>volatile</Created></DocumentInfo><!--unknown comment--><SW.Blocks.OB ID=\"1\"><Unknown typed=\"q:Native\"><Declaration Name=\"保持\"/><Text xml:space=\"preserve\"> deutsch Ω </Text></Unknown></SW.Blocks.OB></Document>";
        JsonObject Artifact(string path, string bytes)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, bytes, new UTF8Encoding(false)); return new() { ["path"] = path, ["sha256"] = Hash(bytes) }; }
        string changes = Path.Combine(m.Workspace, "changes"), libraryId = "lib-" + new string('c', 32), typedId = new string('d', 32);
        string libraryJournalPath = Path.Combine(changes, "change-" + libraryId, "journal.json"), typedJournalPath = Path.Combine(changes, "change-" + typedId, "journal.json");
        var archive = Artifact(Path.Combine(changes, "change-" + libraryId, "before.zap21"), "synthetic checkpoint");
        var checkpoint = new JsonObject { ["kind"] = "project", ["originalPath"] = m.ProjectPath, ["archivePath"] = archive["path"]!.DeepClone(), ["sha256"] = archive["sha256"]!.DeepClone() };
        var anchor = checkpoint.DeepClone().AsObject(); anchor["changeId"] = libraryId; anchor["previewHash"] = new string('c', 64);
        var libraryPrepared = new JsonObject { ["changeId"] = libraryId, ["previewHash"] = new string('c', 64), ["journalPath"] = libraryJournalPath };
        var typedPrepared = new JsonObject { ["changeId"] = typedId, ["previewHash"] = new string('d', 64), ["journalPath"] = typedJournalPath,
            ["preview"] = new JsonObject { ["typed"] = new JsonObject { ["nativeArtifact"] = Artifact(Path.Combine(changes, "caller.xml"), "<Document><Caller ID=\"3\"/></Document>") },
                ["effects"] = new JsonArray(new JsonObject { ["action"] = "createInstanceDB", ["path"] = instance, ["artifact"] = Artifact(Path.Combine(changes, "instance.xml"), "<Document><Instance ID=\"4\"/></Document>") }) } };
        JsonObject Compile() => new() { ["errorCount"] = 0, ["targets"] = new JsonArray(new JsonObject { ["target"] = m.PlcPath, ["errorCount"] = 0, ["state"] = "Success", ["messagesTruncated"] = false, ["messages"] = new JsonArray() }) };
        var libraryJournal = new JsonObject { ["changeId"] = libraryId, ["previewHash"] = new string('c', 64), ["state"] = "applied", ["idempotencyKeyHash"] = Hash(m.ComponentId + ":library"), ["compile"] = Compile(),
            ["checkpoints"] = new JsonArray(checkpoint.DeepClone()), ["events"] = new JsonArray(
                new JsonObject { ["stage"] = "checkpoint_project_archive", ["state"] = "completed" }, new JsonObject { ["stage"] = "native_mutation", ["state"] = "dispatching" },
                new JsonObject { ["stage"] = "compile", ["state"] = "completed" }, new JsonObject { ["stage"] = "save_project", ["state"] = "completed" }) };
        var typedJournal = new JsonObject { ["changeId"] = typedId, ["previewHash"] = new string('d', 64), ["state"] = "applied", ["idempotencyKey"] = m.ComponentId + ":graphical", ["idempotencyKeyHash"] = Hash(m.ComponentId + ":graphical"), ["compile"] = Compile(),
            ["typedQualification"] = new JsonObject { ["engineeringVerified"] = true, ["preservationVerified"] = true, ["saved"] = true } };
        Artifact(libraryJournalPath, libraryJournal.ToJsonString()); Artifact(typedJournalPath, typedJournal.ToJsonString()); File.WriteAllText(Path.Combine(changes, "library-key-" + Hash(m.ComponentId + ":library") + ".claim"), libraryId);
        JsonObject Applied(JsonObject journal) => new() { ["changeId"] = journal["changeId"]!.DeepClone(), ["previewHash"] = journal["previewHash"]!.DeepClone(), ["state"] = "applied", ["journal"] = journal.DeepClone() };
        JsonObject Dispatch(JsonObject prepared, string name) => new() { ["changeId"] = prepared["changeId"]!.DeepClone(), ["previewHash"] = prepared["previewHash"]!.DeepClone(), ["idempotencyKey"] = m.ComponentId + ":" + name, ["checkpointSave"] = true, ["compile"] = true, ["save"] = true };
        var versionPin = new JsonObject { ["typeGuid"] = "type-pin", ["versionGuid"] = "version-pin" };
        var installedRecord = new JsonObject { ["operationId"] = m.ComponentId, ["state"] = "installation_verified",
            ["contract"] = new JsonObject { ["planChangeId"] = m.PlanId, ["planPreviewHash"] = m.PlanPreviewHash, ["projectPath"] = m.ProjectPath,
                ["plan"] = new JsonObject { ["versions"] = new JsonArray(versionPin.DeepClone()), ["caller"] = new JsonObject { ["blockPath"] = caller, ["initiallyAbsent"] = true }, ["instanceDb"] = new JsonObject { ["path"] = instance } } },
            ["library"] = new JsonObject { ["prepared"] = libraryPrepared.DeepClone(), ["dispatch"] = Dispatch(libraryPrepared, "library"), ["result"] = Applied(libraryJournal) },
            ["graphical"] = new JsonObject { ["prepared"] = typedPrepared.DeepClone(), ["dispatch"] = Dispatch(typedPrepared, "graphical"), ["result"] = Applied(typedJournal) },
            ["initialCheckpoint"] = anchor.DeepClone(), ["installationEvidence"] = new JsonObject { ["versions"] = new JsonArray(new JsonObject { ["objectPaths"] = new JsonArray(fb) }) } };
        string recovered = Path.Combine(m.Workspace, "recovery", "restored", "restored.ap21"); Artifact(recovered, "synthetic project");
        JsonObject Snapshot(string phase, bool installed, int identity, string project)
        {
            var paths = new List<(string Path, string Xml)> { (main, originalXml) };
            if (installed) paths.AddRange([(fb, "<Document><FB ID=\"2\"/></Document>"), (caller, "<Document><Caller ID=\"3\"/></Document>"), (instance, "<Document><Instance ID=\"4\"/></Document>")]);
            var objects = new JsonObject(); var inventory = new JsonArray(); int index = 0;
            foreach (var p in paths) { inventory.Add(new JsonObject { ["path"] = p.Path }); objects["block:" + p.Path] = new JsonObject { ["kind"] = "block", ["path"] = p.Path, ["artifact"] = Artifact(Path.Combine(m.Workspace, "captures", phase, (++index) + ".xml"), p.Xml) }; }
            var version = versionPin.DeepClone().AsObject(); version["exists"] = installed;
            if (installed) { version["state"] = "Committed"; version["doNotUse"] = false; version["objectPaths"] = new JsonArray(fb); version["artifact"] = Artifact(Path.Combine(m.Workspace, "captures", phase, "version.xml"), "<Document><Version ID=\"5\"/></Document>"); }
            var metadata = new JsonObject { ["hardware"] = new JsonArray(new JsonObject { ["path"] = "device/CPU", ["name"] = "CPU", ["typeIdentifier"] = "synthetic", ["attributes"] = new JsonObject { ["testParameter"] = "preserved" } }),
                ["softwareOwners"] = new JsonArray(new JsonObject { ["owner"] = "device/CPU", ["type"] = "PlcSoftware" }), ["watchGroups"] = new JsonObject { ["root"] = "Watch and force tables" },
                ["watchTableInventory"] = new JsonArray("watch:root/Peer"), ["access"] = new JsonArray(new JsonObject { ["owner"] = "device/CPU", ["available"] = false, ["watch"] = null, ["force"] = null }),
                ["languages"] = new JsonObject { ["active"] = new JsonArray("de-DE", "en-US"), ["reference"] = "de-DE", ["editing"] = "en-US" } };
            var watchTables = new JsonObject { ["watch:root/Peer"] = new JsonObject { ["kind"] = "watch", ["path"] = "root/Peer", ["group"] = "root", ["name"] = "Peer",
                ["artifact"] = Artifact(Path.Combine(m.Workspace, "captures", phase, "watch.xml"), "<Document><WatchTable ID=\"8\"><Comment>保持</Comment></WatchTable></Document>") } };
            return new() { ["projectPath"] = project, ["plcPath"] = m.PlcPath, ["processId"] = identity, ["nativeBirthUtc"] = "2026-01-01T00:" + (identity - 999).ToString("D2") + ":00Z", ["sessionEpoch"] = identity == 999 ? m.InitialNativeSessionEpoch : "fresh-" + identity,
                ["scopeComplete"] = true, ["isModified"] = false, ["projectFileSha256"] = Hash("synthetic project"), ["componentVersions"] = new JsonArray(version), ["objects"] = objects,
                ["peerMetadata"] = metadata, ["watchForceTables"] = watchTables,
                ["inventory"] = new JsonObject { ["completeness"] = new JsonObject { ["scopeComplete"] = true, ["truncated"] = false }, ["softwareScopes"] = new JsonArray(new JsonObject()), ["technologyObjects"] = new JsonArray(), ["blocks"] = inventory, ["types"] = new JsonArray(), ["tagTables"] = new JsonArray() } };
        }
        var observations = new Dictionary<string, JsonObject> { ["start"] = Snapshot("before", false, 999, m.ProjectPath), ["verifyInstallation"] = Snapshot("after", true, 999, m.ProjectPath),
            ["observeOriginal"] = Snapshot("original", true, 1000, m.ProjectPath), ["recover"] = Snapshot("restored", false, 1001, recovered), ["observeRestored"] = Snapshot("restored-fresh", false, 1002, recovered) };
        string[] steps = ["start", "applyLibrary", "verifyImport", "prepareGraphical", "applyGraphical", "verifyInstallation", "observeOriginal", "recover", "observeRestored"];
        string[] states = ["library_prepared", "library_applied", "import_verified", "graphical_prepared", "graphical_applied", "installation_verified"];
        var originals = new List<JsonObject>();
        for (int i = 0; i < steps.Length; i++)
        {
            string step = steps[i]; var record = installedRecord.DeepClone().AsObject(); record["state"] = i < 6 ? states[i] : "installation_verified";
            if (i == 0) { record["library"]!.AsObject().Remove("dispatch"); record["library"]!.AsObject().Remove("result"); }
            if (i < 3) record.Remove("graphical"); else if (i == 3) { record["graphical"]!.AsObject().Remove("dispatch"); record["graphical"]!.AsObject().Remove("result"); }
            var row = new JsonObject { ["changeId"] = "lib-" + m.ComponentId, ["state"] = record["state"]!.DeepClone(), ["record"] = record,
                ["componentWindow"] = new JsonObject { ["manifestSha256"] = ComponentQualificationWindowStore.ManifestHash(m), ["step"] = step, ["processId"] = i < 6 ? 999 : 1000 + i - 6, ["sessionEpoch"] = i < 6 ? m.InitialNativeSessionEpoch : "fresh-" + i } };
            if (observations.TryGetValue(step, out var observation)) { row["componentWindow"]!["observation"] = observation.DeepClone(); row["componentWindow"]!["sessionEpoch"] = observation["sessionEpoch"]!.DeepClone(); }
            if (step == "recover")
            {
                row["state"] = "restored_to_new_locations"; row["firstCheckpointEvidence"] = anchor.DeepClone(); var coordinator = installedRecord.DeepClone().AsObject(); coordinator["firstInnerRecoveryStatus"] = Applied(libraryJournal); row["coordinatorBeforeRecovery"] = coordinator;
                row["resources"] = new JsonArray(new JsonObject { ["kind"] = "project", ["archiveSha256"] = checkpoint["sha256"]!.DeepClone(), ["retrieved"] = true, ["project"] = new JsonObject { ["path"] = recovered } });
            }
            originals.Add(row);
        }
        void Verify(List<JsonObject> rows, bool unknown = false)
        {
            var children = rows.Select((row, i) => new ComponentWindowChild(steps[i], new string('a', 64), unknown && i == 1 ? "outcomeUnknown" : "returned", Retain(root, "child-" + i + ".json", row), null)).ToArray();
            using var process = Process.GetCurrentProcess();
            var reservation = new ComponentWindowReservation(m.Id, 30, unknown ? "outcomeUnknown" : "active", m, ComponentQualificationWindowStore.ManifestHash(m), [], new string('a', 64), process.Id, process.StartTime.ToUniversalTime(), false, children, null, DateTimeOffset.UtcNow, null, "synthetic");
            var completion = Retain(root, "completion.json", new JsonObject { ["manifestSha256"] = reservation.ManifestSha256, ["sourceSnapshotSha256"] = m.SourceSnapshotSha256, ["childReceipts"] = new JsonArray(children.Select(c => JsonValue.Create(c.Response!.Sha256)).ToArray()) });
            ComponentWindowEvidence.Validate(root, reservation, completion);
        }
        List<JsonObject> Clone() => originals.Select(r => r.DeepClone().AsObject()).ToList();
        void Reject(Action<List<JsonObject>> mutation, string label)
        { var rows = Clone(); mutation(rows); try { Verify(rows); } catch (BridgeException) { checks++; return; } throw new Exception("Accepted corrupt component evidence: " + label); }
        Verify(Clone()); checks++;
        Reject(r => r[1]["record"]!["library"]!["result"]!["journal"]!["compile"]!["targets"]![0]!["state"] = "Error", "native Error with zero error count");
        Reject(r => r[1]["record"]!["library"]!["result"]!["journal"]!["events"]![0]!["stage"] = "after_mutation", "first checkpoint ordering");
        Reject(r => r[4]["record"]!["graphical"]!["result"]!["journal"]!["typedQualification"]!["saved"] = false, "typed persistence");
        Reject(r => r[1]["record"]!["library"]!["dispatch"]!["idempotencyKey"] = "external-review-key", "actual inner key");
        Reject(r => r[7]["firstCheckpointEvidence"]!["changeId"] = typedId, "later typed checkpoint substituted");
        Reject(r => r[6]["componentWindow"]!["observation"]!["nativeBirthUtc"] = observations["verifyInstallation"]["nativeBirthUtc"]!.DeepClone(), "stale original lifecycle");
        Reject(r => r[6]["componentWindow"]!["observation"]!["processId"] = 8877, "capture belongs to another native child");
        Reject(r => r[5]["componentWindow"]!["observation"]!["nativeBirthUtc"] = "2026-02-01T00:00:00Z", "initial lifetime birth changed");
        Reject(r => r[6]["componentWindow"]!["observation"]!["objects"]!.AsObject().Remove("block:" + main), "missing unchanged peer");
        Reject(r => r[6]["componentWindow"]!["observation"]!.AsObject().Remove("peerMetadata"), "missing hardware/language/access scope");
        Reject(r => r[6]["componentWindow"]!["observation"]!["peerMetadata"]!["access"] = new JsonArray(), "missing access owners");
        Reject(r => r[6]["componentWindow"]!["observation"]!["watchForceTables"] = new JsonObject(), "missing watch/force export");
        Reject(r => r[6]["componentWindow"]!["observation"]!["peerMetadata"]!["softwareOwners"]!.AsArray().Add(new JsonObject { ["owner"] = "another/HMI", ["type"] = "HmiTarget" }), "another unobserved software owner");
        foreach (string kind in new[] { "hardware", "languages", "access", "watch" })
            Reject(rows => { foreach (int i in new[] { 5, 6 }) { var observation = rows[i]["componentWindow"]!["observation"]!;
                if (kind == "hardware") observation["peerMetadata"]!["hardware"]![0]!["attributes"]!["testParameter"] = "changed";
                if (kind == "languages") observation["peerMetadata"]!["languages"]!["active"]!.AsArray().Add("fr-FR");
                if (kind == "access") observation["peerMetadata"]!["access"]![0]!["available"] = true;
                if (kind == "watch") observation["watchForceTables"]!["watch:root/Peer"]!["artifact"] = Artifact(Path.Combine(m.Workspace, "captures", "changed-watch.xml"), "<Document><WatchTable ID=\"9\"><Comment>保持</Comment></WatchTable></Document>");
            } }, "installed and reopened " + kind + " differ from initial peer state");
        Reject(r => r[8]["componentWindow"]!["observation"]!["componentVersions"]![0]!["exists"] = true, "dependency persists in restored project");
        Reject(r => r[0]["record"]!["library"]!["prepared"]!["journalPath"] = Path.Combine(root, "escaped.json"), "inner journal escapes parent");
        try { Verify(Clone(), true); throw new Exception("Unknown accepted"); } catch (BridgeException) { checks++; }
        foreach (var replacement in new[] { originalXml.Replace("ID=\"1\"", "ID=\"91\""), originalXml.Replace("unknown comment", "changed comment"), originalXml.Replace("urn:retained", "urn:changed"), originalXml.Replace("<?proof retained?>", "<?proof changed?>"), originalXml.Replace("保持", "lost declaration"), originalXml.Replace("deutsch Ω ", "deutsch Ω") })
        {
            Reject(rows => rows[6]["componentWindow"]!["observation"]!["objects"]!["block:" + main]!["artifact"] = Artifact(Path.Combine(m.Workspace, "captures", "tampered.xml"), replacement), "unknown XML/ID/comment/PI/QName/typed declaration preserved");
        }
        string source = File.ReadAllText(m.OriginalSourcePath); File.WriteAllText(m.OriginalSourcePath, "changed original");
        try { Reject(_ => { }, "source archive drift"); } finally { File.WriteAllText(m.OriginalSourcePath, source); }
        Verify(Clone()); checks++;
        var recoveryRows = new List<JsonObject> { originals[0].DeepClone().AsObject(), new JsonObject { ["nativeOutcome"] = "unknown" }, originals[7].DeepClone().AsObject(), originals[8].DeepClone().AsObject(), originals[8].DeepClone().AsObject() };
        var failedInner = libraryJournal.DeepClone().AsObject(); failedInner["state"] = "recovery_required"; failedInner["events"] = new JsonArray(new JsonObject { ["stage"] = "checkpoint_project_archive", ["state"] = "completed" });
        var recoveryCoordinator = recoveryRows[2]["coordinatorBeforeRecovery"]!.AsObject(); recoveryCoordinator.Remove("graphical"); recoveryCoordinator["firstInnerRecoveryStatus"] = new JsonObject { ["journal"] = failedInner.DeepClone() };
        recoveryCoordinator["innerRecoveryStatuses"] = new JsonArray(new JsonObject { ["journal"] = failedInner.DeepClone() });
        recoveryRows[4]["componentWindow"]!["step"] = "observeRecoveryOriginal"; recoveryRows[4]["componentWindow"]!["observation"] = Snapshot("recovery-original", false, 1003, m.ProjectPath);
        recoveryRows[4]["componentWindow"]!["processId"] = 1003; recoveryRows[4]["componentWindow"]!["sessionEpoch"] = "fresh-1003";
        string[] recoverySteps = ["start", "applyLibrary", "recover", "observeRestored", "observeRecoveryOriginal"];
        void VerifyRecovery(List<JsonObject> rows)
        {
            var children = rows.Select((row, i) => new ComponentWindowChild(recoverySteps[i], new string('a', 64), i == 1 ? "outcomeUnknown" : "returned", Retain(root, "recovery-child-" + i + ".json", row), null)).ToArray();
            using var process = Process.GetCurrentProcess();
            var reservation = new ComponentWindowReservation(m.Id, 30, "outcomeUnknown", m, ComponentQualificationWindowStore.ManifestHash(m), [], new string('a', 64), process.Id, process.StartTime.ToUniversalTime(), false, children, null, DateTimeOffset.UtcNow, null, "synthetic");
            var completion = Retain(root, "recovery-completion.json", new JsonObject { ["manifestSha256"] = reservation.ManifestSha256, ["sourceSnapshotSha256"] = m.SourceSnapshotSha256, ["childReceipts"] = new JsonArray(children.Select(c => JsonValue.Create(c.Response!.Sha256)).ToArray()) });
            ComponentWindowEvidence.ValidateRecovery(root, reservation, completion);
        }
        VerifyRecovery(recoveryRows); checks++;
        void RejectRecovery(Action<List<JsonObject>> mutation, string label)
        { var rows = recoveryRows.Select(r => r.DeepClone().AsObject()).ToList(); mutation(rows); try { VerifyRecovery(rows); } catch (BridgeException) { checks++; return; } throw new Exception("Accepted invalid recovery-only reconciliation: " + label); }
        RejectRecovery(rows => rows[2]["coordinatorBeforeRecovery"]!["innerRecoveryStatuses"]![0]!["journal"]!["events"]!.AsArray().Add(new JsonObject { ["stage"] = "native_mutation", ["state"] = "dispatching" }), "ambiguous actual inner dispatch remains reserved");
        RejectRecovery(rows => rows[4]["componentWindow"]!["observation"] = observations["observeOriginal"].DeepClone(), "changed original is not baseline-preserved recovery");
        RejectRecovery(rows => rows[2]["coordinatorBeforeRecovery"]!["innerRecoveryStatuses"] = new JsonArray(), "missing actual inner outcome");
        RejectRecovery(rows => rows[3]["componentWindow"]!["observation"]!["nativeBirthUtc"] = observations["recover"]["nativeBirthUtc"]!.DeepClone(), "recovery reopened in same native lifecycle");
        return checks;
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static EngineeringArtifactReference Retain(string root, string name, JsonObject value)
    { string json = value.ToJsonString(); File.WriteAllText(Path.Combine(root, name), json, new UTF8Encoding(false)); return new(name, Hash(json)); }
}
