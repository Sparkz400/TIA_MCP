using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class WatchSourceNegativeRecoveryChecks
{
    internal static async Task Run()
    {
        int passed = 0;
        var changes = new Dictionary<string, Action<JsonObject>> {
            ["same epoch"] = x => x["observation"]!["sessionEpoch"] = "epoch-1",
            ["same process"] = x => x["processId"] = 1,
            ["wrong worker"] = x => x["observation"]!["adapterSha256"] = new string('a', 64),
            ["wrong native profile"] = x => x["observation"]!["profileSha256"] = new string('a', 64),
            ["wrong hardware"] = x => x["observation"]!["hardwareIdentity"] = "other",
            ["changed source peer"] = x => x["peers"]!["block:/PLC/FB"] = new string('a', 64),
            ["missing probe"] = x => x["observation"]!["probes"]!.AsArray().RemoveAt(1),
            ["changed target"] = x => x["target"]!["exists"] = true,
            ["unsaved"] = x => x["projectModified"] = true,
            ["online"] = x => x["observation"]!["offline"] = false,
            ["wrong project"] = x => x["observation"]!["projectFilePath"] = Path.Combine(Path.GetTempPath(), "wrong.ap21")
        };
        foreach (var mutation in changes.Prepend(new KeyValuePair<string, Action<JsonObject>>("unchanged", _ => { })))
        {
            var bridge = new Reader(); var store = new EngineeringMutationAdmissionStore(bridge.Root);
            var admission = store.Acquire(new("watch_authoring_source_negative", new('b', 64), new('c', 64), "epoch-1", new('d', 64), 1,
                new('e', 64), [new("project", bridge.Project, "ownedWatchSourceNegative")]), new('f', 64));
            store.Unknown(admission.Id, admission.Revision);
            mutation.Value(bridge.Current);
            var dispatcher = new WatchTableDispatcher(bridge, bridge.Root, allowUnqualifiedDocumentEdits: true, allowComponentQualification: true);
            try
            {
                await dispatcher.CallAsync("qualification_watch_authoring_source_reconcile", new { admissionId = admission.Id });
                if (mutation.Key != "unchanged") throw new Exception("Unsafe source reconciliation: " + mutation.Key);
                if (store.Read(admission.Id).State != "reconciled") throw new Exception("Unchanged source was not reconciled.");
            }
            catch (BridgeException error) when (mutation.Key != "unchanged" && error.Code is "ReconciliationIncomplete" or "ArtifactChanged")
            {
                if (store.Read(admission.Id).State != "outcomeUnknown") throw new Exception("Failed proof released original ownership.");
            }
            if (bridge.Reads != 1) throw new Exception("Reconciliation must observe once, never mutate/replay.");
            passed++;
        }
        // An error before HeaderAuthor read/archive now retains this initial marker.
        // It deliberately contains no checkpoint or author fields; fresh complete
        // baseline observation is sufficient, whereas marker absence is never proof.
        foreach (string failure in new[] { "missing initial marker", "wrong request binding", "changed baseline receipt" })
        {
            var bridge = new Reader(); var store = new EngineeringMutationAdmissionStore(bridge.Root);
            var admission = store.Acquire(new("watch_authoring_source_negative", new('b', 64), new('c', 64), "epoch-1", new('d', 64), 1,
                new('e', 64), [new("project", bridge.Project, "ownedWatchSourceNegative")]), new('f', 64));
            store.Unknown(admission.Id, admission.Revision);
            if (failure == "missing initial marker") File.Delete(bridge.Marker);
            else if (failure == "wrong request binding")
            {
                var marker = JsonNode.Parse(File.ReadAllText(bridge.Marker))!;
                marker["requestSha256"] = new string('a', 64); File.WriteAllText(bridge.Marker, marker.ToJsonString());
            }
            else File.AppendAllText(Path.Combine(bridge.Root, "snapshot-1.json"), "changed");
            var dispatcher = new WatchTableDispatcher(bridge, bridge.Root, allowUnqualifiedDocumentEdits: true, allowComponentQualification: true);
            bool refused = false;
            try { await dispatcher.CallAsync("qualification_watch_authoring_source_reconcile", new { admissionId = admission.Id }); }
            catch (BridgeException) { refused = true; }
            if (!refused || bridge.Reads != 0 || store.Read(admission.Id).State != "outcomeUnknown")
                throw new Exception("Unproven initial-marker error released ownership: " + failure);
            passed++;
        }
        Console.WriteLine($"PASS WT source-negative recovery: {passed} actual dispatcher checks including pre-effect marker failure; no native mutation.");
    }

    private sealed class Reader : IBridgeClient
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "watch-negative-recovery-" + Guid.NewGuid().ToString("N"));
        internal string Project => Path.Combine(Root, "owned", "fixture.ap21");
        internal string Marker => Path.Combine(Root, "watch-source-negative", "negative-intent-" + WatchTableDocument.Hash(System.Text.Encoding.UTF8.GetBytes(Project)) + ".json");
        internal readonly JsonObject Current;
        internal int Reads;
        internal Reader()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Project)!);
            Directory.CreateDirectory(Path.Combine(Root, "watch-source-negative"));
            var before = Snapshot(1);
            File.WriteAllText(Marker, new JsonObject {
                ["before"] = before, ["requestSha256"] = new string('b', 64),
                ["sourceBinding"] = new JsonObject { ["plcPath"] = "/PLC" }
            }.ToJsonString());
            Current = Snapshot(2);
        }
        private JsonObject Snapshot(int epoch)
        {
            string absent = Path.Combine(Root, "absence-" + epoch + ".txt");
            byte[] bytes = WatchTableDocument.AbsentBytes("root", WatchAuthoringProfile.Table); File.WriteAllBytes(absent, bytes);
            string hash = WatchTableDocument.Hash(bytes);
            var observation = new NativeTargetObservation(new(new(Project, "/PLC", "/PLC", "/PLC/watch/root/" + WatchAuthoringProfile.Table, "watchTable"),
                new("capture-" + epoch, new('1', 64)), hash), Project, "epoch-" + epoch, new('d', 64), new('2', 64), "cpu", null, true,
                [new("offline", "observed", new('3', 64)), new("watchImport", "observed", new('4', 64))]);
            var result = JsonSerializer.SerializeToNode(new { observation, processId = epoch, projectModified = false,
                peerSha256 = new string('5', 64), peers = new Dictionary<string, string> { ["block:/PLC/FB"] = new('6', 64) },
                target = new { exists = false, name = WatchAuthoringProfile.Table, group = "root", isConsistent = true, rows = Array.Empty<object>(),
                    semanticSha256 = hash, artifact = new { path = absent, sha256 = hash } } }, BridgeProtocol.Json)!.AsObject();
            string receipt = Path.Combine(Root, "snapshot-" + epoch + ".json"); File.WriteAllText(receipt, result.ToJsonString());
            result["receipt"] = new JsonObject { ["path"] = receipt, ["sha256"] = WatchTableDocument.Hash(File.ReadAllBytes(receipt)) };
            return result;
        }
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            if (operation != "watch_read_native") throw new Exception("Reconciliation attempted a mutation: " + operation);
            Reads++; return Task.FromResult(JsonSerializer.SerializeToElement(Current));
        }
    }
}
