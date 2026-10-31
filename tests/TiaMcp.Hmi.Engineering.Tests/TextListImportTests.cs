using System.Text;
using Newtonsoft.Json.Linq;
using TiaMcp.Model;

// Change kind hmi_text_list_import: the export reader against real V21 exports, the import comparison, the compile rule and the gate.
static class TextListImportTests
{
    static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "fixtures", "hmi", "unified-text-list", "native-export-tl4");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new Exception("Repository fixture fixtures/hmi/unified-text-list/native-export-tl4 is missing.");
    }
    static string Text(string path) => new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
    static (string Configuration, string Library) Pair(string directory)
    {
        var files = Directory.GetFiles(directory);
        string library = files.Single(f => f.EndsWith(".TextLibrary.hmi.yml", StringComparison.Ordinal));
        return (Text(files.Single(f => f != library)), Text(library));
    }
    static SortedDictionary<string, string> Lists(string directory) { var (c, l) = Pair(directory); return HmiTextListImportRules.Resolve(c, l); }
    static void Assert(bool value, string label) { if (!value) throw new Exception(label); }
    static void Refuse(Action action, string label)
    {
        try { action(); } catch (FormatException) { return; } catch (ArgumentException) { return; }
        throw new Exception("Expected a refusal: " + label);
    }

    internal static void Run(Action<string, Action> check)
    {
        string root = Root();
        check("Every tl4 import reads back exactly through the export reader, as the native oracle found", () => {
            int imports = 0;
            foreach (JObject sequence in JObject.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")))["sequences"])
            {
                var before = Lists(Path.Combine(root, (string)sequence["baseline"]));
                Assert(before.Keys.SequenceEqual(new[] { "QualificationStates" }), "baseline holds the documented peer list");
                foreach (JObject row in sequence["imports"])
                {
                    var after = Lists(Path.Combine(root, (string)row["export"]));
                    var result = HmiTextListImportRules.Evaluate(before, after, Lists(Path.Combine(root, (string)row["candidate"])));
                    Assert(result.Value<bool>("exact"), "exact import " + row["export"] + ": " + result);
                    before = after; imports++;
                }
            }
            Assert(imports == 15, "all 15 native imports compared");
        });
        check("The reader decodes V21's literal, folded, quoted and escaped texts", () => {
            var (configuration, library) = Pair(Path.Combine(root, "n", "p1"));
            var lists = HmiTextListImportRules.Resolve(configuration, library);
            string chars = lists["TextChars"];
            foreach (string text in new[] { "tab\there\nLF", "\"quoted\" \\ backslash", "Über 日本語 😀", "---\n!tag &anchor #hash: colon", "x\u2028y\u2029z", new string('L', 16381) })
                Assert(chars.Contains(Newtonsoft.Json.JsonConvert.ToString(text).Trim('"')), "decoded " + text.Substring(0, Math.Min(12, text.Length)));
            Assert(library.Contains(">-") && library.Contains("|-") && library.Contains("\\U0001F600"), "the fixture uses folded, literal and escaped styles");
        });
        check("A changed peer, a changed, absent or extra list is not exact", () => {
            var baseline = Lists(Path.Combine(root, "n", "baseline"));
            var (configuration, library) = Pair(Path.Combine(root, "n", "i0"));
            var candidate = Lists(Path.Combine(root, "c", "m0"));
            var peer = HmiTextListImportRules.Evaluate(baseline, HmiTextListImportRules.Resolve(configuration, library.Replace("Qualifikation aus", "Qualifikation AUS")), candidate);
            Assert(!peer.Value<bool>("exact") && peer["peersChanged"].Values<string>().Single() == "QualificationStates", "changed peer");
            var changed = HmiTextListImportRules.Evaluate(baseline, HmiTextListImportRules.Resolve(configuration.Replace("ToValue: 10\r\n", "ToValue: 11\r\n"), library), candidate);
            Assert(!changed.Value<bool>("exact") && (string)changed["lists"]["States"] == "changed" && !changed["peersChanged"].Any(), "changed candidate list");
            var absent = HmiTextListImportRules.Evaluate(baseline, baseline, candidate);
            Assert(!absent.Value<bool>("exact") && absent["lists"].ToString().Contains("absent"), "absent candidate list");
            var extra = new SortedDictionary<string, string>(Lists(Path.Combine(root, "n", "i0"))) { ["Extra"] = "{}" };
            Assert(HmiTextListImportRules.Evaluate(baseline, extra, candidate)["unexpectedLists"].Values<string>().Single() == "Extra", "unexpected list");
            var exact = HmiTextListImportRules.Evaluate(baseline, Lists(Path.Combine(root, "n", "i0")), candidate);
            Assert(exact.Value<bool>("exact") && exact["created"].Values<string>().Single() == "States" && exact.Value<int>("peersCompared") == 1, "created and compared");
        });
        check("Values, defaults and booleans are significant; texts are never typed", () => {
            const string library = "TextLibraries:\n  L:\n    Languages:\n    - en-US\n    Entries:\n      T:\n        Text:\n        - On\n";
            string List(string fields) => "TextListContainers:\n  C:\n    ResourceLists:\n      A:\n        Entries:\n          E:\n" + fields + "            Text: L.T\n";
            string plain = HmiTextListImportRules.Resolve(List(""), library)["A"];
            Assert(plain == HmiTextListImportRules.Resolve(List("            Type: SingleValue\n            Value: 0\n            IsDefaultEntry: false\n"), library)["A"], "defaults applied");
            Assert(plain != HmiTextListImportRules.Resolve(List("            Value: 1\n"), library)["A"], "value significant");
            Assert(plain != HmiTextListImportRules.Resolve(List("            IsDefaultEntry: True\n"), library)["A"], "default flag significant");
            Assert(plain != HmiTextListImportRules.Resolve(List("            Value: '0'\n"), library)["A"], "quoted number is text");
            Assert(plain.Contains("\"en-US\":\"On\""), "plain text On stays text");
            Assert(HmiTextListImportRules.Resolve(List("            Text: L.Missing\n").Replace("            Text: L.T\n", ""), library)["A"].Contains("unresolved"), "unresolved reference marked");
            Assert(HmiTextListImportRules.Resolve(List(""), library.Replace("- On", "- \"2026-10-31T10:00:00+01:00\""))["A"].Contains("2026-10-31T10:00:00+01:00"), "date-like texts stay exact");
        });
        check("The reader refuses YAML outside the qualified subset", () => {
            foreach (string yaml in new[] { "a: &x 1\n", "a: *x\n", "a: !tag 1\n", "a: [1]\n", "a: {b: 1}\n", "a: b\n  c\n", "a: 1\na: 2\n", "a:\n\t- b\n",
                "a: |2-\n    x\n", "a: \"open\n", "a: 'open\n", "a: 1\rb: 2\n", "a: x #c\n", "a: \"\\q\"\n", "a: \"\\uD800\"\n", "- a: b\n", "a: |\n   x\n  y\n", " a: 1\n" })
                Refuse(() => HmiTextListImportRules.Read(yaml), yaml);
            Assert(HmiTextListImportRules.Read("a: []\nb: {}\n# c\nd: ''\n") != null, "empty collections and comments");
        });
        check("Compiler problems must be unchanged; Information rows and order do not matter", () => {
            JObject Compile(params (string State, string Text)[] rows) => new JObject { ["targets"] = new JArray(new JObject { ["target"] = "/d", ["messagesTruncated"] = false,
                ["messages"] = new JArray(rows.Select(r => new JObject { ["state"] = r.State, ["description"] = r.Text, ["path"] = "p", ["depth"] = 0 })) }) };
            var baseline = Compile(("Error", "password"), ("Information", "a"), ("Warning", "w"));
            Assert(HmiTextListImportRules.CompileProblemsUnchanged(baseline, Compile(("Warning", "w"), ("Information", "b"), ("Error", "password"))), "same problems");
            Assert(!HmiTextListImportRules.CompileProblemsUnchanged(baseline, Compile(("Error", "password"), ("Warning", "w"), ("Warning", "new"))), "new warning");
            Assert(!HmiTextListImportRules.CompileProblemsUnchanged(baseline, Compile(("Error", "password"))), "removed warning");
            var truncated = Compile(("Error", "password"), ("Warning", "w")); truncated["targets"][0]["messagesTruncated"] = true;
            Assert(!HmiTextListImportRules.CompileProblemsUnchanged(baseline, truncated) && !HmiTextListImportRules.CompileProblemsUnchanged(null, baseline), "incomplete diagnostics");
        });
        check("The gate opens only for Unified Comfort and Unified PC", () => {
            Assert(HmiTextListImportRules.Gate("unified", "unified-comfort").Value<bool>("available") && HmiTextListImportRules.Gate("unified", "unified-pc").Value<bool>("available"), "open");
            foreach (var (family, profile) in new[] { ("unified", "unknown"), ("classic", "classic-comfort"), ("unified", null) })
                Assert(!HmiTextListImportRules.Gate(family, profile).Value<bool>("available") && HmiTextListImportRules.Gate(family, profile).Value<string>("reasonCode") == "HmiPreservationUnavailable", "closed " + profile);
            foreach (string operation in new[] { "screen_create", "tag_create", "classic_xml_import" })
                Assert(!HmiEngineeringRules.Gate("unified", "unified-comfort", operation).Value<bool>("available"), "other HMI writes stay closed: " + operation);
        });
        check("The worker request is exactly the Host-resolved candidate pair", () => {
            JObject Request() => new JObject { ["schemaVersion"] = 1, ["expectedContentHash"] = new string('a', 64),
                ["target"] = new JObject { ["family"] = "unified", ["kind"] = "hmiDevice", ["projectCopyId"] = "c", ["sessionGeneration"] = "g", ["hmiPath"] = "/d/hmi/H", ["objectPath"] = "/d/hmi/H" },
                ["candidate"] = new JObject { ["recordId"] = new string('b', 64), ["languages"] = new JArray("en-US"), ["lists"] = new JArray("States"),
                    ["files"] = new JArray(new JObject { ["fileName"] = "S.hmi.yml", ["bytes"] = 10, ["sha256"] = new string('c', 64), ["path"] = "D:/a/x.candidate" },
                        new JObject { ["fileName"] = "S.TextLibrary.hmi.yml", ["bytes"] = 10, ["sha256"] = new string('d', 64), ["path"] = "D:/a/y.candidate" }) } };
            HmiTextListImportRules.RequireRequest(Request());
            var mutations = new Action<JObject>[] {
                r => r["force"] = true, r => r["target"]["kind"] = "hmiScreen", r => r["target"]["family"] = "classic", r => r["target"]["objectPath"] = "/d/hmi/H/screens/S",
                r => r["expectedContentHash"] = "0", r => r.Remove("candidate"), r => r["candidate"]["files"][1]["fileName"] = "Other.TextLibrary.hmi.yml",
                r => r["candidate"]["files"][0]["fileName"] = "../S.hmi.yml", r => r["candidate"]["files"][0]["bytes"] = 9L * 1024 * 1024, r => ((JArray)r["candidate"]["files"]).RemoveAt(1),
                r => r["candidate"]["lists"] = new JArray(), r => r["schemaVersion"] = 2 };
            foreach (var mutate in mutations) { var request = Request(); mutate(request); Refuse(() => HmiTextListImportRules.RequireRequest(request), request.ToString()); }
        });
        check("Preservation outside the text-list prefixes stays exact", () => {
            var before = new Dictionary<string, string> { ["/h/textLists/A"] = "1", ["/h/textListExport/T.hmi.yml"] = "2", ["/h/screens/S"] = "3", ["/o/textLists/A"] = "4" };
            var after = new Dictionary<string, string>(before) { ["/h/textLists/A"] = "9", ["/h/textLists/B"] = "8", ["/h/textListExport/T.hmi.yml"] = "7" };
            HmiPreservationRules.RequireUnrelatedPreservedOutsidePrefixes(before, after, "/h/textLists/", "/h/textListExport/");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsidePrefixes(before, new Dictionary<string, string>(after) { ["/h/screens/S"] = "0" }, "/h/textLists/", "/h/textListExport/"), "changed screen");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsidePrefixes(before, new Dictionary<string, string>(after) { ["/o/textLists/A"] = "0" }, "/h/textLists/", "/h/textListExport/"), "another HMI's list");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsidePrefixes(before, new Dictionary<string, string>(after) { ["/h/new"] = "0" }, "/h/textLists/", "/h/textListExport/"), "created outside");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsidePrefixes(before, after, "/h/textLists"), "prefix without slash");
        });
    }
}
