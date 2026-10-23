using System.Text;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal static class IdentityTextChecks
{
    internal static string[] InvalidText => ["bad\ud800", "bad\udc00", "bad\ud800x", "bad\udc00\ud800", "bad\u0000", "bad\n", "bad\u007f", "bad\ufffe", "bad\uffff", "", " ", null!];
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Reject(Action action, string code)
        { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected identity-text refusal: " + code); }
        foreach (string raw in new[] { "{\"x\":\"\\ud800\"}", "{\"x\":\"\\udc00\"}", "{\"\\ud800\":\"x\"}", "{\"x\":[\"\\ud800\"]}", "{\"\\udc00\":\"x\"}" })
            Reject(() => TestSuiteStyleJobs.Parse<Dictionary<string,string>>(Encoding.UTF8.GetBytes(raw)), "InvalidTestSuiteArtifact");
        Check(TestSuiteStyleJobs.Parse<Dictionary<string,string>>(Encoding.UTF8.GetBytes("{\"raw\":\"\\u0000\\n\\ud83d\\ude00\"}"))["raw"] == "\0\n😀", "JSON diagnostic text permits controls and valid surrogate pairs");
        var reference = new EngineeringObjectReference(new("Project/Text", "PLC/A", "Root", "Rules/A", "ruleSet"), new("text-test", new('a', 64)));
        var scope = reference with { Object = reference.Object with { ObjectPath = "Blocks/A", Kind = "block" } };
        TestSuiteDefinitionCapture Capture(string id, string[] dependencies, string[] syntax) => new(id, "style", id, ".xml", reference,
            Encoding.UTF8.GetBytes("<Rule/>"), "synthetic-text-v1", [scope], dependencies, true, true, syntax);
        foreach (string bad in InvalidText)
        {
            Reject(() => Capture("root", [bad], []), "InvalidDefinition");
            Reject(() => Capture("root", [], [bad]), "InvalidDefinition");
        }
        // A replacement character is valid input; an unpaired surrogate must not silently bind to it.
        var replacement = Capture("dependency\ufffd", [], []);
        Reject(() => TestSuiteRunPlanner.Prepare([Capture("root", ["dependency\ud800"], []), replacement], ["root"], [], "style"), "InvalidDefinition");
        foreach (string text in new[] { "日本語 Ω 😀", "e\u0301", "é", "literal\\ud800", "replacement\ufffd", new string('x',256) })
        {
            var capture = Capture("root", [text], [text]);
            Check(capture.Manifest.Dependencies.Single() == text && capture.Manifest.UnresolvedSyntax.Single() == text, "Valid Unicode is retained exactly without normalization");
            var dep = Capture(text, [], []);
            var plan = TestSuiteRunPlanner.Prepare([Capture("root", [text], []), dep], ["root"], [], "style");
            Check(plan.RequiredDefinitionIds.SequenceEqual(["root",text]), "Exact valid dependency survives capture and closure");
        }
        var distinct = TestSuiteRunPlanner.Prepare([Capture("root", ["é", "e\u0301"], []), Capture("é", [], []), Capture("e\u0301", [], [])], ["root"], [], "style");
        Check(distinct.RequiredDefinitionIds.Length == 3, "Canonically equivalent Unicode is not folded into one definition");
        Check(Capture("root", [], [new string('x',2000)]).Manifest.UnresolvedSyntax.Single().Length == 2000, "Syntax boundary is preserved");
        Reject(() => Capture("root", [new string('x',257)], []), "InvalidDefinition");
        Reject(() => Capture("root", [], [new string('x',2001)]), "InvalidDefinition");
        Reject(() => TestSuiteRunPlanner.Prepare([null!], ["root"], [], "style"), "RunPlanLimit");
        Console.WriteLine($"PASS Test Suite exact identity text: {checks} checks"); return checks;
    }
}
