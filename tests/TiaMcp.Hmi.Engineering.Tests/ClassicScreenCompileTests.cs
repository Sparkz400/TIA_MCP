using Newtonsoft.Json.Linq;
using TiaMcp.Model;

static class ClassicScreenCompileTests
{
    static JObject Row(string description, string path, string state, int depth)
        => new() { ["description"] = description, ["path"] = path, ["state"] = state, ["depth"] = depth };
    static JObject Fixture(string stamp = "03.11.2026 10:08", int bytes = 2883484, string owner = "ControlA", string warning = "The process tag is missing.")
        => new() { ["errorCount"] = 0, ["warningCount"] = 1, ["targets"] = new JArray(new JObject {
            ["target"] = "/hmi/Runtime", ["errorCount"] = 0, ["warningCount"] = 1, ["messagesTruncated"] = false,
            ["messages"] = new JArray(Row("", "Runtime", "Warning", 0), Row($"Time stamp: {stamp} - {bytes} bytes used", "Runtime", "Warning", 1),
                Row("Software compilation started.", null, "Information", 2), Row("", owner, "Warning", 2), Row(warning, null, "Warning", 3),
                Row("Compilation completed: 0 errors, 1 warnings.", null, "Warning", 0)) }) };
    static JObject Completed(JObject value) { value["targets"][0]["messages"].Last["description"] = "Compiling finished (errors: 0; warnings: 1)"; return value; }
    public static void Run(Action<string, Action> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("Classic diagnostic comparison assertion failed"); }
        check("Completed baseline errors remain usable; new diagnostics and incomplete compiler runs refuse", () => {
            var baseline = Fixture(); baseline["errorCount"] = 1; baseline["targets"][0]["errorCount"] = 1;
            var rows = (JArray)baseline["targets"][0]["messages"];
            rows.Insert(rows.Count - 1, Row("Existing unrelated error", null, "Error", 3));
            rows.Last["description"] = "Compiling finished (errors: 1; warnings: 1)";
            Assert(ClassicScreenCompileRules.Completed(baseline));
            Assert(ClassicScreenCompileRules.NoNewProblems(baseline, (JObject)baseline.DeepClone()));
            var aborted = (JObject)baseline.DeepClone(); aborted["targets"][0]["messages"].Last["description"] = "Compiling aborted (errors: 1; warnings: 1)";
            Assert(ClassicScreenCompileRules.Completed(aborted));
            aborted["targets"][0]["messages"].Last["description"] = "Compiling aborted (errors: 2; warnings: 1)";
            Assert(!ClassicScreenCompileRules.Completed(aborted));
            var fixedResult = (JObject)baseline.DeepClone(); var fixedRows = (JArray)fixedResult["targets"][0]["messages"];
            fixedRows.RemoveAt(fixedRows.Count - 2); fixedResult["errorCount"] = 0; fixedResult["targets"][0]["errorCount"] = 0;
            fixedRows.Last["description"] = "Compiling finished (errors: 0; warnings: 1)";
            Assert(ClassicScreenCompileRules.NoNewProblems(baseline, fixedResult));
            Assert(!ClassicScreenCompileRules.NoNewProblems(fixedResult, baseline));
            rows[rows.Count - 2]["description"] = "Cannot compile. Cannot access the project path.";
            Assert(ClassicScreenCompileRules.IsTransientPathFailure(baseline));
            Assert(!ClassicScreenCompileRules.Completed(baseline));
            Assert(!ClassicScreenCompileRules.NoNewProblems(baseline, baseline));
            Assert(!ClassicScreenCompileRules.Completed(new JObject()));
        });
        check("A compiler that could not run is reported apart from the edit and from a diagnostics difference", () => {
            // Shapes of the 11 November 2026 apply, with synthetic names: the baseline was answered from the last compile,
            // the compile after the import and the rollback compile both stopped at the generator with the path error.
            var baseline = Completed(Fixture());
            ((JArray)baseline["targets"][0]["messages"]).Insert(3, Row(ClassicScreenCompileRules.GenerationSkippedText, null, "Information", 2));
            Assert(ClassicScreenCompileRules.Completed(baseline) && ClassicScreenCompileRules.SkippedGeneration(baseline));
            Assert(!ClassicScreenCompileRules.SkippedGeneration(Completed(Fixture())) && !ClassicScreenCompileRules.SkippedGeneration(null));
            // The same text as a warning, not the compiler's information row, is an ordinary diagnostic.
            Assert(!ClassicScreenCompileRules.SkippedGeneration(Completed(Fixture(warning: ClassicScreenCompileRules.GenerationSkippedText))));

            var failed = Fixture(); failed["errorCount"] = 1; failed["targets"][0]["errorCount"] = 1;
            var rows = (JArray)failed["targets"][0]["messages"];
            rows.RemoveAt(1); rows[0]["state"] = "Error";
            rows.Insert(rows.Count - 1, Row("Internal WARNING (detail: EvaluateStringRelations: stream to patch is null).", null, "Warning", 2));
            rows.Insert(rows.Count - 1, Row(ClassicScreenCompileRules.PathFailureText, null, "Error", 2));
            failed["warningCount"] = 2; failed["targets"][0]["warningCount"] = 2;
            rows.Last["description"] = "Compiling aborted (errors: 1; warnings: 2)"; rows.Last["state"] = "Error";
            Assert(ClassicScreenCompileRules.IsTransientPathFailure(failed) && !ClassicScreenCompileRules.Completed(failed));
            Assert(!ClassicScreenCompileRules.NoNewProblems(baseline, failed));

            string final = ClassicScreenCompileRules.FinalCompileUnavailable(failed);
            Assert(final != null && final.Contains(ClassicScreenCompileRules.PathFailureText) && final.Contains("says nothing about the edited screens"));
            Assert(ClassicScreenCompileRules.FinalCompileUnavailable(baseline) == null && ClassicScreenCompileRules.FinalCompileUnavailable(null) == null);

            string unverified = ClassicScreenCompileRules.RollbackCompileFailure(baseline, failed, false);
            string restored = ClassicScreenCompileRules.RollbackCompileFailure(baseline, failed, true);
            Assert(unverified.Contains("could not run") && !unverified.Contains("saved project is still the checkpoint"));
            Assert(restored.StartsWith(unverified) && restored.Contains("scope equals the pre-import capture") && restored.Contains("saved project is still the checkpoint"));
            Assert(!restored.Contains("do not match"));
            // A failure after the apply's own save: the saved project is the edit, so "close without saving" must not be advised.
            string afterSave = ClassicScreenCompileRules.RollbackCompileFailure(baseline, failed, true, editSaved: true);
            Assert(afterSave.StartsWith(unverified) && afterSave.Contains("saved project holds the edit") && afterSave.Contains("recovery archive")
                && !afterSave.Contains("saved project is still the checkpoint") && !afterSave.Contains("returns to it"));
            // A real difference keeps the old verdict; an exact match has none. A skipped-generation baseline is a valid reference for both.
            Assert(ClassicScreenCompileRules.RollbackCompileFailure(baseline, Completed(Fixture(owner: "ControlB")), true) == "Rollback compiler diagnostics do not match the successful baseline.");
            Assert(ClassicScreenCompileRules.RollbackCompileFailure(baseline, new JObject(), true) == "Rollback compiler diagnostics do not match the successful baseline.");
            Assert(ClassicScreenCompileRules.RollbackCompileFailure(baseline, Completed(Fixture("03.11.2026 10:10", 2883512)), true) == null);
        });
        check("Classic timestamp/byte-count wrapper is metadata, retained warning locations are significant", () => {
            var before = Fixture(); var copy = before.DeepClone();
            Assert(ClassicScreenCompileRules.ProblemsUnchanged(before, Fixture("03.11.2026 10:10", 2883512)));
            Assert(JToken.DeepEquals(before, copy));
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Fixture(owner: "ControlB")));
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Fixture(warning: "The position or size of the object is invalid.")));
            var count = Fixture(); count["warningCount"] = 2; Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, count));
            count = Fixture(); count["targets"][0]["warningCount"] = 2; Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, count));
        });
        check("Classic generated screen-trigger labels are ignored; owner, event and other text stay significant", () => {
            JObject Trigger(string name, string owner = "3024_Screen", string evt = "Loaded", string text = "The parameter of a function is not available for trigger") {
                var result = Fixture(owner: owner, warning: $"{text} {name}, event '{evt}'. The referenced object does not exist.");
                return result;
            }
            var before = Trigger("3074_Old_Screen");
            Assert(ClassicScreenCompileRules.ProblemsUnchanged(before, Trigger("3024_Screen")));
            Assert(ClassicScreenCompileRules.NoNewProblems(Completed(before), Completed(Trigger("3024_Screen"))));
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Trigger("3024_Screen", owner: "3025_Screen")));
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Trigger("3024_Screen", evt: "Cleared")));
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Trigger("3024_Screen", text: "The tag is invalid for trigger")));
        });
        check("Classic timestamp recognition refuses leaves, changed formats and incomplete diagnostic trees", () => {
            var before = Fixture();
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Fixture("invalid", 2883512)));
            foreach (string damage in new[] { "leaf", "owner", "start", "truncated", "depth", "missingCount" }) {
                var other = Fixture("03.11.2026 10:10", 2883512); var target = other["targets"][0]; var messages = (JArray)target["messages"];
                if (damage == "leaf") messages[2]["depth"] = 1;
                if (damage == "owner") messages[1]["path"] = "Other";
                if (damage == "start") messages[2]["description"] = "Unexpected header";
                if (damage == "truncated") target["messagesTruncated"] = true;
                if (damage == "depth") messages[2]["depth"] = 6;
                if (damage == "missingCount") other.Remove("errorCount");
                Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, other));
            }
        });
        check("An edit that removes the last error accepts revealed warnings outside its screens only", () => {
            // Baseline: one error in the edited screen; V21 aborts. Candidate: no errors; findings the aborted run never reached appear.
            JObject Result(int errors, string summary, params JObject[] findings)
            {
                int warnings = findings.Count(f => f.Value<string>("state") == "Warning");
                var rows = new JArray(Row("", "Runtime", errors > 0 ? "Error" : "Warning", 0), Row("Software compilation started.", null, "Information", 1));
                foreach (var f in findings) rows.Add(f);
                rows.Add(Row(summary.Replace("{e}", errors.ToString()).Replace("{w}", warnings.ToString()), null, errors > 0 ? "Error" : "Warning", 0));
                return new JObject { ["errorCount"] = errors, ["warningCount"] = warnings, ["targets"] = new JArray(new JObject {
                    ["target"] = "/hmi/Runtime", ["errorCount"] = errors, ["warningCount"] = warnings, ["messagesTruncated"] = false, ["messages"] = rows }) };
            }
            JObject[] In(string screen, string state, string text) => new[] { Row("", screen, state, 1), Row("", "Item_1", state, 2), Row(text, null, state, 3) };
            var edited = new HashSet<string> { "3024_Edited" };
            var baseline = Result(1, "Compiling aborted (errors: {e}; warnings: {w})", In("3024_Edited", "Error", "The tag does not exist.").Concat(In("3031_Other", "Warning", "Known warning.")).ToArray());
            var clean = Result(0, "Compiling finished (errors: {e}; warnings: {w})", In("3031_Other", "Warning", "Known warning.").Concat(In("3004_Other", "Warning", "The position or size of the object is invalid.")).ToArray());
            Assert(ClassicScreenCompileRules.NoNewProblems(baseline, clean, edited, out var unmasked) && unmasked.Count == 1
                && unmasked[0][1].Value<string>() == "The position or size of the object is invalid." && unmasked[0][2].Value<string>().Contains("3004_Other"));
            Assert(!ClassicScreenCompileRules.NoNewProblems(baseline, clean));
            Assert(!ClassicScreenCompileRules.NoNewProblems(baseline, clean, new HashSet<string>(), out _));
            Assert(!ClassicScreenCompileRules.NoNewProblems(baseline, clean, new HashSet<string> { "3004_Other" }, out _));
            var inside = Result(0, "Compiling finished (errors: {e}; warnings: {w})", In("3024_Edited", "Warning", "A new warning in the edited screen.").ToArray());
            Assert(!ClassicScreenCompileRules.NoNewProblems(baseline, inside, edited, out _));
            var stillFailing = Result(1, "Compiling aborted (errors: {e}; warnings: {w})", In("3024_Edited", "Error", "The tag does not exist.").Concat(In("3004_Other", "Warning", "Revealed?")).ToArray());
            Assert(!ClassicScreenCompileRules.NoNewProblems(baseline, stillFailing, edited, out _));
            var finishedBaseline = Result(1, "Compiling finished (errors: {e}; warnings: {w})", In("3024_Edited", "Error", "The tag does not exist.").ToArray());
            Assert(!ClassicScreenCompileRules.NoNewProblems(finishedBaseline, clean, edited, out _));
            Assert(ClassicScreenCompileRules.EditedScreenNames(new JObject { ["target"] = new JObject { ["objectPath"] = "/hmi/R/screens/folders/302x/3024_A%20B" },
                ["additionalScreens"] = new JArray(new JObject { ["target"] = new JObject { ["objectPath"] = "/hmi/R/screens/B" } }) }).SetEquals(new[] { "3024_A B", "B" }));
            try { ClassicScreenCompileRules.EditedScreenNames(new JObject { ["target"] = new JObject { ["name"] = "A" } }); throw new Exception("Expected a missing-path refusal"); }
            catch (ArgumentException) { }
        });
    }
}
