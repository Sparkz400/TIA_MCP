using System.Text;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal static class CasingResultProfileChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new Exception("Casing result profile failed"); checks++; }
        // German as captured; English as TIA V21 Update 3 wrote it on 29 October 2026 (clean leaves say "Executed successfully").
        var languages = new[] {
            (Group: "Styleguide", Footer: "Ausführung von Regelsatz/-sätzen beendet (Fehler : {0} ; Warnungen : 0)",
             Rule: "Regelverletzung/Validierung Codebaustein (FB,FC,OB) : {0}/3, Prozentsatz Regelverletzung - {1} %", With: "Elemente mit Regelverletzung",
             Without: "Elemente ohne Regelverletzung", Conflict: "Konflikte mit {0} : Der Objektname Codebaustein (FB,FC,OB) muss in Camel Case geschrieben werden", Clean: ""),
            (Group: "Style guide", Footer: "Rule set(s) execution finished (errors : {0} ; warnings : 0)",
             Rule: "Violated/Validated CodeBlock (FB,FC,OB) : {0}/3, Violation Percentage - {1}%", With: "Elements with violation",
             Without: "Elements without violation", Conflict: "Conflicts with {0} : The CodeBlock (FB,FC,OB) should be camel-cased", Clean: "Executed successfully") };
        foreach (var text in languages)
        foreach (int errors in new[] { 0, 3 })
        {
            string name = errors == 0 ? "TiaMcp_Q01_Clean" : "TiaMcp_Q01_Violating";
            var reference = new EngineeringObjectReference(new("owned", "plc", "plc", "rule/" + name, "ruleSet"), new("snapshot", new('a', 64)));
            var definition = new TestSuiteDefinitionCapture(name, "style", name, ".xml", reference, Encoding.UTF8.GetBytes("<Rule/>"),
                TestSuiteCasingResultProfile.Profile, [reference with { Object = reference.Object with { Kind = "block", ObjectPath = "Main" } }], [], true, true, []);
            NativeTestMessage Message(string path, int count, NativeTestMessage[] children, string description = "") =>
                new(count == 0 ? "Success" : "Error", description, path, null, "Unknown", count, 0, children);
            var leaves = new[] { "Main", "TiaMcp_EnableGate", "TiaMcp_IsEnabled" }.Select(n => Message("ScratchPLC - " + n, errors == 0 ? 0 : 1, [],
                errors == 0 ? text.Clean : string.Format(text.Conflict, n))).ToArray();
            var root = Message(text.Group, errors, [Message(name, errors, [Message("TiaMcp_CamelCase", errors,
                [Message(text.With, errors, errors == 0 ? [] : leaves), Message(text.Without, 0, errors == 0 ? leaves : [])],
                string.Format(text.Rule, errors, errors == 0 ? 0 : 100))])]);
            var footer = new NativeTestMessage("Information", string.Format(text.Footer, errors), null, null, "Unknown", 0, 0, []);
            var result = new NativeTestResult(name, definition.Manifest.Reference, errors == 0 ? "Success" : "Error", errors, 0, [root, footer], true, true, true);
            var plan = TestSuiteRunPlanner.Prepare([definition], [name], [], "style");
            string expected = errors == 0 ? "passed" : "assertionFailed";
            Check(TestSuiteResultNormalizer.Normalize(plan, [result]).Results.Single().Outcome == expected);
            Check(TestSuiteCasingResultProfile.Interpret(definition.Manifest with { ProfileId = "unqualified-generic" }, result) == null);
            Check(TestSuiteCasingResultProfile.Interpret(definition.Manifest, result with { Messages = [root, footer with { Description = "unexpected diagnostic" }] }) == null);
            Check(TestSuiteCasingResultProfile.Interpret(definition.Manifest, result with { Messages = [root, footer with { WarningCount = 1 }] }) == null);
            Check(TestSuiteCasingResultProfile.Interpret(definition.Manifest, result with { Messages = [root, footer, footer] }) == null);
            Check(TestSuiteCasingResultProfile.Interpret(definition.Manifest, result with { State = "Unknown" }) == null);
            Check(TestSuiteResultNormalizer.Normalize(plan, [result with { ScopeVerified = false }]).Results.Single().Outcome == "inconclusive");
            Check(TestSuiteResultNormalizer.Normalize(plan, [result with { CollectionComplete = false }]).Results.Single().Outcome == "inconclusive");
            Check(TestSuiteCasingResultProfile.Interpret(definition.Manifest, result with { Messages = [root, footer with { Description = string.Format(languages[text.Group == "Styleguide" ? 1 : 0].Footer, errors) }] }) == null);
            leaves[0] = leaves[0] with { Path = "ScratchPLC - unobserved" };
            Check(TestSuiteCasingResultProfile.Interpret(definition.Manifest, result) == null);
        }
        return checks;
    }
}
