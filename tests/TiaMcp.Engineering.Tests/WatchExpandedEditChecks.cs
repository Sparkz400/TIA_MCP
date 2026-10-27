using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using TiaMcp.Core;
using TiaMcp.Core.WatchTables;

internal static class WatchExpandedEditChecks
{
    internal static async Task Run()
    {
        string? repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml"))) repo = Path.GetDirectoryName(repo);
        if (repo is null) throw new Exception("Genuine owned watch fixture is required.");
        byte[] genuine = File.ReadAllBytes(Path.Combine(repo, "fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml"));
        var seed = new WatchTableDocument(genuine); seed.Duplicate(0, 1); seed.Duplicate(0, 2); seed.SetField(1, "ModifyValue", "TRUE");
        seed.SetComment(2, "de-DE", "Separate row");
        int passed = 0;
        foreach (var edit in new JsonObject[] {
            new() { ["editKind"] = "comment", ["ordinal"] = 0, ["culture"] = "de-DE", ["text"] = "Änderung {0}\nΩ" },
            new() { ["editKind"] = "comment", ["ordinal"] = 0, ["culture"] = "en-GB", ["text"] = "Change {0}\nΩ" },
            new() { ["editKind"] = "comment", ["ordinal"] = 0, ["culture"] = "nl-NL", ["text"] = "Wijziging {0}\nΩ" },
            new() { ["editKind"] = "clone", ["ordinal"] = 0, ["insertAt"] = 3 },
            new() { ["editKind"] = "delete", ["ordinal"] = 1 },
            new() { ["editKind"] = "reorder", ["order"] = new JsonArray(2, 0, 1) },
            new() { ["editKind"] = "delete", ["ordinal"] = 1, ["reindexDocumentIds"] = true },
            new() { ["editKind"] = "reorder", ["order"] = new JsonArray(2, 0, 1), ["reindexDocumentIds"] = true }
        })
        {
            string root = Path.Combine(Path.GetTempPath(), "watch-expanded-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root, seed.ToBytes());
            var guarded = new WatchTableDispatcher(native, root);
            edit["tableName"] = "MilestoneWatch"; edit["idempotencyKey"] = "review";
            try { await guarded.CallAsync("watch_prepare", edit); throw new Exception("Unqualified production profile was accepted."); }
            catch (BridgeException e) when (e.Code == "UnsupportedProfile") { passed++; }
            var dispatcher = new WatchTableDispatcher(native, root, allowUnqualifiedDocumentEdits: true);
            var plan = JsonNode.Parse((await dispatcher.CallAsync("watch_prepare", edit)).GetRawText())!;
            if (edit["reindexDocumentIds"]?.GetValue<bool>() == true &&
                ((string?)plan["review"]?["edit"]?["documentIdReindex"]?["profile"] != "v21-watch-reference-free-preorder-1" ||
                 plan["review"]?["edit"]?["documentIdReindex"]?["mapping"] is not JsonObject))
                throw new Exception("Explicit reviewed ID transformation is absent from the immutable plan.");
            string id = (string)plan["operation"]!["id"]!;
            await dispatcher.CallAsync("watch_apply", new { operationId = id, reviewedPlanSha256 = (string)plan["operation"]!["planSha256"]! });
            native.NewInstance(); await dispatcher.CallAsync("watch_verify_reopen", new { operationId = id });
            await dispatcher.CallAsync("watch_recover", new { operationId = id });
            native.NewInstance(); await dispatcher.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" });
            native.SelectOriginal(); var final = JsonNode.Parse((await dispatcher.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "original" })).GetRawText())!;
            if ((string)final["operation"]!["state"]! != "reconciled" || native.ApplyCount != 1 || native.RestoreCount != 1) throw new Exception("Expanded edit did not reconcile exactly once.");
            passed++;
        }
        // Force documents reuse the same reviewed prepare/apply/verify/recover route; only targetKind differs.
        var force = new WatchTableDocument(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(genuine).Replace("PlcWatchTable", "PlcForceTable")
            .Replace("<ModifyTrigger>Permanent</ModifyTrigger>", "<Address>%I0.0:P</Address>").Replace("ModifyValue", "ForceValue").Replace("MilestoneWatch", "Force table")));
        force.Duplicate(0, 1); force.Duplicate(0, 2); force.SetField(1, "ForceValue", "TRUE"); force.SetComment(2, "de-DE", "Separate row");
        foreach (var edit in new JsonObject[] {
            new() { ["editKind"] = "field", ["ordinal"] = 0, ["field"] = "ForceValue", ["value"] = "TRUE" },
            new() { ["editKind"] = "field", ["ordinal"] = 1, ["field"] = "Address", ["value"] = "%Q0.0:P" },
            new() { ["editKind"] = "field", ["ordinal"] = 2, ["field"] = "DisplayFormat", ["value"] = "Hex" },
            new() { ["editKind"] = "comment", ["ordinal"] = 0, ["culture"] = "en-GB", ["text"] = "Valve {0}" },
            new() { ["editKind"] = "clone", ["ordinal"] = 0, ["insertAt"] = 3 },
            new() { ["editKind"] = "delete", ["ordinal"] = 1 },
            new() { ["editKind"] = "reorder", ["order"] = new JsonArray(2, 0, 1) } })
        {
            string root = Path.Combine(Path.GetTempPath(), "force-expanded-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root, force.ToBytes());
            edit["tableName"] = "Force table"; edit["targetKind"] = "forceTable"; edit["idempotencyKey"] = "force-review";
            try { await new WatchTableDispatcher(native, root).CallAsync("watch_prepare", edit); throw new Exception("Unqualified force profile was accepted."); }
            catch (BridgeException e) when (e.Code == "UnsupportedProfile") { passed++; }
            var dispatcher = new WatchTableDispatcher(native, root, allowUnqualifiedDocumentEdits: true);
            var plan = JsonNode.Parse((await dispatcher.CallAsync("watch_prepare", edit)).GetRawText())!;
            if ((string?)plan["review"]?["selector"]?["targetKind"] != "forceTable") throw new Exception("Force target is absent from the immutable review selector.");
            string id = (string)plan["operation"]!["id"]!;
            await dispatcher.CallAsync("watch_apply", new { operationId = id, reviewedPlanSha256 = (string)plan["operation"]!["planSha256"]! });
            native.NewInstance(); await dispatcher.CallAsync("watch_verify_reopen", new { operationId = id });
            await dispatcher.CallAsync("watch_recover", new { operationId = id });
            native.NewInstance(); await dispatcher.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" });
            native.SelectOriginal(); var final = JsonNode.Parse((await dispatcher.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "original" })).GetRawText())!;
            if ((string)final["operation"]!["state"]! != "reconciled" || native.ApplyCount != 1 || native.RestoreCount != 1) throw new Exception("Force edit did not reconcile exactly once.");
            passed++;
        }
        foreach (var (code, bytes, edit, selected) in new (string, byte[], JsonObject, bool)[] {
            ("ForceSelectionRefused", force.ToBytes(), new() { ["editKind"] = "field", ["field"] = "ForceValue", ["value"] = "TRUE" }, true),
            ("InvalidDataException", force.ToBytes(), new() { ["editKind"] = "field", ["field"] = "ForceIntention", ["value"] = "true" }, false),
            ("InvalidDataException", force.ToBytes(), new() { ["modifyValue"] = "TRUE" }, false),
            ("InvalidRequest", force.ToBytes(), new() { ["editKind"] = "field", ["field"] = "ForceValue", ["value"] = "TRUE", ["targetKind"] = "watchTableGroup" }, false),
            ("IdentityMismatch", genuine, new() { ["editKind"] = "field", ["field"] = "ModifyValue", ["value"] = "TRUE" }, false) })
        {
            string root = Path.Combine(Path.GetTempPath(), "force-refusal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root, bytes);
            if (selected) native.TransformSnapshot = snapshot => { snapshot["target"]!["rows"]![0]!["forceIntention"] = true; return snapshot; };
            edit["tableName"] = "Force table"; edit["ordinal"] = 0; edit["idempotencyKey"] = "force-refusal"; edit["targetKind"] ??= "forceTable";
            try { await new WatchTableDispatcher(native, root, allowUnqualifiedDocumentEdits: true).CallAsync("watch_prepare", edit); throw new Exception("Unsafe force edit was accepted: " + code); }
            catch (Exception e) when (e is BridgeException b ? b.Code == code : e.GetType().Name == code) { passed++; }
            if (native.ApplyCount != 0 || native.CheckpointCount != 0 || Directory.GetFiles(root, "reviewed.xml", SearchOption.AllDirectories).Length != 0)
                throw new Exception("Refused force edit published a review or reached native effects.");
        }
        foreach (var (field, value, edit) in new[] {
            ("DisplayFormat", "Bool", new JsonObject { ["modifyValue"] = "TRUE" }),
            ("Culture", "de-DE", new JsonObject { ["editKind"] = "comment", ["culture"] = "de-DE", ["text"] = "changed" }) })
        foreach (string markup in new[] { "<opaque>" + value + "</opaque>", value + "<!-- retain -->" })
        {
            string root = Path.Combine(Path.GetTempPath(), "watch-opaque-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root, genuine);
            byte[] malformed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(genuine).Replace("<" + field + ">" + value + "</" + field + ">", "<" + field + ">" + markup + "</" + field + ">"));
            if (malformed.SequenceEqual(genuine)) throw new Exception("Adversarial fixture must actually change the capture.");
            native.TransformSnapshot = snapshot => {
                var target = snapshot["target"]!;
                File.WriteAllBytes(target["artifact"]!["path"]!.GetValue<string>(), malformed);
                target["artifact"]!["sha256"] = WatchTableDocument.Hash(malformed);
                target["semanticSha256"] = WatchTableDocument.SemanticHash(malformed);
                snapshot["observation"]!["reference"]!["contentSha256"] = target["semanticSha256"]!.DeepClone();
                return snapshot;
            };
            var dispatcher = new WatchTableDispatcher(native, root, allowUnqualifiedDocumentEdits: true);
            edit["tableName"] = "MilestoneWatch"; edit["idempotencyKey"] = "opaque-refusal";
            try { await dispatcher.CallAsync("watch_prepare", edit); throw new Exception("Opaque capture was accepted as a reviewed scalar."); }
            catch (InvalidDataException) { passed++; }
            if (native.ApplyCount != 0 || native.CheckpointCount != 0 || Directory.GetFiles(root, "reviewed.xml", SearchOption.AllDirectories).Length != 0)
                throw new Exception("Opaque input must refuse before review publication or native side effects.");
        }
        foreach (string annotation in new[] { "<!-- retained -->", "<?vendor retained?>" })
        {
            string root = Path.Combine(Path.GetTempPath(), "watch-document-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root, genuine); bool changed = false;
            native.TransformSnapshot = snapshot => {
                var target = snapshot["target"]!; string path = target["artifact"]!["path"]!.GetValue<string>();
                byte[] bytes = File.ReadAllBytes(path).Concat(Encoding.UTF8.GetBytes(changed ? annotation.Replace("retained", "changed") : annotation)).ToArray();
                File.WriteAllBytes(path, bytes); target["artifact"]!["sha256"] = WatchTableDocument.Hash(bytes);
                target["semanticSha256"] = WatchTableDocument.SemanticHash(bytes);
                snapshot["observation"]!["reference"]!["contentSha256"] = target["semanticSha256"]!.DeepClone(); return snapshot;
            };
            var dispatcher = new WatchTableDispatcher(native, root);
            var plan = JsonNode.Parse((await dispatcher.CallAsync("watch_prepare", new { tableName = "MilestoneWatch", modifyValue = "TRUE", idempotencyKey = "annotation" })).GetRawText())!;
            changed = true;
            try { await dispatcher.CallAsync("watch_apply", new { operationId = plan["operation"]!["id"]!.GetValue<string>(), reviewedPlanSha256 = plan["operation"]!["planSha256"]!.GetValue<string>() }); throw new Exception("Changed document annotation bypassed freshness."); }
            catch (BridgeException error) when (error.Code == "StaleContent") { passed++; }
            if (native.CheckpointCount != 0 || native.ApplyCount != 0 || native.EndCount != 1) throw new Exception("Stale document metadata must refuse before side effects and release the lease.");
        }
        Console.WriteLine($"PASS Expanded watch dispatcher: {passed} checks with genuine seed XML and synthetic bridge; native profiles remain gated.");
    }
}
