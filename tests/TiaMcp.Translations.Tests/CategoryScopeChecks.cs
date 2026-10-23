using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Translations;

internal static class CategoryScopeChecks
{
    internal static int Probe(string casePath, string outputPath)
    {
        if (File.Exists(outputPath)) throw new Exception("Use a new probe verification output path.");
        var manifest = JsonNode.Parse(File.ReadAllText(casePath))!;
        var workbooks = new Dictionary<string, TranslationWorkbook>(StringComparer.Ordinal);
        var reads = new JsonArray();
        int totalRows = 0;
        foreach (var item in manifest["cases"]!.AsArray())
        {
            string mode = item!["mode"]!.GetValue<string>();
            byte[] bytes = File.ReadAllBytes(item["workbookPath"]!.GetValue<string>());
            var profile = TranslationJson.Parse<WorkbookProfile>(File.ReadAllText(item["profilePath"]!.GetValue<string>()));
            var identity = TranslationJson.Parse<EngineeringObjectReference>(item["identity"]!.ToJsonString());
            var workbook = TranslationWorkbook.Open(bytes, profile, identity, item["workbookSha256"]!.GetValue<string>());
            foreach (var row in item["rows"]!.AsArray())
            {
                var read = workbook.Read(row!["nativeKey"]!.GetValue<string>(), "en-GB");
                if (read.Source.Text != row["source"]!.GetValue<string>() || read.Target.Text != row["target"]!.GetValue<string>())
                    throw new Exception("Managed text differs from independent complete ZIP/XML decoding.");
            }
            if (profile.Rows.Length != item["rows"]!.AsArray().Count || !bytes.SequenceEqual(workbook.OriginalBytes()) ||
                TranslationInspection.Page(workbook, 1000)["rows"]!.AsArray().Count != profile.Rows.Length)
                throw new Exception("Complete row coverage, inspection or original-byte preservation differs.");
            if (profile.Rows.Length > 0)
            {
                var first = profile.Rows[0]; var pair = workbook.Read(first.NativeKey, "en-GB");
                var edit = new TranslationEdit(first.NativeKey, "en-GB", TranslationEditKind.Set, "refusal probe", pair.Source.Sha256, pair.Target.Sha256, true, "test reviewer");
                if (TranslationReview.Review(workbook, workbook, [edit])["canWriteOfflineCandidate"]!.GetValue<bool>()) throw new Exception("Unqualified native profile allowed a changed candidate.");
            }
            totalRows += profile.Rows.Length; workbooks.Add(mode, workbook);
            reads.Add(new JsonObject { ["mode"] = mode, ["rows"] = profile.Rows.Length, ["workbookSha256"] = workbook.WorkbookSha256,
                ["profileSha256"] = workbook.ProfileSha256, ["originalBytesPreserved"] = true,
                ["changedCandidateBlocked"] = profile.Rows.Length > 0 ? JsonValue.Create(true) : null,
                ["emptyCapture"] = profile.Rows.Length == 0 });
        }
        var bindings = TranslationJson.Parse<TranslationCategoryBinding[]>(manifest["categoryBindings"]!.ToJsonString());
        var comparisons = new JsonArray();
        foreach (var test in manifest["comparisons"]!.AsArray())
        {
            var report = TranslationCategoryScope.Compare(workbooks[test!["baselineMode"]!.GetValue<string>()], workbooks[test["selectedMode"]!.GetValue<string>()],
                test["requestedCategoryIds"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray(), bindings);
            if (report["expectedSelectedRows"]!.GetValue<int>() != test["expectedRows"]!.GetValue<int>() ||
                report["observedSelectedRows"]!.GetValue<int>() != test["actualRows"]!.GetValue<int>() ||
                report["outOfCategoryKeys"]!.AsArray().Count != test["outOfCategoryRows"]!.GetValue<int>() ||
                report["matchesRequestedCategoryIntersection"]!.GetValue<bool>() != test["matchesIntersection"]!.GetValue<bool>() ||
                report["restrictiveFilteringDemonstrated"]!.GetValue<bool>() || report["nativeImportAllowed"]!.GetValue<bool>())
                throw new Exception("Managed scope report differs from the independent request/row oracle or grants qualification.");
            comparisons.Add(new JsonObject { ["baselineMode"] = test["baselineMode"]!.DeepClone(), ["selectedMode"] = test["selectedMode"]!.DeepClone(), ["comparison"] = report });
        }
        var result = new JsonObject { ["kind"] = "managed-native-category-probe-read-verification", ["allPassed"] = true, ["nativeInvoked"] = false,
            ["nativeImportAllowed"] = false, ["rowsVerified"] = totalRows, ["workbooks"] = reads, ["comparisons"] = comparisons };
        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream); writer.Write(result.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine($"{reads.Count} native workbooks / {totalRows} rows / {comparisons.Count} category comparisons independently cross-checked; no native invocation.");
        return 0;
    }

    internal static int Run(string casePath, string outputPath)
    {
        if (File.Exists(outputPath)) throw new Exception("Use a new category-scope output path.");
        var cases = JsonNode.Parse(File.ReadAllText(casePath))!.AsArray();
        TranslationWorkbook Open(string mode, Func<WorkbookProfile, WorkbookProfile>? alter = null, byte[]? bytes = null,
            Func<EngineeringObjectReference, EngineeringObjectReference>? identityAlter = null)
        {
            var item = cases.Single(c => c!["mode"]!.GetValue<string>() == mode)!;
            var profile = TranslationJson.Parse<WorkbookProfile>(File.ReadAllText(item["profilePath"]!.GetValue<string>()));
            var identity = TranslationJson.Parse<EngineeringObjectReference>(item["identity"]!.ToJsonString());
            bytes ??= File.ReadAllBytes(item["workbookPath"]!.GetValue<string>());
            return TranslationWorkbook.Open(bytes, alter?.Invoke(profile) ?? profile, identityAlter?.Invoke(identity) ?? identity, TranslationJson.Sha256(bytes));
        }
        var baseline = Open("filtered-plc");
        var category = Open("filtered-category");
        TranslationCategoryBinding[] labels = [new("HwcnCommentTextCategoryData", "<HwcnCommentTextCategoryData>"),
            new("BlockCommentCategoryData", "<BlockCommentCategoryData>")];
        string[] hardware = ["HwcnCommentTextCategoryData"];
        var checks = new JsonArray();
        void Check(string name, Action action) { action(); checks.Add(name); }
        void Assert(bool condition) { if (!condition) throw new Exception("Category-scope assertion failed."); }
        void Reject(string code, Action action)
        { try { action(); } catch (BridgeException ex) { Assert(ex.Code == code); return; } throw new Exception("Expected " + code); }
        JsonObject Compare(TranslationWorkbook selected) => TranslationCategoryScope.Compare(baseline, selected, hardware, labels);
        var comparison = Compare(category);
        Check("genuine category leakage is rejected", () => {
            Assert(comparison["expectedSelectedRows"]!.GetValue<int>() == 1 && comparison["observedSelectedRows"]!.GetValue<int>() == 5);
            Assert(comparison["outOfCategoryKeys"]!.AsArray().Count == 4 && !comparison["matchesRequestedCategoryIntersection"]!.GetValue<bool>());
            Assert(!comparison["restrictiveFilteringDemonstrated"]!.GetValue<bool>());
        });
        Check("all-category equality does not prove restrictive filtering", () => {
            var all = TranslationCategoryScope.Compare(baseline, category, labels.Select(l => l.Identifier).ToArray(), labels);
            Assert(all["matchesRequestedCategoryIntersection"]!.GetValue<bool>() && !all["restrictiveFilteringDemonstrated"]!.GetValue<bool>());
        });
        Check("unknown categories refused", () => Reject("InvalidTranslationCategoryScope", () => TranslationCategoryScope.Compare(baseline, category, ["not-observed"], labels)));
        Check("empty category selection refused", () => Reject("InvalidTranslationCategoryScope", () => TranslationCategoryScope.Compare(baseline, category, [], labels)));
        Check("duplicate categories refused", () => Reject("InvalidTranslationCategoryScope", () => TranslationCategoryScope.Compare(baseline, category, [hardware[0], hardware[0]], labels)));
        Check("no bracket normalization", () => Reject("TranslationScopeMapMismatch", () => TranslationCategoryScope.Compare(baseline, category, hardware, labels.Select(l => l with { ExportLabel = l.Identifier }).ToArray())));
        Check("profile cannot hide excluded rows", () => Reject("TranslationScopeMapMismatch", () => Compare(Open("filtered-category", p => p with { Rows = p.Rows.Where(r => r.Category == labels[0].ExportLabel).ToArray() }))));
        Check("profile cannot relabel excluded rows", () => Reject("TranslationScopeMapMismatch", () => Compare(Open("filtered-category", p => p with { Rows = p.Rows.Select(r => r with { Category = labels[0].ExportLabel }).ToArray() }))));
        Check("foreign project refused", () => Reject("IdentityMismatch", () => Compare(Open("filtered-category", identityAlter: i => i with { Object = i.Object with { ProjectPath = @"D:\foreign.ap21" } }))));
        Check("unrelated capture receipt refused", () => Reject("TranslationScopeCaptureMismatch", () => Compare(Open("filtered-category", identityAlter: i => i with { Snapshot = i.Snapshot with { SourceSha256 = new string('b', 64) } }))));
        // Construct a file-only positive control from retained bytes, clearly distinct from native evidence.
        byte[] Restrict(bool keepHardware, bool changeText = false)
        {
            using var input = new ZipArchive(new MemoryStream(category.OriginalBytes()));
            using var memory = new MemoryStream();
            using (var output = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var entry in input.Entries)
            {
                using var source = entry.Open(); using var buffer = new MemoryStream(); source.CopyTo(buffer); byte[] data = buffer.ToArray();
                if (entry.FullName == "xl/worksheets/sheet1.xml")
                {
                    XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    var doc = XDocument.Load(new MemoryStream(data));
                    var allowed = category.Profile.Rows.Where(r => keepHardware && r.Category == labels[0].ExportLabel).Select(r => r.KeyCell.Cell[1..]).ToHashSet();
                    foreach (var row in doc.Descendants(s + "row").Where(r => (string?)r.Attribute("r") != "1" && !allowed.Contains((string)r.Attribute("r")!)).ToArray()) row.Remove();
                    if (changeText)
                    {
                        var cell = doc.Descendants(s + "c").Single(c => ((string?)c.Attribute("r"))?.StartsWith("F") == true && (string?)c.Attribute("r") != "F1");
                        cell.SetAttributeValue("t", "inlineStr"); cell.RemoveNodes(); cell.Add(new XElement(s + "is", new XElement(s + "t", "synthetic changed value")));
                    }
                    data = System.Text.Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
                }
                using var dest = output.CreateEntry(entry.FullName).Open(); dest.Write(data);
            }
            return memory.ToArray();
        }
        TranslationWorkbook Control(bool keepHardware, bool changeText = false) => Open("filtered-category",
            p => p with { Rows = p.Rows.Where(r => keepHardware && r.Category == labels[0].ExportLabel).ToArray() }, Restrict(keepHardware, changeText));
        Check("synthetic strict subset positive control", () => { var report = Compare(Control(true)); Assert(report["matchesRequestedCategoryIntersection"]!.GetValue<bool>() && report["restrictiveFilteringDemonstrated"]!.GetValue<bool>()); });
        Check("missing selected key detected", () => { var report = Compare(Control(false)); Assert(report["missingKeys"]!.AsArray().Count == 1 && !report["matchesRequestedCategoryIntersection"]!.GetValue<bool>()); });
        Check("changed selected text detected", () => { var report = Compare(Control(true, true)); Assert(report["changedKeys"]!.AsArray().Count == 1 && !report["matchesRequestedCategoryIntersection"]!.GetValue<bool>()); });
        Check("no native authorization on positive control", () => { var report = Compare(Control(true)); foreach (string flag in new[] { "nativeQualified", "nativeExecuted", "nativeImportAllowed", "sameNativeObjectSelectionVerified", "projectFreshnessVerified" }) Assert(!report[flag]!.GetValue<bool>()); });
        // Adversarial controls derived from genuine bytes; these never become native evidence.
        XNamespace sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        byte[] Rewrite(byte[] bytes, Action<Dictionary<string, XDocument>> change)
        {
            using var input = new ZipArchive(new MemoryStream(bytes));
            var documents = input.Entries.ToDictionary(e => e.FullName, e => { using var stream = e.Open(); return XDocument.Load(stream); }, StringComparer.Ordinal);
            change(documents);
            using var memory = new MemoryStream();
            using (var output = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var part in documents)
                { using var stream = output.CreateEntry(part.Key).Open(); part.Value.Save(stream); }
            return memory.ToArray();
        }
        void SetCell(XDocument document, string address, string value)
        {
            var cell = document.Descendants(sheet + "c").Single(c => (string?)c.Attribute("r") == address);
            cell.SetAttributeValue("t", "inlineStr"); cell.RemoveNodes();
            cell.Add(new XElement(sheet + "is", new XElement(sheet + "t", value)));
        }
        Check("empty system sheet target culture is part of scope", () => Reject("UnsupportedTranslationScopeLayout", () => Compare(Open("filtered-category", bytes:
            Rewrite(category.OriginalBytes(), docs => SetCell(docs["xl/worksheets/sheet2.xml"], "F1", "en-US"))))));
        Check("empty export still binds target culture", () => Reject("TranslationScopeCaptureMismatch", () => Compare(Open("filtered-category", p => p with { Rows = [] },
            Rewrite(Restrict(false), docs => { SetCell(docs["xl/worksheets/sheet1.xml"], "F1", "en-US"); SetCell(docs["xl/worksheets/sheet2.xml"], "F1", "en-US"); })))));
        Check("renamed empty worksheet refused", () => Reject("UnsupportedTranslationScopeLayout", () => Compare(Open("filtered-category", bytes:
            Rewrite(category.OriginalBytes(), docs => docs["xl/workbook.xml"].Descendants(sheet + "sheet").Last().SetAttributeValue("name", "Other Texts"))))));
        Check("hidden empty worksheet refused", () => Reject("UnsupportedTranslationScopeLayout", () => Compare(Open("filtered-category", bytes:
            Rewrite(category.OriginalBytes(), docs => docs["xl/workbook.xml"].Descendants(sheet + "sheet").Last().SetAttributeValue("state", "veryHidden"))))));
        Check("undeclared empty worksheet refused", () => Reject("UnsupportedTranslationScopeLayout", () => Compare(Open("filtered-category", bytes:
            Rewrite(category.OriginalBytes(), docs => docs["xl/workbook.xml"].Descendants(sheet + "sheet").Last().Remove())))));
        Check("duplicate empty worksheet identity refused", () => Reject("UnsupportedTranslationScopeLayout", () => Compare(Open("filtered-category", bytes:
            Rewrite(category.OriginalBytes(), docs => docs["xl/workbook.xml"].Descendants(sheet + "sheet").Last().SetAttributeValue("name", "User Texts"))))));
        Check("worksheet order must match captured layout", () => Reject("UnsupportedTranslationScopeLayout", () => Compare(Open("filtered-category", bytes:
            Rewrite(category.OriginalBytes(), docs => { var first = docs["xl/workbook.xml"].Descendants(sheet + "sheet").First(); first.Remove(); docs["xl/workbook.xml"].Root!.Element(sheet + "sheets")!.Add(first); })))));
        Check("row relocation between user and system text is detected", () => {
            var moved = Open("filtered-category", p => p with { Rows = p.Rows.Where(r => r.Category == labels[0].ExportLabel).Select(r => r with {
                KeyCell = r.KeyCell with { Part = "xl/worksheets/sheet2.xml" }, SourceCell = r.SourceCell with { Part = "xl/worksheets/sheet2.xml" },
                Targets = r.Targets.Select(t => t with { Location = t.Location with { Part = "xl/worksheets/sheet2.xml" } }).ToArray() }).ToArray() },
                Rewrite(Restrict(true), docs => { var row = docs["xl/worksheets/sheet1.xml"].Descendants(sheet + "row").Last(); row.Remove(); docs["xl/worksheets/sheet2.xml"].Root!.Element(sheet + "sheetData")!.Add(row); }));
            var report = Compare(moved); Assert(report["changedKeys"]!.AsArray().Count == 1 && !report["matchesRequestedCategoryIntersection"]!.GetValue<bool>());
        });
        Check("selected view path change detected", () => {
            string address = category.Profile.Rows.Single(r => r.Category == labels[0].ExportLabel).KeyCell.Cell.Replace('C', 'B');
            var changed = Open("filtered-category", p => p with { Rows = p.Rows.Where(r => r.Category == labels[0].ExportLabel).ToArray() },
                Rewrite(Restrict(true), docs => SetCell(docs["xl/worksheets/sheet1.xml"], address, "different owner path")));
            var report = Compare(changed); Assert(report["changedKeys"]!.AsArray().Count == 1 && !report["matchesRequestedCategoryIntersection"]!.GetValue<bool>());
        });
        var result = new JsonObject { ["kind"] = "translation-category-scope-file-only-checks", ["allPassed"] = true,
            ["checks"] = checks, ["nativeCaptureComparison"] = comparison, ["positiveControlsAreSynthetic"] = true,
            ["nativeInvoked"] = false, ["nativeImportAllowed"] = false };
        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream); writer.Write(result.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine($"{checks.Count} category-scope checks passed against retained genuine captures and labelled synthetic controls; no native invocation.");
        return 0;
    }
}
