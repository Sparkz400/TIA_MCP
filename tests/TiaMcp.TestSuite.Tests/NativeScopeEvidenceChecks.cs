using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal static class NativeScopeEvidenceChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action action, string code)
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; }
            throw new Exception("Expected " + code);
        }
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "fixtures", "testsuite", "native-owned-v21")))
            root = Directory.GetParent(root)?.FullName ?? throw new Exception("Retained genuine Test Suite fixtures are required.");
        string fixture = Path.Combine(root, "fixtures", "testsuite", "native-owned-v21");
        byte[] Read(string path) => File.ReadAllBytes(Path.Combine(fixture, path));
        string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        byte[] source = Read("inputs/vendor-help-example.tst"), exported = Read("definitions/system.tst");
        Check(Hash(source) == "7c139e29b965e7769e32b8cf14be55b2f0ef6f4a83febd1ca78cb10e0c77bcee" &&
            Hash(exported) == "fba99160b6e881c3561c2175f46395aad51613873eb6e0cfa2ff93a6a0210276", "Genuine source and native-export bytes changed.");
        var expected = TestSuiteSystemScopePreservation.ReadScope(source);
        var actual = TestSuiteSystemScopePreservation.ReadScope(exported);
        Check(expected.FolderPath == @"D:\Simatic" && actual.FolderPath == "", "Genuine native folder loss remains visible.");
        var loss = TestSuiteSystemScopePreservation.Compare(source, exported);
        Check(!loss.ScopePreservationPassed && !loss.ExportScopePreserved && loss.Differences is [{ Property: "FOLDER_PATH", Stage: "export" }], "Successful import cannot hide the genuine folder loss.");
        Check(!loss.NativeExecutionAuthorized && !loss.FullDefinitionPreservationVerified && !loss.FreshReopenVerified, "Pure scope comparison cannot claim native execution or complete preservation.");
        var unchanged = TestSuiteSystemScopePreservation.Compare(exported, exported);
        Check(unchanged.ExportScopePreserved && !unchanged.NativeReadbackVerified && !unchanged.ScopePreservationPassed, "Equal exports do not establish typed readback or fresh reopen.");
        string project = Path.Combine(Path.GetTempPath(), "system-scope.synthetic.ap21");
        TestSuiteSystemScopeReadback Readback(byte[] bytes, string epoch = "open-1", char evidence = 'a') =>
            new(Hash(bytes), new(evidence, 64), project, epoch, TestSuiteSystemScopePreservation.ReadScope(bytes));
        foreach (string invalid in IdentityTextChecks.InvalidText)
            Reject(() => TestSuiteSystemScopePreservation.Compare(exported, exported, Readback(exported, invalid)), "UnsupportedSystemScopeProfile");
        Check(TestSuiteSystemScopePreservation.Compare(exported, exported, Readback(exported, "epoch-😀-e\u0301-\ufffd")).NativeReadbackVerified, "Valid Unicode epoch survives pure supplied-readback comparison");
        var observation = Readback(exported);
        var readBack = TestSuiteSystemScopePreservation.Compare(exported, exported, observation);
        Check(readBack.NativeReadbackVerified && !readBack.FreshReopenVerified, "One native readback cannot claim a fresh reopen.");
        var full = TestSuiteSystemScopePreservation.Compare(exported, exported, observation, exported, Readback(exported, "open-2", 'b'));
        Check(full.ScopePreservationPassed && full.EvidenceSha256.Length == 2 && !full.NativeExecutionAuthorized, "Independently bound reopen evidence only passes scope preservation.");
        var nativeLoss = TestSuiteSystemScopePreservation.Compare(source, exported, observation, exported, Readback(exported, "open-2", 'b'));
        Check(!nativeLoss.ScopePreservationPassed && !nativeLoss.NativeReadbackVerified && !nativeLoss.FreshReopenVerified, "Reopening a lossy export never repairs the reviewed source obligation.");
        Reject(() => TestSuiteSystemScopePreservation.Compare(source, exported, Readback(source)), "StaleSystemScopeReadback");
        Reject(() => TestSuiteSystemScopePreservation.Compare(exported, exported, observation, exported, observation), "StaleSystemScopeReadback");
        Reject(() => TestSuiteSystemScopePreservation.Compare(exported, exported, observation, exported, Readback(exported, "open-2")), "StaleSystemScopeReadback");
        Reject(() => TestSuiteSystemScopePreservation.Compare(exported, exported, observation, exported, Readback(exported, "open-2", 'b') with { ProjectFilePath = project + ".other" }), "StaleSystemScopeReadback");
        Reject(() => TestSuiteSystemScopePreservation.Compare(exported, exported, observation, null, observation), "UnsupportedSystemScopeProfile");
        Reject(() => TestSuiteSystemScopePreservation.Compare(exported, exported, observation with { ProjectOpenEpoch = "" }), "UnsupportedSystemScopeProfile");
        var mismatch = TestSuiteSystemScopePreservation.Compare(exported, exported, observation with { Scope = actual with { ServerAddress = "opc.tcp://different:4840" } });
        Check(!mismatch.NativeReadbackVerified && mismatch.Differences.All(d => d.Property == "SERVER_ADDRESS"), "Typed scope disagreement with exported bytes fails.");
        byte[] Edit(string from, string to) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(exported).Replace(from, to, StringComparison.Ordinal));
        Reject(() => TestSuiteSystemScopePreservation.ReadScope(Edit("FOLDER_PATH : \"\"", "FOLDER_PATH : \"\"\r\nFOLDER_PATH : \"elsewhere\"")), "UnsupportedSystemScopeProfile");
        Reject(() => TestSuiteSystemScopePreservation.ReadScope(Edit("FOLDER_PATH : \"\"", "FOLDER_PATH : \"unterminated")), "UnsupportedSystemScopeProfile");
        Reject(() => TestSuiteSystemScopePreservation.ReadScope(Edit("FOLDER_PATH : \"\"", "UNREVIEWED_SCOPE : \"\"")), "UnsupportedSystemScopeProfile");
        Reject(() => TestSuiteSystemScopePreservation.ReadScope(Edit("Standard_SIMATIC", "UserDefined")), "UnsupportedSystemScopeProfile");
        Reject(() => TestSuiteSystemScopePreservation.ReadScope([0xff]), "UnsupportedSystemScopeProfile");
        Reject(() => TestSuiteSystemScopePreservation.ReadScope(exported.Concat(exported).ToArray()), "UnsupportedSystemScopeProfile");
        var changedPath = TestSuiteSystemScopePreservation.Compare(source, Edit("FOLDER_PATH : \"\"", "FOLDER_PATH : \"d:/Simatic\""));
        Check(!changedPath.ExportScopePreserved, "Folder values are exact retained strings; no lossy path rewriting is hidden.");

        string group = "evidence/ts02/run-style-group-availability-01/";
        byte[] rawResults = Read(group + "native-results.json");
        var result = JsonNode.Parse(rawResults)!;
        var before = JsonNode.Parse(Read(group + "before.json"))!;
        var definitionReference = new EngineeringObjectReference(new("fixture-project", "ScratchPLC", "Program blocks", "TiaMcp_CasingAvailability", "ruleSet"),
            new("retained-ts02-analysis", Hash(Read(group + "before.json"))));
        var inventory = before["inventory"]!["rootBlocks"]!.AsArray().Select(n => definitionReference with {
            Object = definitionReference.Object with { ObjectPath = n!.GetValue<string>(), Kind = "block" } }).ToArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode node) { if (node["path"] is JsonValue path) paths.Add(path.GetValue<string>()); foreach (var child in node["messages"]!.AsArray()) Walk(child!); }
        Walk(result);
        var mentioned = inventory.Where(o => paths.Contains("ScratchPLC - " + o.Object.ObjectPath)).ToArray();
        var definition = new TestSuiteDefinitionCapture("casing", "style", "TiaMcp_CasingAvailability", ".xml", definitionReference,
            Read("definitions/style.xml"), "retained-owned-group-diagnostic-analysis-v1", inventory, [], true, true, []);
        var plan = TestSuiteRunPlanner.Prepare([definition], ["casing"], [], "style");
        var diagnostic = new TestSuiteDiagnosticScopeEvidence("casing", definition.Manifest.Reference, mentioned, Hash(rawResults));
        var report = TestSuiteScopeCoverage.CompareDiagnosticEvidence(plan, [diagnostic]);
        Check(mentioned.Length == 3 && inventory.Length == 4, "Retained diagnostics mention three code blocks, while requested inventory also contains the DB.");
        Check(!report.Coverage.RequiredScopePassed && report.Coverage.Entries[0].Status == "incomplete" &&
            report.Coverage.Entries[0].MissingScope is [{ Object.ObjectPath: "TiaMcp_EnableGate_DB" }], "Diagnostic mentions retain the unverified DB without inventing explicit exclusion.");
        Check(report.Coverage.Entries[0].IgnoredScope.Length == 0 && !report.CompleteEffectiveScopeVerified && !report.NativeExecutionAuthorized, "Silence cannot prove ignored scope or complete coverage.");
        var all = TestSuiteScopeCoverage.CompareDiagnosticEvidence(plan, [diagnostic with { MentionedObjects = inventory }]);
        Check(!all.Coverage.RequiredScopePassed && all.Coverage.Entries[0].MissingScope.Length == 0, "Even all required objects in diagnostics cannot prove no additional silent scope was evaluated.");
        TestSuiteScopeCoverage.Validate(plan, report.Coverage); checks++;
        Reject(() => TestSuiteScopeCoverage.CompareDiagnosticEvidence(plan, [diagnostic with { RawResultSha256 = "not-a-hash" }]), "InvalidIdentity");
        Reject(() => TestSuiteScopeCoverage.CompareDiagnosticEvidence(plan, [diagnostic, diagnostic]), "InvalidScopeCoverage");
        Reject(() => TestSuiteScopeCoverage.CompareDiagnosticEvidence(plan, [diagnostic with { MentionedObjects = [mentioned[0] with { Snapshot = new("another", new('f', 64)) }] }]), "StaleObservation");
        return checks;
    }
}
