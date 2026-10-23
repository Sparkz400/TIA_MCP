using System.Text;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal static class CasingResultProfileChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new Exception("Casing result profile failed"); checks++; }
        foreach (int errors in new[] { 0, 3 })
        {
            string name = errors == 0 ? "TiaMcp_Q01_Clean" : "TiaMcp_Q01_Violating";
            var reference = new EngineeringObjectReference(new("owned", "plc", "plc", "rule/" + name, "ruleSet"), new("snapshot", new('a', 64)));
            var definition = new TestSuiteDefinitionCapture(name, "style", name, ".xml", reference, Encoding.UTF8.GetBytes("<Rule/>"),
                TestSuiteCasingResultProfile.Profile, [reference with { Object = reference.Object with { Kind = "block", ObjectPath = "Main" } }], [], true, true, []);
            NativeTestMessage Message(string path, int count, NativeTestMessage[] children, string description = "") =>
                new(count == 0 ? "Success" : "Error", description, path, null, "Unknown", count, 0, children);
            var leaves = new[] { "Main", "TiaMcp_EnableGate", "TiaMcp_IsEnabled" }.Select(n => Message("ScratchPLC - " + n, errors == 0 ? 0 : 1, [],
                errors == 0 ? "" : $"Konflikte mit {n} : Der Objektname Codebaustein (FB,FC,OB) muss in Camel Case geschrieben werden")).ToArray();
            var root = Message("Styleguide", errors, [Message(name, errors, [Message("TiaMcp_CamelCase", errors,
                [Message("Elemente mit Regelverletzung", errors, errors == 0 ? [] : leaves), Message("Elemente ohne Regelverletzung", 0, errors == 0 ? leaves : [])],
                $"Regelverletzung/Validierung Codebaustein (FB,FC,OB) : {errors}/3, Prozentsatz Regelverletzung - {(errors == 0 ? 0 : 100)} %")])]);
            var footer = new NativeTestMessage("Information", $"Ausführung von Regelsatz/-sätzen beendet (Fehler : {errors} ; Warnungen : 0)", null, null, "Unknown", 0, 0, []);
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
            leaves[0] = leaves[0] with { Path = "ScratchPLC - unobserved" };
            Check(TestSuiteCasingResultProfile.Interpret(definition.Manifest, result) == null);
        }
        return checks;
    }
}
