using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
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
        test("a capture that keeps no rows is a well-formed header-only workbook", async () => {
            var native = new Native(output, Over());
            var (observation, profile) = await native.Capture([Alarms]);
            var parts = Fixture.Parts(observation.Workbook);
            True(profile.Rows.Length == 0 && parts.Keys.Order(StringComparer.Ordinal).SequenceEqual(Fixture.Parts(native.Bytes).Keys.Order(StringComparer.Ordinal)), "Empty capture changed the package parts");
            foreach (string sheet in new[] { "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml" })
            {
                var rows = XDocument.Parse(Encoding.UTF8.GetString(parts[sheet])).Descendants(Fixture.S + "row").ToArray();
                True(rows.Length == 1 && (string?)rows[0].Attribute("r") == "1" && rows[0].Elements(Fixture.S + "c").Count() == 22, sheet + " lost its header row");
            }
            var workbook = TranslationWorkbook.Open(observation.Workbook, profile, observation.Target.Reference, Sha256(observation.Workbook));
            True(TranslationCategoryScope.NativeTargetCulture(workbook) == "en-GB" && TranslationInspection.Page(workbook, 1000)["rows"]!.AsArray().Count == 0,
                "Empty capture is not a readable bilingual layout");
        });
        test("restricted capture refuses missing or ambiguous row identity and a silent whole-project export", async () => {
            async Task Refused(string code, Native native, string[] requested)
            {
                try { await native.Capture(requested); }
                catch (BridgeException error) when (error.Code == code) { return; }
                throw new Exception("Expected " + code);
            }
            var rows = Over();
            await Refused("TranslationScopeMapMismatch", new(output, [.. rows, ProjectTextExportChecks.Values("K-6", "<UnknownCategoryData>", 1)]), [Comments]);
            await Refused("UnsupportedWorkbookProfile", new(output, [.. rows, ProjectTextExportChecks.Values("K-6", "", 1)]), [Comments]);
            await Refused("DuplicateNativeKey", new(output, [.. rows, ProjectTextExportChecks.Values("K-1", "<" + Hardware + ">", 1)]), [Comments]);
            await Refused("TranslationScopeMismatch", new(output, rows, reply => reply.Remove("requestedCategoryIds")), [Comments]);
            await Refused("InvalidTranslationCategoryScope", new(output, rows), ["OtherCategory"]);
        });
        test("row filtering refuses worksheet content or parts that can reference removed rows", async () => {
            string sheet = "xl/worksheets/sheet1.xml";
            var shapes = new Func<byte[], byte[]>[]
            {
                b => Fixture.Modify(b, sheet, x => x.Add(new XElement(Fixture.S + "mergeCells", new XElement(Fixture.S + "mergeCell", new XAttribute("ref", "E2:E3"))))),
                b => Fixture.Modify(b, sheet, x => x.Add(new XElement(Fixture.S + "hyperlinks", new XElement(Fixture.S + "hyperlink", new XAttribute("ref", "B3"))))),
                b => Fixture.Modify(b, sheet, x => x.Add(new XElement(Fixture.S + "conditionalFormatting", new XAttribute("sqref", "F2:F7")))),
                b => Fixture.Modify(b, sheet, x => x.Add(new XElement(Fixture.S + "dataValidations", new XElement(Fixture.S + "dataValidation", new XAttribute("sqref", "F2"))))),
                b => Fixture.Modify(b, "xl/workbook.xml", x => x.Add(new XElement(Fixture.S + "definedNames", new XElement(Fixture.S + "definedName", "'User Texts'!$A$2:$F$7")))),
                b => { var parts = Fixture.Parts(b); parts["xl/comments1.xml"] = Encoding.UTF8.GetBytes("<comments/>"); return Fixture.Pack(parts); }
            };
            foreach (var shape in shapes)
            {
                try { await new Native(output, Over()) { Shape = shape }.Capture([Comments]); throw new Exception("Row-referencing layout was filtered"); }
                catch (BridgeException error) when (error.Code == "UnsupportedTranslationScopeLayout") { }
            }
            // The same content in the plain observed layout is filtered: the refusal comes from the referencing element alone.
            True((await new Native(output, Over()) { Shape = b => Fixture.Pack(Fixture.Parts(b)) }.Capture([Comments])).Item2.Rows.Length == 5, "Plain layout refused");
        });
        test("Host-filtered capture reviews, approves and prepares only its admitted category rows", async () => {
            var native = new Native(output, Over());
            var (observation, candidateRef, receipt) = await Candidate(native);
            var runner = new TranslationImportWorkflow(native.Root);
            // The same rows under a scope that admits another category are outside it and never reach a plan.
            var foreign = Fixture.Bind(observation with { Scope = observation.Scope with { CategoryIds = [Hardware] } });
            try { runner.Prepare(candidateRef, receipt, foreign, "outside"); throw new Exception("Out-of-scope candidate rows were prepared"); }
            catch (BridgeException error) when (error.Code == "TranslationScopeMismatch") { }
            var op = runner.Prepare(candidateRef, receipt, observation, "restricted");
            True(op.State == "prepared" && runner.Status(op.Id).PlanSha256 == op.PlanSha256, "Admitted restricted candidate was not prepared");
        });
        // After the import the worker exports the edited Main row; in the damaged case an excluded row changed too.
        string[][] Imported(bool foreign) => Over().Select(r => r[2] == "K-1" ? [.. r[..5], "Restricted edit"] :
            foreign && r[2] == "K-0" ? [.. r[..5], "Foreign hardware edit"] : r).ToArray();
        test("restricted import completes, recovers and reconciles through the worker adapter", async () => {
            var native = new Native(output, Over()) { AfterImport = Imported(false) };
            var (runner, op, adapter) = await Planned(native);
            var applied = await runner.Apply(op.Id, op.PlanSha256, op.Revision, adapter);
            var recovered = await runner.Recover(op.Id, op.PlanSha256, applied.Revision, Path.Combine(native.Root, "restored", "Fixture.ap21"), adapter);
            var done = await runner.Reconcile(op.Id, op.PlanSha256, recovered.Revision, adapter);
            True(applied.State == "succeeded" && native.Imports == 1 && done.State == "reconciled" && done.Reconciliation.Single().Disposition == "verifiedApplied" &&
                done.RecoveryResolutions.Single().Disposition == "verifiedRestored", "Restricted lifecycle differs");
        });
        test("a changed excluded row after import fails readback through the excluded-rows peer", async () => {
            var native = new Native(output, Over()) { AfterImport = Imported(true) };
            var (runner, op, adapter) = await Planned(native);
            try { await runner.Apply(op.Id, op.PlanSha256, op.Revision, adapter); throw new Exception("Changed excluded row was accepted"); }
            catch (BridgeException error) when (error.Code == "TranslationPreservationFailure") { }
            var status = runner.Status(op.Id);
            True(status.State == "recoveryRequired" && native.Imports == 1 && !native.Saved && status.Evidence.Any(e => e.Gate == "dispatch.readback"),
                "Readback refusal must keep recovery ownership before save");
        });
    }

    private static async Task<(TranslationImportObservation Observation, string CandidateRef, string Receipt)> Candidate(Native native)
    {
        var (observation, profile) = await native.Capture([Comments]);
        var store = new TranslationWorkflowStore(native.Root); var uploads = new ArtifactUploadStore(native.Root);
        string Upload(byte[] bytes, string name) { var item = uploads.Begin(name, bytes.Length, Sha256(bytes)); uploads.Write(item.UploadId, 0, Convert.ToBase64String(bytes)); return uploads.Commit(item.UploadId).ArtifactRef!; }
        var capture = store.Capture(Upload(observation.Workbook, "original.xlsx"), Upload(TranslationWorkerAdapter.JsonBytes(profile), "profile.json"),
            Upload(TranslationWorkerAdapter.JsonBytes(observation.Target.Reference), "project.json"));
        var pair = TranslationWorkbook.Open(observation.Workbook, profile, observation.Target.Reference, Sha256(observation.Workbook)).Read("K-1", "en-GB");
        var review = store.Prepare(capture["recordRef"]!.GetValue<string>(), Upload(TranslationWorkerAdapter.JsonBytes(new TranslationProposalBatch(
            [new("K-1", "en-GB", TranslationEditKind.Set, "Restricted edit", pair.Source.Sha256, pair.Target.Sha256)])), "proposals.json"));
        string reference = review["recordRef"]!.GetValue<string>(), digest = review["metadata"]!["reviewSha256"]!.GetValue<string>();
        var candidate = store.Candidate(reference, digest, store.Approve(reference, digest, "synthetic test reviewer", true)["recordSha256"]!.GetValue<string>());
        return (observation, candidate["recordRef"]!.GetValue<string>(), candidate["recordSha256"]!.GetValue<string>());
    }

    private static async Task<(TranslationImportWorkflow Runner, EngineeringOperationRecord Operation, TranslationWorkerAdapter Adapter)> Planned(Native native)
    {
        var (observation, candidateRef, receipt) = await Candidate(native);
        var runner = new TranslationImportWorkflow(native.Root);
        return (runner, runner.Prepare(candidateRef, receipt, observation, "restricted"), native.Adapter([Comments]));
    }

    // One fresh artifact root per synthetic worker. It always returns the native (unfiltered) category export and imitates
    // the translation stages: after the import it exports AfterImport, and a recovered copy exports the original rows.
    private sealed class Native : IBridgeClient
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        internal string Root { get; }
        internal byte[] Bytes { get; }
        internal string[][]? AfterImport { get; init; }
        internal Func<byte[], byte[]>? Shape { get; init; }
        internal int Imports { get; private set; }
        internal bool Saved { get; private set; }
        private readonly string[][] original;
        private readonly Action<JsonObject>? reply;
        private string[][] rows;
        private JsonNode? selector;
        private int files, epoch = 1;
        internal Native(string output, string[][] rows, Action<JsonObject>? reply = null)
        {
            Root = Path.Combine(output, "restricted-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            Bytes = ProjectTextExportChecks.Book(["de-DE", "en-GB"], rows); original = this.rows = rows; this.reply = reply;
        }
        internal TranslationWorkerAdapter Adapter(string[] categoryIds) => new(this, Root, Path.Combine(Root, "Fixture.ap21"), "PLC", (_, _) => { }, categoryIds: categoryIds);
        internal Task<(TranslationImportObservation, WorkbookProfile)> Capture(string[] categoryIds) => Adapter(categoryIds).Capture();
        private JsonObject Artifact(string name, byte[] bytes)
        { string path = Path.Combine(Root, ++files + "-" + name); File.WriteAllBytes(path, bytes); return new() { ["path"] = path, ["sha256"] = Sha256(bytes) }; }
        private JsonObject Observation(JsonNode request, string project, string[][] exported)
        {
            byte[] book = ProjectTextExportChecks.Book(["de-DE", "en-GB"], exported);
            var value = new JsonObject
            {
                ["workbook"] = Artifact("all.xlsx", Shape?.Invoke(book) ?? book), ["projectPath"] = project, ["projectName"] = "Fixture",
                ["peers"] = new JsonArray(new JsonObject { ["identity"] = "Main.xml", ["profile"] = "exact", ["artifact"] = Artifact("Main.xml", Encoding.UTF8.GetBytes("<Main/>")) }),
                ["sessionEpoch"] = "epoch-" + epoch, ["workerSha256"] = new string('e', 64), ["hardwareIdentity"] = "synthetic-CPU", ["offline"] = true,
                ["activeCultures"] = new JsonArray("de-DE", "en-GB"), ["categoryIds"] = new JsonArray(Inventory.Select(c => (JsonNode)c!).ToArray()),
                ["requestedCategoryIds"] = request["categoryIds"]!.DeepClone()
            };
            reply?.Invoke(value);
            return value;
        }
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            var args = JsonNode.Parse(JsonSerializer.Serialize(arguments, Web))!;
            JsonNode value;
            switch (operation)
            {
                case "translation_capture": value = Observation(args, Path.Combine(Root, "Fixture.ap21"), rows); break;
                case "translation_begin":
                    selector = args["selector"]!.DeepClone();
                    value = new JsonObject { ["leaseToken"] = "lease", ["operationId"] = args["context"]!["operation"]!["id"]!.DeepClone(),
                        ["planSha256"] = args["context"]!["operation"]!["planSha256"]!.DeepClone() }; break;
                case "translation_stage":
                    value = new JsonObject { ["value"] = Stage(args["stage"]!.GetValue<string>(), args["payload"], args["operation"]!), ["receipt"] = new JsonObject() }; break;
                case "translation_end": value = new JsonObject { ["released"] = true, ["operationId"] = args["operationId"]!.DeepClone() }; break;
                default: throw new InvalidOperationException(operation);
            }
            return Task.FromResult(JsonDocument.Parse(value.ToJsonString()).RootElement.Clone());
        }
        private JsonNode Stage(string stage, JsonNode? payload, JsonNode operation)
        {
            string project = Path.Combine(Root, "Fixture.ap21");
            switch (stage)
            {
                case "observe": return Observation(selector!, project, rows);
                case "checkpoint": return Artifact("checkpoint.zap21", Encoding.UTF8.GetBytes("SYNTHETIC CHECKPOINT, NEVER A TIA ARCHIVE"));
                case "import":
                    Imports++; string candidate = payload!["path"]!.GetValue<string>(); rows = AfterImport!;
                    var raw = new JsonObject { ["state"] = "Info", ["path"] = candidate, ["candidateSha256"] = Sha256(File.ReadAllBytes(candidate)),
                        ["updateSourceLanguage"] = false, ["operationId"] = operation["id"]!.DeepClone(), ["planSha256"] = operation["planSha256"]!.DeepClone() };
                    return new JsonObject { ["raw"] = Artifact("native-return.json", Encoding.UTF8.GetBytes(raw.ToJsonString())), ["log"] = Artifact("log.xml", Encoding.UTF8.GetBytes("<LogFile/>")) };
                case "compile": return new JsonObject { ["passed"] = true };
                case "save": Saved = true; return new JsonObject { ["saved"] = true, ["modified"] = false };
                case "reopen": epoch++; return Observation(selector!, project, rows);
                case "restore": return new JsonObject { ["restored"] = payload!["destination"]!.DeepClone() };
                case "recovered": epoch++; return Observation(selector!, payload!["destination"]!.GetValue<string>(), original);
                default: throw new InvalidOperationException(stage);
            }
        }
    }
}
