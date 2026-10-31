using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Translations;
using static TiaMcp.Translations.TranslationJson;

// Host-side category filter on the qualification capture path. Synthetic native workbooks in the observed V21 layout only.
internal static class RestrictedCaptureChecks
{
    private const string Comments = "BlockCommentCategoryData", Hardware = "HwcnCommentTextCategoryData", Alarms = "AlarmClassDisplayNameCategory";
    private static readonly string[] Inventory = [Alarms, Comments, Hardware];

    internal static void Register(Action<string, Func<Task>> test, string output)
    {
        static void True(bool value, string message) { if (!value) throw new Exception(message); }
        // 28 October shape: every category request on the PLC returned all six rows (five block comments, one hardware comment).
        string[][] Over() => [ProjectTextExportChecks.Values("K-0", "<" + Hardware + ">", 1),
            .. Enumerable.Range(1, 5).Select(i => ProjectTextExportChecks.Values("K-" + i, "<" + Comments + ">", 1))];
        test("restricted capture keeps exactly the requested categories of an over-returned native export", async () => {
            foreach (var (category, expected) in new[] { (Comments, 5), (Hardware, 1), (Alarms, 0) })
            {
                var native = new Native(output, Over());
                var (observation, profile) = await native.Capture([category]);
                True(profile.Rows.Length == expected && profile.Rows.All(r => r.Category == "<" + category + ">") &&
                    profile.Rows.Select(r => r.KeyCell.Cell).SequenceEqual(Enumerable.Range(2, expected).Select(n => "C" + n)), category + ": filtered rows differ");
                True(observation.Scope.CategoryIds.SequenceEqual([category]) && observation.Scope.ObjectSelections.SequenceEqual(["plc:PLC"]), category + ": scope differs");
                var excluded = observation.Peers.Single(p => p.Identity == TranslationWorkerAdapter.ExcludedRowsPeer);
                var rows = JsonNode.Parse(new TranslationImportArtifacts(native.Root).ReadBytes(excluded.Evidence))!.AsObject();
                True(rows.Count == 6 - expected && rows.All(r => r.Value!["category"]!.GetValue<string>() != "<" + category + ">"), category + ": excluded rows differ");
                True(File.Exists(Path.Combine(native.Root, "translation-import", "blobs", Sha256(native.Bytes))), category + ": unfiltered native export not retained");
            }
        });
        test("restricted capture refuses missing or ambiguous row identity and a silent whole-project export", async () => {
            async Task Refused(string code, string[][] rows, string[] requested, Action<JsonObject>? reply = null)
            {
                try { await new Native(output, rows, reply).Capture(requested); }
                catch (BridgeException error) when (error.Code == code) { return; }
                throw new Exception("Expected " + code);
            }
            var native = Over();
            await Refused("TranslationScopeMapMismatch", [.. native, ProjectTextExportChecks.Values("K-6", "<UnknownCategoryData>", 1)], [Comments]);
            await Refused("UnsupportedWorkbookProfile", [.. native, ProjectTextExportChecks.Values("K-6", "", 1)], [Comments]);
            await Refused("DuplicateNativeKey", [.. native, ProjectTextExportChecks.Values("K-1", "<" + Hardware + ">", 1)], [Comments]);
            await Refused("TranslationScopeMismatch", native, [Comments], reply => reply.Remove("requestedCategoryIds"));
            await Refused("InvalidTranslationCategoryScope", native, ["OtherCategory"]);
        });
        test("Host-filtered capture reviews, approves and prepares only its admitted category rows", async () => {
            var native = new Native(output, Over());
            var (observation, profile) = await native.Capture([Comments]);
            var store = new TranslationWorkflowStore(native.Root); var uploads = new ArtifactUploadStore(native.Root);
            string Upload(byte[] bytes, string name) { var item = uploads.Begin(name, bytes.Length, Sha256(bytes)); uploads.Write(item.UploadId, 0, Convert.ToBase64String(bytes)); return uploads.Commit(item.UploadId).ArtifactRef!; }
            var capture = store.Capture(Upload(observation.Workbook, "original.xlsx"), Upload(TranslationWorkerAdapter.JsonBytes(profile), "profile.json"),
                Upload(TranslationWorkerAdapter.JsonBytes(observation.Target.Reference), "project.json"));
            var workbook = TranslationWorkbook.Open(observation.Workbook, profile, observation.Target.Reference, Sha256(observation.Workbook));
            var pair = workbook.Read("K-1", "en-GB");
            var review = store.Prepare(capture["recordRef"]!.GetValue<string>(), Upload(TranslationWorkerAdapter.JsonBytes(new TranslationProposalBatch(
                [new("K-1", "en-GB", TranslationEditKind.Set, "Restricted edit", pair.Source.Sha256, pair.Target.Sha256)])), "proposals.json"));
            string reference = review["recordRef"]!.GetValue<string>(), digest = review["metadata"]!["reviewSha256"]!.GetValue<string>();
            var candidate = store.Candidate(reference, digest, store.Approve(reference, digest, "synthetic test reviewer", true)["recordSha256"]!.GetValue<string>());
            string candidateRef = candidate["recordRef"]!.GetValue<string>(), receipt = candidate["recordSha256"]!.GetValue<string>();
            var runner = new TranslationImportWorkflow(native.Root);
            // The same rows under a scope that admits another category are outside it and never reach a plan.
            var foreign = Fixture.Bind(observation with { Scope = observation.Scope with { CategoryIds = [Hardware] } });
            try { runner.Prepare(candidateRef, receipt, foreign, "outside"); throw new Exception("Out-of-scope candidate rows were prepared"); }
            catch (BridgeException error) when (error.Code == "TranslationScopeMismatch") { }
            var op = runner.Prepare(candidateRef, receipt, observation, "restricted");
            True(op.State == "prepared" && runner.Status(op.Id).PlanSha256 == op.PlanSha256, "Admitted restricted candidate was not prepared");
        });
    }

    // One fresh artifact root per worker reply; the synthetic worker returns the native (unfiltered) category export.
    private sealed class Native : IBridgeClient
    {
        internal string Root { get; }
        internal byte[] Bytes { get; }
        private readonly Action<JsonObject>? reply;
        internal Native(string output, string[][] rows, Action<JsonObject>? reply = null)
        {
            Root = Path.Combine(output, "restricted-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            Bytes = ProjectTextExportChecks.Book(["de-DE", "en-GB"], rows); this.reply = reply;
        }
        internal Task<(TranslationImportObservation, WorkbookProfile)> Capture(string[] categoryIds) =>
            new TranslationWorkerAdapter(this, Root, Path.Combine(Root, "Fixture.ap21"), "PLC", (_, _) => { }, categoryIds: categoryIds).Capture();
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            if (operation != "translation_capture") throw new InvalidOperationException(operation);
            var selector = JsonNode.Parse(JsonSerializer.Serialize(arguments))!;
            JsonObject Artifact(string name, byte[] bytes) { string path = Path.Combine(Root, name); File.WriteAllBytes(path, bytes); return new() { ["path"] = path, ["sha256"] = Sha256(bytes) }; }
            var value = new JsonObject
            {
                ["workbook"] = Artifact("all.xlsx", Bytes), ["projectPath"] = Path.Combine(Root, "Fixture.ap21"), ["projectName"] = "Fixture",
                ["peers"] = new JsonArray(new JsonObject { ["identity"] = "Main.xml", ["profile"] = "exact", ["artifact"] = Artifact("Main.xml", Encoding.UTF8.GetBytes("<Main/>")) }),
                ["sessionEpoch"] = "epoch-1", ["workerSha256"] = new string('e', 64), ["hardwareIdentity"] = "synthetic-CPU", ["offline"] = true,
                ["activeCultures"] = new JsonArray("de-DE", "en-GB"), ["categoryIds"] = new JsonArray(Inventory.Select(c => (JsonNode)c!).ToArray()),
                ["requestedCategoryIds"] = selector["categoryIds"]!.DeepClone()
            };
            reply?.Invoke(value);
            return Task.FromResult(JsonDocument.Parse(value.ToJsonString()).RootElement.Clone());
        }
    }
}
