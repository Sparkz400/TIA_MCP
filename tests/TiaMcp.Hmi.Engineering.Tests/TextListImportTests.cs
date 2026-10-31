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
            Refuse(() => HmiTextListImportRules.Resolve(List("            Text: L.Missing\n").Replace("            Text: L.T\n", ""), library), "unresolved reference refuses");
            Refuse(() => HmiTextListImportRules.Resolve(List(""), library.Replace("    - en-US\n", "    - en-US\n    - de-DE\n")), "a text count differing from the languages refuses");
            Refuse(() => HmiTextListImportRules.Resolve(List("").Replace("Text: L.T\n", "Text: L.T.X\n"), library + "  L.T:\n    Languages:\n    - en-US\n    Entries:\n      X:\n        Text:\n        - x\n"), "an ambiguous library prefix refuses");
            Assert(HmiTextListImportRules.Resolve(List("").Replace("            Text: L.T\n", "            Value: 1\n"), library)["A"].Contains("\"Text\":null"), "an entry without a reference stays null");
            Assert(HmiTextListImportRules.Resolve(List(""), library.Replace("- On", "- \"2026-10-31T10:00:00+01:00\""))["A"].Contains("2026-10-31T10:00:00+01:00"), "date-like texts stay exact");
        });
        check("The reader refuses YAML outside the qualified subset", () => {
            foreach (string yaml in new[] { "a: &x 1\n", "a: *x\n", "a: !tag 1\n", "a: [1]\n", "a: {b: 1}\n", "a: b\n  c\n", "a: 1\na: 2\n", "a:\n\t- b\n",
                "a: |2-\n    x\n", "a: \"open\n", "a: 'open\n", "a: 1\rb: 2\n", "a: x #c\n", "a: \"\\q\"\n", "a: \"\\uD800\"\n", "- a: b\n", "a: |\n   x\n  y\n", " a: 1\n" })
                Refuse(() => HmiTextListImportRules.Read(yaml), yaml);
            Assert(HmiTextListImportRules.Read("a: []\nb: {}\n# c\nd: ''\n") != null, "empty collections and comments");
            string Nested(int levels) => string.Concat(Enumerable.Range(0, levels).Select(i => new string(' ', i) + "k" + i + ":\n")) + new string(' ', levels) + "v: 1\n";
            Assert(HmiTextListImportRules.Read(Nested(32)) != null, "32 nested levels read");
            Refuse(() => HmiTextListImportRules.Read(Nested(33)), "deeper nesting refuses cleanly");
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
        check("No profile is open in production; a qualification Host admits exactly its profile", () => {
            Assert(HmiTextListImportRules.Profiles.Length == 0, "ships with no qualified profile");
            foreach (string profile in new[] { "unified-comfort", "unified-pc" })
            {
                var closed = HmiTextListImportRules.Gate("unified", profile);
                Assert(!closed.Value<bool>("available") && closed.Value<string>("reasonCode") == "HmiPreservationUnavailable", "closed without qualification: " + profile);
                var admitted = HmiTextListImportRules.Gate("unified", profile, profile);
                Assert(admitted.Value<bool>("available") && admitted.Value<string>("admission") == "qualification" && admitted.Value<string>("nativeQualification") == "unverified", "qualification admits " + profile);
            }
            Assert(!HmiTextListImportRules.Gate("unified", "unified-pc", "unified-comfort").Value<bool>("available"), "another profile's qualification does not admit");
            foreach (var (family, profile) in new[] { ("unified", "unknown"), ("classic", "classic-comfort"), ("unified", null) })
                Assert(!HmiTextListImportRules.Gate(family, profile, profile).Value<bool>("available"), "never admitted: " + profile);
            foreach (string operation in new[] { "screen_create", "tag_create", "classic_xml_import" })
                Assert(!HmiEngineeringRules.Gate("unified", "unified-comfort", operation).Value<bool>("available"), "other HMI writes stay closed: " + operation);
        });
        check("The capture is accepted with exactly the two standing Unified notes", () => {
            JObject Manifest(params string[] notes) => new JObject { ["family"] = "unified", ["nativeFailure"] = null, ["observedArtifactsHash"] = new string('e', 64),
                ["targets"] = new JArray(new JObject { ["family"] = "unified", ["hmiPath"] = "/d/hmi/H" }), ["missingCoverage"] = new JArray(notes) };
            Assert(HmiTextListImportRules.ScopeAccepted(Manifest(HmiPreservationRules.UnifiedTagTableExportNote, HmiPreservationRules.UnifiedTypedReadbackNote)), "standing notes");
            Assert(HmiTextListImportRules.ScopeAccepted(Manifest(HmiPreservationRules.UnifiedTagTableExportNote, HmiPreservationRules.UnifiedTypedReadbackNote,
                HmiPreservationRules.UnifiedTagTableExportNote, HmiPreservationRules.UnifiedTypedReadbackNote)), "two Unified HMIs repeat them");
            var readError = Manifest(HmiPreservationRules.UnifiedTagTableExportNote, "2 Unified typed attribute read(s) failed; their readError markers are compared, their values are not. See typed-model.json.", HmiPreservationRules.UnifiedTypedReadbackNote);
            Assert(!HmiTextListImportRules.ScopeAccepted(readError), "a typed read error refuses");
            var failed = Manifest(HmiPreservationRules.UnifiedTagTableExportNote); failed["nativeFailure"] = new JObject { ["message"] = "x" };
            Assert(!HmiTextListImportRules.ScopeAccepted(failed), "a capture failure refuses");
            var classic = Manifest(HmiPreservationRules.UnifiedTagTableExportNote); ((JArray)classic["targets"]).Add(new JObject { ["family"] = "classic" });
            Assert(!HmiTextListImportRules.ScopeAccepted(classic), "a Classic HMI in the project refuses");
            var unhashed = Manifest(HmiPreservationRules.UnifiedTagTableExportNote); unhashed["observedArtifactsHash"] = null;
            Assert(!HmiTextListImportRules.ScopeAccepted(unhashed) && !HmiTextListImportRules.ScopeAccepted(null), "no observed hash refuses");
            var odd = Manifest(HmiPreservationRules.UnifiedTagTableExportNote); ((JArray)odd["missingCoverage"]).Add(new JObject());
            Assert(!HmiTextListImportRules.ScopeAccepted(odd), "a non-text note refuses");
        });
        check("Every typed text-list node must be a list the export holds", () => {
            string[] keys = { "/d/hmi/H/textLists/States", "/d/hmi/H/textLists/Name%20with%20space", "/d/hmi/H/textLists/States/entries/x", "/d/hmi/H/screens/S", "/d/hmi/O/textLists/Other" };
            Assert(HmiTextListImportRules.ExportCoversTypedLists(keys, "/d/hmi/H", new[] { "States", "Name with space" }), "covered");
            Assert(!HmiTextListImportRules.ExportCoversTypedLists(keys, "/d/hmi/H", new[] { "States" }), "a list the export skipped (formatted text) refuses");
            Assert(!HmiTextListImportRules.ExportCoversTypedLists(keys, "/d/hmi/H", new[] { "States", "Name with space", "Extra" }), "an exported list without typed node refuses");
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
        check("Only the candidate lists' typed subtrees and the export may change", () => {
            var before = new Dictionary<string, string> { ["/h/textLists/A"] = "1", ["/h/textLists/A/entries/x"] = "1", ["/h/textLists/Peer"] = "5", ["/h/textLists/AB"] = "6",
                ["/h/textListExport/T.hmi.yml"] = "2", ["/h/screens/S"] = "3", ["/o/textLists/A"] = "4" };
            var after = new Dictionary<string, string>(before) { ["/h/textLists/A"] = "9", ["/h/textLists/A/entries/x"] = "9", ["/h/textLists/B"] = "8", ["/h/textListExport/T.hmi.yml"] = "7" };
            string[] roots = { "/h/textLists/A", "/h/textLists/B", "/h/textListExport" };
            HmiPreservationRules.RequireUnrelatedPreservedOutsideSubtrees(before, after, roots);
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsideSubtrees(before, new Dictionary<string, string>(after) { ["/h/textLists/Peer"] = "0" }, roots), "an untouched peer list's typed node");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsideSubtrees(before, new Dictionary<string, string>(after) { ["/h/textLists/AB"] = "0" }, roots), "a list sharing the candidate's name prefix");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsideSubtrees(before, new Dictionary<string, string>(after) { ["/h/screens/S"] = "0" }, roots), "changed screen");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsideSubtrees(before, new Dictionary<string, string>(after) { ["/o/textLists/A"] = "0" }, roots), "another HMI's list");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsideSubtrees(before, new Dictionary<string, string>(after) { ["/h/new"] = "0" }, roots), "created outside");
            Refuse(() => HmiPreservationRules.RequireUnrelatedPreservedOutsideSubtrees(before, after, "/h/textLists/"), "root with a trailing slash");
        });
    }
}
