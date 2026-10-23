using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Translations;

internal static class ExpansionChecks
{
    private const string SheetPart = "xl/worksheets/sheet1.xml";
    private const string HiddenPart = "xl/worksheets/hidden.xml";
    private static readonly XNamespace Ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    internal static void Register(List<(string Name, Action Run)> tests, Dictionary<string, byte[]> fixture,
        WorkbookProfile profile, EngineeringObjectReference identity)
    {
        var original = Pack(fixture);
        TranslationWorkbook Open(byte[]? bytes = null, WorkbookProfile? map = null, EngineeringObjectReference? project = null)
        { bytes ??= original; return TranslationWorkbook.Open(bytes, map ?? profile, project ?? identity, Hash(bytes)); }
        TranslationWorkbook Changed(string old, string value) => Open(Mutate(fixture, SheetPart, s => s.Replace(old, value, StringComparison.Ordinal)));
        void Test(string name, Action action) => tests.Add((name, action));

        Test("broad format envelope preserves styles comments hidden sheets numbers and rich source", () =>
        {
            var (data, map) = Formatted(fixture, profile);
            var wb = Open(Pack(data), map);
            var candidate = Build(wb, [Edit(wb, "K-007", "Betriebsbereit"), Edit(wb, "HIDDEN-001", "Verborgen neu")]);
            var output = Unpack(candidate.Bytes);
            Equal("Betriebsbereit", Cell(output, SheetPart, "C8"));
            Equal("Verborgen neu", Cell(output, HiddenPart, "C1"));
            Equal("Fetter Text", Open(candidate.Bytes, map).Read("K-010", "de-DE").Target.Text);
            foreach (var pair in data.Where(p => p.Key != SheetPart && p.Key != HiddenPart))
                Check(pair.Value.AsSpan().SequenceEqual(output[pair.Key]), "Unrelated part changed: " + pair.Key);
            CompareMasked(data[SheetPart], output[SheetPart], "C8");
            CompareMasked(data[HiddenPart], output[HiddenPart], "C1");
        });
        Test("format envelope no-op returns exact original package", () =>
        { var (data, map) = Formatted(fixture, profile); var bytes = Pack(data); Check(bytes.AsSpan().SequenceEqual(Build(Open(bytes, map), []).Bytes)); });
        Test("rich source formatting survives target change", () =>
        { var (data, map) = Formatted(fixture, profile); var wb = Open(Pack(data), map); Check(wb.Read("K-004", "de-DE").Source.RichText); var after = Open(Build(wb, [Edit(wb, "K-004", "Neue Temperatur")]).Bytes, map); Equal(wb.Read("K-004", "de-DE").Source, after.Read("K-004", "de-DE").Source); });
        Test("inspection pages enumerate each exact target once in ordinal order", () =>
        {
            var wb = Open(); var seen = new List<string>(); TranslationInspectionCursor? cursor = null;
            do
            {
                var page = TranslationInspection.Page(wb, 3, cursor);
                foreach (var row in page["rows"]!.AsArray()) seen.Add(row!["nativeKey"] + "/" + row["culture"]);
                cursor = Cursor(page);
            } while (cursor is not null);
            var expected = profile.Rows.OrderBy(r => r.NativeKey, StringComparer.Ordinal)
                .SelectMany(r => r.Targets.OrderBy(t => t.Culture, StringComparer.Ordinal).Select(t => r.NativeKey + "/" + t.Culture)).ToArray();
            Check(expected.SequenceEqual(seen)); Equal(expected.Length, seen.Distinct().Count());
        });
        Test("inspection resume is serializable deterministic and never an approval", () =>
        {
            var wb = Open(); var cursor = Cursor(TranslationInspection.Page(wb, 3));
            var resumed = TranslationJson.Parse<TranslationInspectionCursor>(TranslationJson.Node(cursor).ToJsonString());
            Equal(CanonicalJson.Serialize(TranslationInspection.Page(wb, 3, cursor)), CanonicalJson.Serialize(TranslationInspection.Page(wb, 3, resumed)));
            Equal(false, TranslationInspection.Page(wb)["nativeImportAllowed"]!.GetValue<bool>());
        });
        Test("inspection cursor rejects changed package profile and observation", () =>
        {
            var wb = Open(); var cursor = Cursor(TranslationInspection.Page(wb, 3));
            Reject("StaleTranslationCursor", () => TranslationInspection.Page(Changed("Cooling enabled", "Cooling available"), 3, cursor));
            Reject("StaleTranslationCursor", () => TranslationInspection.Page(Open(map: profile with { Id = "different-profile" }), 3, cursor));
            Reject("StaleTranslationCursor", () => TranslationInspection.Page(Open(project: identity with { Snapshot = identity.Snapshot with { ObservationId = "new-capture" } }), 3, cursor));
        });
        Test("inspection bounds and empty final page are explicit", () =>
        {
            var wb = Open(); Reject("TranslationPageLimit", () => TranslationInspection.Page(wb, 1001)); Reject("TranslationPageLimit", () => TranslationInspection.Page(wb, 0));
            var cursor = Cursor(TranslationInspection.Page(wb, 3))!;
            Reject("InvalidTranslationCursor", () => TranslationInspection.Page(wb, 3, cursor with { Offset = -1 }));
            Reject("InvalidTranslationCursor", () => TranslationInspection.Page(wb, 3, cursor with { Offset = 10000 }));
            var last = TranslationInspection.Page(wb, 3, cursor with { Offset = 12 }); Equal(0, last["rows"]!.AsArray().Count); Check(last["nextCursor"] is null);
        });
        Test("inspection distinguishes missing empty unchanged and rich targets", () =>
        {
            var page = TranslationInspection.Page(Open());
            string[] Findings(string key) => page["rows"]!.AsArray().Single(r => r!["nativeKey"]!.GetValue<string>() == key)!["findings"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
            Check(Findings("K-002").Contains("missing")); Check(Findings("K-006").Contains("empty"));
            Check(Findings("K-009").Contains("same-as-source")); Check(Findings("K-010").Contains("rich-text-edit-unsupported"));
        });
        Test("reconciliation creates unapproved proposals bound to fresh capture", () =>
        {
            var baseline = Open(); var edited = Open(Build(baseline, [Edit(baseline, "K-007", "Betriebsbereit")]).Bytes);
            var current = Open(project: identity with { Snapshot = identity.Snapshot with { ObservationId = "fresh-capture" } });
            var result = TranslationReconciliation.Reconcile(baseline, edited, current);
            var proposals = TranslationJson.Parse<TranslationEdit[]>(result["proposals"]!.ToJsonString());
            Equal(1, proposals.Length); Check(!proposals[0].Approved && proposals[0].Reviewer is null);
            Equal(current.Read("K-007", "de-DE").Target.Sha256, proposals[0].ExpectedTargetSha256);
            Equal(0, result["conflicts"]!.GetValue<int>());
            Check(!TranslationReview.Review(current, current, proposals)["canWriteOfflineCandidate"]!.GetValue<bool>());
            var approved = proposals.Select(p => p with { Approved = true, Reviewer = "fresh reviewer" }).ToArray();
            Equal("Betriebsbereit", Open(Build(current, approved).Bytes).Read("K-007", "de-DE").Target.Text);
        });
        Test("reconciliation reports source conflicts without automatic rebase", () =>
        {
            var wb = Open(); var edited = Open(Build(wb, [Edit(wb, "K-007", "Neu")]).Bytes);
            var current = Changed("<c r=\"B8\" t=\"inlineStr\"><is><t>Ready", "<c r=\"B8\" t=\"inlineStr\"><is><t>Prepared");
            var report = TranslationReconciliation.Reconcile(wb, edited, current); Equal("SourceTextConflict", report["rows"]![0]!["conflictCode"]!.GetValue<string>());
            Equal(0, report["proposals"]!.AsArray().Count); Equal(false, report["canReviewProposals"]!.GetValue<bool>());
        });
        Test("reconciliation reports target conflicts and already applied values independently", () =>
        {
            var wb = Open(); var edited = Open(Build(wb, [Edit(wb, "K-007", "Neu")]).Bytes);
            var concurrent = Open(Build(wb, [Edit(wb, "K-007", "Andere Änderung")]).Bytes);
            Equal("TargetTextConflict", TranslationReconciliation.Reconcile(wb, edited, concurrent)["rows"]![0]!["conflictCode"]!.GetValue<string>());
            var applied = TranslationReconciliation.Reconcile(wb, edited, edited); Equal(1, applied["alreadyApplied"]!.GetValue<int>()); Equal(0, applied["proposals"]!.AsArray().Count);
        });
        Test("reconciliation preserves concurrent unrelated translation change", () =>
        {
            var wb = Open(); var edited = Open(Build(wb, [Edit(wb, "K-007", "Neu")]).Bytes);
            var current = Open(Build(wb, [Edit(wb, "K-006", "Nebenläufig")]).Bytes);
            var report = TranslationReconciliation.Reconcile(wb, edited, current);
            var approvals = TranslationJson.Parse<TranslationEdit[]>(report["proposals"]!.ToJsonString()).Select(e => e with { Approved = true, Reviewer = "new reviewer" }).ToArray();
            var after = Open(Build(current, approvals).Bytes); Equal("Nebenläufig", after.Read("K-006", "de-DE").Target.Text); Equal("Neu", after.Read("K-007", "de-DE").Target.Text);
        });
        Test("reconciliation source and native-key edits cannot become target suggestions", () =>
        {
            var wb = Open(); Reject("TranslationPreservationFailure", () => TranslationReconciliation.Reconcile(wb, Changed("Cooling enabled", "Heating enabled"), wb));
            Reject("StaleTextKey", () => Changed("K-008-current", "K-008-recreated"));
        });
        Test("reconciliation rejects foreign identity and changed mapping", () =>
        {
            var wb = Open(); Reject("IdentityMismatch", () => TranslationReconciliation.Reconcile(wb, wb, Open(project: identity with { Object = identity.Object with { ProjectPath = @"D:\copy.ap21" } })));
            Reject("ReconciliationProfileMismatch", () => TranslationReconciliation.Reconcile(wb, wb, Open(map: profile with { Id = "copy-map" })));
            Reject("StaleObservation", () => TranslationReconciliation.Reconcile(wb, Open(project: identity with { Snapshot = identity.Snapshot with { ObservationId = "wrong-editor-provenance" } }), wb));
        });
        Test("reconciliation refuses removed cells and accepts explicit clear only", () =>
        {
            var wb = Open(); Reject("RemovedTranslationCell", () => TranslationReconciliation.Reconcile(wb, Changed("<c r=\"C7\"/>", ""), wb));
            var pair = wb.Read("K-007", "de-DE"); var clear = new TranslationEdit("K-007", "de-DE", TranslationEditKind.Clear, null, pair.Source.Sha256, pair.Target.Sha256, true, "reviewer");
            var report = TranslationReconciliation.Reconcile(wb, Open(Build(wb, [clear]).Bytes), wb); Equal("clear", report["proposals"]![0]!["kind"]!.GetValue<string>());
        });
        Test("reconciliation rejects rich-text flattening and same-text format edits", () =>
        {
            var wb = Open(); Reject("RichTextEditUnsupported", () => TranslationReconciliation.Reconcile(wb, Changed("<r><rPr><b/></rPr><t>Fetter</t></r><r><t xml:space=\"preserve\"> Text</t></r>", "<t>Plain replacement</t>"), wb));
            Reject("TranslationPreservationFailure", () => TranslationReconciliation.Reconcile(wb, Changed("<b/>", "<i/>"), wb));
        });
        Test("reconciliation rejects unrelated package and selected-cell style edits", () =>
        {
            var wb = Open(); Reject("TranslationPreservationFailure", () => TranslationReconciliation.Reconcile(wb, Open(Mutate(fixture, "customXml/item1.xml", s => s.Replace("</", "<!--new--></", StringComparison.Ordinal))), wb));
            var edit = Build(wb, [Edit(wb, "K-007", "Neu")]); var data = Unpack(edit.Bytes);
            Reject("TranslationPreservationFailure", () => TranslationReconciliation.Reconcile(wb, Open(Mutate(data, SheetPart, s => s.Replace("r=\"C8\"", "r=\"C8\" s=\"4\"", StringComparison.Ordinal))), wb));
        });
        Test("reconciliation no-op is deterministic with zero proposals", () =>
        { var wb = Open(); var one = TranslationReconciliation.Reconcile(wb, wb, wb); Equal(0, one["proposals"]!.AsArray().Count); Equal(CanonicalJson.Serialize(one), CanonicalJson.Serialize(TranslationReconciliation.Reconcile(wb, wb, wb))); });
        Test("simultaneous source and target changes require a fresh target review", () =>
        {
            var wb = Open(); var report = TranslationReview.Review(wb, wb, [Edit(wb, "K-007", "Neu"), Edit(wb, "K-007", "Prepared", "en-US")], new(UpdateSourceLanguage: true));
            Check(report["issues"]!.AsArray().Any(i => i!["code"]!.GetValue<string>() == "SourceAndTargetEditConflict"));
            Equal(false, report["canWriteOfflineCandidate"]!.GetValue<bool>());
        });
        Test("malformed proposals and glossary are refused even without changes", () =>
        {
            var wb = Open(); Reject("InvalidTranslationEdit", () => TranslationReview.Review(wb, wb, [null!]));
            Reject("InvalidGlossary", () => TranslationReview.Review(wb, wb, [], new(Glossary: [new("", "target", "de-DE")])));
            Reject("InvalidGlossary", () => TranslationReview.Review(wb, wb, [], new(GlossaryId: "v1", Glossary: [null!])));
        });
        Test("multi-owner duplicate keys are refused across worksheet boundaries", () =>
        {
            var (data, map) = ScaleFixture(1000); var rows = map.Rows.ToArray(); rows[^1] = rows[^1] with { NativeKey = rows[0].NativeKey };
            Reject("DuplicateNativeKey", () => Open(Pack(data), map with { Rows = rows }));
        });
        Test("profile missing-target locations count toward cell limit", () =>
        {
            var rows = profile.Rows.Select(r => r with { Targets = r.Targets.Concat([new TranslationCell("it-IT", new CellLocation(r.SourceCell.Part, "Z" + r.SourceCell.Cell[1..]))]).ToArray() }).ToArray();
            Reject("WorkbookLimit", () => TranslationWorkbook.Open(original, profile with { Rows = rows }, identity, Hash(original), new(MaximumCells: 40)));
        });
        Test("ambiguous mapped string encodings are refused", () =>
        {
            Reject("WorkbookIntegrityFailure", () => Changed("<v>0</v>", "<v>0</v><v>0</v>"));
            Reject("WorkbookIntegrityFailure", () => Changed("<t>Cooling enabled</t>", "<t>Cooling enabled</t><t>Hidden duplicate</t>"));
            Reject("WorkbookIntegrityFailure", () => Changed("<t>Cooling enabled</t>", "<t>Cooling enabled</t><r><t>Hidden run</t></r>"));
            Reject("WorkbookIntegrityFailure", () => Changed("<c r=\"C7\"/>", "<c r=\"C7\" t=\"inlineStr\"><v>0</v><is><t>Ambiguous</t></is></c>"));
        });
        Test("escaped invalid UTF16 is refused during workbook capture", () =>
        { Reject("InvalidTranslationText", () => Changed("Cooling enabled", "_xD800_")); Reject("InvalidTranslationText", () => Changed("Cooling enabled", "_xDC00_")); });
    }

    internal static void WriteEvidence(string path, Dictionary<string, byte[]> fixture, WorkbookProfile profile,
        EngineeringObjectReference identity, int tests)
    {
        if (Directory.Exists(path)) throw new Exception("Evidence directory already exists: " + path);
        Directory.CreateDirectory(path);
        var (formatted, map) = Formatted(fixture, profile); var bytes = Pack(formatted);
        var wb = TranslationWorkbook.Open(bytes, map, identity, Hash(bytes));
        File.WriteAllBytes(Path.Combine(path, "formatted-synthetic-original.xlsx"), bytes);
        File.WriteAllText(Path.Combine(path, "formatted-synthetic-profile.json"), TranslationJson.Node(map).ToJsonString());
        var candidate = Build(wb, [Edit(wb, "K-007", "Betriebsbereit"), Edit(wb, "HIDDEN-001", "Verborgen neu")]);
        candidate.WriteNew(Path.Combine(path, "formatted-synthetic-candidate.xlsx"), Path.Combine(path, "formatted-synthetic-candidate.manifest.json"));
        var edited = TranslationWorkbook.Open(candidate.Bytes, map, identity, Hash(candidate.Bytes));
        File.WriteAllText(Path.Combine(path, "reconciliation.json"), TranslationReconciliation.Reconcile(wb, edited, wb).ToJsonString());
        File.WriteAllText(Path.Combine(path, "inspection-page.json"), TranslationInspection.Page(wb, 4).ToJsonString());
        var scale = RunScale(identity, 100000);
        File.WriteAllText(Path.Combine(path, "scale-100k.json"), scale.ToJsonString());
        File.WriteAllText(Path.Combine(path, "verification.json"), new JsonObject { ["checks"] = tests, ["passed"] = tests,
            ["scalePassed"] = true, ["fixtureKind"] = "synthetic-not-native-qualified", ["nativeExecuted"] = false,
            ["nativeQualified"] = false, ["nativeImportAllowed"] = false, ["scale"] = scale.DeepClone() }.ToJsonString());
        Console.WriteLine("Evidence artifacts: " + path);
    }

    private static JsonObject RunScale(EngineeringObjectReference identity, int count)
    {
        var timer = Stopwatch.StartNew(); long allocated = GC.GetTotalAllocatedBytes(true);
        var times = new JsonObject();
        var (data, map) = ScaleFixture(count); var bytes = Pack(data);
        times["fixtureAndZipMs"] = timer.ElapsedMilliseconds; timer.Restart();
        var wb = TranslationWorkbook.Open(bytes, map, identity, Hash(bytes));
        times["captureMs"] = timer.ElapsedMilliseconds; timer.Restart();
        int seen = 0, missing = 0, empty = 0, whitespace = 0, pages = 0;
        bool resumeVerified = false;
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        TranslationInspectionCursor? cursor = null;
        do
        {
            var page = TranslationInspection.Page(wb, 1000, cursor);
            if (pages == 17)
            {
                var resumed = TranslationJson.Parse<TranslationInspectionCursor>(TranslationJson.Node(cursor).ToJsonString());
                Equal(page["pageSha256"]!.GetValue<string>(), TranslationInspection.Page(wb, 1000, resumed)["pageSha256"]!.GetValue<string>());
                resumeVerified = true;
            }
            foreach (var row in page["rows"]!.AsArray())
            {
                actual.AppendData(Encoding.UTF8.GetBytes(row!["nativeKey"]!.GetValue<string>() + "\0" + row["culture"]!.GetValue<string>() + "\n")); seen++;
                var findings = row["findings"]!.AsArray().Select(f => f!.GetValue<string>()).ToArray();
                if (findings.Contains("missing")) missing++; if (findings.Contains("empty")) empty++; if (findings.Contains("whitespace-only")) whitespace++;
            }
            cursor = Cursor(page); pages++;
        } while (cursor is not null);
        times["inspectAllPagesAndReplayMs"] = timer.ElapsedMilliseconds; timer.Restart();
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int owner = 0; owner < 10; ++owner)
            for (int row = 0; row < count / 10; ++row)
                foreach (string culture in new[] { "de-DE", "fr-FR" })
                    expected.AppendData(Encoding.UTF8.GetBytes($"OWNER-{owner:D2}/KEY-{row:D6}\0{culture}\n"));
        string actualDigest = Convert.ToHexStringLower(actual.GetHashAndReset()), expectedDigest = Convert.ToHexStringLower(expected.GetHashAndReset());
        Equal(expectedDigest, actualDigest); Equal(count * 2, seen); Equal(count / 10, missing); Equal(count / 10, empty); Equal(count / 10, whitespace);
        var edits = Enumerable.Range(0, 1000).Select(i => Edit(wb, $"OWNER-{i % 10:D2}/KEY-{i / 10:D6}", "Reviewed " + i)).ToArray();
        var candidate = Build(wb, edits);
        times["reviewBuildReopenPreservationMs"] = timer.ElapsedMilliseconds; timer.Restart();
        // Independent oracle reads the authored inline form directly from package XML.
        var output = Unpack(candidate.Bytes); int verified = 0;
        for (int owner = 0; owner < 10; ++owner)
        {
            string part = $"xl/worksheets/owner{owner:D2}.xml";
            var document = XDocument.Parse(Encoding.UTF8.GetString(output[part]));
            foreach (var row in document.Root!.Element(Ss + "sheetData")!.Elements(Ss + "row"))
            {
                int index = int.Parse(row.Attribute("r")!.Value) - 1;
                var cells = row.Elements(Ss + "c").ToDictionary(c => c.Attribute("r")!.Value);
                string? de = cells.GetValueOrDefault("C" + (index + 1))?.Element(Ss + "is")?.Element(Ss + "t")?.Value;
                string? expectedDe = index < 100 ? "Reviewed " + (index * 10 + owner) : (index % 10) switch { 0 => null, 1 => "", 2 => " ", _ => "Deutsch " + index };
                if (index % 10 == 1 && index >= 100) de ??= "";
                Equal(expectedDe, de);
                Equal("Français " + index, cells["D" + (index + 1)].Element(Ss + "is")!.Element(Ss + "t")!.Value);
                Equal("Source " + index, cells["B" + (index + 1)].Element(Ss + "is")!.Element(Ss + "t")!.Value);
                Equal($"OWNER-{owner:D2}/KEY-{index:D6}", cells["A" + (index + 1)].Element(Ss + "is")!.Element(Ss + "t")!.Value);
                verified++;
            }
        }
        Equal(count, verified); Equal(1000, candidate.Manifest["changedCells"]!.GetValue<int>());
        times["independentFullValueOracleMs"] = timer.ElapsedMilliseconds;
        return new JsonObject { ["kind"] = "synthetic-translation-scale-receipt", ["keyCount"] = count, ["owners"] = 10, ["categories"] = 4,
            ["targetCultures"] = new JsonArray("de-DE", "fr-FR"), ["mappedTargets"] = seen, ["pageSize"] = 1000, ["pages"] = pages,
            ["missing"] = missing, ["empty"] = empty, ["whitespaceOnly"] = whitespace, ["changedTargets"] = 1000,
            ["independentVerifiedKeys"] = verified, ["expectedOrderedKeyCultureSha256"] = expectedDigest, ["actualOrderedKeyCultureSha256"] = actualDigest,
            ["cursorSerializationAndReadOnlyReplayVerified"] = resumeVerified, ["processCrashRecoveryTested"] = false,
            ["originalSha256"] = Hash(bytes), ["candidateSha256"] = Hash(candidate.Bytes), ["compressedBytes"] = bytes.Length,
            ["expandedPartBytes"] = data.Values.Sum(v => (long)v.Length), ["managedAllocatedBytes"] = GC.GetTotalAllocatedBytes(true) - allocated,
            ["processPeakWorkingSetBytes"] = Process.GetCurrentProcess().PeakWorkingSet64, ["timings"] = times,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["scope"] = "Synthetic explicit-map observations only; timings are this run, not production guarantees or native qualification.",
            ["nativeExecuted"] = false, ["nativeQualified"] = false, ["nativeImportAllowed"] = false };
    }

    private static (Dictionary<string, byte[]> Parts, WorkbookProfile Profile) ScaleFixture(int count)
    {
        var data = MinimalPackage(); var rows = new List<TranslationRow>(count);
        var workbook = new XElement(Ss + "workbook", new XElement(Ss + "sheets")); var links = new XElement(Rel + "Relationships");
        for (int owner = 0; owner < 10; ++owner)
        {
            string part = $"xl/worksheets/owner{owner:D2}.xml";
            workbook.Element(Ss + "sheets")!.Add(new XElement(Ss + "sheet", new XAttribute("name", "SyntheticOwner" + owner), new XAttribute("sheetId", owner + 1), new XAttribute(XName.Get("id", OfficeRel.TrimEnd('/')), "s" + owner)));
            links.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "s" + owner), new XAttribute("Type", OfficeRel + "worksheet"), new XAttribute("Target", "worksheets/owner" + owner.ToString("D2") + ".xml")));
            var text = new StringBuilder("<worksheet xmlns=\"" + Ss + "\"><sheetData>");
            for (int index = 0; index < count / 10; ++index)
            {
                int number = index + 1; string key = $"OWNER-{owner:D2}/KEY-{index:D6}";
                text.Append("<row r=\"").Append(number).Append("\">");
                Inline(text, "A" + number, key); Inline(text, "B" + number, "Source " + index);
                if (index % 10 == 1) text.Append("<c r=\"C").Append(number).Append("\"/>");
                else if (index % 10 != 0) Inline(text, "C" + number, index % 10 == 2 ? " " : "Deutsch " + index);
                Inline(text, "D" + number, "Français " + index); text.Append("</row>");
                rows.Add(new(key, "synthetic.category." + index % 4, new(part, "A" + number), new(part, "B" + number),
                    [new("de-DE", new(part, "C" + number), true, true), new("fr-FR", new(part, "D" + number), true)]));
            }
            text.Append("</sheetData></worksheet>"); data.Add(part, Encoding.UTF8.GetBytes(text.ToString()));
        }
        data["xl/workbook.xml"] = Encoding.UTF8.GetBytes(workbook.ToString(SaveOptions.DisableFormatting));
        data["xl/_rels/workbook.xml.rels"] = Encoding.UTF8.GetBytes(links.ToString(SaveOptions.DisableFormatting));
        return (data, new("synthetic-multi-owner-scale.v1", "synthetic", "en-US", rows.ToArray()));
    }
    private static void Inline(StringBuilder text, string address, string value) => text.Append("<c r=\"").Append(address).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(value).Append("</t></is></c>");

    private static (Dictionary<string, byte[]> Parts, WorkbookProfile Profile) Formatted(Dictionary<string, byte[]> fixture, WorkbookProfile profile)
    {
        var data = new Dictionary<string, byte[]>(fixture, StringComparer.Ordinal);
        void Replace(string part, string old, string value) => data[part] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data[part]).Replace(old, value, StringComparison.Ordinal));
        Replace(SheetPart, "r=\"C8\"", "r=\"C8\" s=\"1\"");
        Replace(SheetPart, "<row r=\"8\">", "<row r=\"8\" ht=\"30\" customHeight=\"1\" hidden=\"1\">");
        Replace(SheetPart, "<c r=\"B5\" t=\"inlineStr\"><is><t xml:space=\"preserve\"> Temperature Ω ≤ 80 °C — 東京 </t></is></c>", "<c r=\"B5\" t=\"inlineStr\"><is><r><rPr><i/><sz val=\"12\"/></rPr><t xml:space=\"preserve\"> Temperature Ω </t></r><r><t xml:space=\"preserve\">≤ 80 °C — 東京 </t></r></is></c>");
        Replace(SheetPart, "</sheetData>", "<row r=\"13\"><c r=\"E13\" s=\"1\"><v>45200</v></c><c r=\"F13\" t=\"b\"><v>1</v></c></row></sheetData><mergeCells count=\"1\"><mergeCell ref=\"E1:F1\"/></mergeCells><sheetProtection sheet=\"1\" objects=\"1\"/>");
        data["xl/styles.xml"] = Encoding.UTF8.GetBytes("<styleSheet xmlns=\"" + Ss + "\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd\"/></numFmts><fonts count=\"2\"><font/><font><b/><color rgb=\"FF0055AA\"/></font></fonts><fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills><borders count=\"1\"><border/></borders><cellXfs count=\"2\"><xf/><xf numFmtId=\"164\" fontId=\"1\"/></cellXfs></styleSheet>");
        data["xl/comments1.xml"] = Encoding.UTF8.GetBytes("<comments xmlns=\"" + Ss + "\"><authors><author>Synthetic reviewer</author></authors><commentList><comment ref=\"C8\" authorId=\"0\"><text><r><rPr><b/></rPr><t>Preserve review comment Ω</t></r></text></comment></commentList></comments>");
        data["xl/worksheets/_rels/sheet1.xml.rels"] = Encoding.UTF8.GetBytes("<Relationships xmlns=\"" + Rel + "\"><Relationship Id=\"comment\" Type=\"" + OfficeRel + "comments\" Target=\"../comments1.xml\"/></Relationships>");
        data[HiddenPart] = Encoding.UTF8.GetBytes("<worksheet xmlns=\"" + Ss + "\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>HIDDEN-001</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>Hidden source</t></is></c><c r=\"C1\" t=\"inlineStr\"><is><t>Verborgen</t></is></c></row></sheetData></worksheet>");
        Replace("xl/workbook.xml", "</sheets>", "<sheet name=\"Synthetic hidden owner\" sheetId=\"2\" state=\"veryHidden\" r:id=\"hiddenSheet\"/></sheets><definedNames><definedName name=\"PreservedSentinel\">'Synthetic hidden owner'!$A$1</definedName></definedNames>");
        Replace("xl/_rels/workbook.xml.rels", "</Relationships>", "<Relationship Id=\"hiddenSheet\" Type=\"" + OfficeRel + "worksheet\" Target=\"worksheets/hidden.xml\"/><Relationship Id=\"styles\" Type=\"" + OfficeRel + "styles\" Target=\"styles.xml\"/></Relationships>");
        var hidden = new TranslationRow("HIDDEN-001", "synthetic.hidden", new(HiddenPart, "A1"), new(HiddenPart, "B1"), [new("de-DE", new(HiddenPart, "C1"))]);
        return (data, profile with { Id = "synthetic-format-preservation.v1", Rows = profile.Rows.Concat([hidden]).ToArray() });
    }

    private static Dictionary<string, byte[]> MinimalPackage() => new(StringComparer.Ordinal)
    {
        ["[Content_Types].xml"] = Encoding.UTF8.GetBytes("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>"),
        ["_rels/.rels"] = Encoding.UTF8.GetBytes("<Relationships xmlns=\"" + Rel + "\"><Relationship Id=\"workbook\" Type=\"" + OfficeRel + "officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>")
    };
    private static TranslationInspectionCursor? Cursor(JsonObject page) => page["nextCursor"] is null ? null : TranslationJson.Parse<TranslationInspectionCursor>(page["nextCursor"]!.ToJsonString());
    private static TranslationEdit Edit(TranslationWorkbook wb, string key, string text, string culture = "de-DE")
    { var pair = wb.Read(key, culture); return new(key, culture, TranslationEditKind.Set, text, pair.Source.Sha256, pair.Target.Sha256, true, "Synthetic reviewer"); }
    private static TranslationCandidate Build(TranslationWorkbook wb, TranslationEdit[] edits)
    { var report = TranslationReview.Review(wb, wb, edits); return TranslationReview.BuildCandidate(wb, wb, edits, report["reviewSha256"]!.GetValue<string>()); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Mutate(IReadOnlyDictionary<string, byte[]> data, string part, Func<string, string> edit)
    { var copy = new Dictionary<string, byte[]>(data, StringComparer.Ordinal); copy[part] = Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(copy[part]))); return Pack(copy); }
    private static byte[] Pack(IReadOnlyDictionary<string, byte[]> data)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var item in data.OrderBy(p => p.Key, StringComparer.Ordinal))
            { var entry = zip.CreateEntry(item.Key); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(item.Value); }
        return output.ToArray();
    }
    private static Dictionary<string, byte[]> Unpack(byte[] bytes)
    { using var zip = new ZipArchive(new MemoryStream(bytes)); return zip.Entries.ToDictionary(e => e.FullName, e => { using var stream = e.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray(); }, StringComparer.Ordinal); }
    private static string Cell(Dictionary<string, byte[]> data, string part, string address)
    { var document = XDocument.Parse(Encoding.UTF8.GetString(data[part])); return string.Concat(document.Descendants(Ss + "c").Single(c => (string?)c.Attribute("r") == address).Descendants(Ss + "t").Select(t => t.Value)); }
    private static void CompareMasked(byte[] before, byte[] after, params string[] addresses)
    {
        var left = XDocument.Parse(Encoding.UTF8.GetString(before), LoadOptions.PreserveWhitespace); var right = XDocument.Parse(Encoding.UTF8.GetString(after), LoadOptions.PreserveWhitespace);
        foreach (var address in addresses)
        {
            var oldCell = left.Descendants(Ss + "c").Single(c => (string?)c.Attribute("r") == address); var newCell = right.Descendants(Ss + "c").Single(c => (string?)c.Attribute("r") == address);
            Check(oldCell.Attributes().Where(a => a.Name != "t").Select(a => a.ToString()).SequenceEqual(newCell.Attributes().Where(a => a.Name != "t").Select(a => a.ToString())));
            oldCell.Remove(); newCell.Remove();
        }
        Check(XNode.DeepEquals(left, right), "Independent XML preservation oracle failed.");
    }
    private static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]"); }
    private static void Reject(string code, Action action) { try { action(); } catch (BridgeException ex) { Equal(code, ex.Code); return; } throw new Exception("Expected rejection " + code); }
}
