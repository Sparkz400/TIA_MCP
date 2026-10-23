using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Translations;

if (args.Length == 3 && args[0] == "--native-read")
    return NativeReadChecks.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--category-scope")
    return CategoryScopeChecks.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--native-category-probe")
    return CategoryScopeChecks.Probe(args[1], args[2]);

const string sheetPart = "xl/worksheets/sheet1.xml";
XNamespace ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures");
var parts = Directory.GetFiles(fixtureRoot, "*", SearchOption.AllDirectories)
    .Where(p => !p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToDictionary(p => Path.GetRelativePath(fixtureRoot, p).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
var profile = TranslationJson.Parse<WorkbookProfile>(File.ReadAllText(Path.Combine(fixtureRoot, "profile.json")));
var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "expected.json")))!;
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
var identity = new EngineeringObjectReference(new(@"D:\synthetic\TranslationFixture_A.ap21", null, null, "/project/TranslationFixture_A", "project"), new("fixture-observation-1", new string('a', 64)), new string('b', 64));
var tests = new List<(string Name, Action Run)>();
void Test(string name, Action body) => tests.Add((name, body));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]"); }
void Reject(string code, Action body) { try { body(); } catch (BridgeException ex) { Equal(code, ex.Code); return; } throw new Exception("Expected rejection " + code); }
byte[] Pack(IReadOnlyDictionary<string, byte[]> data, string? duplicate = null)
{
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
    {
        foreach (var entry in data.OrderBy(p => p.Key, StringComparer.Ordinal))
        { var z = zip.CreateEntry(entry.Key); z.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var s = z.Open(); s.Write(entry.Value); }
        if (duplicate is not null) { using var s = zip.CreateEntry(duplicate).Open(); s.Write(data[duplicate]); }
    }
    return output.ToArray();
}
byte[] original = Pack(parts);
TranslationWorkbook Open(byte[]? bytes = null, WorkbookProfile? map = null, EngineeringObjectReference? project = null, WorkbookLimits? limits = null)
{ bytes ??= original; return TranslationWorkbook.Open(bytes, map ?? profile, project ?? identity, Hash(bytes), limits); }
TranslationEdit Edit(TranslationWorkbook wb, string key, string value, string culture = "de-DE", bool approved = true)
{ var pair = wb.Read(key, culture); return new(key, culture, TranslationEditKind.Set, value, pair.Source.Sha256, pair.Target.Sha256, approved, approved ? "Fixture reviewer" : null); }
TranslationCandidate Build(TranslationWorkbook wb, TranslationEdit[] edits, TranslationReviewOptions? options = null)
{ var review = TranslationReview.Review(wb, wb, edits, options); return TranslationReview.BuildCandidate(wb, wb, edits, review["reviewSha256"]!.GetValue<string>(), options); }
void HasIssue(string code, TranslationWorkbook wb, TranslationEdit[] edits, TranslationReviewOptions? options = null)
{ var review = TranslationReview.Review(wb, wb, edits, options); Assert(review["issues"]!.AsArray().Any(i => i!["code"]!.GetValue<string>() == code), "Missing issue: " + code); Equal(false, review["canWriteOfflineCandidate"]!.GetValue<bool>()); }
byte[] MutatePart(string name, Func<string, string> mutation)
{ var changed = new Dictionary<string, byte[]>(parts, StringComparer.Ordinal); changed[name] = Encoding.UTF8.GetBytes(mutation(Encoding.UTF8.GetString(changed[name]))); return Pack(changed); }
Dictionary<string, byte[]> Unzip(byte[] bytes)
{ using var stream = new MemoryStream(bytes); using var zip = new ZipArchive(stream); return zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }, StringComparer.Ordinal); }
// Independent fixture reader: only the authored inline/shared fixture forms, no production parser.
string? ExpectedCell(byte[] bytes, string address)
{
    var output = Unzip(bytes); var doc = XDocument.Parse(Encoding.UTF8.GetString(output[sheetPart]));
    var cell = doc.Descendants(ss + "c").SingleOrDefault(c => (string?)c.Attribute("r") == address);
    if (cell is null) return null;
    if ((string?)cell.Attribute("t") == "s") return XDocument.Parse(Encoding.UTF8.GetString(output["xl/sharedStrings.xml"])).Descendants(ss + "si").ElementAt(int.Parse(cell.Element(ss + "v")!.Value)).Value;
    return string.Concat(cell.Descendants(ss + "t").Select(t => t.Value));
}

const string inheritedAlarm = "###Inherited from type###";
TranslationWorkbook AlarmWorkbook(string source, string? german, string french, string grammar = "none")
{
    var data = new Dictionary<string, byte[]>(parts, StringComparer.Ordinal);
    var doc = XDocument.Parse(Encoding.UTF8.GetString(data[sheetPart]), LoadOptions.PreserveWhitespace);
    foreach (var (address, text) in new[] { ("B2", source), ("C2", german), ("D2", french) })
    {
        var cell = doc.Descendants(ss + "c").Single(c => (string?)c.Attribute("r") == address);
        if (text is null) { cell.Remove(); continue; }
        cell.SetAttributeValue("t", "inlineStr"); cell.ReplaceNodes(new XElement(ss + "is", new XElement(ss + "t", text)));
    }
    data[sheetPart] = Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    var mapping = profile.Rows[0] with { AlarmTextPolicy = "instance-inheritance-v1", TokenGrammar = grammar,
        Targets = profile.Rows[0].Targets.Select(t => t with { MaxUtf16Length = null, AllowCreateMissing = true }).ToArray() };
    return Open(Pack(data), profile with { Rows = [mapping] });
}
Test("old profiles omit alarm policy and preserve their serialized identity", () => {
    var serialized = TranslationJson.Node(profile).ToJsonString();
    Assert(!serialized.Contains("alarmTextPolicy"));
    var wb = Open(); var review = TranslationReview.Review(wb, wb, []);
    Assert(!review.ContainsKey("alarmInheritance"));
    Assert(TranslationInspection.Page(wb)["rows"]!.AsArray().All(r => !r!.AsObject().ContainsKey("alarmInheritance")));
});
Test("unknown supplied alarm policy refuses capture", () => {
    Reject("UnsupportedWorkbookProfile", () => Open(map: profile with { Rows = [profile.Rows[0] with { AlarmTextPolicy = "invented" }] }));
});
Test("mapped target count enforces the published profile bound before reading cells", () => {
    Reject("UnsupportedWorkbookProfile", () => Open(map: profile with { Rows = [profile.Rows[0] with { Targets = Enumerable.Repeat(profile.Rows[0].Targets[0], 1001).ToArray() }] }));
});
Test("all inherited languages retain exact original workbook on no-op", () => {
    var wb = AlarmWorkbook(inheritedAlarm, inheritedAlarm, inheritedAlarm);
    var review = TranslationReview.Review(wb, wb, []);
    Equal("inherited", review["alarmInheritance"]![0]!["after"]!["status"]!.GetValue<string>());
    Assert(wb.OriginalBytes().AsSpan().SequenceEqual(Build(wb, []).Bytes));
    Equal(3, TranslationInspection.Page(wb)["rows"]![0]!["alarmInheritance"]!["inheritedFields"]!.GetValue<int>());
});
Test("mixed inheritance is visible and blocks even a no-op candidate", () => {
    var wb = AlarmWorkbook(inheritedAlarm, "Text", inheritedAlarm);
    HasIssue("AlarmInheritanceMixed", wb, []);
    Reject("TranslationReviewBlocked", () => Build(wb, []));
    Equal("mixed", TranslationInspection.Page(wb)["rows"]![0]!["alarmInheritance"]!["status"]!.GetValue<string>());
});
Test("all mapped languages participate including an unedited third language", () => {
    var wb = AlarmWorkbook(inheritedAlarm, inheritedAlarm, "Non-inherited");
    HasIssue("AlarmInheritanceMixed", wb, [Edit(wb, "K-001", inheritedAlarm)]);
});
Test("single inherited target cannot silently become literal or empty", () => {
    var wb = AlarmWorkbook(inheritedAlarm, inheritedAlarm, inheritedAlarm);
    HasIssue("AlarmInheritanceMixed", wb, [Edit(wb, "K-001", "Override")]);
    var edit = Edit(wb, "K-001", "unused") with { Kind = TranslationEditKind.Clear, Value = null };
    HasIssue("AlarmInheritanceMixed", wb, [edit]);
});
Test("missing alarm language is unknown not an empty literal", () => {
    var wb = AlarmWorkbook("Text", null, "Texte");
    HasIssue("AlarmLanguageMissing", wb, []);
    var repaired = Build(wb, [Edit(wb, "K-001", "Text DE")]);
    Equal("Text DE", ExpectedCell(repaired.Bytes, "C2"));
});
Test("repair of a mapped inheritance contradiction preserves every peer", () => {
    var wb = AlarmWorkbook(inheritedAlarm, "Override", inheritedAlarm);
    var candidate = Build(wb, [Edit(wb, "K-001", inheritedAlarm)]);
    foreach (string cell in new[] { "B2", "C2", "D2" }) Equal(inheritedAlarm, ExpectedCell(candidate.Bytes, cell));
    var before = Unzip(wb.OriginalBytes()); var after = Unzip(candidate.Bytes);
    foreach (string part in before.Keys.Where(k => k != sheetPart)) Assert(before[part].AsSpan().SequenceEqual(after[part]));
    var oldSheet = XDocument.Parse(Encoding.UTF8.GetString(before[sheetPart]), LoadOptions.PreserveWhitespace);
    var newSheet = XDocument.Parse(Encoding.UTF8.GetString(after[sheetPart]), LoadOptions.PreserveWhitespace);
    oldSheet.Descendants(ss+"c").Single(c => (string?)c.Attribute("r")=="C2").Remove();
    newSheet.Descendants(ss+"c").Single(c => (string?)c.Attribute("r")=="C2").Remove();
    Assert(XNode.DeepEquals(oldSheet, newSheet));
    Equal(false, candidate.Manifest["nativeImportAllowed"]!.GetValue<bool>());
});
Test("ordinary supplied alarm texts retain independent token and approval guards", () => {
    var wb = AlarmWorkbook("Alarm {0}", "Alarm {0}", "Alarme {0}", "brace-literals-v1");
    HasIssue("PlaceholderMismatch", wb, [Edit(wb, "K-001", "Alarm {1}")]);
    HasIssue("UnapprovedTranslationEdit", wb, [Edit(wb, "K-001", "Neu {0}", approved: false)]);
    Equal("Neu {0}", ExpectedCell(Build(wb, [Edit(wb, "K-001", "Neu {0}")]).Bytes, "C2"));
});
Test("alarm policy cannot certify unknown placeholder grammar", () => {
    var wb = AlarmWorkbook("Alarm", "Alarm", "Alarme", "unknown");
    HasIssue("PlaceholderGrammarUnqualified", wb, [Edit(wb, "K-001", "Neu")]);
});
Test("source and target transition still requires a fresh source review", () => {
    var wb = AlarmWorkbook(inheritedAlarm, inheritedAlarm, inheritedAlarm);
    HasIssue("SourceAndTargetEditConflict", wb, [Edit(wb,"K-001","Alarm","en-US"), Edit(wb,"K-001","Alarm DE"), Edit(wb,"K-001","Alarme","fr-FR")], new(UpdateSourceLanguage:true));
});
Test("marker comparison is exact and never trims or case folds", () => {
    var wb = AlarmWorkbook(inheritedAlarm, inheritedAlarm+" ", inheritedAlarm);
    HasIssue("AlarmInheritanceMixed", wb, []);
    wb = AlarmWorkbook(inheritedAlarm.ToLowerInvariant(), "Literal", "Texte");
    Equal("literal", TranslationReview.Review(wb,wb,[])["alarmInheritance"]![0]!["after"]!["status"]!.GetValue<string>());
});

Test("independent bilingual oracle and missing/empty distinction", () => {
    var wb = Open(); var keys = oracle["keys"]!.AsArray(); var expected = oracle["initialGerman"]!.AsArray();
    for (int i = 0; i < keys.Count; i++) Equal(expected[i]?.GetValue<string>(), wb.Read(keys[i]!.GetValue<string>(), "de-DE").Target.Text);
    Equal("missing", wb.Read("K-002", "de-DE").Target.State); Equal("empty", wb.Read("K-006", "de-DE").Target.State);
    Assert(wb.Read("K-002", "de-DE").Target.Sha256 != wb.Read("K-006", "de-DE").Target.Sha256);
    Equal(false, wb.NativeQualified);
});
Test("no-op returns original ZIP bytes exactly", () => { var wb = Open(); Assert(original.AsSpan().SequenceEqual(Build(wb, []).Bytes)); });
Test("original and candidate byte arrays are defensive copies", () => { var wb = Open(); var a = wb.OriginalBytes(); a[0] = 0; Equal(Hash(original), Hash(wb.OriginalBytes())); var c = Build(wb, []); var b = c.Bytes; b[0] = 0; Equal(Hash(original), Hash(c.Bytes)); });
Test("profile maps are captured defensively", () => { var p = profile with { Rows = profile.Rows.ToArray() }; var wb = Open(map: p); p.Rows[0] = p.Rows[0] with { NativeKey = "changed" }; Equal("K-001", wb.Profile.Rows[0].NativeKey); var copy = wb.Profile; copy.Rows[0] = copy.Rows[0] with { NativeKey = "again" }; Equal("K-001", wb.Profile.Rows[0].NativeKey); });
Test("accepted lexical row numbers remain editable without rewriting row identity", () => {
    foreach (string lexical in new[] { "0002", " +2 " }) {
        var bytes = MutatePart(sheetPart, s => s.Replace("<row r=\"2\">", "<row r=\"" + lexical + "\">"));
        var wb = Open(bytes); var candidate = Build(wb, [Edit(wb, "K-001", "Neu")]);
        Equal("Neu", ExpectedCell(candidate.Bytes, "C2"));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(Unzip(candidate.Bytes)[sheetPart]));
        Equal(lexical, (string?)doc.Root!.Element(ss + "sheetData")!.Elements(ss + "row").ElementAt(1).Attribute("r"));
    }
});
Test("opaque extension rows cannot enter the worksheet edit index", () => {
    var bytes = MutatePart(sheetPart, s => s.Replace(" Ω </m:opaque>", " Ω <row r=\"2\" vendor=\"keep\"/></m:opaque>"));
    var wb = Open(bytes); var candidate = Build(wb, [Edit(wb, "K-007", "Neu")]);
    Equal("Neu", ExpectedCell(candidate.Bytes, "C8"));
    var before = XDocument.Parse(Encoding.UTF8.GetString(Unzip(bytes)[sheetPart]));
    var after = XDocument.Parse(Encoding.UTF8.GetString(Unzip(candidate.Bytes)[sheetPart]));
    Assert(XNode.DeepEquals(before.Root!.Element(ss + "extLst"), after.Root!.Element(ss + "extLst")));
});
Test("worksheet capture refuses duplicate or nested-only sheet data", () => {
    Reject("UnsupportedWorkbookProfile", () => Open(MutatePart(sheetPart, s => s.Replace("</sheetData>", "</sheetData><sheetData/>"))));
    Reject("UnsupportedWorkbookProfile", () => Open(MutatePart(sheetPart, s => s.Replace("<sheetData>", "<m:wrapper><sheetData>").Replace("</sheetData>", "</sheetData></m:wrapper>"))));
});
Test("opaque inline comments and instructions are retained and block edits", () => {
    foreach (string value in new[] { "<is><!--retain--><t>Neu erstelltes Objekt</t></is>", "<is><t><!--retain-->Neu erstelltes Objekt</t></is>", "<is><t><?vendor retain?>Neu erstelltes Objekt</t></is>" }) {
        var bytes = MutatePart(sheetPart, s => s.Replace("<is><t>Neu erstelltes Objekt</t></is>", value));
        var wb = Open(bytes); Assert(bytes.AsSpan().SequenceEqual(Build(wb, []).Bytes));
        HasIssue("UnsupportedWorkbookProfile", wb, [Edit(wb, "K-008-current", "Neu")]);
        Reject("TranslationReviewBlocked", () => Build(wb, [Edit(wb, "K-008-current", "Neu")]));
    }
});
Test("shared string and index annotations cannot disappear during conversion", () => {
    var bytes = MutatePart("xl/sharedStrings.xml", s => s.Replace("<si>", "<si><!--retain-->"));
    var wb = Open(bytes); Assert(bytes.AsSpan().SequenceEqual(Build(wb, []).Bytes));
    HasIssue("SharedStringEditUnsupported", wb, [Edit(wb, "K-007", "Neu")]);
    foreach (string index in new[] { "<v><!--retain-->0</v>", "<v m:sentinel=\"retain\">0</v>", "<v><?vendor retain?>0</v>" }) {
        var annotated = MutatePart(sheetPart, s => s.Replace("<v>0</v>", index));
        wb = Open(annotated); Assert(annotated.AsSpan().SequenceEqual(Build(wb, []).Bytes));
        HasIssue("UnsupportedWorkbookProfile", wb, [Edit(wb, "K-007", "Neu")]);
    }
});
Test("mapped text and shared indices cannot flatten nested opaque elements", () => {
    Reject("UnsupportedWorkbookProfile", () => Open(MutatePart(sheetPart, s => s.Replace("<t>Neu erstelltes Objekt</t>", "<t><m:opaque>Neu</m:opaque> erstelltes Objekt</t>"))));
    Reject("WorkbookIntegrityFailure", () => Open(MutatePart(sheetPart, s => s.Replace("<v>0</v>", "<v><m:opaque>0</m:opaque></v>"))));
});
Test("UTF8 BOM and UTF16 worksheet encodings preserve no-op bytes and edited values", () => {
    foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) }) {
        var changed = new Dictionary<string, byte[]>(parts);
        string xml = Encoding.UTF8.GetString(parts[sheetPart]).Replace("encoding=\"utf-8\"", "encoding=\"" + encoding.WebName + "\"");
        changed[sheetPart] = encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray();
        var bytes = Pack(changed); var wb = Open(bytes);
        Assert(bytes.AsSpan().SequenceEqual(Build(wb, []).Bytes));
        var candidate = Build(wb, [Edit(wb, "K-007", "Neu 東京")]);
        Equal("Neu 東京", Open(candidate.Bytes).Read("K-007", "de-DE").Target.Text);
        foreach (var part in changed.Where(p => p.Key != sheetPart)) Assert(part.Value.AsSpan().SequenceEqual(Unzip(candidate.Bytes)[part.Key]));
    }
});
Test("cell-level annotations survive changes to the cell's text container", () => {
    var bytes = MutatePart(sheetPart, s => s.Replace("<c r=\"C9\" t=\"inlineStr\">", "<c r=\"C9\" t=\"inlineStr\"><!--retain--><?vendor retain?>"));
    var wb = Open(bytes); var after = Build(wb, [Edit(wb, "K-008-current", "Neu")]);
    var cell = XDocument.Parse(Encoding.UTF8.GetString(Unzip(after.Bytes)[sheetPart])).Descendants(ss + "c").Single(c => (string?)c.Attribute("r") == "C9");
    Equal("retain", cell.Nodes().OfType<XComment>().Single().Value);
    Equal("retain", cell.Nodes().OfType<XProcessingInstruction>().Single().Data);
    Equal("Neu", ExpectedCell(after.Bytes, "C9"));
});
Test("reconciliation cannot hide removed or introduced text annotations", () => {
    var annotated = Open(MutatePart(sheetPart, s => s.Replace("<t>Neu erstelltes Objekt</t>", "<t><!--retain-->Neu erstelltes Objekt</t>")));
    var edited = Open(MutatePart(sheetPart, s => s.Replace("<t>Neu erstelltes Objekt</t>", "<t>Changed</t>")));
    Reject("UnsupportedWorkbookProfile", () => TranslationReconciliation.Reconcile(annotated, edited, annotated));
    var introduced = Open(MutatePart(sheetPart, s => s.Replace("<t>Neu erstelltes Objekt</t>", "<t><?vendor retain?>Changed</t>")));
    Reject("UnsupportedWorkbookProfile", () => TranslationReconciliation.Reconcile(Open(), introduced, Open()));
});
Test("reconciliation checks non-text nodes of changed existing cells", () => {
    var baseline = Open(MutatePart(sheetPart, s => s.Replace("<c r=\"C9\" t=\"inlineStr\">", "<c r=\"C9\" t=\"inlineStr\"><!--retain--><?vendor retain?>")));
    var deleted = Open(MutatePart(sheetPart, s => s.Replace("<t>Neu erstelltes Objekt</t>", "<t>Changed</t>")));
    Reject("TranslationPreservationFailure", () => TranslationReconciliation.Reconcile(baseline, deleted, baseline));
    var added = Open(MutatePart(sheetPart, s => s.Replace("<c r=\"C9\" t=\"inlineStr\">", "<c r=\"C9\" t=\"inlineStr\"><!--added-->").Replace("<t>Neu erstelltes Objekt</t>", "<t>Changed</t>")));
    Reject("TranslationPreservationFailure", () => TranslationReconciliation.Reconcile(Open(), added, Open()));
});
Test("reconciliation cannot attach unreviewed metadata to newly created cells", () => {
    var wb = Open(); var candidate = Build(wb, [Edit(wb, "K-002", "Neu")]);
    Equal(1, TranslationReconciliation.Reconcile(wb, Open(candidate.Bytes), wb)["proposals"]!.AsArray().Count);
    foreach (bool attribute in new[] { false, true }) {
        var data = Unzip(candidate.Bytes);
        string xml = Encoding.UTF8.GetString(data[sheetPart]);
        const string marker = "<c r=\"C3\" t=\"inlineStr\">";
        Assert(xml.Contains(marker));
        data[sheetPart] = Encoding.UTF8.GetBytes(xml.Replace(marker, attribute ? "<c r=\"C3\" t=\"inlineStr\" s=\"999\">" : marker + "<!--unreviewed-->"));
        Reject("TranslationPreservationFailure", () => TranslationReconciliation.Reconcile(wb, Open(Pack(data)), wb));
    }
});
Test("approved target overlay preserves unrelated parts and shared peers", () => {
    var wb = Open(); var candidate = Build(wb, [Edit(wb, "K-002", "Kühlung aktiviert"), Edit(wb, "K-007", "Betriebsbereit")]);
    Equal("Kühlung aktiviert", ExpectedCell(candidate.Bytes, "C3")); Equal("Betriebsbereit", ExpectedCell(candidate.Bytes, "C8"));
    Equal("Bereit", ExpectedCell(candidate.Bytes, "C2")); Equal(oracle["expectedFrench"]!.GetValue<string>(), ExpectedCell(candidate.Bytes, "D2"));
    foreach (var p in Unzip(original)) if (p.Key != sheetPart) Assert(p.Value.AsSpan().SequenceEqual(Unzip(candidate.Bytes)[p.Key]), "Changed unrelated part " + p.Key);
    var oldDoc = XDocument.Parse(Encoding.UTF8.GetString(Unzip(original)[sheetPart]), LoadOptions.PreserveWhitespace);
    var newDoc = XDocument.Parse(Encoding.UTF8.GetString(Unzip(candidate.Bytes)[sheetPart]), LoadOptions.PreserveWhitespace);
    foreach (string address in new[] { "C3", "C8" }) { oldDoc.Descendants(ss + "c").Where(c => (string?)c.Attribute("r") == address).Remove(); newDoc.Descendants(ss + "c").Where(c => (string?)c.Attribute("r") == address).Remove(); }
    Assert(XNode.DeepEquals(oldDoc.Root, newDoc.Root), "Independent unedited XML comparison failed");
    foreach (string flag in new[] { "nativeQualified", "nativeExecuted", "nativeImportAllowed" }) Equal(false, candidate.Manifest[flag]!.GetValue<bool>());
    Equal(true, candidate.Manifest["preservationVerified"]!.GetValue<bool>());
});
Test("literal formula-looking text is never executed", () => { var wb = Open(); Equal("=1+1", wb.Read("K-009", "de-DE").Target.Text); Equal("=HYPERLINK(\"untrusted\")", ExpectedCell(Build(wb, [Edit(wb, "K-009", "=HYPERLINK(\"untrusted\")")]).Bytes, "C10")); });
Test("Unicode spaces combining marks and CRLF survive", () => { var wb = Open(); string text = " Ω e\u0301 é 東京 😀\r\n\t "; var after = Open(Build(wb, [Edit(wb, "K-004", text)]).Bytes); Equal(text, after.Read("K-004", "de-DE").Target.Text); Equal("Zeile 1\r\nZeile 2\tFertig", after.Read("K-005", "de-DE").Target.Text); });
Test("OOXML control and literal escape sequences preserve exact values", () => { var wb = Open(); string text = "_x0041_\0\u0001\r\n"; var after = Open(Build(wb, [Edit(wb, "K-011", text)]).Bytes); Equal(text, after.Read("K-011", "de-DE").Target.Text); });
Test("isolated surrogate is blocked", () => { var wb = Open(); HasIssue("InvalidTranslationText", wb, [Edit(wb, "K-011", "bad\uD800")]); });
Test("review completeness excludes no hidden global claims", () => { var wb = Open(); var report = TranslationReview.Review(wb, wb, []); var de = report["completeness"]!.AsArray().Single(r => r!["culture"]!.GetValue<string>() == "de-DE")!; Equal(11, de["mapped"]!.GetValue<int>()); Equal(1, de["missing"]!.GetValue<int>()); Equal(1, de["empty"]!.GetValue<int>()); Equal("explicit-profile-rows-only", de["scope"]!.GetValue<string>()); });
Test("unapproved suggestion cannot produce candidate", () => { var wb = Open(); HasIssue("UnapprovedTranslationEdit", wb, [Edit(wb, "K-001", "Geändert", approved: false)]); Reject("TranslationReviewBlocked", () => Build(wb, [Edit(wb, "K-001", "Geändert", approved: false)])); });
Test("approval digest binds edited text and reviewer", () => { var wb = Open(); var edits = new[] { Edit(wb, "K-001", "Neu") }; var old = TranslationReview.Review(wb, wb, edits); edits[0] = edits[0] with { Value = "Anders" }; Reject("ReviewApprovalMismatch", () => TranslationReview.BuildCandidate(wb, wb, edits, old["reviewSha256"]!.GetValue<string>())); });
Test("source mode is explicit and peer target remains untouched", () => { var wb = Open(); var edit = Edit(wb, "K-001", "Prepared", "en-US"); HasIssue("SourceEditNotApproved", wb, [edit]); var outBytes = Build(wb, [edit], new(UpdateSourceLanguage: true)).Bytes; Equal("Prepared", ExpectedCell(outBytes, "B2")); Equal("Bereit", ExpectedCell(outBytes, "C2")); });
Test("source and target expected states are independently checked", () => { var wb = Open(); var edit = Edit(wb, "K-001", "Neu"); HasIssue("SourceTextConflict", wb, [edit with { ExpectedSourceSha256 = new string('0', 64) }]); HasIssue("TargetTextConflict", wb, [edit with { ExpectedTargetSha256 = new string('0', 64) }]); });
Test("stale native key does not fall back to text or row name", () => { var wb = Open(); HasIssue("StaleTextKey", wb, [Edit(wb, "K-001", "Neu") with { NativeKey = "K-008-old" }]); var rows = profile.Rows.ToArray(); rows[7] = rows[7] with { NativeKey = "K-008-old" }; Reject("StaleTextKey", () => Open(map: profile with { Rows = rows })); });
Test("changed project observation and content are rejected", () => { var wb = Open(); Reject("IdentityMismatch", () => TranslationReview.Review(wb, Open(project: identity with { Object = identity.Object with { ProjectPath = @"D:\other.ap21" } }), [])); Reject("StaleObservation", () => TranslationReview.Review(wb, Open(project: identity with { Snapshot = identity.Snapshot with { ObservationId = "later" } }), [])); Reject("StaleContent", () => TranslationReview.Review(wb, Open(project: identity with { ContentSha256 = new string('c', 64) }), [])); });
Test("changed workbook or profile cannot silently rebase", () => { var wb = Open(); var changed = MutatePart(sheetPart, s => s.Replace("Cooling enabled", "Cooling disabled")); Reject("StaleWorkbook", () => TranslationReview.Review(wb, Open(changed), [])); Reject("StaleWorkbook", () => TranslationReview.Review(wb, Open(map: profile with { Id = "v2" }), [])); });
Test("provided workbook digest must be exact", () => Reject("StaleWorkbook", () => TranslationWorkbook.Open(original, profile, identity, new string('0', 64))));
Test("keep is explicit no-op and needs no approval", () => { var wb = Open(); var edit = Edit(wb, "K-001", "unused") with { Kind = TranslationEditKind.Keep, Value = null, Approved = false, Reviewer = null }; Assert(original.AsSpan().SequenceEqual(Build(wb, [edit]).Bytes)); });
Test("empty set differs from clear and missing approval remains keep", () => { var wb = Open(); HasIssue("InvalidTranslationEdit", wb, [Edit(wb, "K-001", "")]); var clear = Edit(wb, "K-001", "unused") with { Kind = TranslationEditKind.Clear, Value = null }; Equal("", Open(Build(wb, [clear]).Bytes).Read("K-001", "de-DE").Target.Text); Equal("missing", Open(Build(wb, []).Bytes).Read("K-002", "de-DE").Target.State); });
Test("clear and missing-cell creation obey profile", () => { var rows = profile.Rows.ToArray(); rows[0] = rows[0] with { Targets = rows[0].Targets.Select(t => t with { AllowClear = false }).ToArray() }; rows[1] = rows[1] with { Targets = rows[1].Targets.Select(t => t with { AllowCreateMissing = false }).ToArray() }; var wb = Open(map: profile with { Rows = rows }); HasIssue("ClearSemanticsUnqualified", wb, [Edit(wb, "K-001", "unused") with { Kind = TranslationEditKind.Clear, Value = null }]); HasIssue("MissingCellEditUnsupported", wb, [Edit(wb, "K-002", "Neu")]); });
Test("placeholder omission addition and multiplicity mismatch are blocked", () => { var wb = Open(); foreach (var text in new[] { "Achse {0}", "Achse {0}: {1:F1} {2}", "Achse {0}: {0}: {1:F1}" }) HasIssue("PlaceholderMismatch", wb, [Edit(wb, "K-003", text)]); Equal("Achse {1:F1} mm — {0}", Open(Build(wb, [Edit(wb, "K-003", "Achse {1:F1} mm — {0}")]).Bytes).Read("K-003", "de-DE").Target.Text); });
Test("unknown token grammar blocks edits", () => { var rows = profile.Rows.ToArray(); rows[0] = rows[0] with { TokenGrammar = "unknown" }; var wb = Open(map: profile with { Rows = rows }); HasIssue("PlaceholderGrammarUnqualified", wb, [Edit(wb, "K-001", "Neu")]); });
Test("glossary version scope and approved terminology are checked", () => { var wb = Open(); var options = new TranslationReviewOptions(GlossaryId: "glossary.v1", Glossary: [new("Cooling", "Kühlung", "de-DE", "synthetic.comment")]); HasIssue("GlossaryMismatch", wb, [Edit(wb, "K-002", "Kälte aktiviert")], options); Equal("Kühlung aktiviert", ExpectedCell(Build(wb, [Edit(wb, "K-002", "Kühlung aktiviert")], options).Bytes, "C3")); });
Test("UTF16 profile limit blocks oversize text", () => { var wb = Open(); HasIssue("TextLengthExceeded", wb, [Edit(wb, "K-001", new string('x', 21))]); });
Test("rich text is readable and preserved but editing refused", () => { var wb = Open(); Assert(wb.Read("K-010", "de-DE").Target.RichText); HasIssue("RichTextEditUnsupported", wb, [Edit(wb, "K-010", "Flach")]); });
Test("shared string conversion requires profile consent", () => { var wb = Open(map: profile with { AllowSharedStringToInline = false }); HasIssue("SharedStringEditUnsupported", wb, [Edit(wb, "K-007", "Neu")]); });
Test("shared phonetic opaque metadata is preserved and edit refused", () => { var bytes = MutatePart("xl/sharedStrings.xml", s => s.Replace("</si>", "<phoneticPr fontId=\"0\"/></si>")); var wb = Open(bytes); Assert(bytes.AsSpan().SequenceEqual(Build(wb, []).Bytes)); HasIssue("SharedStringEditUnsupported", wb, [Edit(wb, "K-007", "Neu")]); });
Test("malformed relationship part location gives structured refusal", () => { var changed = new Dictionary<string, byte[]>(parts) { ["foo.rels"] = parts["_rels/.rels"] }; Reject("WorkbookIntegrityFailure", () => Open(Pack(changed))); });
Test("source edit explicitly invalidates downstream translation reviews", () => { var wb = Open(); var report = TranslationReview.Review(wb, wb, [Edit(wb, "K-001", "Prepared", "en-US")], new(UpdateSourceLanguage: true)); Equal(true, report["sourceChangesRequireFreshExport"]!.GetValue<bool>()); Equal(true, report["edits"]![0]!["invalidatesTranslationReviews"]!.GetValue<bool>()); });
Test("limits cannot be enlarged beyond fixed safety ceilings", () => { Reject("InvalidWorkbookLimits", () => Open(limits: new(MaximumExpandedBytes: long.MaxValue))); Reject("WorkbookLimit", () => Open(limits: new(MaximumXmlBytes: 50))); Reject("WorkbookLimit", () => Open(limits: new(MaximumXmlNodes: 5))); });
Test("duplicate edits keys and locations rejected", () => { var wb = Open(); HasIssue("DuplicateTranslationEdit", wb, [Edit(wb, "K-001", "Neu"), Edit(wb, "K-001", "Neu")]); var rows = profile.Rows.ToArray(); rows[1] = rows[1] with { NativeKey = rows[0].NativeKey }; Reject("DuplicateNativeKey", () => Open(map: profile with { Rows = rows })); rows = profile.Rows.ToArray(); rows[1] = rows[1] with { SourceCell = rows[0].SourceCell }; Reject("UnsupportedWorkbookProfile", () => Open(map: profile with { Rows = rows })); });
Test("claimed native-qualified profile is refused", () => Reject("UnsupportedWorkbookProfile", () => Open(map: profile with { Kind = "native-qualified" })));
Test("duplicate JSON keys are rejected", () => Reject("DuplicateJsonKey", () => TranslationJson.Parse<WorkbookProfile>("{\"id\":\"a\",\"id\":\"b\"}")));
Test("ZIP traversal absolute percent and backslash parts rejected", () => { foreach (string name in new[] { "../bad", "/absolute", "C:/bad", "safe/../../bad", "safe\\bad", "%2e%2e/bad" }) { var changed = new Dictionary<string, byte[]>(parts) { [name] = [1] }; Reject("UnsafePackagePath", () => Open(Pack(changed))); } });
Test("duplicate package entries rejected", () => Reject("DuplicatePackagePart", () => Open(Pack(parts, sheetPart))));
Test("compressed expanded entry cell and XML depth bounds enforced", () => { Reject("WorkbookLimit", () => Open(limits: new(MaximumCompressedBytes: 50))); Reject("WorkbookLimit", () => Open(limits: new(MaximumExpandedBytes: 100))); Reject("WorkbookLimit", () => Open(limits: new(MaximumEntries: 2))); Reject("WorkbookLimit", () => Open(limits: new(MaximumCells: 3))); Reject("WorkbookLimit", () => Open(limits: new(MaximumXmlDepth: 2))); });
Test("ZIP CRC corruption rejected even with correct outer SHA", () => { var bytes = original.ToArray(); int at = -1; for (int i = 0; i < bytes.Length - 46; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x02014b50) { at = i; break; } Assert(at >= 0); bytes[at + 16] ^= 1; Reject("WorkbookIntegrityFailure", () => Open(bytes)); });
Test("truncated ZIP rejected", () => Reject("WorkbookIntegrityFailure", () => Open(original[..^7])));
Test("encrypted ZIP declaration refused", () => { var bytes = original.ToArray(); for (int i = 0; i < bytes.Length - 46; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x02014b50) { bytes[i + 8] |= 1; break; } Reject("UnsupportedWorkbookProfile", () => Open(bytes)); });
Test("DTD UTF8 UTF16 and malformed XML rejected", () => { foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) }) { var changed = new Dictionary<string, byte[]>(parts); string xml = "<!DOCTYPE worksheet [<!ENTITY x 'bad'>]><worksheet xmlns='" + ss + "'>&x;</worksheet>"; changed[sheetPart] = encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray(); Reject("WorkbookXmlRejected", () => Open(Pack(changed))); } Reject("WorkbookXmlRejected", () => Open(MutatePart(sheetPart, _ => "<broken>"))); });
Test("external relationships and package escape rejected", () => { Reject("ExternalWorkbookRelationship", () => Open(MutatePart("_rels/.rels", s => s.Replace("Target=\"xl/workbook.xml\"", "TargetMode=\"External\" Target=\"https://example.invalid/\"")))); Reject("UnsafePackagePath", () => Open(MutatePart("_rels/.rels", s => s.Replace("xl/workbook.xml", "../outside.xml")))); });
Test("macro content types and formula cells refused", () => { Reject("ActiveWorkbookContent", () => Open(MutatePart("[Content_Types].xml", s => s.Replace("sheet.main+xml", "macroEnabled.main+xml")))); Reject("WorkbookFormulaRejected", () => Open(MutatePart(sheetPart, s => s.Replace("<c r=\"C7\"/>", "<c r=\"C7\"><f>1+1</f><v>2</v></c>")))); });
Test("duplicate cells and inconsistent row addresses refused", () => { Reject("WorkbookIntegrityFailure", () => Open(MutatePart(sheetPart, s => s.Replace("<c r=\"C7\"/>", "<c r=\"C7\"/><c r=\"C7\"/>")))); Reject("WorkbookIntegrityFailure", () => Open(MutatePart(sheetPart, s => s.Replace("<c r=\"C7\"/>", "<c r=\"C8\"/>")))); });
Test("missing package relationship targets refused", () => Reject("WorkbookIntegrityFailure", () => Open(MutatePart("_rels/.rels", s => s.Replace("xl/workbook.xml", "xl/missing.xml")))));
Test("immutable output paths preserve existing bytes", () => { string directory = Path.Combine(Path.GetTempPath(), "translation-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); try { string workbook = Path.Combine(directory, "out.xlsx"), manifest = Path.Combine(directory, "out.json"); File.WriteAllText(workbook, "sentinel"); Reject("OutputAlreadyExists", () => Build(Open(), []).WriteNew(workbook, manifest)); Equal("sentinel", File.ReadAllText(workbook)); Assert(!File.Exists(manifest)); } finally { Directory.Delete(directory, true); } });
Test("output artifacts have exact hashes and immutable receipts", () => { string directory = Path.Combine(Path.GetTempPath(), "translation-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); try { var wb = Open(); var candidate = Build(wb, [Edit(wb, "K-002", "Kühlung aktiviert")]); string output = Path.Combine(directory, "candidate.xlsx"), receipt = Path.Combine(directory, "candidate.json"); candidate.WriteNew(output, receipt); Equal(Hash(File.ReadAllBytes(output)), JsonNode.Parse(File.ReadAllText(receipt))!["outputSha256"]!.GetValue<string>()); Reject("OutputAlreadyExists", () => candidate.WriteNew(output, receipt)); } finally { Directory.Delete(directory, true); } });
Test("review and candidate are deterministic across reordered edits", () => { var wb = Open(); var edits = new[] { Edit(wb, "K-002", "Kühlung aktiviert"), Edit(wb, "K-007", "Betriebsbereit") }; var first = Build(wb, edits); var second = Build(wb, edits.Reverse().ToArray()); Equal(Hash(first.Bytes), Hash(second.Bytes)); Equal(CanonicalJson.Serialize(first.Manifest), CanonicalJson.Serialize(second.Manifest)); });

ExpansionChecks.Register(tests, parts, profile, identity);

int failures = 0;
foreach (var test in tests) { try { test.Run(); Console.WriteLine("PASS " + test.Name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex); } }
Console.WriteLine($"{tests.Count - failures}/{tests.Count} translation checks passed; native execution: false");
if (failures == 0 && args.Length == 2 && args[0] == "--demo")
{
    string directory = Path.GetFullPath(args[1]);
    if (Directory.Exists(directory)) throw new Exception("Demo output directory already exists.");
    Directory.CreateDirectory(directory);
    using (var s = new FileStream(Path.Combine(directory, "original.xlsx"), FileMode.CreateNew)) s.Write(original);
    var wb = Open(); var edits = new[] { Edit(wb, "K-002", "Kühlung aktiviert"), Edit(wb, "K-007", "Betriebsbereit") };
    Build(wb, edits).WriteNew(Path.Combine(directory, "candidate.xlsx"), Path.Combine(directory, "candidate.manifest.json"));
    File.WriteAllText(Path.Combine(directory, "test-receipt.json"), new JsonObject { ["checks"] = tests.Count, ["passed"] = tests.Count, ["nativeExecuted"] = false, ["fixtureKind"] = "synthetic-not-native-qualified" }.ToJsonString());
    Console.WriteLine("Demo artifacts: " + directory);
}
if (failures == 0 && args.Length == 2 && args[0] == "--evidence")
    ExpansionChecks.WriteEvidence(Path.GetFullPath(args[1]), parts, profile, identity, tests.Count);
return failures == 0 ? 0 : 1;
